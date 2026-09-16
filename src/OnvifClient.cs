/* -*- coding: utf-8 -*-
 * OnvifClient.cs — ONVIF 设备发现 + 取流地址查询
 *
 * ★ 为什么加这个（2026-09-13 用户要求「加个 ONVIF 取流方式，这样更友好一些」）
 *
 *   现在每换一个牌子的摄像头，用户都要自己去查 RTSP 路径 ✗
 *   实测：海康是 /Streaming/Channels/101 ✓ TP-Link 是 /stream1 ✓ 大华又是别的 ✓
 *   路径不对就 404「Stream Not Found」✗ 用户根本不知道该怎么填 ✓
 *
 *   ONVIF 是摄像机行业的通用标准 ✓ 支持它的摄像头**自己知道**取流地址是什么 ✓
 *   程序问一句就行 ✓ 用户不用再记任何路径 ✓
 *
 * ★ 和 HCNetSDK 的区别：ONVIF 是**开放标准** ✓ 纯 HTTP + SOAP ✓
 *   不需要任何厂商 DLL ✓ 符合本项目「零第三方依赖」的底线 ✓
 *
 * 做了什么：
 *   1. Discover()      —— WS-Discovery（UDP 组播 239.255.255.250:3702）找局域网里的 ONVIF 设备
 *   2. GetStreamUri()  —— 问设备要 RTSP 地址（自动带上正确的路径 ✓）
 *   3. GetDeviceInfo() —— 取厂商/型号，用于列表里显示
 *
 * 认证：ONVIF 用 **WS-Security UsernameToken + PasswordDigest** ✓
 *   digest = Base64( SHA1( nonce + created + password ) ) ✓
 *   （不是 HTTP Basic ✗ 也不是 HTTP Digest ✗ 是写在 SOAP 头里的 ✓）
 *
 * C# 5 兼容语法。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoChecker
{
    /// <summary>发现到的一台 ONVIF 设备。</summary>
    public class OnvifDevice
    {
        public string Address = "";        // http://192.168.1.10/onvif/device_service
        public string Host = "";           // 192.168.1.10
        public string XAddr = "";          // 设备服务地址（可能带端口）
        public string Manufacturer = "";
        public string Model = "";
        public string Name = "";

        public string Display
        {
            get
            {
                string s = Host;
                if (Name.Length > 0) s += "  " + Name;
                if (Manufacturer.Length > 0 || Model.Length > 0)
                    s += "  [" + (Manufacturer + " " + Model).Trim() + "]";
                return s;
            }
        }
    }

    public static class OnvifClient
    {
        // ==================== 设备发现 ====================

        /// <summary>
        /// WS-Discovery：往组播地址发一个 Probe，收集回应的设备。
        /// **不需要账号密码** ✓ 设备发现本身是匿名的 ✓
        /// </summary>
        public static List<OnvifDevice> Discover(int timeoutMs)
        {
            List<OnvifDevice> found = new List<OnvifDevice>();
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                udp.Client.ReceiveTimeout = 500;
                udp.EnableBroadcast = true;
                udp.MulticastLoopback = false;

                string msg =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                  + "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\""
                  + " xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\""
                  + " xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\""
                  + " xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">"
                  + "<e:Header>"
                  + "<w:MessageID>uuid:" + Guid.NewGuid().ToString() + "</w:MessageID>"
                  + "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>"
                  + "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>"
                  + "</e:Header>"
                  + "<e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>"
                  + "</e:Envelope>";
                byte[] data = Encoding.UTF8.GetBytes(msg);

                IPEndPoint mcast = new IPEndPoint(IPAddress.Parse("239.255.255.255"), 3702);
                try { udp.Send(data, data.Length, mcast); } catch (Exception) { }
                try { udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, 3702)); } catch (Exception) { }

                DateTime t0 = DateTime.Now;
                while ((DateTime.Now - t0).TotalMilliseconds < timeoutMs)
                {
                    try
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] rep = udp.Receive(ref from);
                        string xml = Encoding.UTF8.GetString(rep);
                        OnvifDevice d = ParseProbeMatch(xml, from.Address.ToString());
                        if (d != null && d.XAddr.Length > 0)
                        {
                            bool dup = false;
                            foreach (OnvifDevice e in found)
                                if (e.XAddr == d.XAddr) { dup = true; break; }
                            if (!dup) found.Add(d);
                        }
                    }
                    catch (SocketException) { /* 超时，继续等 */ }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            finally { try { if (udp != null) udp.Close(); } catch (Exception) { } }
            return found;
        }

        private static OnvifDevice ParseProbeMatch(string xml, string fromIp)
        {
            if (xml == null) return null;
            int i = xml.IndexOf("ProbeMatch", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            OnvifDevice d = new OnvifDevice();
            d.XAddr = Tag(xml, "XAddrs");
            if (d.XAddr.IndexOf(' ') > 0) d.XAddr = d.XAddr.Split(' ')[0];   // 多个地址取第一个
            d.Address = d.XAddr;
            try
            {
                Uri u = new Uri(d.XAddr);
                d.Host = u.Host;
            }
            catch (Exception) { d.Host = fromIp; }
            if (d.Host.Length == 0) d.Host = fromIp;
            return d;
        }

        // ==================== 取流地址 ====================

        /// <summary>
        /// 问设备要 RTSP 地址。**这就是加 ONVIF 的核心价值** ✓
        /// 用户不用再管"这台是 /stream1 还是 /Streaming/Channels/101" ✓
        /// </summary>
        public static string GetStreamUri(string serviceUrl, string user, string pwd, out string err)
        {
            err = "";
            try
            {
                // ① 先要媒体服务地址（很多设备不在 /onvif/device_service 上提供媒体服务）
                string media = serviceUrl;
                string cap = Soap(serviceUrl, "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
                    "<tds:GetCapabilities><tds:Category>Media</tds:Category></tds:GetCapabilities>",
                    user, pwd, null);
                string m = Tag(cap, "XAddr");
                if (m.Length > 0 && m.StartsWith("http")) media = m;

                // ② 要一个 profile
                string profs = Soap(media, "http://www.onvif.org/ver10/media/wsdl/GetProfiles",
                    "<trt:GetProfiles/>", user, pwd, null);
                string token = Attr(profs, "Profiles", "token");
                if (token.Length == 0) token = Tag(profs, "token");
                if (token.Length == 0)
                {
                    err = "设备没有返回可用的媒体配置（GetProfiles 为空）";
                    return null;
                }

                // ③ 要 RTSP 地址
                string body =
                    "<trt:GetStreamUri>"
                  + "<trt:StreamSetup>"
                  + "<tt:Stream>RTP-Unicast</tt:Stream>"
                  + "<tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport>"
                  + "</trt:StreamSetup>"
                  + "<trt:ProfileToken>" + token + "</trt:ProfileToken>"
                  + "</trt:GetStreamUri>";
                string r = Soap(media, "http://www.onvif.org/ver10/media/wsdl/GetStreamUri", body, user, pwd, null);
                string uri = Tag(r, "Uri");
                if (uri.Length == 0)
                {
                    err = "设备没有返回取流地址（GetStreamUri 为空）";
                    return null;
                }

                // ★ 两个必须处理的地方（2026-09-13 实测发现）：
                //   ① XML 转义要还原：海康返回的地址里是 &amp; ✗ 直接用会连不上 ✓
                //   ② **返回的地址不带账号密码** ✗
                //      海康: rtsp://192.168.1.64:554/…  （没有 user:pass）
                //      → 直接填进程序会因为没认证而拉不到流 ✓ 必须补上 ✓
                uri = XmlUnescape(uri);
                uri = AddCredential(uri, user, pwd);
                return uri;
            }
            catch (WebException we)
            {
                // ★ ONVIF 的失败要说清楚是什么失败（2026-09-13 用户实测反馈）
                //   用户看到「400」第一反应是"端口/协议不对" ✓
                //   实测（TP-Link 192.168.1.10，ONVIF 在非默认端口 2020）：
                //     账号 admin + 正确密码  → ✓ 拿到 rtsp://…:554/stream1
                //     账号 admin + **空密码** → ✗ HTTP 400
                //     账号 admin + 错误密码  → ✗ HTTP 400
                //   也就是说 **这台设备用 400 表示"凭据不对"** ✗ 而不是标准的 401 ✓
                //   所以不能只把状态码抛给用户 ✓ 得解释清楚 ✓
                int code = -1;
                try { if (we.Response != null) code = (int)((HttpWebResponse)we.Response).StatusCode; }
                catch (Exception) { }

                if (code == 400 || code == 401 || code == 403)
                {
                    if (string.IsNullOrEmpty(pwd))
                    {
                        err = "密码是空的（HTTP " + code + "）。这台设备用 " + code
                            + " 表示凭据不对 —— 不是端口或协议的问题。";
                    }
                    else
                    {
                        err = "认证失败（HTTP " + code + "）—— 账号或密码不对。"
                            + "注：有些设备（如 TP-Link）用 400 而不是 401 表示认证失败，"
                            + "看到 400 不代表端口或协议有问题。";
                    }
                }
                else if (code > 0)
                {
                    err = "设备返回 HTTP " + code + "（ONVIF 请求被拒绝）";
                }
                else
                {
                    err = "连不上 ONVIF 服务（" + we.Status + "）—— 确认端口对不对";
                }
                return null;
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 拿到「媒体服务地址」和「第一个 profile token」。
        /// ★ 抓图要用（GetSnapshotUri 需要 media + token）✓
        ///   2026-09-13 加：LiveAiForm.Onvif.cs 那边要抓图，但 GetStreamUri 内部
        ///   拿到的这两个东西没暴露出来 ✗ 与其在那边重写一遍 ✓
        ///   不如从这里正经暴露 ✓（重写一遍就会有两份"怎么找媒体服务"的逻辑 ✗ 迟早不一致 ✓）
        /// </summary>
        public static bool GetMediaAndProfile(string serviceUrl, string user, string pwd,
            out string media, out string token, out string err)
        {
            media = ""; token = ""; err = "";
            if (string.IsNullOrEmpty(serviceUrl)) { err = "没有设备服务地址"; return false; }
            try
            {
                // ① 媒体服务地址（很多设备不在 /onvif/device_service 上提供媒体服务）
                media = serviceUrl;
                string cap = Soap(serviceUrl, "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
                    "<tds:GetCapabilities><tds:Category>Media</tds:Category></tds:GetCapabilities>",
                    user, pwd, null);
                string m = Tag(cap, "XAddr");
                if (m.Length > 0 && m.StartsWith("http")) media = m;

                // ② 第一个 profile
                string profs = Soap(media, "http://www.onvif.org/ver10/media/wsdl/GetProfiles",
                    "<trt:GetProfiles/>", user, pwd, null);
                token = Attr(profs, "Profiles", "token");
                if (token.Length == 0) token = Tag(profs, "token");
                if (token.Length == 0) { err = "设备没有返回可用的媒体配置（GetProfiles 为空）"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                string mm = "";
                try { mm = ex.Message; } catch (Exception) { }
                err = ex.GetType().Name + " " + mm;
                return false;
            }
        }

        /// <summary>取厂商/型号（用于在列表里显示得友好一点）。</summary>
        public static void FillDeviceInfo(OnvifDevice d, string user, string pwd)
        {
            if (d == null || d.Address.Length == 0) return;
            try
            {
                string r = Soap(d.Address, "http://www.onvif.org/ver10/device/wsdl/GetDeviceInformation",
                    "<tds:GetDeviceInformation/>", user, pwd, null);
                d.Manufacturer = Tag(r, "Manufacturer");
                d.Model = Tag(r, "Model");
                d.Name = d.Model;
            }
            catch (Exception) { }
        }

        // ==================== SOAP 底层 ====================

        private static string Soap(string url, string action, string innerBody,
            string user, string pwd, string extraNs)
        {
            string env =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
              + "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\""
              + " xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\""
              + " xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\""
              + " xmlns:tt=\"http://www.onvif.org/ver10/schema\""
              + " xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\""
              + " xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">"
              + "<s:Header>"
              + Security(user, pwd)
              + "</s:Header>"
              + "<s:Body>" + innerBody + "</s:Body>"
              + "</s:Envelope>";

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/soap+xml; charset=utf-8; action=\"" + action + "\"";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.KeepAlive = false;
            byte[] body = Encoding.UTF8.GetBytes(env);
            req.ContentLength = body.Length;
            using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            // ★ 报文记录（2026-09-15 用户要求"抓包报文"）
            //   ONVIF 的失败以前只有一句"取流地址没试出来"✗
            //   而到底返回了 401 还是 404、SOAP Fault 说了什么，**没人知道** ✗
            //   ★ 请求正文里含 WS-Security 的 nonce/digest ✓ 它们**不是密码** ✓
            //     （是拿 nonce+时间+密码算出来的一次性摘要 ✗ 重放不了 ✓）
            //     但为了"这份文件要发给别人"这条，Mask 仍会把 Authorization 之类兜掉 ✓
            string soapResp;
            try
            {
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    soapResp = sr.ReadToEnd();
            }
            catch (WebException we)
            {
                // 失败也要留下现场 ✗ 而且失败时**更**需要 ✓
                string errBody = "";
                try
                {
                    if (we.Response != null)
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                            errBody = sr.ReadToEnd();
                }
                catch (Exception) { }
                NetTrace.Exchange("ONVIF " + action + " → " + url,
                    action + Environment.NewLine + NetTrace.Short(innerBody.Replace("\r", "").Replace("\n", " "), 300),
                    "出现异常：" + we.Message + Environment.NewLine + NetTrace.Short(errBody, 400));
                throw;
            }
            NetTrace.Exchange("ONVIF " + action + " → " + url,
                action + Environment.NewLine + NetTrace.Short(innerBody.Replace("\r", "").Replace("\n", " "), 300),
                "返回 " + soapResp.Length + " 字" + Environment.NewLine
                    + NetTrace.Short(soapResp.Replace("\r", "").Replace("\n", " "), 400));
            return soapResp;
        }

        /// <summary>
        /// WS-Security UsernameToken + PasswordDigest。
        /// digest = Base64( SHA1( nonce + created + password ) ) ✓
        /// 注意：nonce 和 created 用的是**原始字节/原字符串** ✓ 不是 Base64 之后的 ✓
        /// </summary>
        private static string Security(string user, string pwd)
        {
            if (user == null) user = "";
            if (pwd == null) pwd = "";
            byte[] nonce = new byte[16];
            new Random(Guid.NewGuid().GetHashCode()).NextBytes(nonce);
            string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            byte[] createdBytes = Encoding.UTF8.GetBytes(created);
            byte[] pwdBytes = Encoding.UTF8.GetBytes(pwd);
            byte[] buf = new byte[nonce.Length + createdBytes.Length + pwdBytes.Length];
            Buffer.BlockCopy(nonce, 0, buf, 0, nonce.Length);
            Buffer.BlockCopy(createdBytes, 0, buf, nonce.Length, createdBytes.Length);
            Buffer.BlockCopy(pwdBytes, 0, buf, nonce.Length + createdBytes.Length, pwdBytes.Length);

            string digest;
            using (SHA1 sha = SHA1.Create())
                digest = Convert.ToBase64String(sha.ComputeHash(buf));
            string nonceB64 = Convert.ToBase64String(nonce);

            return "<wsse:Security s:mustUnderstand=\"1\">"
                 + "<wsse:UsernameToken>"
                 + "<wsse:Username>" + Xml(user) + "</wsse:Username>"
                 + "<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">"
                 + digest + "</wsse:Password>"
                 + "<wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">"
                 + nonceB64 + "</wsse:Nonce>"
                 + "<wsu:Created>" + created + "</wsu:Created>"
                 + "</wsse:UsernameToken>"
                 + "</wsse:Security>";
        }

        /// <summary>还原 XML 实体（&amp; → & 等）。海康返回的地址里有 &amp; ✗ 不还原连不上 ✓</summary>
        private static string XmlUnescape(string s)
        {
            if (s == null) return "";
            return s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&apos;", "'");
        }

        /// <summary>
        /// 把 user:pass 插进取流地址（ONVIF 返回的地址是**不带凭据**的 ✗）。
        /// rtsp://1.2.3.4:554/path  →  rtsp://user:pass@1.2.3.4:554/path
        /// 已经有凭据的就不动 ✓
        /// </summary>
        private static string AddCredential(string uri, string user, string pwd)
        {
            if (uri == null || uri.Length == 0) return uri;
            if (user == null || user.Length == 0) return uri;
            int scheme = uri.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) return uri;
            string head = uri.Substring(0, scheme + 3);
            string rest = uri.Substring(scheme + 3);
            if (rest.IndexOf('@') >= 0) return uri;                    // 已带凭据
            int slash = rest.IndexOf('/');
            string hostPart = slash > 0 ? rest.Substring(0, slash) : rest;
            string tail = slash > 0 ? rest.Substring(slash) : "";
            // 账号密码里可能出现的字符要转义（ONVIF 的地址是要直接拿去连的）
            string u = Uri.EscapeDataString(user);
            string p = Uri.EscapeDataString(pwd == null ? "" : pwd);
            return head + u + ":" + p + "@" + hostPart + tail;
        }

        private static string Xml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string Tag(string xml, string name)
        {
            if (xml == null) return "";
            // 允许带命名空间前缀：<trt:Uri> 或 <Uri>
            Match m = Regex.Match(xml, "<(?:[A-Za-z0-9_]+:)?" + name + "[^>]*>([^<]*)</(?:[A-Za-z0-9_]+:)?" + name + ">",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static string Attr(string xml, string tag, string attr)
        {
            if (xml == null) return "";
            Match m = Regex.Match(xml, "<(?:[A-Za-z0-9_]+:)?" + tag + "[^>]*\\b" + attr + "=\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
    }
}
