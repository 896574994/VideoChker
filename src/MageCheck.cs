/* -*- coding: utf-8 -*-
 * MageCheck.cs — AI 画面理解（Ollama / OpenAI 兼容接口，可选）
 * 与 Python 版 checker/mage.py 的在线模式判定等价（均匀抽帧简化版）。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    /// <summary>AI 画面理解配置。</summary>
    public class MageConfig
    {
        public string Url = "http://127.0.0.1:11434";
        public string Model = "qwen2.5vl:7b";
        public int Frames = 4;
        public string Strategy = "auto";   // auto=智能(动静优先) motion=仅动静 uniform=均匀 custom=自定义时间点 full=全片分析(按时长按需抽帧)
        public List<double> CustomTimes = new List<double>();
        public double FullInterval = 15;    // 全片分析：抽帧间隔（秒）
        public int FullMaxFrames = 120;     // 全片分析：帧数上限（长视频自动稀释间隔）
        public int MaxTokens = 768;   // 注意：qwen3-vl 等"思考型"模型会先输出推理，
                                      // 预算太小会被推理吃光导致正文为空，故默认给足
        public string Question =
            "请分析这张视频截帧，输出两行：\n" +
            "第一行：状态|原因。状态只能是 NORMAL 或 ABNORMAL 这两个词之一（不要写“画面正常”或“画面异常”）。" +
            "注意：画面昏暗、光线不足、偏暗、曝光不足属于环境或风格特征，不是故障，应判 NORMAL；" +
            "只有黑屏、花屏/雪花、画面冻结卡住、无信号、大量马赛克、镜头遮挡、严重偏色等信号故障才判 ABNORMAL。" +
            "原因用一句话说明，不超过30字。\n" +
            "第二行：画面内容描述，以“内容：”开头，用一句话说明画面里有什么、正在发生什么，不超过40字。";
    }

    public static class MageCheck
    {
        private static string[] _severeWords = new string[] {
            "黑屏", "黑场", "全黑", "纯黑", "无信号", "花屏", "雪花", "冻结",
            "卡住", "马赛克", "无法识别画面", "静止画面", "黑画面", "画面丢失"
        };

        // 环境/风格性描述（昏暗、光线不足等）——不等于信号故障，命中且无故障词时按正常处理
        private static string[] _envWords = new string[] {
            "光线不足", "光线暗", "昏暗", "曝光不足", "偏暗", "较暗", "阴影",
            "拍摄角度", "环境光", "灯光不足", "光照不足", "光线较暗", "黑暗"
        };

        /// <summary>用户主动点「停止」导致的中断 —— 这不是错误，调用方应当安静地结束。</summary>
        public class StoppedException : Exception
        {
            public StoppedException() : base("用户已停止") { }
        }

        // ==================== AI 调用互斥 ====================
        // 「AI 画面理解」与「内容理解/文搜」共用同一台 Ollama。两个窗口同时开跑会把
        // 本机显存和推理队列压满（表现为两边都变极慢甚至超时），所以全局只放行一个。

        private static int _aiBusy;                    // 0=空闲，1=占用
        private static volatile string _aiHolder = "";  // 占用者名称，写进日志便于排查

        /// <summary>当前是否有 AI 任务在跑。</summary>
        public static bool AiBusy { get { return _aiBusy != 0; } }

        /// <summary>当前占用者名称（无人占用时为空串）。</summary>
        public static string AiHolder { get { return _aiHolder; } }

        /// <summary>尝试占用 AI 名额。返回 false 表示已有任务在跑，holder 里是占用者名称。</summary>
        public static bool TryAcquire(string who, out string holder)
        {
            holder = _aiHolder;
            if (System.Threading.Interlocked.CompareExchange(ref _aiBusy, 1, 0) != 0) return false;
            _aiHolder = who;
            Log.Info("AI 调用互斥：已占用（" + who + "）");
            return true;
        }

        /// <summary>释放 AI 名额。</summary>
        public static void Release(string who)
        {
            _aiHolder = "";
            System.Threading.Interlocked.Exchange(ref _aiBusy, 0);
            Log.Info("AI 调用互斥：已释放（" + who + "）");
        }

        /// <summary>当前正在进行的推理请求。供「停止」按钮中途打断，否则要等超时（可能几分钟）。</summary>
        private static volatile HttpWebRequest _currentReq;

        /// <summary>立即中断正在进行的推理请求（停止按钮调用）。</summary>
        public static void AbortCurrent()
        {
            HttpWebRequest r = _currentReq;
            if (r != null) { try { r.Abort(); } catch { } }
        }

        /// <summary>规范化 Ollama 地址（剥 /v1、/api 后缀）。</summary>
        public static string NormalizeBase(string url)
        {
            if (string.IsNullOrEmpty(url)) return "http://127.0.0.1:11434";
            string u = url.Trim().TrimEnd('/');
            if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                u = u.Substring(0, u.Length - 3);
            else if (u.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                u = u.Substring(0, u.Length - 4);
            if (u.Length == 0) return "http://127.0.0.1:11434";
            return u;
        }

        /// <summary>自动获取 Ollama 模型列表（GET /api/tags）。</summary>
        public static List<string> FetchModels(string baseUrl)
        {
            List<string> names = new List<string>();
            string u = NormalizeBase(baseUrl) + "/api/tags";
            string json = HttpGet(u, 15000);
            if (string.IsNullOrEmpty(json)) return names;
            object parsed = MiniJson.Parse(json);
            Dictionary<string, object> data = parsed as Dictionary<string, object>;
            if (data == null || !data.ContainsKey("models")) return names;
            List<object> models = data["models"] as List<object>;
            if (models == null) return names;
            foreach (object mo in models)
            {
                Dictionary<string, object> m = mo as Dictionary<string, object>;
                if (m == null) continue;
                string n = MiniJson.GetStr(m, "name");
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
            names.Sort();
            return names;
        }

        /// <summary>水印时间解析：兼容 "2026年09月10日 星期四 16:07:47" 与 "2026-09-10 16:07:47"。</summary>
        private static readonly Regex _osdRe = new Regex(
            @"(?<y>20\d{2})\s*[-/年]\s*(?<m>\d{1,2})\s*[-/月]\s*(?<d>\d{1,2})\s*[日号]?"
          + @"(\s*星期[一二三四五六日天])?\s*(?<hh>\d{1,2}):(?<mm>\d{2})(?::(?<ss>\d{2}))?");

        /// <summary>
        /// 用水印时间来校准模型读出的年份。
        ///
        /// 为什么需要：实测 qwen2.5vl:7b 对同一素材的 10 帧水印全部读成 "2025年"，而放大截图人工确认
        /// 真实值是 "2026年" —— 误差是系统性的一位数字误读（6→5），不是随机噪声。
        /// 因此"多次采样投票"没用（10 票全投给错误答案），必须用外部锚点判定。
        ///
        /// 做法：月/日/时分秒模型读得准且自洽，只有年份可疑。拿锚点时间（文件用修改时间、
        /// 实时流用系统时间）比较"锚点年份±1"三个候选，取最接近锚点的那一个；
        /// 仅在候选与模型读数相差恰好 1 年（典型的单数字误读）且录像在锚点一周内时才改写。
        /// </summary>
        public static string CalibrateOsdYear(string osd, DateTime anchor)
        {
            if (string.IsNullOrEmpty(osd)) return osd;
            Match m = _osdRe.Match(osd);
            if (!m.Success) return osd;

            int y, mo, d, hh, mi, ss;
            if (!int.TryParse(m.Groups["y"].Value, out y)) return osd;
            if (!int.TryParse(m.Groups["m"].Value, out mo)) return osd;
            if (!int.TryParse(m.Groups["d"].Value, out d)) return osd;
            if (!int.TryParse(m.Groups["hh"].Value, out hh)) return osd;
            if (!int.TryParse(m.Groups["mm"].Value, out mi)) return osd;
            ss = m.Groups["ss"].Success ? int.Parse(m.Groups["ss"].Value) : 0;

            int bestYear = y;
            double bestDiff = double.MaxValue;
            for (int dy = -1; dy <= 1; dy++)
            {
                int cand = anchor.Year + dy;
                try
                {
                    DateTime dt = new DateTime(cand, mo, d, hh, mi, ss);
                    double diff = Math.Abs((dt - anchor).TotalSeconds);
                    if (diff < bestDiff) { bestDiff = diff; bestYear = cand; }
                }
                catch (ArgumentOutOfRangeException) { /* 非法日期，跳过该候选 */ }
            }

            if (bestYear == y) return osd;                  // 模型读数已是最贴近锚点的
            if (Math.Abs(bestYear - y) > 1) return osd;      // 差超过 1 年，不像单数字误读，保留原值
            if (bestDiff > 7 * 86400) return osd;            // 距锚点超过一周，不强行校准

            // ★ 降成 Debug，而且**只记一次**（2026-09-13 修）
            //
            //   用户贴日志问"这是啥情况"：这一行**刷了 40 条** ✗
            //   原因：每抽一帧就调一次这个方法 ✓ 40 帧 = 40 条 ✓
            //   而校准结果每次都一样（2025 → 2026）✓ 记 40 遍毫无意义 ✗
            //
            //   ★ 而且它是 **INFO 级** ✗ —— 用户日常看的就是 INFO ✓
            //     真正的"排障细节"不该占 INFO ✓
            //   ★ 用静态标记保证"同一个校准只记一次" ✓
            if (!_yearFixLogged)
            {
                _yearFixLogged = true;
                Log.Debug("水印年份校准：" + y + " → " + bestYear + "（模型读数 " + osd.Trim()
                    + "；锚点时间 " + anchor.ToString("yyyy-MM-dd HH:mm:ss") + "）");
            }
            return osd.Substring(0, m.Groups["y"].Index) + bestYear.ToString()
                 + osd.Substring(m.Groups["y"].Index + m.Groups["y"].Length);
        }

        /// <summary>「水印年份校准」这条日志本进程内只记一次（2026-09-13 加）。</summary>
        private static bool _yearFixLogged = false;

        /// <summary>用 ffmpeg 抽一帧为 JPEG（输出到临时文件，避免管道阻塞）。
        /// isCancelled 非空时每 100ms 检查一次，用户点「停止」可立即杀掉 ffmpeg。</summary>
        public static byte[] ExtractFrameJpeg(string path, double timeSec, int maxWidth = 960, Func<bool> isCancelled = null)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "vc_mage_" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                List<string> cmd = new List<string>();
                cmd.Add("-v"); cmd.Add("error");
                // 网络流显式 TCP，避免 ffmpeg 默认走 UDP 时丢包导致抽帧失败
                if (Ffmpeg.IsStreamUrl(path)) { cmd.Add("-rtsp_transport"); cmd.Add("tcp"); }
                // -strict unofficial（2026-09-12 加）：
                //   ffmpeg 9.0 的 MJPEG 编码器遇到"非全范围 YUV"会直接拒绝初始化 ✗
                //   报错是「Non full-range YUV is non-standard, set strict_std_compliance
                //   to at most unofficial」—— 不少监控录像就是这个色域 ✓
                //   加上这个开关无副作用，但能把这整类抽帧失败挡掉 ✓
                cmd.Add("-strict"); cmd.Add("unofficial");
                cmd.Add("-ss"); cmd.Add(timeSec.ToString("0.###", CultureInfo.InvariantCulture));
                cmd.Add("-i"); cmd.Add(path);
                cmd.Add("-frames:v"); cmd.Add("1");
                cmd.Add("-vf"); cmd.Add("scale='min(" + maxWidth.ToString() + ",iw)':-2");
                // ★★ JPEG 质量：3 → 6（2026-09-13 优化 Ollama 调用）
                //
                //   ffmpeg 的 -q:v 是**反的** ✗ 范围 2~31，**数字越小质量越高** ✓
                //     q:v 2 = 最高 ✓  q:v 3 = 接近无损 ✓  q:v 6 = 高 ✓
                //   原来用 3 ✗ 1280 宽的监控画面出来 300~600 KB ✓
                //   实测送出去的 base64 **平均 360 KB、最大 822 KB**（=616 KB 原图）✗
                //
                //   而 Ollama 那边：显存 7.3/8.0 GB 已经塞满 ✓
                //   大图一进来就换页/换出模型 ✓ 日志里 **26% 的调用超过 20 秒** ✗
                //   （最大 34.7 秒 ✓ 而中位数才 2.2 秒 ✓ 差 15 倍 ✓）
                //
                //   → 对"有没有人 / 有没有车 / 有没有翻越"这种判断 ✓
                //     1280 宽下 q:v 3 和 q:v 6 **肉眼分不出** ✓ 而体积差 3~4 倍 ✓
                cmd.Add("-q:v"); cmd.Add("6");
                cmd.Add("-y");
                cmd.Add(tmp);

                // 抓单帧的超时**必须短**（2026-09-12 改）：
                //   原来是 120 秒 ✗ —— 摄像头卡住时会占着 RTSP 会话整整 2 分钟 ✓
                //   而 RTSP 会话是**摄像头端的稀缺资源** ✓ 便宜的 IPC 很容易被拖垮 ✗
                //   （用户问"这么频繁拉流会不会把摄像头拉崩"，这是代码里该改的一处 ✓）
                //   一帧正常 1~3 秒就够 ✓ 给 15 秒已经很宽容 ✓
                //   超时后 RunSimple 会 Kill 掉 ffmpeg，连接随之断开 ✓
                int tout = Ffmpeg.IsStreamUrl(path) ? 20000 : 120000;   // ★ 15→20 秒：便宜的 IPC 建立会话+抽帧可能要十几秒
                RunSimple(cmd, tout, isCancelled);
                if (!File.Exists(tmp))
                {
                    // ★ 把失败原因说清楚（2026-09-13 加）
                    //   原来不管什么原因都只是"返回空数组" ✗ 上层只能说"抓帧失败" ✓
                    //   用户根本不知道是密码错、账号被锁、还是网络不通 ✓
                    //   实测最有价值的是这一条：**401 = 认证失败** ✓
                    //   而且很多摄像头连续认证失败会**锁号** ✓ 必须让用户知道别再试了 ✓
                    string err = _lastStderr == null ? "" : _lastStderr;
                    if (err.IndexOf("401", StringComparison.Ordinal) >= 0
                        || err.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // 状态栏位置有限 ✗ 主体要短到能完整显示 ✓
                        // 完整建议放日志里 ✓（用户能去日志页看 ✓）
                        Log.Warn("抓帧被拒（401）：账号或密码不对，或账号已被临时锁号。"
                               + "连续认证失败会触发锁定，需等 10~30 分钟，或把摄像头断电重启。");
                        throw new FfmpegError("认证失败(401)：密码错或已被锁号，需等待或重启摄像头");
                    }
                    if (err.IndexOf("Connection refused", StringComparison.OrdinalIgnoreCase) >= 0
                        || err.IndexOf("No route", StringComparison.OrdinalIgnoreCase) >= 0
                        || err.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new FfmpegError("连不上摄像头：检查 IP 和网络");
                    if (err.IndexOf("404", StringComparison.Ordinal) >= 0)
                        throw new FfmpegError("路径不存在(404)：用「网络搜索」自动取地址");
                    // 其它原因（比如就是没抓到帧）→ 返回空，让上层按"抓帧失败"处理 ✓
                    return new byte[0];
                }
                return File.ReadAllBytes(tmp);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { }
            }
        }

        /// <summary>
        /// 一次 ffmpeg 调用抽一小段**连续帧**（关键帧连续播放用）。
        ///
        /// 为什么必须批量（2026-09-12 实测数据）：
        ///   逐帧抽（一帧起一个 ffmpeg 进程）：1280px → 212ms/帧，480px → 191ms/帧
        ///     · 缩小分辨率几乎没用 —— 成本在「进程启动 + 定位」这个固定开销上 ✗
        ///     · 折算上限只有 4~5 帧/秒，画面必然一卡一卡 ✓
        ///   批量抽（一次进程出 20 帧）：477ms / 20 帧 = **23.9ms/帧** ✓
        ///     · 快 8 倍，折算可达 40 帧/秒 ✓
        ///   所以播放改成：后台一次抽一小段，前台按时间从内存里取帧显示 ✓
        /// </summary>
        public static List<byte[]> ExtractFrameBatch(string path, double startSec, double spanSec, double fps, int maxWidth) { return ExtractFrameBatch(path, startSec, spanSec, fps, maxWidth, SeekMarginSec); }
        /// <summary>定位余量（秒）：先退这么多做输入定位，再用输出定位精确前进。</summary>
        public const double SeekMarginSec = 2.0;

        /// <summary>批次里"字节长度完全相同"的帧是否过多（说明解码失败在重复输出同一帧）。</summary>
        private static bool HasTooManyDuplicates(string[] files)
        {
            if (files == null || files.Length < 10) return false;
            Array.Sort(files, StringComparer.Ordinal);
            int dup = 0, n = 0;
            long prev = -1;
            for (int i = 0; i < files.Length; i += 3)      // 抽样即可，快
            {
                long len;
                try { len = new FileInfo(files[i]).Length; } catch (Exception) { continue; }
                if (prev >= 0 && len == prev) dup++;
                prev = len; n++;
            }
            return n >= 5 && dup * 100 / n > 40;           // 抽样里 40% 以上雷同 = 有问题
        }

        private static List<byte[]> ExtractFrameBatch(string path, double startSec, double spanSec, double fps, int maxWidth, double margin)
        {
            List<byte[]> list = new List<byte[]>();
            if (string.IsNullOrEmpty(path) || spanSec <= 0 || fps <= 0) return list;
            string dir = Path.Combine(Path.GetTempPath(), "vc_bat_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                List<string> cmd = new List<string>();
                cmd.Add("-v"); cmd.Add("error");
                cmd.Add("-strict"); cmd.Add("unofficial");
                if (Ffmpeg.IsStreamUrl(path)) { cmd.Add("-rtsp_transport"); cmd.Add("tcp"); }
                // ★ 两段式定位（2026-09-12 修的**真凶**）：
                //
                //   原来只有一段输入定位（-ss 放在 -i 之前）✗
                //   实测在这个 H.265 监控文件上会**落在 GOP 中间、参考帧缺失** ✓
                //   ffmpeg 报大量「Could not find ref with POC / Error constructing the frame RPS」✗
                //   解不出新帧时它会**反复输出同一帧** ✓
                //   → 抽出来的 75 帧里只有 33 帧不同（42 帧重复）✗
                //   → 播放时画面冻住、过一会儿突然动一下 —— 用户说的"卡一下又动起来" ✓✓
                //
                //   实测对照（目标 3 秒 = 75 帧）：
                //     单段输入定位        446ms   只有 33/75 帧不同 ✗
                //     纯输出定位        24447ms   75/75 但太慢 ✗
                //     两段式（-2 秒）      446ms   75/75 ✓✓   ← 用这个
                //
                //   做法：先快速定位到目标前 2 秒（输入定位，快）
                //         再从那里精确前进 2 秒（输出定位，准）
                //   两者结合既快又准 ✓
                double mg = margin;
                if (startSec - mg < 0) mg = startSec;
                cmd.Add("-ss"); cmd.Add((startSec - mg).ToString("0.###", CultureInfo.InvariantCulture));
                cmd.Add("-i"); cmd.Add(path);
                if (mg > 0.05)
                {
                    cmd.Add("-ss"); cmd.Add(mg.ToString("0.###", CultureInfo.InvariantCulture));
                }
                cmd.Add("-t"); cmd.Add(spanSec.ToString("0.###", CultureInfo.InvariantCulture));
                // fps 滤镜按固定节奏重采样，保证「一帧 = 1/fps 秒」的均匀关系
                cmd.Add("-vf"); cmd.Add("fps=" + fps.ToString("0.###", CultureInfo.InvariantCulture)
                    + ",scale='min(" + maxWidth.ToString() + ",iw)':-2");
                // ★★ JPEG 质量：3 → 6（2026-09-13 优化 Ollama 调用）
                //
                //   ffmpeg 的 -q:v 是**反的** ✗ 范围 2~31，**数字越小质量越高** ✓
                //     q:v 2 = 最高 ✓  q:v 3 = 接近无损 ✓  q:v 6 = 高 ✓
                //   原来用 3 ✗ 1280 宽的监控画面出来 300~600 KB ✓
                //   实测送出去的 base64 **平均 360 KB、最大 822 KB**（=616 KB 原图）✗
                //
                //   而 Ollama 那边：显存 7.3/8.0 GB 已经塞满 ✓
                //   大图一进来就换页/换出模型 ✓ 日志里 **26% 的调用超过 20 秒** ✗
                //   （最大 34.7 秒 ✓ 而中位数才 2.2 秒 ✓ 差 15 倍 ✓）
                //
                //   → 对"有没有人 / 有没有车 / 有没有翻越"这种判断 ✓
                //     1280 宽下 q:v 3 和 q:v 6 **肉眼分不出** ✓ 而体积差 3~4 倍 ✓
                cmd.Add("-q:v"); cmd.Add("6");   // 质量 3：比 4 清晰一档，体积只大一点
                cmd.Add("-y");
                cmd.Add(Path.Combine(dir, "f%04d.jpg"));

                RunSimple(cmd, 90000, null);

                // 结果校验：如果大量帧**字节长度完全相同**，说明又踩到"反复输出同一帧"✗
                // 这时把定位余量加大重来一次 ✓ 保证交付出去的批次一定是逐帧不同的 ✓
                string[] files = Directory.GetFiles(dir, "f*.jpg");
                if (HasTooManyDuplicates(files) && margin < 8.0)
                {
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                    return ExtractFrameBatch(path, startSec, spanSec, fps, maxWidth, 6.0);
                }
                Array.Sort(files, StringComparer.Ordinal);   // f0001, f0002… 必须按序号，不然顺序乱
                foreach (string f in files)
                {
                    try { list.Add(File.ReadAllBytes(f)); }
                    catch (Exception) { }
                    if (list.Count >= 240) break;            // 上限：240 帧约 18MB，别把内存吃掉
                }
            }
            catch (Exception) { }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
            }
            return list;
        }

        /// <summary>运行 ffmpeg 命令（stderr 异步排空 + 超时强杀，绝不阻塞调用线程）。</summary>
        private static void RunSimple(List<string> cmd, int timeoutMs)
        {
            RunSimple(cmd, timeoutMs, null);
        }

        private static string _lastStderr;

        private static void RunSimple(List<string> cmd, int timeoutMs, Func<bool> isCancelled)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Ffmpeg.FfmpegBin();
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cmd.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArg(cmd[i]));
            }
            psi.Arguments = sb.ToString();
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                Ffmpeg.RegisterRunning(p);
                StringBuilder errBuf = new StringBuilder();   // try 外声明，finally 里要用
                try
                {
                    // ★ 必须挂 ErrorDataReceived 才收得到 stderr（2026-09-13 修）
                    //   原来只有 BeginErrorReadLine() ✗ 没有处理函数 ✓
                    //   那 stderr 读出来就被丢掉了 ✓ 于是失败时**完全不知道原因** ✓
                    //   用户只看到一句"抓帧失败"✗ 分不清是密码错、账号被锁、还是网络不通 ✓
                    p.ErrorDataReceived += delegate (object se, DataReceivedEventArgs ee)
                    {
                        if (ee.Data != null) errBuf.AppendLine(ee.Data);
                    };
                    p.BeginErrorReadLine();   // 异步排空 stderr，不阻塞
                    // 轮询等待：既能管超时，也能在用户点「停止」时立刻杀进程。
                    // 原实现用 WaitForExit(timeoutMs) 一次等到底，最多 120 秒都无法响应停止。
                    int waited = 0;
                    while (!p.WaitForExit(100))
                    {
                        waited += 100;
                        if (isCancelled != null && isCancelled())
                        {
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(3000); } catch { }
                            throw new StoppedException();
                        }
                        if (waited >= timeoutMs)
                        {
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(3000); } catch { }
                            // ★★ 文案要说清楚「是哪一步卡了」（2026-09-13 用户截图问"这是啥情况"）
                            //
                            //   原来的消息：「AI 抽帧超时（15s）：stream1」✗
                            //   用户的反应是"这是啥情况" ✓ —— 因为：
                            //     ① 写「AI 抽帧」会让人以为是**AI 那一侧**的问题 ✗
                            //        其实是**从摄像头抓一帧**这一步没抓到 ✓
                            //        AI 根本还没被调用 ✓
                            //     ② 没说怎么办 ✓
                            //     ③ 只给了 URL 最后一段（stream1）✗
                            //        看不出是哪台摄像头 ✓
                            //
                            //   ★ 实测场景（用户截图）：
                            //     一台便宜的 IPC，间隔 10 秒拉一次 RTSP
                            //     → **间歇性**抽不到（同一批里还有成功的）
                            //     → 那就是它忙不过来 ✓ 或者网络慢 ✓
                            //   ★ 修法：文案说清「怎么调」+ 超时从 15 提到 20 秒
                            //     （20 秒还是比原来的 120 秒短得多 ✓
                            //       不会长时间占着摄像头的 RTSP 会话 ✓）
                            throw new FfmpegError("抓不到画面（等了 " + timeoutMs / 1000 + " 秒没响应）："
                                + Path.GetFileName(pathOrCmd(cmd))
                                + " —— 摄像头忙或网络慢。把上面的「间隔」调大一点（比如 15~20 秒）再试。");
                        }
                    }
                }
                finally
                {
                    Ffmpeg.UnregisterRunning(p);
                    // 留下 ffmpeg 的报错原文，供上层给出"能看懂"的失败原因 ✓
                    try { _lastStderr = errBuf.ToString(); } catch (Exception) { }
                }
            }
        }

        private static string pathOrCmd(List<string> cmd)
        {
            for (int i = 0; i < cmd.Count - 1; i++)
                if (cmd[i] == "-i" && i + 1 < cmd.Count) return cmd[i + 1];
            return "";
        }

        private static string QuoteArg(string arg)
        {
            if (arg.Length == 0) return "\"\"";
            if (arg.IndexOfAny(new char[] { ' ', '"' }) < 0) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>调用 Ollama /api/chat 分析单帧，返回模型回答正文。</summary>
        public static string QueryOllama(string baseUrl, string model, byte[] imageJpeg, string question, int maxTokens)
        {
            if (maxTokens < 512) maxTokens = 512;
            string content, thinking, done;
            CallChat(baseUrl, model, imageJpeg, question, maxTokens, out content, out thinking, out done);

            // 思考型模型（qwen3-vl 等）先把推理写进 message.thinking，正文才写进 message.content。
            // 两种情况都要放大预算重试：
            //   ① 正文为空 —— 预算被推理吃光，答案还没开始生成；
            //   ② done_reason=length —— 输出被硬截断（表现为时间只剩一半、内容整段丢失）。
            bool empty = string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(thinking);
            bool truncated = (done == "length");
            if (empty || truncated)
            {
                int bigger = maxTokens * 3;
                Log.Debug("模型输出不完整（" + (empty ? "正文为空" : "被截断 done_reason=length")
                    + "，推理 " + thinking.Length + " 字），改用 " + bigger + " token 重试");
                CallChat(baseUrl, model, imageJpeg, question, bigger, out content, out thinking, out done);
            }
            if (string.IsNullOrEmpty(content))
                throw new Exception("模型没有返回正文（仅推理 " + (thinking == null ? 0 : thinking.Length)
                    + " 字）。可把「帧数」调小，或换一个非思考型的视觉模型。");
            if (done == "length")
                Log.Warn("模型输出在 " + maxTokens + " token 处仍被截断，结果可能不完整");
            return content.Trim();
        }

        /// <summary>发一次 /api/chat，取出正文、推理与结束原因。</summary>
        /// <summary>
        /// 让 Ollama 把模型在内存/显存里**留多久**。
        ///
        /// ★ 2026-09-13 加（优化 Ollama 调用）
        ///   Ollama 的默认值是 **5 分钟** ✗ —— 5 分钟不调用就把模型卸掉 ✓
        ///   下次调用要**重新加载几 GB** ✓ 实测耗时 30 秒以上 ✗
        ///
        ///   日志实测：调用间隔中位 10 秒 ✓ 但有 **3 次超过 5 分钟** ✗
        ///   而且**显存只剩 0.7 GB 余量** ✓ Ollama 在压力下会更早换出模型 ✓
        ///
        ///   设成 2 小时 ✓ 巡检期间模型一直常驻 ✓
        ///   代价：模型一直占着内存 ✗ —— 但**反复加载比一直占着贵得多** ✓
        ///   （加载一次读几 GB 磁盘 + 重新分配显存 ✓ 比常驻的开销大得多 ✓）
        /// </summary>
        private const string KeepAlive = "2h";

        private static void CallChat(string baseUrl, string model, byte[] imageJpeg, string question,
            int maxTokens, out string content, out string thinking, out string doneReason)
        {
            content = ""; thinking = ""; doneReason = "";
            string b64 = Convert.ToBase64String(imageJpeg);
            string payload = "{\"model\":\"" + JsonEsc(model) + "\",\"messages\":[{\"role\":\"user\",\"content\":\""
                + JsonEsc(question) + "\",\"images\":[\"" + b64 + "\"]}],\"stream\":false,\"options\":{\"temperature\":0,\"num_predict\":"
                + maxTokens.ToString() + ",\"num_ctx\":8192},\"keep_alive\":\"" + KeepAlive + "\"}";
            // num_ctx 必须显式给足：Ollama 默认 4096，而一张图本身就吃掉上千 token，
            // 加上思考型模型的推理开销，很容易把上下文撑爆导致输出被截断。
            Log.Debug("Ollama 请求：模型=" + model + " 图片=" + (imageJpeg.Length / 1024) + "KB(base64 " + (b64.Length / 1024)
                + "KB) num_predict=" + maxTokens + " num_ctx=8192");
            string url = NormalizeBase(baseUrl) + "/api/chat";
            string json;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            json = HttpPost(url, payload, 180000);
            sw.Stop();
            if (string.IsNullOrEmpty(json)) throw new Exception("Ollama 未返回内容");
            object parsed = MiniJson.Parse(json);
            Dictionary<string, object> data = parsed as Dictionary<string, object>;
            if (data == null) throw new Exception("Ollama 返回格式异常");
            Dictionary<string, object> msg = data.ContainsKey("message") ? data["message"] as Dictionary<string, object> : null;
            if (msg == null) throw new Exception("Ollama 返回格式异常：" + Truncate(json, 150));
            content = MiniJson.GetStr(msg, "content");
            thinking = MiniJson.GetStr(msg, "thinking");
            if (content == null) content = "";
            if (thinking == null) thinking = "";
            doneReason = MiniJson.GetStr(data, "done_reason");   // "stop"=正常结束，"length"=被 token 上限截断
            if (doneReason == null) doneReason = "";
            Log.Debug("Ollama 返回：耗时 " + sw.ElapsedMilliseconds + "ms，正文 " + content.Length
                + " 字，推理 " + thinking.Length + " 字，结束原因=" + doneReason
                + (content.Length > 0 ? "，正文首行=" + Truncate(content.Split('\n')[0], 70) : "（正文为空）"));
        }

        private static string JsonEsc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        private static string HttpPost(string url, string body, int timeoutMs)
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                _currentReq = req;                      // 登记，供 AbortCurrent() 中断
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream())
                    s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException we)
            {
                // 被 AbortCurrent() 打断 —— 归为「用户停止」，不是错误
                if (we.Status == WebExceptionStatus.RequestCanceled) throw new StoppedException();
                if (we.Response != null)
                {
                    try
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            string respBody = sr.ReadToEnd();
                            if (respBody.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                                throw new Exception("Ollama 里没有这个模型。请点「自动获取模型」，从列表里选一个带 vl 的视觉模型。原始返回："
                                    + Truncate(respBody, 160));
                            throw new Exception("AI 推理请求失败: " + Truncate(respBody, 200));
                        }
                    }
                    catch (Exception e2) { throw e2; }
                }
                throw new Exception("AI 推理请求失败: " + we.Message);
            }
            finally
            {
                if (ReferenceEquals(_currentReq, req)) _currentReq = null;
            }
        }

        /// <summary>「否定说法」正则：无信号故障 / 没有花屏 / 未发现冻结 等。命中即整段挖掉，
        /// 否则模型写「无信号故障」（意思是"没有信号故障"）会被关键词误判成故障。</summary>
        private static readonly Regex _negRe = new Regex(
            "(无|没有|未见|未发现|未出现|不存在|不是|并无|没)[\\u4e00-\\u9fa5]{0,4}?"
          + "(故障|问题|异常|毛病|情况|现象|黑屏|黑场|全黑|纯黑|无信号|花屏|雪花|冻结|卡住|马赛克|遮挡|偏色|模糊|无法识别画面|静止画面|黑画面|画面丢失)");

        /// <summary>解析模型回答，返回 (status, reason, content)。</summary>
        public static string[] Judge(string answer)
        {
            if (string.IsNullOrEmpty(answer))
                return new string[] { "SKIP", "模型未返回内容，无法判断", "" };
            string text = answer.Trim();
            string content = "";
            Match m = Regex.Match(text, "(?:内容|画面内容)[:：]\\s*(.+)");
            if (m.Success)
                content = m.Groups[1].Value.Trim().Trim('"', '\'', '“', '”');

            string scan = StripNegated(text);   // 挖掉否定说法后再做关键词匹配

            // 故障性严重词（命中判 FAIL）
            bool hasFault = false;
            foreach (string w in _severeWords)
                if (scan.IndexOf(w, StringComparison.Ordinal) >= 0) { hasFault = true; break; }
            // 环境/风格性昏暗描述（光线不足等，非故障）
            bool hasEnv = false;
            foreach (string w in _envWords)
                if (text.IndexOf(w, StringComparison.Ordinal) >= 0) { hasEnv = true; break; }
            // 结构化标记
            bool abnormalMark = Regex.IsMatch(text, "ABNORMAL", RegexOptions.IgnoreCase);
            bool normalMark = Regex.IsMatch(text, "(?<!AB)NORMAL", RegexOptions.IgnoreCase);
            // 自由文本信号（用具体问题词，不用裸“异常”，避免“无异常”误伤）
            bool abnormal = Regex.IsMatch(scan, "遮挡|偏色|模糊|花屏|冻结|卡住|雪花|黑屏|无信号|马赛克", RegexOptions.IgnoreCase);
            bool normal = Regex.IsMatch(text, "正常|画面清晰|内容正常|无异常|没有问题", RegexOptions.IgnoreCase);

            string reason = CleanReason(text);

            // ① 模型给了明确且不矛盾的结构化标记 —— 以标记为准，关键词只用来分严重程度。
            //    这样能避免"原因里提到了故障词"把 NORMAL 翻成 FAIL。
            if (normalMark && !abnormalMark)
                return new string[] { "PASS", reason, content };
            if (abnormalMark && !normalMark)
            {
                if (hasFault) return new string[] { "FAIL", reason, content };
                if (hasEnv) return new string[] { "PASS", reason + "（环境/风格性昏暗，非信号故障）", content };
                return new string[] { "WARN", reason, content };
            }

            // ② 没有结构化标记（或两个都有）—— 退回自由文本关键词判断
            if (hasFault) return new string[] { "FAIL", reason, content };
            if (normal && !abnormal) return new string[] { "PASS", reason, content };
            if (abnormal)
            {
                if (hasEnv) return new string[] { "PASS", reason + "（环境/风格性昏暗，非信号故障）", content };
                return new string[] { "WARN", reason, content };
            }
            if (hasEnv) return new string[] { "PASS", reason + "（环境/风格性昏暗，非信号故障）", content };
            return new string[] { "SKIP", reason, content };
        }

        /// <summary>反复挖掉"否定 + 故障词"的组合，直到不再变化（最多 6 轮防死循环）。</summary>
        private static string StripNegated(string text)
        {
            string cur = text;
            for (int i = 0; i < 6; i++)
            {
                string prev = cur;
                cur = _negRe.Replace(cur, "　");   // 用全角空格占位，避免把前后文字粘在一起
                if (cur == prev) break;
            }
            return cur;
        }

        /// <summary>把首行整理成简短原因：去掉模型爱加的「状态|原因：」「NORMAL|」这类前缀。</summary>
        private static string CleanReason(string text)
        {
            string reason = text.Split('\n')[0].Trim();
            reason = Regex.Replace(reason, "^\\s*状态\\s*[|｜]\\s*原因\\s*[:：]\\s*", "");
            reason = Regex.Replace(reason, "^(NORMAL|ABNORMAL)\\s*[|｜]\\s*", "", RegexOptions.IgnoreCase);
            if (reason.Length > 120) reason = reason.Substring(0, 120);
            return reason;
        }

        /// <summary>场景变化点检测：只输出画面突变时刻（ffmpeg select scene + showinfo）。</summary>
        public static List<double> DetectScenes(string path, double threshold, int maxPoints, double? duration)
        {
            List<string> cmd = new List<string>();
            cmd.Add("-v"); cmd.Add("info");
            cmd.Add("-i"); cmd.Add(path);
            if (duration.HasValue && duration.Value > 0)
            {
                cmd.Add("-t");
                cmd.Add(duration.Value.ToString("0.###", CultureInfo.InvariantCulture));
            }
            string th = threshold.ToString("0.###", CultureInfo.InvariantCulture);
            // 注意：filter 参数内的逗号必须反斜杠转义（Windows 下同样适用）
            string vf = "select='gt(scene\\," + th + ")',showinfo";
            cmd.Add("-vf"); cmd.Add(vf);
            cmd.Add("-f"); cmd.Add("null");
            cmd.Add("-");

            string stderr;
            try { stderr = RunCapture(cmd); }
            catch (Exception) { return new List<double>(); }

            Regex re = new Regex("pts_time:([0-9]+(?:\\.[0-9]+)?)");
            List<double> times = new List<double>();
            foreach (Match m in re.Matches(stderr))
            {
                double t;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                {
                    if (!times.Contains(t)) times.Add(t);
                    if (times.Count >= maxPoints) break;
                }
            }
            return times;
        }

        /// <summary>从变化点中均匀取 n 个（Python _pick_frame_times 同款逻辑）。</summary>
        private static List<double> PickSceneTimes(List<double> sceneTimes, int n)
        {
            List<double> pts = new List<double>(sceneTimes);
            pts.Sort();
            if (pts.Count <= n) return pts;
            List<double> res = new List<double>();
            double step = (double)pts.Count / n;
            for (int i = 0; i < n; i++) res.Add(pts[(int)(i * step)]);
            return res;
        }

        /// <summary>时长内均匀取 n 个时间点。</summary>
        private static List<double> PickUniformTimes(double duration, int n)
        {
            List<double> times = new List<double>();
            if (duration <= 0 || duration <= n)
            {
                for (int i = 0; i < n; i++)
                    times.Add(duration > 0 ? (double)i * duration / n : (double)i);
            }
            else
            {
                for (int i = 0; i < n; i++)
                    times.Add(duration * (i + 1) / (n + 1));
            }
            return times;
        }

        /// <summary>全片分析：按视频总时长按需抽帧（默认每 interval 秒一帧，长视频自动稀释到上限帧数内）。</summary>
        public static List<double> PickFullTimes(double duration, double interval, int maxFrames)
        {
            List<double> res = new List<double>();
            if (duration <= 0) return res;
            double step = interval;
            if (step <= 0) step = 15;
            if (maxFrames < 1) maxFrames = 1;
            if (duration / step > maxFrames) step = duration / maxFrames;   // 长视频自动放宽间隔
            for (double t = 0.0; t < duration; t += step)
                res.Add(Math.Min(t, duration));
            if (res.Count == 0) res.Add(0);
            if (res[res.Count - 1] < duration - 0.5) res.Add(Math.Max(duration - 0.5, 0));   // 末尾补帧（留 0.5s 余量，避免在结尾抽帧失败）
            return res;
        }

        /// <summary>运行 ffmpeg 命令并捕获 stderr（供场景检测等使用）。</summary>
        private static string RunCapture(List<string> cmd)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Ffmpeg.FfmpegBin();
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cmd.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArg(cmd[i]));
            }
            psi.Arguments = sb.ToString();
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                Ffmpeg.RegisterRunning(p);
                try
                {
                    // 异步读，避免 ffmpeg 不退出时 ReadToEnd 永久阻塞
                    System.Threading.Tasks.Task<string> seTask = p.StandardError.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> soTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(600000))
                    {
                        try { p.Kill(); } catch { }
                        p.WaitForExit();
                    }
                    string err = "";
                    try { err = seTask.Result; } catch { }
                    return err;
                }
                finally
                {
                    Ffmpeg.UnregisterRunning(p);
                }
            }
        }

        /// <summary>对视频执行 AI 画面理解（抽帧策略 + 逐帧 Ollama 推理）。</summary>
        /// <param name="onFrame">每帧进度回调(当前帧, 总帧)，用于实时进度条。</param>
        /// <param name="isCancelled">取消判定回调（返回 true 则中断剩余帧）。</param>
        /// <param name="onStage">阶段提示回调（扫描/抽帧/推理等），用于状态栏。</param>
        public static void CheckMage(string path, Dictionary<string, object> probe, MageConfig cfg, double sampling, MediaReport rep,
            Action<int, int> onFrame = null, Func<bool> isCancelled = null, Action<string> onStage = null)
        {
            if (string.IsNullOrEmpty(cfg.Url))
            {
                rep.Add("AI 画面理解", "SKIP", "未填写 Ollama 地址，已跳过", null);
                return;
            }

            // 时长
            double duration = 0.0;
            if (probe != null && probe.ContainsKey("format"))
            {
                Dictionary<string, object> fmt = probe["format"] as Dictionary<string, object>;
                double? d = Ffmpeg.SafeF(fmt != null ? MiniJson.GetStr(fmt, "duration") : null);
                if (d.HasValue) duration = d.Value;
            }
            if (sampling > 0 && sampling < duration) duration = sampling;
            int n = Math.Max(1, cfg.Frames);

            Log.Info("AI 画面理解开始：" + Path.GetFileName(path) + "（模型 " + cfg.Model + "，帧数上限 " + n + "）");

            // ---- 抽帧策略：auto=智能(动静优先) / motion=仅动静 / uniform=均匀 / custom=自定义时间点 / full=全片分析 ----
            List<double> times;
            string strategyTxt = "均匀";
            string motionNote = "";
            if (cfg.Strategy == "full")
            {
                strategyTxt = "全片分析";
                times = PickFullTimes(duration, cfg.FullInterval, cfg.FullMaxFrames);
            }
            else if (cfg.Strategy == "custom")
            {
                strategyTxt = "自定义";
                times = new List<double>(cfg.CustomTimes);
                times.Sort();
                if (times.Count == 0)
                {
                    times = PickUniformTimes(duration, n);
                    motionNote = "（未设置有效时间点，已回退为均匀抽帧）";
                }
            }
            else if (cfg.Strategy == "auto" || cfg.Strategy == "motion")
            {
                strategyTxt = cfg.Strategy == "auto" ? "智能(动静优先)" : "仅动静";
                if (onStage != null) onStage("正在扫描画面变化点（大文件可能需要 1-3 分钟）...");
                List<double> scenes = DetectScenes(path, 0.2, 60, duration > 0 ? (double?)duration : null);
                Log.Debug("场景变化点数量：" + scenes.Count + (scenes.Count > 0 ? "，前几个=" + string.Join(",", scenes.ToArray(), 0, Math.Min(5, scenes.Count)) : ""));
                if (scenes != null && scenes.Count > 0)
                {
                    times = PickSceneTimes(scenes, n);
                }
                else
                {
                    times = PickUniformTimes(duration, n);
                    if (cfg.Strategy == "motion")
                        motionNote = "（未检测到画面变化，已回退为均匀抽帧）";
                }
            }
            else
            {
                strategyTxt = "均匀";
                times = PickUniformTimes(duration, n);
            }
            Log.Debug("抽帧策略=" + strategyTxt + " 抽帧时间点=[" + string.Join(",", times.ToArray()) + "]");

            List<string[]> frames = new List<string[]>(); // [status, reason, content]
            List<string> errors = new List<string>();
            for (int i = 0; i < times.Count; i++)
            {
                if (isCancelled != null && isCancelled())
                    break;   // 用户点了停止：中断剩余帧
                if (onFrame != null) onFrame(i + 1, times.Count);
                string[] fr = new string[] { "SKIP", "抽帧失败", "" };
                try
                {
                    if (onStage != null) onStage(string.Format("正在抽帧 第 {0}/{1} 帧（时间点 {2:0.#}s）...", i + 1, times.Count, times[i]));
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] jpg = ExtractFrameJpeg(path, times[i], 960, isCancelled);
                    sw.Stop();
                    Log.Debug(string.Format("抽帧第 {0}/{1} 帧 {2:0.#}s 耗时 {3}ms，大小 {4}KB", i + 1, times.Count, times[i], sw.ElapsedMilliseconds, jpg.Length / 1024));
                    if (jpg.Length == 0) throw new Exception("抽帧为空");
                    if (onStage != null) onStage(string.Format("正在 AI 分析 第 {0}/{1} 帧（时间点 {2:0.#}s）...", i + 1, times.Count, times[i]));
                    string answer = QueryOllama(cfg.Url, cfg.Model, jpg, cfg.Question, cfg.MaxTokens);
                    Log.Debug(string.Format("第 {0}/{1} 帧回答: {2}", i + 1, times.Count, Truncate(answer.Replace("\n", " "), 150)));
                    fr = Judge(answer);
                }
                catch (Exception ex)
                {
                    fr[1] = "推理失败: " + Truncate(ex.Message, 100);
                    if (errors.Count < 2) errors.Add(Truncate(ex.Message, 120));
                    Log.Debug(string.Format("第 {0}/{1} 帧推理失败: {2}", i + 1, times.Count, ex.Message));
                }
                frames.Add(fr);
            }

            int nFail = 0, nWarn = 0, nPass = 0, nSkip = 0;
            foreach (string[] fr in frames)
            {
                if (fr[0] == "FAIL") nFail++;
                else if (fr[0] == "WARN") nWarn++;
                else if (fr[0] == "PASS") nPass++;
                else nSkip++;
            }
            string overall;
            if (nFail > 0) overall = "FAIL";
            else if (nWarn > 0) overall = "WARN";
            else if (nPass > 0 && nSkip == 0) overall = "PASS";
            else if (nPass > 0 && nSkip > 0) overall = "WARN";
            else overall = "SKIP";

            List<string> parts = new List<string>();
            parts.Add(string.Format("抽帧策略:{0}{1} 分析 {2} 帧: 正常 {3} / 异常 {4} / 严重 {5} / 无法判断 {6}",
                strategyTxt, motionNote, frames.Count, nPass, nWarn, nFail, nSkip));
            if (cfg.Strategy == "custom" && times.Count > 0)
            {
                List<string> tp = new List<string>();
                foreach (double t in times) tp.Add(t.ToString("0.#") + "s");
                parts.Add("抽帧时间点: " + string.Join(", ", tp.ToArray()));
            }
            if (nFail > 0 || nWarn > 0)
            {
                List<string> bad = new List<string>();
                for (int i = 0; i < frames.Count; i++)
                    if (frames[i][0] == "FAIL" || frames[i][0] == "WARN")
                        bad.Add(string.Format("第{0}帧({1:0.0}s): {2}", i + 1, times[i], frames[i][1]));
                parts.Add(string.Join("；", bad.ToArray(), 0, Math.Min(6, bad.Count)));
            }
            List<string> descs = new List<string>();
            for (int i = 0; i < frames.Count; i++)
                if (frames[i][2].Length > 0)
                    descs.Add(string.Format("第{0}帧({1:0.0}s): {2}", i + 1, times[i], frames[i][2]));
            if (descs.Count > 0)
                parts.Add("画面内容摘要: " + string.Join("；", descs.ToArray(), 0, Math.Min(6, descs.Count)));
            if (errors.Count > 0)
                parts.Add("部分帧推理失败: " + string.Join("; ", errors.ToArray()));

            Log.Info("AI 画面理解完成：" + Path.GetFileName(path) + " → " + overall
                + "（分析 " + frames.Count + " 帧，正常 " + nPass + " / 异常 " + nWarn + " / 严重 " + nFail + " / 无法判断 " + nSkip + "）");

            // 把每帧结果存进报告（供异常时间轴标注）：[time, code] code: 0=PASS 1=WARN 2=FAIL 3=SKIP
            List<double[]> aiFrames = new List<double[]>();
            for (int i = 0; i < frames.Count; i++)
            {
                int code = 3;
                if (frames[i][0] == "PASS") code = 0;
                else if (frames[i][0] == "WARN") code = 1;
                else if (frames[i][0] == "FAIL") code = 2;
                aiFrames.Add(new double[] { times[i], code });
            }
            Dictionary<string, object> val = new Dictionary<string, object>();
            val["ai_frames"] = aiFrames;
            rep.Add("AI 画面理解", overall, string.Join("。", parts.ToArray()), val);
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
