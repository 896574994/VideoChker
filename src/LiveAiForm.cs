/* -*- coding: utf-8 -*-
 * LiveAiForm.cs — AI 实时巡检页（独立窗口，从左侧导航栏「AI 实时巡检」进入）
 *
 * 做什么：每隔 N 秒从 RTSP 摄像头抓一帧 → 交给本地 Ollama 的视觉模型理解 →
 *         给出「正常 / 告警」判定和文字描述 → 画面实时显示 + 告警记录 + 写入本地文件。
 *
 * 复用了现成的三样东西（不重复造轮子）：
 *   · MageCheck.ExtractFrameJpeg  —— 抓帧（本来就支持 RTSP，走 TCP）
 *   · MageCheck.QueryOllama       —— 视觉模型调用（URL / 模型名沿用设置页那份配置）
 *   · MageCheck.TryAcquire/Release —— AI 串行保护（避免和文搜页同时压 Ollama）
 *
 * C# 5 兼容语法。
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VideoChecker
{
    public partial class LiveAiForm : Form
    {
        // ==================== 状态 ====================
        private bool _running;
        private bool _busy;                 // 上一轮还没跑完 → 本轮跳过（防止堆积）
        private volatile bool _stop;        // 请求停止
        private System.Windows.Forms.Timer _timer;
        private int _round;                 // 已巡检轮次
        private int _alerts;                // 累计告警数
        private DateTime _started;

        // ==================== 控件 ====================
        private TextBox _urlBox;
        // ★ 地址框的「明文/打码」（2026-09-13 用户指出：这里没有显示/隐藏）
        //   用户截图里地址框明晃晃写着 rtsp://admin:真实密码@… ✗
        //   **RTSP 页早就有这个功能** ✓ 这一页漏了 ✓ —— 又是"同一个功能只做了一半" ✗
        //
        //   实现照抄 RtspForm ✓（含一个很微妙的细节：TabStop=false ✓ 见 HookUrlBox）
        //   ★ 程序内部一律读 _realUrl ✗ **绝不能读 _urlBox.Text**
        //     因为里面可能是打码后的假地址 ✓ 拿它去拉流会直接失败 ✓
        private string _realUrl = "";
        private bool _urlRevealed;              // 用户是否点了「显示」临时看明文
        private RoundButton _showUrlBtn;        // 「显示 / 隐藏」切换
        private ComboBox _streamPick;
        private ComboBox _modeBox;              // 巡检模式：事件订阅 / 定时轮询
        private volatile bool _evtRunning;      // 事件订阅线程是否在跑
        private AlarmServer _alarm;             // 报警接收服务（摄像头主动推图）
        private int _pushCount;                 // 收到的报警推送条数
        private string _pushFrom = "";          // ★ 本条报警**真实来自哪台摄像头**（发送方 IP）
        private RoundButton _onvifBtn;          // 「网络搜索」按钮（ONVIF + SSDP）
        private EventLogForm _logWin;           // 事件流水小窗（心跳等原始事件都进这里）
        // ★ 巡检记录的搜索框（2026-09-13 用户问"实况分析里没有文搜功能么"）
        //   答：确实没有 ✗ —— 但每轮巡检的 AI 描述都存在「内容」这一列里 ✓
        //   几百条记录里翻一句话很难 ✓ 加个关键词过滤很有用 ✓
        //   实现**照抄 VideoSearchForm.DoSearch()** ✓ 不重新发明（见交接文档 29.5）
        private TextBox _alertSearch;
        private Label _alertCount;              // 右侧显示"N / M 条"
        // ★★ 匹配模式 + 三个语义筛选项（2026-09-13 用户要求）
        //   用户说：「实况巡检的文搜是精确匹配，我想要模糊搜索做不到。
        //             能做成二选一的可选项么？然后在实况巡检文搜里
        //             做三个选项，人、车、动的选项筛选么？」
        //
        //   ★ 实测发现：现在的匹配**已经是模糊**（多关键词、任一命中、子串匹配）✗
        //     用户真正的痛点应该是另一个：
        //       搜「人」搜不到写「男子 / 行人 / 无人」的记录 ——
        //       因为 AI 的描述里**不含「人」这个字** ✓
        //     → 所以要的是**语义分组**：勾「人」= 一次匹配一整组近义词 ✓✓
        private ComboBox _alertMode;            // 模糊 / 精确
        private CheckBox _chkPerson;            // 人
        private CheckBox _chkCar;               // 车
        private CheckBox _chkMove;              // 动
        private RoundButton _logBtn;            // 「事件流水」按钮
        private int _lastAnalyzeTick;           // 上次分析的时刻（事件去抖用）
        // ★★ 「我已经认领了这一波」的标记（2026-09-13 加，验收测试逼出来的）
        //
        //   验收脚本推了 3 条（0 / 0.6 / 1.2 秒）✗ 结果分析了 **2 轮** ✓ 记了 2 行 ✓
        //   而按去抖的意图，**同一波动作只该分析 1 次** ✓
        //
        //   ★ 根因：_lastAnalyzeTick 是**"分析真的开始了"才盖的章** ✗
        //     而盖章之前有一段**最长 6 秒的抓图** ✓
        //     第 2、3 条在抓图那 6 秒里到 ✓ 一查时间戳发现"我没分析过"✓
        //     → 也跟着去抓图 ✓ 也跟着记一行"触发" ✗✓
        //     4 条并发抓图全超时 → 那两条分析各等 6 秒 → 又互相拖慢 ✗
        //
        //   ★ 修法：**认领要盖在"决定要分析"的那一刻** ✓ 不能等分析真的起来 ✓
        //     就像排队：先领号 ✓ 再慢慢准备材料 ✓
        //     没领到号的人看到的号是"刚有人领过"✓ 自然就跳过 ✓
        private volatile bool _analyzing;        // 本条已认领，正在分析中

        // ★★ 「本机地址」选择（2026-09-14 用户要求：自动获取 IP + 手动设置 IP）
        //   为什么需要：报警接收要把本机地址写进摄像头的「上传中心」✓
        //   而一台机器可能有好几个网卡 ✗ 自动挑错了 → 摄像头推不到本机 ✗
        //   见 BuildUi 里那段长注释 ✓
        private ComboBox _ipBox;                 // 自动 / 各个候选地址
        private RoundButton _ipRefreshBtn;        // 重新枚举（插网线后不用重开程序）
        private Label _ipHint;                   // 右边那行说明（选了哪个 / 有没有问题）
        // ★★ 去抖间隔只能有**一个数**（2026-09-13 修）
        //
        //   两条来事件的路径 ——「报警接收」OnAlarmPush ✓ 和「事件订阅」OnHikEvent ✓
        //   —— 用的是**同一个** _lastAnalyzeTick ✓ 也就是说它们互相去抖 ✓
        //   可阈值原来一个写 2500 ✗ 一个写 3000 ✗ 是**两个写了一遍的数** ✓
        //
        //   两处对不上的后果：先走 A 路径、2.6 秒后再走 B 路径 ✗
        //   A 觉得"该跳过"✓ B 觉得"该分析"✓ —— 同一波动作被分析两次 ✓
        //   （照 evidence-first「一个值只有一处」那一条 ✓ 抽成一个常量 ✓）
        private const int DebounceMs = 3000;    // 同一波动作内只分析一次
        private HikIsapi.Target _target;        // 解析出来的摄像头信息（事件模式用）
        private int _evtCount;                  // 收到的事件总数
        private List<StreamEntry> _streams = new List<StreamEntry>();
        private TextBox _intervalBox;
        private RoundButton _startBtn, _stopBtn, _loadBtn, _openDirBtn, _clearBtn;
        private Label _statusLab, _outLab, _previewLab;
        private ToolTip _statusTip;             // 状态栏全文（定宽省略后悬停可看全）
        private PictureBox _frame;
        private Label _verdictLab, _msLab, _descLab, _roundLab;
        private ComboBox _modelBox;                 // 本页的模型选择（和设置页共用配置）
        private RoundButton _fetchModelBtn;         // 「获取」按钮
        // ★ 本次巡检**锁定的模型**（2026-09-13 用户要求"开始巡检之后就不允许修改模型"）
        //   为什么不能只锁界面 ✗：
        //     模型是三个页面共用的一份配置 ✓ 在设置页改一下照样会影响正在跑的巡检 ✓
        //     而且下面三轮调用原来都是直接读 AppSettings.OllamaModel ✗ **每一轮都重读** ✓
        //     → 中途换模型会让同一次巡检里前后用的模型不一样 ✓ 结果没法比较 ✓
        //   现在的做法：开始巡检时把模型**抓进这个字段** ✓
        //     跑的这一轮永远用它 ✓ 界面上也锁住 ✓ 双保险 ✓
        private string _runModel = "";
        private ToolTip _modelTip;                   // 模型那一行的悬停说明（锁定时换文案）
        private DataGridView _grid;
        private TextBox _promptBox;
        private CardPanel _cardCtrl, _cardPreview, _cardResult, _cardAlerts, _cardPrompt;

        /// <summary>默认巡检提示词（和参考图一致的风格）。用户可在界面里改，改完会记住。</summary>
        private const string DefaultPrompt =
            "你是一名安防监控助手。请根据当前监控画面简要描述：画面中有什么、正在发生什么；" +
            "如有人员出现、物品移动、异常聚集或安全隐患，请重点说明。\n" +
            "最后另起一行，用「判定：正常」或「判定：告警」给出结论。\n" +
            "保持客观、简洁，不超过 100 字。";

        public LiveAiForm()
        {
            AppInfo.SetFormIcon(this);
            Text = "AI 实时巡检 - " + AppInfo.Title + " " + AppInfo.Version;
            Font = new Font("Microsoft YaHei", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1080, 820);
            // ★ 最小尺寸从 1000x680 提到 1010x756（2026-09-13）
            //   这一页有四张卡片 + 提示词框 ✓ 680 高的时候右侧两张卡根本排不下 ✓
            //   实测 1000x680 时「AI 分析结果」只能分到 111px ✗
            //   而它内部的标签一直排到 y=173 ✓ → 直接跑出卡片 ✓
            //   —— 与其让布局在挤不下的尺寸上算错 ✓ 不如不让窗口缩到那么小 ✓
            MinimumSize = new Size(1010, 756);

            BuildUi();
            LoadStreamList();
            ApplyTheme();
            UpdateModeHint();     // 初始化底部提示（默认是"事件订阅"模式）
            // ★ 填「本机地址」下拉（2026-09-14 加）
            //   必须在 BuildUi 之后（控件才存在）✓ 也要在地址框有值之后 ✓
            try { ReloadIpChoices(); } catch (Exception) { }

            Themes.Changed += OnThemeChanged;
            FormClosing += OnClosing;
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            Font sf = new Font("Microsoft YaHei", 8.5F);
            int M = 12;
            int W = ClientSize.Width;

            // ---------- 卡片一：巡检控制 ----------
            _cardCtrl = new CardPanel();
            _cardCtrl.SetTitle("巡检控制", "每隔几秒抓一帧送去分析；发现异常自动记入告警并写入本地文件");
            _cardCtrl.Location = new Point(M, 10);
            _cardCtrl.Size = new Size(W - M * 2, 214);
            Controls.Add(_cardCtrl);

            MkLabel(_cardCtrl, "摄像头地址", 16, 56, sf);
            _urlBox = new TextBox();
            // 右边留给：显示56 + 14 + 网络搜索104 + 14 + 流列表180 + 16 + 载入88 + 16 = 488
            _urlBox.Font = Font; _urlBox.Location = new Point(88, 52); _urlBox.Width = W - M * 2 - 88 - 488;
            _urlBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cardCtrl.Controls.Add(_urlBox);

            // 「显示 / 隐藏」按钮：默认打码，点一下临时看明文 ✓
            //   ★ 照抄 RtspForm（含 HookUrlBox 里那个 TabStop 的细节 ✓）
            _showUrlBtn = new RoundButton();
            _showUrlBtn.Text = "显示"; _showUrlBtn.Font = sf;
            _showUrlBtn.Location = new Point(W - M * 2 - 474, 50);
            _showUrlBtn.Size = new Size(56, 26);
            _showUrlBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _showUrlBtn.Click += delegate (object s, EventArgs e)
            {
                _urlRevealed = !_urlRevealed;
                SyncUrlDisplay();
                // 点「隐藏」时把焦点移开，不然焦点还在框里、下次照样露明文 ✗
                if (!_urlRevealed) { try { _showUrlBtn.Focus(); } catch (Exception) { } }
            };
            _cardCtrl.Controls.Add(_showUrlBtn);

            _streamPick = new ComboBox();
            _streamPick.DropDownStyle = ComboBoxStyle.DropDownList;
            _streamPick.Font = sf;
            _streamPick.Location = new Point(W - M * 2 - 284, 52); _streamPick.Width = 180;
            _streamPick.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _streamPick.SelectedIndexChanged += OnPickStream;
            _cardCtrl.Controls.Add(_streamPick);

            _loadBtn = new RoundButton();
            _loadBtn.Text = "载入"; _loadBtn.Font = sf;
            _loadBtn.Location = new Point(W - M * 2 - 104, 50); _loadBtn.Size = new Size(88, 26);
            _loadBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _loadBtn.Click += delegate(object s, EventArgs e) { LoadStreamList(); _statusLab.Text = "已重新载入流列表（" + _streams.Count + " 路）"; };
            _cardCtrl.Controls.Add(_loadBtn);

            // 网络搜索（ONVIF + SSDP）：不用再记各家不同的 RTSP 路径（海康 /Streaming/Channels/101、
            // TP-Link /stream1、大华又是别的）—— 让摄像头自己说出取流地址 ✓
            _onvifBtn = new RoundButton();
            _onvifBtn.Text = "网络搜索"; _onvifBtn.Font = sf;
            _onvifBtn.Location = new Point(W - M * 2 - 404, 50); _onvifBtn.Size = new Size(104, 26);   // ★ 原来在 -296 ✗ 和「从流列表选」重叠 100px ✓ 被完全盖住 ✓ 功能等于没有 ✓
            _onvifBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _onvifBtn.Click += OnOnvifSearch;
            _cardCtrl.Controls.Add(_onvifBtn);

            // 第二行：按钮 + 间隔（卡片标题占顶部约 52px，所以这一行从 y=88 起）
            _startBtn = new RoundButton();
            _startBtn.Text = "开始巡检"; _startBtn.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            _startBtn.Location = new Point(16, 90);
            _startBtn.Size = new Size(96, 30);
            _startBtn.Click += OnStart;
            _cardCtrl.Controls.Add(_startBtn);

            _stopBtn = new RoundButton();
            _stopBtn.Text = "停止"; _stopBtn.Font = Font;
            _stopBtn.Location = new Point(120, 90); _stopBtn.Size = new Size(72, 30);
            _stopBtn.Click += OnStop;
            _cardCtrl.Controls.Add(_stopBtn);

            MkLabel(_cardCtrl, "巡检方式", 208, 96, sf);
            _modeBox = new ComboBox();
            _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _modeBox.Font = sf;
            // 顺序有讲究：把"报警接收"放第一并设为默认 ——
            // 实测这台海康的 alertStream 只推「异常类」事件（videoloss 等），
            // 移动侦测根本不走那条路，必须用「报警接收」这条 ✓
            // 下拉项文字要**短**（2026-09-13 用户指出显示不全）：
            //   原来写"报警接收（推荐·摄像头主动推图）"这种长句 ✗
            //   下拉框宽度有限 ✗ 全被截成"…" ✓ 反而看不懂
            //   → 短标签放在下拉里 ✓ 完整说明放在下面那行提示里 ✓
            _modeBox.Items.Add("报警接收（推荐）");
            _modeBox.Items.Add("事件订阅");
            _modeBox.Items.Add("定时轮询");
            // ★ 第四种：ONVIF 订阅（2026-09-13 加）
            //   跨品牌 ✓ 而且**能拿到移动侦测**（海康的 alertStream 拿不到 ✗）
            //   放在最后：前三种是已验证稳定的 ✓ 这个是新加的 ✓
            _modeBox.Items.Add("ONVIF 订阅（通用）");
            _modeBox.SelectedIndex = 0;
            _modeBox.Location = new Point(272, 92); _modeBox.Width = 168;
            _modeBox.SelectedIndexChanged += delegate(object s, EventArgs e) { UpdateModeHint(); };
            _cardCtrl.Controls.Add(_modeBox);

            MkLabel(_cardCtrl, "间隔(秒)", 450, 96, sf);
            _intervalBox = new TextBox();
            _intervalBox.Font = Font; _intervalBox.Text = "10";   // 默认 10 秒：对摄像头更友好（5 秒=每小时 720 次连接，偏频繁）
            _intervalBox.Location = new Point(506, 92); _intervalBox.Width = 52;
            _cardCtrl.Controls.Add(_intervalBox);

            // ==================== ★ 本机地址（自动 / 手动）（2026-09-14 用户要求）====================
            //
            //   用户说：「能不能做一个自动获取IP和手动设置IP的功能进去，
            //           这样可以防止局域网地址不对导致连不上的问题」
            //
            //   ★ 它治的是什么病：
            //     「报警接收」模式要**把本机地址写进摄像头的「上传中心」** ✓
            //     原来只会自动挑第一个地址 ✗
            //     而一台机器常常有好几个 IPv4（有线 + 无线 + 虚拟机网卡 + VPN + 热点）✓
            //     挑错了 → 摄像头往一个本机收不到的地址推 → **一条报警都收不到** ✗
            //     用户那边看到的只是"已收到 0 条" ✓ 完全不知道该去查哪儿 ✓
            //
            //   ★ 所以：把候选**摆出来**（带网卡名、是否同网段、是否虚拟网卡）✓
            //     默认还是"自动"（老行为，选最合适的那个）✓
            //     用户可以钉死某一个 ✓ 而且换网络之后那个地址不在了会**明说** ✗ 不偷偷换 ✓
            MkLabel(_cardCtrl, "本机地址", 16, 134, sf);
            _ipBox = new ComboBox();
            _ipBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _ipBox.Font = sf;
            _ipBox.Location = new Point(88, 130); _ipBox.Width = 400;
            _ipBox.SelectedIndexChanged += delegate(object s, EventArgs e) { OnUploadIpChanged(); };
            _cardCtrl.Controls.Add(_ipBox);

            // 「刷新」：插上网线 / 换了网络之后，候选会变 —— 不用重开程序
            _ipRefreshBtn = new RoundButton();
            _ipRefreshBtn.Text = "刷新"; _ipRefreshBtn.Font = sf;
            _ipRefreshBtn.Location = new Point(496, 128); _ipRefreshBtn.Size = new Size(56, 26);
            _ipRefreshBtn.Click += delegate(object s, EventArgs e) { ReloadIpChoices(); };
            _cardCtrl.Controls.Add(_ipRefreshBtn);

            _ipHint = new Label();
            _ipHint.Font = sf; _ipHint.ForeColor = Color.Gray;
            _ipHint.AutoSize = false; _ipHint.AutoEllipsis = true;
            _ipHint.TextAlign = ContentAlignment.MiddleLeft;
            _ipHint.Location = new Point(560, 132); _ipHint.Size = new Size(200, 20);
            _ipHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cardCtrl.Controls.Add(_ipHint);

            _statusLab = new Label();
            _statusLab.Text = "就绪：填摄像头地址（或从右边下拉里选一路已保存的），点「开始巡检」。";
            // 定宽 + 超长省略：报错信息可能很长 ✗ AutoSize 会撑出卡片被裁掉 ✓
            // 全文放 ToolTip ✓ 悬停可见 ✓（2026-09-13 修，和 RTSP 页同一类问题）
            _statusLab.Font = sf; _statusLab.ForeColor = Color.Gray;
            _statusLab.AutoSize = false;
            _statusLab.AutoEllipsis = true;
            _statusLab.TextAlign = ContentAlignment.MiddleLeft;
            _statusLab.Location = new Point(570, 96);
            _statusLab.Size = new Size(200, 20);
            _statusLab.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _statusTip = new ToolTip();
            _statusLab.TextChanged += delegate (object s, EventArgs e)
            {
                try { _statusTip.SetToolTip(_statusLab, _statusLab.Text); } catch (Exception) { }
            };
            _cardCtrl.Controls.Add(_statusLab);

            // 摄像头保护提示（用户问过"这么频繁拉流会不会把摄像头拉崩" ✓ 这一行就是答案）
            Label tip = new Label();
            tip.Name = "camTip";
            // 定宽 + 自动换行（AutoSize=true 的标签不会换行，长文本会伸出卡片被截断 ✗）
            tip.Text = "用摄像头须知：① 间隔别小于 10 秒（5 秒 = 每小时 720 次连接，偏频繁）；"
                     + "② 尽量换成子码流地址（海康 …/Channels/102、大华 subtype=1）—— 分辨率低，摄像头负担小得多；"
                     + "③ 支持 HTTP 抓拍就更省（海康 http://IP/ISAPI/Streaming/channels/101/picture，"
                     + "大华 http://IP/cgi-bin/snapshot.cgi），那种方式根本不建 RTSP 会话。";
            tip.Font = sf; tip.ForeColor = Color.Gray;
            tip.AutoSize = false;
            tip.Location = new Point(16, 162);
            tip.Size = new Size(W - M * 2 - 32, 44);
            tip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cardCtrl.Controls.Add(tip);

            // ---------- 卡片二：画面预览（左） / 卡片三+四：结果与告警（右） ----------
            int top = 234;
            int rightW = RightColumnWidth(W);
            int leftW = W - M * 2 - rightW - 10;
            int promptH = (int)(ClientSize.Height * 0.22);        // 和 LayoutCards 用同一套算法
            if (promptH < 150) promptH = 150;
            int bodyH = ClientSize.Height - top - 10 - promptH - 12;
            if (bodyH < 240) bodyH = 240;

            _cardPreview = new CardPanel();
            _cardPreview.SetTitle("画面预览", "巡检抓到的实时画面");
            _cardPreview.Location = new Point(M, top);
            _cardPreview.Size = new Size(leftW, bodyH);
            Controls.Add(_cardPreview);

            _frame = new PictureBox();
            _frame.SizeMode = PictureBoxSizeMode.Zoom;
            _frame.BackColor = Color.Black;
            _frame.Location = new Point(16, 52);
            _frame.Size = new Size(leftW - 32, bodyH - 52 - 34);
            _frame.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _cardPreview.Controls.Add(_frame);

            _previewLab = new Label();
            _previewLab.Text = "尚未开始巡检";
            _previewLab.Font = sf; _previewLab.ForeColor = Color.Gray; _previewLab.AutoSize = true;
            _previewLab.Location = new Point(16, bodyH - 26);
            _previewLab.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _cardPreview.Controls.Add(_previewLab);

            _cardResult = new CardPanel();
            _cardResult.SetTitle("AI 分析结果", "本地模型对当前画面的理解");
            _cardResult.Location = new Point(M + leftW + 10, top);
            _cardResult.Size = new Size(rightW, 214);       // 190 → 214：底部加一行"模型选择"
            Controls.Add(_cardResult);

            _descLab = new Label();
            _descLab.Font = new Font("Microsoft YaHei", 9.5F);
            _descLab.AutoSize = false;
_descLab.Location = new Point(16, 90);
_descLab.Size = new Size(rightW - 32, 60);
            _descLab.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _descLab.Text = "（等待第一轮巡检）";
            _cardResult.Controls.Add(_descLab);

            _verdictLab = new Label();
            _verdictLab.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            _verdictLab.AutoSize = true;
            _verdictLab.Location = new Point(16, 154);
            _verdictLab.Text = "—";
            _cardResult.Controls.Add(_verdictLab);

            _msLab = new Label();
            _msLab.Font = sf; _msLab.ForeColor = Color.Gray; _msLab.AutoSize = true;
            _msLab.Location = new Point(120, 156);
            _msLab.Text = "";
            _cardResult.Controls.Add(_msLab);

            _roundLab = new Label();
            _roundLab.Font = sf; _roundLab.ForeColor = Color.Gray; _roundLab.AutoSize = true;
            _roundLab.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _roundLab.TextAlign = ContentAlignment.MiddleRight;
            _roundLab.Text = "已巡检 0 轮 / 告警 0 次";
            _roundLab.Location = new Point(rightW - 200, 156);
            _cardResult.Controls.Add(_roundLab);

            // ==================== ★ 模型选择（2026-09-13 用户要求加）====================
            // 用户反馈：在 AI 实时巡检页看不到"现在用的是哪个模型" ✗
            // 想换模型得跑去「设置」页 ✓ 来回切很麻烦 ✓
            // 所以在这一页直接加一个 ✓
            //
            // ★ 但要说清楚：**和设置页是同一份配置** ✓
            //   换这里就是换全局 ✓ 三个 AI 页面一起变 ✓
            //   （不搞"每页一个模型" ✗ 那样反而让人搞不清哪次用了哪个模型 ✓）
            MkLabel(_cardResult, "模型", 16, 60, sf);   // ★ y=60：副标题到 y=49，必须让开
            // ★ 放在标题正下方（不是卡片底部）✗
            //   第一版放在底部 ✓ 窗口一矮卡片就被压 ✓ 模型行直接被切掉 ✓ 表格也挤没了 ✓
            //   放顶部的话：卡片再矮也先压描述区 ✓ 这一行永远看得见 ✓
            _modelBox = new ComboBox();
            _modelBox.Font = sf;
            _modelBox.DropDownStyle = ComboBoxStyle.DropDown;   // 允许手输（Ollama 没启动时也能填）
_modelBox.Location = new Point(56, 56);
_modelBox.Size = new Size(rightW - 56 - 72 - 22, 24);   // 右边给按钮留 72 + 22 间隙
            _modelBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _modelBox.Text = AppSettings.OllamaModel;
            _modelBox.Items.Add(AppSettings.OllamaModel);
            _modelBox.TextChanged += delegate (object s, EventArgs e)
            {
                // 和设置页共用一份配置：这里改了，三个 AI 页面一起变，并且立刻存盘
                string m = _modelBox.Text.Trim();
                if (m.Length > 0) AppSettings.SetModel(m);
            };
            _cardResult.Controls.Add(_modelBox);

            _fetchModelBtn = new RoundButton();
            _fetchModelBtn.Text = "获取";
            _fetchModelBtn.Font = sf;
_fetchModelBtn.Location = new Point(rightW - 72 - 16, 54);
_fetchModelBtn.Size = new Size(72, 28);   // 60 太窄：文字会变成"获取 10"，挤
            _fetchModelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _fetchModelBtn.Click += OnFetchModels;
            _cardResult.Controls.Add(_fetchModelBtn);

            // 说明：让用户知道改这里会影响别的页（和设置页那句"三个页面共用"对上）














            _modelTip = new ToolTip();
        _modelTip.SetToolTip(_modelBox,
                "这一页用哪个视觉模型。\n\n" +
                "· 和「设置」页是同一份配置，这里改了，AI 内容理解 / 文搜 / 本页三处一起变\n" +
                "· 点右边「获取」可以从 Ollama 拉本机已装的模型，列在下拉里\n" +
                "· 必须是带 vl 的视觉模型（能看图），比如 qwen2.5vl:7b\n" +
                "· Ollama 没启动时也可以手动输入模型名");
            _modelTip.SetToolTip(_fetchModelBtn, "从 Ollama 取本机已装的模型列表（需要先启动 Ollama）");

            _cardAlerts = new CardPanel();
            _cardAlerts.SetTitle("告警记录", "每轮巡检写一行；标红的行是判定为告警的");
            _cardAlerts.Location = new Point(M + leftW + 10, top + 224);
            _cardAlerts.Size = new Size(rightW, bodyH - 224);
            Controls.Add(_cardAlerts);

            _grid = new DataGridView();
            _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false; _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 28;
            _grid.Columns.Add("cTime", "时间");
            _grid.Columns.Add("cVerdict", "判定");
            _grid.Columns.Add("cText", "内容");
            _grid.Columns[0].Width = 62;
            _grid.Columns[1].Width = 48;
            _grid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _grid.Columns[2].MinimumWidth = 120;
            _grid.Location = new Point(16, 82);   // 上面多了一行搜索框
            _grid.Size = new Size(rightW - 32, bodyH - 224 - 52 - 40);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _cardAlerts.Controls.Add(_grid);

            // ==================== ★ 巡检记录搜索（2026-09-13 用户要求）====================
            // 用户问：「实况分析里没有文搜功能么？」
            //   答：确实没有 ✗ —— 但**每轮巡检的 AI 描述都存在「内容」这一列里** ✓
            //   攒了几百条以后，想找「有没有人翻越围栏」很难翻 ✓ 加个关键词过滤很有用 ✓
            //
            // ★ 实现照抄 VideoSearchForm.DoSearch() ✓ 不重新发明
            //   （那条路已经验证过：多关键词按空格/逗号分开 ✓
            //     任一命中就显示 ✓ 命中词数多的排前面 ✓）
            //
            // ★ 位置：放在卡片副标题那一行的**右边** ✓
            //   那块地方一直是空的 ✓ 副标题只占左边一小截 ✓
            //   卡片窄的时候副标题会自动省略号（它已经设了 AutoEllipsis ✓）
            // ★ 放在**单独一行**（y=52，表格上面那行）✗
            //   第一版想塞在副标题右边 ✓ 结果被 tools\check_layout.ps1 抓出来：
            //     「重叠 139x17px: 副标题 × 搜索框」—— **6 个尺寸全都报** ✓
            //   根因：副标题是 CardPanel.SetTitle 画的 ✓ **占满整个卡片宽** ✗
            //         右边根本没有留给别人的地方 ✓
            //   → 与其想办法给副标题让位 ✓ 不如给搜索框单独一行 ✓
            //     代价是表格少 30px 高 ✓ 换来"任何尺寸都不会叠" ✓✓
            Label sl = new Label();
            sl.Name = "alertSearchLab";      // LayoutCards 要按名字找它（局部变量没法直接引用 ✗）
            sl.Text = "搜索"; sl.Font = sf; sl.AutoSize = true;
            sl.Location = new Point(16, 56);
            _cardAlerts.Controls.Add(sl);

            _alertSearch = new TextBox();
            _alertSearch.Font = Font;
            _alertSearch.Location = new Point(58, 52);
            // ★ 搜索框宽度要留出右边的「匹配模式」下拉（2026-09-13）
            // 宽度重算：右边要留 三个勾选 118 + 模式下拉 52 + 计数 40 + 边距 32 = 242
            //           左边是「搜索」标签 + 8 + 左边距 16 = 58
            _alertSearch.Size = new Size(Math.Max(60, rightW - 58 - 258), 22);
            _alertSearch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _alertSearch.TextChanged += delegate (object s, EventArgs e) { FilterAlerts(); };
            // 回车也当"筛一下"（打完字顺手回车）
            _alertSearch.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { FilterAlerts(); e.SuppressKeyPress = true; }
            };
            _cardAlerts.Controls.Add(_alertSearch);

            _alertCount = new Label();
            _alertCount.Font = new Font("Microsoft YaHei", 7.5F);
            _alertCount.ForeColor = Color.Gray;
            _alertCount.AutoSize = true;
            _alertCount.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _alertCount.Location = new Point(rightW - 32 - 34, 57);
            _alertCount.Text = "";
            _cardAlerts.Controls.Add(_alertCount);

            // ★★ 匹配模式：模糊 / 精确（2026-09-13 用户要求「二选一的可选项」）
            //   模糊（默认）= 拆成多个关键词，**命中任意一个**就显示
            //                 搜「人 车」= 有人的 OR 有车的 ✓
            //   精确        = 整句按原样匹配，**不拆词**
            //                 搜「红色卡车」= 必须有这四个字连在一起 ✓

            _alertMode = new ComboBox();            _alertMode.Name = "alertMode";
            _alertMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _alertMode.Font = new Font("Microsoft YaHei", 8.5F);
            _alertMode.Items.Add("模糊");
            _alertMode.Items.Add("精确");
            _alertMode.SelectedIndex = 0;
            _alertMode.Size = new Size(68, 22);   // ★ 52 太窄：两个字+箭头+内边距正好占满，会显示成「模…」
            _alertMode.Location = new Point(rightW - 32 - 68 - 128, 53);
            _alertMode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _alertMode.SelectedIndexChanged += delegate(object s, EventArgs e) { FilterAlerts(); };
            _cardAlerts.Controls.Add(_alertMode);

            _clearBtn = new RoundButton();
            _clearBtn.Text = "清空记录"; _clearBtn.Font = sf;
            _clearBtn.Location = new Point(16, bodyH - 224 - 32);
            _clearBtn.Size = new Size(88, 26);
            _clearBtn.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _clearBtn.Click += delegate(object s, EventArgs e) { _grid.Rows.Clear(); };
            _cardAlerts.Controls.Add(_clearBtn);

            // 事件流水小窗：心跳之类的原始事件都进这里，主记录只留结论 ✓
            _logBtn = new RoundButton();
            _logBtn.Text = "事件流水"; _logBtn.Font = sf;
            _logBtn.Location = new Point(112, bodyH - 224 - 32);   // 和 LayoutCards 保持一致（224 = 告警卡片上边距）
            _logBtn.Size = new Size(80, 26);
            _logBtn.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _logBtn.Click += delegate(object s, EventArgs e) { ShowLogWin(); };
            _cardAlerts.Controls.Add(_logBtn);

            _openDirBtn = new RoundButton();
            _openDirBtn.Text = "打开输出目录"; _openDirBtn.Font = sf;
            // ★★ 人 / 车 / 动 三个语义筛选项（2026-09-13 用户要求）
            //   放在底部这一行 —— 那里「事件流水」和「打开输出目录」之间有空位 ✓
            //   （比在搜索行挤三个复选框好得多 ✓ 卡片最窄只有 436px ✓）

            // 人 / 车 / 动 三个筛选项放在**搜索行右边**
            //
            // 第一版放在底部按钮那一行，检查器立刻报了 29 处：
            //   跑出卡片（卡片最窄只有 150px 高），而且和「打开输出目录」挤在一起
            // 算过空间：436px 的卡片底部只有 404px 可用，
            //   而「清空记录 + 事件流水 + 打开输出目录」已经占掉 296px，
            //   剩下的 112px 放不下「只看 人 车 动」（约 165px）
            // → 改到搜索行右边，那里本来就有空，
            //   而且「搜索 / 匹配 / 筛选」在语义上本来就该在一起
            _chkPerson = new CheckBox();
            _chkPerson.Name = "chkPerson";
            _chkPerson.Text = "人"; _chkPerson.Font = sf; _chkPerson.AutoSize = true;
            _chkPerson.Location = new Point(rightW - 32 - 124, 55);
            _chkPerson.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _chkPerson.CheckedChanged += delegate(object s, EventArgs e) { FilterAlerts(); };
            _cardAlerts.Controls.Add(_chkPerson);

            _chkCar = new CheckBox();
            _chkCar.Name = "chkCar";
            _chkCar.Text = "车"; _chkCar.Font = sf; _chkCar.AutoSize = true;
            _chkCar.Location = new Point(rightW - 32 - 87, 55);
            _chkCar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _chkCar.CheckedChanged += delegate(object s, EventArgs e) { FilterAlerts(); };
            _cardAlerts.Controls.Add(_chkCar);

            _chkMove = new CheckBox();
            _chkMove.Name = "chkMove";
            _chkMove.Text = "动"; _chkMove.Font = sf; _chkMove.AutoSize = true;
            _chkMove.Location = new Point(rightW - 32 - 50, 55);
            _chkMove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _chkMove.CheckedChanged += delegate(object s, EventArgs e) { FilterAlerts(); };
            _cardAlerts.Controls.Add(_chkMove);

            _openDirBtn.Location = new Point(rightW - 32 - 108, bodyH - 224 - 32);
            _openDirBtn.Size = new Size(108, 26);
            _openDirBtn.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _openDirBtn.Click += delegate(object s, EventArgs e)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory + "\""); }
                catch (Exception ex) { Log.Warn("打开目录失败：" + ex.Message); }
            };
            _cardAlerts.Controls.Add(_openDirBtn);

            // ---------- 卡片五：提示词 ----------
            _cardPrompt = new CardPanel();
            _cardPrompt.SetTitle("系统提示词", "决定 AI 怎么看画面；想让它盯特定目标（例如「有没有人翻越围栏」）就改这里，改完自动记住");
            _cardPrompt.Location = new Point(M, top + bodyH + 10);
            _cardPrompt.Size = new Size(W - M * 2, ClientSize.Height - (top + bodyH + 10) - 12);
            _cardPrompt.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_cardPrompt);

            _promptBox = new TextBox();
            _promptBox.Multiline = true;
            _promptBox.ScrollBars = ScrollBars.Vertical;
            _promptBox.Font = new Font("Microsoft YaHei", 9F);
            _promptBox.Location = new Point(16, 52);
            // 高度要给下面那行"输出文件"留 24px（原来没留 → 被输入框盖住，用户截图指出来了）
            _promptBox.Size = new Size(W - M * 2 - 32, Math.Max(40, _cardPrompt.Height - 52 - 16 - 24));
            _promptBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _promptBox.Text = AppSettings.LivePrompt.Length > 0 ? AppSettings.LivePrompt : DefaultPrompt;
            _promptBox.TextChanged += delegate(object s, EventArgs e) { AppSettings.LivePrompt = _promptBox.Text; };
            _cardPrompt.Controls.Add(_promptBox);

            _outLab = new Label();
            _outLab.Font = sf; _outLab.ForeColor = Color.Gray;
            _outLab.AutoSize = false;                    // 定宽 + 超长省略：路径很长也不会撑出卡片
            _outLab.AutoEllipsis = true;
            _outLab.TextAlign = ContentAlignment.MiddleLeft;
            _outLab.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _outLab.Text = "输出文件：" + OutFile;
            _cardPrompt.Controls.Add(_outLab);

            Resize += delegate(object s, EventArgs e) { LayoutCards(); };
            // ★ 挂上地址框的焦点事件（编辑时露明文、离开时打码）✓
            //   必须在 LayoutCards() 之前挂：HookUrlBox 里会把 TabStop 设成 false ✓
            //   万一布局之后又动了焦点 ✓ 就白设了 ✓
            HookUrlBox();
            LayoutCards();
        }

        /// <summary>
        /// 窗口改变大小时重排卡片。
        ///
        /// ★ 中间区域（画面预览 + 右侧结果/告警）的高度要**跟着窗口算** ✓
        ///   原来写死 430 ✗ 窗口一矮（比如 1000x680）→ 提示词卡片只剩 30px ✗
        ///   → 卡片里的输入框被压成负数高度、标签跑到 -2 ✗ 全部叠在一起 ✓
        ///   实测 4 种窗口尺寸才发现这个 ✗ 只测默认尺寸是看不出来的 ✓
        /// </summary>
        /// <summary>
        /// 右边两栏的宽度（「AI 分析结果」+「告警记录」）。
        ///
        /// ★ 2026-09-13 用户要求加宽
        ///   用户截图圈出来说：「把红框里的视频监控画面往左迁移，
        ///   然后 AI 分析结果和告警记录放大到第二个蓝框的宽度」
        ///
        ///   原来是**写死的 400** ✗ —— 窗口 1920 宽的时候，
        ///   视频画面占 1486 ✓ 右边两栏只有 400 ✗
        ///   AI 分析结果里的文字和告警表格都被挤得很难看 ✓
        ///
        ///   现在按窗口宽度**按比例**给 ✓ 大约 45% ✓
        ///   上下限：不比原来的 400 窄 ✓ 也不超过 980（太宽了视频就没地方了）✓
        ///
        ///   ★ 而且**必须只有一个地方算** ✗
        ///     BuildUi 和 LayoutCards 原来各写了一个 400 ✓
        ///     改一处漏一处是这类 bug 的老毛病（见交接文档第 13 节族 2）✓
        ///     所以收成这个方法 ✓ 两处都调它 ✓
        /// </summary>
        private static int RightColumnWidth(int clientW)
        {
            int total = clientW - 24;                 // 减掉左右边距 M*2
            if (total < 400) total = 400;
            int w = (int)(total * 0.45);
            if (w < 400) w = 400;                     // 不比原来窄
            if (w > 980) w = 980;                     // 封顶，给视频留地方
            return w;
        }

        private void LayoutCards()
        {
            int M = 12, W = ClientSize.Width, H = ClientSize.Height;
            if (W < 600 || H < 300) return;
            int rightW = RightColumnWidth(W);
            int leftW = W - M * 2 - rightW - 10;
            if (leftW < 300) { leftW = 300; rightW = W - M * 2 - leftW - 10; }

            // 中间区域高度 = 整窗 - 控制卡片 - 间距 - 提示词卡片（保证提示词卡至少 150）
            int top = 234;                       // 控制卡片高 214 + 上下留白
            // 提示词卡片按窗口高度给 22%（至少 170）：默认提示词有 3~4 行，太小不好编辑
            int promptH = (int)(H * 0.22);
            if (promptH < 150) promptH = 150;
            int bodyH = H - top - 10 - promptH - 12;
            if (bodyH < 240) bodyH = 240;        // 兜底：窗口极矮时也不让卡片塌掉

            _cardCtrl.Size = new Size(W - M * 2, 214);
            _cardPreview.Location = new Point(M, top);
            _cardPreview.Size = new Size(leftW, bodyH);
            _cardResult.Location = new Point(M + leftW + 10, top);

            // ★ 右侧两张卡片的高度是**互相抢**的，不能写死 ✗
            //   AI 分析结果想要高一点好看 ✓ 告警表格想要高一点好用 ✓
            //   原来两张都写死 → 窗口一矮，下面的表格被压到只剩十几像素 ✗
            //   （实测 1280x760 时表格只有 15px ✗ 一行都显示不下 ✓）
            //
            //   现在的规则：
            //     · 告警表格**保底 150px**（够显示几行 ✓）
            //     · 不够就让上面的「AI 分析结果」让位 ✓
            //     · 上面也保底 150px ✓
            //     · **模型选择那一行现在在卡片顶部**（标题下面）✓
            //       所以上面的卡片再矮 ✓ 它也不会被切 ✓✓
            //       —— 这正是把它从底部挪到顶部的原因 ✓
            int gapBetween = 10;                       // 两张卡片的间距
            int resultH = 214;                         // 理想高度
            int alertsH = bodyH - resultH - gapBetween;
            // 告警表格保底 150：不够就让上面对卡片让位
            // ★ 这里不加"窗口太矮就跳过"的条件 ✗
            //   第一版写了 bodyH > 320 才让位 ✓ 结果 1120x700 那种矮窗口直接跳过 ✓
            //   表格高度变成 0 ✓ 一行都看不见 ✓
            //   现在改成**无条件让位** ✓ 上面最多收到 150 ✓ 再矮也保证有用
            // ★ 上面那张卡片最多收到 110px ✗
            //   不能再低了 —— 但可以这么低，是因为**模型选择那一行在标题下面**
            //   （y=44，底边 70 ✓ 卡片 110 也装得下 ✓）
            //   这正是把它从卡片底部挪到顶部的第二个好处 ✓
            if (alertsH < 150)
            {
                // ★ 最小 176 ✗（2026-09-13 修「控件跑出卡片」）
                //   这张卡片里最靠下的标签在 y=156..173（「已巡检 N 轮 / 告警 N 次」）
                //   原来允许缩到 110 ✗ 那三个标签就掉到卡片外面了 ✓
                //   —— 这是 tools\check_layout.ps1 抓出来的（光看截图看不出来 ✓）
                resultH = Math.Max(176, bodyH - 150 - gapBetween);
                alertsH = bodyH - resultH - gapBetween;
            }
            if (alertsH < 90) alertsH = 90;            // 实在太小就认了（窗口已到最小尺寸）

            _cardResult.Size = new Size(rightW, resultH);
            _cardAlerts.Location = new Point(M + leftW + 10, top + resultH + gapBetween);
            _cardAlerts.Size = new Size(rightW, alertsH);
            _cardPrompt.Location = new Point(M, top + bodyH + 10);
            _cardPrompt.Size = new Size(W - M * 2, H - (top + bodyH + 10) - 12);

            // ★ 这里**不要**再设 _urlBox.Width ✗（2026-09-13 修重叠）
            //   地址框的 Anchor 是 Top|Left|Right ✓ 窗口一变它自己会拉伸 ✓
            //   而这里又按公式设了一次 ✗ **两个一起调就重了** ✓
            //   实测症状：地址框伸到「显示」按钮底下 ✓ 重叠 54x23px（正好是按钮大小）✗
            //   —— 而且这个重叠**在每一个窗口尺寸下都存在** ✗
            //      是 tools\check_layout.ps1 抓出来的（看截图完全看不出来 ✓）
            //   宽度只在 BuildUi 里设一次 ✓ 之后交给 Anchor ✓
            _statusLab.Width = Math.Max(120, W - M * 2 - 570 - 16);
            // ★ 本机地址那一行（2026-09-14 加）也要在这里重排 ✗
            //   **BuildUi 里 new 出来的控件，LayoutCards 里都要有对应的一行** ✓
            //   这个规则今天已经是第 7 次被提醒了（前面 6 次都是漏掉一个控件 ✓）
            //   这一行的宽度分配：
            //     下拉 88..488（400）→ 刷新 496..552（56）→ 提示 560..卡片右边
            _ipBox.Location = new Point(88, 130);
            _ipHint.Location = new Point(560, 132);
            _ipHint.Width = Math.Max(120, W - M * 2 - 560 - 16);
            _ipRefreshBtn.Location = new Point(496, 128);
            _streamPick.Location = new Point(W - M * 2 - 284, 52);
            // ★ 「网络搜索」按钮原来**压根没在 LayoutCards 里重新定位** ✗
            //   只有 BuildUi 给了个 -296 ✗ 而那个位置和「从流列表选」重叠 100px ✓
            //   → 按钮被完全盖住 ✓ 用户从来没见过它 ✓✓
            //   这是我这轮加的「控件两两重叠检测」翻出来的 ✓
            _onvifBtn.Location = new Point(W - M * 2 - 404, 50);
            _loadBtn.Location = new Point(W - M * 2 - 104, 50);
            _descLab.Width = rightW - 32;
_descLab.Height = Math.Max(36, _cardResult.Height - 90 - 66);   // 下面判定标签在 y=154 ✗ 必须留够
            // 右对齐：按标签实际宽度算，避免和左边的「耗时 xx ms」叠在一起
            _roundLab.Location = new Point(Math.Max(200, rightW - 16 - _roundLab.PreferredWidth), 156);
            // ★ 表格上方多了一行搜索框（y=52..82）✓ 所以高度要减 82 不是 52 ✓
            _grid.Size = new Size(rightW - 32, _cardAlerts.Height - 82 - 40);
            _clearBtn.Location = new Point(16, _cardAlerts.Height - 32);
            // ★ 「事件流水」按钮原来没在这里重新定位 ✗（用户截图指出被遮挡 ✓）
            //   和「网络搜索」按钮是同一个 bug：BuildUi 里定位了，LayoutCards 里忘了 ✓
            _logBtn.Location = new Point(112, _cardAlerts.Height - 32);
            // ★ 搜索框那一行也要在这里重排 ✗（不然就是第四个漏掉的 ✓）
            //   规则：**BuildUi 里 new 出来的控件，LayoutCards 里都要有对应的一行**
            // ★ 搜索框宽度要留出右边的「匹配模式」下拉（2026-09-13）
            // 宽度重算：右边要留 三个勾选 118 + 模式下拉 52 + 计数 40 + 边距 32 = 242
            //           左边是「搜索」标签 + 8 + 左边距 16 = 58
            _alertSearch.Size = new Size(Math.Max(60, rightW - 58 - 258), 22);
            Control[] slArr = _cardAlerts.Controls.Find("alertSearchLab", false);
            if (slArr.Length > 0) slArr[0].Location = new Point(16, 56);
            _alertCount.Location = new Point(rightW - 32 - 34, 57);
            // ★★ 搜索行右边那些控件也要在这里重排（2026-09-13，第六次犯这个毛病 ✗）
            //   检查器报：「CheckBox [动] 控件 x=419..458 卡片 436」✗
            //   根因还是 **BuildUi 里定位了、LayoutCards 里没更新** ✓
            //   —— 和「网络搜索」「事件流水」「搜索框」是同一个 bug ✓
            //
            //   ★ 间距按 CheckBox 的**实际宽度 39px** 算 ✗
            //     第一版按 34px 排，检查器报「重叠 5x21px」✗
            //   右起：计数 34 ｜ 动 39 ｜ 车 39 ｜ 人 39 ｜ 下拉 52 ｜ 输入框
            _alertMode.Location = new Point(rightW - 32 - 68 - 128, 53);
            _chkPerson.Location = new Point(rightW - 32 - 124, 55);
            _chkCar.Location = new Point(rightW - 32 - 87, 55);
            _chkMove.Location = new Point(rightW - 32 - 50, 55);
            _openDirBtn.Location = new Point(rightW - 32 - 108, _cardAlerts.Height - 32);
            // ★ 提示词输入框要给下面那行「输出文件」留 24px（2026-09-12 修遮挡）：
            //   原来输入框底边 = 卡片高 - 16 ✗ 而标签定位在卡片高 - 26 ✗
            //   → 标签整个被输入框盖住 ✓ 最大化窗口时特别明显 ✓ 用户截图指出来了 ✓
            //   现在：输入框底边落在 H-40 ✓ 标签放在 H-32 ✓ 不再重叠 ✓
            int boxH2 = _cardPrompt.Height - 52 - 16 - 24;
            if (boxH2 < 40) boxH2 = 40;                  // 窗口很矮时保留最小可用高度
            _promptBox.Size = new Size(W - M * 2 - 32, boxH2);
            _outLab.Location = new Point(16, _cardPrompt.Height - 32);
            _outLab.Size = new Size(W - M * 2 - 32, 20);
        }

        private Label MkLabel(Control parent, string text, int x, int y, Font f)
        {
            Label l = new Label();
            l.Text = text; l.Font = f; l.AutoSize = true; l.Location = new Point(x, y);
            l.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            parent.Controls.Add(l);
            return l;
        }

        // ==================== 主题 ====================

        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke((MethodInvoker)OnThemeChanged); return; }
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            // 统一走主题引擎：它会把卡片、按钮、表格、标签一次性刷成当前主题 ✓
            // （不要自己 foreach 刷子控件 —— 会漏掉自绘控件内部的同步 ✓）
            Themes.ApplyTo(this);
            BackColor = Themes.Current.Bg;
            SetVerdictVisual(null, 0);
        }

        // ==================== 流列表 ====================

        /// <summary>从 RTSP 页保存的加密流列表里读摄像头（复用它的解析函数，不重复实现）。</summary>
        private void LoadStreamList()
        {
            _streams = new List<StreamEntry>();
            try
            {
                string f = AppPaths.Data("rtsp_streams.dat");
                string text = Secret.UnprotectFromFile(f);
                if (text != null && text.Length > 0)
                {
                    int b, d;
                    List<StreamEntry> got = RtspForm.ParseStreamLines(
                        text.Replace("\r\n", "\n").Split('\n'), _streams, out b, out d);
                    _streams.AddRange(got);
                }
            }
            catch (Exception ex) { Log.Warn("AI 巡检：读取流列表失败 " + ex.Message); }

            _streamPick.Items.Clear();
            _streamPick.Items.Add("（从流列表选…）");
            foreach (StreamEntry e in _streams)
            {
                string name = e.Remark.Length > 0 ? e.Remark : e.Url;
                if (name.Length > 22) name = name.Substring(0, 22) + "…";
                _streamPick.Items.Add(name);
            }
            _streamPick.SelectedIndex = 0;
        }

        private void OnPickStream(object sender, EventArgs e)
        {
            int i = _streamPick.SelectedIndex - 1;
            if (i >= 0 && i < _streams.Count) SetUrl(_streams[i].Url);
        }

        // ==================== 巡检主流程 ====================

        private static string OutFile
        {
            get { return AppPaths.Data("live_alerts.csv"); }
        }

        /// <summary>
        /// 巡检运行中，锁定"途中不该改"的控件。
        ///
        /// ★ 为什么做成一个方法（2026-09-13）
        ///   原来这 8 行散在 5 个地方 ✗（OnStart / OnStop / 三个失败回滚 / StartAlarmMode）
        ///   加一个控件就得改 5 处 ✓ 漏一处就是 bug ✓
        ///   —— 和导航 NavItems、使用说明那两次是同一族问题：**同一个事实写多遍** ✓
        ///
        /// ★ 用户要求：「开始巡检之后就不允许修改模型了。不停止，不许改模型」
        ///   但**只锁界面是不够的** ✗：
        ///     模型是三个 AI 页面共用的一份配置 ✓
        ///     用户在「设置」页改一下，照样会影响正在跑的巡检 ✓
        ///     而下面三轮调用原来是每轮都重读 AppSettings.OllamaModel ✗
        ///     → 同一次巡检里前后用的模型会不一样 ✓ 结果没法比较 ✓
        ///   所以 running=true 时把模型**抓进 _runModel** ✓
        ///   跑的这一轮永远用它 ✓ 界面也锁上 ✓ **双保险** ✓
        /// </summary>
        // ==================== 地址框的明文/打码 ====================
        //
        // ★ 这一段是照抄 RtspForm 的（2026-09-13）
        //   用户截图指出：AI 实时巡检页的地址框**明文显示密码** ✗
        //   而 RTSP 页早就做了打码 ✓ —— 同一个功能只做了一半 ✓
        //   **不重新发明** ✓ 直接把验证过的实现搬过来（含下面 HookUrlBox 的细节 ✓）

        /// <summary>
        /// 取**真实**流地址。
        /// ★ 不要直接读 `_urlBox.Text` ✗ —— 那里可能是打码后的假地址
        ///   （`rtsp://admin:***@…`）✓ 拿它去拉流会直接失败 ✓
        /// 输入框获得焦点时里面是真实地址（否则没法编辑），所以焦点状态要额外同步一次 ✓
        /// </summary>
        private string ReadUrl()
        {
            if (_urlBox != null && _urlBox.Focused) _realUrl = _urlBox.Text.Trim();
            return _realUrl == null ? "" : _realUrl.Trim();
        }

        /// <summary>设置流地址：内部存真实值，界面按当前状态决定显示明文还是打码。</summary>
        private void SetUrl(string url)
        {
            _realUrl = url == null ? "" : url.Trim();
            SyncUrlDisplay();
        }

        /// <summary>刷新输入框显示：编辑中或已点「显示」→ 明文；否则打码。</summary>
        private void SyncUrlDisplay()
        {
            if (_urlBox == null) return;
            bool reveal = _urlRevealed || _urlBox.Focused;
            string shown = reveal ? _realUrl : Secret.MaskUrl(_realUrl);
            if (_urlBox.Text != shown) _urlBox.Text = shown;
            if (_showUrlBtn != null) _showUrlBtn.Text = _urlRevealed ? "隐藏" : "显示";
        }

        /// <summary>给地址框挂焦点事件：主动点进去才露明文，离开时收回去。</summary>
        private void HookUrlBox()
        {
            // ★ 关键：不让它自动获得焦点
            //   地址框是页面上第一个可聚焦控件 ✓ 窗体一打开就会自动聚焦它 ✓
            //   于是「聚焦即显示明文」的规则立刻生效 ✓
            //   **屏幕上还是明摆着密码** ✗
            //   设成 TabStop=false 后，只有用户**主动点进去**才显示明文 ✓
            //   —— 这个细节是我从 RtspForm 抄来的 ✓ 自己写肯定想不到 ✗
            if (_urlBox != null) _urlBox.TabStop = false;

            _urlBox.GotFocus += delegate (object s, EventArgs e) { SyncUrlDisplay(); };
            _urlBox.LostFocus += delegate (object s, EventArgs e)
            {
                // 编辑期间用户看到并改的就是真实地址 ✓ 离开时存下来再打码 ✓
                _realUrl = _urlBox.Text.Trim();
                SyncUrlDisplay();
            };
        }

        /// <summary>
        /// 按关键词过滤巡检记录。
        ///
        /// ★ 照抄 VideoSearchForm.DoSearch()（2026-09-13）
        ///   那条路已经验证过，逻辑是：
        ///     · 关键词按 空格 / 中文逗号 / 英文逗号 / 分号 / 冒号 分开 ✓
        ///     · **任一命中就显示**（模糊搜索 ✓ 不是全部都要命中）
        ///     · 命中词数多的**排到前面** ✓
        ///   不重新发明 —— 见交接文档 29.5：「新写一段和已有路径做同样事的代码时，
        ///   去把那条路径完整读一遍」
        ///
        /// 搜的是「判定 + 内容」两列 ✓（时间列搜不了，那是格式化过的时间 ✓）
        /// </summary>
        /// <summary>
        /// 「人 / 车 / 动」三个语义组的词表。
        ///
        /// ★ 2026-09-13 加。用户说：「在实况巡检文搜里做三个选项，
        ///   人、车、动的选项筛选么？」
        ///
        /// ★ 为什么需要它 —— AI 的描述是**自然语言** ✗
        ///   它不会每次都写「人」字，而是写「一名男子」「有行人经过」「画面中无人」✓
        ///   所以搜「人」搜不到那些记录 ✓ 用户以为"搜不了" ✓
        ///   ★ 勾上「人」= 一次匹配一整组近义词 ✓✓
        ///
        /// ★ 想加词就往对应那一组里加 ✓ 加完就生效（不用改别的地方）✓
        ///   注意：**短词会带来误判** ✗
        ///     比如「动」会命中「自动」「动作」✓ 但也会命中「移动侦测」这种
        ///     系统自己写的词 ✓ —— 那是有意保留的（用户可能正想找它）✓
        /// </summary>
        private static readonly string[][] WordGroups = new string[][] {
            // 组名, 之后是这个词组的成员
                        // ★★ 词表要"收紧"✗ 不能贪多（2026-09-13 实测修）
            //
            //   第一版写得太宽 ✓ 实测发现明显的误匹配：
            //     组「车」里的单字 "车" → 命中了「画面中**停车**场没有人员出现」✗✗
            //       那是"停车场"这个词里的一个字 ✓ 跟有没有车完全无关 ✓
            //     组「人」里的单字 "男" / "女" 同理 ✓
            //     组「动」里的 "动" / "拉" / "推" / "跑" 也会乱中 ✓
            //
            //   ★ 中文没有词边界 ✗ 所以**单字词基本不能用** ✓
            //     要么用双字以上的词 ✓ 要么接受误匹配 ✓
            //   ★ 但"动"这一组还有个特点：它的词之间**本来就没有共同词根** ✓
            //     （走动/行驶/翻越/经过/出现 —— 都不含"动"字 ✓）
            //     所以这一组只能靠**列举** ✗ 列不到的就不命中 ✓
            //
            //   ★ 用户要知道的事：**这三个勾选是"宁滥勿缺"** ✗
            //     它保证"不漏掉可能相关的"✓ 但不保证"每一条都真的相关"✓
            //     （比如"停车场"里的"车" —— 收紧之后已经没有这种了 ✓
            //       但"画面中出现"仍会命中"动" ✓ 那是有意的：
            //       "出现"确实是"有东西动了"的意思 ✓）
            new string[] { "人", "人", "男子", "女子", "行人", "人员", "有人", "无人",
                                 "身影", "小孩", "老人", "居民", "业主",
                                 "陌生人", "外来", "路人", "访客", "面孔", "男子汉" },
            new string[] { "车", "汽车", "卡车", "轿车", "货车", "面包车", "电动车",
                                 "摩托车", "自行车", "三轮", "车辆", "车牌", "辆车",
                                 "驶入", "驶出", "逆行", "占道" },
            new string[] { "动", "移动", "走动", "行驶", "经过", "进出", "靠近",
                                 "变化", "翻越", "搬运", "逗留", "徘徊", "离开",
                                 "出现", "消失", "攀爬" }
        };
        /// <summary>
        /// 按组名取这一组的**词**（★ 不含组名本身 ✗）。
        ///
        /// ★★ 这里原来错了一版（2026-09-13 实测修）：
        ///   原来直接 `return WordGroups[i]` ✗ —— 那个数组的 **[0] 是组名** ✓
        ///   于是匹配的时候把组名也当成一个词 ✓
        ///   → 「车」这一组的"车"又回来了 ✗
        ///   → 实测：「画面中**停车**场没有人员出现」又被命中 ✓
        ///   ★ 我明明把单字"车"从词表里去掉了 ✗ 但它从组名那里溜回来了 ✓
        ///     —— "改一处漏一处"的又一个变种 ✓
        /// </summary>
        private static string[] WordsOf(string group)
        {
            for (int i = 0; i < WordGroups.Length; i++)
            {
                if (WordGroups[i][0] == group)
                {
                    // ★ 跳过 [0]（组名）✓ 只取真正的词 ✓
                    int n = WordGroups[i].Length - 1;
                    if (n <= 0) break;
                    string[] w = new string[n];
                    for (int k = 0; k < n; k++) w[k] = WordGroups[i][k + 1];
                    return w;
                }
            }
            return new string[0];
        }

        private void FilterAlerts()
        {
            if (_grid == null || _alertSearch == null) return;
            string q = _alertSearch.Text.Trim();

                        // ★★ 搜索框留空时**不能直接全显示** ✗（2026-09-13 修一个真 bug）
            //
            //   原来这里无条件 return ✓ 于是：
            //     「只想看有人的记录」+ 搜索框留空
            //     → 走的就是"全部显示"✗ 三个筛选项根本不起作用 ✗✗
            //   实测踩到了：勾「人」还是 7/7 条 ✓
            //
            //   ★ 改法：**只有"搜索框空 且 一个语义组都没勾"时**才全显示 ✓
            //     只要勾了任意一组 ✓ 就往下走匹配逻辑 ✓
            bool noGroup = (_chkPerson == null || !_chkPerson.Checked)
                        && (_chkCar == null || !_chkCar.Checked)
                        && (_chkMove == null || !_chkMove.Checked);
            if (q.Length == 0 && noGroup)
            {
                for (int r2 = 0; r2 < _grid.Rows.Count; r2++) _grid.Rows[r2].Visible = true;
                if (_alertCount != null) _alertCount.Text = "";
                return;
            }

            // ★★ 匹配模式 + 语义组（2026-09-13 用户要求）
            //
            //   模式：
            //     模糊（默认）—— 拆成多个关键词，**命中任意一个**就显示
            //                     搜「人 车」= 有人的 OR 有车的
            //     精确        —— 整句按原样匹配，**不拆词**，关键词必须全部命中
            //                     搜「红色卡车」= 必须有这四个字连在一起
            //
            //   语义组（人 / 车 / 动）：
            //     ★ 解决用户真正的痛点 —— AI 描述是自然语言，
            //       它写「一名男子」「有行人经过」，**不含「人」字**
            //       所以搜「人」搜不到 ✓ 勾上「人」= 一次匹配一整组近义词 ✓
            bool exact = (_alertMode != null && _alertMode.SelectedIndex == 1);

            List<string> groupWords = new List<string>();
            if (_chkPerson != null && _chkPerson.Checked) groupWords.AddRange(WordsOf("人"));
            if (_chkCar != null && _chkCar.Checked) groupWords.AddRange(WordsOf("车"));
            if (_chkMove != null && _chkMove.Checked) groupWords.AddRange(WordsOf("动"));

            // 精确模式：整句当一个关键词（不拆）
            string[] keys = exact
                ? new string[] { q }
                : q.Split(new char[] { ' ', '　', ',', '，', ';', '；', ':', '：' },
                          StringSplitOptions.RemoveEmptyEntries);
            // ★★ 这里也要判断语义组（2026-09-13 修，第二个 return ✗）
            //   上面那个"空搜索框 → 全显示"我改了 ✓ 但**这里还有一个** ✗
            //   搜索框留空时 keys 是空数组 ✓ 于是这里又 return 了 ✓
            //   结果：勾「人」还是全显示 ✗ 实测 8/8 条 ✓
            //   ★ 又是"改一处漏一处" —— 同一件事在两个地方判断 ✓
            if (keys.Length == 0 && noGroup)
            {
                for (int r = 0; r < _grid.Rows.Count; r++) _grid.Rows[r].Visible = true;
                if (_alertCount != null) _alertCount.Text = "";
                return;
            }

            List<DataGridViewRow> hits = new List<DataGridViewRow>();
            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                DataGridViewRow row = _grid.Rows[r];
                string hay = "";
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    object v = row.Cells[c].Value;
                    if (v != null) hay += Convert.ToString(v) + " ";
                }
                int score = 0;
                for (int k = 0; k < keys.Length; k++)
                {
                    if (keys[k].Length > 0 &&
                        hay.IndexOf(keys[k], StringComparison.OrdinalIgnoreCase) >= 0) score++;
                }
                // ★ 语义组：**任一命中就算这一组命中** ✓
                //   （不是"全部命中"✗ 那样反而搜不到东西 ✓）
                bool groupHit = false;
                for (int g = 0; g < groupWords.Count; g++)
                {
                    if (groupWords[g].Length > 0 &&
                        hay.IndexOf(groupWords[g], StringComparison.OrdinalIgnoreCase) >= 0)
                    { groupHit = true; break; }
                }

                // ★ 判定：
                //   精确模式 → 关键词必须**全部**命中（AND）✓ 这才是"精确"的意义 ✓
                //   模糊模式 → 命中**任意一个**就显示（OR）✓
                //   ★ 勾了语义组 → **关键词和组之间是 OR** ✓
                //     （勾"人" + 搜"翻越" = 有人的 OR 翻越的 ✓ 这样更实用 ✓）
                bool kwOk = exact ? (score == keys.Length) : (score > 0);
                if (kwOk || groupHit) hits.Add(row);
            }

            // 先全部藏起来，再把命中的放出来
            for (int r = 0; r < _grid.Rows.Count; r++) _grid.Rows[r].Visible = false;
            for (int i = 0; i < hits.Count; i++) hits[i].Visible = true;

            // ★ 这里**故意不照抄**文搜页的"按匹配度排序" ✗（2026-09-13）
            //
            //   文搜页要排序 ✓ 因为那是在**整部片子**里找东西 ✓
            //     "哪几段最相关"是它的核心价值 ✓
            //   但**告警记录是一条时间线** ✓
            //     拍平重排会打乱时间顺序 ✓ 反而看不懂"什么时候发生了什么" ✗
            //     （而且 DataGridViewRowCollection 也没有 SetChildIndex ✓
            //       它只有 Control 才有 ✓ 想重排得把行删掉再加一遍 ✓ 很别扭 ✓）
            //
            //   ★ 教训：照抄"实现"是对的 ✓ **但照抄时要停下来想"这个逻辑在这里成不成立"** ✓
            //     这次是编译器先拦住了我（没有 SetChildIndex）✓
            //     但正确的理由不是"编不过" ✓ 而是"时间线不该重排" ✓
            for (int i = 0; i < hits.Count; i++) hits[i].Visible = true;

            if (_alertCount != null)
                _alertCount.Text = hits.Count + "/" + _grid.Rows.Count;
            // ★ 状态栏说清楚用了哪些条件（否则用户不知道"为什么少了"）
            StringBuilder cond = new StringBuilder();
            cond.Append("「").Append(q).Append("」");
            if (exact) cond.Append("（精确）");
            if (groupWords.Count > 0)
            {
                cond.Append(" + ");
                if (_chkPerson.Checked) cond.Append("人");
                if (_chkCar.Checked) cond.Append("车");
                if (_chkMove.Checked) cond.Append("动");
            }
            _statusLab.Text = "记录筛选： " + cond.ToString() + "  命中 " + hits.Count + " 条";
        }

        private void SetRunLock(bool running)
        {
            _urlBox.ReadOnly = running;
            _startBtn.Enabled = !running;
            _stopBtn.Enabled = running;
            _modeBox.Enabled = !running;
            _modelBox.Enabled = !running;          // ★ 用户要求：不停止不许改模型
            _fetchModelBtn.Enabled = !running;

            if (running)
            {
                // 抓住这次巡检要用的模型（后面每一轮都用它，不再读全局配置）
                _runModel = AppSettings.OllamaModel;
                try
                {
                    _modelTip.SetToolTip(_modelBox,
                        "巡检进行中，模型已锁定 —— 先点「停止」才能改。\n\n"
                      + "本次巡检使用的模型：" + _runModel + "\n\n"
                      + "为什么锁：模型是三个 AI 页面共用的配置，\n"
                      + "跑的过程中换掉，同一次巡检前后用的模型就不一样了，结果没法比较。");
                }
                catch (Exception) { }
            }
            else
            {
                _runModel = "";
                try
                {
                    _modelTip.SetToolTip(_modelBox,
                        "这一页用哪个视觉模型。\n\n"
                      + "· 和「设置」页是同一份配置，这里改了，AI 内容理解 / 文搜 / 本页三处一起变\n"
                      + "· 点右边「获取」可以从 Ollama 拉本机已装的模型，列在下拉里\n"
                      + "· 必须是带 vl 的视觉模型（能看图），比如 qwen2.5vl:7b\n"
                      + "· Ollama 没启动时也可以手动输入模型名\n"
                      + "· 巡检开始后会自动锁定，先「停止」才能改");
                }
                catch (Exception) { }
            }
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (_running) return;
            string url = ReadUrl();
            if (url.Length == 0) { _statusLab.Text = "请先填摄像头地址，或从右边下拉里选一路已保存的。"; return; }
            if (!url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            { _statusLab.Text = "地址要以 rtsp:// 开头（例如 rtsp://admin:密码@192.168.1.10:554/…）"; return; }

            int sec = 10;
            int.TryParse(_intervalBox.Text.Trim(), out sec);
            if (sec < 5) sec = 5;   // 下限 5 秒：本地 7B 模型一次推理也要 1~3 秒（仅轮询模式用）
            if (sec > 600) sec = 600;
            _intervalBox.Text = sec.ToString();

            if (!File.Exists(Ffmpeg.FfmpegBin()))
            { MessageBox.Show(this, "找不到 ffmpeg，无法抓帧。请确认 ffmpeg\\bin 目录还在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            _running = true;
            _stop = false;
            _round = 0;
            _alerts = 0;
            _evtCount = 0;
            _started = DateTime.Now;
            UpdateRoundLabel();

            SetRunLock(true);

            // ── 报警接收模式（默认）：起一个 HTTP 服务，等摄像头把报警和图片推过来 ──
            //    实测这台海康 DS-2CD7A47EWD-XZS 的 alertStream 只推「异常类」事件 ✗
            //    移动侦测走的是「联动方式 → 上传中心」= 摄像头主动 POST ✓
            //    和 HCNetSDK 的「报警布防」同一个机制 ✓ 但不用带厂商 DLL ✓
            if (_modeBox.SelectedIndex == 0)
            {
                StartAlarmMode(url);
                return;
            }

            // ── ONVIF 订阅模式：跨品牌的"变化才分析"（2026-09-13 加）──
            //   和前两种的区别：它走 ONVIF 标准 ✓ 不依赖厂商私有协议 ✓
            //   实测海康和 TP-Link 都支持 ✓ 而且能拿到移动侦测 ✓
            if (_modeBox.SelectedIndex == 3)
            {
                StartOnvifMode(url);
                return;
            }

            // ── 事件订阅模式：挂着摄像头的事件流，变化了才分析 ──
            //    这是用户提议的做法 ✓ 比定时轮询好得多：
            //      · 摄像头上**零 RTSP 会话、零轮询** ✓
            //      · 不浪费 AI 去分析一成不变的画面 ✓
            //      · 短事件也能抓到（轮询会漏）✓
            if (_modeBox.SelectedIndex == 1)
            {
                _target = HikIsapi.Parse(url);
                if (!_target.Ok)
                {
                    _running = false;
            SetRunLock(false);
                    _statusLab.Text = "事件订阅需要完整地址：" + _target.Error;
                    MessageBox.Show(this, "事件订阅模式需要地址里带账号密码，例如\n"
                        + "rtsp://admin:密码@192.168.1.64:554/Streaming/Channels/101\n\n" + _target.Error,
                        "地址不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                StartEventMode();
                AppendAlert(DateTime.Now, "—", "开始【事件订阅】巡检：" + Secret.MaskUrl(url)
                    + "（有事件才分析，摄像头上不建 RTSP 会话）", Color.Gray);
                Log.Info("AI 实时巡检【事件订阅】开始：" + Secret.MaskUrl(url));
                return;
            }

            // ── 定时轮询模式（原来的做法）──
            _statusLab.Text = "巡检中：每 " + sec + " 秒一帧……";

            if (_timer == null)
            {
                _timer = new System.Windows.Forms.Timer();
                _timer.Tick += delegate(object s2, EventArgs e2) { InspectOnce(); };
            }
            _timer.Interval = sec * 1000;
            _timer.Start();
            InspectOnce();   // 不等第一个间隔，立刻来一轮

            AppendAlert(DateTime.Now, "—", "开始巡检：" + Secret.MaskUrl(url) + "，间隔 " + sec + " 秒", Color.Gray);
            Log.Info("AI 实时巡检开始：" + Secret.MaskUrl(url) + "，间隔 " + sec + " 秒");
        }

        /// <summary>
        /// 「获取」按钮：从 Ollama 拉本机已装的模型列表，填进下拉框。
        /// ★ 在后台线程做 ✓ 不然 Ollama 没启动时会卡住界面好几秒 ✓
        /// （和 SettingsForm 里那个「自动获取模型」是同一套做法 ✓）
        /// </summary>
        private void OnFetchModels(object sender, EventArgs e)
        {
            _fetchModelBtn.Enabled = false;
            string old = _fetchModelBtn.Text;
            _fetchModelBtn.Text = "获取中";
            string url = AppSettings.OllamaUrl;
            ThreadPool.QueueUserWorkItem(delegate (object _)
            {
                List<string> models = null;
                string err = null;
                try { models = MageCheck.FetchModels(url); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate ()
                        {
                            try
                            {
                                _fetchModelBtn.Text = old;
                                _fetchModelBtn.Enabled = true;
                                if (models == null || models.Count == 0)
                                {
                                    MessageBox.Show(this,
                                        "没取到模型列表。\n\n请确认：\n" +
                                        "  · Ollama 已经启动（地址：" + url + "）\n" +
                                        "  · 已经拉过带 vl 的视觉模型（ollama pull qwen2.5vl:7b）\n" +
                                        (err != null ? "\n错误：" + err : ""),
                                        "获取模型列表", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                    return;
                                }
                                string keep = _modelBox.Text.Trim();
                                _modelBox.Items.Clear();
                                for (int i = 0; i < models.Count; i++) _modelBox.Items.Add(models[i]);
                                // 保住用户当前选的（列表里没有也保留，允许手输）
                                if (keep.Length > 0)
                                {
                                    if (!_modelBox.Items.Contains(keep)) _modelBox.Items.Insert(0, keep);
                                    _modelBox.Text = keep;
                                }
                                _fetchModelBtn.Text = "获取 " + models.Count;
                            }
                            catch (Exception) { }
                        });
                    }
                }
                catch (Exception) { }
            });
        }

        /// <summary>
        /// 打断在途的 AI 推理和抓帧。
        ///
        /// ★★ 为什么要有这个方法（2026-09-13 用户指出）：
        ///
        ///   用户说：**「你拉取完 Ollama 的流后，好像停止之后没有终止符去停止拉取 ollama 流」**
        ///   ★ 他说对了 ✗ —— 而且这条页面是**唯一漏了**的那条 ✓
        ///
        ///   实测（审计出来的）：
        ///     RtspForm.cs                  AbortCurrent=2  KillRunning=2  ✓
        ///     VideoSearchForm.cs           AbortCurrent=1  KillRunning=1  ✓
        ///     VideoSearchForm.Timeline.cs  AbortCurrent=1  KillRunning=1  ✓
        ///     **LiveAiForm.cs               AbortCurrent=0  KillRunning=0** ✗✗
        ///     **LiveAiForm.Onvif.cs         AbortCurrent=0  KillRunning=0** ✗✗
        ///
        ///   后果（这条页面恰恰是等得最久的）：
        ///     实时巡检一轮要 2~40 秒 ✓ 用户按了「停止」✓
        ///     界面显示"已停止"✓ 但后台那个线程**还阻塞在 HTTP 读里** ✗
        ///     要等到 Ollama 把整段生成完才回来（最长 180 秒）✓
        ///     —— 也就是用户感觉的"按了停止它还在拉" ✓
        ///
        ///   为什么"置标志位"不够（别的页面早就写在注释里了 ✓ 这里又忘了一次）：
        ///     标志位只在**每轮开头**检查 ✓ 线程此刻正卡在阻塞调用里 ✓
        ///     它根本走不到检查那一步 ✓ → **必须把在途调用直接掐断** ✓
        ///
        ///   两件事都要做 ✗ 少一个都不够：
        ///     · AbortCurrent()：掐断 Ollama 的 HTTP 读 ✓
        ///       （socket 一断，Ollama 那边**会看到"客户端没了"从而停下生成** ✓
        ///         这就是用户说的"终止符"—— 我们这边的终止符是 **socket 断开** ✓
        ///         它比发一个"别生成了"的报文更可靠：**不需要对方配合** ✓）
        ///     · KillRunning()：掐断正在抓帧的 ffmpeg ✓
        ///       （抓帧最长 20 秒 ✓ 只掐 AI 的话，它还得等抓帧超时 ✓）
        ///
        /// ★ 抽成一个方法，不在两处各写一遍（同一个教训今天已经犯过两次 ✓）
        /// </summary>
        private void StopInFlight()
        {
            try { MageCheck.AbortCurrent(); } catch (Exception) { }
            try { Ffmpeg.KillRunning(); } catch (Exception) { }
        }

        private void OnStop(object sender, EventArgs e)
        {
            if (!_running) return;
            _running = false;
            _stop = true;
            _evtRunning = false;
            // ★ 先把在途的推理/抓帧掐断，再收摊（2026-09-13 加，见 StopInFlight）
            StopInFlight();
            // ★ ONVIF 订阅线程也要停（2026-09-13 加）
            //   它的循环是 while (!_stop && _running) ✓ 靠上面两个标志退出 ✓
            //   再显式置一次是为了让"正在重建订阅"那个可打断等待也立刻退出 ✓
            _onvifRunning = false;
            if (_alarm != null) { _alarm.Stop(); }
            if (_timer != null) _timer.Stop();
            SetRunLock(false);
            _statusLab.Text = "已停止。共巡检 " + _round + " 轮，告警 " + _alerts + " 次。";
            // ★★ 停止时把摄像头的「报警上传」地址还原（2026-09-13 加）
            //
            //   用户问：「我用完了，你会进摄像头改回默认的旧地址么？」
            //   ★ 那时候的答案是"不会" ✗ —— 而那是错的 ✓
            //   不还原的话：程序停了 ✓ 摄像头还一直往这个地址推 ✓
            //   推到没人接 ✓ 而且下次换台电脑跑，它还是指着旧地址 ✓
            string rb = HikIsapi.RestoreUploadHost();
            if (rb.Length > 0)
            {
                AppendAlert(DateTime.Now, "—", rb, Color.Gray);
                Log.Info(rb);
            }
            AppendAlert(DateTime.Now, "—", "停止巡检", Color.Gray);
            Log.Info("AI 实时巡检停止：共 " + _round + " 轮，告警 " + _alerts + " 次");
        }

        // ==================== 报警接收模式 ====================

        /// <summary>
        /// 起一个 HTTP 服务等着摄像头报警上传。
        ///
        /// 为什么用这条路而不是 alertStream（实测逼出来的 ✓）：
        ///   这台海康 DS-2CD7A47EWD-XZS 的 alertStream **只推「异常类」事件** ✗
        ///   实测 3 分钟 20 条全是 videoloss，一条移动侦测都没有 ✓
        ///   而 /ISAPI/Event/capabilities 明确写着 isSupportMotionDetection = true ✓
        ///   移动侦测走的是「联动方式 → 上传中心」= **摄像头主动 POST 到我们** ✓
        ///   —— 这正是用户说的「画面变化时它推送图片」✓ 也是 HCNetSDK「报警布防」的同一机制 ✓
        ///   但摄像头本身就支持 HTTP 上传 ✓ **不用带 50MB 厂商 DLL** ✓
        /// </summary>
        private void StartAlarmMode(string url)
        {
            HikIsapi.Target tg = HikIsapi.Parse(url);
            if (!tg.Ok)
            {
                _running = false;
            SetRunLock(false);
                _statusLab.Text = "报警接收需要完整地址：" + tg.Error;
                MessageBox.Show(this, "报警接收模式需要地址里带账号密码，例如\n"
                    + "rtsp://admin:密码@192.168.1.64:554/Streaming/Channels/101\n\n"
                    + "（账号密码用来在启动时自动把「上传中心地址」告诉摄像头）\n\n" + tg.Error,
                    "地址不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_alarm == null)
            {
                _alarm = new AlarmServer();
                _alarm.Port = 18080;
                _alarm.OnStatus = delegate (string s) { Ui(delegate () { _statusLab.Text = s; }); };
                _alarm.OnAlarm = delegate (AlarmPush p) { OnAlarmPush(p); };
            }
            if (!_alarm.Start())
            {
                _running = false;
            SetRunLock(false);
                string tips = "端口 18080 起不来。可能原因：\n"
                    + "· 已被别的程序占用（换个端口再试）\n"
                    + "· 需要用管理员身份运行本程序\n\n"
                    + "以管理员身份在命令行执行一次这个可以放宽限制：\n"
                    + "netsh http add urlacl url=http://+:18080/ user=Everyone";
                _statusLab.Text = "报警接收服务启动失败";
                MessageBox.Show(this, tips, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // ★★ 本机地址：**用户指定的优先** ✗ 没指定才自动挑（2026-09-14 用户要求）
            //
            //   用户说：「能不能做一个自动获取IP和手动设置IP的功能进去，
            //           这样可以防止局域网地址不对导致连不上的问题」
            //   ★ 原来这里写的是 `ips[0]` ✗ —— "第一个"是哪个完全看枚举顺序 ✓
            //     多网卡的机器（有线+无线+虚拟机+VPN）很容易挑错 ✗
            //     挑错 → 摄像头往收不到的地址推 → 一条报警都收不到 ✓
            //   现在是：AppSettings.UploadIp 非空就用它 ✓ 空才自动 ✓
            List<AlarmServer.LocalIp> cand = AlarmServer.AllLocalIPs(tg.Host);
            string wantedIp = AppSettings.UploadIp == null ? "" : AppSettings.UploadIp;
            string myIp = AlarmServer.PickIp(tg.Host, wantedIp);
            if (myIp.Length == 0)
            {
                // ★ 两种"挑不出来"要分开说 ✗（含糊过去用户就没法自己解决）
                _running = false;
                SetRunLock(false);
                if (cand.Count == 0)
                {
                    _statusLab.Text = "没找到可用的本机地址";
                    MessageBox.Show(this,
                        "找不到可用的本机 IP（网线 / WiFi 是不是没连上？）\n\n"
                        + "「报警接收」要把本机地址写进摄像头的「上传中心」，没有本机地址就没法做。",
                        "没有本机地址", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    _statusLab.Text = "你指定的本机地址不在这台机器上";
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("你指定的是 " + wantedIp + "，但它现在**不在这台机器上**。");
                    sb.AppendLine();
                    sb.AppendLine("换过网络之后地址是会变的（原来的网线/热点断了）✓");
                    sb.AppendLine("现在可用的有：");
                    for (int i = 0; i < cand.Count; i++)
                        sb.AppendLine("    " + cand[i].Display());
                    sb.AppendLine();
                    sb.AppendLine("★ 程序**不会**偷偷换成别的 —— 那样你会以为设置生效了，");
                    sb.AppendLine("  而摄像头其实推不到你身上。请在上面「本机地址」里重选一个。");
                    MessageBox.Show(this, sb.ToString(), "指定的地址不在了",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            // 和摄像头不同网段 → 摄像头基本推不过来 ✗ 必须提醒（但**不拦着** ✓
            // 因为跨网段路由也可能是通的，用户自己清楚）
            bool sameNet = false;
            for (int i = 0; i < cand.Count; i++) if (cand[i].Ip == myIp) sameNet = cand[i].SameNet;
            if (!sameNet)
            {
                AppendAlert(DateTime.Now, "—",
                    "★ 提醒：本机地址 " + myIp + " 和摄像头 " + tg.Host
                    + " **不在同一网段** —— 摄像头很可能推不过来。", Color.Firebrick);
                Trace("网络", "本机 " + myIp + " 与摄像头 " + tg.Host + " 不同网段", Color.Salmon);
                Log.Warn("报警接收：本机地址 " + myIp + " 与摄像头 " + tg.Host + " 不同网段");
            }
            if (wantedIp.Length > 0)
                Log.Info("报警接收用**手工指定**的本机地址：" + myIp + "（自动挑会选 "
                    + (cand.Count > 0 ? cand[0].Ip : "无") + "）");
            UpdateIpHint();
            _running = true; _stop = false; _pushCount = 0;
            SetRunLock(true);
            _statusLab.Text = "报警接收中：地址 " + myIp + ":18080";
            UpdateRoundLabel();

            AppendAlert(DateTime.Now, "—", "开始【报警接收】巡检，本机监听 " + myIp + ":18080", Color.Gray);
            Trace("启动", "报警接收服务监听 " + myIp + ":18080", Color.LightGreen);
            AppendAlert(DateTime.Now, "—",
                "请在摄像头里把「报警主机/上传中心」地址设为 " + myIp + "，端口 18080，"
                + "并在 事件→移动侦测→联动方式 里勾上「上传中心」", Color.Gray);
            Log.Info("AI 实时巡检【报警接收】启动，监听 " + myIp + ":18080");

            // ★★ 检查防火墙有没有放行这个端口（2026-09-13 加）
            //
            //   用户实测：摄像头配好了（移动侦测启用 ✓ 联动方式勾了「上传中心」✓
            //   灵敏度 60 ✓ 区域画好了 ✓）✓ 画面里明明有人在动 ✓
            //   ★★ 但程序**一条都没收到** ✗
            //
            //   ★ 查出来：**防火墙里没有任何 18080 的规则** ✗
            //   ★★ 而"监听成功"✗ 不等于"外部能连进来" ✓✓：
            //     HttpListener 起了 ✓ 端口在 LISTENING ✓
            //     但 Windows 防火墙会**默默把包丢掉 ✗ 不报错** ✓
            //     → 程序显示"已启动、监听中"✗ 而实际上一条也收不到 ✓✓
            //
            //   ★ 代码原来只考虑了 URL ACL（"端口起不来"那种失败 ✓）
            //     没考虑"起来了但连不进来" ✗ —— 那是两种完全不同的失败 ✓
            //
            //   ★ 所以这里主动查一次 ✓ 没有规则就**明确告诉用户** ✓
            //     并且给一条**能直接粘贴的命令** ✓✓
            Thread fwCheck = new Thread(delegate ()
            {
                try
                {
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                    psi.FileName = "netsh";
                    psi.Arguments = "advfirewall firewall show rule name=all";
                    psi.UseShellExecute = false;
                    psi.RedirectStandardOutput = true;
                    psi.CreateNoWindow = true;
                    string txt;
                    using (System.Diagnostics.Process p2 = System.Diagnostics.Process.Start(psi))
                    {
                        txt = p2.StandardOutput.ReadToEnd();
                        p2.WaitForExit(8000);
                    }
                    if (txt == null) txt = "";
                    if (txt.IndexOf("18080", StringComparison.Ordinal) >= 0) return;   // 有规则 ✓ 不用管

                    Log.Info("报警接收：防火墙里没有 18080 的放行规则，已提示用户");
                    Ui(delegate ()
                    {
                        AppendAlert(DateTime.Now, "—",
                            "★ 提醒：Windows 防火墙里没有放行 18080 —— 摄像头可能推不进来。", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  「监听成功」不等于「外面连得进来」：端口在监听，但防火墙会把包默默丢掉，不报错。", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  用管理员身份跑一次这条命令就好（复制粘贴）：", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  netsh advfirewall firewall add rule name=\"VideoChecker 报警接收 18080\" dir=in action=allow protocol=TCP localport=18080",
                            Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  加完再去镜头前挥挥手试试。", Color.DarkOrange);
                    });
                }
                catch (Exception) { }
            });
            fwCheck.IsBackground = true;
            fwCheck.Start();

            // 自动把"上传中心地址"写进摄像头（省得用户去翻网页）
            Thread t2 = new Thread(delegate ()
            {
                string msg = HikIsapi.ConfigureUploadHost(tg, myIp, 18080);
                bool failed = msg.IndexOf("失败") >= 0 || msg.IndexOf("跳过") >= 0;
                Ui(delegate ()
                {
                    AppendAlert(DateTime.Now, "—", msg, failed ? Color.Firebrick : Color.Gray);
                    Trace("配置", msg, failed ? Color.Salmon : Color.LightGreen);
                    // 配不上就说明这台摄像头不支持报警推送（不是海康/大华 ISAPI 机型）
                    // 实测 TP-Link 就是这样：RTSP 能用，但没有 ISAPI、没有 alertStream
                    //
                    // ★★★ 原来是弹一个 Yes/No 对话框问"要不要切到定时轮询"✗
                    //   验收脚本实测（2026-09-13）：**整个界面卡死，一条报警都进不来** ✗✗
                    //
                    //     对话框是**模态**的 ✓ 它一弹出来就占住 UI 线程的消息循环 ✓
                    //     而 AppendAlert / _statusLab 都是 BeginInvoke 过去的 ✓
                    //     于是：摄像头照推 ✓ 接收线程照收 ✓
                    //     但**回调全卡在 UI 线程门口**✓ 一行记录都出不来 ✓
                    //     现象：用户看到界面"没响应"，还以为是自己程序死了 ✓
                    //
                    //   ★ 教训：**"提示"不能阻塞"干活"** ✓
                    //     摄像头不支持推送是**一个事实** ✓ 不是**一个需要立刻回答的问题** ✓
                    //     事实写进状态栏和记录里就够 ✓ 用户自己会去看 ✓
                    //     （真要引导他切模式 ✓ 按钮就在旁边 ✓ 不用我拦着路问 ✓）
                    if (failed && _running && !_stop)
                    {
                        _statusLab.Text = "这台摄像头不支持报警推送 —— 建议用「定时轮询」模式";
                        AppendAlert(DateTime.Now, "—",
                            "这台摄像头不支持「报警主动推送」（不是海康/大华那类有 ISAPI 的机型）。"
                            + "把上面的模式切到「定时轮询」就能继续巡检 —— 程序每 N 秒自己抓一帧分析，同样能发现异常。",
                            Color.DarkOrange);
                        Log.Info("报警上传配置失败，已提示改用定时轮询（不弹窗，避免卡住接收）");
                    }
                });
            });
            t2.IsBackground = true;

            // ★★ 报警接收的自检（2026-09-13 加）
            //
            //   用户贴日志问"这是啥情况"：
            //     16:53:50 【报警接收】启动
            //     17:08:29 停止：共 0 轮，告警 0 次
            //   ★★ **跑了 15 分钟，一条都没收到** ✗ 而程序一句话都没说 ✓
            //
            //   ★ 这个模式的原理是"摄像头主动推" ✗
            //     而摄像头那边有**四个开关**要同时满足 ✓
            //     少任何一个 ✓ 就是一条都收不到 ✓ 而用户不知道是哪儿的问题 ✓
            //   → 所以过一会儿还是 0 条，就主动把检查清单摆出来 ✓✓
            Thread selfCheck = new Thread(delegate ()
            {
                int[] marks = new int[] { 60, 180, 600 };      // 秒：1 分钟 / 3 分钟 / 10 分钟
                int done = 0;
                for (int s = 0; s < 660 && _running && !_stop; s++)
                {
                    System.Threading.Thread.Sleep(1000);
                    if (done >= marks.Length) continue;
                    if (s + 1 < marks[done]) continue;
                    done++;
                    if (_pushCount > 0) continue;               // 收到过就不用提醒了
                    Ui(delegate ()
                    {
                        AppendAlert(DateTime.Now, "—",
                            "报警接收已经 " + (s + 1) + " 秒了，一条都没收到 —— 摄像头那边可能没配好。", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  ★ 四个开关缺一不可：① 事件→移动侦测→启用（并画好区域）", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  ② 事件→移动侦测→布防时间（要覆盖当前时段）", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  ③ 事件→移动侦测→联动方式→勾上「上传中心」  ← 最常漏的就是这个", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  ④ 网络→高级配置→报警主机→地址端口（程序已经自动填好了）", Color.DarkOrange);
                        AppendAlert(DateTime.Now, "—",
                            "  也可能是这段时间真的没人动 —— 去镜头前挥挥手试试。", Color.DarkOrange);
                    });
                    Log.Info("报警接收自检：" + (s + 1) + " 秒内 0 条推送，已提示用户检查联动配置");
                }
            });
            selfCheck.IsBackground = true;
            selfCheck.Start();

            t2.Start();
        }

        /// <summary>
        /// 把 JPEG 缩到指定宽度再送 AI。
        ///
        /// 为什么必须缩（2026-09-13 实测）：
        ///   摄像头报警上传推来的是**原图** ✓ 实测 2688x1520 / 289KB ✓
        ///   直接送本地 qwen2.5vl:7b → **29.7 秒** ✗ 太慢了（用户会觉得程序卡住）
        ///   缩到 1280 宽后通常 2~4 秒 ✓
        ///   视觉模型本来也不需要原始分辨率 ✓ 缩了更快也更稳 ✓
        ///   缩不动就返回 null，调用方保持用原图 ✓（不能因为缩放失败就不分析）
        /// </summary>
        private static byte[] ResizeJpeg(byte[] jpg, int maxWidth)
        {
            if (jpg == null || jpg.Length == 0) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(jpg))
                using (Image src = Image.FromStream(ms))
                {
                    // ★ 判断条件加了"体积" ✗ 不能只看宽度（2026-09-13）
                    //   原来只看宽度 ✓ 于是"1280 宽但 600 KB"的图**原样送出去** ✗
                    //   而 q:v 3 抽出来的帧正好就是这样 ✓
                    //   现在：宽度没超**而且体积也小**才跳过 ✓ 否则重编码成质量 85 ✓
                    //   （质量 85 的 1280 宽监控画面约 80~150 KB ✓ 比原来小 3~4 倍 ✓
                    //     而 AI 看到的画面内容完全一样 ✓）
                    if (src.Width <= maxWidth && jpg.Length <= 150 * 1024) return null;
                    int w = maxWidth;
                    int h = (int)Math.Round((double)src.Height * maxWidth / src.Width);
                    using (Bitmap dst = new Bitmap(w, h))
                    {
                        using (Graphics g = Graphics.FromImage(dst))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        using (MemoryStream o = new MemoryStream())
                        {
                            // JPEG 编码器参数：质量 85 够 AI 用了
                            System.Drawing.Imaging.EncoderParameters ep = new System.Drawing.Imaging.EncoderParameters(1);
                            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                                System.Drawing.Imaging.Encoder.Quality, 85L);
                            System.Drawing.Imaging.ImageCodecInfo codec = null;
                            foreach (System.Drawing.Imaging.ImageCodecInfo c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                                if (c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) { codec = c; break; }
                            if (codec == null) dst.Save(o, System.Drawing.Imaging.ImageFormat.Jpeg);
                            else dst.Save(o, codec, ep);
                            return o.ToArray();
                        }
                    }
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>摄像头推来一条报警（后台线程）。有图就直接分析图。</summary>
        private void OnAlarmPush(AlarmPush p)
        {
            _pushCount++;
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            bool worth = p.WorthAnalyzing;       // ★ 别删：下面要用
            bool hasImg = p.Image != null && p.Image.Length > 0;
            // ★ 用**发送方 IP** 而不是下拉框里选的那台（2026-09-13 修）：
            //   接收服务能同时接住多台摄像头 ✓ 都往 18080 推 ✓
            //   原来记 CSV 时写的是"当前下拉选的那台" ✗ 多机同推时记录会张冠李戴 ✓
            //   AlarmServer 已经把 RemoteEndPoint 抓下来了 ✓ 直接用 ✓
            string from = (p.SourceIp != null && p.SourceIp.Length > 0) ? p.SourceIp : ReadUrl();
            _pushFrom = from;

            Ui(delegate ()
            {
                _previewLab.Text = "报警推送：已收到 " + _pushCount + " 条 · 最近 " + stamp + " "
                    + p.Chinese + (hasImg ? "（含图片 " + (p.Image.Length / 1024) + "KB）" : "（无图片）");
                _statusLab.Text = "报警接收中 · 已收到 " + _pushCount + " 条 · 最近 " + stamp + " " + p.Chinese;
            });
            string line = p.Chinese + "（" + p.EventType + (p.EventState.Length > 0 ? " · " + p.EventState : "") + "）"
                + (hasImg ? " · 含图片 " + (p.Image.Length / 1024) + "KB" : " · 无图片")
                + "  ← " + from;
            // ★★ 主记录应该"一行 = 一次真实的分析"（2026-09-13 修 ✗ 第二次才修到这里）
            //
            //   v10.4 只把「事件订阅」那条路（OnHikEvent）改了 ✗
            //   **报警接收这条路（本方法）漏了** ✗ —— 而用户用的正是这条路 ✓
            //   （「同一个功能只改了一条路」—— 交接文档里那一类错，又犯了一次 ✗）
            //
            //   这条路上本来是老毛病：先记"触发"✓ 再去抖 ✓
            //   被去抖丢掉的那些**照样被记成"触发"**✗ 主记录越堆越乱 ✓
            //
            //   而且这里比 OnHikEvent 还多一个坑：
            //   "触发"是**在抓图之前**记的 ✓ 要是图没抓回来 ✓
            //   主记录里就留下一条"触发" + 一条"无图"✗ 像发生了两件事 ✓
            //   → 先把"会不会真分析"算清楚 ✓ 再决定记哪边 ✓
            //
            // ★★★ 认领用**原子的比较交换**，不是"先读再写"（2026-09-13 第三次修去抖）
            //
            //   ★ 验收场景 B 抓到的真 bug ✗：
            //     推 2 条、间隔 0.6 秒 → **分析了 2 轮、记了 2 行** ✗
            //
            //   根因：AlarmServer 给**每条请求开一个线程**（它自己为了不排队，是好事 ✓）
            //     两条推送在**几乎同一时刻**跑进 OnAlarmPush ✓
            //     原来的写法是"读 `_lastAnalyzeTick` → 判断 → 再写"✗
            //     A 线程读到"0（没人领过）"✓ B 线程**也**读到"0"✓
            //     两边都判"该分析"✓ 两边都去抓图 ✓ → 两轮 ✓
            //   —— 经典 check-then-act ✗ 中间那个空档就是竞态 ✓
            //
            //   ★ 为什么 v10.6 的验收没抓到：
            //     那次抓图要 6 秒 ✓ 错得足够开 ✓ 掩盖了竞态 ✓
            //     这次抓图只花 1 秒 ✓ 两个线程真的撞上了 ✓
            //     （**测试环境变快，反而暴露了一个一直存在的 bug** ✓）
            //
            //   ★ 修法：`Interlocked.CompareExchange` 把"读+比+写"变成一个原子动作 ✓
            //     只有**真的把旧值换成新值**的那一个线程算领到 ✓
            //     另一个线程看到的是"刚领过"✓ 自然跳过 ✓
            //
            //   ★ 注意 elapsed 的处理：TickCount 会在 24.9 天后回绕 ✓
            //     单帧存的是"上一次领的时间戳"✓ 做差要用 int 的**有符号**溢出 ✓
            //     （int 相减在 unchecked 下天然处理回绕 ✓ 别用 uint 或 long 硬算 ✓）
            int now = Environment.TickCount;
            int snapshot = _lastAnalyzeTick;
            bool claimed = false;
            bool debounceOk = false;
            if (snapshot == 0)
            {
                // 从来没人领过：试着自己领
                claimed = (Interlocked.CompareExchange(ref _lastAnalyzeTick, now, 0) == 0);
                debounceOk = claimed;
            }
            else
            {
                // 有人领过：只有距上一次够久，才允许领新的
                debounceOk = (int)(now - snapshot) >= DebounceMs;
                if (debounceOk)
                    claimed = (Interlocked.CompareExchange(ref _lastAnalyzeTick, now, snapshot) == snapshot);
            }
            bool canRun = _running && !_stop;
            bool willAnalyze = worth && canRun && claimed;

            if (willAnalyze) _analyzing = true;

            try
            {
                byte[] jpg = p.Image;

                // 没图片就现抓一张（补偿）
                if (willAnalyze && (jpg == null || jpg.Length == 0))
                {
                    HikIsapi.Target tg = _target != null && _target.Ok ? _target : HikIsapi.Parse(ReadUrl());
                    jpg = HikIsapi.Snapshot(tg, 6000);
                    if (jpg == null || jpg.Length == 0) willAnalyze = false;   // 抓不到 → 不是一次分析
                }

                if (willAnalyze)
                {
                    AppendAlert(DateTime.Now, "触发", line, Color.DarkOrange);
                }
                else if (worth && canRun && !debounceOk)
                {
                    Trace("心跳", line + "（去抖跳过，同一波动作）", Color.Gray);
                }
                else if (worth && canRun && !_analyzing)
                {
                    // 值得分析、也在跑，却卡在"抓不到图"上 ✓ 这是**失败** ✓ 要留痕 ✓
                    // ★ 加 !_analyzing 这一条（2026-09-13）：
                    //   抓图失败时 _analyzing 还是 true ✓ 说明**这一波已经有主了** ✓
                    //   再记一行"无图"就变成"一波动作两行记录"✗ 正是要治的那个病 ✓
                    AppendAlert(DateTime.Now, "无图", "报警 " + p.Chinese + " 没有图片，现场快照也取不到", Color.Firebrick);
                }
                else
                {
                    Trace("心跳", line, Color.Gray);       // 心跳 ✓ 或服务已经停了 ✓
                }

                if (!willAnalyze) return;                   // ★ 不分析的就不该在主记录留"触发"

                string question = _promptBox.Text.Trim();
                if (question.Length == 0) question = DefaultPrompt;
                string evName = p.Chinese;
                byte[] img = jpg;

                Thread t = new Thread(delegate ()
                {
                    try
                    {
                        Ui(delegate () { ShowPreview(img); _statusLab.Text = "报警「" + evName + "」→ 正在分析画面…"; });
                        // ★ 摄像头推来的是原图（实测 2688x1520 = 289KB）✗
                        //   直接送 AI 要 **29.7 秒** ✓ 缩到 1280 宽后通常 2~4 秒 ✓
                        //   视觉模型本来也不需要那么高的分辨率 ✓ 缩了反而更快更稳 ✓
                        byte[] small = ResizeJpeg(img, 1280);
                        if (small != null && small.Length > 0) img = small;
                        string holder;
                        if (!MageCheck.TryAcquire("AI 实时巡检", out holder))
                        {
                            Ui(delegate () { _statusLab.Text = "AI 正被「" + holder + "」占用，本条跳过"; });
                            return;
                        }
                        string ans; int ms;
                        try
                        {
                            DateTime t0 = DateTime.Now;
                            ans = MageCheck.QueryOllama(AppSettings.OllamaUrl, _runModel, img, question, 768);
                            ms = (int)(DateTime.Now - t0).TotalMilliseconds;
                        }
                        finally { MageCheck.Release("AI 实时巡检"); }
                        bool alert; string desc = ParseAnswer(ans, out alert);
                        if (_stop) return;
                        // 用**发送方 IP** 而不是下拉框选的那台 —— 多台摄像头同推时才分得清 ✓
                        Ui(delegate () { FinishRound("【" + evName + "】" + desc, alert, ms, _pushFrom); });
                    }
                    catch (MageCheck.StoppedException)
                    {
                        // ★ 用户按了停止 → 我们在途的请求被掐断（2026-09-13 加）
                        //   不单独接住的话会落到下面那个通用分支 ✗
                        //   主记录里就会出现一行「报警分析出错：用户已停止」✓
                        //   —— 用户明明是自己点的停止 ✓ 却记成一条错误 ✓ 看着像程序出毛病了 ✓
                        Ui(delegate () { _statusLab.Text = "已停止（在途的分析被打断）"; });
                        Log.Info("AI 报警分析被用户停止打断");
                    }
                    catch (Exception ex)
                    {
                        Ui(delegate () { _statusLab.Text = "报警分析出错：" + ex.Message; });
                        Log.Warn("AI 报警分析出错：" + ex.Message);
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            finally
            {
                // ★ 无论分析成功、失败、还是被判"抓不到图" ✓ 都要把认领标记放掉 ✓
                //   否则后面真实的推送会一直被当成"这一波已经有主了"✓ 再也分析不了 ✓
                _analyzing = false;
            }
        }

        // ==================== 事件订阅模式 ====================

        /// <summary>起一个后台线程挂上摄像头的事件流；有事件才抓图分析。</summary>
        private void StartEventMode()
        {
            if (_evtRunning) return;
            _evtRunning = true;
            HikIsapi.Target tg = _target;
            Thread t = new Thread(delegate ()
            {
                HikIsapi.SubscribeLoop(tg,
                    delegate () { return _stop || !_evtRunning; },
                    delegate (HikEvent ev) { OnHikEvent(ev); },
                    delegate (string st) { Ui(delegate () { _statusLab.Text = st + (_evtCount > 0 ? "（已收到 " + _evtCount + " 条）" : ""); }); });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>收到一条摄像头事件：去抖 → 抓快照 → 送 AI。</summary>
        private void OnHikEvent(HikEvent ev)
        {
            _evtCount++;
            // ★ 收到的**每一条**事件都要在界面上留痕（2026-09-13 修）
            //
            //   原来只把"值得分析的"记进表格 ✗ 其余的（比如 videoloss 的 inactive 心跳）
            //   直接丢掉 ✓ 结果用户看到的就是"事件订阅里一条数据都没有" ✗
            //   其实程序**收到了** ✓ 只是没显示 ✓
            //
            //   「不送去 AI 分析」和「界面上不显示」是**两件事** ✗ 不能混在一起 ✓
            //   （事件流水本身就有价值：能看出摄像头在推什么、频率多少、链路通不通 ✓）
            bool worth = ev.WorthAnalyzing;
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            Ui(delegate ()
            {
                _previewLab.Text = "事件流：已收到 " + _evtCount + " 条 · 最近 "
                    + stamp + " " + ev.Chinese + (worth ? "（会分析）" : "（" + ev.State + "，不分析）");
                _statusLab.Text = "事件订阅中 · 已收到 " + _evtCount + " 条事件 · 最近 "
                    + stamp + " " + ev.Chinese;
            });
            // ★★ 先判断"这条会不会真的送去分析"，再决定记哪边（2026-09-13 修）
            //
            //   用户看到的现象：告警记录里挤着 8 条「触发 VMD」✗
            //   而「已巡检 1 轮」✗ —— 那 8 条里**只有 1 条真分析了** ✓
            //
            //   根因：去抖的判断在"记主记录"**之后** ✗
            //     所以 8 条全被记成"触发"✓ 其中 7 条其实被丢掉了 ✓
            //
            //   ★ 「收到了」和「分析了」是两件事 ✗ 不能都写成"触发" ✓
            //     主记录应该**一行 = 一次真实的分析** ✓
            //     其余的（收到但没分析）进事件流水小窗 ✓
            //     —— 这正是用户早就要求过的「心跳别堆在主记录里」的同一条道理 ✓✓
            int now = Environment.TickCount;
            bool willAnalyze = worth && _running && !_stop
                && (_lastAnalyzeTick == 0 || (int)(now - _lastAnalyzeTick) >= DebounceMs);

            string line2 = ev.Chinese + "（" + ev.EventType + "）" + (ev.State.Length > 0 ? " · " + ev.State : "");
            if (willAnalyze) AppendAlert(DateTime.Now, "触发", line2, Color.DarkOrange);
            else Trace("事件", line2 + (worth ? "（去抖跳过）" : ""), Color.Gray);

            if (!worth) return;                             // 只处理"发生"，忽略"结束"和心跳
            if (!_running || _stop) return;
            if (!willAnalyze) return;                       // ★ 去抖没过，不分析（但已经记进小窗了）
            _lastAnalyzeTick = now;


            Log.Info("AI 实时巡检：收到事件 " + ev.EventType + "（" + ev.State + "）");

            string question = _promptBox.Text.Trim();
            if (question.Length == 0) question = DefaultPrompt;
            HikIsapi.Target tg = _target;

            Thread t = new Thread(delegate ()
            {
                try
                {
                    Ui(delegate () { _statusLab.Text = "事件「" + ev.Chinese + "」→ 正在抓图分析…"; });
                    // ★★ 抓图失败要重试一次（2026-09-13 修）
                    //
                    //   用户贴日志问"这是啥情况"：里面有一条「失败：VMD，HTTP 快照没取到」✗
                    //   而**同一批的其它 VMD 都成功了** ✓ → 那是**间歇性**的 ✓
                    //
                    //   实测原因：海康的 /onvif-http/snapshot 偶尔要十几秒才返回 ✓
                    //   8 秒超时对它会误判 ✗ 而且**失败就放弃了 ✗ 不重试** ✓
                    //
                    //   → 超时提到 12 秒 ✓ 失败后再试一次 ✓
                    //   ★ 只有真的两次都失败才报"抓图失败" ✗
                    //     （一条"失败"会让用户担心 ✓ 而它其实自己就能恢复 ✓）
                    byte[] jpg = HikIsapi.Snapshot(tg, 12000);
                    if (jpg == null || jpg.Length == 0)
                    {
                        Log.Info("AI 实时巡检：快照第一次没取到，重试一次");
                        System.Threading.Thread.Sleep(300);
                        jpg = HikIsapi.Snapshot(tg, 12000);
                    }
                    if (jpg == null || jpg.Length == 0)
                    {
                        Ui(delegate () { _statusLab.Text = "事件「" + ev.Chinese + "」抓图失败（快照取不到，已重试）"; });
                        AppendAlert(DateTime.Now, "抓图失败", "事件：" + ev.Chinese + "，快照两次都没取到（摄像头忙或网络慢）", Color.Firebrick);
                        return;
                    }
                    Ui(delegate () { ShowPreview(jpg); });

                    string holder;
                    if (!MageCheck.TryAcquire("AI 实时巡检", out holder))
                    {
                        Ui(delegate () { _statusLab.Text = "AI 正被「" + holder + "」占用，本条事件跳过"; });
                        return;
                    }
                    string ans; int ms;
                    try
                    {
                        DateTime t0 = DateTime.Now;
                        ans = MageCheck.QueryOllama(AppSettings.OllamaUrl, _runModel, jpg, question, 768);
                        ms = (int)(DateTime.Now - t0).TotalMilliseconds;
                    }
                    finally { MageCheck.Release("AI 实时巡检"); }

                    bool alert; string desc = ParseAnswer(ans, out alert);
                    if (_stop) return;
                    string evName = ev.Chinese;
                    Ui(delegate () { FinishRound("【" + evName + "】" + desc, alert, ms, ReadUrl()); });
                }
                catch (MageCheck.StoppedException)
                {
                    // ★ 用户停止打断（2026-09-13 加）—— 同 OnAlarmPush 那条，别记成"出错"
                    Ui(delegate () { _statusLab.Text = "已停止（在途的分析被打断）"; });
                    Log.Info("AI 事件分析被用户停止打断");
                }
                catch (Exception ex)
                {
                    Ui(delegate () { _statusLab.Text = "事件分析出错：" + ex.Message; });
                    Log.Warn("AI 事件分析出错：" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 重新列出本机地址可选值（自动 + 各网卡）。
        ///
        /// ★ 下拉里第一项永远是「自动（推荐）」✓ 另外再加一个**手动填**的入口 ✓
        ///   （有些情况枚举不到，比如 IP 是后来才配上的 ✓ 留个口子总没错）
        /// </summary>
        private void ReloadIpChoices()
        {
            if (_ipBox == null) return;
            string camIp = "";
            try
            {
                HikIsapi.Target tg = HikIsapi.Parse(ReadUrl());
                if (tg != null && tg.Ok) camIp = tg.Host;
            }
            catch (Exception) { }
            if (camIp.Length == 0 && _target != null && _target.Ok) camIp = _target.Host;

            string wanted = AppSettings.UploadIp == null ? "" : AppSettings.UploadIp;
            List<AlarmServer.LocalIp> list = AlarmServer.AllLocalIPs(camIp);

            // 重建列表（用 Items.Clear + Add，避免 SelectedIndexChanged 在过程中乱触发）
            _ipBox.SelectedIndexChanged -= delegate(object s, EventArgs e) { OnUploadIpChanged(); };
            _ipBox.Items.Clear();
            _ipBox.Items.Add("自动（推荐）");
            for (int i = 0; i < list.Count; i++) _ipBox.Items.Add(list[i].Display());
            // 用户手填过的、但这次没枚举到的地址 → 也列出来（否则他一改就丢）
            bool wantListed = false;
            for (int i = 0; i < list.Count; i++) if (list[i].Ip == wanted) wantListed = true;
            if (wanted.Length > 0 && !wantListed) _ipBox.Items.Add(wanted + "  ·  （手工填的，这次没枚举到）");
            _ipBox.SelectedIndexChanged += delegate(object s, EventArgs e) { OnUploadIpChanged(); };

            // 选中当前设置
            int sel = 0;
            if (wanted.Length > 0)
            {
                for (int i = 1; i < _ipBox.Items.Count; i++)
                {
                    if (((string)_ipBox.Items[i]).StartsWith(wanted + " ")) { sel = i; break; }
                }
            }
            _ipBox.SelectedIndex = sel;
            UpdateIpHint();
        }

        /// <summary>用户改了下拉 → 存起来（下次开程序还记得）。</summary>
        private void OnUploadIpChanged()
        {
            if (_ipBox == null || _ipBox.SelectedIndex < 0) return;
            string pick = (string)_ipBox.Items[_ipBox.SelectedIndex];
            // 从显示文字里把 IP 抠出来（"192.168.1.100  ·  以太网…" 的第一段）
            string ip = "";
            if (pick != null && pick != "自动（推荐）")
            {
                int sp = pick.IndexOf(' ');
                ip = (sp > 0) ? pick.Substring(0, sp) : pick;
            }
            if (ip != AppSettings.UploadIp)
            {
                AppSettings.UploadIp = ip;
                try { AppSettings.Save(); } catch (Exception) { }
                Log.Info("报警接收的本机地址设为：" + (ip.Length == 0 ? "自动" : ip));
            }
            UpdateIpHint();
        }

        /// <summary>右边那行提示：选了哪个 / 有没有"摄像头够不到"这类问题。</summary>
        private void UpdateIpHint()
        {
            if (_ipHint == null) return;
            try
            {
                string camIp = "";
                HikIsapi.Target tg = HikIsapi.Parse(ReadUrl());
                if (tg != null && tg.Ok) camIp = tg.Host;
                if (camIp.Length == 0 && _target != null && _target.Ok) camIp = _target.Host;

                List<AlarmServer.LocalIp> list = AlarmServer.AllLocalIPs(camIp);
                if (list.Count == 0)
                {
                    _ipHint.ForeColor = Color.Firebrick;
                    _ipHint.Text = "★ 没找到可用的本机地址 —— 网线/WiFi 是不是没连上？";
                    return;
                }
                string wanted = AppSettings.UploadIp == null ? "" : AppSettings.UploadIp;
                string use = AlarmServer.PickIp(camIp, wanted);
                if (use.Length == 0)
                {
                    _ipHint.ForeColor = Color.Firebrick;
                    _ipHint.Text = "★ 你指定的 " + wanted + " 现在不在这台机器上 —— 请重选（不会偷偷用别的）";
                    return;
                }
                // 找到它，看跟摄像头同不同网段
                bool same = false;
                string nm = "";
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Ip == use) { same = list[i].SameNet; nm = list[i].Name; break; }

                if (camIp.Length == 0)
                {
                    _ipHint.ForeColor = Color.Gray;
                    _ipHint.Text = "将用 " + use + "（填上摄像头地址后能判断是否同网段）";
                }
                else if (same)
                {
                    _ipHint.ForeColor = Color.Gray;
                    _ipHint.Text = "将用 " + use + "  ✓ 和摄像头同网段";
                }
                else
                {
                    // ★ 这一条最要紧：不同网段 → 摄像头**基本推不过来** ✗
                    _ipHint.ForeColor = Color.Firebrick;
                    _ipHint.Text = "★ " + use + " 和摄像头 " + camIp + " **不在同一网段** —— 摄像头很可能推不过来";
                }
            }
            catch (Exception) { }
        }

        /// <summary>换模式时更新底部提示行。</summary>
        private void UpdateModeHint()
        {
            Control[] found = _cardCtrl.Controls.Find("camTip", false);
            if (found.Length == 0) return;
            Label tip = found[0] as Label;
            if (tip == null) return;
            // ★ 三种模式三套说明（2026-09-13 修）：
            //   原来只有"index==0 / else"两个分支 ✗ 加了第三种模式后
            //   index 0 的含义变了 ✓ 说明文字却还是旧的 ✓ 用户看到的提示和选的模式对不上 ✓
            //   —— 这类"加了一项忘了改分支"和坑 19（改了布局没改方位词）是同一类错 ✓
            if (_modeBox.SelectedIndex == 0)
                tip.Text = "报警接收（推荐）：摄像头检测到画面变化时，会主动把现场图片推给本程序 —— "
                         + "程序不轮询、不建 RTSP 会话，负载最低，短事件也不会漏。"
                         + "前提：事件→移动侦测→布防时间要覆盖你想监控的时段，联动方式里勾上「上传中心」。"
                         + "程序启动时会自动把本机地址填进摄像头的「报警上传」设置。"
                         + "间隔框在这个模式里不用。";
            else if (_modeBox.SelectedIndex == 1)
                tip.Text = "事件订阅：程序挂在摄像头的事件流（alertStream）上等推送。"
                         + "但实测部分机型（含本机这台 DS-2CD7A47EWD-XZS）只会通过它推异常类事件，"
                         + "移动侦测收不到 —— 那种情况请改用「报警接收」。间隔框在这个模式里不用。";
            else if (_modeBox.SelectedIndex == 3)
                tip.Text = "ONVIF 订阅（通用）：走 ONVIF 标准挂着设备的事件队列，有变化才分析。"
                         + "和「事件订阅」的区别：这个**跨品牌**，而且实测**能拿到移动侦测**"
                         + "（海康的 alertStream 拿不到）。事件是排队的，断线期间也不丢。"
                         + "收到事件后优先用 ONVIF 抓图（不建 RTSP 会话，对摄像头最友好），"
                         + "设备不支持抓图时自动退回 RTSP 抽帧。间隔框在这个模式里只用于去抖。";
            else
                tip.Text = "定时轮询：每 N 秒抓一帧分析。用摄像头须知：① 间隔别小于 10 秒"
                         + "（5 秒 = 每小时 720 次连接，偏频繁）；② 尽量用子码流地址"
                         + "（海康 …/Channels/102、大华 subtype=1）；"
                         + "③ 程序会优先用 HTTP 抓拍（/ISAPI/Streaming/channels/101/picture），"
                         + "取不到才退回 RTSP —— HTTP 方式对摄像头最友好。";
        }

        /// <summary>一轮巡检：抓帧 → 送 AI → 解析 → 显示 + 记录。全程在后台线程，不卡界面。</summary>
        private void InspectOnce()
        {
            if (!_running || _busy) return;      // 上一轮没跑完就跳过这一轮，避免任务堆积
            _busy = true;
            string url = ReadUrl();
            string question = _promptBox.Text.Trim();
            if (question.Length == 0) question = DefaultPrompt;

            Thread t = new Thread(delegate ()
            {
                try
                {
                    // 优先用 HTTP 抓拍（不建 RTSP 会话，对摄像头最省）✓
                    // 海康/大华这类支持 ISAPI/CGI 的摄像头直接出 JPEG ✓
                    // 取不到才退回 RTSP 抽帧（那时的连接开销大一些 ✓）
                    byte[] jpg = null;
                    HikIsapi.Target tg = HikIsapi.Parse(url);
                    if (tg.Ok) jpg = HikIsapi.Snapshot(tg, 8000);
                    if (jpg == null || jpg.Length == 0)
                        jpg = MageCheck.ExtractFrameJpeg(url, 0, 1280);
                    if (jpg == null || jpg.Length == 0)
                    {
                        Ui(delegate () { _statusLab.Text = "抓帧失败（" + DateTime.Now.ToString("HH:mm:ss") + "）：地址不通或摄像头忙"; });
                        AppendAlert(DateTime.Now, "抓帧失败", "取不到画面，请检查地址/网络", Color.Firebrick);
                        return;
                    }
                    Ui(delegate () { ShowPreview(jpg); _statusLab.Text = "已抓到画面，" + DateTime.Now.ToString("HH:mm:ss") + "，正在分析…"; });

                    string holder;
                    if (!MageCheck.TryAcquire("AI 实时巡检", out holder))
                    {
                        Ui(delegate () { _statusLab.Text = "AI 正被「" + holder + "」占用，本轮跳过（等它跑完会自动继续）"; });
                        return;
                    }
                    string ans;
                    int ms;
                    try
                    {
                        DateTime t0 = DateTime.Now;
                        ans = MageCheck.QueryOllama(AppSettings.OllamaUrl, _runModel, jpg, question, 768);
                        ms = (int)(DateTime.Now - t0).TotalMilliseconds;
                    }
                    finally { MageCheck.Release("AI 实时巡检"); }

                    bool alert;
                    string desc = ParseAnswer(ans, out alert);
                    if (_stop) return;
                    Ui(delegate () { FinishRound(desc, alert, ms, url); });
                }
                catch (MageCheck.StoppedException)
                {
                    // ★ 用户停止打断（2026-09-13 加）
                    //   这里最要紧 ✗：定时轮询是**等待最久**的模式（一轮 2~40 秒）✓
                    //   原来会把「用户已停止」记成一条「出错」✗ 看着像程序坏了 ✓
                    Ui(delegate () { _statusLab.Text = "已停止（在途的分析被打断）"; });
                    Log.Info("AI 实时巡检被用户停止打断");
                }
                catch (Exception ex)
                {
                    Ui(delegate () { _statusLab.Text = "本轮出错：" + ex.Message; });
                    AppendAlert(DateTime.Now, "出错", ex.Message, Color.Firebrick);
                    Log.Warn("AI 实时巡检出错：" + ex.Message);
                }
                finally { _busy = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 解析 AI 的回答，得出「是否告警」和「画面描述」。
        ///
        /// ★ 首要依据是「判定：正常 / 判定：告警」后面那个词 ✓
        ///
        /// 为什么不能整段搜关键词（第一次端到端实测就踩到了 ✗）：
        ///   模型原话：「……没有人员出现，物品移动或异常聚集。整体画面稳定，未发现安全隐患。
        ///              判定：正常」
        ///   我在整段里搜「异常」两个字 → 命中 ✗ → 判成**告警** ✓
        ///   但它说的是「物品移动**或**异常聚集」这种**列举场景**，结论明明是"正常" ✓
        ///   这类误判在实时巡检里最要命 —— 会**一直误报** ✗
        ///
        /// 没有「判定：」标记时才退化找关键词，且要求前面没有否定词（未/无/没有/不见）✓
        /// </summary>
        private static string ParseAnswer(string ans, out bool alert)
        {
            alert = false;
            if (ans == null) return "（模型没有返回内容）";
            string s = ans.Trim();
            string flat = s.Replace("：", ":").Replace("\r", "").Replace("\n", " ");

            int k = flat.IndexOf("判定:", StringComparison.OrdinalIgnoreCase);
            if (k >= 0)
            {
                // 只看「判定：」后面开头那几个字，不整段搜
                string tail = flat.Substring(k + 3).TrimStart();
                string head = tail.Length > 8 ? tail.Substring(0, 8) : tail;
                alert = head.StartsWith("告警", StringComparison.OrdinalIgnoreCase)
                     || head.StartsWith("异常", StringComparison.OrdinalIgnoreCase)
                     || head.StartsWith("ABNORMAL", StringComparison.OrdinalIgnoreCase);
                // 描述 = 「判定：」之前的内容
                string before = flat.Substring(0, k).Trim();
                return before.Length > 0 ? before : tail;
            }

            // 退化：没有「判定：」——找关键词，但要排除被否定的（"未发现异常"不算异常）
            alert = HasUnnegatedKeyword(flat, "告警") || HasUnnegatedKeyword(flat, "异常")
                 || flat.IndexOf("ABNORMAL", StringComparison.OrdinalIgnoreCase) >= 0;
            return flat.Trim();
        }

        /// <summary>找关键词，但要求它前面 4 个字里没有否定词。</summary>
        private static bool HasUnnegatedKeyword(string text, string word)
        {
            string[] neg = new string[] { "未", "无", "没有", "不见", "不存在", "排除" };
            int from = 0;
            while (true)
            {
                int i = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return false;
                int st = i - 4; if (st < 0) st = 0;
                string pre = text.Substring(st, i - st);
                bool negated = false;
                foreach (string n in neg)
                    if (pre.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) { negated = true; break; }
                if (!negated) return true;
                from = i + word.Length;
            }
        }

        private void FinishRound(string desc, bool alert, int ms, string url)
        {
            if (!_running && _stop) return;
            _round++;
            if (alert) _alerts++;
            _descLab.Text = desc;
            _msLab.Text = "耗时 " + ms + " ms";
            SetVerdictVisual(alert ? "告警" : "正常", alert ? 1 : 0);
            UpdateRoundLabel();
            _previewLab.Text = "第 " + _round + " 轮 · " + DateTime.Now.ToString("HH:mm:ss")
                + " · " + (alert ? "判定为告警" : "判定为正常");
            // 状态栏文字按模式区分：原来在报警/事件模式下也显示"每 N 秒一帧" ✗ 会误导 ✓
            if (_modeBox != null && _modeBox.SelectedIndex == 0)
                _statusLab.Text = "报警接收中 · 已收到 " + _pushCount + " 条 · 最近 " + DateTime.Now.ToString("HH:mm:ss");
            else if (_modeBox != null && _modeBox.SelectedIndex == 1)
                _statusLab.Text = "事件订阅中 · 已收到 " + _evtCount + " 条事件";
            else if (_modeBox != null && _modeBox.SelectedIndex == 3)
                _statusLab.Text = "ONVIF 订阅中 · 已收到 " + _onvifRecv + " 条事件"
                    + (_onvifMerged > 0 ? "（合并 " + _onvifMerged + " 条）" : "");
            else
                _statusLab.Text = "巡检中：每 " + (_timer != null ? (_timer.Interval / 1000).ToString() : "?") + " 秒一帧……";

            AppendAlert(DateTime.Now, alert ? "告警" : "正常", desc, alert ? Color.Firebrick : Color.ForestGreen);
            WriteLog(DateTime.Now, alert, ms, url, desc);

            if (alert) Log.Warn("AI 实时巡检【告警】：" + desc);
        }

        private void SetVerdictVisual(string verdict, int level)
        {
            if (InvokeRequired)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed && !Disposing)
                        BeginInvoke((MethodInvoker)delegate () { SetVerdictVisual(verdict, level); });
                }
                catch (Exception) { }
                return;
            }
            if (verdict == null) { _verdictLab.Text = "—"; _verdictLab.ForeColor = Color.Gray; return; }
            _verdictLab.Text = level == 0 ? "● 正常" : "● 告警";
            _verdictLab.ForeColor = level == 0 ? Color.ForestGreen : Color.Firebrick;
        }

        private void UpdateRoundLabel()
        {
            if (InvokeRequired)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed && !Disposing)
                        BeginInvoke((MethodInvoker)delegate () { UpdateRoundLabel(); });
                }
                catch (Exception) { }
                return;
            }
            _roundLab.Text = "已巡检 " + _round + " 轮 / 告警 " + _alerts + " 次";
        }

        /// <summary>把一帧显示到预览框。同样自适应线程（后台线程抓到图后可以直接调）。</summary>
        private void ShowPreview(byte[] jpg)
        {
            if (InvokeRequired)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed && !Disposing)
                        BeginInvoke((MethodInvoker)delegate () { ShowPreview(jpg); });
                }
                catch (Exception) { }
                return;
            }
            try
            {
                using (MemoryStream ms = new MemoryStream(jpg))
                {
                    Image img = Image.FromStream(ms);
                    Image old = _frame.Image;
                    _frame.Image = new Bitmap(img);   // 拷一份：不然后面 ms 释放了图就废了
                    if (old != null) old.Dispose();
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 网络搜索（ONVIF + SSDP）：搜局域网 → 自动试出取流地址 → 填进地址栏 ✓
        ///
        /// 为什么加这个（用户要求「加个 ONVIF 取流方式，这样更友好一些」）：
        ///   以前换牌子就要自己查 RTSP 路径 ✗ 填错就 404 Stream Not Found ✓
        ///   实测：海康 /Streaming/Channels/101 ✓ TP-Link /stream1 ✓ 大华又是别的 ✓
        ///   ONVIF 让**摄像头自己报出地址** ✓ 用户一个路径都不用记 ✓
        ///   纯 SOAP + HTTP ✓ 不需要任何厂商 DLL ✓ 符合零依赖原则 ✓
        /// </summary>
        private void OnOnvifSearch(object sender, EventArgs e)
        {
            try
            {
                using (NetSearchForm dlg = new NetSearchForm())
                {
                    // 把当前地址栏里可能已有的账号密码预填进去，省得再打一遍 ✓
                    // （同一台摄像头的地址栏里通常就带着账号密码 ✓）
                    string u, p;
                    if (TryPickCredential(ReadUrl(), out u, out p))
                        dlg.PresetCredential(u, p);
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ResultUrl.Length > 0)
                    {
                        SetUrl(dlg.ResultUrl);
                        _statusLab.Text = "已从 ONVIF 取到取流地址，可以直接点「开始巡检」了。";
                        Trace("ONVIF", "取到地址 " + Secret.MaskUrl(dlg.ResultUrl), Color.LightGreen);
                        AppendAlert(DateTime.Now, "—", "ONVIF 取到取流地址：" + Secret.MaskUrl(dlg.ResultUrl), Color.Gray);
                        Log.Info("ONVIF 取到取流地址：" + Secret.MaskUrl(dlg.ResultUrl));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ONVIF 搜索失败：" + ex.Message);
                MessageBox.Show(this, "ONVIF 搜索出错：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>从一条 rtsp 地址里拆出账号密码（用于预填）。</summary>
        private static bool TryPickCredential(string url, out string user, out string pwd)
        {
            user = ""; pwd = "";
            if (url == null) return false;
            int at = url.IndexOf('@');
            if (at <= 0) return false;
            int ss = url.IndexOf("://");
            if (ss < 0) return false;
            string cred = url.Substring(ss + 3, at - ss - 3);
            int c = cred.IndexOf(':');
            if (c < 0) { user = cred; return true; }
            user = cred.Substring(0, c);
            pwd = cred.Substring(c + 1);
            return true;
        }

        /// <summary>打开（或激活）事件流水小窗 ✓ 会记住位置。</summary>
        private void ShowLogWin()
        {
            try
            {
                if (_logWin == null || _logWin.IsDisposed)
                {
                    _logWin = new EventLogForm();
                    // 默认贴在本窗口右边，不挡住主界面
                    _logWin.Location = new Point(Left + Width + 8, Top + 60);
                    Screen sc = Screen.FromControl(this);
                    if (sc != null)
                    {
                        Rectangle w = sc.WorkingArea;
                        if (_logWin.Right > w.Right) _logWin.Location = new Point(w.Right - _logWin.Width - 8, Top + 60);
                        if (_logWin.Left < w.Left) _logWin.Location = new Point(w.Left + 8, Top + 60);
                    }
                    _logWin.Show(this);
                }
                else
                {
                    if (!_logWin.Visible) _logWin.Show(this);
                    _logWin.Activate();
                }
            }
            catch (Exception ex) { Log.Warn("打开事件流水小窗失败：" + ex.Message); }
        }

        /// <summary>把一条原始事件写进流水小窗（任何线程都能调）。</summary>
        private void Trace(string tag, string text, Color color)
        {
            if (_logWin == null || _logWin.IsDisposed) return;
            _logWin.Append(tag, text, color);
        }

        /// <summary>
        /// 往流水表格插一行。
        ///
        /// ★ 这个方法**自己会切到 UI 线程**（2026-09-13 修界面卡死）：
        ///   原来它直接被事件订阅线程调用 ✗ 而里面动了 DataGridView ✓
        ///   表格开了 AutoSizeRowsMode=DisplayedCells ✓ 插入行会回调 UI 重新测量 ✓
        ///   非 UI 线程 + 回调 = **死锁** ✓ 用户那边界面直接「未响应」✓✓
        ///
        ///   做成自适应之后，**任何线程调用都安全** ✓ 不用每个调用点都记得包 Ui() ✓
        ///   （这类"底层方法自己保证线程安全"比"要求调用方都记住"可靠得多 ✗）
        /// </summary>
        private void AppendAlert(DateTime when, string verdict, string text, Color color)
        {
            if (_grid == null) return;
            if (InvokeRequired)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed && !Disposing)
                        BeginInvoke((MethodInvoker)delegate () { AppendAlert(when, verdict, text, color); });
                }
                catch (Exception) { }
                return;
            }
            try
            {
                // Insert 是 void，插完新行就在第 0 行（最新的在最上面）
                _grid.Rows.Insert(0, when.ToString("HH:mm:ss"), verdict, text);
                _grid.Rows[0].DefaultCellStyle.ForeColor = color;
                while (_grid.Rows.Count > 500) _grid.Rows.RemoveAt(_grid.Rows.Count - 1);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 追加一行到 live_alerts.csv。**每轮都写**（不只写告警）——
        /// 这样回头能看出"什么时候是正常的"，比只留告警更容易判断规律。
        /// 地址做脱敏（密码打码），免得文件里躺着明文密码。UTF-8 带 BOM，Excel 双击不乱码。
        /// </summary>
        /// <summary>
        /// 跨天就把当天那份 roll 走：live_alerts.csv → live_alerts_2026-09-13.csv，
        /// 然后留一个空文件继续写今天。
        ///
        /// ★ 2026-09-13 加。用户问：「告警记录太多了，会不会卡爆 .csv 文件？
        ///   要不要间隔一小时或三小时新起一个 .csv 文件」
        ///
        /// ★ 为什么"卡"不会发生（先澄清）：
        ///   · 写文件用的是 append（StreamWriter 第二参 true）→ O(1) ✓
        ///     不会因为文件变大而变慢 ✓
        ///   · 界面表格只留最近 500 行（`_grid.Rows.Count > 500` 就裁）✓
        ///   ★ 真正的问题是**打不开、找不到** ✗
        ///     定时轮询 10 秒一行 → 315 万行/年 → **470 MB** ✗
        ///     Excel 上限约 100 万行 ✗ 用户根本打不开 ✓
        ///
        /// ★ 为什么按**天**分，不是按小时：
        ///   按小时 → 一天 24 个 ✗ 一年 8760 个文件 ✗ 找不到 ✓
        ///   按三小时 → 一年 2920 个 ✗ 还是太多 ✓
        ///   按天   → 一天一个 ✓ 一年 365 个 ✓ 每个约 1.3 MB ✓✓
        ///            ★ 而且"昨天的记录"就是昨天那个文件 ✓ 最好找 ✓
        ///
        /// ★ 命名策略（关键，为了兼容）：
        ///   `live_alerts.csv` **永远是"今天"那份** ✓
        ///   跨天时把它改名成 `live_alerts_2026-09-13.csv` ✓ 再留个空的 ✓
        ///   → 界面显示的路径、sanitize.ps1、卸载清单**全都不用改** ✓✓
        ///
        /// ★ 只在"文件存在 且 最后写入日期不是今天"时才动 ✗
        ///   所以正常运行时零开销（一次 File.Exists）✓
        /// </summary>
        private void RollAlertsIfNeeded()
        {
            try
            {
                if (!File.Exists(OutFile)) return;

                // ★ 用**文件自身的最后写入时间**判断它属于哪一天
                //   （不是"现在几点" ✓ —— 程序长时间跑着跨天时也一样对 ✓）
                DateTime day = File.GetLastWriteTime(OutFile).Date;
                if (day >= DateTime.Today) return;        // 今天写的，不用动

                string archive = Path.Combine(AppPaths.DataDir,
                    "live_alerts_" + day.ToString("yyyy-MM-dd") + ".csv");

                if (File.Exists(archive))
                {
                    // 那一天已经有一份了（比如程序中途重启过几次）
                    // → **追加**进去，让同一天的数据在一起 ✓
                    // ★ 但要去掉当前文件的标题行 ✗ 否则标题会重复 ✓
                    string all = File.ReadAllText(OutFile, Encoding.UTF8);
                    int nl = all.IndexOf('\n');
                    if (nl >= 0) all = all.Substring(nl + 1);
                    if (all.Trim().Length > 0)
                        File.AppendAllText(archive, all, new UTF8Encoding(true));
                    File.Delete(OutFile);
                }
                else
                {
                    File.Move(OutFile, archive);          // ★ 移动不是复制 ✓ 快 ✓
                }

                // ★ 分卷完立刻建一个**空的、带表头的**当天文件 ✓
                //   为什么 —— 否则"今天那份"要等下一次写记录才出现 ✗
                //   用户在这中间去看 data\ 会以为记录丢了 ✓
                //   （而且程序正好在这时候崩掉的话，那个困惑会一直留着 ✗）
                try
                {
                    using (StreamWriter w2 = new StreamWriter(OutFile, false, new UTF8Encoding(true)))
                        w2.WriteLine("时间,判定,耗时ms,摄像头,内容");
                }
                catch (Exception) { }   // 建不出来也不要紧 ✓ 下次写记录会自动建 ✓

                Log.Info("巡检记录按天分卷：" + Path.GetFileName(archive) + "（今天另起一份）");
            }
            catch (Exception ex)
            {
                // ★★ 分卷失败不能影响写记录 ✗ —— 大不了继续往同一个文件里追加 ✓
                //   最常见的失败原因：用户正用 Excel 打开着那个文件 ✓
                Log.Debug("巡检记录分卷失败（继续用同一个文件）：" + ex.Message);
            }
        }

        private void WriteLog(DateTime when, bool alert, int ms, string url, string desc)
        {
            try
            {
                // ★★ 写之前先检查要不要按天分卷（2026-09-13 加）
                //   用户问：「它输出的告警记录太多了，会不会卡爆 .csv 文件。
                //             记录要不要间隔一小时或三小时新起一个 .csv 文件」
                //   实测：定时轮询模式 10 秒一行 → 360 行/小时 → 315 万行/年
                //         一行约 150 字节 → **470 MB/年**，Excel 直接打不开
                //
                //   ★ 但"卡"不会发生：
                //     · 写文件是 append（O(1)），不会因为文件变大而变慢
                //     · 界面表格只留最近 500 行（超过就裁）
                //     真正的问题是**打不开、找不到** ✗
                //
                //   ★ 按**天**分卷（不是按小时 —— 那样一年 8760 个文件，找不到）
                //     一天一个，一年 365 个，每个约 1.3 MB，Excel 秒开
                RollAlertsIfNeeded();

                bool needHead = !File.Exists(OutFile);
                using (StreamWriter w = new StreamWriter(OutFile, true, new UTF8Encoding(true)))
                {
                    if (needHead) w.WriteLine("时间,判定,耗时ms,摄像头,内容");
                    w.WriteLine(string.Join(",", new string[] {
                        when.ToString("yyyy-MM-dd HH:mm:ss"),
                        alert ? "告警" : "正常",
                        ms.ToString(),
                        Csv(Secret.MaskUrl(url)),
                        Csv(desc)
                    }));
                }
            }
            catch (Exception ex) { Log.Warn("写入 " + Path.GetFileName(OutFile) + " 失败：" + ex.Message); }
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
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

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            try { Themes.Changed -= OnThemeChanged; } catch (Exception) { }
            _stop = true;
            _running = false;
            // ★ 关窗口也要掐断在途的推理/抓帧（2026-09-13 加）
            //   用户多半是**直接关窗口**，不点停止 ✓ 不掐断的话：
            //   进程虽然退了 ✓ 但库那边的 HTTP 连接要等超时，
            //   Ollama 会把这段生成跑完才释放显存 ✓ 下次打开更慢 ✓
            StopInFlight();
            try { if (_logWin != null && !_logWin.IsDisposed) _logWin.Close(); } catch (Exception) { }
            try { if (_timer != null) _timer.Stop(); } catch (Exception) { }
            try { if (_alarm != null) _alarm.Stop(); } catch (Exception) { }

            // ★★ 关程序时也要还原摄像头的「报警上传」地址（2026-09-13 加）
            //
            //   用户问：「我用完了，你会进摄像头改回默认的旧地址么？」
            //   ★ 光在"点停止"里还原是不够的 —— **很多人是直接关窗口**，不点停止 ✓
            //     不还原的话摄像头就一直往这个地址推，而且下次换台电脑跑它还是指着旧地址 ✓
            //   ★ 而且必须**同步**做 ✗：这是个 Background 线程随进程退出 ✓
            //     丢到后台线程去做 → 进程可能先退出了 ✓ 还原就没发生 ✓
            //   ★ 还原失败不能挡住关闭 ✓ 所以整段包在 try 里 ✓
            try
            {
                string rb = HikIsapi.RestoreUploadHost();
                if (rb.Length > 0) Log.Info("关程序时：" + rb);
            }
            catch (Exception) { }

            // 不等后台那一轮跑完：它是 Background 线程，随进程退出即可
        }
    }
}
