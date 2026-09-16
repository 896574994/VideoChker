/* -*- coding: utf-8 -*-
 * Diagnostics.cs — 把"排障要用的东西"收集成一个文件，交给用户发回来
 *
 * ★★ 为什么要有它（2026-09-14，用户直接提的需求）
 *
 *   用户原话：
 *     ① 「我就是你的 debug 日志不够详细吧，现在我连带数据回来给你分析都做不到」
 *     ② 「你在这个程序里面的日志功能中的 debug 搞一个能够收集到你需要的报错日志
 *         信息的工具或代码，然后我才好弄报错的信息回来给你分析」
 *
 *   ★ 这两句说的是**同一件事的产品缺陷**：
 *     排障需要的信息**散在好几处** ✓
 *       · 系统/.NET/有没有 UCRT        → 只在脑子里问过，没写下来 ✗
 *       · ffmpeg/ffprobe 起不起得来     → 退出码一闪而过，日志里可能没有 ✗
 *       · 日志文件                      → 在 logs\ 里，用户得先找到它 ✗
 *       · 当时的设置                    → 在 data\settings.ini 里 ✗
 *     **而用户不是开发者** ✗ 他不知道该给我什么 ✓
 *     我问他"把报错发我"✗ 他只能回一句"它说无法解析媒体"✓
 *
 *   → 所以做成**一个按钮 / 一条命令**，产出**一个文件** ✓
 *     用户只要会"发送文件"就能把现场带回来 ✓
 *
 * ★ 三条设计原则
 *   ① **一个文件**：微信/QQ 直接发 ✓ 不要给一个目录让他自己挑 ✓
 *   ② **只写事实**：不知道就写"(读不到)"✗ 不猜 ✓
 *   ③ **先脱敏再落盘**：密码一律 `***` ✓
 *      （日志出口本来就有脱敏 ✓ 这里再来一道是防"设置摘要"这类新加的来源 ✓）
 *
 * ★ 用 C# 5 语法（不能用 string 插值、不能用 LINQ 方法链）✓
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    public static class Diagnostics
    {
        /// <summary>诊断文件最多带多少行日志（再长就截断，只留**最后**这些行）。</summary>
        private const int MaxLogLines = 4000;

        /// <summary>
        /// 产出诊断文件，返回写好的路径。
        ///
        /// 默认放在 exe 同目录（桌面上找得到的地方通常更好找，
        /// 但 exe 目录用户是知道的 —— 界面上会直接把路径显示出来 ✓）。
        /// </summary>
        public static string ExportBundle()
        {
            string name = "诊断_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            string path = Path.Combine(AppPaths.BaseDir, name);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("视频核对工具 —— 诊断报告（请把这个文件整个发回给开发者）");
            sb.AppendLine("================================================================");
            sb.AppendLine("★ 这个文件里**没有密码**（日志和设置都做过脱敏 ✓）可直接发送。");
            sb.AppendLine("★ 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("★ 程序版本：" + AppInfo.Title + " " + AppInfo.Version + "（" + AppInfo.BuildDate + "）");
            sb.AppendLine();

            AppendEnvironment(sb);
            AppendLogErrors(sb);
            AppendLogTail(sb);
            AppendNetTrace(sb);
            AppendSettingsDigest(sb);
            AppendFileList(sb);

            sb.AppendLine();
            sb.AppendLine("=============== 到这儿就完了 ===============");
            sb.AppendLine("把这个文件（" + name + "）整个发给开发者即可。");
            sb.AppendLine("如果不方便发文件，也可以只把上面「二、错误与警告摘录」那一段复制过去。");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Log.Info("诊断报告已导出：" + path);
            return path;
        }

        // ───────────────────────── 一、环境 ─────────────────────────

        /// <summary>环境报告（`--diag` 用的也是这一份 —— 一个来源 ✓）。</summary>
        public static string BuildReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("视频核对工具 —— 环境诊断报告");
            sb.AppendLine("================================");
            sb.AppendLine("程序版本 : " + AppInfo.Title + " " + AppInfo.Version + "（" + AppInfo.BuildDate + "）");
            sb.AppendLine("生成时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("程序位置 : " + AppPaths.BaseDir);
            sb.AppendLine();
            AppendEnvironment(sb);
            return sb.ToString();
        }

        private static void AppendEnvironment(StringBuilder sb)
        {
            sb.AppendLine("一、这台机器和运行库");
            sb.AppendLine("--------------------------------");
            sb.AppendLine("系统      : " + SafeStr(delegate () { return Environment.OSVersion.ToString(); }));
            sb.AppendLine("            （6.1.7601 = Windows 7 ｜ 6.2/6.3 = Win8/8.1 ｜ 10.0 = Win10/11）");
            sb.AppendLine("            ★ 注意：.NET 4.0 的程序没带清单文件时，在 Win10/11 上也会**报成 6.2** ✓");
            sb.AppendLine("              所以看到 6.2 不一定是 Win8 ✓ 以「关于本机」里写的为准 ✓");
            sb.AppendLine("64 位系统 : " + (Environment.Is64BitOperatingSystem ? "是" : "否"));
            sb.AppendLine("进程位数  : " + (Environment.Is64BitProcess ? "64" : "32"));
            sb.AppendLine("CPU 核数  : " + Environment.ProcessorCount);
            sb.AppendLine(".NET      : " + SafeStr(delegate ()
            {
                return typeof(object).Assembly.GetName().Version == null
                    ? "(读不到)" : typeof(object).Assembly.GetName().Version.ToString();
            }));
            sb.AppendLine("系统目录  : " + SafeStr(delegate () { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); }));
            sb.AppendLine();
            sb.AppendLine("ucrtbase.dll（通用 C 运行库 UCRT 的本体 —— ★这一项才是判据）:");
            string win = SafeStr(delegate () { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); });
            AppendFileInfo(sb, "  ", Path.Combine(win, "System32", "ucrtbase.dll"));
            AppendFileInfo(sb, "  ", Path.Combine(win, "SysWOW64", "ucrtbase.dll"));
            sb.AppendLine("  ★ 随包的 ffmpeg/ffprobe 导入的是 msvcrt.dll，**不依赖 UCRT** ✓");
            sb.AppendLine("    （2026-09-15 读导入表核实：21 个依赖 DLL 里没有一个 api-ms-win-crt-*）");
            sb.AppendLine("    → 这一项在不在，都**不影响本程序** ✓ 列出来只为排查别的问题 ✓");
            sb.AppendLine("  ★ 下面这个 api-ms-*.dll 在 Win10/11 上**本来就查不到文件**");
            sb.AppendLine("    （它是加载器内部解析的「API 集」名字）✓ 它在不在都**不能说明问题** ✓");
            sb.AppendLine("api-ms-win-crt-runtime-l1-1-0.dll（参考项，不代表结论）:");
            AppendFileInfo(sb, "  ", Path.Combine(win, "System32", "api-ms-win-crt-runtime-l1-1-0.dll"));
            sb.AppendLine();

            sb.AppendLine("ffmpeg / ffprobe（★ 真的把它们跑了一遍，看退出码）");
            sb.AppendLine("--------------------------------");
            AppendBinaryCheck(sb, "ffmpeg", Ffmpeg.FfmpegBin());
            AppendBinaryCheck(sb, "ffprobe", Ffmpeg.FfprobeBin());
            sb.AppendLine("★ 退出码对照：0xC0000135 = 找不到依赖的 DLL   0xC0000005 = 访问冲突");
            sb.AppendLine("              0xC000007B = 32/64 位不匹配    0xC0000142 = DLL 初始化失败");
            sb.AppendLine("★ Win7 上「0xC0000005 + 输出全空」= 这份 ffmpeg 太新 ✗");
            sb.AppendLine("  （2024-05-31 之后的构建在 Win7 上就是这个死法，与运行库无关）");
            sb.AppendLine("  v10.19 起随包的是 **2022-06-29** 构建 ✓ 它不该再出现这个码 ✓");
            sb.AppendLine();
        }

        private static void AppendFileInfo(StringBuilder sb, string indent, string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    FileInfo fi = new FileInfo(path);
                    sb.AppendLine(indent + "在 ✓  " + (fi.Length / 1024) + " KB  日期 " + fi.LastWriteTime.ToString("yyyy-MM-dd")
                        + "  " + path);
                }
                else
                {
                    sb.AppendLine(indent + "**不在** ✗  " + path);
                }
            }
            catch (Exception ex) { sb.AppendLine(indent + "读不到（" + ex.Message + "）  " + path); }
        }

        private static void AppendBinaryCheck(StringBuilder sb, string name, string path)
        {
            bool bare = (path == null) || (path.IndexOf('\\') < 0 && path.IndexOf('/') < 0);
            sb.AppendLine(name + " 路径 : " + path
                + (bare ? "   ★ 这是「裸名字」—— 同目录没找到，改为让系统去 PATH 里找 ✓" : ""));
            AppendFileInfo(sb, "  ", path);
            try
            {
                string[] v = new string[] { "-version" };
                ProcessResult r = Ffmpeg.RunForDiag(path, v);
                sb.AppendLine("  退出码 : " + r.Code + "   十六进制 " + ((uint)r.Code).ToString("X8")
                    + "   " + Ffmpeg.ExplainExitCode(r.Code));
                string so = r.Stdout == null ? "" : r.Stdout.Trim();
                string se = r.Stderr == null ? "" : r.Stderr.Trim();
                sb.AppendLine("  标准输出: " + (so.Length == 0 ? "(空)" : so.Length + " 字"));
                if (so.Length > 0) sb.AppendLine("    " + Trim(so, 400));
                sb.AppendLine("  标准错误: " + (se.Length == 0 ? "(空)" : se.Length + " 字"));
                if (se.Length > 0) sb.AppendLine("    " + Trim(se, 600));
            }
            catch (Exception ex)
            {
                sb.AppendLine("  ★ 连「启动」都没成功：" + ex.Message);
            }
        }

        // ───────────────────── 二、错误与警告摘录 ─────────────────────

        /// <summary>
        /// 把日志里的 ERROR / WARN 挑出来单独列一段。
        ///
        /// ★ 为什么要单独摘：完整日志可能有几千行 ✓
        ///   开发者第一眼想看的就是**出错的那几行** ✓
        ///   摘出来 → 双方都省事 ✓（而且用户"只复制一段"时也不会复制错东西 ✓）
        /// </summary>
        private static void AppendLogErrors(StringBuilder sb)
        {
            sb.AppendLine("二、错误与警告摘录（完整日志在下面第三节）");
            sb.AppendLine("--------------------------------");
            List<string> all = ReadLogLines();
            if (all.Count == 0)
            {
                sb.AppendLine("（读不到日志文件，或者今天还没有日志）");
                sb.AppendLine();
                return;
            }
            int nErr = 0, nWarn = 0;
            List<string> errs = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                string ln = all[i];
                if (ln.IndexOf("[ERROR]") >= 0) { nErr++; errs.Add(ln); }
                else if (ln.IndexOf("[WARN]") >= 0) { nWarn++; if (errs.Count < 400) errs.Add(ln); }
            }
            sb.AppendLine("统计：ERROR " + nErr + " 行 ｜ WARN " + nWarn + " 行（日志共 " + all.Count + " 行）");
            if (errs.Count == 0)
            {
                sb.AppendLine("★ 没有 ERROR / WARN —— 那问题可能在别处（比如「功能没反应」而不是「报错」）✓");
                sb.AppendLine("  下面第三节里有完整日志，照样发过来就有用 ✓");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("★ 下面是**最后 " + errs.Count + " 条** ERROR/WARN（越靠后越新）：");
                // 只留最后 400 条，避免文件太大
                int from = errs.Count > 400 ? errs.Count - 400 : 0;
                for (int i = from; i < errs.Count; i++) sb.AppendLine("  " + errs[i]);
                if (from > 0) sb.AppendLine("  （前面还有 " + from + " 条，见第三节完整日志）");
            }
            // ★ 未处理异常（error.log）—— 2026-09-16 加
            //   为什么必须一起带上：崩溃原来**只**写 logs\error.log ✗
            //   而上面这段"错误摘录"读的是 video_checker_*.log ✗
            //   → 用户 2026-09-16 发回来的诊断包里写着「ERROR 0 行」✗
            //     而他几分钟前**刚崩过一次**（Win7 上点停止）✓
            //   —— 一处异常要两个地方都写、诊断包两处都收，
            //      才不会出现"报告说没事、其实刚崩过" ✗
            AppendUnhandledErrors(sb);

            sb.AppendLine();
        }

        /// <summary>把 logs\error.log（未处理异常）也带进诊断包。</summary>
        private static void AppendUnhandledErrors(StringBuilder sb)
        {
            try
            {
                string f = Path.Combine(AppPaths.LogsDir, "error.log");
                if (!File.Exists(f)) return;
                string[] lines = File.ReadAllLines(f, Encoding.UTF8);
                sb.AppendLine();
                sb.AppendLine("★ 未处理异常（logs\\error.log 共 " + lines.Length + " 行，这里带最后 200 行）：");
                int from = lines.Length > 200 ? lines.Length - 200 : 0;
                if (from > 0) sb.AppendLine("  （前面还有 " + from + " 行）");
                for (int i = from; i < lines.Length; i++) sb.AppendLine("  " + Mask(lines[i]));
            }
            catch (Exception ex) { sb.AppendLine("  （读 error.log 失败：" + ex.Message + "）"); }
        }

        // ────────────────────── 三、完整日志 ──────────────────────

        private static void AppendLogTail(StringBuilder sb)
        {
            sb.AppendLine("三、完整日志（含 [DEBUG] —— 排障要看的细节在这里）");
            sb.AppendLine("--------------------------------");
            List<string> all = ReadLogLines();
            if (all.Count == 0)
            {
                sb.AppendLine("（读不到日志文件）");
                sb.AppendLine();
                return;
            }
            sb.AppendLine("日志文件：" + Log.FilePath);
            sb.AppendLine("日志目录：" + AppPaths.LogsDir);
            int from = all.Count > MaxLogLines ? all.Count - MaxLogLines : 0;
            if (from > 0)
                sb.AppendLine("★ 共 " + all.Count + " 行，为了文件不至于太大，这里只带**最后 " + MaxLogLines + " 行**：");
            // 完整日志**脱敏再过一遍**（日志出口已经脱敏了 ✓ 这里是双保险 ✓）
            for (int i = from; i < all.Count; i++) sb.AppendLine("  " + Mask(all[i]));
            sb.AppendLine();
        }

        private static List<string> ReadLogLines()
        {
            List<string> r = new List<string>();
            try
            {
                // ★ 今天的日志优先，没有就用**最近的那一份**（2026-09-14）
                //   用户多半是"昨天出问题、今天才想起来导出"✗
                //   而 Log.FilePath 只指向今天 ✓ 今天还没记过东西 → 文件还不存在 ✗
                //   → 报告会写"读不到日志" ✓ 而出问题的日志就在旁边 ✗ 那就白做了
                //   见下面 PickLogFile() ✓
                string f = PickLogFile();
                if (f == null || f.Length == 0 || !File.Exists(f)) return r;
                string[] lines = File.ReadAllLines(f, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++) r.Add(lines[i]);
            }
            catch (Exception) { }
            return r;
        }

        /// <summary>
        /// 挑一个日志文件：**今天的优先，没有就用最近的那一份** ✓
        ///
        /// ★ 为什么要"最近的那一份"（2026-09-14 想通的）：
        ///   用户多半是"**昨天出了问题，今天才想起来导出**"✗
        ///   而 `Log.FilePath` 只指向**今天**那个文件 ✓
        ///   今天要是还没记过东西 → 那个文件还不存在 ✗
        ///   → 报告里就会写"读不到日志" ✓ 而出问题的日志**明明就在旁边** ✗✗
        ///   那就白做了 ✓ 所以按"最后修改时间"挑最近的一个 ✓
        /// </summary>
        public static string PickLogFile()
        {
            try
            {
                string today = Log.FilePath;          // 会顺带把 logs 目录建出来
                if (today != null && today.Length > 0 && File.Exists(today)) return today;
                string dir = AppPaths.LogsDir;
                if (dir == null || dir.Length == 0 || !Directory.Exists(dir)) return today;
                string best = null;
                DateTime bestT = DateTime.MinValue;
                string[] files = Directory.GetFiles(dir, "video_checker_*.log");
                for (int i = 0; i < files.Length; i++)
                {
                    DateTime t;
                    try { t = File.GetLastWriteTime(files[i]); } catch (Exception) { continue; }
                    if (t > bestT) { bestT = t; best = files[i]; }
                }
                return best != null ? best : today;
            }
            catch (Exception) { return ""; }
        }
        // ───────────────────── 报文（抓包）─────────────────────

        /// <summary>
        /// 把协议报文（抓包）也带上。
        ///
        /// ★ 为什么（2026-09-15 用户提的：「可以搞个 .txt 的 pacp 抓包报文 bug 反馈」）
        ///   前面几次排障我拿到的都是"结论"（连不上 / 没收到 / 失败了）✗
        ///   而真正能定位的是**双方到底发了什么、收回了什么** ✓
        ///   → 所以报文单独一节，跟着诊断包一起走 ✓
        ///
        /// ★ 太长就只带**最后一部分**：排障看的是"出问题前后那几下" ✓
        ///   前面的几十次正常往返没有价值 ✗ 还会把文件撑大 ✓
        /// </summary>
        private static void AppendNetTrace(StringBuilder sb)
        {
            sb.AppendLine("四、协议报文（抓包文本 —— 摄像头推了什么、我们发了什么）");
            sb.AppendLine("--------------------------------");
            try
            {
                if (!NetTrace.Enabled)
                {
                    sb.AppendLine("（报文记录**被关掉了** —— 在「日志」窗口里把「记录报文」勾上，"
                        + "复现一次问题再导出 ✓）");
                    sb.AppendLine();
                    return;
                }
                string f = NetTrace.FilePath;
                if (f == null || f.Length == 0 || !File.Exists(f))
                {
                    sb.AppendLine("（还没有报文文件 —— 说明这段时间没有网络交互，"
                        + "或者问题不在这台机器上）");
                    sb.AppendLine("报文文件位置：" + (f == null ? "(空)" : f));
                    sb.AppendLine();
                    return;
                }
                string[] lines = File.ReadAllLines(f, Encoding.UTF8);
                sb.AppendLine("报文文件：" + f);
                sb.AppendLine("共 " + lines.Length + " 行");
                int from = lines.Length > MaxTraceLines ? lines.Length - MaxTraceLines : 0;
                if (from > 0)
                    sb.AppendLine("★ 只带**最后 " + MaxTraceLines + " 行**（前面的是正常往返，用不上）");
                for (int i = from; i < lines.Length; i++) sb.AppendLine("  " + Mask(lines[i]));
            }
            catch (Exception ex) { sb.AppendLine("（读报文失败：" + ex.Message + "）"); }
            sb.AppendLine();
        }

        /// <summary>诊断包里最多带多少行报文（够看出问题前后那几下就行）。</summary>
        private const int MaxTraceLines = 1500;
        // ────────────────────── 四、设置摘要 ──────────────────────

        private static void AppendSettingsDigest(StringBuilder sb)
        {
            sb.AppendLine("五、当前设置（已脱敏）");
            sb.AppendLine("--------------------------------");
            sb.AppendLine("AI 地址   : " + Mask(AppSettings.OllamaUrl));
            sb.AppendLine("AI 模型   : " + AppSettings.OllamaModel);
            sb.AppendLine("抽帧数    : " + AppSettings.MageFrames);
            sb.AppendLine("抽帧策略  : " + AppSettings.StrategyToText(AppSettings.MageStrategy));
            sb.AppendLine("本机地址  : " + (AppSettings.UploadIp == null || AppSettings.UploadIp.Length == 0
                ? "自动（推荐）" : AppSettings.UploadIp));
            sb.AppendLine();
            sb.AppendLine("设置文件（原样附在下面，密码已打码）：");
            try
            {
                string ini = AppPaths.Data("settings.ini");
                if (File.Exists(ini))
                {
                    string[] lines = File.ReadAllLines(ini, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++) sb.AppendLine("  " + Mask(lines[i]));
                }
                else sb.AppendLine("  （没有 settings.ini）");
            }
            catch (Exception ex) { sb.AppendLine("  （读不到：" + ex.Message + "）"); }
            sb.AppendLine();
        }

        // ────────────────────── 五、文件清单 ──────────────────────

        private static void AppendFileList(StringBuilder sb)
        {
            sb.AppendLine("六、程序目录里有什么（帮开发者判断「是不是缺文件」）");
            sb.AppendLine("--------------------------------");
            try
            {
                string dir = AppPaths.BaseDir;
                sb.AppendLine("目录：" + dir);
                string[] names = Directory.GetFileSystemEntries(dir);
                Array.Sort(names);
                int shown = 0;
                for (int i = 0; i < names.Length && shown < 40; i++)
                {
                    string nm = Path.GetFileName(names[i]);
                    // 报告/日志/归档目录里东西多，只写个数
                    if (Directory.Exists(names[i]))
                    {
                        int cnt = 0;
                        try { cnt = Directory.GetFileSystemEntries(names[i]).Length; } catch (Exception) { }
                        sb.AppendLine("  [目录] " + nm + "  （" + cnt + " 项）");
                    }
                    else
                    {
                        long sz = 0;
                        try { sz = new FileInfo(names[i]).Length; } catch (Exception) { }
                        sb.AppendLine("  " + nm + "  " + (sz / 1024) + " KB");
                    }
                    shown++;
                }
                if (names.Length > shown) sb.AppendLine("  …（还有 " + (names.Length - shown) + " 项）");
            }
            catch (Exception ex) { sb.AppendLine("  （列不出来：" + ex.Message + "）"); }
            sb.AppendLine();
        }

        // ────────────────────── 小工具 ──────────────────────

        /// <summary>
        /// 脱敏：`scheme://user:密码@host` 里的密码换成 ***，`password=` 之类的也换掉。
        ///
        /// ★ 为什么这里还要再来一道（日志出口 Logger.Sanitize 已经做过 ✓）：
        ///   这一份报告还会带上 **settings.ini 原文** 和别的来源 ✓
        ///   那些**没走过日志出口** ✗ 所以在这里统一兜一次 ✓
        ///   ——"一个出口"是最理想的 ✓ 但"多一道网"也不亏 ✓ 尤其是要发给别人的文件 ✓
        /// </summary>
        public static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            try
            {
                s = Regex.Replace(s,
                    @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<user>[^:/@\s]+):(?<pass>[^@/\s]*)@",
                    "${scheme}${user}:***@");
                s = Regex.Replace(s,
                    @"(?<k>(?i)(password|passwd|pwd|token|secret|apikey|api_key)\s*[=:]\s*)(?<v>[^\s;，,]+)",
                    "${k}***");
                return s;
            }
            catch (Exception) { return s; }
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f(); } catch (Exception) { return "(读不到)"; }
        }

        private static string Trim(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "…（后面还有 " + (s.Length - max) + " 字）";
        }
    }
}
