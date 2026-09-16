/* -*- coding: utf-8 -*-
 * SsdpClient.cs — SSDP / UPnP 设备发现 + 常见 RTSP 路径自动探测
 *
 * ★ 为什么加这个（2026-09-13 用户要求「有个 SSDP 功能可以主动探测网络里的摄像头广播」）
 *
 *   ONVIF 的 WS-Discovery 只能发现**支持 ONVIF** 的摄像头 ✗
 *   很多消费级摄像头（小米、萤石、部分 TP-Link）**没开 ONVIF** ✓ 但支持 UPnP ✓
 *   SSDP 就是 UPnP 的发现协议 ✓ 覆盖面更广 ✓
 *
 *   两者的分工：
 *     · ONVIF WS-Discovery → 设备少，但**能直接问出取流地址** ✓✓
 *     · SSDP / UPnP        → 设备多，但**只知道"这里有个设备"** ✗
 *
 *   所以 SSDP 发现之后**必须再来一步**：自动挨个试常见的 RTSP 路径 ✓
 *   → 这就是把"我手工帮用户找 TP-Link 的 /stream1"那件事**自动化** ✓
 *     （实测各家的路径完全不一样：海康 /Streaming/Channels/101 ✓
 *       TP-Link /stream1 ✓ 大华 /cam/realmonitor ✓ 用户根本记不住 ✓）
 *
 * C# 5 兼容语法。零第三方依赖（纯 Socket + HttpWebRequest）✓
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VideoChecker
{
    /// <summary>发现到的网络设备（SSDP 或 ONVIF）。</summary>
    public class NetDevice
    {
        public string Ip = "";
        public string Source = "";         // "ONVIF" / "SSDP"
        public string Server = "";         // 设备自报的服务名（常带厂商型号）
        public string Location = "";       // UPnP 描述文件地址
        public string FriendlyName = "";   // 从描述文件里读到的友好名
        public string Model = "";
        public string Manufacturer = "";
        public string ServiceUrl = "";     // ONVIF 设备服务地址

        /// <summary>自动探测到的可用 RTSP 地址（探测完才有）。</summary>
        public string RtspUrl = "";
        public bool Probing;
        public string ProbeNote = "";

        public string Title
        {
            get
            {
                string s = Ip;
                if (Source.Length > 0) s = "[" + Source + "] " + s;
                return s;
            }
        }

        public string Info
        {
            get
            {
                List<string> bits = new List<string>();
                if (FriendlyName.Length > 0) bits.Add(FriendlyName);
                if (Manufacturer.Length > 0 || Model.Length > 0)
                    bits.Add((Manufacturer + " " + Model).Trim());
                if (bits.Count == 0 && Server.Length > 0) bits.Add(Server);
                if (Probing) bits.Add("正在探测取流地址…");
                else if (RtspUrl.Length > 0) bits.Add("✓ " + RtspUrl.Replace("://", "://").Substring(0, Math.Min(60, RtspUrl.Length)));
                else if (ProbeNote.Length > 0) bits.Add(ProbeNote);
                return string.Join("  ", bits.ToArray());
            }
        }

        public string Display { get { return Title + "    " + Info; } }
    }

    public static class SsdpClient
    {
        /// <summary>
        /// SSDP M-SEARCH：往 239.255.255.250:1900 发搜索请求，收设备应答。
        /// **不需要账号密码** ✓ 就是"喊一声看谁答应" ✓
        /// </summary>
        public static List<NetDevice> Discover(int timeoutMs)
        {
            List<NetDevice> found = new List<NetDevice>();
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                udp.Client.ReceiveTimeout = 500;
                udp.MulticastLoopback = false;

                // 搜几种常见的设备类型（不限定类型也发一条，覆盖面更广）
                string[] searches = new string[] {
                    "ssdp:all",
                    "urn:schemas-upnp-org:device:Basic:1",
                    "urn:schemas-upnp-org:device:DigitalSecurityCamera:1",
                    "urn:schemas-upnp-org:device:DigitalSecurityCamera:2"
                };
                IPEndPoint target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (string st in searches)
                {
                    string req =
                        "M-SEARCH * HTTP/1.1\r\n"
                      + "HOST: 239.255.255.250:1900\r\n"
                      + "MAN: \"ssdp:discover\"\r\n"
                      + "MX: 2\r\n"
                      + "ST: " + st + "\r\n"
                      + "\r\n";
                    byte[] b = Encoding.ASCII.GetBytes(req);
                    try { udp.Send(b, b.Length, target); } catch (Exception) { }
                }

                DateTime t0 = DateTime.Now;
                while ((DateTime.Now - t0).TotalMilliseconds < timeoutMs)
                {
                    try
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] rep = udp.Receive(ref from);
                        string text = Encoding.ASCII.GetString(rep);
                        string ip = from.Address.ToString();

                        NetDevice d = new NetDevice();
                        d.Ip = ip;
                        d.Source = "SSDP";
                        d.Location = Header(text, "LOCATION");
                        d.Server = Header(text, "SERVER");

                        // 同 IP 只留一条（SSDP 会因为多种 ST 重复应答）
                        bool dup = false;
                        foreach (NetDevice e in found) if (e.Ip == ip) { dup = true; break; }
                        if (!dup) found.Add(d);
                    }
                    catch (SocketException) { }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            finally { try { if (udp != null) udp.Close(); } catch (Exception) { } }

            // 读描述文件补齐厂商/型号（让列表看着友好一点）
            foreach (NetDevice d in found) FillFromDescription(d);
            return found;
        }

        private static string Header(string text, string name)
        {
            if (text == null) return "";
            Match m = Regex.Match(text, "^" + name + ":\\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        /// <summary>读 UPnP 描述 XML，取友好名/厂商/型号。</summary>
        private static void FillFromDescription(NetDevice d)
        {
            if (d.Location == null || d.Location.Length == 0) return;
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(d.Location);
                r.Timeout = 4000; r.ReadWriteTimeout = 4000; r.KeepAlive = false;
                using (WebResponse resp = r.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    string xml = sr.ReadToEnd();
                    d.FriendlyName = Tag(xml, "friendlyName");
                    d.Manufacturer = Tag(xml, "manufacturer");
                    d.Model = Tag(xml, "modelName");
                    if (d.Model.Length == 0) d.Model = Tag(xml, "modelNumber");
                }
            }
            catch (Exception) { }
        }

        private static string Tag(string xml, string name)
        {
            if (xml == null) return "";
            Match m = Regex.Match(xml, "<" + name + ">(.*?)</" + name + ">",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // ==================== 常见 RTSP 路径自动探测 ====================

        /// <summary>
        /// 各家摄像头的 RTSP 路径**完全不一样** ✗ 用户记不住也猜不到 ✓
        /// 这里按"常见程度"排序挨个试 ✓ 试通一个就停 ✓
        /// 实测依据：
        ///   海康   /Streaming/Channels/101  ✓
        ///   TP-Link /stream1                ✓
        ///   大华   /cam/realmonitor?channel=1&amp;subtype=0  ✓
        /// </summary>
        private static readonly string[] CommonPaths = new string[] {
            "/stream1",
            "/Streaming/Channels/101",
            "/cam/realmonitor?channel=1&subtype=0",
            "/h264/ch1/main/av_stream",
            "/onvif1",
            "/live/ch0",
            "/ch01/0",
            "/11",
            "/av0_0",
            "/media/video1"
        };

        /// <summary>
        /// 对一台设备自动试出可用的 RTSP 地址。
        /// 用 ffprobe 试（每个给 4 秒），试通就返回带账号密码的完整地址 ✓
        /// </summary>
        public static string ProbeRtsp(string ip, string user, string pwd, out string note)
        {
            note = "";
            // 先看 554 端口通不通，不通就别白试了
            if (!PortOpen(ip, 554, 800))
            {
                note = "554 端口不通（可能不是 RTSP 摄像头，或不在同一网段）";
                return null;
            }

            // ★★ 一遇 401 立刻停（2026-09-13 修，这条很关键）
            //
            //   原来不管返回什么码都继续试下一个路径 ✗
            //   401 = **认证失败** ✓ 说明"密码不对"，不是"路径不对" ✓
            //   继续拿错密码试剩下 9 个路径 = **又发 9 次失败登录** ✗
            //   而很多摄像头（实测 TP-Link 就是）**连续认证失败会把账号临时锁掉** ✓✓
            //   我第一次测试时用空密码 + 错密码各试了一遍 ✓ 20 次失败 ✓
            //   结果摄像头把账号锁了 ✓ 之后正确密码也返回 401 ✓
            //
            //   所以：401 就停下来 ✓ 只提示"密码不对" ✓ 绝不再试 ✓
            foreach (string path in CommonPaths)
            {
                string url = "rtsp://" + Uri.EscapeDataString(user) + ":" + Uri.EscapeDataString(pwd == null ? "" : pwd)
                           + "@" + ip + ":554" + path;
                int code;
                if (RtspDescribe(url, 900, 900, out code))
                    return url;
                if (code == 401)
                {
                    note = "账号或密码不对（摄像头返回 401）。"
                         + "注意：连续认证失败有些摄像头会临时锁号，请确认密码后再试，别反复试。";
                    return null;                       // ★ 立刻停，不再试别的路径
                }
                // 404 = Stream Not Found → 路径不对 ✓ 继续试下一个 ✓
            }
            note = "常见路径都试过了，这台可能要用别的地址（可在摄像头网页里查）";
            return null;
        }

        private static bool PortOpen(string ip, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 试一个地址能不能出流。
        ///
        /// ★ 用**轻量 RTSP DESCRIBE** 而不是起 ffprobe ✗（2026-09-13）
        ///   一开始想用 ffprobe ✓ 但它是 211MB 的静态文件 ✗ 每试一个路径就启一个进程 ✓
        ///   10 个路径要 10~40 秒 ✓ 用户等不起 ✓
        ///   改成纯 TCP 发一个 DESCRIBE 请求 ✓ **每个路径 300~500ms** ✓ 不启任何进程 ✓
        ///   还能顺便支持 RTSP 层的 Digest 认证（很多摄像头要 ✓）
        /// </summary>
        private static bool TryUrl(string url)
        {
            int code;
            return RtspDescribe(url, 900, 900, out code);
        }

        /// <summary>
        /// 极简 RTSP 客户端：发 DESCRIBE，看返回里有没有视频流。
        /// 401 时会按 Digest 算一次重发 ✓（RTSP 的 Digest 和 HTTP 的一样 ✓）
        /// </summary>
        private static bool RtspDescribe(string url, int connectMs, int readMs, out int code)
        {
            code = 0;
            Uri u;
            try { u = new Uri(url); } catch (Exception) { return false; }
            if (u.Scheme.ToLower() != "rtsp") return false;

            string userInfo = "";
            try { userInfo = u.UserInfo; } catch (Exception) { }
            string path = u.PathAndQuery;
            if (path.Length == 0) path = "/";

            // ★★ 关键：DESCRIBE 的请求行和 Digest 的 uri 都必须是**不带账号密码**的地址
            //   （2026-09-13 修，这是之前一直返回 401 的真正原因 ✗）
            //   我把 url 直接拼进请求行 ✗ 它是 rtsp://admin:pwd@ip:554/stream1 ✓
            //   而摄像头算 digest 用的是 rtsp://ip:554/stream1 ✓ 两者不一样 ✓
            //   → 签名永远对不上 → 永远 401 ✓✓
            string clean = "rtsp://" + u.Host + (u.Port > 0 ? ":" + u.Port : "") + path;

            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(u.Host, u.Port <= 0 ? 554 : u.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(connectMs)) return false;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = readMs;
                    c.SendTimeout = readMs;
                    using (NetworkStream ns = c.GetStream())
                    {
                        string req =
                            "DESCRIBE " + clean + " RTSP/1.0\r\n"
                          + "CSeq: 1\r\n"
                          + "Accept: application/sdp\r\n"
                          + "User-Agent: VideoChecker\r\n"
                          + "\r\n";
                        byte[] b = Encoding.ASCII.GetBytes(req);
                        ns.Write(b, 0, b.Length);
                        string resp = ReadResponse(ns);
                        if (resp.Length == 0) return false;
                        code = Code(resp);

                        if (resp.StartsWith("RTSP/1.0 200") || resp.IndexOf(" 200 OK") >= 0)
                            return resp.IndexOf("m=video", StringComparison.OrdinalIgnoreCase) >= 0
                                || resp.IndexOf("m=audio", StringComparison.OrdinalIgnoreCase) >= 0;

                        // 401 → 按 Digest 重发一次
                        if (resp.IndexOf("401") >= 0 && resp.IndexOf("Digest", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string auth = BuildDigest(resp, "DESCRIBE", clean, userInfo);
                            if (auth.Length == 0) return false;
                            string req2 =
                                "DESCRIBE " + clean + " RTSP/1.0\r\n"
                              + "CSeq: 2\r\n"
                              + "Accept: application/sdp\r\n"
                              + "Authorization: " + auth + "\r\n"
                              + "User-Agent: VideoChecker\r\n"
                              + "\r\n";
                            byte[] b2 = Encoding.ASCII.GetBytes(req2);
                            ns.Write(b2, 0, b2.Length);
                            string resp2 = ReadResponse(ns);
                            code = Code(resp2);
                            if (resp2.IndexOf(" 200") >= 0)
                                return resp2.IndexOf("m=video", StringComparison.OrdinalIgnoreCase) >= 0
                                    || resp2.IndexOf("m=audio", StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                        return false;
                    }
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>从 RTSP 应答里取状态码（如 "RTSP/1.0 401 Unauthorized" → 401）。</summary>
        private static int Code(string resp)
        {
            try
            {
                Match m = Regex.Match(resp ?? "", @"RTSP/1\.0\s+(\d{3})");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch (Exception) { }
            return 0;
        }

        private static string ReadResponse(NetworkStream ns)
        {
            MemoryStream m = new MemoryStream();
            byte[] buf = new byte[4096];
            DateTime t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalMilliseconds < 1500)
            {
                int n;
                try { n = ns.Read(buf, 0, buf.Length); }
                catch (Exception) { break; }
                if (n <= 0) break;
                m.Write(buf, 0, n);
                string sofar = Encoding.ASCII.GetString(m.ToArray());
                if (sofar.IndexOf("\r\n\r\n") >= 0) break;      // 头收完了
            }
            return Encoding.ASCII.GetString(m.ToArray());
        }

        /// <summary>
        /// 按 RTSP/HTTP Digest 规则算 Authorization 头。
        /// HA1 = MD5(user:realm:pass) ✓ HA2 = MD5(method:uri) ✓
        /// response = MD5(HA1:nonce:HA2) ✓（qop 存在时是 MD5(HA1:nonce:nc:cnonce:qop:HA2) ✓）
        /// </summary>
        private static string BuildDigest(string resp, string method, string uri, string userInfo)
        {
            if (userInfo.Length == 0) return "";
            int c = userInfo.IndexOf(':');
            string user = c > 0 ? userInfo.Substring(0, c) : userInfo;
            string pwd = c > 0 ? userInfo.Substring(c + 1) : "";
            try { user = Uri.UnescapeDataString(user); } catch (Exception) { }
            try { pwd = Uri.UnescapeDataString(pwd); } catch (Exception) { }

            string realm = Hdr(resp, "realm");
            string nonce = Hdr(resp, "nonce");
            string qop = Hdr(resp, "qop");
            if (realm.Length == 0 || nonce.Length == 0) return "";

            string ha1 = Md5(user + ":" + realm + ":" + pwd);
            string ha2 = Md5(method + ":" + uri);
            string nc = "00000001";
            string cnonce = Guid.NewGuid().ToString("N").Substring(0, 8);
            string digest;
            if (qop.Length > 0 && qop.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
                digest = Md5(ha1 + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + ha2);
            else
            {
                digest = Md5(ha1 + ":" + nonce + ":" + ha2);
                nc = ""; cnonce = "";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("Digest username=\"").Append(user).Append("\", realm=\"").Append(realm)
              .Append("\", nonce=\"").Append(nonce).Append("\", uri=\"").Append(uri)
              .Append("\", response=\"").Append(digest).Append("\"");
            if (nc.Length > 0) sb.Append(", qop=auth, nc=").Append(nc).Append(", cnonce=\"").Append(cnonce).Append("\"");
            return sb.ToString();
        }

        private static string Hdr(string resp, string name)
        {
            Match m = Regex.Match(resp, name + "=\"?([^\",\\r\\n]+)\"?", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static string Md5(string s)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.ASCII.GetBytes(s));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>把 ONVIF 发现的结果并进来（同一个 IP 只留 ONVIF 那条，信息更全）。</summary>
        public static List<NetDevice> Merge(List<NetDevice> ssdp, List<OnvifDevice> onvif)
        {
            List<NetDevice> all = new List<NetDevice>();
            if (ssdp != null) all.AddRange(ssdp);
            if (onvif != null)
            {
                foreach (OnvifDevice o in onvif)
                {
                    bool merged = false;
                    foreach (NetDevice d in all)
                    {
                        if (d.Ip == o.Host)
                        {
                            d.Source = "ONVIF+SSDP";
                            d.ServiceUrl = o.Address;
                            if (o.Model.Length > 0 && d.Model.Length == 0) d.Model = o.Model;
                            merged = true;
                            break;
                        }
                    }
                    if (!merged)
                    {
                        NetDevice d = new NetDevice();
                        d.Ip = o.Host;
                        d.Source = "ONVIF";
                        d.ServiceUrl = o.Address;
                        d.FriendlyName = o.Name;
                        d.Model = o.Model;
                        d.Manufacturer = o.Manufacturer;
                        all.Add(d);
                    }
                }
            }
            return all;
        }
    }
}
