/* -*- coding: utf-8 -*-
 * Logger.cs — 运行日志（两级：INFO 用户级 / DEBUG 排障级）
 * 内存环形历史（实时窗口用）+ 文件持久化（logs\ 目录，按日期分文件，UTF-8）。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    /// <summary>全局日志器：UI 订阅 OnLog 实时显示，内存环形历史 + 落盘持久化。</summary>
    public static class Log
    {
        public static event Action<string> OnLog;

        private static readonly object _lock = new object();
        private static readonly List<string> _history = new List<string>();
        private const int MaxHistory = 3000;

        private static readonly string _logDir;
        private static string _logFile = "";

        static Log()
        {
            try
            {
                _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(_logDir);
            }
            catch
            {
                _logDir = "";
            }
        }

        /// <summary>当前日志文件路径（logs\video_checker_yyyyMMdd.log）。</summary>
        public static string FilePath
        {
            get
            {
                lock (_lock)
                {
                    if (_logFile.Length == 0) _logFile = CurrentFile();
                    return _logFile;
                }
            }
        }

        private static string CurrentFile()
        {
            return Path.Combine(_logDir, "video_checker_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        }

        public static void Info(string msg)
        {
            Emit("INFO", msg);
        }

        public static void Debug(string msg)
        {
            Emit("DEBUG", msg);
        }

        public static void Warn(string msg)
        {
            Emit("WARN", msg);
        }

        public static void Error(string msg)
        {
            Emit("ERROR", msg);
        }

        private static void Emit(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " [" + level + "] " + Sanitize(msg);
            lock (_lock)
            {
                _history.Add(line);
                if (_history.Count > MaxHistory)
                    _history.RemoveRange(0, _history.Count - MaxHistory);
                // 落盘：按日期切换文件，UTF-8 追加
                try
                {
                    string f = CurrentFile();
                    if (f != _logFile) _logFile = f;   // 跨天自动切换新文件
                    if (_logFile.Length > 0)
                        File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 磁盘不可写时静默降级：只保留内存历史
                }
            }
            Action<string> h = OnLog;
            if (h != null)
            {
                try { h(line); }
                catch { }
            }
        }

        /// <summary>
        /// 日志脱敏：把 rtsp://用户:密码@host... 里的密码换成 ***。
        ///
        /// 为什么放在日志出口而不是各个调用点：
        ///   ffmpeg 的报错信息经常会把完整地址回显出来，
        ///   抛异常时「异常消息」里也可能带地址 —— 逐个调用点去堵堵不干净 ✗
        ///   在这里做一次全局处理，任何来源都覆盖得到 ✓
        ///
        /// 只改密码部分，主机、端口、路径都保留 —— 不影响排障。
        /// </summary>
        private static string Sanitize(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            try
            {
                return Regex.Replace(msg,
                    @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<user>[^:/@\s]+):(?<pass>[^@/\s]*)@",
                    "${scheme}${user}:***@");
            }
            catch { return msg; }
        }

        /// <summary>历史日志快照（新→旧 或 旧→新由调用方决定）。</summary>
        public static List<string> Snapshot()
        {
            lock (_lock)
                return new List<string>(_history);
        }
    }
}
