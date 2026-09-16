/* -*- coding: utf-8 -*-
 * NetTrace.cs — 协议报文记录（"抓包"的文本版）
 *
 * ★★ 为什么要有它（2026-09-15 用户提的）
 *
 *   用户原话：**「可以搞个 .txt 的 pacp 抓包报文 bug 反馈」**
 *
 *   ★ 他说的是要害 ✗ 前面几次排障，我拿到的都是"结论"（"连不上""没收到""失败了"）✓
 *     而真正能定位问题的，是**双方到底发了什么、收回了什么** ✓
 *     · 摄像头到底有没有推过来？推的是什么事件？
 *     · 往摄像头写「上传中心」时，它是 200 还是 401？
 *     · ONVIF 那次调用返回了什么？
 *   这些以前**只在内存里闪过** ✗ 出问题时没人抓得住 ✓
 *
 *   所以：把每一次网络对话**按原文记成一个 .txt** ✓
 *   用户点「导出诊断包」就能把它一起带回来 ✓
 *
 * ★ 三条规矩
 *   ① **脱敏**：URL 里的密码、Authorization 头里的 Basic 凭据，一律 *** ✓
 *   ② **有上限**：默认每天最多 4 MB ✗ 满了就停并写明 ✓
 *      （不能让它把用户磁盘写满 ✓ 也不能让它拖慢程序 ✓）
 *   ③ **不知道的也写**：正文空就写"(空)" ✓ 长度 0 就写 0 ✓
 *      （"空"本身就是信息 ✓ 省掉就分不清"没有"和"没记" ✓）
 *
 * ★ 用 C# 5 语法
 */

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    public static class NetTrace
    {
        private static readonly object _lock = new object();
        private static string _curFile = "";
        private static long _curSize;
        private static bool _capped;

        /// <summary>单日上限 4 MB —— 超了就停（并只写一条"已达上限"）。</summary>
        private const long MaxBytes = 4 * 1024 * 1024;

        /// <summary>开关（界面上可关）。关掉时 Note() 直接返回。</summary>
        public static volatile bool Enabled = true;

        /// <summary>当前报文文件（给"导出诊断包"和界面显示用）。</summary>
        public static string FilePath
        {
            get
            {
                lock (_lock)
                {
                    if (_curFile.Length == 0) _curFile = CurrentFile();
                    return _curFile;
                }
            }
        }

        private static string CurrentFile()
        {
            try
            {
                string dir = AppPaths.LogsDir;
                if (dir == null || dir.Length == 0) return "";
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "报文_" + DateTime.Now.ToString("yyyyMMdd") + ".txt");
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// 记一段。kind 用「→ 发」/「← 收」这种短标签，text 是正文（可以多行）。
        /// </summary>
        public static void Note(string kind, string text)
        {
            if (!Enabled) return;
            try
            {
                string block = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + kind
                    + Environment.NewLine + Mask(text == null ? "(空)" : text)
                    + Environment.NewLine;
                byte[] data = Encoding.UTF8.GetBytes(block);
                lock (_lock)
                {
                    string f = CurrentFile();
                    if (f != _curFile) { _curFile = f; _curSize = 0; _capped = false; }
                    if (_curFile.Length == 0) return;
                    if (_capped) return;
                    if (_curSize + data.Length > MaxBytes)
                    {
                        // 满了：写一条说明就收手（不再无限写）
                        _capped = true;
                        string note = "[" + DateTime.Now.ToString("HH:mm:ss") + "] "
                            + "★ 今天的报文已达 " + (MaxBytes / 1024 / 1024) + " MB 上限，停止记录。"
                            + "把这一份发回来就够定位了。" + Environment.NewLine;
                        File.AppendAllText(_curFile, note, Encoding.UTF8);
                        return;
                    }
                    File.AppendAllText(_curFile, block, Encoding.UTF8);
                    _curSize += data.Length;
                }
            }
            catch (Exception) { /* 记录本身不能把程序拖垮 */ }
        }

        /// <summary>
        /// 记一次 HTTP 往返。request/response 传原文块，空就写"(空)"。
        /// </summary>
        public static void Exchange(string what, string request, string response)
        {
            if (!Enabled) return;
            Note("→ 发【" + what + "】", request);
            Note("← 收【" + what + "】", response);
        }

        /// <summary>
        /// 把正文截到 n 字（长报文只留前面一段 + 说明"还有多少字"）。
        /// 为什么不整段记：一张 300KB 的 JPEG base64 能把文件撑爆 ✗ 而排障只看开头 ✓
        /// </summary>
        public static string Short(string s, int n)
        {
            if (s == null) return "(空)";
            if (s.Length == 0) return "(空)";
            if (s.Length <= n) return s;
            return s.Substring(0, n) + "…（后面还有 " + (s.Length - n) + " 字，已省略）";
        }

        /// <summary>
        /// 脱敏：
        ///   · `scheme://user:密码@host` → 密码换 ***
        ///   · `Authorization: Basic xxxx` → 整段换 ***（那是 base64 的用户名密码）✗
        ///   · `password=xxx` / `pwd=xxx` 之类 → ***
        /// ★ 为什么这里也要做（日志出口 Logger.Sanitize 已经做过）：
        ///   报文是**新加的出口** ✓ 它不经过日志 ✓ 所以必须自己兜 ✓
        ///   而且这份文件是**要发给别人的** ✗ 更不能漏 ✓
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
                    @"(?im)^(\s*Authorization\s*:\s*).*$", "${1}***（已脱敏）");
                s = Regex.Replace(s,
                    @"(?i)(password|passwd|pwd|token|secret|apikey|api_key)(\s*[=:]\s*)([^\s;&,，]+)",
                    "${1}${2}***");
                return s;
            }
            catch (Exception) { return s; }
        }
    }
}
