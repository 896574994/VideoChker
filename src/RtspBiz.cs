/* -*- coding: utf-8 -*-
 * RtspBiz.cs — RTSP 拉流业务检测
 *
 * 与原来的 RtspCheck 的区别：原来的做法是"把流当文件"，直接调 CheckVideoIntegrity /
 * CheckAudioIntegrity / CheckAvSync。但实时流没有 duration，导致时长一致性、时长偏差、
 * 音画同步等项全部空转（只能 SKIP），而静音分布还会误判（已单独修复）。
 *
 * 本模块改用「拉流时长」作为参照系，只做对实时流有意义的判定：
 *   连接 / 流参数 / 实际帧率与丢帧 / 码率稳定性 / 关键帧间隔 / 画面动态性 /
 *   视频解码 / 音频电平与静音 / 拉流稳定性 / 时间水印（可选，需 AI）
 *
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    /// <summary>拉流业务检测阈值。</summary>
    public class RtspThresholds
    {
        public double FrameLossWarn = 0.05;    // 丢帧率
        public double FrameLossFail = 0.15;
        public double BitrateCvWarn = 0.40;    // 每秒码率的变异系数（标准差/均值）
        public double BitrateCvFail = 0.70;
        public double MinBitrateKbps = 150;    // 视频码率下限
        public double StaticWarn = 0.50;       // 画面静止占比（疑似卡死/遮挡）
        public double StaticFail = 0.85;
        public double GopWarn = 3.0;           // 关键帧间隔（秒）
        public double GopFail = 6.0;
        public int UnstableFailCount = 20;     // 断流/不稳定日志条数
        public int MinFramesForRate = 20;      // 少于这么多帧就不判断帧率
        // —— 拾音器检测（低频交流嗡鸣 = 拾音器故障）——
        public double PickupNoSignalDb = -60;    // RMS 低于此值视为无有效信号（拾音器未接/已损坏无输出）
        public double PickupHumCentroid = 400;   // 频谱质心低于此值 + 过零率偏低 → 判定为交流嗡鸣
        public double PickupHumZcr = 0.05;       // 过零率上限（正常环境音实测 0.2 左右，嗡鸣 0.006）
        public double PickupWarnCentroid = 800;  // 质心低于此值但未达故障标准 → 提示人工确认
        // —— 市电频段占比判据（用真实样本「B 站麦克风电流音 6~8s」校准）——
        // 故障时 45-55Hz 能量比全频段仅低 -3 ~ -15dB；正常人声低 40dB 以上。
        public double PickupHumRatioDb = -16;    // 高于此值 → 判为交流声故障（备用路径，滤波器法）
        public double PickupWarnRatioDb = -26;   // 落在此区间 → 提示人工确认（备用路径）

        // Goertzel 精确测量的阈值（首选路径）。标定依据（16kHz 采样、1 秒窗口实测）：
        //   纯 50Hz 合成素材  -0.0 dB   ← 必然检出
        //   真实电流声 A      -9.0 dB   ← 必然检出
        //   真实电流声 B     -22.6 dB   ← 与正常人声几乎重合，频率分析分不开
        //   正常人声（说话中）-21.1 dB
        //
        // 重要教训：早先用「45-55Hz 频段能量求和」测出 buzzB 是 -4.4dB，看着像强交流声；
        // 换成 Goertzel 单频点测量才发现真实值是 -22.6dB —— 频段求和把宽带噪声的几十个
        // 频点累加起来，会虚高十几 dB。旧的结论是错的。
        //
        // 因此分两组阈值：
        //   门限关闭（任何时刻都判）→ 用严格阈值，避免人声误报；
        //   门限开启（只判安静段）  → 没人说话时 50Hz 占比高就是真嗡鸣，可以更灵敏。
        //
        // 已知局限（据实说明）：50Hz 占比低于 -20dB 的**弱**交流声，与正常人声在频率上
        // 是重叠的，本项分不出来，不会报警。要抓弱交流声请勾选「仅安静段判定」——
        // 安静时段的阈值放宽到 -25dB。
        public double PickupMainsFailDb = -15.0;
        public double PickupMainsWarnDb = -20.0;
        public double PickupMainsQuietFailDb = -25.0;
        public double PickupMainsQuietWarnDb = -32.0;
        public double PickupBandLo = 45;         // 市电频段下限 Hz
        public double PickupBandHi = 55;         // 市电频段上限 Hz
        // 只有在整体安静时才用市电占比判故障：说话时低频能量本来就高，
        // 会通过滤波器缓降滚漏进来 —— 实测正常人声段占比 -7.8dB（比电流声还高），
        // 不加这个门限会误报。安静段（电流声 -41~-44dB）才具有判别力。
        public double PickupQuietDb = -35;       // 低于此电平才认为"没有人在说话"
        /// <summary>
        /// 是否启用「安静门限」。
        ///   开（默认）：只在没人说话时（RMS 低于 PickupQuietDb）才用市电占比判故障 —— 误报少，
        ///               但若人一直在说话，可能漏检。
        ///   关：任何时刻都判 —— 覆盖更全，但说话时低频能量本就高，容易误报，
        ///       因此关闭时只给 WARN 提示、不直接判 FAIL。
        /// </summary>
        public bool PickupQuietGateEnabled = true;
    }

    public static class RtspBiz
    {
        private static string[] _tcpOpts = new string[] { "-rtsp_transport", "tcp" };

        // 断流/不稳定特征日志（与旧版一致）
        private static Regex _unstableRe = new Regex(
            "(?i)connection reset|connection refused|immediate exit|end of file|" +
            "timed out|stall|realtime buffer|buffer underflow|broken pipe|" +
            "could not find codec|invalid data|failed to|unable to",
            RegexOptions.IgnoreCase);
        private static Regex _frameCountRe = new Regex("frame=\\s*(\\d+)");

        /// <summary>检测一路 RTSP/网络流，结果写入 rep。</summary>
        public static void Check(string url, double duration, CheckOptions opts, MediaReport rep)
        {
            Check(url, duration, opts, rep, null);
        }

        /// <summary>检测一路 RTSP/网络流。isCancelled 在每个步骤边界检查，配合 Ffmpeg.KillRunning 实现快速停止。</summary>
        public static void Check(string url, double duration, CheckOptions opts, MediaReport rep, Func<bool> isCancelled)
        {
            RtspThresholds th = opts.Rtsp;
            if (duration <= 0) duration = 30;

            // ---------- 1. 连接与流信息 ----------
            Dictionary<string, object> probe;
            try { probe = Ffmpeg.ProbeMedia(url, 40, _tcpOpts); }
            catch (FfmpegError e)
            {
                rep.Error = "无法连接或解析 RTSP 流: " + e.Message;
                rep.Add("RTSP 流连接", "FAIL", "无法连接（请检查地址、网络与端口）", null);
                return;
            }
            Tick(isCancelled);
            rep.MediaInfo = InfoDict(probe);
            rep.Add("RTSP 流连接", "PASS",
                "连接成功，已获取流信息（本次拉流 " + duration.ToString("0") + " 秒）", null);

            bool hasVideo = rep.MediaInfo.ContainsKey("video");
            bool hasAudio = rep.MediaInfo.ContainsKey("audio");
            if (!hasVideo) { rep.Add("视频流存在", "FAIL", "该网络流不包含视频流", null); return; }
            if (!hasAudio) rep.Add("音频流存在", "WARN", "该网络流不包含音频流（若摄像头本就无拾音可忽略）", null);

            Dictionary<string, object> vinfo = rep.MediaInfo["video"] as Dictionary<string, object>;
            double nomFps = 0;
            if (vinfo != null) nomFps = Ffmpeg.FF(Ffmpeg.SafeF(vinfo.ContainsKey("fps") ? vinfo["fps"] : null), 0);

            // ---------- 2. 视频拉流：帧计数 + 冻结 + 解码错误 ----------
            string vStderr = "";
            List<string> vErr = new List<string>();
            try
            {
                AnalyzeResult vr = Ffmpeg.Analyze(url, Ffmpeg.VF_CHAIN, null, duration, (int)duration + 180, _tcpOpts);
                vStderr = vr.Stderr;
                vErr = vr.ErrorLines;
            }
            catch (FfmpegError e)
            {
                rep.Error = e.Message;
                rep.Add("拉流分析", "FAIL", "拉流分析失败：" + e.Message, null);
                return;
            }
            Tick(isCancelled);

            // 2.1 实际帧率与丢帧率
            int frames = ParseFrameCount(vStderr);
            if (!opts.RtspRate)
            {
                // 未勾选，跳过
            }
            else if (nomFps > 0 && frames >= th.MinFramesForRate)
            {
                double expect = nomFps * duration;
                double loss = expect > 0 ? (expect - frames) / expect : 0;
                if (loss < 0) loss = 0;
                double actualFps = frames / duration;
                string st;
                if (loss >= th.FrameLossFail) st = "FAIL";
                else if (loss >= th.FrameLossWarn) st = "WARN";
                else st = "PASS";
                rep.Add("实际帧率与丢帧", st, string.Format(
                    "标称 {0:0.##}fps → 实收 {1:0.##}fps（{2} 帧 / 期望 {3:0} 帧），丢帧率 {4:0.0}%",
                    nomFps, actualFps, frames, expect, loss * 100), null);
            }
            else if (frames >= 0)
            {
                rep.Add("实际帧率与丢帧", "WARN",
                    "标称帧率未知或拉流帧数过少（" + frames + " 帧），无法判断丢帧", null);
            }

            // 2.2 画面动态性（卡死 / 遮挡）
            List<Seg> freeze = Ffmpeg.ParseFreeze(vStderr);
            double freezeTotal = 0;
            double longestFreeze = 0;
            foreach (Seg sg in freeze)
            {
                if (!sg.End.HasValue) continue;
                double d = sg.End.Value - sg.Start.Value;
                freezeTotal += d;
                if (d > longestFreeze) longestFreeze = d;
            }
            double staticRatio = duration > 0 ? freezeTotal / duration : 0;
            if (staticRatio > 1) staticRatio = 1;
            if (!opts.RtspMotion)
            {
                // 未勾选，跳过
            }
            else if (staticRatio >= th.StaticFail)
                rep.Add("画面动态性", "FAIL", string.Format(
                    "拉流期间画面静止占比 {0:0%}（最长 {1:0.0}s），疑似卡死或镜头被遮挡", staticRatio, longestFreeze), null);
            else if (staticRatio >= th.StaticWarn)
                rep.Add("画面动态性", "WARN", string.Format(
                    "画面静止占比 {0:0%}（最长 {1:0.0}s），请确认是否本就无活动（监控场景人少时属正常）", staticRatio, longestFreeze), null);
            else
                rep.Add("画面动态性", "PASS", string.Format(
                    "画面持续有变化（静止占比 {0:0%}，最长静止 {1:0.0}s）", staticRatio, longestFreeze), null);

            // 2.3 黑帧（实时流上整段黑屏说明镜头被挡或夜视异常）
            List<Seg3> black = Ffmpeg.ParseBlack(vStderr);
            double blackTotal = 0;
            foreach (Seg3 sg in black) blackTotal += sg.Duration;
            double blackRatio = duration > 0 ? blackTotal / duration : 0;
            if (blackRatio > 1) blackRatio = 1;
            if (blackRatio >= 0.7)
                rep.Add("黑帧检测", "FAIL", string.Format("黑屏占比 {0:0%}（{1} 段），画面大部分为黑", blackRatio, black.Count), null);
            else if (blackRatio >= 0.4)
                rep.Add("黑帧检测", "WARN", string.Format("黑屏占比 {0:0%}（{1} 段），请确认是否符合预期", blackRatio, black.Count), null);
            else
                rep.Add("黑帧检测", "PASS", string.Format("黑屏占比 {0:0%}（{1} 段）", blackRatio, black.Count), null);

            // 2.4 视频解码错误
            int verrs = vErr != null ? vErr.Count : 0;
            if (verrs > 5) rep.Add("视频解码", "FAIL", "解码报错 " + verrs + " 条，画面数据损坏严重", null);
            else if (verrs > 0) rep.Add("视频解码", "WARN", "解码报错 " + verrs + " 条，可能有丢包/坏帧", null);
            else rep.Add("视频解码", "PASS", "解码无报错（0 条）", null);

            // ---------- 3. 包探测：码率稳定性 + 关键帧间隔 ----------
            List<double[]> pk = Ffmpeg.ProbePacketsDetail(url, "v", 400);
            Tick(isCancelled);
            if (pk.Count >= 10)
            {
                // 3.1 码率稳定性（按秒聚合包大小）
                double totalBytes = 0, maxPts = 0;
                foreach (double[] p in pk) { totalBytes += p[1]; if (p[0] > maxPts) maxPts = p[0]; }
                if (opts.RtspBitrate && maxPts > 1)
                {
                    int secs = (int)Math.Floor(maxPts) + 1;
                    double[] per = new double[secs];
                    foreach (double[] p in pk)
                    {
                        int idx = (int)Math.Floor(p[0]);
                        if (idx >= 0 && idx < secs) per[idx] += p[1];
                    }
                    double sum = 0; int n = 0;
                    foreach (double v in per) { if (v > 0) { sum += v; n++; } }
                    double mean = n > 0 ? sum / n : 0;
                    double var = 0;
                    foreach (double v in per) if (v > 0) var += (v - mean) * (v - mean);
                    double sd = n > 0 ? Math.Sqrt(var / n) : 0;
                    double cv = mean > 0 ? sd / mean : 0;
                    double kbps = mean * 8 / 1000.0;
                    string st;
                    string why = "";
                    if (kbps < th.MinBitrateKbps) { st = "FAIL"; why = "，码率过低"; }
                    else if (cv >= th.BitrateCvFail) { st = "FAIL"; why = "，波动过大"; }
                    else if (cv >= th.BitrateCvWarn) { st = "WARN"; why = "，波动偏大"; }
                    else st = "PASS";
                    rep.Add("码率稳定性", st, string.Format(
                        "平均 {0:0} kbps（峰值 {1:0} / 最低 {2:0} kbps），波动系数 {3:0.00}{4}",
                        kbps, Max(per) * 8 / 1000.0, MinNonZero(per) * 8 / 1000.0, cv, why), null);
                }

                // 3.2 关键帧间隔（录像回放的可定位性）
                List<double> keys = new List<double>();
                foreach (double[] p in pk) if (p[2] > 0) keys.Add(p[0]);
                if (!opts.RtspGop)
                {
                    // 未勾选，跳过
                }
                else if (keys.Count >= 2)
                {
                    double gopSum = 0; int gopN = 0; double gopMax = 0;
                    for (int i = 1; i < keys.Count; i++)
                    {
                        double g = keys[i] - keys[i - 1];
                        if (g <= 0 || g > 60) continue;
                        gopSum += g; gopN++;
                        if (g > gopMax) gopMax = g;
                    }
                    double gopAvg = gopN > 0 ? gopSum / gopN : 0;
                    string st;
                    if (gopMax > th.GopFail) st = "FAIL";
                    else if (gopMax > th.GopWarn) st = "WARN";
                    else st = "PASS";
                    rep.Add("关键帧间隔", st, string.Format(
                        "平均 {0:0.0}s / 最大 {1:0.0}s（{2} 个关键帧）；间隔越大，录像回放拖动定位越难",
                        gopAvg, gopMax, keys.Count), null);
                }
                else
                {
                    rep.Add("关键帧间隔", "WARN", "拉流期间未取到足够的关键帧（" + keys.Count + " 个），无法评估", null);
                }
            }
            else
            {
                if (opts.RtspBitrate) rep.Add("码率稳定性", "SKIP", "取包不足，无法评估码率", null);
                if (opts.RtspGop) rep.Add("关键帧间隔", "SKIP", "取包不足，无法评估关键帧间隔", null);
            }

            // ---------- 4. 音频（用拉流时长作参照）----------
            Tick(isCancelled);
            if (opts.RtspAudio && hasAudio)
            {
                string aStderr = "", aStdout = "";
                try
                {
                    AnalyzeResult ar = Ffmpeg.Analyze(url, null, Ffmpeg.AF_CHAIN_PICKUP, duration, (int)duration + 180, _tcpOpts);
                    aStderr = ar.Stderr; aStdout = ar.Stdout;
                }
                catch (FfmpegError e) { Log.Warn("音频拉流分析失败：" + e.Message); }

                Dictionary<string, double> vol = Ffmpeg.ParseVolume(aStderr);
                double meanV, maxV;
                bool hasMean = vol.TryGetValue("mean_volume", out meanV);
                bool hasMax = vol.TryGetValue("max_volume", out maxV);
                // 监控摄像头不带拾音是常态，音频无声不应让整路流判失败，
                // 因此实时流上音频相关项最高只到 WARN，并在说明里写清楚。
                if (hasMax && maxV < -70)
                    rep.Add("音频音量", "WARN", "全程几乎无声（峰值极低）—— 若该摄像头本就未接拾音可忽略", null);
                else if (hasMean && meanV < -60)
                    rep.Add("音频音量", "WARN", "平均音量过低，疑似静音 —— 若该摄像头本就未接拾音可忽略", null);
                else if (hasMax && maxV > -0.5) rep.Add("音频音量", "WARN", "峰值接近满幅，可能削波失真", null);
                else rep.Add("音频音量", "PASS", string.Format("平均 {0:0.0}dB / 峰值 {1:0.0}dB",
                        hasMean ? meanV : 0.0, hasMax ? maxV : 0.0), null);

                // 静音：以拉流时长作参照（实时流没有 duration）。
                // 注意 silencedetect 报出的 end 可能略超出拉流时长，比值要夹到 1.0 以内。
                List<Seg> silence = Ffmpeg.ParseSilence(aStderr);
                List<Seg> closed = new List<Seg>();
                double silTotal = 0;
                foreach (Seg sg in silence)
                    if (sg.End.HasValue) { closed.Add(sg); silTotal += sg.End.Value - sg.Start.Value; }
                double silRatio = duration > 0 ? silTotal / duration : 0;
                if (silRatio > 1) silRatio = 1;
                if (silRatio > 0.65) rep.Add("静音分布", "WARN", string.Format("静音占比 {0:0%}（{1} 段）—— 若该摄像头本就未接拾音可忽略", silRatio, closed.Count), null);
                else if (silRatio > 0.35) rep.Add("静音分布", "WARN", string.Format("静音占比 {0:0%}（{1} 段），请确认是否符合预期", silRatio, closed.Count), null);
                else rep.Add("静音分布", "PASS", string.Format("静音占比 {0:0%}（{1} 段）", silRatio, closed.Count), null);

                // ---------- 拾音器检测 ----------
                // 拾音器坏了/屏蔽失效时，输出的是持续的低频交流嗡鸣（50Hz 市电 + 谐波）。
                // 实测判据（见 Ffmpeg.AF_CHAIN_PICKUP 注释）：嗡鸣质心 117~144Hz、过零率 0.006~0.012；
                // 正常环境音质心 2600Hz+、过零率 0.2 左右。两者差 17~34 倍。
                if (opts.RtspPickup)
                {
                    Dictionary<string, double> st = Ffmpeg.ParseAStats(aStderr);
                    double centroid = Ffmpeg.ParseCentroidMean(aStderr);

                    // 精确测量：用 Goertzel 算法直接算 50Hz 及其谐波的能量占比。
                    // 为什么不用 ffmpeg 的高通/低通：那是二阶滤波器、滚降平缓，
                    // 所谓"45-55Hz 带"会漏进 100~200Hz 的人声基频 ——
                    // 实测说话时测出的占比（-7.8dB）比真实电流声（-10.4dB）还高，
                    // 只能靠"仅安静段"门限绕开，结果说话期间的交流声就漏检了。
                    // Goertzel 只算单一频点，实测：纯50Hz 0.0dB / 真实电流声 -9.0dB /
                    // 正常人声 -37.5dB，本来就分得开，不需要门限。
                    MainsResult mains = null;
                    try
                    {
                        mains = PickupAnalyzer.Analyze(url, duration,
                            _tcpOpts, th.PickupQuietGateEnabled ? th.PickupQuietDb : double.NaN);
                    }
                    catch (Exception ex) { Log.Debug("交流声分析失败：" + ex.Message); }

                    // 兼容路径：Goertzel 没跑出来时，退回旧的频段测量
                    double mainsRatio = double.NaN;
                    if (mains == null || !mains.Ok)
                    {
                        double fullRms;
                        if (st.TryGetValue("rms", out fullRms))
                        {
                            double bandDb = Ffmpeg.MeasureBandDb(url, th.PickupBandLo, th.PickupBandHi,
                                duration < 10 ? duration : 10, _tcpOpts);
                            if (!double.IsNaN(bandDb)) mainsRatio = bandDb - fullRms;
                        }
                    }

                    string[] verdict = JudgePickup(st, centroid, mainsRatio, mains, th);
                    rep.Add("拾音器检测", verdict[0], verdict[1], null);
                }
            }

            // ---------- 5. 拉流稳定性 ----------
            List<string> unstable = new List<string>();
            List<string> allErr = new List<string>(vErr);
            foreach (string ln in allErr)
                if (_unstableRe.IsMatch(ln) && unstable.Count < 30)
                    unstable.Add(ln.Trim().Length > 140 ? ln.Trim().Substring(0, 140) : ln.Trim());
            if (unstable.Count == 0)
                rep.Add("拉流稳定性", "PASS", string.Format("拉流 {0:0}s 期间流持续稳定，未发现断流/严重丢包", duration), null);
            else if (unstable.Count < th.UnstableFailCount)
                rep.Add("拉流稳定性", "WARN", string.Format("拉流期间出现 {0} 次不稳定信号：{1}",
                    unstable.Count, string.Join(" | ", unstable.ToArray(), 0, Math.Min(3, unstable.Count))), null);
            else
                rep.Add("拉流稳定性", "FAIL", string.Format("拉流期间出现 {0} 次断流/解码异常，流不稳定", unstable.Count), null);

            // ---------- 6. 时间水印是否在走（需 AI）----------
            Tick(isCancelled);
            if (opts.RtspWatermark && opts.CheckMage && opts.Mage.Url.Length > 0)
                CheckWatermarkAdvancing(url, duration, opts, rep);
        }

        /// <summary>
        /// 拾音器状态判定，返回 {状态, 说明}。
        /// 抽成独立方法是为了能用素材自检 —— 直接验证判据而不是只验证代码能跑。
        ///
        /// 判据依据：
        /// ① 合成素材（纯 50Hz/100Hz 正弦）：频谱质心 117~144Hz、过零率 0.006~0.012（低质心路径）
        /// ② 真实样本（B 站「麦克风有电流音怎么办」6~8s、9.5~10s 的电流声）：
        ///    主频就是 50Hz，**但频谱质心高达 1400~2100Hz**（因为带大量谐波），过零率也不低 ——
        ///    所以只看质心的判据抓不住真实电流麦。真实样本上最可靠的特征是
        ///    **45-55Hz 市电频段能量占全频段的比例**：故障时 -3 ~ -15dB，正常人声 -40 ~ -75dB。
        ///    因此新增 mainsRatioDb 路径，与低质心路径并存。
        /// </summary>
        public static string[] JudgePickup(Dictionary<string, double> st, double centroid,
            double mainsRatioDb, RtspThresholds th)
        {
            return JudgePickup(st, centroid, mainsRatioDb, null, th);
        }

        /// <summary>
        /// 拾音器状态判定（带 Goertzel 精确测量结果）。
        /// 判据优先级：
        ///   ① 无信号 → WARN（拾音器没接/断线，与"只剩嗡鸣"是两种故障）；
        ///   ② Goertzel 测出的 50Hz 占比 → FAIL/WARN/PASS（首选，精确、不受人声干扰）；
        ///   ③ 旧的频段占比 → 备用路径（Goertzel 失败时）；
        ///   ④ 低频谱质心 → 对纯低频嗡鸣有效（合成素材）。
        /// </summary>
        public static string[] JudgePickup(Dictionary<string, double> st, double centroid,
            double mainsRatioDb, MainsResult mains, RtspThresholds th)
        {
            double rmsDb = 0, zcr = 0;
            bool hasRms = st != null && st.TryGetValue("rms", out rmsDb);
            bool hasZcr = st != null && st.TryGetValue("zcr", out zcr);
            bool hasRatio = !double.IsNaN(mainsRatioDb) && !double.IsInfinity(mainsRatioDb);
            bool hasMains = mains != null && mains.Ok;

            if (hasRms && rmsDb <= th.PickupNoSignalDb)
                return new string[] { "WARN", string.Format(
                    "无有效音频信号（RMS {0:0.0}dB，低于 {1:0}dB）—— 拾音器可能未接、断线，或已损坏无输出。"
                    + "注意：「无信号」与「有信号但只剩嗡鸣」是两种不同故障，本项此处只判前者",
                    rmsDb, th.PickupNoSignalDb) };

            // ===== 首选：Goertzel 精确测量 =====
            if (hasMains)
            {
                double r50 = mains.Mains50RatioDb;
                // 安静段判定时可以用更灵敏的阈值：没人说话，50Hz 占比高就是真嗡鸣
                bool quietMode = th.PickupQuietGateEnabled && (mains.TotalDb < th.PickupQuietDb);
                double failDb = quietMode ? th.PickupMainsQuietFailDb : th.PickupMainsFailDb;
                double warnDb = quietMode ? th.PickupMainsQuietWarnDb : th.PickupMainsWarnDb;

                string basis = quietMode ? "安静段" : "全时段";
                if (r50 > failDb)
                    return new string[] { "FAIL", string.Format(
                        "检测到持续交流声：{0}的 50Hz 市电成分占全带能量仅低 {1:0.0}dB"
                        + "（判定线 {2:0}dB），100Hz 谐波低 {3:0.0}dB —— 能量被市电嗡鸣主导，"
                        + "是拾音器屏蔽失效 / 供电干扰 / 接地的典型故障特征",
                        basis, -r50, -failDb, -mains.Mains100RatioDb) };
                if (r50 > warnDb)
                    return new string[] { "WARN", string.Format(
                        "50Hz 市电成分偏高（{0}占全带能量低 {1:0.0}dB，判定线 {2:0}dB）—— "
                        + "未达故障标准，建议人工听一下是否有嗡嗡声",
                        basis, -r50, -failDb) };
                return new string[] { "PASS", string.Format(
                    "未发现交流声（50Hz 成分占全带能量低 {0:0.0}dB，判定线 {1:0}dB）{2}",
                    -r50, -failDb,
                    mains.Windows > 1 ? string.Format("，统计 {0} 个片段", mains.Windows) : "") };
            }

            // 分析器明确说了"没有可用音频"（比如全程无声）—— 直接把原因转达，别掉到 SKIP
            if (mains != null && !mains.Ok && mains.Note.Length > 0
                && st != null && st.Count == 0)
                return new string[] { "WARN", mains.Note + "。拾音器可能未接、断线或已损坏无输出" };

            // ===== 备用：旧的频段占比（Goertzel 不可用时）=====
            // 注意这条路径依赖"仅安静段"门限：滤波器滚降平缓，说话时 100Hz 人声基频会漏进来，
            // 实测说话段占比（-7.8dB）比真实电流声（-10.4dB）还高，不加门限必误报。
            bool gateOn = th.PickupQuietGateEnabled;
            bool quiet = hasRms && rmsDb < th.PickupQuietDb;
            bool ratioUsable = gateOn ? quiet : true;

            if (hasRatio && ratioUsable && mainsRatioDb > th.PickupHumRatioDb)
            {
                if (gateOn)
                    return new string[] { "FAIL", string.Format(
                        "检测到持续交流声（安静段 50Hz 频段能量比全频段仅低 {0:0.0}dB，正常应在 40dB 以上）—— "
                        + "没有人说话时市电嗡鸣仍主导音频，是拾音器屏蔽失效/供电干扰/接地的典型故障",
                        -mainsRatioDb) };
                return new string[] { "WARN", string.Format(
                    "50Hz 频段能量比全频段高 {0:0.0}dB —— 已关闭安静门限，说话时低频能量本身就高，"
                    + "此结果可能是人声造成的误判，请人工听一下确认是否有嗡嗡声",
                    -mainsRatioDb) };
            }
            if (hasRatio && ratioUsable && mainsRatioDb > th.PickupWarnRatioDb)
                return new string[] { "WARN", string.Format(
                    "50Hz 市电成分偏高（比全频段低 {0:0.0}dB）—— 未达故障标准，建议人工听一下是否有嗡嗡声",
                    -mainsRatioDb) };

            // —— 判据二：低频谱质心（对纯低频嗡鸣有效）——
            if (centroid > 0 && centroid < th.PickupHumCentroid && (!hasZcr || zcr < th.PickupHumZcr))
                return new string[] { "FAIL", string.Format(
                    "检测到持续低频嗡鸣（频谱质心 {0:0}Hz，过零率 {1:0.000}）—— "
                    + "能量几乎全部集中在低频，是拾音器故障或屏蔽失效的典型特征",
                    centroid, hasZcr ? zcr : 0.0) };

            if (centroid <= 0 && !hasRatio)
                return new string[] { "SKIP", "未能取到频谱数据，无法判断拾音器状态" };

            if (centroid > 0 && centroid < th.PickupWarnCentroid)
                return new string[] { "WARN", string.Format(
                    "音频能量明显偏向低频（频谱质心 {0:0}Hz，正常环境音应在 2000Hz 以上）—— "
                    + "未达故障标准，建议人工听一下是否有嗡鸣", centroid) };

            return new string[] { "PASS", string.Format(
                "未发现交流声{0}{1}",
                hasRatio ? string.Format("（50Hz 成分比全频段低 {0:0.0}dB，正常）", -mainsRatioDb) : "",
                centroid > 0 ? string.Format("，频谱质心 {0:0}Hz", centroid) : "") };
        }

        /// <summary>步骤边界检查取消。配合 Ffmpeg.KillRunning() 使用：杀进程让当前步快速返回，这里再抛出让整个分析退出。</summary>
        private static void Tick(Func<bool> isCancelled)
        {
            if (isCancelled != null && isCancelled()) throw new MageCheck.StoppedException();
        }

        /// <summary>读拉流开头与结尾两帧的时间水印并对比：水印在走 = 确实在实时录制。</summary>
        private static void CheckWatermarkAdvancing(string url, double duration, CheckOptions opts, MediaReport rep)
        {
            try
            {
                double t2 = duration > 12 ? duration - 2 : duration / 2;
                byte[] f1 = MageCheck.ExtractFrameJpeg(url, 0, 960);
                byte[] f2 = MageCheck.ExtractFrameJpeg(url, t2, 960);
                if (f1.Length == 0 || f2.Length == 0)
                {
                    rep.Add("时间水印是否在走", "SKIP", "拉流抽帧失败，无法核对水印", null);
                    return;
                }
                string q = "只回答一行：读出画面右上角或角落的时间水印数字，原样输出；看不清就回答：未识别到时间。";
                string a1 = MageCheck.QueryOllama(opts.Mage.Url, opts.Mage.Model, f1, q, 256);
                string a2 = MageCheck.QueryOllama(opts.Mage.Url, opts.Mage.Model, f2, q, 256);
                // 年份校准：实时流的水印时间应≈当前时间，锚点用系统时间最可靠
                string s1 = MageCheck.CalibrateOsdYear(ExtractAnyTime(a1), DateTime.Now);
                string s2 = MageCheck.CalibrateOsdYear(ExtractAnyTime(a2), DateTime.Now);
                if (s1.Length == 0 || s2.Length == 0)
                    rep.Add("时间水印是否在走", "SKIP",
                        "模型未能读出时间水印（首帧：" + (s1.Length > 0 ? s1 : "未识别") + "，末帧：" + (s2.Length > 0 ? s2 : "未识别") + "）", null);
                else if (s1 == s2)
                    rep.Add("时间水印是否在走", "FAIL",
                        "间隔 " + t2.ToString("0") + " 秒前后两次读到的时间完全相同（" + s1 + "），画面水印没有走动，疑似播放的是静止录像而非实时流", null);
                else
                    rep.Add("时间水印是否在走", "PASS", "水印在走动：" + s1 + " → " + s2, null);
            }
            catch (MageCheck.StoppedException) { throw; }
            catch (Exception ex)
            {
                rep.Add("时间水印是否在走", "SKIP", "AI 核对水印失败：" + ex.Message, null);
            }
        }

        /// <summary>从任意文本里抠出第一段时间（含纯时分秒）。</summary>
        private static string ExtractAnyTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            Match m = Regex.Match(s, @"\d{4}\s*[-/年]\s*\d{1,2}\s*[-/月]\s*\d{1,2}\s*[日号]?\s*\d{1,2}:\d{2}(:\d{2})?");
            if (m.Success) return m.Value;
            m = Regex.Match(s, @"\d{1,2}:\d{2}(:\d{2})?");
            if (m.Success) return m.Value;
            return "";
        }

        /// <summary>从 ffmpeg 进度行取实际解码帧数（取最后一行）。</summary>
        private static int ParseFrameCount(string stderr)
        {
            int last = -1;
            if (string.IsNullOrEmpty(stderr)) return last;
            foreach (string raw in stderr.Split('\n'))
            {
                Match m = _frameCountRe.Match(raw);
                if (m.Success)
                {
                    int v;
                    if (int.TryParse(m.Groups[1].Value, out v)) last = v;
                }
            }
            return last;
        }

        private static double Max(double[] a)
        {
            double m = 0; foreach (double v in a) if (v > m) m = v; return m;
        }

        private static double MinNonZero(double[] a)
        {
            double m = double.MaxValue; foreach (double v in a) if (v > 0 && v < m) m = v;
            return m == double.MaxValue ? 0 : m;
        }

        /// <summary>用 Checks.BuildMediaInfo 转字典。</summary>
        private static Dictionary<string, object> InfoDict(Dictionary<string, object> probe)
        {
            return Engine.InfoToDict(Checks.BuildMediaInfo(probe));
        }
    }
}
