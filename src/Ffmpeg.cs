/* -*- coding: utf-8 -*-
 * Ffmpeg.cs — ffmpeg/ffprobe 封装与媒体分析（.NET Framework 4.x，零第三方依赖）
 * 与 Python 版 checker/ffmpeg.py 判定等价。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VideoChecker
{
    /// <summary>ffmpeg/ffprobe 调用失败或媒体无法解析。</summary>
    public class FfmpegError : Exception
    {
        public FfmpegError(string message) : base(message) { }
    }

    /// <summary>ffmpeg 工具封装：命令执行 + 滤镜输出解析。</summary>
    public static class Ffmpeg
    {
        // ---- 二进制解析 ----
        private static string _ffmpegPath;
        private static string _ffprobePath;

        public static string FfmpegBin()
        {
            if (_ffmpegPath == null) _ffmpegPath = ResolveBinary("ffmpeg");
            return _ffmpegPath;
        }

        public static string FfprobeBin()
        {
            if (_ffprobePath == null) _ffprobePath = ResolveBinary("ffprobe");
            return _ffprobePath;
        }

        private static string ResolveBinary(string name)
        {
            // 1) exe 同目录 ffmpeg\bin\name.exe
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string cand = Path.Combine(Path.Combine(exeDir, "ffmpeg"), Path.Combine("bin", name + ".exe"));
            if (File.Exists(cand)) return cand;
            // 2) exe 同目录 name.exe
            cand = Path.Combine(exeDir, name + ".exe");
            if (File.Exists(cand)) return cand;
            // 3) PATH
            return name;
        }

        // ---- 进程执行 ----
        private static string _lastErrorOutput = "";

        public static string LastErrorOutput { get { return _lastErrorOutput; } }

        /// <summary>
        /// ffmpeg/ffprobe **起不来**时，说清楚到底是什么原因。
        ///
        /// ★★ 为什么要有它（2026-09-14，用户在公司的 Win7 上实测踩到）：
        ///
        ///   现象：**每一个文件都是 0.1 秒就报 ERROR（无法分析）** ✗
        ///     08:38:15 文件完成 → ERROR（耗时 0.1s）
        ///     08:38:26 文件完成 → ERROR（耗时 0.1s）   ← 换个目录也一样
        ///     08:39:18 文件完成 → ERROR（耗时 0.1s）
        ///   而同一份程序在别的电脑上完全正常 ✓
        ///
        ///   ★ 根因：**这台 Win7 没有「通用 C 运行库」（UCRT）** ✗
        ///     随程序带的 ffmpeg.exe / ffprobe.exe 依赖：
        ///       api-ms-win-crt-runtime-l1-1-0.dll
        ///       api-ms-win-crt-stdio-l1-1-0.dll
        ///     这两个是 **Windows 10 才自带**的 ✓
        ///     Win7 **必须另外装 KB2999226**（Update for Universal C Runtime）✓
        ///     不装的话：ffmpeg.exe **根本起不来** ✗
        ///     → Process.Start 立刻抛 Win32Exception ✓
        ///     → 0.1 秒就"检测完"了 ✓ 每个文件都一样 ✓
        ///
        ///   ✗ **而原来的提示是误导人的**：
        ///     「未找到 ffmpeg/ffprobe，请确认程序目录下有 ffmpeg\bin\ 文件夹」
        ///     —— 文件夹明明在 ✓ 文件明明在 ✓ 用户照着这句话查什么都查不出来 ✓
        ///     （这正是这个项目反复踩的同一个坑：**报错没说清是哪一步卡了**）
        ///
        ///   → 所以这里**先看文件在不在，再看系统缺不缺运行库** ✓
        ///     两种情况给两套完全不同的话 ✓ 而且**给出能照做的动作** ✓
        /// </summary>
        private static string ExplainStartFailure(string bin)
        {
            // ★ 参数是**真的那个路径** ✗ 不是一个名字（2026-09-14 我自己的探针抓到的 bug）
            //   第一版收的是 "ffmpeg/ffprobe" 这种名字 ✓ 然后用
            //     which.IndexOf("ffprobe") >= 0 ? FfprobeBin() : FfmpegBin()
            //   去猜是哪个 ✗ —— 字符串 "ffmpeg/ffprobe" **同时含有两个名字** ✓
            //   于是它永远猜成 ffprobe ✗ 报出"找不到 ffprobe"，而真正起不来的是 ffmpeg ✓
            //   （实测：目录里只有一个坏的 ffmpeg.exe ✓ 报的却是 ffprobe ✓）
            //   ★ 教训：**要报"哪一个"，就别传一个模棱两可的名字** ✓ 直接传那个路径 ✓
            string which = "ffmpeg";
            try { which = Path.GetFileNameWithoutExtension(bin); } catch (Exception) { }
            if (which == null || which.Length == 0) which = "ffmpeg";
            bool exists = false;
            try { exists = File.Exists(bin); } catch (Exception) { }

            if (!exists)
            {
                return "找不到 " + which + "（应该在程序目录的 ffmpeg\\bin\\ 下面）。"
                    + "现在找的位置是：" + bin + "。"
                    + "如果这一份是从别处拷来的，请确认 ffmpeg 文件夹一起拷过来了。";
            }

            // 文件在 → 那就是**起不来**。
            //
            // ★★ 这一整段原来判的是"缺 UCRT → 让用户去装 KB2999226" ✗（2026-09-14 写的）
            //   2026-09-15 把随包二进制的**导入表**读出来之后，那个结论被推翻了 ✗
            //     · 随包的这份（2022-06-29，n5.0.1）：21 个依赖 DLL，
            //       用的是 **msvcrt.dll** ✓ **一个 api-ms-win-crt-* 都不导入** ✓
            //       → 它根本不需要额外装任何运行库 ✓
            //     · 而那台真出问题的 Win7，UCRT 是**装着的**（ucrtbase.dll 在，961KB）✗
            //       病根是那份 ffmpeg 太新（静态导入了 Win8 才有的
            //       api-ms-win-core-synch-l1-2-0.dll / WaitOnAddress）✗ 与运行库无关 ✓
            //   → 所以这里不再劝人装 KB2999226 ✓ 那句话会把人往错路上带 ✗
            //     （真实代价：用户照着装了，问题当然没好，白折腾一轮 ✗）
            //
            //   ★ 判据：**"缺运行库"要由二进制的导入表说话** ✓ 不是凭记忆猜 ✓
            string ucrtNote = "\n（参考：这台机器 ucrtbase.dll "
                + (HasUcrt() ? "在" : "不在")
                + " —— 但随包的 ffmpeg 用的是 msvcrt.dll，本来就不需要 UCRT）";

            if (IsWin7())
            {
                return which + " 起不来（文件在：" + bin + "）。"
                    + "\n\n★ 这台是 Windows 7。随包的这份是 **2022-06-29** 的构建 ✓"
                    + "只依赖 Win7 自带的 DLL ✓ **不需要装任何运行库** ✓"
                    + "\n  如果它还是起不来，多半是这份 exe 被换过、或者被拷坏了 ✗"
                    + "\n  请点「日志 → 导出诊断包」发回来 ✓（里面有退出码和完整现场）"
                    + ucrtNote;
            }

            return which + " 起不来（文件在：" + bin + "）。"
                + "\n常见原因：被杀毒软件拦了，或者文件在拷贝/传输过程中损坏了。"
                + "\n请点「日志 → 导出诊断包」发回来 ✓"
                + "\n也可以自己跑一句看退出码：\"" + bin + "\" -version"
                + ucrtNote;
        }

        /// <summary>
        /// 这台机器有没有「通用 C 运行库」。
        ///
        /// 判据用 ucrtbase.dll ✓ —— 它是 UCRT 的本体 ✓
        /// （api-ms-win-crt-*.dll 那一堆是**转发用的壳** ✓ 可能被别的软件单独拷进来 ✓
        ///   只看壳会误判成"有" ✗ 看 ucrtbase.dll 才准 ✓）
        ///
        /// 两个目录都要看 ✗：
        ///   64 位系统里 32 位组件在 SysWOW64 ✓ 64 位组件在 System32 ✓
        ///   只看一个目录会把"装了"看成"没装" ✓
        /// </summary>
        /// <summary>这台是不是 Windows 7（6.1）。用于把"新版 ffmpeg 在 Win7 上崩"说清楚。</summary>
        private static bool IsWin7()
        {
            try
            {
                Version v = Environment.OSVersion.Version;
                return (v.Major == 6 && v.Minor == 1);
            }
            catch (Exception) { return false; }
        }

        private static bool HasUcrt()
        {
            try
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (win.Length == 0) return true;          // 查不了就别乱报，宁可不说
                string[] dirs = new string[] {
                    Path.Combine(win, "System32"),
                    Path.Combine(win, "SysWOW64")
                };
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dirs[i], "ucrtbase.dll"))) return true;
                    }
                    catch (Exception) { }
                }
                return false;
            }
            catch (Exception) { return true; }             // 判断失败就别诬告
        }

        /// <summary>
        /// 自检：**真的把 ffmpeg 跑一次**，跑不起来就给出人话。
        ///
        /// ★ 为什么不能只看"文件在不在"（2026-09-14 加）：
        ///   那台 Win7 上 ffmpeg.exe **文件是在的** ✗ 只是起不来 ✓
        ///   「在不在」和「跑不跑得起来」是两件事 ✗
        ///   → 唯一的判据就是**真的跑一次** ✓（这也是这份项目第 1 条铁律）
        ///
        /// 返回空串 = 没问题 ✓ 否则返回要显示给用户的话 ✓
        /// </summary>
        /// <summary>给诊断报告用：跑一次子进程并把结果原样返回。</summary>
        public static ProcessResult RunForDiag(string bin, string[] args)
        {
            return Run(bin, args, 15, true);
        }

        public static string PreflightCheck()
        {
            try
            {
                string bin = FfmpegBin();
                string[] vargs = new string[] { "-version" };
                ProcessResult r = Run(bin, vargs, 15, true);
                if (r != null && r.Code == 0) return "";
                // ★ 退出码要翻译成人话（2026-09-14）
                //   用户那台 Win7 就是这种情况：**进程起得来，但立刻就死了** ✗
                //   退出码是 0xC0000135（找不到 DLL）✓ 而输出是空的 ✓
                //   原来的 PreflightCheck 只会说"ffmpeg 能起来但报了错（退出码 -1073741515）"✗
                //   那个负数谁都认不出是什么 ✓ → 现在用 ExplainExitCode 直接给结论 ✓
                return DescribeFailure(bin, vargs, r, 15, "(自检：跑一次 -version)");
            }
            catch (FfmpegError e)
            {
                return e.Message;                       // 这里已经是人话了（见 ExplainStartFailure）
            }
            catch (Exception e)
            {
                return "ffmpeg 自检失败：" + e.Message;
            }
        }

        // ---- 运行中进程跟踪（供「停止」按钮中断 ffmpeg）----
        private static volatile Process _running;

        public static void RegisterRunning(Process p)
        {
            if (p != null) _running = p;
        }

        public static void UnregisterRunning(Process p)
        {
            if (p != null && ReferenceEquals(_running, p)) _running = null;
        }

        /// <summary>强制结束当前正在运行的 ffmpeg/ffprobe 进程（停止检测用）。</summary>
        public static void KillRunning()
        {
            Process p = _running;
            if (p != null)
            {
                try { p.Kill(); }
                catch { }
            }
        }

        public static ProcessResult Run(string file, string[] args, int timeoutSec, bool captureStdout)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = file;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = captureStdout;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArg(args[i]));
            }
            psi.Arguments = sb.ToString();

            try
            {
                using (Process p = Process.Start(psi))
                {
                    RegisterRunning(p);
                    try
                    {
                        string stderr = "";
                        string stdout = "";
                        if (captureStdout)
                        {
                            p.BeginOutputReadLine();
                            p.BeginErrorReadLine();
                            StringBuilder so = new StringBuilder();
                            StringBuilder se = new StringBuilder();
                            p.OutputDataReceived += (s, e) => { if (e.Data != null) so.AppendLine(e.Data); };
                            p.ErrorDataReceived += (s, e) => { if (e.Data != null) se.AppendLine(e.Data); };
                            if (!p.WaitForExit(timeoutSec * 1000))
                            {
                                try { p.Kill(); } catch { }
                                throw new FfmpegError("命令执行超时：" + Path.GetFileName(file));
                            }
                            p.WaitForExit();
                            stdout = so.ToString();
                            stderr = se.ToString();
                        }
                        else
                        {
                            // 同步读避免死锁：先读 stderr
                            stderr = p.StandardError.ReadToEnd();
                            if (!p.WaitForExit(timeoutSec * 1000))
                            {
                                try { p.Kill(); } catch { }
                                throw new FfmpegError("命令执行超时：" + Path.GetFileName(file));
                            }
                        }
                        _lastErrorOutput = stderr;
                        return new ProcessResult(p.ExitCode, stdout, stderr);
                    }
                    finally
                    {
                        UnregisterRunning(p);
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new FfmpegError(ExplainStartFailure(file));
            }
        }

        private static string QuoteArg(string arg)
        {
            if (arg.Length == 0) return "\"\"";
            if (arg.IndexOfAny(new char[] { ' ', '"' }) < 0) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        // ---- 探测 ----
        public static Dictionary<string, object> ProbeMedia(string path, int timeoutSec = 120)
        {
            return ProbeMedia(path, timeoutSec, null);
        }

        public static Dictionary<string, object> ProbeMedia(string path, int timeoutSec, string[] inputOptions)
        {
            if (!File.Exists(path) && !IsStreamUrl(path))
                throw new FfmpegError("路径不存在: " + path);
            List<string> args = new List<string>();
            args.Add("-v"); args.Add("error");
            if (inputOptions != null)
                for (int i = 0; i < inputOptions.Length; i++) args.Add(inputOptions[i]);
            args.Add("-show_format"); args.Add("-show_streams");
            args.Add("-of"); args.Add("json");
            args.Add(path);
            ProcessResult r = Run(FfprobeBin(), args.ToArray(), timeoutSec, true);
            if (r.Code != 0)
            {
                // ★★ 失败要**说清楚到能被人拿去分析**（2026-09-14，用户为这个专门提了意见）
                //
                //   用户原话：**「我就是你的 debug 日志不够详细吧，
                //               现在我连带数据回来给你分析都做不到」**
                //   ★ 他说得对 ✗ 原来的报错只有这一句：
                //       "ffprobe 无法解析媒体: <文件路径>\n" + stderr 前 500 字
                //     如果 ffprobe **根本没输出**（启动就死了）✗ stderr 是空的 ✓
                //     那这句话里**一个可分析的字段都没有** ✗
                //     既不知道跑的是哪个 exe ✓ 也不知道退出码 ✓ 更不知道它有没有输出 ✓
                //     → 用户只能回一句"它说无法解析媒体" ✓ 而这句话等于没说 ✓
                //
                //   → 现在给一份**完整的现场**：路径 / 命令行 / 退出码(十进制+十六进制) /
                //     标准输出 / 标准错误 / 退出码的人话解释 ✓✓
                //     而且**同时写进日志** ✓ —— 用户可以直接把日志发过来 ✓
                string detail = DescribeFailure(FfprobeBin(), args.ToArray(), r, timeoutSec, path);
                Log.Error("ffprobe 失败 —— " + detail.Replace("\n", " | "));
                throw new FfmpegError(detail);
            }
            object parsed;
            try { parsed = MiniJson.Parse(r.Stdout); }
            catch { throw new FfmpegError("ffprobe 输出解析失败: " + path); }
            Dictionary<string, object> data = parsed as Dictionary<string, object>;
            if (data == null) throw new FfmpegError("ffprobe 输出解析失败: " + path);
            List<object> streams = data.ContainsKey("streams") ? data["streams"] as List<object> : null;
            if (streams == null || streams.Count == 0)
                throw new FfmpegError("媒体中未发现任何流: " + path);
            return data;
        }

        /// <summary>
        /// 把退出码翻译成人话。
        ///
        /// ★ 为什么必须给十六进制：Windows 的**加载期失败**全是 0xC0000xxx 那一段 ✓
        ///   十进制 -1073741515 谁都认不出来 ✗ 写成 0xC0000135 一眼就知道是"找不到 DLL" ✓
        ///   （2026-09-14 加 —— 用户那台 Win7 报的就是这一类，只是原来的日志里没记退出码 ✓）
        /// </summary>
        public static string ExplainExitCode(int code)
        {
            switch (code)
            {
                case -1073741515:      // 0xC0000135
                    // ★ 原文是"缺 UCRT，Win7 装 KB2999226" ✗（v10.9 的结论）
                    //   读导入表之后确认：随包的 ffmpeg/ffprobe 用 msvcrt.dll，
                    //   **不导入任何 api-ms-win-crt-*** ✓ 所以这条正常**不该出现** ✓
                    //   —— 出现了就说明 ffmpeg\bin\ 里的东西不是随包的那份 ✗
                    return "0xC0000135 = **找不到依赖的 DLL**。"
                        + "随包的 ffmpeg/ffprobe 只依赖 Windows 自带的老组件"
                        + "（msvcrt.dll 等 ✓ 已核对导入表），**不需要另外装运行库** ✓"
                        + " → 出现这一条，基本说明 ffmpeg\\bin\\ 里的 exe 被换过或拷坏了 ✗"
                        + "\n    怎么办：重新解压 / 重新安装一次程序包 ✓"
                        + " 仍不行就点「日志 → 导出诊断包」发我分析";
                case -1073741701:      // 0xC000007B
                    return "0xC000007B = 映像格式无效（**32 位/64 位不匹配**，或混进了错误的 DLL 版本）";
                case -1073741502:      // 0xC0000142
                    return "0xC0000142 = DLL 初始化失败（通常是运行库装得不全或被杀软拦了）";
                case -1073741819:      // 0xC0000005
                    // ★★ 这一条是 2026-09-15 用真报告定位出来的（用户在 Win7 上）✗
                    //
                    //   报告里两个二进制都是：退出码 C0000005、标准输出/错误全空 ✓
                    //   而 UCRT **装着**（ucrtbase.dll 和 api-ms-win-crt-*.dll 都在）✗
                    //   → 那就不是"缺运行库" ✓ 是**这个 ffmpeg 本身在 Win7 上跑不了** ✓
                    //
                    //   ★ 实测（读 exe 的导入表）：它用到这些 **Win8/Win10 才有**的函数
                    //       SetThreadDescription        ← Win10 1607+
                    //       WaitOnAddress / WakeByAddressSingle   ← Win8+
                    //       SetDefaultDllDirectories / AddDllDirectory ← Win8+
                    //       GetSystemTimePreciseAsFileTime        ← Win8+
                    //     Win7 上这些**不存在** ✓ 动态取到 NULL 再调用 → **访问冲突** ✓
                    //   而 PE 头里声明的子系统版本是 5.2 ✗ —— 那个字段**不能信** ✓
                    //
                    //   ★ 这是**上游已知问题**：2024-05-31 之后的 ffmpeg 构建在 Win7 上
                    //     就是这个 0xc0000005 ✗（见 BtbN/FFmpeg-Builds issue #386）
                    //     报告里那个是 **2026-08-04** 的构建 ✓ 正好在之后 ✓
                    //
                    //   → 所以这条要说清楚"怎么办"，而不是只说"程序崩了" ✓
                    if (IsWin7())
                        return "0xC0000005 = 访问冲突。★ 这台是 **Windows 7**，"
                            + "而这份 ffmpeg 是**较新的构建**（它们从 2024-05-31 起"
                            + "就不再支持 Win7 了 ✓ 上游已知问题）→ 它在 Win7 上一启动就崩 ✓"
                            + " 与运行库无关（UCRT 在不在都一样）✗"
                            + "\n    ★ 如果你装的是 v10.19 及以后的包：随包的已经是"
                            + " **2022-06-29** 那份 ✓（21 个依赖全是 Win7 自带的 DLL ✓）"
                            + "\n      → 还报这个，说明 ffmpeg\\bin\\ 里那只 exe 不是随包的那份 ✗"
                            + "\n    → 重新安装一次程序包；仍不行就点「日志 → 导出诊断包」发我 ✓";
                    return "0xC0000005 = 访问冲突（程序崩了）";
                case -1073741795:      // 0xC000001D
                    return "0xC000001D = 执行了非法指令（**CPU 太老**，跑不了这份 ffmpeg）";
                case -1073741676:      // 0xC0000094
                    return "0xC0000094 = 除零";
                case -1073741571:      // 0xC00000FD
                    return "0xC00000FD = 栈溢出";
                case -1073740791:      // 0xC0000409
                    return "0xC0000409 = 快速失败（程序自己发现状态不对主动退出）";
                case -1:
                    return "0xFFFFFFFF = 未知错误（很多程序用它表示「参数不对」）";
                case 1:
                    return "1 = 普通的错误退出（一般是**输入文件有问题**，不是环境问题）";
            }
            if (code == 0) return "0 = 正常结束";
            return "（这个码不在已知列表里）";
        }

        /// <summary>
        /// 一次子进程失败的**完整现场**。给人看，也给人**发出去**（用户要能带回来分析）。
        ///
        /// ★ 设计要点：**不知道的字段也要写出来** ✗
        ///   比如 stderr 是空的 ✓ 就写"(空)" ✓ 而不是省掉 ✓
        ///   —— "空"本身就是一条重要信息（说明它还没来得及说话就死了）✓
        ///   （省掉的话，看的人分不清是"没有"还是"没记"✗）
        /// </summary>
        public static string DescribeFailure(string bin, string[] args, ProcessResult r, int timeoutSec, string input)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("ffprobe/ffmpeg 执行失败 —— 下面是完整现场，可直接发给开发者：");
            sb.AppendLine("  输入    : " + input);
            sb.AppendLine("  程序    : " + bin);
            try
            {
                if (File.Exists(bin))
                {
                    FileInfo fi = new FileInfo(bin);
                    sb.AppendLine("  文件    : 在 ✓  " + fi.Length + " 字节  改写时间 " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                }
                else
                {
                    sb.AppendLine("  文件    : **不在** ✗（要找的就是上面那个路径）");
                }
            }
            catch (Exception ex) { sb.AppendLine("  文件    : 读不到（" + ex.Message + "）"); }

            sb.AppendLine("  命令行  : \"" + bin + "\" " + string.Join(" ", args));
            sb.AppendLine("  超时    : " + timeoutSec + " 秒");

            int code = (r == null) ? 0 : r.Code;
            sb.AppendLine("  退出码  : " + code + "   十六进制 " + ((uint)code).ToString("X8")
                + "   " + ExplainExitCode(code));

            string so = (r == null || r.Stdout == null) ? "" : r.Stdout;
            string se = (r == null || r.Stderr == null) ? "" : r.Stderr;
            sb.AppendLine("  标准输出: " + (so.Length == 0 ? "(空)" : so.Length + " 字"));
            if (so.Length > 0) sb.AppendLine("            " + Trim(so, 300).Replace("\n", "\n            "));
            sb.AppendLine("  标准错误: " + (se.Length == 0 ? "(空)" : se.Length + " 字"));
            if (se.Length > 0) sb.AppendLine("            " + Trim(se, 800).Replace("\n", "\n            "));
            if (so.Length == 0 && se.Length == 0)
                sb.AppendLine("  ★ 两个输出都是空的 + 退出码是 0xC0000xxx → 它**还没开始干活就死了** ✓"
                    + " 基本可以确定是**缺 DLL / 运行库**（见上面退出码那一行）✓");

            sb.AppendLine("  这台机器: " + SafeOsText() + " / .NET " + SafeFxText()
                + " / UCRT " + (HasUcrt() ? "有" : "**没有**"));
            return sb.ToString();
        }

        private static string SafeOsText()
        {
            try { return Environment.OSVersion.ToString(); } catch (Exception) { return "(读不到)"; }
        }

        private static string SafeFxText()
        {
            try
            {
                object v = typeof(object).Assembly.GetName().Version;
                return v == null ? "(读不到)" : v.ToString();
            }
            catch (Exception) { return "(读不到)"; }
        }

        public static bool IsStreamUrl(string path)
        {
            return path != null && (path.StartsWith("rtsp://") || path.StartsWith("rtmp://")
                || path.StartsWith("http://") || path.StartsWith("https://") || path.StartsWith("udp://"));
        }

        // ---- 错误行过滤（与 Python 版一致）----
        private static Regex _errorRe = new Regex("(?i)error|corrupt|invalid data|failed to|unable to|decode|nul packet", RegexOptions.IgnoreCase);
        private static string[] _errSkip = new string[] {
            "parsed_", "configuration:", "input #", "output #",
            "press [q]", "libav", "built with",
            "silencedetect", "blackdetect", "freezedetect", "volumedetect",
            "framecrc"
        };

        public static List<string> ExtractErrorLines(string stderrText)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(stderrText)) return lines;
            foreach (string raw in stderrText.Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.Length == 0) continue;
                string lower = ln.ToLowerInvariant();
                if (_errorRe.IsMatch(ln) && !ContainsAny(lower, _errSkip))
                    lines.Add(ln);
            }
            return lines;
        }

        private static bool ContainsAny(string text, string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                if (text.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ---- 滤镜分析命令 ----
        // 视频分析滤镜链（黑帧 + 冻结帧 + showinfo 逐帧 pts，供解码错误时间定位）
        // ============================ 性能注记（请勿为提速移除 showinfo）============================
        // 实测环境：2688x1520 @25fps 监控源，120 秒切片，i5-12400F。
        //   showinfo 是唯一能逐帧提供时间戳的来源，负责把解码错误定位到具体时间点（报告里的花屏段时间轴）。
        //   它同时是本链最大的单项开销 —— 含它 12.99 秒；去掉后 8.64 秒（快 33%），stderr 从 1.17MB 降到 5KB。
        //   586 秒、248MB 的整片实测：62.5 秒 → 42.6 秒。
        //   但去掉它的代价是花屏位置标注彻底丢失，且进度行 time= 兜底并不可靠：
        //     进度行按"墙钟"每约 0.5 秒才打印一次，而 80 秒的视频解码仅需约 1 秒，几乎不产生进度行，
        //     因此不只是小文件，中等长度文件同样定位不出（实测：可定位的错误时间点数 79 → 0）。
        //   -stats_period 也无法可靠替代：设为 0.05 时 20 段塌缩成 4 段；设为 0.01 时能定位但整体偏移约 1 秒。
        //   它输出的行含有 "[Parsed_showinfo"，会被 ExtractErrorLines 的 parsed_ 规则排除，
        //   所以它对"解码错误条数"这一判定依据没有贡献 —— 它的价值仅在于时间定位，而这个价值不可替代。
        //   若确实需要提速，应解决解码本身（如硬件解码 -hwaccel），而不是砍掉时间基准。
        // ================================================================================
        public const string VF_CHAIN = "blackdetect=d=1.0:pix_th=0.10,freezedetect=n=-30dB:d=3.0,showinfo";
        // 音频分析滤镜链（静音 + 音量 + astats 逐帧 RMS 打印，供"音量过低段"解析；ametadata 输出到 stderr）
        public const string AF_CHAIN = "silencedetect=noise=-35dB:d=1.0,volumedetect,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level";

        /// <summary>执行一条 ffmpeg 分析命令，返回 (stderr全文, 解码错误行列表)。</summary>
        public static AnalyzeResult Analyze(string path, string vf, string af, double? durationSec, int timeoutSec = 1800)
        {
            return Analyze(path, vf, af, durationSec, timeoutSec, null);
        }

        public static AnalyzeResult Analyze(string path, string vf, string af, double? durationSec, int timeoutSec, string[] inputOptions)
        {
            List<string> cmd = new List<string>();
            cmd.Add("-v"); cmd.Add("info");
            if (inputOptions != null)
                for (int i = 0; i < inputOptions.Length; i++) cmd.Add(inputOptions[i]);
            cmd.Add("-i"); cmd.Add(path);
            if (durationSec.HasValue && durationSec.Value > 0)
            { cmd.Add("-t"); cmd.Add(durationSec.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(vf)) { cmd.Add("-vf"); cmd.Add(vf); cmd.Add("-an"); }
            if (!string.IsNullOrEmpty(af)) { cmd.Add("-map"); cmd.Add("0:a?"); cmd.Add("-af"); cmd.Add(af); cmd.Add("-vn"); }
            cmd.Add("-f"); cmd.Add("null"); cmd.Add("-");

            ProcessResult r = Run(FfmpegBin(), cmd.ToArray(), timeoutSec, true);
            List<string> errLines = ExtractErrorLines(r.Stderr);
            return new AnalyzeResult(r.Stderr, r.Stdout, errLines);
        }

        // ---- 解码为 PCM（用于交流声 / 波形分析）----
        /// <summary>将主音频流解码为单声道 8kHz s16le，返回原始字节。</summary>
        public static byte[] DecodeAudioPcm(string path, double durationSec, int timeoutSec = 900)
        {
            return DecodeAudioPcm(path, durationSec, 8000, null, timeoutSec);
        }

        /// <summary>
        /// 解码主音频流为单声道 s16le PCM。
        /// sampleRate 可指定采样率（交流声分析用 16kHz，能覆盖市电谐波与人声）。
        /// </summary>
        public static byte[] DecodeAudioPcm(string path, double durationSec, int sampleRate,
            string[] inputOptions, int timeoutSec = 900)
        {
            if (sampleRate <= 0) sampleRate = 8000;
            List<string> cmd = new List<string>();
            cmd.Add("-v"); cmd.Add("error");
            if (inputOptions != null)
                for (int i = 0; i < inputOptions.Length; i++) cmd.Add(inputOptions[i]);
            cmd.Add("-i"); cmd.Add(path);
            if (durationSec > 0)
            { cmd.Add("-t"); cmd.Add(durationSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
            cmd.Add("-map"); cmd.Add("0:a:0");
            cmd.Add("-vn"); cmd.Add("-ac"); cmd.Add("1");
            cmd.Add("-ar"); cmd.Add(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.Add("-f"); cmd.Add("s16le"); cmd.Add("-");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = FfmpegBin();
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

            try
            {
                using (Process p = Process.Start(psi))
                {
                    // ① 注册到全局运行列表：否则「停止」按钮的 KillRunning() 杀不到它，
                    //    用户点了停止也只能等它自己跑完。
                    RegisterRunning(p);

                    // ② 必须异步排空 stderr。RedirectStandardError=true 却不读，
                    //    ffmpeg 往 stderr 写满管道缓冲（约 4KB）就会永久阻塞 —— 典型死锁。
                    //    这里虽然用了 -v error 输出很少，但任何一条意外告警都可能触发。
                    StringBuilder errBuf = new StringBuilder();
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null && errBuf.Length < 8192) errBuf.AppendLine(e.Data);
                    };
                    p.BeginErrorReadLine();

                    // ③ ReadAll 现在真正使用超时（原先参数传了却没用，卡住就永久挂起）
                    byte[] data = ReadAll(p.StandardOutput.BaseStream, timeoutSec * 1000, p);

                    // ④ 退出也要有上限，不再无限等
                    if (!p.WaitForExit(10000))
                    {
                        try { p.Kill(); } catch { }
                        Log.Warn("ffmpeg 解码音频后未在 10 秒内退出，已强制结束");
                    }
                    if (data.Length == 0 && errBuf.Length > 0)
                        Log.Debug("解码音频无输出，ffmpeg 提示：" + errBuf.ToString().Trim());
                    return data;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new FfmpegError(ExplainStartFailure(FfmpegBin()));
            }
        }

        /// <summary>
        /// 读取流直到结束，**真正带超时**。
        /// 放在后台线程上读，主线程限时等待 —— 这样超时能中断，进程被杀时也能自然退出。
        /// （旧实现在调用线程上同步 Read，timeoutMs 参数根本没用上，一旦 ffmpeg 卡住就永久挂起。）
        /// </summary>
        private static byte[] ReadAll(Stream stream, int timeoutMs, Process owner)
        {
            MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[65536];
            Exception readErr = null;

            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try
                {
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                }
                catch (Exception ex) { readErr = ex; }
            });
            th.IsBackground = true;
            th.Start();

            if (!th.Join(timeoutMs > 0 ? timeoutMs : 600000))
            {
                try { if (owner != null && !owner.HasExited) owner.Kill(); } catch { }
                throw new FfmpegError("命令执行超时：ffmpeg.exe");
            }
            if (readErr != null) throw new FfmpegError("读取 ffmpeg 输出失败：" + readErr.Message);
            return ms.ToArray();
        }

        // ---- 包探测（时间戳连续性）----
        // 结果缓存：同一个文件里 ("v",400) 会被「帧率稳定性」与「时间戳连续性」各调一次，
        // 而实时流上每次都要拉满 400 个包（25fps 即 16 秒实时视频），重复调用纯属浪费。
        private static readonly Dictionary<string, List<double>> _packetCache = new Dictionary<string, List<double>>();
        private static readonly object _packetCacheLock = new object();

        /// <summary>清空包探测缓存。每个文件/每路流开始检测前调用，避免缓存无上限增长。</summary>
        public static void ClearPacketCache()
        {
            lock (_packetCacheLock) _packetCache.Clear();
        }

        /// <summary>探测前 limit 个包的时间戳（DTS 优先），返回 [dts_or_pts]。结果按 (路径,流,数量) 缓存。</summary>
        public static List<double> ProbePackets(string path, string streamSpec, int limit = 400)
        {
            string key = path + "|" + streamSpec + "|" + limit.ToString();
            lock (_packetCacheLock)
            {
                List<double> hit;
                if (_packetCache.TryGetValue(key, out hit)) return new List<double>(hit);
            }

            List<double> result = new List<double>();
            string readIntervals = "%+#" + limit.ToString();
            List<string> args = new List<string>();
            args.Add("-v"); args.Add("error");
            // 网络流必须显式 TCP：ffprobe 默认走 UDP，取包容易丢、也慢
            if (IsStreamUrl(path)) { args.Add("-rtsp_transport"); args.Add("tcp"); }
            args.Add("-select_streams"); args.Add(streamSpec);
            args.Add("-show_entries"); args.Add("packet=pts_time,dts_time,duration_time");
            args.Add("-of"); args.Add("csv=p=0");
            args.Add("-read_intervals"); args.Add(readIntervals);
            args.Add(path);

            ProcessResult r = Run(FfprobeBin(), args.ToArray(), 300, true);
            if (r.Code != 0) return result;
            foreach (string line in r.Stdout.Split('\n'))
            {
                string ln = line.Trim();
                if (ln.Length == 0) continue;
                string[] parts = ln.Split(',');
                string pts = parts.Length > 0 ? parts[0] : "";
                string dts = parts.Length > 1 ? parts[1] : "";
                // 与 Python 版一致：DTS 优先（B 帧视频 PTS 会乱序），DTS 缺失才用 PTS
                double? v = SafeF(dts);
                if (!v.HasValue) v = SafeF(pts);
                if (v.HasValue) result.Add(v.Value);
            }
            lock (_packetCacheLock) _packetCache[key] = new List<double>(result);
            return result;
        }

        // ---- 滤镜输出解析 ----
        /// <summary>
        /// RTSP 专用音频链：在原链基础上追加「频谱质心」，用于拾音器故障检测。
        ///
        /// 原理：拾音器坏了（或屏蔽失效）时输出的是持续的低频交流嗡鸣（50Hz 市电及其谐波）。
        /// 实测：50Hz 嗡鸣的频谱质心约 117Hz、过零率 0.006；正常环境音质心 2600Hz+、过零率 0.2。
        /// 两者差 17~34 倍，区分度极高。
        /// 两个 ametadata 分别打印逐秒 RMS（原有用途）与质心，互不干扰。
        /// </summary>
        public const string AF_CHAIN_PICKUP =
            "silencedetect=noise=-35dB:d=1.0,volumedetect,astats=metadata=1:reset=1,"
          + "ametadata=mode=print:key=lavfi.astats.Overall.RMS_level,"
          + "aspectralstats=measure=centroid,"
          + "ametadata=mode=print:key=lavfi.aspectralstats.1.centroid";

        /// <summary>解析 astats 整体统计：RMS level dB / Zero crossings rate / Crest factor / DC offset。取最后一组（Overall）。</summary>
        public static Dictionary<string, double> ParseAStats(string stderrText)
        {
            Dictionary<string, double> d = new Dictionary<string, double>();
            if (string.IsNullOrEmpty(stderrText)) return d;
            PutLast(d, stderrText, "rms", "RMS level dB:\\s*(-?[\\d.]+|-?inf|nan)");
            PutLast(d, stderrText, "zcr", "Zero crossings rate:\\s*([\\d.]+)");
            PutLast(d, stderrText, "crest", "Crest factor:\\s*([\\d.]+)");
            PutLast(d, stderrText, "dc", "DC offset:\\s*(-?[\\d.]+)");
            return d;
        }

        private static void PutLast(Dictionary<string, double> d, string text, string key, string pattern)
        {
            MatchCollection ms = Regex.Matches(text, pattern);
            if (ms.Count == 0) return;
            string v = ms[ms.Count - 1].Groups[1].Value;
            // astats 对数字静音报的是 "-inf"，对无数据报 "nan"。
            // 不处理的话「无有效信号」分支永远不会触发，静音素材会被误判成交流嗡鸣。
            if (v == "-inf" || v == "inf" || v == "-Infinity" || v == "Infinity")
            {
                d[key] = v.StartsWith("-") ? double.NegativeInfinity : double.PositiveInfinity;
                return;
            }
            if (v == "nan" || v == "NaN") return;      // 无数据，不记录该键
            double x;
            if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x))
                d[key] = x;
        }

        /// <summary>
        /// 测某个频段的平均电平（dB）。用于拾音器检测：测量 45-55Hz 市电频段能量，
        /// 与全频段能量比较即可判断是否被交流声主导。
        /// 依据来自真实样本实测：真电流麦的 50Hz 占比约 -3 ~ -15dB，而正常人声约 -40 ~ -75dB。
        /// </summary>
        public static double MeasureBandDb(string path, double loHz, double hiHz,
            double durationSec, string[] inputOptions)
        {
            string af = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "highpass=f={0},lowpass=f={1},volumedetect", loHz, hiHz);
            List<string> cmd = new List<string>();
            cmd.Add("-v"); cmd.Add("info");
            if (inputOptions != null)
                for (int i = 0; i < inputOptions.Length; i++) cmd.Add(inputOptions[i]);
            cmd.Add("-i"); cmd.Add(path);
            if (durationSec > 0)
            {
                cmd.Add("-t");
                cmd.Add(durationSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            cmd.Add("-map"); cmd.Add("0:a?");
            cmd.Add("-af"); cmd.Add(af);
            cmd.Add("-vn");
            cmd.Add("-f"); cmd.Add("null"); cmd.Add("-");

            int timeout = (int)((durationSec > 0 ? durationSec : 10) + 120);
            ProcessResult r = Run(FfmpegBin(), cmd.ToArray(), timeout, true);
            if (string.IsNullOrEmpty(r.Stderr)) return double.NaN;
            Dictionary<string, double> vol = ParseVolume(r.Stderr);
            double mv;
            return vol.TryGetValue("mean_volume", out mv) ? mv : double.NaN;
        }
        /// <summary>解析频谱质心序列并返回平均值（Hz）。无数据返回 0。</summary>
        public static double ParseCentroidMean(string stderrText)
        {
            if (string.IsNullOrEmpty(stderrText)) return 0;
            MatchCollection ms = Regex.Matches(stderrText, "aspectralstats\\.1\\.centroid=([\\d.]+)");
            if (ms.Count == 0) return 0;
            double sum = 0; int n = 0;
            foreach (Match m in ms)
            {
                double v;
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0)
                {
                    sum += v; n++;
                }
            }
            return n > 0 ? sum / n : 0;
        }

        /// <summary>探测包的 (pts, 字节大小, 是否关键帧)。用于码率稳定性与关键帧间隔分析。</summary>
        public static List<double[]> ProbePacketsDetail(string path, string streamSpec, int limit)
        {
            List<double[]> result = new List<double[]>();
            List<string> args = new List<string>();
            args.Add("-v"); args.Add("error");
            if (IsStreamUrl(path)) { args.Add("-rtsp_transport"); args.Add("tcp"); }
            args.Add("-select_streams"); args.Add(streamSpec);
            args.Add("-show_entries"); args.Add("packet=pts_time,dts_time,size,flags");
            args.Add("-of"); args.Add("csv=p=0");
            args.Add("-read_intervals"); args.Add("%+#" + limit.ToString());
            args.Add(path);
            ProcessResult r = Run(FfprobeBin(), args.ToArray(), 300, true);
            if (r.Code != 0) return result;
            foreach (string line in r.Stdout.Split('\n'))
            {
                string ln = line.Trim();
                if (ln.Length == 0) continue;
                string[] parts = ln.Split(',');
                if (parts.Length < 3) continue;
                // 取时间：pts_time 优先，缺失（N/A）时回落 dts_time
                double? t = SafeF(parts[0]);
                if (!t.HasValue && parts.Length > 1) t = SafeF(parts[1]);
                double size;
                if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out size))
                    continue;
                // flags 是最后一列，形如 "K__" / "___"
                string flags = parts[parts.Length - 1];
                bool key = flags.IndexOf('K') >= 0;
                if (t.HasValue && size > 0)
                    result.Add(new double[] { t.Value, size, key ? 1 : 0 });
            }
            return result;
        }

        private static Regex _silenceStart = new Regex("silence_start:\\s*([-\\d.]+)");
        private static Regex _silenceEnd = new Regex("silence_end:\\s*([-\\d.]+)");

        /// <summary>解析静音段，返回 [(start, end)]，end 可为空。</summary>
        public static List<Seg> ParseSilence(string stderrText)
        {
            List<Seg> segs = new List<Seg>();
            if (string.IsNullOrEmpty(stderrText)) return segs;
            MatchCollection ms = _silenceStart.Matches(stderrText);
            MatchCollection me = _silenceEnd.Matches(stderrText);
            for (int i = 0; i < ms.Count; i++)
            {
                double s = double.Parse(ms[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                double? e = null;
                if (i < me.Count) e = double.Parse(me[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                segs.Add(new Seg(s, e));
            }
            return segs;
        }

        private static Regex _blackRe = new Regex("black_start:([\\d.]+)\\s+black_end:([\\d.]+)\\s+black_duration:([\\d.]+)");

        /// <summary>解析黑帧段，返回 [(start, end, duration)]。</summary>
        public static List<Seg3> ParseBlack(string stderrText)
        {
            List<Seg3> segs = new List<Seg3>();
            if (string.IsNullOrEmpty(stderrText)) return segs;
            MatchCollection ms = _blackRe.Matches(stderrText);
            foreach (Match m in ms)
            {
                segs.Add(new Seg3(
                    double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
            return segs;
        }

        private static Regex _freezeStart = new Regex("freeze_start:\\s*([-\\d.]+)");
        private static Regex _freezeEnd = new Regex("freeze_end:\\s*([-\\d.]+)");

        /// <summary>解析冻结段，返回 [(start, end)]，任一端可为空。</summary>
        public static List<Seg> ParseFreeze(string stderrText)
        {
            List<Seg> segs = new List<Seg>();
            if (string.IsNullOrEmpty(stderrText)) return segs;
            MatchCollection ms = _freezeStart.Matches(stderrText);
            MatchCollection me = _freezeEnd.Matches(stderrText);
            int n = Math.Max(ms.Count, me.Count);
            for (int i = 0; i < n; i++)
            {
                double? s = null, e = null;
                if (i < ms.Count) s = double.Parse(ms[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (i < me.Count) e = double.Parse(me[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (s.HasValue)
                    segs.Add(new Seg(s, e));
            }
            return segs;
        }

        /// <summary>解析 volumedetect，返回 {n_samples, mean_volume, max_volume}（取最后一次）。</summary>
        public static Dictionary<string, double> ParseVolume(string stderrText)
        {
            Dictionary<string, double> result = new Dictionary<string, double>();
            if (string.IsNullOrEmpty(stderrText)) return result;
            string[] keys = new string[] { "n_samples", "mean_volume", "max_volume" };
            foreach (string key in keys)
            {
                Regex re = new Regex(key + ":\\s*([-\\d.]+)");
                MatchCollection ms = re.Matches(stderrText);
                if (ms.Count > 0)
                    result[key] = double.Parse(ms[ms.Count - 1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static readonly Regex _ptsRe = new Regex("pts_time:\\s*([\\d.]+)");
        private static readonly Regex _rmsRe = new Regex("RMS_level=\\s*(-?[\\d.]+)");

        /// <summary>解析 astats 逐帧 RMS（stdout 文本），返回 [(time, rms_db)]。</summary>
        public static List<double[]> ParseFrameRms(string stdoutText)
        {
            List<double[]> frames = new List<double[]>();
            if (string.IsNullOrEmpty(stdoutText)) return frames;
            double lastPts = -1;
            foreach (string raw in stdoutText.Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.Length == 0) continue;
                Match m = _ptsRe.Match(ln);
                if (m.Success)
                {
                    double t;
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out t))
                        lastPts = t;
                    continue;
                }
                Match mr = _rmsRe.Match(ln);
                if (mr.Success && lastPts >= 0)
                {
                    double v;
                    if (double.TryParse(mr.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v))
                        frames.Add(new double[] { lastPts, v });
                }
            }
            return frames;
        }

        /// <summary>把连续低电平帧聚合成"音量过低段"，返回 [[start, end], ...]（end 为 -1 表示到结尾）。</summary>
        public static List<double[]> ParseLowVolumeSegs(string stdoutText, double thresholdDb, double minDur)
        {
            List<double[]> low = new List<double[]>();
            List<double[]> rms = ParseFrameRms(stdoutText);
            foreach (double[] fr in rms)
                if (fr[1] < thresholdDb) low.Add(fr);
            List<double[]> segs = new List<double[]>();
            if (low.Count == 0) return segs;
            double segStart = low[0][0];
            double prev = low[0][0];
            for (int i = 1; i < low.Count; i++)
            {
                double t = low[i][0];
                if (t - prev > 1.5)   // 间隔超 1.5s 视为断开
                {
                    if (prev - segStart >= minDur) segs.Add(new double[] { segStart, prev });
                    segStart = t;
                }
                prev = t;
            }
            if (prev - segStart >= minDur) segs.Add(new double[] { segStart, prev });
            return segs;
        }

        private static readonly Regex _progTimeRe = new Regex("time=\\s*(\\d+):(\\d+):([\\d.]+)");
        private static readonly Regex _showinfoPtsRe = new Regex("pts_time:\\s*([\\d.]+)");

        /// <summary>解析解码错误发生的时间点（用 showinfo 逐帧 pts / 进度时间定位），返回秒列表。</summary>
        public static List<double> ExtractErrorTimes(string stderrText)
        {
            List<double> times = new List<double>();
            if (string.IsNullOrEmpty(stderrText)) return times;
            double lastSec = -1;
            foreach (string raw in stderrText.Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.Length == 0) continue;
                // showinfo 帧时刻（优先）：[Parsed_showinfo...] n: 12 pts:... pts_time:0.48
                Match ms = _showinfoPtsRe.Match(ln);
                if (ms.Success && ln.IndexOf("showinfo", StringComparison.Ordinal) >= 0)
                {
                    double t;
                    if (double.TryParse(ms.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out t))
                        lastSec = t;
                    continue;
                }
                // 进度行 time=（兜底）
                Match mt = _progTimeRe.Match(ln);
                if (mt.Success)
                {
                    double h, m, s;
                    if (double.TryParse(mt.Groups[1].Value, out h) && double.TryParse(mt.Groups[2].Value, out m)
                        && double.TryParse(mt.Groups[3].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out s))
                    {
                        if (h < 1000) lastSec = h * 3600 + m * 60 + s;
                    }
                    continue;
                }
                string lower = ln.ToLowerInvariant();
                if (lastSec >= 0 && _errorRe.IsMatch(ln) && !ContainsAny(lower, _errSkip))
                    times.Add(lastSec);
            }
            return times;
        }

        /// <summary>把错误时间点聚合成段：相邻 ≤gapSec 合并，每段 end 取最后一点+1s。返回 [[start, end], ...]。</summary>
        public static List<double[]> MergeErrorPoints(List<double> points, double gapSec)
        {
            List<double[]> segs = new List<double[]>();
            if (points == null || points.Count == 0) return segs;
            List<double> sorted = new List<double>(points);
            sorted.Sort();
            double s = sorted[0];
            double last = sorted[0];
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] - last > gapSec)
                {
                    segs.Add(new double[] { s, last + 1.0 });
                    s = sorted[i];
                }
                last = sorted[i];
            }
            segs.Add(new double[] { s, last + 1.0 });
            return segs;
        }

        // ---- 工具函数 ----
        public static double? SafeF(object v)
        {
            if (v == null) return null;
            string s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            if (s.Length == 0 || s == "N/A") return null;
            double d;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                return d;
            return null;
        }

        public static double FF(double? v, double def = 0.0)
        {
            return v.HasValue ? v.Value : def;
        }

        /// <summary>从 JSON 字典取字符串（MiniJson 简写）。</summary>
        public static string MiniGetStr(Dictionary<string, object> obj, string key)
        {
            return MiniJson.GetStr(obj, key);
        }

        private static string Trim(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    /// <summary>进程执行结果。</summary>
    public class ProcessResult
    {
        public int Code;
        public string Stdout;
        public string Stderr;
        public ProcessResult(int code, string stdout, string stderr)
        {
            Code = code; Stdout = stdout; Stderr = stderr;
        }
    }

    /// <summary>滤镜分析结果。</summary>
    public class AnalyzeResult
    {
        public string Stderr;
        public string Stdout;
        public List<string> ErrorLines;
        public AnalyzeResult(string stderr, string stdout, List<string> errorLines)
        {
            Stderr = stderr; Stdout = stdout; ErrorLines = errorLines;
        }
    }

    /// <summary>时间段（start / end，可为空）。</summary>
    public class Seg
    {
        public double? Start;
        public double? End;
        public Seg(double? s, double? e) { Start = s; End = e; }
    }

    /// <summary>时间段时间段（start / end / duration）。</summary>
    public class Seg3
    {
        public double Start;
        public double End;
        public double Duration;
        public Seg3(double s, double e, double d) { Start = s; End = e; Duration = d; }
    }
}
