/* -*- coding: utf-8 -*-
 * NetSearchForm.cs — 网络搜索摄像头（ONVIF + SSDP/UPnP + 自动探测取流地址）
 *
 * ★ 为什么做这个（用户要求「加个 SSDP 功能主动探测网络里的摄像头广播」）
 *
 *   两种发现协议各有短板 ✓ 合起来才完整：
 *     · ONVIF WS-Discovery → 设备少，但**能直接问出取流地址** ✓✓
 *     · SSDP / UPnP        → 设备多（消费级摄像头也认），但**只知道有个设备** ✗
 *
 *   所以：**两种都搜** ✓ 搜到之后**再自动挨个试常见 RTSP 路径** ✓
 *   → 把"我手工帮用户找 TP-Link 的 /stream1"那件事**自动化** ✓✓
 *     （实测各家路径完全不一样：海康 /Streaming/Channels/101 ✓
 *       TP-Link /stream1 ✓ 大华 /cam/realmonitor ✓ 用户记不住也猜不到 ✓）
 *
 * C# 5 兼容语法。零第三方依赖 ✓
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace VideoChecker
{
    public class NetSearchForm : Form
    {
        private ListBox _list;
        private TextBox _user, _pwd;
        private Label _status;
        private RoundButton _searchBtn, _okBtn, _cancelBtn;
        private List<NetDevice> _devices = new List<NetDevice>();
        private volatile bool _searching;

        /// <summary>用户选定的取流地址（点确定后才有值）。</summary>
        public string ResultUrl = "";

        /// <summary>
        /// 选中那台设备的**名字**（友好名 / 厂商型号）。
        ///
        /// ★ 2026-09-15 加。为什么：
        ///   用户要求给「RTSP 实时取流分析」页也加网络搜索 ✓
        ///   而那个页面有个「备注（机位）」字段 ✓ 他自己填的是
        ///   「大门口半球1」这种名字 ✓
        ///   搜索里刚好能读到摄像头的 FriendlyName / 厂商型号 ✓
        ///   → 顺手带回去预填备注 ✓ 省得他再手打一遍 ✓
        ///   （拿不到就留空 ✓ **不编一个** ✓ 编错了比空着更坏 ✓）
        /// </summary>
        public string ResultName = "";

        public NetSearchForm()
        {
            AppInfo.SetFormIcon(this);
            Text = "网络搜索摄像头（ONVIF + SSDP）";
            Font = new Font("Microsoft YaHei", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 380);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            MinimumSize = new Size(520, 340);

            Font sf = new Font("Microsoft YaHei", 8.5F);

            Label tip = new Label();
            tip.Text = "同时用 ONVIF 和 SSDP/UPnP 搜局域网里的摄像头，并逐个自动试出取流地址 —— 不用再记各家不同的 RTSP 路径。";
            tip.Font = sf; tip.ForeColor = Color.Gray;
            tip.AutoSize = false;
            tip.Location = new Point(14, 10);
            tip.Size = new Size(ClientSize.Width - 28, 30);
            tip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(tip);

            _status = new Label();
            _status.Font = sf; _status.ForeColor = Color.Gray;
            _status.AutoSize = true;
            _status.Location = new Point(14, 42);
            _status.Text = "准备搜索…";
            Controls.Add(_status);

            _list = new ListBox();
            _list.Font = new Font("Microsoft YaHei", 9F);
            _list.Location = new Point(14, 64);
            _list.Size = new Size(ClientSize.Width - 28, 200);
            _list.IntegralHeight = false;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.DoubleClick += delegate (object s, EventArgs e) { OnOk(null, EventArgs.Empty); };
            Controls.Add(_list);

            int bottom = ClientSize.Height;

            Label lu = new Label();
            lu.Text = "账号"; lu.Font = sf; lu.AutoSize = true;
            lu.Location = new Point(14, bottom - 96);
            lu.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Controls.Add(lu);

            _user = new TextBox();
            _user.Font = Font; _user.Text = "admin";
            _user.Location = new Point(54, bottom - 100); _user.Width = 110;
            _user.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Controls.Add(_user);

            Label lp = new Label();
            lp.Text = "密码"; lp.Font = sf; lp.AutoSize = true;
            lp.Location = new Point(180, bottom - 96);
            lp.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Controls.Add(lp);

            _pwd = new TextBox();
            _pwd.Font = Font; _pwd.UseSystemPasswordChar = true;
            _pwd.Location = new Point(220, bottom - 100); _pwd.Width = 130;
            _pwd.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Controls.Add(_pwd);

            Label note = new Label();
            note.Text = "（填账号密码后会自动试出每台的取流地址）";
            note.Font = sf; note.ForeColor = Color.Gray; note.AutoSize = true;
            note.Location = new Point(362, bottom - 96);
            note.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Controls.Add(note);

            _searchBtn = new RoundButton();
            _searchBtn.Text = "重新搜索"; _searchBtn.Font = sf;
            _searchBtn.Location = new Point(14, bottom - 52); _searchBtn.Size = new Size(92, 30);
            _searchBtn.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _searchBtn.Click += delegate (object s, EventArgs e) { StartSearch(); };
            Controls.Add(_searchBtn);

            RoundButton probeBtn = new RoundButton();
            probeBtn.Text = "试取流地址"; probeBtn.Font = sf;
            probeBtn.Location = new Point(114, bottom - 52); probeBtn.Size = new Size(104, 30);
            probeBtn.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            probeBtn.Click += delegate (object s, EventArgs e) { StartProbe(); };
            Controls.Add(probeBtn);

            _okBtn = new RoundButton();
            _okBtn.Text = "填进地址栏"; _okBtn.Font = Font;
            _okBtn.Location = new Point(ClientSize.Width - 234, bottom - 52); _okBtn.Size = new Size(120, 30);
            _okBtn.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _okBtn.Click += OnOk;
            Controls.Add(_okBtn);

            _cancelBtn = new RoundButton();
            _cancelBtn.Text = "取消"; _cancelBtn.Font = Font;
            _cancelBtn.Location = new Point(ClientSize.Width - 100, bottom - 52); _cancelBtn.Size = new Size(86, 30);
            _cancelBtn.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _cancelBtn.Click += delegate (object s, EventArgs e) { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancelBtn);

            Resize += delegate (object s, EventArgs e)
            {
                try
                {
                    _list.Size = new Size(ClientSize.Width - 28, ClientSize.Height - 180);
                    _searchBtn.Location = new Point(14, ClientSize.Height - 52);
                    probeBtn.Location = new Point(114, ClientSize.Height - 52);
                    _okBtn.Location = new Point(ClientSize.Width - 234, ClientSize.Height - 52);
                    _cancelBtn.Location = new Point(ClientSize.Width - 100, ClientSize.Height - 52);
                    _user.Location = new Point(54, ClientSize.Height - 100);
                    _pwd.Location = new Point(220, ClientSize.Height - 100);
                    note.Location = new Point(362, ClientSize.Height - 96);
                    lu.Location = new Point(14, ClientSize.Height - 96);
                    lp.Location = new Point(180, ClientSize.Height - 96);
                }
                catch (Exception) { }
            };

            Themes.ApplyTo(this);
            BackColor = Themes.Current.Bg;

            Shown += delegate (object s, EventArgs e) { StartSearch(); };
        }

        /// <summary>把主界面地址栏里已有的账号密码预填进来（省得用户再打一遍）。</summary>
        /// <summary>设备名字：友好名优先，其次「厂商 型号」，都没有就返回空串。</summary>
        private static string NameOf(NetDevice d)
        {
            if (d == null) return "";
            if (d.FriendlyName != null && d.FriendlyName.Trim().Length > 0) return d.FriendlyName.Trim();
            string s = ((d.Manufacturer == null ? "" : d.Manufacturer) + " "
                      + (d.Model == null ? "" : d.Model)).Trim();
            return s;
        }

        public void PresetCredential(string user, string pwd)
        {
            try
            {
                if (user != null && user.Length > 0) _user.Text = user;
                if (pwd != null && pwd.Length > 0) _pwd.Text = pwd;
            }
            catch (Exception) { }
        }

        // ==================== 搜索 ====================

        private void StartSearch()
        {
            if (_searching) return;
            _searching = true;
            _list.Items.Clear();
            _devices.Clear();
            _status.Text = "正在搜索…（ONVIF 和 SSDP 同时搜，约 6 秒）";
            _searchBtn.Enabled = false;

            Thread t = new Thread(delegate ()
            {
                // ★ 两种协议**并行**搜（2026-09-13 改）
                //   原来串行：ONVIF 6 秒 + SSDP 6 秒 = 12 秒 ✗ 用户等得难受 ✓
                //   两个发现互不依赖 ✓ 并行只要 6 秒 ✓
                List<OnvifDevice> onvif = null;
                List<NetDevice> ssdp = null;
                Thread t1 = new Thread(delegate () { onvif = OnvifClient.Discover(6000); });
                Thread t2 = new Thread(delegate () { ssdp = SsdpClient.Discover(6000); });
                t1.IsBackground = true; t2.IsBackground = true;
                t1.Start(); t2.Start();
                t1.Join(9000); t2.Join(9000);
                if (onvif == null) onvif = new List<OnvifDevice>();
                if (ssdp == null) ssdp = new List<NetDevice>();
                List<NetDevice> all = SsdpClient.Merge(ssdp, onvif);

                // 同一个 IP 去重（ONVIF 的 Host 可能带端口，SSDP 的不带）
                List<NetDevice> uniq = new List<NetDevice>();
                foreach (NetDevice d in all)
                {
                    bool dup = false;
                    foreach (NetDevice e in uniq)
                        if (e.Ip == d.Ip) { dup = true; break; }
                    if (!dup) uniq.Add(d);
                }

                Ui(delegate () { Fill(uniq); });
                // 搜完自动开始试取流地址（用户填了账号密码才有意义）
                Thread.Sleep(300);
                Ui(delegate () { StartProbe(); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Ui(MethodInvoker a)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch (Exception) { }
        }

        private void Fill(List<NetDevice> found)
        {
            _searching = false;
            _searchBtn.Enabled = true;
            _devices = found;
            _list.Items.Clear();
            foreach (NetDevice d in found) _list.Items.Add(d.Display);
            if (found.Count == 0)
                _status.Text = "没搜到设备。可能：不在同一网段、摄像头禁了发现协议、或防火墙挡了组播。也可以直接手填地址。";
            else
            {
                int onvifN = 0; foreach (NetDevice d in found) if (d.Source.IndexOf("ONVIF") >= 0) onvifN++;
                _status.Text = "找到 " + found.Count + " 台（其中 " + onvifN + " 台支持 ONVIF）。正在自动试取流地址…";
                _list.SelectedIndex = 0;
            }
        }

        // ==================== 自动探测取流地址 ====================

        private void StartProbe()
        {
            if (_devices.Count == 0) return;
            string u = _user.Text.Trim();
            string p = _pwd.Text;
            if (u.Length == 0)
            {
                _status.Text = "要试取流地址，请先填账号密码。";
                return;
            }
            List<NetDevice> work = new List<NetDevice>(_devices);

            Thread t = new Thread(delegate ()
            {
                int ok = 0;
                foreach (NetDevice d in work)
                {
                    d.Probing = true;
                    Ui(delegate () { Refresh2(d); });

                    string uri = null;
                    string onvifErr = "";
                    // ① 支持 ONVIF 就直接问它要（最准 ✓）
                    if (d.ServiceUrl != null && d.ServiceUrl.Length > 0)
                    {
                        string err;
                        uri = OnvifClient.GetStreamUri(d.ServiceUrl, u, p, out err);
                        if (uri == null) onvifErr = err;
                    }
                    // ② 不行就自动挨个试常见路径 ✓（这就是把人工找地址自动化）
                    if (uri == null)
                    {
                        string note;
                        uri = SsdpClient.ProbeRtsp(d.Ip, u, p, out note);
                        if (uri == null)
                            // 两条路的提示都留着 ✓ 不然排障时看不到 ONVIF 那边为什么失败 ✗
                            d.ProbeNote = (onvifErr.Length > 0 ? "ONVIF: " + onvifErr + "；" : "") + note;
                    }

                    d.Probing = false;
                    d.RtspUrl = uri == null ? "" : uri;
                    if (uri != null) ok++;
                    Ui(delegate () { Refresh2(d); });
                }
                int okCount = ok;
                Ui(delegate ()
                {
                    _status.Text = "探测完成：共 " + work.Count + " 台，试出取流地址 " + okCount + " 台。选一台再点「填进地址栏」。";
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Refresh2(NetDevice d)
        {
            for (int i = 0; i < _devices.Count && i < _list.Items.Count; i++)
            {
                if (_devices[i] == d) { _list.Items[i] = d.Display; break; }
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _devices.Count)
            {
                MessageBox.Show(this, "请先在列表里选一台摄像头。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            NetDevice d = _devices[i];

            if (d.RtspUrl.Length > 0)
            {
                ResultUrl = d.RtspUrl;
                ResultName = NameOf(d);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            // 还没试出地址 → 现在试一次
            string u = _user.Text.Trim();
            if (u.Length == 0)
            {
                MessageBox.Show(this, "请先填账号密码，才好试取流地址。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _status.Text = "正在试 " + d.Ip + " 的取流地址…";
            Cursor = Cursors.WaitCursor;
            string note;
            string uri = null;
            if (d.ServiceUrl != null && d.ServiceUrl.Length > 0)
            {
                string err;
                uri = OnvifClient.GetStreamUri(d.ServiceUrl, u, _pwd.Text, out err);
            }
            if (uri == null) uri = SsdpClient.ProbeRtsp(d.Ip, u, _pwd.Text, out note);
            else note = "";
            Cursor = Cursors.Default;

            if (uri == null || uri.Length == 0)
            {
                _status.Text = "没试出地址：" + note;
                MessageBox.Show(this,
                    "没能试出这台摄像头的取流地址。\n\n" + note + "\n\n"
                    + "可以打开这台摄像头的网页或它的手机 App，在网络设置里看 RTSP 地址，\n"
                    + "然后直接手填到主界面的地址栏。",
                    "没试出地址", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            d.RtspUrl = uri;
            Refresh2(d);
            ResultUrl = uri;
            ResultName = NameOf(d);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
