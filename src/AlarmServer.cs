/* -*- coding: utf-8 -*-
 * AlarmServer.cs — 接收海康摄像头「报警上传」推过来的事件和图片
 *
 * ★ 为什么要这个东西（2026-09-13，用户一路追问逼出来的正确答案）
 *
 *   一开始我用 /ISAPI/Event/notification/alertStream 做事件订阅 ✓
 *   实测发现：那台 DS-2CD7A47EWD-XZS **只会通过它推「异常类」事件**（videoloss 等）✗
 *   **移动侦测根本不在 alertStream 上** ✓（3 分钟 20 条全是 videoloss，一条 VMD 都没有）
 *
 *   而摄像头的 /ISAPI/Event/capabilities 明确写着 isSupportMotionDetection = true ✓
 *   VMD-1 也配了 notificationMethod = center（上传中心）✓
 *   **但 /ISAPI/Event/notification/httpHosts 里 3 个目标的 ipAddress 全是 0.0.0.0** ✗
 *   —— 摄像头**不知道该往哪推** ✓
 *
 *   移动侦测走的是另一条路：**摄像头主动 POST 到「上传中心」地址** ✓
 *   这就用户说的「画面变化的时候它推送图片了」✓✓
 *   —— 和 HCNetSDK 的「报警布防」是同一个机制 ✓ 只是 SDK 走私有协议、这里走 HTTP ✓
 *   → **不用带 50MB 的厂商 DLL** ✓ 摄像头自己就支持 HTTP 上传 ✓
 *
 * 本类做的事：起一个 HttpListener 等着，摄像头推什么就收什么 ✓
 *   收到后：从 multipart 里分离出 XML（事件信息）和 JPEG（现场图片）✓
 *
 * C# 5 兼容语法。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace VideoChecker
{
    /// <summary>摄像头推过来的一条报警。</summary>
    public class AlarmPush
    {
        public string EventType = "";      // VMD / linedetection / …
        public string EventState = "";     // active / inactive
        public string ChannelId = "";
        public string DateTime = "";
        public byte[] Image;               // ★ 摄像头**随报警一起推过来的现场图片**（有的话）
        public string RawXml = "";
        public string SourceIp = "";

        private static readonly Dictionary<string, string> Cn = BuildCn();

        private static Dictionary<string, string> BuildCn()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            d["VMD"] = "移动侦测";
            d["motion"] = "移动侦测";
            d["linedetection"] = "越界侦测";
            d["fielddetection"] = "区域入侵";
            d["regionentrance"] = "进入区域";
            d["regionexiting"] = "离开区域";
            d["tamperdetection"] = "镜头遮挡";
            d["shelteralarm"] = "遮挡报警";
            d["videoloss"] = "视频信号丢失";
            d["diskfull"] = "硬盘满";
            d["diskerror"] = "硬盘错";
            d["nicbroken"] = "网线断开";
            d["ipconflict"] = "IP 冲突";
            d["illaccess"] = "非法访问";
            d["faceSnap"] = "人脸抓拍";
            d["ANPR"] = "车牌识别";
            return d;
        }

        public string Chinese
        {
            get
            {
                if (EventType.Length == 0) return "未知报警";
                string v;
                if (Cn.TryGetValue(EventType, out v)) return v;
                string low = EventType.ToLower();
                foreach (KeyValuePair<string, string> kv in Cn)
                    if (low.IndexOf(kv.Key.ToLower()) >= 0) return kv.Value;
                return EventType;
            }
        }

        public bool WorthAnalyzing
        {
            get
            {
                if (State().Length > 0 && State().ToLower() != "active") return false;
                // 纯设备异常类没必要送 AI（没画面可分析）
                string t = EventType.ToLower();
                if (t == "diskfull" || t == "diskerror" || t == "nicbroken"
                    || t == "ipconflict" || t == "illaccess") return false;
                return true;
            }
        }

        private string State()
        {
            if (EventState.Length > 0) return EventState;
            return "";
        }
    }

    /// <summary>
    /// 内嵌的报警接收服务。摄像头按「上传中心」配置 POST 过来，这里接住。
    /// </summary>
    public class AlarmServer : IDisposable
    {
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _run;
        private string _prefix;

        /// <summary>监听端口。海康默认往 80 端口推，但那个端口通常被占；建议用 18080 并在摄像头里填这个端口。</summary>
        public int Port = 18080;

        /// <summary>收到报警时回调（在**后台线程**上执行，处理完再 return）。</summary>
        public Action<AlarmPush> OnAlarm;

        /// <summary>状态文字变化时回调（连接中/已监听/收到…）。</summary>
        public Action<string> OnStatus;

        /// <summary>已收到的报警条数。</summary>
        public int Count;

        public bool Running { get { return _run; } }

        public string Url { get { return _prefix; } }

        public bool Start()
        {
            if (_run) return true;
            try
            {
                _prefix = "http://+:" + Port + "/";
                _listener = new HttpListener();
                _listener.Prefixes.Add(_prefix);
                _listener.Start();
                _run = true;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Start();
                Status("报警接收服务已启动：" + _prefix);
                return true;
            }
            catch (Exception ex)
            {
                // 最常见的失败原因是端口被占或没权限（需要用 netsh 授权 URL ACL）
                Status("启动失败：" + ex.Message);
                try { if (_listener != null) _listener.Close(); } catch (Exception) { }
                _listener = null;
                _run = false;
                return false;
            }
        }

        public void Stop()
        {
            _run = false;
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            try { if (_listener != null) _listener.Close(); } catch (Exception) { }
            _listener = null;
        }

        public void Dispose() { Stop(); }

        private void Status(string s)
        {
            if (OnStatus != null) { try { OnStatus(s); } catch (Exception) { } }
        }

        private void Loop()
        {
            while (_run)
            {
                HttpListenerContext ctx = null;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { break; }          // Stop() 会让 GetContext 抛异常，正常退出
                if (ctx == null) break;

                // ★ 每条请求丢给独立线程处理（2026-09-13 修）
                //   原来是**单线程串行** ✗ 一条在处理时其它连接只能排队 ✓
                //   实测：连推 5 条报警会有 1 条超时 ✗
                //   而摄像头遇到超时可能会重推 ✓ 越积越多 ✓
                //   改成一条一个线程后，接收永远不排队 ✓
                HttpListenerContext c = ctx;
                Thread t = new Thread(delegate ()
                {
                    try { Handle(c); }
                    catch (Exception ex) { Status("处理报警失败：" + ex.Message); }
                });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string src = "";
            try { src = ctx.Request.RemoteEndPoint.Address.ToString(); } catch (Exception) { }
            byte[] body = ReadAll(ctx.Request.InputStream);
            string ctype = ctx.Request.ContentType == null ? "" : ctx.Request.ContentType;

            // ★★ 把这一条推送**按原文记进报文文件**（2026-09-15 用户要求"抓包报文"）
            //
            //   为什么先记这里：用户那个"画面里有人动、一条都没收到"的问题 ✗
            //   最需要的就是**摄像头到底有没有推过来** ✓
            //   以前这条只在内存里过一下 ✗ 出问题时没人抓得住 ✓
            //   现在记下来：来源 IP、方法、路径、Content-Type、长度、
            //               正文里 XML 的开头一段 ✓（图片段只记长度 —— 那是几百 KB 的 base64 ✗）
            try
            {
                StringBuilder tb = new StringBuilder();
                tb.AppendLine("  来自    : " + src);
                tb.AppendLine("  方法    : " + ctx.Request.HttpMethod);
                tb.AppendLine("  路径    : " + ctx.Request.Url == null ? "" : ctx.Request.Url.ToString());
                tb.AppendLine("  Content-Type  : " + (ctype.Length == 0 ? "(空)" : ctype));
                tb.AppendLine("  Content-Length: " + body.Length + " 字节");
                string ua = "";
                try { ua = ctx.Request.UserAgent == null ? "" : ctx.Request.UserAgent; } catch (Exception) { }
                tb.AppendLine("  User-Agent    : " + (ua.Length == 0 ? "(空)" : ua));
                // 正文里如果是 XML（有些配置不推图），把开头记下来 ✓
                string bodyHead = "";
                try { bodyHead = Encoding.UTF8.GetString(body, 0, body.Length > 400 ? 400 : body.Length); }
                catch (Exception) { }
                bool looksXml = bodyHead.IndexOf("<", StringComparison.Ordinal) >= 0;
                if (looksXml)
                    tb.AppendLine("  正文(XML 开头): " + NetTrace.Short(bodyHead.Replace("\r", "").Replace("\n", " "), 320));
                else
                    tb.AppendLine("  正文    : 不是 XML（" + (body.Length > 0 ? "多半是图片段" : "空") + "）");
                NetTrace.Note("← 收【摄像头推送】", tb.ToString());
            }
            catch (Exception) { }

            // 回一个 200，免得摄像头认为推送失败一直重试
            try
            {
                ctx.Response.StatusCode = 200;
                byte[] ok = Encoding.UTF8.GetBytes("OK");
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = ok.Length;
                ctx.Response.OutputStream.Write(ok, 0, ok.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception) { }

            AlarmPush p = new AlarmPush();
            p.SourceIp = src;

            try
            {
                if (ctype.IndexOf("multipart", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // ★ 海康的报警上传：multipart 里一段是 XML（事件信息），一段是 JPEG（现场图片）
                    string boundary = ExtractBoundary(ctype);
                    if (boundary.Length > 0)
                    {
                        List<byte[]> parts = SplitMultipart(body, boundary);
                        foreach (byte[] part in parts)
                        {
                            if (IsJpeg(part))
                            {
                                if (p.Image == null || part.Length > p.Image.Length) p.Image = part;
                            }
                            else
                            {
                                string txt = ToText(part);
                                if (txt.IndexOf("<EventNotificationAlert", StringComparison.OrdinalIgnoreCase) >= 0
                                    || txt.IndexOf("<eventType>", StringComparison.OrdinalIgnoreCase) >= 0)
                                    Fill(p, txt);
                            }
                        }
                    }
                }
                else
                {
                    // 纯 XML（有些配置不推图片）
                    string txt = ToText(body);
                    if (txt.IndexOf("<", StringComparison.Ordinal) >= 0) Fill(p, txt);
                    // 万一直接把 JPEG 当 body 发过来
                    if (p.Image == null && IsJpeg(body)) p.Image = body;
                }
            }
            catch (Exception ex) { Status("解析报警内容出错：" + ex.Message); }

            Count++;
            string desc = "收到报警：" + p.Chinese + "（" + p.EventType + "）"
                + (p.Image != null ? " 含图片 " + (p.Image.Length / 1024) + "KB" : " 无图片")
                + "  来自 " + src;
            Status(desc);

            if (OnAlarm != null)
            {
                try { OnAlarm(p); }
                catch (Exception ex) { Status("处理报警回调出错：" + ex.Message); }
            }
        }

        private static void Fill(AlarmPush p, string xml)
        {
            p.RawXml = xml.Length > 4000 ? xml.Substring(0, 4000) : xml;
            p.EventType = Tag(xml, "eventType");
            p.EventState = Tag(xml, "eventState");
            p.ChannelId = Tag(xml, "channelID");
            p.DateTime = Tag(xml, "dateTime");
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

        private static string ExtractBoundary(string contentType)
        {
            int i = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            string b = contentType.Substring(i + 9).Trim();
            if (b.StartsWith("\"")) b = b.Trim('"');
            int semi = b.IndexOf(';');
            if (semi > 0) b = b.Substring(0, semi);
            return b.Trim();
        }

        /// <summary>按 boundary 切 multipart。切完再剥掉每段自己的 HTTP 头。</summary>
        private static List<byte[]> SplitMultipart(byte[] data, string boundary)
        {
            List<byte[]> res = new List<byte[]>();
            byte[] sep = Encoding.ASCII.GetBytes("--" + boundary);
            List<int> hits = new List<int>();
            for (int i = 0; i + sep.Length <= data.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < sep.Length; k++)
                    if (data[i + k] != sep[k]) { ok = false; break; }
                if (ok) { hits.Add(i); i += sep.Length - 1; }
            }
            for (int h = 0; h + 1 < hits.Count; h++)
            {
                int start = hits[h] + sep.Length;
                int end = hits[h + 1];
                // 跳过 CRLF
                while (start < end && (data[start] == 13 || data[start] == 10)) start++;
                while (end > start && (data[end - 1] == 13 || data[end - 1] == 10)) end--;
                if (end - start < 2) continue;
                // 找空行（段头和段体之间）
                int bodyStart = -1;
                for (int i = start; i + 3 < end; i++)
                {
                    if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    { bodyStart = i + 4; break; }
                    if (data[i] == 10 && data[i + 1] == 10) { bodyStart = i + 2; break; }
                }
                if (bodyStart < 0) continue;
                byte[] part = new byte[end - bodyStart];
                Array.Copy(data, bodyStart, part, 0, part.Length);
                res.Add(part);
            }
            return res;
        }

        private static bool IsJpeg(byte[] b)
        {
            return b != null && b.Length > 512 && b[0] == 0xFF && b[1] == 0xD8;
        }

        private static string ToText(byte[] b)
        {
            try
            {
                string s = Encoding.UTF8.GetString(b);
                // 去掉可能的乱码替换符开头
                return s.Trim('\0', '\uFEFF', ' ', '\r', '\n');
            }
            catch (Exception) { return ""; }
        }

        private static byte[] ReadAll(Stream s)
        {
            using (MemoryStream m = new MemoryStream())
            {
                byte[] buf = new byte[32768];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) m.Write(buf, 0, n);
                return m.ToArray();
            }
        }

        /// <summary>
        /// 本机的一个候选地址（给界面显示、给用户选）。
        ///
        /// ★ 为什么要有这个类（2026-09-14 用户要求）：
        ///   用户说：**「能不能做一个自动获取IP和手动设置IP的功能进去，
        ///             这样可以防止局域网地址不对导致连不上的问题」**
        ///   ★ 他说的是一个**真实且高频**的坑 ✗：
        ///     原来只取 `LocalIPs(...)` 的**第一个** ✓
        ///     而一台机器常常有好几个 IPv4：有线 + 无线 + 虚拟机网卡 + VPN + 热点 ✓
        ///     **同网段的有可能不止一个**（有线接内网、无线也接同一个网段）✗
        ///     那么"第一个"是哪个**完全看枚举顺序** ✓ 用户毫无办法 ✓
        ///     选错了 → 摄像头往一个本机收不到的地址推 → **一条报警都收不到** ✗
        ///     ——正是之前查了半天的那个症状 ✓
        ///
        ///   → 把"有哪些候选、各自是什么网卡、跟摄像头同不同网段"摆到界面上 ✓
        ///     并且允许用户**手动指定** ✓
        /// </summary>
        public class LocalIp
        {
            public string Ip = "";
            public string Name = "";          // 网卡名（"以太网" / "WLAN" / "VMware Network…"）
            public string Mask = "";
            public bool SameNet;              // 和摄像头同网段（按 /24 前三位比）
            public bool Virtual;              // 看着像虚拟网卡
            public bool HasGateway;           // 有默认网关 → 更像"真正在用的那块"

            /// <summary>界面上显示的一行。</summary>
            public string Display()
            {
                return Ip + "  ·  " + (Name.Length > 0 ? Name : "(未命名网卡)")
                    + (SameNet ? "  ★与摄像头同网段" : "")
                    + (Virtual ? "  [虚拟网卡]" : "");
            }
        }

        /// <summary>
        /// 把所有候选地址列出来（带网卡名、是否同网段、是否虚拟网卡）。
        /// 排序：**同网段 > 有网关 > 物理网卡 > IP 小的** ✓
        /// （最后按 IP 排是为了**稳定** —— 不然每次打开顺序都可能不一样 ✓）
        /// </summary>
        public static List<LocalIp> AllLocalIPs(string cameraIp)
        {
            List<LocalIp> list = new List<LocalIp>();
            string camPrefix = "";
            if (cameraIp != null)
            {
                int k = cameraIp.LastIndexOf('.');
                if (k > 0) camPrefix = cameraIp.Substring(0, k + 1);
            }
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni
                    in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    // ★ 回环和隧道直接跳过 ✗（回环地址摄像头不可能连得上）
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                        continue;

                    string nm = ni.Name;
                    try
                    {
                        if (ni.Description != null && ni.Description.Length > 0)
                            nm = ni.Name + "（" + ni.Description + "）";
                    }
                    catch (Exception) { }

                    bool hasGw = false;
                    try
                    {
                        foreach (System.Net.NetworkInformation.GatewayIPAddressInformation g
                            in ni.GetIPProperties().GatewayAddresses)
                        {
                            if (g.Address != null
                                && g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            { hasGw = true; break; }
                        }
                    }
                    catch (Exception) { }

                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ip
                        in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string s = ip.Address.ToString();
                        if (s.StartsWith("127.")) continue;
                        if (s.StartsWith("169.254.")) continue;   // 没拿到 DHCP 时的自动地址，没用 ✗

                        LocalIp li = new LocalIp();
                        li.Ip = s;
                        li.Name = nm;
                        li.HasGateway = hasGw;
                        try { if (ip.IPv4Mask != null) li.Mask = ip.IPv4Mask.ToString(); }
                        catch (Exception) { }
                        li.SameNet = (camPrefix.Length > 0 && s.StartsWith(camPrefix));
                        li.Virtual = LooksVirtual(nm);
                        list.Add(li);
                    }
                }
            }
            catch (Exception) { }

            list.Sort(delegate (LocalIp a, LocalIp b)
            {
                if (a.SameNet != b.SameNet) return a.SameNet ? -1 : 1;
                if (a.HasGateway != b.HasGateway) return a.HasGateway ? -1 : 1;
                if (a.Virtual != b.Virtual) return a.Virtual ? 1 : -1;
                return string.CompareOrdinal(a.Ip, b.Ip);
            });
            return list;
        }

        /// <summary>
        /// 看着像虚拟网卡么。
        /// ★ 为什么要标出来：虚拟网卡的地址**摄像头根本连不到** ✗
        ///   而它们的枚举顺序不保证 ✓ 说不定就排在第一个 ✓
        ///   标出来，用户才知道别选它 ✓
        /// </summary>
        public static bool LooksVirtual(string name)
        {
            if (name == null) return false;
            string s = name.ToLowerInvariant();
            string[] marks = new string[] {
                "vmware", "virtualbox", "vbox", "hyper-v", "vethernet",
                "loopback", "tap-", "tun", "vpn", "openvpn", "wireguard",
                "docker", "wsl", "bluetooth", "virtual", "虚拟", "热点", "mobile"
            };
            for (int i = 0; i < marks.Length; i++)
                if (s.IndexOf(marks[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 按用户的设置挑一个地址：**指定了就用指定的** ✓ 没指定（空串）就自动挑 ✓
        ///
        /// 返回空串有两种情况 ✗ 调用方要分得清：
        ///   · 一个候选都没有（网卡全断？）
        ///   · **用户指定的那个地址已经不在这台机器上了**（换了网络 ✓）
        /// ★ 第二种**绝不能默默换成别的** ✗
        ///   那样用户会以为"我的设置生效了"✓ 而实际上摄像头还是收不到 ✓
        ///   ——正是这一节要治的那个病 ✓
        /// </summary>
        public static string PickIp(string cameraIp, string wanted)
        {
            List<LocalIp> all = AllLocalIPs(cameraIp);
            if (all.Count == 0) return "";
            if (wanted != null && wanted.Length > 0)
            {
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Ip == wanted) return wanted;
                return "";        // 指定的那个不在了
            }
            return all[0].Ip;     // 已排序：同网段 > 有网关 > 物理网卡
        }

        /// <summary>
        /// 列出本机可用于填进摄像头的 IP（让用户知道「上传中心地址」该填什么）。
        /// 优先返回和摄像头同网段的那个。
        /// </summary>
        public static List<string> LocalIPs(string cameraIp)
        {
            List<string> r = new List<string>();
            foreach (LocalIp li in AllLocalIPs(cameraIp)) r.Add(li.Ip);
            if (r.Count > 0) return r;

            // 兜底：上面那套按网卡枚举失败时，回到老办法（至少别什么都不给）
            List<string> all = new List<string>();
            List<string> sameNet = new List<string>();
            try
            {
                string camPrefix = "";
                if (cameraIp != null)
                {
                    int k = cameraIp.LastIndexOf('.');
                    if (k > 0) camPrefix = cameraIp.Substring(0, k + 1);
                }
                foreach (System.Net.NetworkInformation.NetworkInterface ni
                    in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ip
                        in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string s = ip.Address.ToString();
                        if (s.StartsWith("127.")) continue;
                        all.Add(s);
                        if (camPrefix.Length > 0 && s.StartsWith(camPrefix)) sameNet.Add(s);
                    }
                }
            }
            catch (Exception) { }
            if (sameNet.Count > 0) return sameNet;
            return all;
        }
    }
}
