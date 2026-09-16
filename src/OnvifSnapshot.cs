/* -*- coding: utf-8 -*-
 * OnvifSnapshot.cs — ONVIF 标准抓图（GetSnapshotUri）
 *
 * ★ 为什么单独一个文件（2026-09-13）
 *
 *   ONVIF 的事件**不带图片** ✗（PullMessages 只给 Topic 和时间 ✓）
 *   所以「收到事件 → 分析画面」中间必须有一步**抓图** ✓
 *
 *   抓图有两条路 ✗：
 *     ① ONVIF 标准接口 GetSnapshotUri  ← 本文件
 *     ② 厂商私有（海康 /ISAPI/Streaming/channels/101/picture）
 *
 *   **优先用 ①** ✓ 因为它是标准的 ✓ 不用判断厂商 ✓
 *   实测（2026-09-13）：
 *     海康   GetSnapshotUri → http://192.168.1.64/onvif-http/snapshot?Profile_1
 *            → 实测拿到 JPEG，4096 字节 ✓✓
 *     TP-Link GetSnapshotUri → **HTTP 500** ✗ 不支持 ✓
 *            私有 /ISAPI → 404 ✗ ONVIF 标准路径 → 404 ✗
 *            → **TP-Link 完全没有 HTTP 抓图** ✓ 只能 RTSP 抽帧 ✓
 *
 *   ★ 所以调用方**必须处理"抓图失败"** ✗
 *     不能假设"有 ONVIF 就有抓图" ✓ 那是错的 ✓
 *
 * C# 5 兼容语法。零第三方依赖 ✓
 */

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    public static class OnvifSnapshot
    {
        /// <summary>
        /// 问设备要抓图地址。
        /// 成功返回 URL（**不带账号密码** ✗ 调用方自己补 ✓）
        /// 失败返回空串 + err 说明原因 ✓
        /// </summary>
        public static string GetSnapshotUri(string mediaServiceUrl, string profileToken, string user, string pwd, out string err)
        {
            err = "";
            if (string.IsNullOrEmpty(mediaServiceUrl))
            {
                err = "没有媒体服务地址";
                return "";
            }
            if (string.IsNullOrEmpty(profileToken))
            {
                err = "没有 Profile token";
                return "";
            }

            string body =
                "<trt:GetSnapshotUri>"
              + "<trt:ProfileToken>" + profileToken + "</trt:ProfileToken>"
              + "</trt:GetSnapshotUri>";

            string r;
            int code;
            r = Soap(mediaServiceUrl, "http://www.onvif.org/ver10/media/wsdl/GetSnapshotUri", body, user, pwd, out code);

            if (code == 0)
            {
                err = "连不上媒体服务";
                return "";
            }
            if (code != 200)
            {
                // ★ 实测 TP-Link 这里是 500 ✓ 该设备就是不支持 ✓
                err = "设备不支持 ONVIF 抓图（HTTP " + code + "）";
                return "";
            }

            string uri = Tag(r, "Uri");
            if (uri.Length == 0)
            {
                err = "设备没有返回抓图地址";
                return "";
            }
            return XmlUnescape(uri);
        }

        /// <summary>
        /// 直接抓到一张 JPEG。**内部会补齐账号密码** ✓
        /// 成功返回图片字节 ✓ 失败返回 null + err ✓
        /// </summary>
        public static byte[] Grab(string snapshotUrl, string user, string pwd, out string err)
        {
            err = "";
            if (string.IsNullOrEmpty(snapshotUrl)) { err = "抓图地址为空"; return null; }

            string url = AddCredential(snapshotUrl, user, pwd);
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "GET";
                r.Timeout = 8000;
                r.ReadWriteTimeout = 8000;
                r.Proxy = null;
                r.KeepAlive = false;
                // ★ 用基本认证（很多设备抓图接口只认这个 ✗ 不是 ONVIF 那套 WS-Security）
                int ss = url.IndexOf("://");
                int at = url.IndexOf('@');
                if (ss > 0 && at > ss)
                {
                    string ui = url.Substring(ss + 3, at - ss - 3);
                    int c = ui.IndexOf(':');
                    if (c > 0)
                    {
                        string u = Uri.UnescapeDataString(ui.Substring(0, c));
                        string p = Uri.UnescapeDataString(ui.Substring(c + 1));
                        r.Credentials = new NetworkCredential(u, p);
                        r.PreAuthenticate = true;
                    }
                }

                using (WebResponse resp = r.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[16384];
                    int n;
                    int total = 0;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        total += n;
                        if (total > 8 * 1024 * 1024) break;   // 上限 8MB，防止异常设备刷爆内存
                    }
                    byte[] all = ms.ToArray();
                    if (all.Length < 4 || all[0] != 0xFF || all[1] != 0xD8)
                    {
                        err = "抓回来的不是 JPEG（" + all.Length + " 字节）";
                        return null;
                    }
                    return all;
                }
            }
            catch (WebException we)
            {
                int code = -1;
                try { if (we.Response != null) code = (int)((HttpWebResponse)we.Response).StatusCode; }
                catch (Exception) { }
                if (code == 401 || code == 403) err = "抓图认证失败（HTTP " + code + "）";
                else if (code > 0) err = "抓图被拒（HTTP " + code + "）";
                else err = "抓图连不上（" + we.Status + "）";
                return null;
            }
            catch (Exception e)
            {
                string m = "";
                try { m = e.Message; } catch (Exception) { }
                err = "抓图出错：" + m;
                return null;
            }
        }

        // ==================== 工具 ====================

        private static string Soap(string url, string action, string body, string user, string pwd, out int code)
        {
            code = 0;
            string env =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
              + "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\""
              + " xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\""
              + " xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\""
              + " xmlns:tt=\"http://www.onvif.org/ver10/schema\""
              + " xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\""
              + " xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">"
              + "<s:Header>" + Security(user, pwd) + "</s:Header>"
              + "<s:Body>" + body + "</s:Body></s:Envelope>";
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "POST";
                r.ContentType = "application/soap+xml; charset=utf-8; action=\"" + action + "\"";
                r.Timeout = 8000; r.ReadWriteTimeout = 8000; r.KeepAlive = false; r.Proxy = null;
                byte[] b = Encoding.UTF8.GetBytes(env);
                r.ContentLength = b.Length;
                using (Stream s = r.GetRequestStream()) s.Write(b, 0, b.Length);
                using (WebResponse resp = r.GetResponse())
                {
                    code = (int)((HttpWebResponse)resp).StatusCode;
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                if (we.Response != null)
                {
                    try
                    {
                        code = (int)((HttpWebResponse)we.Response).StatusCode;
                        using (Stream s = we.Response.GetResponseStream())
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
                    }
                    catch (Exception) { }
                }
                return "";
            }
            catch (Exception) { return ""; }
        }

        private static string Security(string user, string pwd)
        {
            if (user == null) user = "";
            if (pwd == null) pwd = "";
            byte[] nonce = new byte[16];
            new Random(Guid.NewGuid().GetHashCode()).NextBytes(nonce);
            string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            byte[] cb = Encoding.UTF8.GetBytes(created);
            byte[] pb = Encoding.UTF8.GetBytes(pwd);
            byte[] buf = new byte[nonce.Length + cb.Length + pb.Length];
            Buffer.BlockCopy(nonce, 0, buf, 0, nonce.Length);
            Buffer.BlockCopy(cb, 0, buf, nonce.Length, cb.Length);
            Buffer.BlockCopy(pb, 0, buf, nonce.Length + cb.Length, pb.Length);
            string digest;
            using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
                digest = Convert.ToBase64String(sha.ComputeHash(buf));
            return "<wsse:Security s:mustUnderstand=\"1\"><wsse:UsernameToken>"
                 + "<wsse:Username>" + Xml(user) + "</wsse:Username>"
                 + "<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" + digest + "</wsse:Password>"
                 + "<wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" + Convert.ToBase64String(nonce) + "</wsse:Nonce>"
                 + "<wsu:Created>" + created + "</wsu:Created>"
                 + "</wsse:UsernameToken></wsse:Security>";
        }

        private static string Tag(string xml, string name)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            Match m = Regex.Match(xml,
                "<(?:[A-Za-z0-9_]+:)?" + name + "[^>]*>([^<]*)</(?:[A-Za-z0-9_]+:)?" + name + ">",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static string Xml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string XmlUnescape(string s)
        {
            if (s == null) return "";
            return s.Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&apos;", "'")
                    .Replace("&amp;", "&");
        }

        /// <summary>给 URL 补上 user:pass@（已经有了就不动）。</summary>
        public static string AddCredential(string url, string user, string pwd)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (string.IsNullOrEmpty(user)) return url;
            int ss = url.IndexOf("://");
            if (ss < 0) return url;
            string rest = url.Substring(ss + 3);
            if (rest.IndexOf('@') >= 0) return url;     // 已经有凭据了
            return url.Substring(0, ss + 3)
                 + Uri.EscapeDataString(user) + ":" + Uri.EscapeDataString(pwd) + "@" + rest;
        }
    }
}
