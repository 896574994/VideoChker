/* -*- coding: utf-8 -*-
 * OnvifEvents.cs — ONVIF 事件订阅（PullPoint）
 *
 * ★ 为什么加这个（2026-09-13 实测逼出来的，是目前最好的方案）
 *
 *   之前的三条路各有缺口：
 *     · alertStream  —— 实测海康只推异常类事件 ✗ **移动侦测拿不到** ✓
 *     · 报警接收     —— 好用 ✓ 但**只有海康/大华支持** ✗ TP-Link 没有 ISAPI ✓
 *     · 定时轮询     —— 都能用 ✓ 但**两次采样之间的动作会漏** ✗
 *
 *   ONVIF PullPoint 把这三个缺口都补上了 ✓✓ （2026-09-13 两台实测）：
 *     海康     事件服务 http://192.168.1.64/onvif/Events      ✓
 *              PullMessages 直接给到
 *                tns1:VideoSource/MotionAlarm                ← ★ 移动侦测
 *                tns1:RuleEngine/CellMotionDetector/Motion   ← ★ 单元格移动侦测
 *                tns1:RuleEngine/TamperDetector/Tamper       ← 遮挡
 *      TP-Link  事件服务 http://192.168.1.10:2020/onvif/service ✓
 *              CreatePullPointSubscription 也成功 ✓
 *
 *   **跨品牌** ✓ **能拿到移动侦测** ✓ **事件排队不会漏** ✓ **没有视频流开销** ✓
 *
 * ★ PullPoint 和 alertStream 的区别 ✗
 *   alertStream 是"摄像头往我这条长连接上推" ✓ 断了就丢 ✓
 *   PullPoint   是"摄像头把事件排进队列，我按时去取" ✓ **离线期间的事件也在** ✓✓
 *
 * ★ 代价：事件**不带图片** ✗
 *   所以收到事件后要**另外抓一张快照** ✓
 *     优先 HTTP 抓拍（海康有 ✓ 不建 RTSP 会话 ✓）
 *     没有就 RTSP 抽帧（TP-Link 这种 ✓）
 *
 * C# 5 兼容语法。零第三方依赖 ✓
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VideoChecker
{
    /// <summary>ONVIF 订阅收到的**一条**事件。</summary>
    public class OnvifEvent
    {
        public string Topic = "";        // tns1:RuleEngine/CellMotionDetector/Motion
        public string UtcTime = "";
        public string Operation = "";    // Initialized / Changed
        public string Raw = "";

        /// <summary>是不是"画面有变化"这类值得抓图分析的事件。</summary>
        public bool WorthAnalyzing
        {
            get
            {
                string t = Topic.ToLower();
                // 只认"画面事件" ✓ 设备状态类（重启/时钟同步/CPU）和分析用不上 ✓
                if (t.IndexOf("motion") >= 0) return true;
                if (t.IndexOf("motionalarm") >= 0) return true;
                if (t.IndexOf("cellmotiondetector") >= 0) return true;
                if (t.IndexOf("linedetector") >= 0) return true;
                if (t.IndexOf("fielddetector") >= 0) return true;
                if (t.IndexOf("tamper") >= 0) return true;
                if (t.IndexOf("digitalinput") >= 0) return true;
                if (t.IndexOf("alarm-in") >= 0 || t.IndexOf("alarmin") >= 0) return true;
                if (t.IndexOf("relay") >= 0) return true;
                return false;
            }
        }

        /// <summary>中文名，用于界面和日志。</summary>
        public string Chinese
        {
            get
            {
                string t = Topic.ToLower();
                if (t.IndexOf("cellmotiondetector") >= 0) return "画面变化";
                if (t.IndexOf("motionalarm") >= 0) return "移动侦测";
                if (t.IndexOf("motion") >= 0) return "移动侦测";
                if (t.IndexOf("linedetector") >= 0) return "越界侦测";
                if (t.IndexOf("fielddetector") >= 0) return "区域入侵";
                if (t.IndexOf("tamper") >= 0) return "镜头遮挡";
                if (t.IndexOf("digitalinput") >= 0) return "报警输入";
                if (t.IndexOf("relay") >= 0) return "报警输出";
                if (t.IndexOf("lastreboot") >= 0) return "设备重启";
                if (t.IndexOf("lastclocksynchronization") >= 0) return "时钟同步";
                if (t.IndexOf("storagefailure") >= 0) return "存储异常";
                if (t.IndexOf("processorusage") >= 0) return "CPU 使用率";
                // 取 Topic 的最后一段
                int i = Topic.LastIndexOf('/');
                return i >= 0 && i + 1 < Topic.Length ? Topic.Substring(i + 1) : Topic;
            }
        }
    }

    /// <summary>一个 ONVIF 订阅（含地址和到期时间）。</summary>
    public class OnvifSubscription
    {
        public string Address = "";
        public DateTime Expires = DateTime.MinValue;
        public string EventService = "";

        public bool Valid
        {
            get { return Address.Length > 0 && DateTime.UtcNow < Expires.AddSeconds(-20); }
        }

        /// <summary>
        /// 该不该去续期/重建了。剩不到 60 秒就算"快到期"。
        /// ★ 留 60 秒余量：续期本身可能失败 ✓ 失败就要走重建 ✓ 重建也要时间 ✓
        /// </summary>
        public bool NeedsRefresh
        {
            get
            {
                if (Address.Length == 0) return true;
                return (Expires - DateTime.UtcNow).TotalSeconds < 60;
            }
        }
    }

    public static class OnvifEvents
    {
        // ==================== SOAP 底层 ====================

        private static string Soap(string url, string action, string body, string user, string pwd, int timeoutMs)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string env =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
              + "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\""
              + " xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\""
              + " xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\""
              + " xmlns:tt=\"http://www.onvif.org/ver10/schema\""
              + " xmlns:tev=\"http://www.onvif.org/ver10/events/wsdl\""
              + " xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\""
              + " xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\""
              + " xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">"
              + "<s:Header>" + Security(user, pwd) + "</s:Header>"
              + "<s:Body>" + body + "</s:Body></s:Envelope>";
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "POST";
                r.ContentType = "application/soap+xml; charset=utf-8; action=\"" + action + "\"";
                r.Timeout = timeoutMs;
                r.ReadWriteTimeout = timeoutMs;
                r.KeepAlive = false;
                r.Proxy = null;
                byte[] b = Encoding.UTF8.GetBytes(env);
                r.ContentLength = b.Length;
                using (Stream s = r.GetRequestStream()) s.Write(b, 0, b.Length);
                using (WebResponse resp = r.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException we)
            {
                if (we.Response != null)
                {
                    try
                    {
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

        /// <summary>WS-Security UsernameToken + PasswordDigest（和 OnvifClient 里那套一样）。</summary>
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

        // ==================== 对外功能 ====================

        /// <summary>问设备要事件服务地址。没有就返回空串（说明这台不支持 ONVIF 事件）。</summary>
        public static string GetEventService(string deviceServiceUrl, string user, string pwd)
        {
            if (string.IsNullOrEmpty(deviceServiceUrl)) return "";
            // 先单独问 Events 分类（最准）
            string cap = Soap(deviceServiceUrl,
                "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
                "<tds:GetCapabilities><tds:Category>Events</tds:Category></tds:GetCapabilities>",
                user, pwd, 8000);
            string x = Tag(cap, "XAddr");
            if (x.Length > 0) return x;

            // 退而求其次：All 里找 Events 那一段
            cap = Soap(deviceServiceUrl,
                "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
                "<tds:GetCapabilities><tds:Category>All</tds:Category></tds:GetCapabilities>",
                user, pwd, 8000);
            int i = cap.IndexOf("Events", StringComparison.OrdinalIgnoreCase);
            if (i > 0)
            {
                int j = cap.IndexOf("<XAddr>", i, StringComparison.OrdinalIgnoreCase);
                if (j > 0)
                {
                    int k = cap.IndexOf("</XAddr>", j, StringComparison.OrdinalIgnoreCase);
                    if (k > j) return cap.Substring(j + 7, k - j - 7).Trim();
                }
            }
            return "";
        }

        /// <summary>创建订阅。有效期给 300 秒，够用（到期前 Renew 续）。</summary>
        public static OnvifSubscription Create(string eventServiceUrl, string user, string pwd, int seconds)
        {
            if (string.IsNullOrEmpty(eventServiceUrl)) return null;
            string r = Soap(eventServiceUrl,
                "http://www.onvif.org/ver10/events/wsdl/EventPortType/CreatePullPointSubscriptionRequest",
                "<tev:CreatePullPointSubscription>"
              + "<tev:InitialTerminationTime>PT" + seconds + "S</tev:InitialTerminationTime>"
              + "</tev:CreatePullPointSubscription>",
                user, pwd, 12000);
            string addr = Tag(r, "Address");
            if (addr.Length == 0) return null;
            OnvifSubscription s = new OnvifSubscription();
            s.Address = addr;
            s.EventService = eventServiceUrl;
            s.Expires = DateTime.UtcNow.AddSeconds(seconds);
            string term = Tag(r, "TerminationTime");
            DateTime parsed;
            if (term.Length > 0 && DateTime.TryParse(term, out parsed))
                s.Expires = parsed.ToUniversalTime();
            return s;
        }

        /// <summary>
        /// 续期。**要求新的到期时间必须真的往后走** ✓
        ///
        /// ★ 关于"必须比对时间"这一条（2026-09-13，含一次我自己的误判 ✗）
        ///
        ///   原来写的是：只要返回里有 TerminationTime 就返回 true ✗
        ///   改成：新的到期时间要比原来晚至少 5 秒才算成功 ✓
        ///
        ///   **★ 我一开始的"理由"是错的** ✗
        ///     我测出 TP-Link「Renew 返回成功但到期时间没变」✓ 就断定它"假续期" ✗
        ///     后来发现**是我自己的测试有问题** ✓：
        ///       我用 Create(300) 建订阅，然后 Renew(300) ✗
        ///       —— 同样的 TTL 续期，到期时间当然不变 ✓ 这是**正常行为** ✓✓
        ///     实测三种组合（TP-Link）：
        ///       Create(300) → Renew(300)  前 20:25:34  后 20:25:34  不变（正常 ✓）
        ///       Create(120) → Renew(300)  前 20:22:41  后 20:25:41  延长 ✓
        ///       Create(300) → Renew(120)  前 20:25:47  后 20:25:47  被拒（不能缩短 ✓ 合理）
        ///
        ///   **★ 那这个检查还要不要留？要留** ✓ —— 理由换了：
        ///     不是"因为某家设备会骗人" ✗
        ///     而是**调用方需要知道"续期到底成没成"** ✓
        ///       实际用法是：剩不到 60 秒时 Renew(300)
        ///         原来的到期 = 现在 + <60 秒
        ///         新的到期 = 现在 + 300 秒
        ///         → 正常情况下**一定往后走** ✓ 检查会通过 ✓
        ///       如果没往后走 ✓ 说明续期**真的失败了** ✓
        ///       那就该**重建订阅**（重建比依赖 Renew 可靠 ✓ 各家实现差异大 ✓）
        ///
        ///   ★ 教训：**"现象"和"原因"是两件事** ✗
        ///     我看到了真实现象（时间没变 ✓）
        ///     但给出的原因是错的（"设备假续期" ✗）
        ///     正确原因是"我的测试参数有问题" ✓
        ///     —— 和坑 34（ffmpeg 退出码 0 但内容是同一帧）一样：
        ///       **先把"是不是我自己搞错了"排除掉，再下结论** ✓
        /// </summary>
        public static bool Renew(OnvifSubscription sub, string user, string pwd, int seconds)
        {
            if (sub == null || sub.Address.Length == 0) return false;
            DateTime before = sub.Expires;

            string r = Soap(sub.Address,
                "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/RenewRequest",
                "<wsnt:Renew><wsnt:TerminationTime>PT" + seconds + "S</wsnt:TerminationTime></wsnt:Renew>",
                user, pwd, 8000);
            string term = Tag(r, "TerminationTime");
            DateTime parsed;
            if (term.Length > 0 && DateTime.TryParse(term, out parsed))
            {
                DateTime after = parsed.ToUniversalTime();
                // 必须真的往后走了才算成功（见上面的说明）
                if (after > before.AddSeconds(5))
                {
                    sub.Expires = after;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 取一次排队的消息。**阻塞**最多 timeoutSec 秒 ✓（有事件会立刻返回 ✓）
        /// 返回这一批事件（可能为空 ✓ 表示这段时间没动静，正常 ✓）
        /// </summary>
        public static List<OnvifEvent> Pull(OnvifSubscription sub, string user, string pwd, int timeoutSec, int maxMessages)
        {
            List<OnvifEvent> list = new List<OnvifEvent>();
            if (sub == null || sub.Address.Length == 0) return list;
            string r = Soap(sub.Address,
                "http://www.onvif.org/ver10/events/wsdl/PullPointSubscription/PullMessagesRequest",
                "<tev:PullMessages><tev:Timeout>PT" + timeoutSec + "S</tev:Timeout>"
              + "<tev:MessageLimit>" + maxMessages + "</tev:MessageLimit></tev:PullMessages>",
                user, pwd, (timeoutSec + 4) * 1000);
            if (r.Length == 0) return list;

            // 按 <...NotificationMessage> 切分
            MatchCollection blocks = Regex.Matches(r,
                "<(?:[A-Za-z0-9_]+:)?NotificationMessage[^>]*>(.*?)</(?:[A-Za-z0-9_]+:)?NotificationMessage>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match b in blocks)
            {
                string blk = b.Groups[1].Value;
                OnvifEvent e = new OnvifEvent();
                e.Raw = blk.Length > 2000 ? blk.Substring(0, 2000) : blk;
                // Topic 可能是 <wsnt:Topic ...>tns1:xxx</wsnt:Topic> 也可能带 Dialect 属性
                Match tm = Regex.Match(blk,
                    "<(?:[A-Za-z0-9_]+:)?Topic[^>]*>([^<]+)</(?:[A-Za-z0-9_]+:)?Topic>",
                    RegexOptions.IgnoreCase);
                if (tm.Success) e.Topic = tm.Groups[1].Value.Trim();
                if (e.Topic.Length == 0)
                {
                    // 有些设备把 Topic 写在属性里
                    Match ta = Regex.Match(blk, "Topic[^>]*Dialect[^>]*>([^<]+)<", RegexOptions.IgnoreCase);
                    if (ta.Success) e.Topic = ta.Groups[1].Value.Trim();
                }
                e.UtcTime = Tag(blk, "UtcTime");
                e.Operation = Tag(blk, "Operation");
                if (e.Topic.Length > 0) list.Add(e);
            }
            return list;
        }

        /// <summary>退订（尽力而为，失败无所谓）。</summary>
        public static void Unsubscribe(OnvifSubscription sub, string user, string pwd)
        {
            if (sub == null || sub.Address.Length == 0) return;
            try
            {
                Soap(sub.Address,
                    "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest",
                    "<wsnt:Unsubscribe/>", user, pwd, 4000);
            }
            catch (Exception) { }
        }

        // ==================== 工具 ====================

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
    }
}
