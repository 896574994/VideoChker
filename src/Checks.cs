/* -*- coding: utf-8 -*-
 * Checks.cs — 检测判定模块（音频/视频/音画同步/音频质量）与报告模型
 * 判定规则与阈值与 Python 版 checker 完全一致。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VideoChecker
{
    // ==================== 报告模型 ====================

    public class CheckItem
    {
        public string Name;
        public string Status;      // PASS / WARN / FAIL / SKIP
        public string Detail;
        public Dictionary<string, object> Value;

        public CheckItem(string name, string status, string detail, Dictionary<string, object> value)
        {
            Name = name; Status = status; Detail = detail; Value = value;
        }
    }

    public class MediaReport
    {
        public string File;
        public List<CheckItem> Items = new List<CheckItem>();
        public Dictionary<string, object> MediaInfo = new Dictionary<string, object>();
        public string Error = "";

        // 及格率：仍然计算并展示（一眼看出多少项达标），但自 v2.1 起不再单独决定 PASS ——
        // 改由「总评不会优于任何单项结论」的规则判定，见 Overall 的说明。
        public static double PassThreshold = 0.9;
        public static double WarnThreshold = 0.7;

        /// <summary>WARN 项占比超过此值时，说明问题成片，总评直接判 FAIL。</summary>
        public static double WarnRateLimit = 0.30;

        /// <summary>
        /// 升级为 FAIL 所需的最少 WARN 项数。
        /// 只看比例有个缺陷：项目数少时（比如只有 3 项）1 项警告就占 33%，直接被判失败 ✗
        /// 所以要"数量够多 + 比例够高"同时满足。
        ///
        /// 取 2 而不是 3 的理由（审计实测）：
        ///   · 1 项 WARN —— 无论比例多高都不该判失败（可能是单项误报）✓
        ///   · 2 项 WARN 且占比 &gt; 30% —— 确实问题成片，该判失败 ✓
        ///   · 取 3 会把「3 项里 2 项警告（67%）」这种明显有问题的报告放过 ✗
        /// </summary>
        public static int WarnMinCount = 2;

        // 关键项保护：开启后（默认开），下列致命项 FAIL 直接一票否决总评，不走及格率豁免。
        // 注意：冻结帧检测不在名单内（人静止发呆会被误判冻结，按及格率处理更稳）。
        public static bool CriticalVeto = true;
        public static readonly string[] CriticalNamesArr = {
            "视频流存在", "音频流存在",
            "黑帧检测", "视频解码",
            "音频解码", "音频音量", "静音分布",
            "音画同步", "起始时间偏差", "时长偏差", "时间戳连续性", "信号对齐估计",
            "视频时长一致性", "音频时长一致性",
            "底噪电流声", "高频嘶声", "爆音咔哒"
        };

        public MediaReport(string file) { File = file; }

        /// <summary>合格项数（PASS 的检测项）。</summary>
        public int PassCount
        {
            get
            {
                int c = 0;
                foreach (CheckItem it in Items) if (it.Status == "PASS") c++;
                return c;
            }
        }

        /// <summary>参与判定的总项数（排除 SKIP）。</summary>
        public int TotalCount
        {
            get
            {
                int c = 0;
                foreach (CheckItem it in Items) if (it.Status != "SKIP") c++;
                return c;
            }
        }

        /// <summary>合格率 0~1（PASS 项 / 非 SKIP 项）。</summary>
        public double PassRate
        {
            get { int t = TotalCount; return t == 0 ? 0 : (double)PassCount / t; }
        }

        /// <summary>合格率文本，如 "90%"。</summary>
        public string PassRateText
        {
            get { return (int)Math.Round(PassRate * 100) + "%"; }
        }

        /// <summary>某个检测项是否属于致命项（CriticalNamesArr）。</summary>
        public static bool IsCritical(string name)
        {
            foreach (string cn in CriticalNamesArr)
                if (name == cn) return true;
            return false;
        }

        /// <summary>
        /// 总评判定。
        ///
        /// 原则：**总评不会优于任何单项结论**。
        /// 早先的纯及格率模型有个致命缺陷：只要合格项够多，个别 WARN / FAIL 会被"平均"掉。
        /// 实测教训 —— damaged.mkv 检出「解码报错 3 条」只判 WARN，最后靠 92% 及格率拿到了
        /// 「通过」，报告的结论与它自己列出的问题自相矛盾。
        ///
        /// 现在的规则：
        ///   ① 关键项 FAIL（保护开启时）→ FAIL，一票否决；
        ///   ② 出现 ≥2 项 FAIL → FAIL（问题不止一处，关掉保护也不放行）；
        ///   ③ 出现 1 项 FAIL  → WARN（确定有问题，但需要人工判断严重程度）；
        ///   ④ 出现 WARN 项    → WARN（警告同样不能被及格率吸收成"通过"）；
        ///      但若 WARN 项占比超过 WarnRateLimit，说明问题成片，直接判 FAIL；
        ///   ⑤ 全部 PASS       → PASS。
        ///
        /// 及格率仍然计算并展示（方便一眼看出有多少项达标），但不再单独决定 PASS。
        /// </summary>
        public string Overall
        {
            get
            {
                if (Error.Length > 0 && Items.Count == 0) return "ERROR";

                int total = 0, fails = 0, warns = 0;
                bool criticalFail = false;
                foreach (CheckItem it in Items)
                {
                    if (it.Status == "SKIP") continue;
                    total++;
                    if (it.Status == "FAIL")
                    {
                        fails++;
                        if (IsCritical(it.Name)) criticalFail = true;
                    }
                    else if (it.Status == "WARN") warns++;
                }
                if (total == 0) return "SKIP";

                // ① 关键项一票否决
                if (CriticalVeto && criticalFail) return "FAIL";
                // ② 多处失败
                if (fails >= 2) return "FAIL";
                // ③ 单处失败
                if (fails == 1) return "WARN";
                // ④ 有警告：警告太多则判失败，否则至少 WARN（不允许 PASS）
                if (warns > 0)
                {
                    // 必须"数量够多"且"比例够高"才升级为 FAIL。
                    // 只看比例时，3 项里 1 项 WARN 就占 33% 会被误判成失败 ✗（审计实测抓到）
                    if (warns >= WarnMinCount && (double)warns / total > WarnRateLimit) return "FAIL";
                    return "WARN";
                }
                // ⑤ 全通过
                return "PASS";
            }
        }

        public void Add(string name, string status, string detail, Dictionary<string, object> value)
        {
            Items.Add(new CheckItem(name, status, detail, value));
        }
    }

    // ==================== 阈值 ====================

    public class AudioThresholds
    {
        public double DurationGapWarn = 2.0;
        public double DurationGapFail = 5.0;
        public double SilenceRatioWarn = 0.35;
        public double SilenceRatioFail = 0.65;
        public double LongSilenceWarn = 10.0;
        public double LongSilenceFail = 30.0;
        public double VolumeFloorDb = -60.0;
        public double MaxVolumeFloorDb = -70.0;
        public double ClipHeadroomDb = 0.5;
        public int DecodeErrWarn = 1;
        public int DecodeErrFail = 5;
    }

    public class VideoThresholds
    {
        public double DurationGapWarn = 2.0;
        public double DurationGapFail = 5.0;
        public double BlackRatioWarn = 0.40;
        public double BlackRatioFail = 0.70;
        public double BlackMinDur = 1.0;
        public double FreezeWarn = 10.0;
        public double FreezeFail = 30.0;
        public double FreezeMinDur = 3.0;
        public int DecodeErrWarn = 1;
        public int DecodeErrFail = 5;
        public double PtsJitterRatioWarn = 0.02;
        public double PtsJitterRatioFail = 0.10;
    }

    public class SyncThresholds
    {
        public double OffsetWarnMs = 100.0;
        public double OffsetFailMs = 500.0;

        // ★★ 「时长偏差」的通过线 = **10 秒**（2026-09-15 用户定的）
        //
        //   用户拿报告截图指着那一行说：
        //     「时长偏差小于10秒内，算通过。」
        //   当时那行是：
        //     WARN  时长偏差  音视频时长差 0.75s（视频 34.80s / 音频 34.05s）
        //   ★ 他说得对 ✓ 0.75s 在这个场景里**根本不是问题** ✗
        //     监控录像的音视频两条流**本来就不会严丝合缝** ✓
        //       音频编码器有启动延迟 ✓ 结束时刻也常差个零点几秒 ✓
        //     拿"0.5 秒"当警告线 → **几乎每份录像都会 WARN** ✗
        //     而一条天天出现的 WARN = 没人看 ✓ = 把真正的问题淹掉 ✓
        //
        //   ★ 这和「冻结帧检测」是同一条道理（见 15.x 那一节）：
        //     检测门槛 3s ✓ 但 **10s 才 WARN、30s 才 FAIL** ✓
        //     因为"监控里 3~10s 静止是正常的"✓
        //     这里也是：**0.5s 的差是正常的** ✓
        //
        //   ★ 而且**判据要写进报告那一行** ✗（照抄冻结帧那条的做法 ✓）
        //     只写"差 0.75s"却判通过 → 看报告的人会以为程序算错了 ✓
        //
        //   三档：
        //     ≤ 10s         PASS   （用户定的通过线）
        //     10s ~ 60s     WARN   （差得有点多，请看一眼）
        //     > 60s         FAIL   （明显不是同一段，长度不一致）
        public double DurDiffWarn = 10.0;
        public double DurDiffFail = 60.0;

        public double PtsJumpFactor = 3.0;
        public int PtsJumpWarn = 2;
        public int PtsJumpFail = 8;
        public double AlignTolerance = 0.15;
        public int AlignMinPairs = 2;
    }

    public class QualityThresholds
    {
        public double NoiseFloorWarnDb = -45.0;
        public double NoiseFloorFailDb = -35.0;
        public double HissRatioWarn = 0.90;
        public double HissRatioFail = 1.20;
        public double PopJumpDb = 18.0;
        public double PopFloorDb = -30.0;
        public double PopFallDb = 8.0;
        public int PopCountWarn = 2;
        public int PopCountFail = 10;
    }

    public class MediaInfo
    {
        public string Container = "?";
        public double? Duration;
        public long? Size;
        public double? BitRate;
        public Dictionary<string, object> Video;
        public Dictionary<string, object> Audio;
    }

    // ==================== 检测选项 ====================

    public class CheckOptions
    {
        public bool CheckAudio = true;
        public bool CheckVideo = true;
        public bool CheckSync = true;
        public bool CheckQuality = false;   // 电流麦
        public bool CheckMage = false;      // AI 画面理解
        public double Sampling = 0.0;       // 0 = 全片
        public bool FastMode = false;
        public MageConfig Mage = new MageConfig();

        public AudioThresholds AudioTh = new AudioThresholds();
        public VideoThresholds VideoTh = new VideoThresholds();
        public SyncThresholds SyncTh = new SyncThresholds();
        public QualityThresholds QualityTh = new QualityThresholds();

        // 拉流业务检测（仅 RTSP 用）
        public bool RtspBizMode = true;                       // true=RTSP 专用判定；false=沿用旧的"当文件处理"
        public RtspThresholds Rtsp = new RtspThresholds();
        // 拉流业务检测的逐项开关（供 RTSP 专用页面勾选）
        public bool RtspRate = true;         // 实际帧率与丢帧
        public bool RtspBitrate = true;      // 码率稳定性
        public bool RtspGop = true;          // 关键帧间隔
        public bool RtspMotion = true;       // 画面动态性
        public bool RtspAudio = true;        // 音频
        public bool RtspPickup = true;       // 拾音器（低频交流嗡鸣检测）
        public bool RtspWatermark = true;    // 时间水印（需 AI）

        public double EffectiveSampling()
        {
            if (Sampling > 0) return Sampling;
            if (FastMode) return 60.0;
            return 0.0;
        }
    }

    // ==================== 检测实现 ====================

    public static class Checks
    {
        public const double QualityMaxSec = 600.0;
        public const double WinSec = 0.1;
        public const int SampleRate = 8000;

        private static string[] _audioOnlyExt = new string[] { ".m4a", ".mp3", ".wav", ".flac", ".aac", ".ogg" };

        public static bool IsAudioOnlyFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            for (int i = 0; i < _audioOnlyExt.Length; i++)
                if (ext == _audioOnlyExt[i]) return true;
            return false;
        }

        // ---------- 媒体信息 ----------
        public static MediaInfo BuildMediaInfo(Dictionary<string, object> probe)
        {
            MediaInfo info = new MediaInfo();
            Dictionary<string, object> fmt = GetObj(probe, "format");
            if (fmt != null)
            {
                info.Container = Ffmpeg.MiniGetStr(fmt, "format_name") ?? "?";
                info.Duration = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(fmt, "duration"));
                info.Size = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(fmt, "size")).HasValue ? (long?)Ffmpeg.SafeF(Ffmpeg.MiniGetStr(fmt, "size")).Value : null;
                info.BitRate = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(fmt, "bit_rate"));
            }
            List<object> streams = GetList(probe, "streams");
            if (streams != null)
            {
                foreach (object so in streams)
                {
                    Dictionary<string, object> s = so as Dictionary<string, object>;
                    if (s == null) continue;
                    string type = Ffmpeg.MiniGetStr(s, "codec_type");
                    if (type == "video" && info.Video == null)
                    {
                        info.Video = new Dictionary<string, object>();
                        info.Video["codec"] = Ffmpeg.MiniGetStr(s, "codec_name") ?? "?";
                        info.Video["width"] = Ffmpeg.MiniGetStr(s, "width");
                        info.Video["height"] = Ffmpeg.MiniGetStr(s, "height");
                        info.Video["fps"] = ParseFps(Ffmpeg.MiniGetStr(s, "avg_frame_rate"));
                        info.Video["pix_fmt"] = Ffmpeg.MiniGetStr(s, "pix_fmt") ?? "?";
                        info.Video["bit_rate"] = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(s, "bit_rate"));
                        info.Video["start_time"] = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(s, "start_time"));
                        info.Video["duration"] = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(s, "duration"));
                    }
                    else if (type == "audio" && info.Audio == null)
                    {
                        info.Audio = new Dictionary<string, object>();
                        info.Audio["codec"] = Ffmpeg.MiniGetStr(s, "codec_name") ?? "?";
                        info.Audio["sample_rate"] = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(s, "sample_rate"));
                        info.Audio["channels"] = Ffmpeg.MiniGetStr(s, "channels");
                        info.Audio["channel_layout"] = Ffmpeg.MiniGetStr(s, "channel_layout") ?? "?";
                        info.Audio["bit_rate"] = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(s, "bit_rate"));
                        info.Audio["start_time"] = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(s, "start_time"));
                        info.Audio["duration"] = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(s, "duration"));
                    }
                }
            }
            return info;
        }

        public static double? ParseFps(string rate)
        {
            if (string.IsNullOrEmpty(rate)) return null;
            try
            {
                if (rate.IndexOf('/') >= 0)
                {
                    string[] parts = rate.Split('/');
                    double den = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (den == 0) return null;
                    return Math.Round(double.Parse(parts[0], CultureInfo.InvariantCulture) / den, 3);
                }
                return Math.Round(double.Parse(rate, CultureInfo.InvariantCulture), 3);
            }
            catch { return null; }
        }

        // ---------- 音频完整性 ----------
        public static void CheckAudioIntegrity(string path, Dictionary<string, object> probe,
            string stderrText, string stdoutText, List<string> errorLines, AudioThresholds th, MediaReport rep)
        {
            List<object> streams = GetList(probe, "streams");
            List<Dictionary<string, object>> audioStreams = new List<Dictionary<string, object>>();
            if (streams != null)
                foreach (object so in streams)
                {
                    Dictionary<string, object> s = so as Dictionary<string, object>;
                    if (s != null && Ffmpeg.MiniGetStr(s, "codec_type") == "audio") audioStreams.Add(s);
                }
            double? containerDur = ContainerDuration(probe);

            if (audioStreams.Count == 0)
            {
                rep.Add("音频流存在", "FAIL", "媒体中不存在任何音频流", Dict("audio_streams", 0));
                return;
            }

            Dictionary<string, object> astream = audioStreams[0];
            double? audioDur = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(astream, "duration"));
            double? sampleRate = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(astream, "sample_rate"));
            string channels = Ffmpeg.MiniGetStr(astream, "channels");
            string codec = Ffmpeg.MiniGetStr(astream, "codec_name") ?? "?";

            // 参数
            List<string> paramNotes = new List<string>();
            if (sampleRate.HasValue && sampleRate.Value < 16000)
                paramNotes.Add("采样率 " + (int)sampleRate.Value + "Hz 偏低（<16kHz），疑似低质量音频");
            int ch = 0;
            int.TryParse(channels ?? "", out ch);
            if (ch > 2) paramNotes.Add(ch + " 声道（多声道）");
            string paramDetail = codec + " / " + (sampleRate.HasValue ? ((int)sampleRate.Value).ToString() : "?") + "Hz / " + (channels ?? "?") + "ch";
            if (paramNotes.Count > 0) paramDetail += "；" + string.Join("；", paramNotes.ToArray());
            rep.Add("音频参数", paramNotes.Count > 0 ? "WARN" : "PASS", paramDetail, null);

            // 时长一致性
            if (audioDur.HasValue && containerDur.HasValue)
            {
                double gap = containerDur.Value - audioDur.Value;
                if (gap > th.DurationGapFail)
                    rep.Add("音频时长一致性", "FAIL", string.Format("音轨比容器短 {0:0.00}s（容器 {1:0.00}s / 音频 {2:0.00}s），疑似尾部截断", gap, containerDur.Value, audioDur.Value), null);
                else if (gap > th.DurationGapWarn)
                    rep.Add("音频时长一致性", "WARN", string.Format("音轨比容器短 {0:0.00}s，需要留意", gap), null);
                else
                    rep.Add("音频时长一致性", "PASS", string.Format("音轨 {0:0.00}s ≈ 容器 {1:0.00}s", audioDur.Value, containerDur.Value), null);
            }

            // 解码错误
            int errs = errorLines != null ? errorLines.Count : 0;
            if (errs > th.DecodeErrFail)
                rep.Add("音频解码", "FAIL", "解码报错 " + errs + " 条，音频数据损坏严重", Dict("error_count", errs));
            else if (errs > th.DecodeErrWarn)
                rep.Add("音频解码", "WARN", "解码报错 " + errs + " 条，可能有坏帧", Dict("error_count", errs));
            else if (errs > 0)
                rep.Add("音频解码", "PASS", "解码报错 " + errs + " 条（低于警告线）", Dict("error_count", errs));
            else
                rep.Add("音频解码", "PASS", "解码无报错（0 条）", Dict("error_count", errs));

            // 音量
            Dictionary<string, double> vol = Ffmpeg.ParseVolume(stderrText ?? "");
            if (vol.Count > 0)
            {
                double meanV, maxV;
                bool hasMean = vol.TryGetValue("mean_volume", out meanV);
                bool hasMax = vol.TryGetValue("max_volume", out maxV);
                if (hasMax && maxV < th.MaxVolumeFloorDb)
                    rep.Add("音频音量", "FAIL", "全程几乎无声（峰值极低），疑似无声素材", null);
                else if (hasMean && meanV < th.VolumeFloorDb)
                    rep.Add("音频音量", "FAIL", string.Format("平均音量 {0:0.0}dB 过低，疑似静音素材", meanV), null);
                else if (hasMax && maxV > -th.ClipHeadroomDb)
                    rep.Add("音频音量", "WARN", string.Format("峰值 {0:0.0}dB 接近满幅，可能存在削波失真", maxV), null);
                else
                    rep.Add("音频音量", "PASS", string.Format("平均 {0:0.0}dB / 峰值 {1:0.0}dB", hasMean ? meanV : 0.0, hasMax ? maxV : 0.0), null);
            }
            else
                rep.Add("音频音量", "SKIP", "音量分析未执行", null);

            // 静音分布
            List<Seg> silence = Ffmpeg.ParseSilence(stderrText ?? "");
            // 实时流（RTSP）没有 duration，audioDur 会是 null。原来这里直接要求 audioDur.HasValue，
            // 导致实时流上已检测到的静音被整段丢弃、永远落到 "未检测到静音" 的 PASS 分支 ——
            // 会出现「音频音量判定全程无声 FAIL，静音分布却说未检测到静音 PASS」的自相矛盾结果。
            // 没有 duration 时改用「观测到的最晚静音结束时刻」当参照。
            double? refDur = audioDur;
            if (!refDur.HasValue && silence.Count > 0)
            {
                double maxEnd = 0;
                foreach (Seg sg in silence)
                    if (sg.End.HasValue && sg.End.Value > maxEnd) maxEnd = sg.End.Value;
                if (maxEnd > 0) refDur = maxEnd;
            }
            if (silence.Count > 0 && refDur.HasValue)
            {
                List<Seg> closed = new List<Seg>();
                foreach (Seg sg in silence)
                    if (sg.End.HasValue) closed.Add(sg);
                double totalSilence = 0;
                foreach (Seg sg in closed) totalSilence += sg.End.Value - sg.Start.Value;
                double ratio = totalSilence / refDur.Value;
                double longest = 0;
                foreach (Seg sg in closed) longest = Math.Max(longest, sg.End.Value - sg.Start.Value);
                string status, detail;
                if (ratio > th.SilenceRatioFail)
                { status = "FAIL"; detail = string.Format("静音占比 {0:0%}（共 {1} 段），音频大部分时间无声", ratio, closed.Count); }
                else if (ratio > th.SilenceRatioWarn)
                { status = "WARN"; detail = string.Format("静音占比 {0:0%}（共 {1} 段），请确认是否符合预期", ratio, closed.Count); }
                else if (longest > th.LongSilenceFail)
                { status = "FAIL"; detail = string.Format("最长连续静音 {0:0.0}s，疑似长时间无声音", longest); }
                else if (longest > th.LongSilenceWarn)
                { status = "WARN"; detail = string.Format("最长连续静音 {0:0.0}s", longest); }
                else
                { status = "PASS"; detail = string.Format("静音占比 {0:0%}，最长连续静音 {1:0.0}s", ratio, longest); }
                rep.Add("静音分布", status, detail, Dict("segments", closed));
            }
            else
                rep.Add("静音分布", "PASS", "未检测到明显静音段（≥1s）", null);

            // 音量过低段（非静音但很轻，RMS < -35dB，≥1s；astats 逐帧输出在 stderr）
            // 说明：该项为提示性检查，只做时间轴标注与文本提示，不改变整体判定（受源响度影响大，避免误伤）
            List<double[]> lowVol = Ffmpeg.ParseLowVolumeSegs(stderrText ?? "", -35.0, 1.0);
            if (lowVol.Count > 0 && audioDur.HasValue)
            {
                double totalLow = 0;
                foreach (double[] sg in lowVol) totalLow += (sg[1] > 0 ? sg[1] : audioDur.Value) - sg[0];
                double ratio = totalLow / audioDur.Value;
                double longest = 0;
                foreach (double[] sg in lowVol) longest = Math.Max(longest, (sg[1] > 0 ? sg[1] : audioDur.Value) - sg[0]);
                string detail;
                if (ratio > 0.30)
                    detail = string.Format("低音量占比 {0:0%}（{1} 段），声音整体偏小，建议试听核对（提示项，不影响判定）", ratio, lowVol.Count);
                else if (longest > 10.0)
                    detail = string.Format("最长低音量段 {0:0.0}s，声音偏小（提示项，不影响判定）", longest);
                else
                    detail = string.Format("低音量占比 {0:0%}（{1} 段），声音偏轻（提示项，不影响判定）", ratio, lowVol.Count);
                rep.Add("音量过低", "PASS", detail, Dict("segments", lowVol));
            }
            else
                rep.Add("音量过低", "PASS", "未检测到音量过低段（≥1s，RMS < -35dB）", null);

            // 尾部无声
            if (silence.Count > 0 && audioDur.HasValue)
            {
                bool tailInSilence = false;
                double tail = audioDur.Value;
                foreach (Seg sg in silence)
                {
                    bool sOk = (!sg.Start.HasValue || sg.Start.Value < tail - 0.5);
                    bool eOk = (!sg.End.HasValue || sg.End.Value > tail - 0.5);
                    if (sOk && eOk) { tailInSilence = true; break; }
                }
                rep.Add("音频结尾", tailInSilence ? "WARN" : "PASS",
                    tailInSilence ? "结尾处于静音段内，疑似尾部无声/切断" : "结尾有声音内容", null);
            }
        }

        // ---------- 视频完整性 ----------
        public static void CheckVideoIntegrity(string path, Dictionary<string, object> probe,
            string stderrText, List<string> errorLines, VideoThresholds th, MediaReport rep)
        {
            List<object> streams = GetList(probe, "streams");
            List<Dictionary<string, object>> videoStreams = new List<Dictionary<string, object>>();
            if (streams != null)
                foreach (object so in streams)
                {
                    Dictionary<string, object> s = so as Dictionary<string, object>;
                    if (s != null && Ffmpeg.MiniGetStr(s, "codec_type") == "video") videoStreams.Add(s);
                }
            double? containerDur = ContainerDuration(probe);

            if (videoStreams.Count == 0)
            {
                rep.Add("视频流存在", "FAIL", "媒体中不存在任何视频流", Dict("video_streams", 0));
                return;
            }

            Dictionary<string, object> vstream = videoStreams[0];
            double? videoDur = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(vstream, "duration"));
            string width = Ffmpeg.MiniGetStr(vstream, "width");
            string height = Ffmpeg.MiniGetStr(vstream, "height");
            string codec = Ffmpeg.MiniGetStr(vstream, "codec_name") ?? "?";
            double? fps = ParseFps(Ffmpeg.MiniGetStr(vstream, "avg_frame_rate"));
            if (!fps.HasValue) fps = ParseFps(Ffmpeg.MiniGetStr(vstream, "r_frame_rate"));

            // 参数
            List<string> paramNotes = new List<string>();
            int w = 0, h = 0;
            int.TryParse(width ?? "", out w);
            int.TryParse(height ?? "", out h);
            if (w > 0 && h > 0 && (w < 320 || h < 240)) paramNotes.Add("分辨率 " + w + "x" + h + " 偏低");
            if (fps.HasValue && fps.Value < 12) paramNotes.Add(string.Format("帧率 {0:0.00}fps 偏低", fps.Value));
            string paramDetail = codec + " / " + (width ?? "?") + "x" + (height ?? "?") + " / "
                + (fps.HasValue ? fps.Value.ToString("0.00", CultureInfo.InvariantCulture) : "?") + "fps / "
                + (Ffmpeg.MiniGetStr(vstream, "pix_fmt") ?? "?");
            if (paramNotes.Count > 0) paramDetail += "；" + string.Join("；", paramNotes.ToArray());
            rep.Add("视频参数", paramNotes.Count > 0 ? "WARN" : "PASS", paramDetail, null);

            // 时长一致性
            if (videoDur.HasValue && containerDur.HasValue)
            {
                double gap = containerDur.Value - videoDur.Value;
                if (gap > th.DurationGapFail)
                    rep.Add("视频时长一致性", "FAIL", string.Format("视频比容器短 {0:0.00}s（容器 {1:0.00}s / 视频 {2:0.00}s），疑似尾部截断", gap, containerDur.Value, videoDur.Value), null);
                else if (gap > th.DurationGapWarn)
                    rep.Add("视频时长一致性", "WARN", string.Format("视频比容器短 {0:0.00}s，需要留意", gap), null);
                else
                    rep.Add("视频时长一致性", "PASS", string.Format("视频 {0:0.00}s ≈ 容器 {1:0.00}s", videoDur.Value, containerDur.Value), null);
            }

            // 解码错误（花屏段：用错误行出现时最近的进度时间定位）
            int errs = errorLines != null ? errorLines.Count : 0;
            List<double[]> errSegs = Ffmpeg.MergeErrorPoints(Ffmpeg.ExtractErrorTimes(stderrText ?? ""), 2.0);
            if (errs > th.DecodeErrFail)
                rep.Add("视频解码", "FAIL", "解码报错 " + errs + " 条，画面数据损坏严重（可能花屏/绿屏）", Dict("error_count", errs, "segments", errSegs));
            else if (errs > th.DecodeErrWarn)
                rep.Add("视频解码", "WARN", "解码报错 " + errs + " 条，可能有坏帧", Dict("error_count", errs, "segments", errSegs));
            else if (errs > 0)
                rep.Add("视频解码", "PASS", "解码报错 " + errs + " 条（低于警告线）", Dict("error_count", errs, "segments", errSegs));
            else
                rep.Add("视频解码", "PASS", "解码无报错（0 条）", Dict("error_count", errs, "segments", errSegs));

            // 黑帧
            List<Seg3> black = Ffmpeg.ParseBlack(stderrText ?? "");
            if (videoDur.HasValue && black.Count > 0)
            {
                double totalBlack = 0;
                foreach (Seg3 sg in black) totalBlack += sg.Duration;
                double ratio = totalBlack / videoDur.Value;
                string status, detail;
                if (ratio > th.BlackRatioFail)
                { status = "FAIL"; detail = string.Format("黑帧占比 {0:0%}（{1} 段），画面大部分为黑屏", ratio, black.Count); }
                else if (ratio > th.BlackRatioWarn)
                { status = "WARN"; detail = string.Format("黑帧占比 {0:0%}（{1} 段），请确认是否符合预期", ratio, black.Count); }
                else
                { status = "PASS"; detail = string.Format("黑帧占比 {0:0%}（{1} 段）", ratio, black.Count); }
                rep.Add("黑帧检测", status, detail, Dict("segments", black));
            }
            else
                rep.Add("黑帧检测", "PASS", string.Format("未检测到黑帧段（≥{0:0.#}s）", th.BlackMinDur), null);

            // 冻结
            List<Seg> freeze = Ffmpeg.ParseFreeze(stderrText ?? "");
            List<Seg> closedF = new List<Seg>();
            foreach (Seg sg in freeze)
                if (sg.Start.HasValue && sg.End.HasValue) closedF.Add(sg);
            if (closedF.Count > 0)
            {
                double longest = 0;
                foreach (Seg sg in closedF) longest = Math.Max(longest, sg.End.Value - sg.Start.Value);
                string status, detail;
                if (longest > th.FreezeFail)
                {
                    status = "FAIL";
                    detail = string.Format("画面冻结最长 {0:0.0}s（{1} 段），超过 {2:0.#}s 失败线，疑似画面卡死",
                        longest, closedF.Count, th.FreezeFail);
                }
                else if (longest > th.FreezeWarn)
                {
                    status = "WARN";
                    detail = string.Format("画面冻结最长 {0:0.0}s（{1} 段），超过 {2:0.#}s 警告线，请确认是否该时段本就无活动",
                        longest, closedF.Count, th.FreezeWarn);
                }
                else
                {
                    // 这里必须把阈值写出来：只写"冻结 3.3s"却判通过，看报告的人会以为程序算错了。
                    // 检测门槛是 FreezeMinDur（3s），但 3~10s 的静止在监控里属正常（没人走动）。
                    status = "PASS";
                    detail = string.Format("画面冻结最长 {0:0.0}s（{1} 段），未超过 {2:0.#}s 警告线（监控场景短时静止属正常）",
                        longest, closedF.Count, th.FreezeWarn);
                }
                rep.Add("冻结帧检测", status, detail, Dict("segments", closedF));
            }
            else
                rep.Add("冻结帧检测", "PASS", string.Format("未检测到画面冻结（≥{0:0.#}s）", th.FreezeMinDur), null);

            // 帧率稳定性
            List<double> packets = new List<double>();
            try { packets = Ffmpeg.ProbePackets(path, "v", 400); }
            catch { packets = new List<double>(); }
            if (packets.Count >= 10)
            {
                List<double> intervals = new List<double>();
                for (int i = 1; i < packets.Count; i++)
                {
                    double iv = packets[i] - packets[i - 1];
                    if (iv >= 0) intervals.Add(iv);
                }
                if (intervals.Count > 0)
                {
                    double median = Median(intervals);
                    if (median > 0)
                    {
                        int anomalies = 0;
                        foreach (double iv in intervals)
                            if (iv > 2.5 * median) anomalies++;
                        double ratio = (double)anomalies / intervals.Count;
                        string status, detail;
                        if (ratio > th.PtsJitterRatioFail)
                        { status = "FAIL"; detail = string.Format("帧间隔异常占比 {0:0.0%}（{1}/{2}），时间轴抖动严重", ratio, anomalies, intervals.Count); }
                        else if (ratio > th.PtsJitterRatioWarn)
                        { status = "WARN"; detail = string.Format("帧间隔异常占比 {0:0.0%}（{1}/{2}），可能有丢帧/停顿", ratio, anomalies, intervals.Count); }
                        else
                        { status = "PASS"; detail = string.Format("帧间隔稳定（中位 {0:0.0}ms，异常 {1:0.0%}）", median * 1000, ratio); }
                        rep.Add("帧率稳定性", status, detail, null);
                        goto stabilityDone;
                    }
                }
            }
            rep.Add("帧率稳定性", "SKIP", "包数量不足，无法分析", null);
        stabilityDone: ;
        }

        // ---------- 音画同步 ----------
        public static void CheckAvSync(string path, Dictionary<string, object> probe,
            SyncThresholds th, List<Seg> silenceSegs, List<Seg3> blackSegs, MediaReport rep)
        {
            List<object> streams = GetList(probe, "streams");
            List<Dictionary<string, object>> vstreams = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> astreams = new List<Dictionary<string, object>>();
            if (streams != null)
                foreach (object so in streams)
                {
                    Dictionary<string, object> s = so as Dictionary<string, object>;
                    if (s == null) continue;
                    string t = Ffmpeg.MiniGetStr(s, "codec_type");
                    if (t == "video") vstreams.Add(s);
                    else if (t == "audio") astreams.Add(s);
                }

            if (vstreams.Count == 0 || astreams.Count == 0)
            {
                rep.Add("音画同步", "FAIL", "缺少视频流或音频流，无法进行音画同步检测", null);
                return;
            }

            Dictionary<string, object> vs = vstreams[0];
            Dictionary<string, object> as_ = astreams[0];
            double? vStart = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(vs, "start_time"));
            double? aStart = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(as_, "start_time"));
            double? vDur = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(vs, "duration"));
            double? aDur = Ffmpeg.SafeF(Ffmpeg.MiniGetStr(as_, "duration"));

            // 1. 起始时间偏差
            if (vStart.HasValue && aStart.HasValue)
            {
                double offsetMs = (vStart.Value - aStart.Value) * 1000.0;
                double absMs = Math.Abs(offsetMs);
                string status, detail;
                if (absMs > th.OffsetFailMs)
                {
                    status = "FAIL";
                    detail = string.Format("音视频起始时间偏差 {0:+0;-0}ms（视频从 {1:0.000}s、音频从 {2:0.000}s 开始），整体不同步，需重新封装",
                        offsetMs, vStart.Value, aStart.Value);
                }
                else if (absMs > th.OffsetWarnMs)
                {
                    status = "WARN";
                    detail = string.Format("音视频起始时间偏差 {0:+0;-0}ms（视频 {1:0.000}s / 音频 {2:0.000}s）", offsetMs, vStart.Value, aStart.Value);
                }
                else
                {
                    status = "PASS";
                    detail = string.Format("起始时间偏差 {0:+0;-0}ms（视频 {1:0.000}s / 音频 {2:0.000}s）", offsetMs, vStart.Value, aStart.Value);
                }
                rep.Add("起始时间偏差", status, detail, null);
            }
            else
                rep.Add("起始时间偏差", "SKIP", "缺少流的起始时间戳", null);

            // 2. 时长偏差
            //
            // ★ 判据写进 detail ✗（2026-09-15 用户把通过线定成 10 秒之后）
            //   照抄「冻结帧检测」那条的做法 ✓
            //   只写"差 0.75s"却判通过 → 看报告的人会以为程序算错了 ✓
            if (vDur.HasValue && aDur.HasValue)
            {
                double diff = Math.Abs(vDur.Value - aDur.Value);
                string status, detail;
                if (diff > th.DurDiffFail)
                {
                    status = "FAIL";
                    detail = string.Format("音视频时长差 {0:0.00}s（视频 {1:0.00}s / 音频 {2:0.00}s），"
                        + "超过 {3:0.#}s 失败线，两条流长度对不上，疑似缺一段",
                        diff, vDur.Value, aDur.Value, th.DurDiffFail);
                }
                else if (diff > th.DurDiffWarn)
                {
                    status = "WARN";
                    detail = string.Format("音视频时长差 {0:0.00}s（视频 {1:0.00}s / 音频 {2:0.00}s），"
                        + "超过 {3:0.#}s 通过线，请确认是否该有声音的时段确实短了",
                        diff, vDur.Value, aDur.Value, th.DurDiffWarn);
                }
                else
                {
                    status = "PASS";
                    detail = string.Format("音视频时长差 {0:0.00}s（视频 {1:0.00}s / 音频 {2:0.00}s），"
                        + "未超过 {3:0.#}s 通过线（监控录像两条流本来就差一点，属正常）",
                        diff, vDur.Value, aDur.Value, th.DurDiffWarn);
                }
                rep.Add("时长偏差", status, detail, null);
            }
            else
                rep.Add("时长偏差", "SKIP", "缺少流的时长信息", null);

            // 3. 时间戳连续性
            int vJumps = CountPtsJumps(path, "v", th);
            int aJumps = CountPtsJumps(path, "a", th);
            int totalJumps = vJumps + aJumps;
            string js, jd;
            if (totalJumps > th.PtsJumpFail)
            { js = "FAIL"; jd = string.Format("时间戳跳变 {0} 次（视频 {1} / 音频 {2}），时间轴严重不连续", totalJumps, vJumps, aJumps); }
            else if (totalJumps > th.PtsJumpWarn)
            { js = "WARN"; jd = string.Format("时间戳跳变 {0} 次（视频 {1} / 音频 {2}），可能存在缺段/编辑痕迹", totalJumps, vJumps, aJumps); }
            else
            { js = "PASS"; jd = string.Format("时间戳连续（视频跳变 {0} / 音频跳变 {1}）", vJumps, aJumps); }
            rep.Add("时间戳连续性", js, jd, null);

            // 4. 信号对齐估计
            List<double> silenceStarts = new List<double>();
            foreach (Seg sg in silenceSegs)
                if (sg.Start.HasValue) silenceStarts.Add(sg.Start.Value);
            List<double> blackStarts = new List<double>();
            foreach (Seg3 sg in blackSegs) blackStarts.Add(sg.Start);

            double? est = null; int matched = 0;
            EstimateOffset(silenceStarts, blackStarts, th.AlignTolerance, out est, out matched);
            if (est.HasValue && matched >= 1)
            {
                double absMs2 = Math.Abs(est.Value * 1000.0);
                string status;
                if (matched >= th.AlignMinPairs && absMs2 > th.OffsetFailMs) status = "FAIL";
                else if (absMs2 > th.OffsetWarnMs) status = "WARN";
                else status = "PASS";
                string conf = matched >= th.AlignMinPairs ? "高" : "低";
                rep.Add("信号对齐估计", status,
                    string.Format("静音/黑帧信号估算整体偏移 {0:+0;-0}ms（{1} 处对齐，置信度{2}；正值≈画面滞后/声音超前，负值≈画面超前/声音滞后）",
                        est.Value * 1000.0, matched, conf), null);
            }
            else
                rep.Add("信号对齐估计", "SKIP",
                    string.Format("参考信号不足（静音起点 {0} / 黑帧起点 {1}），无法估计偏移", silenceStarts.Count, blackStarts.Count), null);
        }

        private static int CountPtsJumps(string path, string streamSpec, SyncThresholds th)
        {
            List<double> ts = new List<double>();
            try { ts = Ffmpeg.ProbePackets(path, streamSpec, 400); }
            catch { return 0; }
            if (ts.Count < 10) return 0;
            List<double> intervals = new List<double>();
            for (int i = 1; i < ts.Count; i++) intervals.Add(ts[i] - ts[i - 1]);
            List<double> positive = new List<double>();
            foreach (double iv in intervals) if (iv > 0) positive.Add(iv);
            if (positive.Count == 0) return 0;
            double median = Median(positive);
            if (median <= 0) return 0;
            int jumps = 0;
            foreach (double iv in intervals)
            {
                if (iv < 0) jumps++;
                else if (iv > th.PtsJumpFactor * median) jumps++;
            }
            return jumps;
        }

        private static void EstimateOffset(List<double> silenceStarts, List<double> blackStarts, double tolerance,
            out double? best, out int bestCount)
        {
            best = null; bestCount = 0;
            if (silenceStarts.Count == 0 || blackStarts.Count == 0) return;
            List<double> candidates = new List<double>();
            for (int i = 0; i < blackStarts.Count; i++)
                for (int j = 0; j < silenceStarts.Count; j++)
                {
                    double d = blackStarts[i] - silenceStarts[j];
                    if (Math.Abs(d) <= 5.0) candidates.Add(d);
                }
            if (candidates.Count == 0) return;
            for (int i = 0; i < candidates.Count; i++)
            {
                int count = 0;
                for (int j = 0; j < candidates.Count; j++)
                    if (Math.Abs(candidates[j] - candidates[i]) <= tolerance) count++;
                if (count > bestCount)
                {
                    bestCount = count;
                    best = candidates[i];
                }
            }
        }

        // ---------- 音频质量（电流麦） ----------
        public static void CheckAudioQuality(string path, Dictionary<string, object> probe,
            double sampling, QualityThresholds th, MediaReport rep)
        {
            List<object> streams = GetList(probe, "streams");
            bool hasAudio = false;
            if (streams != null)
                foreach (object so in streams)
                {
                    Dictionary<string, object> s = so as Dictionary<string, object>;
                    if (s != null && Ffmpeg.MiniGetStr(s, "codec_type") == "audio") { hasAudio = true; break; }
                }
            if (!hasAudio)
            {
                rep.Add("音频质量", "SKIP", "媒体中不存在音频流", null);
                return;
            }

            double dur = sampling > 0 ? sampling : QualityMaxSec;
            if (dur > QualityMaxSec) dur = QualityMaxSec;
            bool truncated = dur >= QualityMaxSec;

            byte[] data;
            try { data = Ffmpeg.DecodeAudioPcm(path, dur, 900); }
            catch (FfmpegError e)
            {
                rep.Add("音频质量", "SKIP", "PCM 解码失败：" + e.Message, null);
                return;
            }
            if (data.Length < (int)(WinSec * SampleRate) * 2)
            {
                rep.Add("音频质量", "SKIP", string.Format("音频过短（{0:0.00}s），无法分析", (double)(data.Length / 2) / SampleRate), null);
                return;
            }

            List<double> dbs, ratios, times;
            WindowStats(data, out dbs, out ratios, out times);
            if (dbs.Count == 0)
            {
                rep.Add("音频质量", "SKIP", "窗口分析失败", null);
                return;
            }

            if (truncated)
                rep.Add("音频质量", "PASS", string.Format("仅分析前 {0:0}s（长视频采样），结果仅供参考", QualityMaxSec), null);

            // 1. 底噪电流声：P10
            List<double> sortedDb = new List<double>(dbs);
            sortedDb.Sort();
            double p10 = sortedDb[Math.Max(0, (int)(sortedDb.Count * 0.10))];
            if (p10 > th.NoiseFloorFailDb)
                rep.Add("底噪电流声", "FAIL", string.Format("安静时段电平 {0:0.0}dB 过高（> {1:0}dB），疑似持续电流声/底噪", p10, th.NoiseFloorFailDb), null);
            else if (p10 > th.NoiseFloorWarnDb)
                rep.Add("底噪电流声", "WARN", string.Format("安静时段电平 {0:0.0}dB 偏高（> {1:0}dB），可能有轻微底噪/电流声", p10, th.NoiseFloorWarnDb), null);
            else
                rep.Add("底噪电流声", "PASS", string.Format("安静时段电平 {0:0.0}dB，无明显底噪", p10), null);

            // 2. 高频嘶声：差分占比中位数
            List<double> sr = new List<double>(ratios);
            sr.Sort();
            double hiss = sr.Count % 2 == 1 ? sr[sr.Count / 2] : (sr[sr.Count / 2 - 1] + sr[sr.Count / 2]) / 2.0;
            if (hiss > th.HissRatioFail)
                rep.Add("高频嘶声", "FAIL", string.Format("高频成分占比 {0:0.00} 过高，疑似持续滋滋声/电流高频噪声", hiss), null);
            else if (hiss > th.HissRatioWarn)
                rep.Add("高频嘶声", "WARN", string.Format("高频成分占比 {0:0.00} 偏高，可能有嘶嘶声", hiss), null);
            else
                rep.Add("高频嘶声", "PASS", string.Format("高频成分占比 {0:0.00}，频谱正常", hiss), null);

            // 3. 爆音咔哒
            List<double> pops = new List<double>();
            for (int i = 1; i < dbs.Count - 1; i++)
            {
                double jump = dbs[i] - dbs[i - 1];
                double fall = dbs[i + 1] - dbs[i];
                if (dbs[i] > th.PopFloorDb && jump > th.PopJumpDb && fall < -th.PopFallDb)
                    pops.Add(Math.Round(times[i], 2));
            }
            int nPop = pops.Count;
            if (nPop > th.PopCountFail)
                rep.Add("爆音咔哒", "FAIL", "检测到 " + nPop + " 次爆音/咔哒（> " + th.PopCountFail + " 次），电平突变频繁", null);
            else if (nPop > th.PopCountWarn)
                rep.Add("爆音咔哒", "WARN", string.Format("检测到 {0} 次爆音/咔哒（首次 @{1:0.0}s）", nPop, pops[0]), null);
            else if (nPop > 0)
                rep.Add("爆音咔哒", "PASS", "爆音 " + nPop + " 次（低于警告线）", null);
            else
                rep.Add("爆音咔哒", "PASS", "未检测到爆音/咔哒", null);
        }

        private static void WindowStats(byte[] data, out List<double> dbs, out List<double> ratios, out List<double> times)
        {
            dbs = new List<double>();
            ratios = new List<double>();
            times = new List<double>();
            int win = SampleRate * (int)(WinSec * 10) / 10;  // 800 样本
            int total = data.Length / 2;
            int usable = (total / win) * win;
            if (usable < win) return;
            short[] samples = new short[usable];
            Buffer.BlockCopy(data, 0, samples, 0, usable * 2);

            double dbFloor = -120.0;
            for (int start = 0; start <= usable - win; start += win)
            {
                double acc = 0.0, dacc = 0.0;
                short prev = samples[start];
                for (int i = start; i < start + win; i++)
                {
                    short x = samples[i];
                    acc += (double)x * x;
                    double d = x - prev;
                    dacc += d * d;
                    prev = x;
                }
                double rms = Math.Sqrt(acc / win);
                double t = (double)start / SampleRate;
                if (rms < 1e-9)
                {
                    dbs.Add(dbFloor);
                    ratios.Add(0.0);
                }
                else
                {
                    dbs.Add(20.0 * Math.Log10(rms / 32768.0));
                    double drms = Math.Sqrt(dacc / (win - 1));
                    ratios.Add(drms / rms);
                }
                times.Add(t);
            }
        }

        // ---------- 通用 ----------
        private static double Median(List<double> values)
        {
            List<double> sorted = new List<double>(values);
            sorted.Sort();
            int n = sorted.Count;
            if (n % 2 == 1) return sorted[n / 2];
            return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }

        private static Dictionary<string, object> GetObj(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v))
                return v as Dictionary<string, object>;
            return null;
        }

        private static List<object> GetList(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v))
                return v as List<object>;
            return null;
        }

        private static double? ContainerDuration(Dictionary<string, object> probe)
        {
            Dictionary<string, object> fmt = GetObj(probe, "format");
            if (fmt == null) return null;
            return Ffmpeg.SafeF(Ffmpeg.MiniGetStr(fmt, "duration"));
        }

        private static Dictionary<string, object> Dict(string key, object val)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d[key] = val;
            return d;
        }

        private static Dictionary<string, object> Dict(string k1, object v1, string k2, object v2)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d[k1] = v1;
            d[k2] = v2;
            return d;
        }
    }
}
