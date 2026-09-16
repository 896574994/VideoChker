/* -*- coding: utf-8 -*-
 * Secret.cs — 敏感信息保护（流地址里的摄像头账号密码）
 *
 * 问题背景：RTSP 地址形如 rtsp://admin:密码@192.168.1.64:554/...，
 * 明文存在 rtsp_streams.csv 里，等于把摄像头密码摊在文件夹里 ——
 * 拷贝程序文件夹、发报告、截图都可能把密码带出去。
 *
 * 措施：
 *   ① 流列表改用 Windows DPAPI 加密存储（rtsp_streams.dat）。
 *      DPAPI 用当前 Windows 用户的凭据加密，换台电脑/换个用户都解不开，
 *      比自定义加密安全得多，且不需要用户记额外密码。
 *   ② 界面上、报告里显示的地址一律把密码替换成 ***。
 *   ③ 仍支持从明文 CSV 导入（方便批量添加），但导入后不再保留明文。
 *
 * C# 5 兼容语法。需要引用 System.Security.dll。
 */
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    public static class Secret
    {
        /// <summary>rtsp://user:pass@host → rtsp://user:***@host；没有密码则原样返回。</summary>
        private static readonly Regex _urlAuth = new Regex(
            @"^(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<user>[^:/@\s]+):(?<pass>[^@/\s]*)@(?<rest>.+)$");

        /// <summary>把地址里的密码替换成 ***，用于界面显示与报告输出。</summary>
        public static string MaskUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            Match m = _urlAuth.Match(url.Trim());
            if (!m.Success) return url;
            return m.Groups["scheme"].Value + m.Groups["user"].Value + ":***@" + m.Groups["rest"].Value;
        }

        /// <summary>取出地址里的密码（用于判断"这条地址是否含明文密码"）。</summary>
        public static string ExtractPassword(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            Match m = _urlAuth.Match(url.Trim());
            return m.Success ? m.Groups["pass"].Value : "";
        }

        // ==================== DPAPI 加解密 ====================

        /// <summary>
        /// 用 Windows DPAPI 加密（当前用户范围）。
        /// 换电脑或换 Windows 用户都无法解密 —— 这是有意的：即使文件夹被拷走也读不出密码。
        /// </summary>
        public static byte[] Protect(string plain)
        {
            if (plain == null) plain = "";
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null,
                DataProtectionScope.CurrentUser);
        }

        /// <summary>解密。失败（换用户/换机器/文件损坏）返回 null，由调用方决定怎么处理。</summary>
        public static string Unprotect(byte[] cipher)
        {
            if (cipher == null || cipher.Length == 0) return null;
            try
            {
                byte[] raw = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch (Exception ex)
            {
                Log.Warn("解密失败（可能是换了 Windows 用户或机器）：" + ex.Message);
                return null;
            }
        }

        /// <summary>加密写入文件（先写临时文件再替换，避免中途失败把原文件写坏）。</summary>
        public static bool ProtectToFile(string path, string plain)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, Protect(plain));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("加密写文件失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>从文件读取并解密，失败返回 null。</summary>
        public static string UnprotectFromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Unprotect(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                Log.Warn("读取加密文件失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>把一个文件彻底抹掉（先覆写随机字节再删，降低被恢复的可能）。</summary>
        public static void ShredFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                FileInfo fi = new FileInfo(path);
                long len = fi.Length;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write))
                {
                    byte[] junk = new byte[Math.Min(len, 65536)];
                    new Random().NextBytes(junk);
                    long left = len;
                    while (left > 0)
                    {
                        int n = (int)Math.Min(left, junk.Length);
                        fs.Write(junk, 0, n);
                        left -= n;
                    }
                    fs.Flush();
                }
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn("抹除明文文件失败：" + ex.Message);
                try { File.Delete(path); } catch { }
            }
        }
    }
}
