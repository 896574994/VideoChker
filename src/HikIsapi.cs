/* -*- coding: utf-8 -*-
 * HikIsapi.cs — 海康 ISAPI 轻量封装（事件订阅 + HTTP 抓拍）
 *
 * 为什么用 ISAPI 而不是 HCNetSDK（2026-09-12，用户提议"画面变化推送"后调研）：
 *   · HCNetSDK 要带 ~50MB 厂商 DLL ✗ 会破坏本项目"零第三方依赖"的底线 ✓
 *   · ISAPI 是摄像头自带的 HTTP 接口 ✓ 纯 HttpWebRequest 就能用 ✓ 什么都不用装 ✓
 *   · 效果一样：挂一条 HTTP 长连接，有事件时摄像头**主动推**过来 ✓
 *
 * 实测机型：海康 DS-2CD7A47EWD-XZS / 固件 V5.5.801
 *   · Digest 认证通过 ✓
 *   · GET /ISAPI/Streaming/channels/101/picture       → 288KB 真 JPEG ✓
 *   · GET /ISAPI/Event/notification/alertStream       → HTTP 200 + multipart 长连接 ✓
 *
 * 用法（事件驱动巡检）：
 *   挂着 alertStream 长连接 → 收到 <EventNotificationAlert> → 抓一张快照 → 送 AI 分析
 *   **没有事件就什么都不做** → 摄像头上零 RTSP 会话、零轮询 ✓
 *
 * C# 5 兼容语法。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace VideoChecker
{
    /// <summary>从一次事件订阅里收到的一条事件。</summary>
    public class HikEvent
    {
        public string EventType = "";     // motion / linedetection / fielddetection / videoloss / tamperdetection …
        public string State = "";         // active / inactive
        public string ChannelId = "";
        public string Time = "";
        public string Raw = "";           // 原始 XML（排障用）

        /// <summary>事件的中文名，用于界面和日志。</summary>
        public string Chinese
        {
            get
            {
                string t = EventType.ToLower();
                if (t.IndexOf("linedetection") >= 0) return "越界侦测";
                if (t.IndexOf("fielddetection") >= 0) return "区域入侵";
                if (t.IndexOf("regionentrance") >= 0) return "进入区域";
                if (t.IndexOf("regionexiting") >= 0) return "离开区域";
                if (t.IndexOf("motion") >= 0) return "移动侦测";
                if (t.IndexOf("tamperdetection") >= 0) return "镜头遮挡";
                if (t.IndexOf("videoloss") >= 0) return "视频信号丢失";
                if (t.IndexOf("shelteralarm") >= 0) return "遮挡报警";
                if (t.IndexOf("scenechangedetection") >= 0) return "场景变化";
                if (t.IndexOf("audioexception") >= 0) return "音频异常";
                if (t.IndexOf("face") >= 0) return "人脸侦测";
                if (t.IndexOf("people") >= 0) return "人员检测";
                if (t.IndexOf("vehicle") >= 0) return "车辆检测";
                if (EventType.Length == 0) return "未知事件";
                return EventType;
            }
        }

        /// <summary>这类事件值不值得送去 AI 分析（心跳/失活之类没必要）。</summary>
        public bool WorthAnalyzing
        {
            get
            {
                if (State.Length > 0 && State.ToLower() != "active") return false;   // 只处理"发生"，不处理"结束"
                if (EventType.Length == 0) return false;
                return true;
            }
        }
    }

    public static class HikIsapi
    {
        /// <summary>地址解析结果。</summary>
        public class Target
        {
            public string Host = "";          // 192.168.1.64
            public string User = "admin";
            public string Password = "";
            public string Channel = "101";    // 101 = 主码流 / 102 = 子码流
            public bool Ok = false;
            public string Error = "";
        }

        /// <summary>
        /// 从用户填的地址推导出 ISAPI 要用的各部分。
        /// 接受这几种写法：
        ///   rtsp://admin:pwd@192.168.1.64:554/Streaming/Channels/101
        ///   http://admin:pwd@192.168.1.64/ISAPI/...
        ///   admin:pwd@192.168.1.64
        ///   192.168.1.64            （没有账号密码 → 报错提示）
        /// </summary>
        public static Target Parse(string url)
        {
            Target t = new Target();
            if (url == null) { t.Error = "地址为空"; return t; }
            string s = url.Trim();
            if (s.Length == 0) { t.Error = "地址为空"; return t; }

            int p = s.IndexOf("://");
            if (p > 0) s = s.Substring(p + 3);

            // 账号密码
            int at = s.IndexOf('@');
            if (at > 0)
            {
                string cred = s.Substring(0, at);
                s = s.Substring(at + 1);
                int c = cred.IndexOf(':');
                if (c > 0) { t.User = cred.Substring(0, c); t.Password = cred.Substring(c + 1); }
                else t.User = cred;
            }
            // 主机名（去掉 :554 和后面的路径）
            int slash = s.IndexOf('/');
            string hostPart = slash > 0 ? s.Substring(0, slash) : s;
            string path = slash > 0 ? s.Substring(slash) : "";
            int colon = hostPart.IndexOf(':');
            if (colon > 0) hostPart = hostPart.Substring(0, colon);
            t.Host = hostPart;

            // 通道号：从路径里找 Channels/1xx
            int ci = path.IndexOf("Channels/", StringComparison.OrdinalIgnoreCase);
            if (ci >= 0)
            {
                string rest = path.Substring(ci + 9);
                int e = rest.IndexOf('/');
                string ch = e > 0 ? rest.Substring(0, e) : rest;
                if (ch.Length >= 3) t.Channel = ch.Substring(0, 3);
            }
            if (t.Host.Length == 0) { t.Error = "地址里看不出摄像头 IP"; return t; }
            if (t.Password.Length == 0) { t.Error = "地址里没有账号密码（要写成 rtsp://用户:密码@IP:554/…）"; return t; }
            t.Ok = true;
            return t;
        }

        private static ICredentials Cred(Target t)
        {
            CredentialCache cc = new CredentialCache();
            Uri u = new Uri("http://" + t.Host + "/");
            NetworkCredential nc = new NetworkCredential(t.User, t.Password);
            cc.Add(u, "Digest", nc);
            cc.Add(u, "Basic", nc);
            return cc;
        }

        /// <summary>HTTP 抓拍一张 JPEG（**不建 RTSP 会话**，对摄像头最省）。失败返回 null。</summary>
        public static byte[] Snapshot(Target t, int timeoutMs)
        {
            if (t == null || !t.Ok) return null;
            string url = "http://" + t.Host + "/ISAPI/Streaming/channels/" + t.Channel + "/picture";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Credentials = Cred(t);
                req.PreAuthenticate = true;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Method = "GET";
                using (WebResponse r = req.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (MemoryStream m = new MemoryStream())
                {
                    byte[] buf = new byte[16384];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) m.Write(buf, 0, n);
                    byte[] b = m.ToArray();
                    // 校验是不是 JPEG（防止拿到一页 HTML 错误页）
                    if (b.Length < 512) return null;
                    if (!(b[0] == 0xFF && b[1] == 0xD8)) return null;
                    return b;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 挂上事件流长连接并一直读，每读到一条完整事件就回调一次。
        /// **阻塞**，调用方要放在后台线程 ✓ 断开会自动重连 ✓
        /// </summary>
        /// <param name="t">摄像头</param>
        /// <param name="stop">返回 true 就退出</param>
        /// <param name="onEvent">收到一条事件时回调（在**当前线程**上执行，注意别做耗时操作）</param>
        /// <param name="onStatus">状态文字变化时回调（连接中/已连接/断线重连…）</param>
        public static void SubscribeLoop(Target t, Func<bool> stop, Action<HikEvent> onEvent, Action<string> onStatus)
        {
            int backoff = 1000;
            while (!stop())
            {
                Stream stream = null;
                WebResponse resp = null;
                try
                {
                    if (onStatus != null) onStatus("正在连接事件流…");
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(
                        "http://" + t.Host + "/ISAPI/Event/notification/alertStream");
                    req.Credentials = Cred(t);
                    req.PreAuthenticate = true;
                    req.Timeout = 8000;
                    req.ReadWriteTimeout = 30000;
                    req.KeepAlive = true;
                    req.Accept = "multipart/x-mixed-replace, application/xml, text/xml, */*";
                    resp = req.GetResponse();
                    stream = resp.GetResponseStream();
                    stream.ReadTimeout = 30000;
                    backoff = 1000;
                    if (onStatus != null) onStatus("事件流已连接（有事件会自动分析）");

                    // 逐块读，按 </EventNotificationAlert> 切分事件
                    byte[] buf = new byte[8192];
                    StringBuilder acc = new StringBuilder();
                    while (!stop())
                    {
                        int n;
                        try { n = stream.Read(buf, 0, buf.Length); }
                        catch (IOException) { n = 0; }            // 读超时/断开 → 触发重连
                        if (n <= 0) break;
                        acc.Append(Encoding.UTF8.GetString(buf, 0, n));
                        if (acc.Length > 200000) acc.Remove(0, acc.Length - 100000);   // 防爆内存

                        string all = acc.ToString();
                        int from = 0;
                        while (true)
                        {
                            int st = all.IndexOf("<EventNotificationAlert", from, StringComparison.OrdinalIgnoreCase);
                            if (st < 0) break;
                            int en = all.IndexOf("</EventNotificationAlert>", st, StringComparison.OrdinalIgnoreCase);
                            if (en < 0) break;
                            en += "</EventNotificationAlert>".Length;
                            string block = all.Substring(st, en - st);
                            HikEvent ev = ParseEvent(block);
                            if (ev != null && onEvent != null) onEvent(ev);
                            from = en;
                        }
                        if (from > 0) acc.Remove(0, from);
                    }
                    if (onStatus != null && !stop()) onStatus("事件流断开，1 秒后重连…");
                }
                catch (Exception ex)
                {
                    if (onStatus != null && !stop()) onStatus("事件流异常（" + Short(ex.Message) + "），" + (backoff / 1000) + " 秒后重连…");
                }
                finally
                {
                    try { if (stream != null) stream.Close(); } catch (Exception) { }
                    try { if (resp != null) resp.Close(); } catch (Exception) { }
                }
                if (stop()) break;
                // 退避重连（1s → 2s → 4s → 最多 8s），避免摄像头没开时疯狂重试
                for (int i = 0; i < backoff / 200 && !stop(); i++) System.Threading.Thread.Sleep(200);
                if (backoff < 8000) backoff *= 2;
            }
        }

        /// <summary>
        /// 把摄像头的「报警主机 / 上传中心」地址自动设成本机。
        ///
        /// ★ 为什么可以自动做（2026-09-13）：
        ///   实测这台海康的 alertStream **只推异常类事件** ✗ 移动侦测走的是
        ///   「联动方式 → 上传中心」= 摄像头主动 POST 到报警主机 ✓
        ///   而三个上传目标的 ipAddress 全是 0.0.0.0 ✓ **摄像头不知道往哪推** ✓
        ///   让用户去网页里翻「网络 → 高级配置 → 报警主机」很绕 ✓ 直接写更快 ✓
        ///
        /// ★ 安全做法：**读 → 只改 ipAddress/portNo → 原样写回 → 再读一次验证** ✓
        ///   不构造新 XML ✗ 在摄像头给的原文上改 ✓ 这样其它字段（协议/格式/认证）一个都不会动 ✓
        ///   写完立刻回读确认 ✓ 失败会明确报出来 ✓
        /// </summary>
        // ★★ 上一次设置时读到的原值（2026-09-13 加）
        //
        //   用户问：「我用完了，你会进摄像头改回默认的旧地址么？」
        //   ★ 答案是不会 ✗ —— 所以现在补上还原 ✓
        //
        //   这里记住的是**摄像头原来的值** ✗ 不是"出厂默认" ✓
        //     可能是 0.0.0.0（从来没设过）
        //     也可能是用户自己设过的别的地址
        //   我们不猜 ✓ 就把读到的那个写回去 ✓
        private static string _lastOldIp = null;
        private static int _lastOldPort = 0;
        private static Target _lastTarget = null;

        /// <summary>
        /// 把摄像头「报警上传」地址**还原成我们改之前的值**。
        ///
        /// ★ 2026-09-13 加。用户问「我用完了，你会改回旧地址么」——
        ///   那时候的答案是"不会" ✗ 而那是错的 ✓
        ///   程序停止 / 关闭之后，摄像头还一直往这个地址推 ✓
        ///   推不到就会一直重试 ✓ 而且下次换台电脑跑，它还是指着旧地址 ✓
        ///
        /// ★ 什么时候调：
        ///   · 点「停止」
        ///   · 关程序（FormClosing）—— 这条更重要 ✗ 很多人是直接关窗口 ✓
        ///
        /// ★ 没设过（_lastTarget 为空）就什么都不做 ✓
        ///   还原失败也不抛 ✗ 只返回一句话让上层记日志 ✓
        ///   （关程序的时候不能因为还原失败就不让关 ✓）
        /// </summary>
        public static string RestoreUploadHost()
        {
            if (_lastTarget == null || _lastOldIp == null || _lastOldIp.Length == 0)
                return "";                        // 我们从来没改过，不用还原

            Target t = _lastTarget;
            string oldIp = _lastOldIp;
            int oldPort = _lastOldPort;
            _lastTarget = null; _lastOldIp = null; _lastOldPort = 0;   // 只还原一次
            try
            {
                string url = "http://" + t.Host + "/ISAPI/Event/notification/httpHosts";
                HttpWebRequest g = (HttpWebRequest)WebRequest.Create(url);
                g.Credentials = Cred(t); g.PreAuthenticate = true;
                g.Timeout = 8000; g.ReadWriteTimeout = 8000;
                string xml;
                using (WebResponse r = g.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    xml = sr.ReadToEnd();

                int i = xml.IndexOf("<ipAddress>", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "还原报警上传地址失败：读不到 ipAddress";
                int j = xml.IndexOf("</ipAddress>", i, StringComparison.OrdinalIgnoreCase);
                if (j <= i) return "还原报警上传地址失败：ipAddress 不完整";
                string newXml = xml.Substring(0, i + 11) + oldIp + xml.Substring(j);
                if (oldPort > 0)
                {
                    int pi = newXml.IndexOf("<portNo>", StringComparison.OrdinalIgnoreCase);
                    if (pi >= 0)
                    {
                        int pj = newXml.IndexOf("</portNo>", pi, StringComparison.OrdinalIgnoreCase);
                        if (pj > pi) newXml = newXml.Substring(0, pi + 8) + oldPort.ToString() + newXml.Substring(pj);
                    }
                }

                HttpWebRequest p = (HttpWebRequest)WebRequest.Create(url);
                p.Credentials = Cred(t); p.PreAuthenticate = true;
                p.Method = "PUT"; p.ContentType = "application/xml";
                p.Timeout = 10000; p.ReadWriteTimeout = 10000;
                byte[] body = Encoding.UTF8.GetBytes(newXml);
                p.ContentLength = body.Length;
                using (Stream s = p.GetRequestStream()) s.Write(body, 0, body.Length);
                using (WebResponse r = p.GetResponse()) { }

                return "已把摄像头「报警上传」地址还原成原值 " + oldIp
                    + (oldPort > 0 ? ":" + oldPort : "") + " ✓";
            }
            catch (Exception ex)
            {
                return "还原报警上传地址失败：" + ex.Message + "（可以到摄像头网页里手动改）";
            }
        }

        /// <summary>把摄像头的「报警主机 / 上传中心」地址自动设成本机。</summary>
        public static string ConfigureUploadHost(Target t, string myIp, int port)
        {
            if (t == null || !t.Ok) return "报警上传配置跳过：摄像头地址不完整";
            if (myIp == null || myIp.Length == 0) return "报警上传配置跳过：拿不到本机 IP";
            try
            {
                string url = "http://" + t.Host + "/ISAPI/Event/notification/httpHosts";

                // ① 先读原文
                HttpWebRequest g = (HttpWebRequest)WebRequest.Create(url);
                g.Credentials = Cred(t); g.PreAuthenticate = true;
                g.Timeout = 8000; g.ReadWriteTimeout = 8000;
                string xml;
                using (WebResponse r = g.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    xml = sr.ReadToEnd();

                // 报文记录（2026-09-15 用户要求"抓包报文"）：读摄像头配置
                NetTrace.Exchange("读摄像头 报警上传配置 GET " + url,
                    "GET " + url,
                    "HTTP 200，返回 " + xml.Length + " 字" + Environment.NewLine
                        + NetTrace.Short(xml.Replace("\r", "").Replace("\n", " "), 400));

                // ② 只改第一个目标的地址和端口（原文替换，不动别的字段）
                int i = xml.IndexOf("<ipAddress>", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "报警上传配置失败：读到的配置里没有 ipAddress 字段";
                int j = xml.IndexOf("</ipAddress>", i, StringComparison.OrdinalIgnoreCase);
                if (j <= i) return "报警上传配置失败：ipAddress 字段不完整";
                string oldIp = xml.Substring(i + 11, j - i - 11).Trim();
                string newXml = xml.Substring(0, i + 11) + myIp + xml.Substring(j);

                int pi = newXml.IndexOf("<portNo>", StringComparison.OrdinalIgnoreCase);
                if (pi >= 0)
                {
                    int pj = newXml.IndexOf("</portNo>", pi, StringComparison.OrdinalIgnoreCase);
                    if (pj > pi) newXml = newXml.Substring(0, pi + 8) + port.ToString() + newXml.Substring(pj);
                }

                // ★ 记住原值，等停止时还原（2026-09-13 加）
                if (_lastTarget == null)
                {
                    _lastTarget = t;
                    _lastOldIp = oldIp;
                    int opi = xml.IndexOf("<portNo>", StringComparison.OrdinalIgnoreCase);
                    if (opi >= 0)
                    {
                        int opj = xml.IndexOf("</portNo>", opi, StringComparison.OrdinalIgnoreCase);
                        if (opj > opi)
                        {
                            int v;
                            if (int.TryParse(xml.Substring(opi + 8, opj - opi - 8).Trim(), out v)) _lastOldPort = v;
                        }
                    }
                }

                if (oldIp == myIp)
                {
                    // 地址已经对了，只确认端口
                    if (xml.IndexOf("<portNo>" + port.ToString() + "</portNo>", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "报警上传地址已经是 " + myIp + ":" + port + " ✓ 无需修改";
                }

                // ③ 写回
                HttpWebRequest p = (HttpWebRequest)WebRequest.Create(url);
                p.Credentials = Cred(t); p.PreAuthenticate = true;
                p.Method = "PUT"; p.ContentType = "application/xml";
                p.Timeout = 10000; p.ReadWriteTimeout = 10000;
                byte[] body = Encoding.UTF8.GetBytes(newXml);
                p.ContentLength = body.Length;
                using (Stream s = p.GetRequestStream()) s.Write(body, 0, body.Length);
                string respText;
                using (WebResponse r = p.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    respText = sr.ReadToEnd();

                // 报文记录：写回摄像头（这一步失败就是"配置不上"的现场）
                NetTrace.Exchange("写摄像头 报警上传配置 PUT " + url,
                    "PUT " + url + Environment.NewLine + "Content-Length: " + body.Length
                        + Environment.NewLine + NetTrace.Short(newXml.Replace("\r", "").Replace("\n", " "), 400),
                    "HTTP 返回 " + respText.Length + " 字" + Environment.NewLine
                        + NetTrace.Short(respText.Replace("\r", "").Replace("\n", " "), 300));

                // ④ ★ 再读一次验证（写成功不等于写对了 ✓）
                HttpWebRequest g2 = (HttpWebRequest)WebRequest.Create(url);
                g2.Credentials = Cred(t); g2.PreAuthenticate = true;
                g2.Timeout = 8000; g2.ReadWriteTimeout = 8000;
                string back;
                using (WebResponse r = g2.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    back = sr.ReadToEnd();

                bool okIp = back.IndexOf("<ipAddress>" + myIp + "</ipAddress>", StringComparison.OrdinalIgnoreCase) >= 0;
                bool okPort = back.IndexOf("<portNo>" + port.ToString() + "</portNo>", StringComparison.OrdinalIgnoreCase) >= 0;
                if (okIp && okPort)
                    return "已把摄像头「报警上传」地址设为 " + myIp + ":" + port + " ✓（原值 " + oldIp + "，回读确认通过）";
                if (okIp)
                    return "已设置 " + myIp + " ✓ 但端口回读是别的值，请到网页确认（原值 " + oldIp + "）";
                return "写入后回读没看到 " + myIp + " ✗ 可能被摄像头拒绝，请到网页手动设置（原值 " + oldIp + "）";
            }
            catch (WebException we)
            {
                string extra = "";
                if (we.Response != null)
                {
                    try
                    {
                        using (Stream s = we.Response.GetResponseStream())
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            extra = sr.ReadToEnd();
                    }
                    catch (Exception) { }
                    extra = extra.Length > 200 ? extra.Substring(0, 200) : extra;
                }
                return "报警上传自动配置失败（HTTP " + we.Status + "）" + extra + " —— 请到网页手动设置";
            }
            catch (Exception ex)
            {
                return "报警上传自动配置失败：" + ex.Message + " —— 请到网页手动设置";
            }
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
        }

        /// <summary>从一段 EventNotificationAlert XML 里取字段。</summary>
        public static HikEvent ParseEvent(string xml)
        {
            if (xml == null || xml.Length == 0) return null;
            HikEvent e = new HikEvent();
            e.Raw = xml.Length > 2000 ? xml.Substring(0, 2000) : xml;
            e.EventType = Tag(xml, "eventType");
            e.State = Tag(xml, "eventState");
            e.ChannelId = Tag(xml, "channelID");
            e.Time = Tag(xml, "dateTime");
            if (e.EventType.Length == 0) return null;
            return e;
        }

        private static string Tag(string xml, string name)
        {
            int i = xml.IndexOf("<" + name + ">", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            i += name.Length + 2;
            int j = xml.IndexOf("</" + name + ">", i, StringComparison.OrdinalIgnoreCase);
            if (j <= i) return "";
            return xml.Substring(i, j - i).Trim();
        }
    }
}
