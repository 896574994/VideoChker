/* -*- coding: utf-8 -*-
 * AppPaths.cs — 程序数据文件的统一位置
 *
 * ★ 为什么加这个（2026-09-13 用户指出）
 *
 *   用户截图问：「live_alerts 不建立个文件夹么？就这样丢在根目录下？」
 *   一看确实乱 —— 程序目录长这样 ✗：
 *     video_checker_gui.exe
 *     ffmpeg\        logs\        reports\     archive\    ← 已经是目录 ✓
 *     settings.ini   lock.dat     rtsp_streams.dat
 *     live_alerts.csv  cli_result.txt  .theme  .help_shown  .pwd_prompted   ← 全散着 ✗
 *   **有目录有文件，不一致** ✓
 *
 * ★ 现在的结构
 *     video_checker_gui.exe
 *     ffmpeg\         依赖（打包时带进来）
 *     data\           ★ 所有数据文件（设置/密码锁/流列表/巡检记录/主题/标记）
 *     logs\           日志（含命令行自检输出）
 *     reports\        检测报告
 *     archive\        归档视频
 *
 * ★ 旧文件会自动迁移（这一步不能省 ✗）
 *   老用户（包括开发者自己）的文件都在根目录 ✓
 *   所以 Data() 第一次被调用时会把根目录下的旧文件**搬进 data\** ✓
 *   只在"新位置没有、旧位置有"时才搬 ✓ 不覆盖 ✓ 搬失败也不影响使用 ✓
 *   —— 和 RtspForm 迁移旧版明文 CSV 是同一个思路 ✓
 *
 * C# 5 兼容语法。
 */

using System;
using System.IO;

namespace VideoChecker
{
    public static class AppPaths
    {
        /// <summary>程序所在目录（exe 所在处）。</summary>
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>数据目录：程序目录下的 data\</summary>
        public static string DataDir
        {
            get { return Path.Combine(BaseDir, "data"); }
        }

        /// <summary>日志目录</summary>
        public static string LogsDir
        {
            get { return Path.Combine(BaseDir, "logs"); }
        }

        /// <summary>检测报告目录</summary>
        public static string ReportsDir
        {
            get { return Path.Combine(BaseDir, "reports"); }
        }

        /// <summary>归档视频目录</summary>
        public static string ArchiveDir
        {
            get { return Path.Combine(BaseDir, "archive"); }
        }

        // ==================== 迁移 ====================

        private static bool _inited;
        private static readonly object _lock = new object();

        /// <summary>需要从根目录搬进 data\ 的老文件。</summary>
        private static readonly string[] LegacyFiles = new string[] {
            "settings.ini",
            "lock.dat",
            "rtsp_streams.dat",
            "rtsp_streams.csv",
            ".theme",
            ".help_shown",
            ".pwd_prompted",
            "live_alerts.csv",
        };

        /// <summary>
        /// 第一次用到路径时做两件事：
        ///   ① 建好 data\ 目录
        ///   ② 把根目录下的老文件搬进来（只在目标不存在时搬 ✓ 不覆盖 ✓）
        /// </summary>
        private static void EnsureInit()
        {
            if (_inited) return;
            lock (_lock)
            {
                if (_inited) return;
                _inited = true;
                try { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); }
                catch (Exception) { }

                foreach (string name in LegacyFiles)
                {
                    try
                    {
                        string oldPath = Path.Combine(BaseDir, name);
                        string newPath = Path.Combine(DataDir, name);
                        if (File.Exists(oldPath) && !File.Exists(newPath))
                            File.Move(oldPath, newPath);
                    }
                    catch (Exception) { /* 搬不动就算了，不影响使用 */ }
                }

                // 命令行自检输出：老位置在根目录，新位置在 logs\（不是 data\）
                try
                {
                    string oldCli = Path.Combine(BaseDir, "cli_result.txt");
                    string newCli = Path.Combine(LogsDir, "cli_result.txt");
                    if (File.Exists(oldCli) && !File.Exists(newCli))
                    {
                        if (!Directory.Exists(LogsDir)) Directory.CreateDirectory(LogsDir);
                        File.Move(oldCli, newCli);
                    }
                }
                catch (Exception) { }
            }
        }

        // ==================== 取路径 ====================

        /// <summary>数据文件的完整路径（会自动建 data\ 并迁移旧文件）。</summary>
        public static string Data(string name)
        {
            EnsureInit();
            return Path.Combine(DataDir, name);
        }

        /// <summary>日志文件的完整路径。</summary>
        public static string Log(string name)
        {
            EnsureInit();
            try { if (!Directory.Exists(LogsDir)) Directory.CreateDirectory(LogsDir); }
            catch (Exception) { }
            return Path.Combine(LogsDir, name);
        }

        /// <summary>报告文件的完整路径。</summary>
        public static string Report(string name)
        {
            try { if (!Directory.Exists(ReportsDir)) Directory.CreateDirectory(ReportsDir); }
            catch (Exception) { }
            return Path.Combine(ReportsDir, name);
        }

        /// <summary>把老文件搬进 data\（已经搬过的不重复搬）。给排障用。</summary>
        public static int MigrateLegacy()
        {
            EnsureInit();
            int n = 0;
            foreach (string name in LegacyFiles)
            {
                if (File.Exists(Path.Combine(DataDir, name))) n++;
            }
            return n;
        }
    }
}
