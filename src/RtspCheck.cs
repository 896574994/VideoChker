/* -*- coding: utf-8 -*-
 * RtspCheck.cs — RTSP / 网络流实时检测（与 Python 版 checker/rtsp.py 判定等价）
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    public static class RtspCheck
    {
        // 拉流传输选项（TCP 比 UDP 稳定）
        private static string[] _rtspOpts = new string[] { "-rtsp_transport", "tcp" };

        // 断流/不稳定特征日志
        private static Regex _unstableRe = new Regex(
            "(?i)connection reset|connection refused|immediate exit|end of file|" +
            "timed out|stall|realtime buffer|buffer underflow|broken pipe|" +
            "could not find codec|invalid data|failed to|unable to",
            RegexOptions.IgnoreCase);

        private const int UnstableFailCount = 20;

        public static string[] RtspOpts { get { return _rtspOpts; } }

        /// <summary>检测一路 RTSP/网络流，结果写入 rep。</summary>
        public static void CheckRtsp(string url, double duration, CheckOptions opts, MediaReport rep)
        {
            // ---- 1. 连接探测 ----
            Dictionary<string, object> probe = null;
            try
            {
                probe = Ffmpeg.ProbeMedia(url, 40, _rtspOpts);
            }
            catch (FfmpegError e)
            {
                rep.Error = "无法连接或解析 RTSP 流: " + e.Message;
                rep.Add("RTSP 流连接", "FAIL", "无法连接（请检查地址、网络与端口）", null);
                return;
            }

            rep.MediaInfo = InfoDict(probe);
            rep.Add("RTSP 流连接", "PASS", "连接成功，已获取流信息", null);

            bool hasVideo = rep.MediaInfo.ContainsKey("video");
            bool hasAudio = rep.MediaInfo.ContainsKey("audio");
            if (opts.CheckVideo && !hasVideo)
                rep.Add("视频流存在", "FAIL", "该网络流不包含视频流", null);
            if (opts.CheckAudio && !hasAudio)
                rep.Add("音频流存在", "FAIL", "该网络流不包含音频流", null);

            // ---- 2. 拉流分析 ----
            string vStderr = "", aStderr = "", aStdout = "";
            List<string> vErr = new List<string>(), aErr = new List<string>();
            bool needV = opts.CheckVideo && hasVideo;
            bool needA = opts.CheckAudio && hasAudio;
            try
            {
                if (needV)
                {
                    AnalyzeResult vr = Ffmpeg.Analyze(url, Ffmpeg.VF_CHAIN, null, duration,
                        (int)duration + 180, _rtspOpts);
                    vStderr = vr.Stderr;
                    vErr = vr.ErrorLines;
                }
                if (needA)
                {
                    AnalyzeResult ar = Ffmpeg.Analyze(url, null, Ffmpeg.AF_CHAIN, duration,
                        (int)duration + 180, _rtspOpts);
                    aStderr = ar.Stderr;
                    aStdout = ar.Stdout;
                    aErr = ar.ErrorLines;
                }
            }
            catch (FfmpegError e)
            {
                rep.Error = e.Message;
                return;
            }

            List<Seg> silenceSegs = Ffmpeg.ParseSilence(aStderr);
            List<Seg3> blackSegs = Ffmpeg.ParseBlack(vStderr);

            // ---- 3. 复用文件检测判定 ----
            List<CheckItem> items = new List<CheckItem>();
            if (needV)
            {
                int before = rep.Items.Count;
                Checks.CheckVideoIntegrity(url, probe, vStderr, vErr, opts.VideoTh, rep);
                for (int i = before; i < rep.Items.Count; i++) items.Add(rep.Items[i]);
                rep.Items.RemoveRange(before, rep.Items.Count - before);
            }
            if (needA)
            {
                int before = rep.Items.Count;
                Checks.CheckAudioIntegrity(url, probe, aStderr, aStdout, aErr, opts.AudioTh, rep);
                for (int i = before; i < rep.Items.Count; i++) items.Add(rep.Items[i]);
                rep.Items.RemoveRange(before, rep.Items.Count - before);
            }
            if (opts.CheckSync && hasVideo && hasAudio)
            {
                int before = rep.Items.Count;
                Checks.CheckAvSync(url, probe, opts.SyncTh, silenceSegs, blackSegs, rep);
                for (int i = before; i < rep.Items.Count; i++) items.Add(rep.Items[i]);
                rep.Items.RemoveRange(before, rep.Items.Count - before);
            }
            else if (opts.CheckSync)
            {
                items.Add(new CheckItem("音画同步", "SKIP", "实时流缺少视频或音频流，无法进行音画同步检测", null));
            }

            // ---- 4. 实时流稳定性（网络流专属）----
            List<string> unstable = new List<string>();
            List<string> allErr = new List<string>(vErr);
            allErr.AddRange(aErr);
            foreach (string ln in allErr)
            {
                if (_unstableRe.IsMatch(ln) && unstable.Count < 30)
                    unstable.Add(ln.Trim().Length > 140 ? ln.Trim().Substring(0, 140) : ln.Trim());
            }
            if (unstable.Count == 0)
                items.Add(new CheckItem("实时流稳定性", "PASS",
                    string.Format("拉流分析 {0:0}s 期间流持续稳定，未发现断流/严重丢帧", duration), null));
            else if (unstable.Count < UnstableFailCount)
                items.Add(new CheckItem("实时流稳定性", "WARN",
                    string.Format("拉流期间出现 {0} 次不稳定信号（断流/丢帧/缓冲）：{1}",
                        unstable.Count, string.Join(" | ", unstable.ToArray(), 0, Math.Min(3, unstable.Count))),
                    Dict("count", unstable.Count)));
            else
                items.Add(new CheckItem("实时流稳定性", "FAIL",
                    string.Format("拉流期间出现 {0} 次断流/解码异常，流不稳定", unstable.Count),
                    Dict("count", unstable.Count)));

            // 连接项在前，分析项在后
            List<CheckItem> merged = new List<CheckItem>();
            merged.AddRange(rep.Items);
            merged.AddRange(items);
            rep.Items = merged;
        }

        private static Dictionary<string, object> InfoDict(Dictionary<string, object> probe)
        {
            return Engine.InfoToDict(Checks.BuildMediaInfo(probe));
        }

        private static Dictionary<string, object> Dict(string key, object val)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d[key] = val;
            return d;
        }
    }
}
