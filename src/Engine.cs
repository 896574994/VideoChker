/* -*- coding: utf-8 -*-
 * Engine.cs — 检测调度引擎（单文件/目录/RTSP）
 * 与 Python 版 video_checker.py 主流程等价。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.IO;

namespace VideoChecker
{
    public static class Engine
    {
        /// <summary>检测 RTSP/网络流（实时拉流）。</summary>
        public static MediaReport CheckRtsp(string url, double duration, CheckOptions opts)
        {
            return CheckRtsp(url, duration, opts, null);
        }

        /// <summary>检测 RTSP/网络流。isCancelled 供「停止」按钮中断（每个步骤边界检查）。</summary>
        public static MediaReport CheckRtsp(string url, double duration, CheckOptions opts, Func<bool> isCancelled)
        {
            // 报告里用打码地址：报告会被打开、导出 PDF（PDF 是整页栅格化的图片，
            // 文字无法被替换），明文密码印在图上就收不回来了。
            MediaReport rep = new MediaReport(Secret.MaskUrl(url));
            Ffmpeg.ClearPacketCache();   // 每路流重新取包，避免沿用上一条流的缓存
            if (opts.RtspBizMode) RtspBiz.Check(url, duration, opts, rep, isCancelled);   // 拉流业务检测（专用判定）
            else RtspCheck.CheckRtsp(url, duration, opts, rep);                            // 旧路径：把流当文件
            return rep;
        }

        private static string[] _videoExt = new string[] {
            ".mp4", ".mov", ".mkv", ".avi", ".flv", ".ts", ".m2ts", ".mts",
            ".webm", ".wmv", ".mpg", ".mpeg", ".m4v", ".m4a", ".3gp", ".ogv",
            ".vob", ".aac", ".mp3", ".wav", ".flac"
        };

        public static bool IsVideoFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            for (int i = 0; i < _videoExt.Length; i++)
                if (ext == _videoExt[i]) return true;
            return false;
        }

        /// <summary>收集待检测文件：文件直接返回，目录递归扫描。</summary>
        public static List<string> CollectFiles(string path)
        {
            List<string> files = new List<string>();
            if (File.Exists(path)) { files.Add(path); return files; }
            if (Directory.Exists(path))
            {
                Stack<string> stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0)
                {
                    string dir = stack.Pop();
                    try
                    {
                        foreach (string d in Directory.GetDirectories(dir)) stack.Push(d);
                        string[] names = Directory.GetFiles(dir);
                        Array.Sort(names, StringComparer.Ordinal);
                        foreach (string f in names)
                            if (IsVideoFile(f)) files.Add(f);
                    }
                    catch { }
                }
            }
            return files;
        }

        /// <summary>检测单个文件，返回报告。</summary>
        public static MediaReport CheckOneFile(string path, CheckOptions opts,
            Action<int, int> mageProgress = null, Func<bool> isCancelled = null, Action<string> onStage = null)
        {
            MediaReport rep = new MediaReport(path);
            Ffmpeg.ClearPacketCache();   // 每个文件重新取包，避免缓存无上限增长、也避免跨文件误用
            Dictionary<string, object> probe = null;
            try
            {
                probe = Ffmpeg.ProbeMedia(path, 120);
            }
            catch (FfmpegError e)
            {
                rep.Error = "无法探测媒体: " + e.Message;
                return rep;
            }

            MediaInfo info = Checks.BuildMediaInfo(probe);
            rep.MediaInfo = InfoToDict(info);

            try
            {
                string vStderr = "", aStderr = "", aStdout = "";
                List<string> vErr = new List<string>(), aErr = new List<string>();
                bool isAudioFile = Checks.IsAudioOnlyFile(path);
                bool needV = (opts.CheckVideo || opts.CheckSync) && !isAudioFile;
                bool needA = opts.CheckAudio || opts.CheckSync;
                double sampling = opts.EffectiveSampling();

                if (needV)
                {
                    AnalyzeResult vr = Ffmpeg.Analyze(path, Ffmpeg.VF_CHAIN, null,
                        sampling > 0 ? (double?)sampling : null, 1800);
                    vStderr = vr.Stderr;
                    vErr = vr.ErrorLines;
                }
                if (needA)
                {
                    AnalyzeResult ar = Ffmpeg.Analyze(path, null, Ffmpeg.AF_CHAIN,
                        sampling > 0 ? (double?)sampling : null, 1800);
                    aStderr = ar.Stderr;
                    aStdout = ar.Stdout;
                    aErr = ar.ErrorLines;
                }

                List<Seg> silenceSegs = Ffmpeg.ParseSilence(aStderr);
                List<Seg3> blackSegs = Ffmpeg.ParseBlack(vStderr);

                if (opts.CheckAudio)
                    Checks.CheckAudioIntegrity(path, probe, aStderr, aStdout, aErr, opts.AudioTh, rep);
                if (opts.CheckQuality)
                    Checks.CheckAudioQuality(path, probe, sampling, opts.QualityTh, rep);
                if (opts.CheckVideo)
                {
                    if (isAudioFile)
                        rep.Add("视频完整性", "SKIP", "纯音频文件，无视频流可检测", null);
                    else
                        Checks.CheckVideoIntegrity(path, probe, vStderr, vErr, opts.VideoTh, rep);
                }
                if (opts.CheckSync)
                {
                    if (isAudioFile)
                        rep.Add("音画同步", "SKIP", "纯音频文件，无法进行音画同步检测", null);
                    else
                        Checks.CheckAvSync(path, probe, opts.SyncTh, silenceSegs, blackSegs, rep);
                }
                if (opts.CheckMage)
                {
                    if (isAudioFile)
                        rep.Add("AI 画面理解", "SKIP", "纯音频文件，无画面可分析", null);
                    else
                        MageCheck.CheckMage(path, probe, opts.Mage, sampling, rep, mageProgress, isCancelled, onStage);
                }
            }
            catch (FfmpegError e)
            {
                rep.Error = e.Message;
            }
            return rep;
        }

        internal static Dictionary<string, object> InfoToDict(MediaInfo info)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["container"] = info.Container;
            d["duration"] = info.Duration.HasValue ? (object)info.Duration.Value : null;
            d["size"] = info.Size.HasValue ? (object)info.Size.Value : null;
            d["bit_rate"] = info.BitRate.HasValue ? (object)info.BitRate.Value : null;
            if (info.Video != null) d["video"] = info.Video;
            if (info.Audio != null) d["audio"] = info.Audio;
            return d;
        }

        /// <summary>格式化时长 mm:ss。</summary>
        public static string FmtSec(double? sec)
        {
            if (!sec.HasValue) return "?";
            int total = (int)sec.Value;
            int m = total / 60, s = total % 60;
            return m.ToString("00") + ":" + s.ToString("00");
        }

        /// <summary>格式化文件大小。</summary>
        public static string FmtSize(double? size)
        {
            if (!size.HasValue || size.Value <= 0) return "?";
            double n = size.Value;
            string[] units = new string[] { "B", "KB", "MB", "GB" };
            int u = 0;
            while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
            if (u == 0) return ((long)n).ToString() + "B";
            return n.ToString("0.0") + units[u];
        }
    }
}
