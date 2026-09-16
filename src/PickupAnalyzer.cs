/* -*- coding: utf-8 -*-
 * PickupAnalyzer.cs — 拾音器交流声精确分析（Goertzel 算法）
 *
 * 为什么不用 ffmpeg 的高通/低通：
 *   那些是二阶 IIR 滤波器，滚降平缓 —— 所谓"45-55Hz 带"其实漏进了大量 100~200Hz 的人声基频。
 *   实测教训：说话时测出的"市电占比"比真实电流声还高，只能靠"仅安静段判定"这个门限绕开，
 *   结果说话期间发生的交流声就会漏检。
 *
 * Goertzel 算法只算某一个频点的能量，不需要 FFT 库，逐样本递推即可，精度足够：
 *   s[n] = x[n] + 2cos(2πk/N)·s[n-1] − s[n-2]
 *   power = s[-1]² + s[-2]² − 2cos(2πk/N)·s[-1]·s[-2]
 *
 * 判据：把 50Hz 及其谐波的功率与全带功率比较。
 *   实测 —— 真实电流麦（B 站样本 6~8s）：50Hz 占比约 −3 ~ −15dB；
 *          正常人声：−40dB 以下。
 *
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;

namespace VideoChecker
{
    /// <summary>交流声分析结果。</summary>
    public class MainsResult
    {
        public bool Ok;                 // 分析是否成功
        public string Note = "";        // 失败原因
        public double TotalDb = -160;   // 全带 RMS（dB）
        public double Mains50RatioDb = -160;   // 50Hz 功率占全带比例（dB）
        public double Mains100RatioDb = -160;  // 100Hz
        public double Mains150RatioDb = -160;  // 150Hz
        /// <summary>交流声强度评分 0~1（越大越像交流声）。</summary>
        public double HumScore;
        /// <summary>参与统计的窗口数。</summary>
        public int Windows;
    }

    public static class PickupAnalyzer
    {
        /// <summary>分析的采样率。16kHz 足够覆盖市电谐波与人声，又不至于太慢。</summary>
        public const int SampleRate = 16000;

        /// <summary>每段分析窗口的秒数。</summary>
        private const double WindowSec = 1.0;

        /// <summary>低于此 RMS 的窗口视为无信号，不参与统计（避免把数字静音算成"50Hz 占比高"）。</summary>
        private const double SilenceDb = -60.0;

        /// <summary>
        /// 分析一路音频的交流声情况。path 可以是文件，也可以是 RTSP 地址。
        /// </summary>
        /// <param name="quietOnlyDb">
        /// 只统计低于此电平的窗口（"仅安静段判定"）。
        /// 传 double.NaN 表示不启用 —— 精确测量下通常不需要它：
        /// 实测正常人声的 50Hz 占比约 −37dB，而真实电流声是 −9 ~ −23dB，本来就分得开。
        /// </param>
        public static MainsResult Analyze(string path, double durationSec, string[] inputOptions)
        {
            return Analyze(path, durationSec, inputOptions, double.NaN);
        }

        public static MainsResult Analyze(string path, double durationSec, string[] inputOptions,
            double quietOnlyDb)
        {
            MainsResult r = new MainsResult();
            byte[] pcm;
            try
            {
                pcm = Ffmpeg.DecodeAudioPcm(path, durationSec, SampleRate, inputOptions, 600);
            }
            catch (Exception ex)
            {
                r.Note = "解码音频失败：" + ex.Message;
                return r;
            }
            if (pcm == null || pcm.Length < SampleRate / 4 * 2)   // 少于 0.25 秒没意义
            {
                r.Note = "音频数据不足，无法分析";
                return r;
            }

            // s16le → double[]
            int n = pcm.Length / 2;
            double[] x = new double[n];
            for (int i = 0; i < n; i++)
            {
                short v = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                x[i] = v / 32768.0;
            }

            // 窗口 1 秒；音频比 1 秒短就用整段（否则短样本会直接分析不了）
            int win = (int)(SampleRate * WindowSec);
            if (win > n) win = n;
            if (win < SampleRate / 4) win = SampleRate / 4;
            double sum50 = 0, sum100 = 0, sum150 = 0, sumTotal = 0;
            int cnt = 0;

            for (int start = 0; start + win <= n; start += win)
            {
                double total = 0;
                for (int i = 0; i < win; i++) total += x[start + i] * x[start + i];
                if (total <= 0) continue;

                double rms = Math.Sqrt(total / win);
                double rmsDb = rms > 1e-9 ? 20 * Math.Log10(rms) : -160;
                if (rmsDb < SilenceDb) continue;      // 无信号段跳过
                // 「仅安静段判定」：启用时只统计没人说话的窗口
                if (!double.IsNaN(quietOnlyDb) && rmsDb >= quietOnlyDb) continue;

                double p50 = Goertzel(x, start, win, 50.0);
                double p100 = Goertzel(x, start, win, 100.0);
                double p150 = Goertzel(x, start, win, 150.0);

                sum50 += p50 / total;
                sum100 += p100 / total;
                sum150 += p150 / total;
                sumTotal += total / win;
                cnt++;
            }

            if (cnt == 0)
            {
                r.Note = double.IsNaN(quietOnlyDb)
                    ? "没有可分析的有效音频段（全程接近无声）"
                    : "没有符合「安静段」条件的音频（全程都在说话），可关闭安静门限后重试";
                return r;
            }

            r.Ok = true;
            r.Windows = cnt;
            r.Mains50RatioDb = ToDb(sum50 / cnt);
            r.Mains100RatioDb = ToDb(sum100 / cnt);
            r.Mains150RatioDb = ToDb(sum150 / cnt);
            r.TotalDb = 10 * Math.Log10(sumTotal / cnt);

            // 综合评分：50Hz 与它的谐波同时存在才更像"市电交流声"（单一频率可能是巧合）
            double s50 = Score(r.Mains50RatioDb, -12, -40);    // −12dB 满分，−40dB 零分
            double s100 = Score(r.Mains100RatioDb, -20, -48);
            double s150 = Score(r.Mains150RatioDb, -24, -52);
            r.HumScore = Math.Max(s50, (s100 + s150) / 2.0 * 0.9);
            return r;
        }

        /// <summary>线性打分：value ≥ hi 得 1，≤ lo 得 0，中间线性。</summary>
        private static double Score(double value, double hi, double lo)
        {
            if (double.IsNegativeInfinity(value)) return 0;
            if (value >= hi) return 1.0;
            if (value <= lo) return 0.0;
            return (value - lo) / (hi - lo);
        }

        private static double ToDb(double ratio)
        {
            return ratio > 1e-16 ? 10 * Math.Log10(ratio) : -160;
        }

        /// <summary>
        /// 单频点能量（Goertzel）。返回该频点的功率（未归一化，与窗口总功率同量纲，可直接比）。
        /// </summary>
        private static double Goertzel(double[] x, int offset, int len, double freq)
        {
            double w = 2.0 * Math.PI * freq / SampleRate;
            double coeff = 2.0 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < len; i++)
            {
                double s = x[offset + i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s;
            }
            // 归一化到与原信号功率可比：除以 len/2
            return (s1 * s1 + s2 * s2 - coeff * s1 * s2) / (len / 2.0);
        }
    }
}
