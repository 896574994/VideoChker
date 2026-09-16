/* -*- coding: utf-8 -*-
 * Program.cs — 程序入口
 * 流程：启动页（设过开屏密码的话）→ 主窗体
 * 附带 --cli <路径> 自检/批量模式：跳过密码锁，直接检测并生成报告后退出。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace VideoChecker
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // ---- CLI 自检/批量模式 ----
            // 用法：video_checker_gui.exe --cli <路径> [--pwd <密码>] [--rtsp 秒] [--mage 地址] [--model 名] [--frames N]
            //
            // ★ 设置过开屏密码时，命令行模式**也必须提供密码**。
            //   早先这段代码在密码校验之前就返回了 —— 等于给程序留了个后门：
            //   加一个 --cli 参数就能绕过密码锁，不需要任何逆向知识 ✗
            //   现在未设密码时行为不变（向后兼容），设了密码就必须带 --pwd。
            if (args != null && args.Length >= 2 && args[0] == "--cli")
            {
                if (PassLock.IsEnabled)
                {
                    string pwd = ArgValue(args, "--pwd");
                    if (!PassLock.Verify(pwd))
                    {
                        string why = "本程序已设置开屏密码。";
                        Log.Warn("命令行模式被拒绝：未提供正确的开屏密码");
                        try { Console.Error.WriteLine(why + " 命令行模式需要密码。"); } catch { }
                        try
                        {
                            Console.Error.WriteLine("用法：video_checker_gui.exe --cli <路径> --pwd <密码>");
                        }
                        catch { }
                        Environment.Exit(6);
                        return;
                    }
                }
                // ★★ CLI 更要先自检（2026-09-14 加）
                //
                //   用户在公司的 Win7 上：**每个文件 0.1 秒 ERROR（无法分析）** ✗
                //   根因是那台机器缺「通用 C 运行库」(UCRT) ✓ ffmpeg.exe 起不来 ✓
                //   而 ffmpeg.exe **文件明明在** ✓ 原来的提示还让他去查那个文件夹在不在 ✗
                //   ★ 拿到别人电脑上，第一件事往往就是跑一次 --cli 试试 ✓
                //     那就在这一步把原因说清楚 ✓ 而不是丢一句"全部无法分析" ✓
                //   （判据是**真的把 ffmpeg 跑一遍** ✓ 不是看文件在不在 ✗ —— 见铁律 1）
                string preCli = "";
                try { preCli = Ffmpeg.PreflightCheck(); }
                catch (Exception ex) { preCli = "ffmpeg 自检异常：" + ex.Message; }
                if (preCli.Length > 0)
                {
                    Log.Error("ffmpeg 自检未通过：" + preCli.Replace("\n", " "));
                    try
                    {
                        Console.Error.WriteLine("★ ffmpeg 起不来，检测会全部失败（无法分析）。原因：");
                        Console.Error.WriteLine(preCli);
                    }
                    catch (Exception) { }
                    Environment.Exit(7);
                    return;
                }

                int code = RunCli(args);
                Environment.Exit(code);
                return;
            }

            // ★★ 图形模式也先自检一次（2026-09-14 加）—— 但**只是提醒，不拦着不让用** ✓
            //
            //   为什么不像 CLI 那样直接退出：
            //     这台机器上 ffmpeg 起不来 ✓ 那"检测"相关的功能确实全废 ✓
            //     但**设置、主题、看日志、导入流列表**这些还是能用的 ✓
            //     一上来就把整个程序退掉，用户会觉得"你这软件在我电脑上打不开" ✗
            //     而真正需要拦的地方（点「开始检测」）在 MainForm.OnStart 里还有一道 ✓
            if (args != null && args.Length == 0)
            {
                string preGui = "";
                try { preGui = Ffmpeg.PreflightCheck(); }
                catch (Exception ex) { preGui = "ffmpeg 自检异常：" + ex.Message; }
                if (preGui.Length > 0)
                {
                    Log.Error("ffmpeg 自检未通过（图形模式）：" + preGui.Replace("\n", " "));
                    MessageBox.Show(preGui,
                        "ffmpeg 起不来 —— 检测功能会全部失败（无法分析）",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            // ---- 诊断：把"排障要用的东西"收集成一个文件（两种用法）----
            //
            //   video_checker_gui.exe --diag <输出文件>        只写环境报告（系统/运行库/ffmpeg 退出码）
            //   video_checker_gui.exe --diag-bundle            写**完整诊断包**（环境 + 错误摘录 + 完整日志 + 设置）
            //
            // ★★ 为什么要这个东西（2026-09-14，用户连着提了两次）：
            //
            //   第一次：**「我就是你的 debug 日志不够详细吧，
            //             现在我连带数据回来给你分析都做不到」**
            //   第二次：**「你在这个程序里面的日志功能中的 debug 搞一个能够收集到你
            //             需要的报错日志信息的工具或代码，然后我才好弄报错的信息回来」**
            //
            //   ★ 这两句说的是同一件事：**排障需要的信息散在好几处** ✗
            //     系统/运行库在"脑子里"、ffmpeg 退出码一闪而过、
            //     日志在 logs\ 里、设置在 data\ 里 ✓
            //     而用户不是开发者 ✗ 他不知道该给我什么 ✓
            //
            //   ★★★ 而这里当初**漏了长度判断** ✗✗（2026-09-15 用户装机后"程序打不开"）
            //
            //     写的是：  if (args != null && args[0] == "--diag-bundle")
            //     而**双击启动时 args 是空数组（长度 0）** ✗
            //     → args[0] 抛 IndexOutOfRangeException ✓
            //     → 崩在 Application.Run 之前 ✓ **窗口一秒都不会出现** ✓
            //     用户看到的就是"装完了，程序打不开" ✗✗
            //
            //     事件日志实锤：
            //       .NET Runtime：异常信息: System.IndexOutOfRangeException
            //                     在 VideoChecker.Program.Main(System.String[])
            //
            //   ★★★ 为什么这一轮所有自检都没拦住它：
            //     · 冒烟测的是 `--cli ...`（**有参数**）✗
            //     · check_release 跑的也是 `--cli ...`（**有参数**）✗
            //     · 验收 harness 有自己的 Main ✗ 根本不走 Program.Main ✓
            //     · 我自己试的也是 --diag / --diag-bundle（**都有参数**）✗
            //     → **没有一个人用"用户的方式"启动过程序** ✓✓
            //       而用户的方式恰恰是：**双击，零参数** ✗
            //   ★ 教训：**"能跑"要用用户那种跑法去跑** ✓
            //     有参数地跑一遍，不等于"用户能打开" ✓
            //     （已加进 smoke_test.ps1 步骤① 之后的"无参数启动"检查 ✓）
            if (args != null && args.Length >= 1 && args[0] == "--diag-bundle")
            {
                int rc0 = 0;
                try
                {
                    string p = Diagnostics.ExportBundle();
                    try { Console.WriteLine("诊断包已写出：" + p); } catch (Exception) { }
                }
                catch (Exception ex)
                {
                    rc0 = 8;
                    try { Console.Error.WriteLine("写诊断包失败：" + ex.Message); } catch (Exception) { }
                    Log.Error("写诊断包失败：" + ex.Message);
                }
                Environment.Exit(rc0);
                return;
            }

            if (args != null && args.Length >= 2 && args[0] == "--diag")
            {
                int rc = 0;
                try
                {
                    string txt = Diagnostics.BuildReport();      // ★ 和日志窗口共用同一份 ✓
                    File.WriteAllText(args[1], txt, new UTF8Encoding(true));
                    try { Console.WriteLine("诊断报告已写出：" + args[1]); } catch (Exception) { }
                    Log.Info("环境诊断报告已写出：" + args[1]);
                }
                catch (Exception ex)
                {
                    rc = 8;
                    try { Console.Error.WriteLine("写诊断报告失败：" + ex.Message); } catch (Exception) { }
                    Log.Error("写诊断报告失败：" + ex.Message);
                }
                Environment.Exit(rc);
                return;
            }

            // ---- 生成发布用说明文件 ----
            // 用法：video_checker_gui.exe --write-readme <输出路径>
            //
            // ★ 为什么让程序自己生成，而不是在打包脚本里写一份：
            //   说明书正文（含免责声明、第三方组件声明）只应该有一个来源 ✓
            //   早先打包脚本里另抄了一份，结果就是"改了程序里的、忘了脚本里的" ✗
            //   现在打包脚本只负责调用这个命令 + 追加打包时间等构建信息 ✓
            if (args != null && args.Length >= 2 && args[0] == "--write-readme")
            {
                try
                {
                    File.WriteAllText(args[1], BuildReadme(), new UTF8Encoding(true));
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine("生成说明文件失败：" + ex.Message); } catch { }
                    Environment.Exit(7);
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ---- 全局异常兜底：未处理异常写入 logs\error.log，不弹 JIT 调试框 ----
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => { WriteError(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) WriteError(ex);
            };

            // 只有启用了开屏密码才弹启动页；未设密码时直接进主界面
            // （默认不拦，进去之后再提示用户设置密码）
            if (PassLock.IsEnabled)
            {
                using (SplashForm splash = new SplashForm())
                {
                    if (splash.ShowDialog() != DialogResult.OK)
                        return;   // 密码错误关闭或直接关闭 = 不进入主界面
                }
            }

            Application.Run(new MainForm());
        }

        /// <summary>从命令行参数里取 "--名字 值" 形式的值，找不到返回空串。</summary>
        private static string ArgValue(string[] args, string name)
        {
            if (args == null) return "";
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == name) return args[i + 1];
            return "";
        }

        /// <summary>
        /// 生成随程序分发的「使用说明.txt」。
        /// 内容全部取自 AppInfo，保证与程序内的使用说明、免责声明完全一致 ✓
        /// 打包脚本会在末尾自行追加「打包时间 / 安全版说明」等构建信息。
        /// </summary>
        /// <summary>
        /// 环境诊断报告。**给用户发给我用** ✗ 不是给程序自己看 ✓
        ///
        /// ★ 设计原则（2026-09-14，用户提「带不回数据」之后定的）：
        ///   ① 只写**机器事实** ✓ 不写推测 ✓
        ///   ② 不知道的字段也要写"(读不到)"✗ 不能省 ✓（省掉分不清"没有"和"没查"）
        ///   ③ 每一项都要能**独立回答一个问题** ✓
        ///      "缺不缺运行库" / "exe 在不在、多大、什么时间" / "跑起来退出码是多少"
        ///      / "它自己输出了什么" / "装的是哪个 .NET" / "系统是几位的"
        ///   ④ 一条命令能产出 ✓ 用户只要会复制 ✓
        // ★ 环境诊断报告**搬到 Diagnostics.cs 了**（2026-09-14）
        //   为什么要搬：日志窗口那个「导出诊断包」也要用同一份内容 ✓
        //   两份就要漂移 ✗ —— 这个项目已经因为这个栽过好几次了 ✓
        //   所以：**一个事实只写一遍** ✓ Program.cs / 日志窗口 都调 Diagnostics ✓

        private static string BuildReadme()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppInfo.Title + " " + AppInfo.Version + "（免安装版）");
            sb.AppendLine("================================");
            sb.AppendLine("开源项目 —— 版本来源与开发历程见仓库里的 docs/版本来源.md");
            sb.AppendLine("版本日期：" + AppInfo.BuildDate);
            sb.AppendLine();
            sb.AppendLine("怎么用");
            sb.AppendLine("------");
            sb.AppendLine("双击 video_checker_gui.exe 即可运行，不需要安装。");
            sb.AppendLine("可以整个文件夹拷到 U 盘 / 其它电脑上直接用。");
            sb.AppendLine();
            sb.AppendLine("目录说明");
            sb.AppendLine("--------");
            sb.AppendLine("  video_checker_gui.exe   主程序");
            sb.AppendLine("  uninstall.exe           删除程序文件（见下方说明）");
            sb.AppendLine("  ffmpeg\\bin\\             音视频分析引擎，必需，不要删除");
            sb.AppendLine("  reports\\                检测报告会自动生成在这里");
            sb.AppendLine("  archive\\                归档分类复制过来的文件");
            sb.AppendLine("  logs\\                   运行日志");
            sb.AppendLine();
            sb.AppendLine("关于 uninstall.exe");
            sb.AppendLine("------------------");
            sb.AppendLine("免安装版其实不需要卸载 —— 不想用了直接删掉整个文件夹即可。");
            sb.AppendLine();
            sb.AppendLine("如果你只想删掉程序、保留自己的数据，可以运行 uninstall.exe：");
            sb.AppendLine("  · 它只删除程序文件（主程序、ffmpeg、卸载程序自身）；");
            sb.AppendLine("  · 检测报告、归档文件、日志、配置默认保留；");
            sb.AppendLine("  · 想连数据一起删，需要在界面上自己勾选（默认不勾）。");
            sb.AppendLine();
            sb.AppendLine("运行环境");
            sb.AppendLine("--------");
            sb.AppendLine("Windows 7 / 10 / 11，需要 .NET Framework 4.x（Win7 以上系统自带）。");
            sb.AppendLine();
            sb.AppendLine("首次运行");
            sb.AppendLine("--------");
            sb.AppendLine("默认不需要密码，直接进入；进去后会问一次要不要设置开屏密码。");
            sb.AppendLine();
            sb.AppendLine("程序内按左侧导航栏最后一个图标（使用说明）可查看完整说明。");
            sb.AppendLine();
            sb.AppendLine("============================================================");
            sb.AppendLine("第三方组件");
            sb.AppendLine("============================================================");
            sb.AppendLine(AppInfo.ThirdParty);
            sb.AppendLine();
            sb.AppendLine("============================================================");
            sb.AppendLine("免责声明");
            sb.AppendLine("============================================================");
            sb.AppendLine(AppInfo.Disclaimer);
            return sb.ToString();
        }

        /// <summary>把未处理异常写入 exe 目录 logs\error.log（供排障 Debug 查看）。</summary>
        private static void WriteError(Exception ex)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "error.log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + (ex == null ? "未知异常" : ex.ToString()) + "\r\n\r\n";
                File.AppendAllText(file, line, Encoding.UTF8);

                // ★ 同时写进**程序自己的日志**（2026-09-16 加）
                //   为什么：诊断包的「二、错误与警告摘录」读的是
                //   logs\video_checker_*.log ✗ 而崩溃原来**只**进 error.log ✗
                //   → 用户 2026-09-16 发回来的诊断包上写着「ERROR 0 行」✗
                //     而他几分钟前刚崩过一次（Win7 上点停止）✓
                //   —— 一处异常要**两个地方都写**，诊断包才自洽 ✓
                try { Log.Error("未处理异常：" + (ex == null ? "未知异常" : ex.ToString())); } catch { }
            }
            catch { }
            try
            {
                MessageBox.Show("程序遇到问题已记录，可继续使用。\n详情见程序目录 logs\\error.log（打开左侧导航栏的「日志」、切到 Debug 级别可见）",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        /// <summary>命令行检测模式：返回退出码（0=通过 1=警告 2=失败 3=错误 4=无文件）。</summary>
        private static int RunCli(string[] args)
        {
            // 密码锁自检。注意要分两种情况：
            //   未启用密码 → 任何输入都应放行（默认不拦）；
            //   已启用密码 → 错密码必须被拒、主控密码必须可用。
            // 早先这里只按"已启用"写，导致默认状态下自检永远 FAIL。
            bool pwdOk;
            if (!PassLock.IsEnabled)
                pwdOk = PassLock.VerifyQuiet("任意输入");            // 未启用 → 任何输入都放行
            else
                pwdOk = !PassLock.VerifyQuiet("123456");             // 启用后 → 错密码必须被拒
            try
            {
                string path = args[1];
                double rtspSec = 0.0;
                string mageUrl = "";
                string mageModel = "";
                int mageFrames = 4;
                string mageStrategy = "";
                string mageTimes = "";
                for (int i = 2; i < args.Length - 1; i++)
                {
                    if (args[i] == "--rtsp" && i + 1 < args.Length) { double.TryParse(args[i + 1], out rtspSec); i++; }
                    else if (args[i] == "--mage" && i + 1 < args.Length) { mageUrl = args[i + 1]; i++; }
                    else if (args[i] == "--model" && i + 1 < args.Length) { mageModel = args[i + 1]; i++; }
                    else if (args[i] == "--frames" && i + 1 < args.Length) { int.TryParse(args[i + 1], out mageFrames); i++; }
                    else if (args[i] == "--strategy" && i + 1 < args.Length) { mageStrategy = args[i + 1]; i++; }
                    else if (args[i] == "--times" && i + 1 < args.Length) { mageTimes = args[i + 1]; i++; }
                }

                CheckOptions opts = new CheckOptions();
                opts.CheckAudio = true;
                opts.CheckVideo = true;
                opts.CheckSync = true;
                opts.CheckQuality = false;
                opts.CheckMage = mageUrl.Length > 0;
                opts.Mage.Url = mageUrl.Length > 0 ? mageUrl : "http://127.0.0.1:11434";
                opts.Mage.Model = mageModel.Length > 0 ? mageModel : "qwen2.5vl:7b";
                opts.Mage.Frames = mageFrames;
                if (mageStrategy.Length > 0)
                {
                    if (mageStrategy == "motion") opts.Mage.Strategy = "motion";
                    else if (mageStrategy == "uniform") opts.Mage.Strategy = "uniform";
                    else if (mageStrategy == "full") opts.Mage.Strategy = "full";
                    else if (mageStrategy == "custom")
                    {
                        opts.Mage.Strategy = "custom";
                        string[] pts = mageTimes.Split(new char[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string pt in pts)
                        {
                            double t;
                            if (double.TryParse(pt, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out t) && t >= 0)
                                opts.Mage.CustomTimes.Add(t);
                        }
                        opts.Mage.CustomTimes.Sort();
                    }
                    else opts.Mage.Strategy = "auto";
                }

                List<MediaReport> reports = new List<MediaReport>();
                StringBuilder log = new StringBuilder();
                log.AppendLine("密码锁自检: " + (pwdOk ? "PASS" : "FAIL"));
                if (Ffmpeg.IsStreamUrl(path))
                {
                    double dur = rtspSec > 0 ? rtspSec : 30.0;
                    log.AppendLine("RTSP 实时检测: " + path + "（拉流 " + dur.ToString("0") + " 秒）");
                    reports.Add(Engine.CheckRtsp(path, dur, opts));
                }
                else
                {
                    List<string> files = Engine.CollectFiles(path);
                    if (files.Count == 0)
                    {
                        File.WriteAllText(AppPaths.Log("cli_result.txt"),
                            "未找到任何视频文件: " + path, Encoding.UTF8);
                        return 4;
                    }
                    for (int i = 0; i < files.Count; i++)
                    {
                        MediaReport rep = Engine.CheckOneFile(files[i], opts);
                        reports.Add(rep);
                        log.AppendLine("[" + rep.Overall + "] " + Path.GetFileName(files[i]));
                    }
                }

                string reportDir = ReportHtml.DefaultReportDir();
                Directory.CreateDirectory(reportDir);
                string htmlPath = ReportHtml.StampedPath(reportDir, "video_check_report", ".html");
                ReportHtml.Export(reports, htmlPath, "全片分析");
                string pdfPath = PdfExport.ExportPdf(reports, Path.ChangeExtension(htmlPath, ".pdf"), "全片分析");
                log.AppendLine("报告: " + htmlPath);
                log.AppendLine("PDF : " + pdfPath);

                int nPass = 0, nWarn = 0, nFail = 0, nErr = 0;
                foreach (MediaReport r in reports)
                {
                    if (r.Overall == "PASS") nPass++;
                    else if (r.Overall == "WARN") nWarn++;
                    else if (r.Overall == "FAIL") nFail++;
                    else nErr++;
                }
                log.AppendLine("汇总: 共 " + reports.Count + " 个 | PASS " + nPass + " | WARN " + nWarn + " | FAIL " + nFail + " | 无法分析 " + nErr);
                File.WriteAllText(AppPaths.Log("cli_result.txt"), log.ToString(), Encoding.UTF8);

                if (nFail > 0) return 2;
                if (nWarn > 0) return 1;
                if (nErr > 0) return 3;
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(AppPaths.Log("cli_result.txt"),
                        "[异常] " + ex.ToString(), Encoding.UTF8);
                }
                catch { }
                return 3;
            }
        }
    }
}
