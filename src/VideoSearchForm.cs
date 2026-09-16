/* -*- coding: utf-8 -*-
 * VideoSearchForm.cs — AI 内容理解 + 海康式文搜窗口（v1.2 关键帧模式）
 * 功能：选一个视频 → 全片按需抽帧 → AI 逐帧理解内容（含画面时间 OSD）→
 *       内容时间轴（列表 + 横向时间轴）→ 文搜定位 → 点击/拖动一键抽帧定位画面
 * 播放：主画面用 ffmpeg 关键帧抽帧显示（H.264/H.265/任何 ffmpeg 可解格式都支持）；
 *       附加系统播放器(WMP)连续播放能力，能播则播，不能播自动降级关键帧模式。
 * 零第三方依赖。C# 5 兼容语法（编译需 /r:Microsoft.CSharp.dll）。
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
    /// <summary>AI 理解的一条记录：时间点 + 判定 + 画面时间 OSD + 内容描述。</summary>
    public class ContentNote
    {
        public double Time;
        public string Status = "SKIP";   // PASS / WARN / FAIL / SKIP
        public string Osd = "";          // 画面上的时间水印（AI 识别）
        public string Text = "";
    }

    public partial class VideoSearchForm : Form
    {
        // ---- 控件 ----
        private TextBox _pathBox;
        private Button _browseBtn, _analyzeBtn, _stopBtn;
        private Label _urlLab;            // Ollama 地址只读显示（统一在「设置」页改）
        private ComboBox _modelBox;
        private Button _fetchBtn;
        private NumericUpDown _intervalBox;   // 抽帧间隔（秒）
        private TextBox _searchBox;
        private Button _searchBtn, _clearSearchBtn;
        private Panel _playerPanel;
        private PictureBox _frameBox;         // 关键帧画面
        private WmpHost _wmp;                 // 附加系统播放器（H.264 等可播格式）
        private bool _wmpUsable;
        private volatile bool _wmpPlayable;   // 系统播放器对该视频能否真的解码播放（后台探测，H.265 常不可播）
        private TrackBar _seekBar;
        private Label _timeLab;
        private Button _playBtn, _pauseBtn, _stopPlayBtn;
        private Panel _tlPanel;               // 横向内容时间轴
        private DataGridView _grid;
        private Label _statusLab;
        private ProgressBar _progress;
        private Label _pctLab;          // 进度百分比（ProgressBar 自身不显示数字，必须另配文字）
        private Label _stateLab;              // 画面右下角播放状态角标
        private Panel _gridSplitter;          // 列表上方可拖拽分隔条（调整列表高度）
        private ComboBox _rateBox;            // 播放倍率
        private Button _exportBtn;            // 导出列表(CSV)
        private Button _zoomBtn;              // 放大查看 AI 理解内容
        private bool _splitDrag;

        private readonly List<ContentNote> _notes = new List<ContentNote>();
        private readonly Dictionary<string, byte[]> _frameCache = new Dictionary<string, byte[]>();   // 键 = 视频路径|时间
        private volatile bool _cancel;
        private Thread _worker;
        private double _duration;
        private bool _userDrag;
        private bool _syncBusy;

        // ==================== AI 时间轴游标（与视频游标独立）====================
        // 设计说明（2026-09-12）：
        //   原先时间轴只响应单击，而且它画的那条"当前位置"线读的是视频进度条的值 ——
        //   等于两个功能共用一个位置 ✗ 用户想沿 AI 结果滑动查看时，视频会被一起拖动。
        //   现在拆成两条真正独立的游标：
        //     · _seekBar       视频游标，管播放位置，跟着播放走
        //     · _tlCursorSec   AI 时间轴游标，只管在 AI 结果上滑动查看，**不动视频**
        //   想跳过去时用「双击时间轴」或「跳到这一帧」按钮 —— 把主动权交给用户。
        private double _tlCursorSec = -1;      // AI 游标位置（秒）；-1 = 还没设置
        private bool _tlDragging;              // 是否正在拖 AI 游标
        private Button _tlJumpBtn;             // 「跳到这一帧」按钮
        private Button _tlPlayBtn;             // 「预览这一帧」按钮

        // ==================== 左右分栏（2026-09-12 改版）====================
        // 原来画面区铺满整宽，视频按 Zoom 居中 → 左右各留一大条黑边 ✗
        // 现在改成：左边视频、右边 AI 内容列表，中间一条竖分隔条可拖动调宽度 ✓
        private double _videoAspect = 16.0 / 9.0;   // 视频宽高比（加载画面后按真实值更新）
        private double _splitRatio = 0.62;          // 视频占左侧可分配宽度的比例
        private int _splitStartX;                   // 拖分隔条起点（屏幕坐标）
        private double _splitStartRatio;

        private System.Windows.Forms.Timer _tm;

        // 关键帧抽帧请求（防抖：同一时间只抽一帧，完成后再补抽最新）
        private readonly object _reqLock = new object();
        private double _reqTime = -1;
        private string _reqPath = "";          // 与 _reqTime 配套的视频路径（换视频后旧线程必须用新路径）
        private volatile bool _frameBusy;

        // 关键帧连续播放：系统播放器不支持该编码（典型是 H.265）时的兜底。
        // 原来这种情况点「播放」只写一句提示、什么也不做，用户会以为播放器坏了且无法退出关键帧模式。
        private System.Windows.Forms.Timer _kfTimer;
        private bool _kfPlaying;
        private double _kfPos;
        private int _kfLastMs;

        // ==================== 播放器（WMP ActiveX 嵌入，可选） ====================

        private sealed class WmpHost : AxHost
        {
            public WmpHost() : base("6BF52A52-394A-11D3-B153-00C04F79FAA6") { }
            public dynamic Player { get { return GetOcx(); } }
        }

        // ==================== 构造 ====================

        public VideoSearchForm()
        {

            AppInfo.SetFormIcon(this);
            Text = "AI 内容理解与文搜（海康式） — 视频核对工具";
            Font = new Font("Microsoft YaHei", 9F);
            ClientSize = new Size(980, 680);
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 246, 250);
            BuildUi();
            _tm = new System.Windows.Forms.Timer();
            _tm.Interval = 500;
            _tm.Tick += OnTick;
            _tm.Start();
        }

        private void BuildUi()
        {
            int y = 10;
            Font f = new Font("Microsoft YaHei", 9F);
            Font sf = new Font("Microsoft YaHei", 8.5F);

            // 第一行：文件选择
            Label fl = new Label(); fl.Text = "视频文件:"; fl.Font = f; fl.AutoSize = true; fl.Location = new Point(12, y + 4);
            Controls.Add(fl);
            _pathBox = new TextBox(); _pathBox.Location = new Point(82, y); _pathBox.Size = new Size(620, 22); _pathBox.Font = f;
            Controls.Add(_pathBox);
            _browseBtn = new RoundButton(); _browseBtn.Text = "浏览…"; _browseBtn.Font = f; _browseBtn.Location = new Point(708, y - 2); _browseBtn.Size = new Size(70, 26);
            _browseBtn.Click += OnBrowse; Controls.Add(_browseBtn);
            _analyzeBtn = new RoundButton(); _analyzeBtn.Text = "开始理解"; _analyzeBtn.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            _analyzeBtn.BackColor = Color.FromArgb(37, 99, 235); _analyzeBtn.ForeColor = Color.White; _analyzeBtn.FlatStyle = FlatStyle.Flat;
            _analyzeBtn.FlatAppearance.BorderSize = 0; _analyzeBtn.Location = new Point(788, y - 2); _analyzeBtn.Size = new Size(88, 26);
            _analyzeBtn.Click += OnAnalyze; Controls.Add(_analyzeBtn);
            _stopBtn = new RoundButton(); _stopBtn.Text = "停止"; _stopBtn.Font = f; _stopBtn.ForeColor = Color.FromArgb(220, 38, 38);
            _stopBtn.Location = new Point(882, y - 2); _stopBtn.Size = new Size(60, 26); _stopBtn.Enabled = false;
            _stopBtn.Click += OnStop; Controls.Add(_stopBtn);
            y += 34;

            // 第二行：Ollama 地址（只读，统一在「设置」页改）+ 模型（可切换，写回设置）+ 抽帧间隔
            Label ul = new Label(); ul.Text = "Ollama:"; ul.Font = f; ul.AutoSize = true; ul.Location = new Point(12, y + 4);
            Controls.Add(ul);
            _urlLab = new Label(); _urlLab.Text = AppSettings.OllamaUrl; _urlLab.Font = f; _urlLab.AutoSize = true;
            _urlLab.Name = "aiUrlLab"; _urlLab.Location = new Point(82, y + 4); Controls.Add(_urlLab);
            Label ml = new Label(); ml.Text = "模型:"; ml.Font = f; ml.AutoSize = true; ml.Location = new Point(300, y + 4);
            Controls.Add(ml);
            _modelBox = new ComboBox(); _modelBox.DropDownStyle = ComboBoxStyle.DropDown;
            _modelBox.Items.Add("qwen2.5vl:7b"); _modelBox.Items.Add("qwen3-vl:8b");
            _modelBox.Text = AppSettings.OllamaModel;      // 与「设置」页共用一份
            _modelBox.Location = new Point(346, y); _modelBox.Size = new Size(180, 24); _modelBox.Font = f;
            _modelBox.TextChanged += delegate(object s2, EventArgs e2) { AppSettings.SetModel(_modelBox.Text.Trim()); };
            Controls.Add(_modelBox);
            _fetchBtn = new RoundButton(); _fetchBtn.Text = "设置..."; _fetchBtn.Font = sf; _fetchBtn.Location = new Point(534, y - 2); _fetchBtn.Size = new Size(80, 26);
            _fetchBtn.Click += OnOpenSettings; Controls.Add(_fetchBtn);
            Label il = new Label(); il.Text = "抽帧间隔(秒):"; il.Font = f; il.AutoSize = true; il.Name = "il"; il.Location = new Point(634, y + 4);
            Controls.Add(il);
            _intervalBox = new NumericUpDown(); _intervalBox.Minimum = 2; _intervalBox.Maximum = 300; _intervalBox.Value = 15; _intervalBox.Increment = 1;
            _intervalBox.Location = new Point(734, y); _intervalBox.Size = new Size(60, 22); _intervalBox.Font = f;
            Controls.Add(_intervalBox);
            Label itip = new Label(); itip.Text = "间隔越小分析越细（帧数上限 120，长视频自动稀释）"; itip.Font = sf; itip.ForeColor = Color.Gray; itip.AutoSize = true;
            itip.Name = "itip";
            Controls.Add(itip);
            y += 34;

            // 抽帧间隔说明独占一行：原来贴在数字框右侧，窗口宽 980 时文字会被裁掉
            itip.Location = new Point(82, y + 2);
            y += 20;

            // AI 进阶功能提示（小字，面向小白）
            Label aiTip = new Label();
            aiTip.Text = "提示：AI 画面理解/文搜属进阶功能，需本机已装并启动 Ollama、已拉取视觉模型（推荐 qwen2.5vl:7b）。小白可跳过，不影响基础检测。";
            aiTip.Font = sf;
            aiTip.ForeColor = Color.Gray;   // 走"灰色提示文字"规则，自动跟随主题
            aiTip.AutoSize = true;
            aiTip.Name = "aiTip";
            aiTip.Location = new Point(82, y + 4);
            Controls.Add(aiTip);
            y += 22;

            // 第三行：文搜
            Label sl = new Label(); sl.Text = "文搜:"; sl.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold); sl.AutoSize = true; sl.Location = new Point(12, y + 4);
            Controls.Add(sl);
            _searchBox = new TextBox(); _searchBox.Font = f; _searchBox.Location = new Point(82, y); _searchBox.Size = new Size(480, 22);
            _searchBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) DoSearch(); };
            Controls.Add(_searchBox);
            _searchBtn = new RoundButton(); _searchBtn.Text = "搜索定位"; _searchBtn.Font = f; _searchBtn.Location = new Point(572, y - 2); _searchBtn.Size = new Size(84, 26);
            _searchBtn.Click += (s, e) => DoSearch(); Controls.Add(_searchBtn);
            _clearSearchBtn = new RoundButton(); _clearSearchBtn.Text = "清除"; _clearSearchBtn.Font = f; _clearSearchBtn.Location = new Point(662, y - 2); _clearSearchBtn.Size = new Size(56, 26);
            _clearSearchBtn.Click += (s, e) => { _searchBox.Text = ""; DoSearch(); }; Controls.Add(_clearSearchBtn);
            _exportBtn = new RoundButton(); _exportBtn.Text = "导出列表"; _exportBtn.Font = f; _exportBtn.Location = new Point(726, y - 2); _exportBtn.Size = new Size(84, 26);
            _exportBtn.Click += (s, e) => OnExportList(); Controls.Add(_exportBtn);
            _zoomBtn = new RoundButton(); _zoomBtn.Text = "放大查看"; _zoomBtn.Font = f; _zoomBtn.Location = new Point(818, y - 2); _zoomBtn.Size = new Size(84, 26);
            _zoomBtn.Click += (s, e) => OpenDetail(); Controls.Add(_zoomBtn);
            Label stip = new Label(); stip.Text = "模糊搜索：逗号/空格分隔，任一命中即显示，命中越多排越前（如：汽车 移动 快递），可搜时间如 06:00"; stip.Font = sf; stip.ForeColor = Color.Gray; stip.AutoSize = true;
            stip.Name = "stip";
            Controls.Add(stip);
            y += 34;

            // 文搜说明独占一行：原来贴在「放大查看」右侧，超出窗口近 500px
            stip.Location = new Point(82, y + 2);
            y += 20;

            // 播放器/关键帧区域
            _playerPanel = new BufferedPanel();   // 双缓冲：画面切换时不闪
            _playerPanel.BorderStyle = BorderStyle.FixedSingle;
            _playerPanel.BackColor = Color.Black;
            _playerPanel.Location = new Point(12, y);
            _playerPanel.Size = new Size(930, 290);
            Controls.Add(_playerPanel);
            _frameBox = new PictureBox();
            _frameBox.Dock = DockStyle.Fill;
            _frameBox.SizeMode = PictureBoxSizeMode.Zoom;   // 缓冲帧已按框尺寸缩好，这里等于 1:1 贴图
            _frameBox.BackColor = Color.Black;
            _playerPanel.Controls.Add(_frameBox);
            // 右下角播放状态角标
            _stateLab = new Label();
            _stateLab.AutoSize = false;
            _stateLab.Size = new Size(96, 22);
            _stateLab.TextAlign = ContentAlignment.MiddleCenter;
            _stateLab.ForeColor = Color.White;
            _stateLab.BackColor = Color.FromArgb(150, 0, 0, 0);
            _stateLab.Font = new Font("Microsoft YaHei", 8.5F);
            _stateLab.Text = "未加载";
            // 注意：这个标签是加进 _playerPanel 的，坐标必须用面板自己的客户区宽度。
            // 原来写 _playerPanel.Right（窗体坐标）会让它伸出面板右边界 8px。
            _stateLab.Location = new Point(_playerPanel.ClientSize.Width - 102, _playerPanel.ClientSize.Height - 28);
            _playerPanel.Controls.Add(_stateLab);
            _stateLab.BringToFront();
            y += 294;

            // 播放控制行（独立一行，不再与文字重叠）
            _playBtn = new RoundButton(); _playBtn.Text = "播放"; _playBtn.Font = f; _playBtn.Location = new Point(12, y); _playBtn.Size = new Size(62, 26);
            _playBtn.Click += (s, e) => PlayOrResume(); Controls.Add(_playBtn);
            _pauseBtn = new RoundButton(); _pauseBtn.Text = "暂停"; _pauseBtn.Font = f; _pauseBtn.Location = new Point(78, y); _pauseBtn.Size = new Size(62, 26);
            _pauseBtn.Click += (s, e) => { SafeWmp("pause"); KeyframePause(); }; Controls.Add(_pauseBtn);
            _stopPlayBtn = new RoundButton(); _stopPlayBtn.Text = "停止"; _stopPlayBtn.Font = f; _stopPlayBtn.Location = new Point(144, y); _stopPlayBtn.Size = new Size(62, 26);
            _stopPlayBtn.Click += (s, e) => { SafeWmp("stop"); KeyframeStop(); }; Controls.Add(_stopPlayBtn);
            Button backBtn = new RoundButton(); backBtn.Text = "快退10s"; backBtn.Font = f; backBtn.Name = "backBtn"; backBtn.Location = new Point(210, y); backBtn.Size = new Size(78, 26);
            backBtn.Click += (s, e) => OnSkip(-10); Controls.Add(backBtn);
            Button fwdBtn = new RoundButton(); fwdBtn.Text = "快进10s"; fwdBtn.Font = f; fwdBtn.Name = "fwdBtn"; fwdBtn.Location = new Point(292, y); fwdBtn.Size = new Size(78, 26);
            fwdBtn.Click += (s, e) => OnSkip(10); Controls.Add(fwdBtn);
            Label rateLab = new Label(); rateLab.Text = "倍率:"; rateLab.Font = f; rateLab.AutoSize = true; rateLab.Location = new Point(380, y + 4);
            Controls.Add(rateLab);
            _rateBox = new ComboBox(); _rateBox.DropDownStyle = ComboBoxStyle.DropDownList; _rateBox.Font = f;
            _rateBox.Items.Add("1x"); _rateBox.Items.Add("1.5x"); _rateBox.Items.Add("2x"); _rateBox.Items.Add("4x"); _rateBox.Items.Add("8x");
            _rateBox.SelectedIndex = 0;
            _rateBox.Location = new Point(422, y); _rateBox.Size = new Size(64, 22);
            Controls.Add(_rateBox);
            _seekBar = new TrackBar(); _seekBar.Minimum = 0; _seekBar.Maximum = 1000; _seekBar.TickStyle = TickStyle.None;
            _seekBar.AutoSize = false; _seekBar.Height = 26;   // 关键：禁用 TrackBar 强制 45px 高度，避免盖住下一行
            _seekBar.Location = new Point(496, y + 1); _seekBar.Size = new Size(350, 26);
            _seekBar.MouseDown += (s, e) => { _userDrag = true; SafeWmp("pause"); KeyframePause(); };   // 拖动即暂停播放
            _seekBar.ValueChanged += OnSeekChanged;
            _seekBar.MouseUp += (s, e) => { _userDrag = false; };
            // 兜底：若鼠标在控件外抬起，MouseUp 可能收不到，_userDrag 会一直卡在 true，
            // 导致进度条滑块永久停止跟随播放。用捕获变化事件保证复位。
            _seekBar.MouseCaptureChanged += (s, e) => { if (!_seekBar.Capture) _userDrag = false; };
            Controls.Add(_seekBar);
            _timeLab = new Label(); _timeLab.Text = "00:00 / 00:00 (0%)"; _timeLab.Font = sf; _timeLab.ForeColor = Color.Gray; _timeLab.AutoSize = true;
            _timeLab.Location = new Point(860, y + 5); Controls.Add(_timeLab);
            y += 34;

            // 状态 + 分析进度（独立一行）
            _statusLab = new Label(); _statusLab.Text = "就绪：选视频后点「开始理解」。H.264/H.265/MP4/AVI 等均支持，画面用关键帧显示，进度条可拖动定位。";
            _statusLab.Font = sf; _statusLab.ForeColor = Color.Gray; _statusLab.AutoSize = true; _statusLab.Location = new Point(12, y + 2);
            Controls.Add(_statusLab);
            _progress = new ProgressBar(); _progress.Location = new Point(700, y); _progress.Size = new Size(190, 16); _progress.Minimum = 0; _progress.Maximum = 100;
            Controls.Add(_progress);
            _pctLab = new Label(); _pctLab.Text = "0%"; _pctLab.Font = sf; _pctLab.AutoSize = false;
            _pctLab.Size = new Size(46, 16); _pctLab.TextAlign = ContentAlignment.MiddleLeft;
            _pctLab.Location = new Point(898, y + 1);
            Controls.Add(_pctLab);
            y += 30;

            // 内容时间轴（横向）+ 列表
            Label tl = new Label();
            tl.Text = "AI 内容时间轴（橙手柄=AI 游标，可拖动查看；蓝竖线=视频当前位置；双击跳转）";
            tl.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            tl.AutoSize = true; tl.Name = "tlTitle"; tl.Location = new Point(12, y + 2); Controls.Add(tl);
            y += 24;

            // 用双缓冲面板：时间轴绘制分「清屏 → 贴点位位图 → 画两条游标」几步 ✗
            // 普通 Panel 不做双缓冲，中间状态会被看到 → 用户反馈"一闪一闪的" ✓
            _tlPanel = new BufferedPanel();
            _tlPanel.BorderStyle = BorderStyle.FixedSingle; _tlPanel.BackColor = Color.White;
            _tlPanel.Location = new Point(12, y); _tlPanel.Size = new Size(930, 58);
            _tlPanel.Paint += OnTlPaint;
            // 时间轴游标：按住拖动只移动 AI 游标，不动视频 ✓
            // （早先只有 MouseClick —— 拖是没反应的，用户以为"拖不动"）
            _tlPanel.MouseDown += OnTlMouseDown;
            _tlPanel.MouseMove += OnTlMouseMove;
            _tlPanel.MouseUp += OnTlMouseUp;
            _tlPanel.MouseDoubleClick += OnTlDoubleClick;   // 双击才跳转视频，主动权交给用户
            Controls.Add(_tlPanel);

            // 时间轴的两个操作按钮（放在标题行右侧，不占额外高度）
            _tlJumpBtn = new RoundButton();
            _tlJumpBtn.Text = "跳到这一帧"; _tlJumpBtn.Font = sf;
            _tlJumpBtn.Location = new Point(ClientSize.Width - 12 - 100 - 8 - 100, y - 25);
            _tlJumpBtn.Size = new Size(100, 22);
            _tlJumpBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _tlJumpBtn.Click += delegate(object s, EventArgs e)
            {
                if (_tlCursorSec < 0) { _statusLab.Text = "先把橙色游标拖到你想看的位置"; return; }
                JumpToFrame(_tlCursorSec);
            };
            Controls.Add(_tlJumpBtn);

            _tlPlayBtn = new RoundButton();
            _tlPlayBtn.Text = "预告片"; _tlPlayBtn.Font = sf;
            _tlPlayBtn.Location = new Point(ClientSize.Width - 12 - 100, y - 25);
            _tlPlayBtn.Size = new Size(100, 22);
            _tlPlayBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _tlPlayBtn.Click += delegate(object s, EventArgs e)
            {
                // 把游标所在位置附近的时间点依次串起来看 —— 比一个个手动跳快
                if (_tlCursorSec < 0) { _statusLab.Text = "先把橙色游标拖到你想看的位置"; return; }
                PlayPreviewFrom(_tlCursorSec);
            };
            Controls.Add(_tlPlayBtn);

            y += 64;

            // 竖分隔条：左右拖动调整「视频 / AI 列表」的宽度比例（2026-09-12 改版）
            // 原来是横的、调列表高度 ✗ 现在列表在右侧和视频等高，改调宽度 ✓
            _gridSplitter = new Panel();
            _gridSplitter.BackColor = Color.FromArgb(203, 213, 225);
            _gridSplitter.Cursor = Cursors.SizeWE;
            _gridSplitter.Size = new Size(6, 300);
            _gridSplitter.Location = new Point(12, y);
            _gridSplitter.MouseDown += OnSplitterDown;
            _gridSplitter.MouseMove += OnSplitterMove;
            _gridSplitter.MouseUp += delegate(object s, MouseEventArgs e) { _splitDrag = false; };
            Controls.Add(_gridSplitter);

            _grid = new DataGridView();
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            // ★ 自动换行 + 行高自适应（2026-09-12 修）：
            //   DataGridView 默认 WrapMode = False ✗ 文本超出就截断加"…"✓
            //   AI 理解内容动辄二三十个字，列表挪到右栏变窄后就只剩省略号了 ✓
            //   开启换行并让行高按内容长高 —— 配合右栏可拖宽，内容能完整读 ✓
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            // ★ AllCells → DisplayedCells（2026-09-12 修"分析之后播放卡顿"）：
            //   AllCells 每次重绘都要重新量**所有**行的换行高度 ✗
            //   实测 400 行时一次重绘要 26ms ✓ 而播放的定时器每 33ms 就重绘一次 ✓
            //   两者抢 UI 线程 → 播放帧间隔从 94ms 恶化到 172ms ✗（用户反馈的场景正是
            //   「先做完开始理解」——那时列表里有几百行 ✓）
            //   DisplayedCells 只量**可见的**那二十来行 ✓ 效果一样但快一个数量级 ✓
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 30;
            _grid.Columns.Add("cTime", "位置");
            _grid.Columns.Add("cOsd", "画面时间");
            _grid.Columns.Add("cStatus", "判定");
            _grid.Columns.Add("cText", "AI 理解内容");
            // 右栏变窄后，固定列宽必须收紧，否则内容列被挤得只剩一点点 ✗
            // 「画面时间」只显示到秒就够（完整日期在导出 CSV 里仍是全的）
            _grid.Columns[0].Width = 46;
            _grid.Columns[1].Width = 84;
            _grid.Columns[2].Width = 46;
            _grid.Columns[1].DefaultCellStyle.Font = new Font("Microsoft YaHei", 8F);
            _grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _grid.Columns[3].MinimumWidth = 120;
            _grid.CellDoubleClick += OnGridJump;
            _grid.Location = new Point(12, y);
            _grid.Size = new Size(930, ClientSize.Height - y - 14);
            Controls.Add(_grid);

            // 窗口缩放自适应：拖动边框/最大化时重新排布全部行
            Resize += delegate(object s2, EventArgs e2) { LayoutRows(); };
            LayoutRows();

            ApplyTheme();
            Themes.Changed += OnThemeChanged;
            FormClosed += delegate(object s2, FormClosedEventArgs e2) { Themes.Changed -= OnThemeChanged; };
        }

        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;   // 已释放的窗体不得再着色（否则访问违例）
            if (InvokeRequired) { BeginInvoke((MethodInvoker)OnThemeChanged); return; }
            ApplyTheme();
        }

        /// <summary>主题化。播放器面板与画面框必须保持纯黑（看视频用），不随主题变。</summary>
        private void ApplyTheme()
        {
            Theme t = Themes.Current;
            Themes.ApplyTo(this);
            _playerPanel.BackColor = Color.Black;
            _frameBox.BackColor = Color.Black;
            _tlPanel.BackColor = t.Card;
            _gridSplitter.BackColor = t.Border;
            if (_analyzeBtn != null) Themes.StylePrimary(_analyzeBtn);
            if (_stopBtn != null) Themes.StyleTextOnly(_stopBtn, t.Danger);
            _tlPanel.Invalidate();
        }

        // ==================== 自适应布局 ====================

        private void LayoutRows()
        {
            int M = 12;
            int W = ClientSize.Width;
            int H = ClientSize.Height;
            if (W < 920) return;
            int y = 10;

            // 第一行：文件选择
            int pathW = W - 82 - 8 - 70 - 8 - 88 - 8 - 60 - M;
            if (pathW < 200) pathW = 200;
            _pathBox.Location = new Point(82, y); _pathBox.Width = pathW;
            _browseBtn.Location = new Point(82 + pathW + 8, y - 2);
            _analyzeBtn.Location = new Point(_browseBtn.Left + 70 + 8, y - 2);
            _stopBtn.Location = new Point(_analyzeBtn.Left + 88 + 8, y - 2);
            y += 34;

            // 第二行：Ollama + 抽帧间隔（右侧区块随窗口右移）
            Label il = (Label)Controls[Controls.IndexOfKey("il")];
            Label itip = (Label)Controls[Controls.IndexOfKey("itip")];
            // 下限必须避开左边的「设置...」按钮（它在 534..614）。
            // 原来下限是 596，于是标签被算到 572，正好压在按钮上 —— 两段文字叠在一起像乱码。
            int intervalX = W - 300;
            if (intervalX < 730) intervalX = 730;
            il.Left = intervalX - 100 - 8;                // "抽帧间隔(秒):"
            _intervalBox.Location = new Point(intervalX, y);
            _intervalBox.Width = 60;
            y += 34;

            // 抽帧间隔说明独占一行（与 BuildUi 保持一致）
            itip.Location = new Point(82, y + 2);
            y += 20;

            // AI 进阶提示行
            Label aiTip = (Label)Controls[Controls.IndexOfKey("aiTip")];
            aiTip.Location = new Point(82, y + 4);
            y += 24;      // 原来 22 会让下一行按钮顶部压住提示文字最后 1 像素

            // 第三行：文搜
            int searchW = W - 82 - 8 - 84 - 8 - 56 - 8 - 84 - 8 - 84 - 8 - 200;
            if (searchW < 200) searchW = 200;
            _searchBox.Location = new Point(82, y); _searchBox.Width = searchW;
            _searchBtn.Location = new Point(82 + searchW + 8, y - 2);
            _clearSearchBtn.Location = new Point(_searchBtn.Left + 84 + 8, y - 2);
            _exportBtn.Location = new Point(_clearSearchBtn.Left + 56 + 8, y - 2);
            _zoomBtn.Location = new Point(_exportBtn.Left + 84 + 8, y - 2);
            y += 34;

            // 文搜说明独占一行（与 BuildUi 保持一致）
            Label stip = (Label)Controls[Controls.IndexOfKey("stip")];
            stip.Location = new Point(82, y + 2);
            y += 20;

            // ==================== 左右分栏：左视频 / 右 AI 内容列表（2026-09-12 改版）====================
            // 原来画面铺满整宽、视频按 Zoom 居中 → 左右各留一大条黑边 ✗
            // 现在按视频宽高比给左侧定宽，右侧腾出来放 AI 内容列表 ✓
            //
            // 预留高度：34 控制行 + 30 状态行 + 24 时间轴标题 + 58 时间轴 + 14 底边距
            int playerH = H - y - (34 + 30 + 24 + 58 + 14);
            if (playerH < 160) playerH = 160;

            int colGap = 10;                                   // 两栏间距（分隔条也在这段里）
            int availW = W - M * 2 - colGap;
            if (availW < 360) availW = 360;

            int videoW = (int)(availW * _splitRatio);
            int listW = availW - videoW;
            if (listW < 200) { listW = 200; videoW = availW - listW; }   // 列表别被压得太窄
            if (videoW < 260) { videoW = 260; listW = availW - videoW; }
            // 左侧再按视频真实宽高比收一下：理想宽 = 高 × 比例。
            // 太宽会留黑边、太窄会上下留黑边，所以夹在 [视频比例的 62%, 100%] 之间。
            int idealW = (int)(playerH * _videoAspect);
            int loW = (int)(idealW * 0.62);
            if (videoW > idealW) videoW = idealW;
            if (videoW < loW) videoW = loW;
            if (videoW < 260) videoW = 260;
            if (videoW > availW - 200) videoW = availW - 200;
            listW = availW - videoW;

            _playerPanel.Location = new Point(M, y);
            _playerPanel.Size = new Size(videoW, playerH);
            // _stateLab 是 _playerPanel 的子控件，坐标必须相对面板自身，不能用面板在窗体里的 Right/Bottom
            if (_stateLab != null) _stateLab.Location = new Point(
                _playerPanel.ClientSize.Width - _stateLab.Width - 6,
                _playerPanel.ClientSize.Height - _stateLab.Height - 6);

            // 竖分隔条 + 右侧 AI 内容列表（与视频等高）
            if (_gridSplitter != null)
            {
                _gridSplitter.Location = new Point(M + videoW + (colGap - 6) / 2, y);
                _gridSplitter.Size = new Size(6, playerH);
                _gridSplitter.BringToFront();
            }
            _grid.Location = new Point(M + videoW + colGap, y);
            _grid.Size = new Size(listW, playerH);
            y += playerH + 4;

            // 播放控制行（保持全宽 —— 按钮+倍率+进度条+时间需要横向空间，塞进左栏会挤爆）
            _playBtn.Location = new Point(12, y);
            _pauseBtn.Location = new Point(78, y);
            _stopPlayBtn.Location = new Point(144, y);
            Button backBtn = (Button)Controls[Controls.IndexOfKey("backBtn")];
            Button fwdBtn = (Button)Controls[Controls.IndexOfKey("fwdBtn")];
            backBtn.Location = new Point(210, y);
            fwdBtn.Location = new Point(292, y);
            _rateBox.Location = new Point(422, y);
            _timeLab.Location = new Point(W - 150, y + 5);
            int seekW = (W - 150) - 496 - 8;
            if (seekW < 200) seekW = 200;
            _seekBar.Location = new Point(496, y + 1);
            _seekBar.Width = seekW;
            y += 34;

            // 状态 + 分析进度（进度条右侧留出百分比文字位置）
            _statusLab.Location = new Point(12, y + 2);
            _progress.Location = new Point(W - 12 - 46 - 8 - 190, y);
            _progress.Width = 190;
            _pctLab.Location = new Point(W - 12 - 46, y + 1);
            y += 30;

            // 时间轴标题 + 时间轴 + 两个操作按钮
            Label tl = (Label)Controls[Controls.IndexOfKey("tlTitle")];
            tl.Location = new Point(12, y + 2);
            if (_tlJumpBtn != null) _tlJumpBtn.Location = new Point(W - M - 100 - 8 - 100, y);
            if (_tlPlayBtn != null) _tlPlayBtn.Location = new Point(W - M - 100, y);
            y += 24;
            _tlPanel.Location = new Point(M, y);
            _tlPanel.Size = new Size(W - M * 2, 58);
            y += 64;
            _grid.BringToFront();
        }

        // ==================== 事件：文件 / 模型 / 分析 ====================

        private void OnBrowse(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "选择要理解的视频文件";
                ofd.Filter = "视频文件|*.mp4;*.avi;*.wmv;*.mov;*.mkv;*.flv;*.ts;*.m4v;*.mpg;*.mpeg|所有文件|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = ofd.FileName;
                    Log.Info("内容理解选择文件：" + ofd.FileName);
                    LoadVideo(ofd.FileName);
                }
            }
        }

        /// <summary>打开共享设置页（Ollama 地址/模型/抽帧参数）。模型或地址改了这里同步刷新。</summary>
        private void OnOpenSettings(object sender, EventArgs e)
        {
            using (SettingsForm sf = new SettingsForm())
                sf.ShowDialog(this);
            if (_urlLab != null) _urlLab.Text = AppSettings.OllamaUrl;
            if (_modelBox != null && _modelBox.Text.Trim() != AppSettings.OllamaModel)
                _modelBox.Text = AppSettings.OllamaModel;
        }

        private void OnAnalyze(object sender, EventArgs e)
        {
            string path = _pathBox.Text.Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                MessageBox.Show(this, "请先选择存在的视频文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_worker != null && _worker.IsAlive)
            {
                MessageBox.Show(this, "正在分析中，请先停止", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // AI 调用全局互斥：防止「画面理解」与「内容文搜」同时开跑压爆本机显存
            string holder;
            if (!MageCheck.TryAcquire("内容理解/文搜", out holder))
            {
                MessageBox.Show(this,
                    "已有一个 AI 任务在运行（" + (holder.Length > 0 ? holder : "其它窗口") + "）。\n\n"
                    + "两个任务同时调用本机模型会把显存和推理队列挤满，两边都会变得极慢甚至超时。\n"
                    + "请等它结束，或先在那边点「停止」再回来重试。",
                    "AI 正忙", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _cancel = false;
            _notes.Clear();
            _grid.Rows.Clear();
            _tlPanel.Invalidate();
            _analyzeBtn.Enabled = false;
            _stopBtn.Enabled = true;
            SetProgress(0);

            string url = AppSettings.OllamaUrl;      // 地址统一在「设置」页维护
            string model = _modelBox.Text.Trim();
            int interval = (int)_intervalBox.Value;
            _worker = new Thread(delegate () { AnalyzeWorker(path, url, model, interval); });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void OnStop(object sender, EventArgs e)
        {
            _cancel = true;
            // 光置标志位不够：工作线程此刻多半正阻塞在推理请求（最长 180s）或 ffmpeg 抽帧
            // （最长 120s）里，标志位只在帧循环开头检查，必须把在途调用直接打断，
            // 否则按下停止后界面要等到当前帧跑完才有反应（最坏累计数分钟，看起来就是卡死）。
            MageCheck.AbortCurrent();   // 打断正在进行的 Ollama 请求
            Ffmpeg.KillRunning();       // 杀掉正在进行的抽帧 ffmpeg
            _stopBtn.Enabled = false;
            _statusLab.Text = "正在停止…";
            Log.Info("内容理解：用户请求停止");
        }

        /// <summary>统一设置进度与百分比文字（ProgressBar 自身不显示数字，必须另配标签）。</summary>
        private void SetProgress(int pct)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            _progress.Value = pct;
            if (_pctLab != null) _pctLab.Text = pct + "%";
        }

        /// <summary>供 ffmpeg 抽帧轮询的取消回调。</summary>
        private bool Cancelled() { return _cancel; }

        // ==================== 后台分析（增强提示词：读时间 OSD + 详细描述） ====================

        private const string ContentQuestion =
            "这是一张监控视频截帧。请严格按两行输出：\n" +
            "第一行：画面时间：仔细辨认画面上叠加的时间水印（形如 2026-09-11 06:00:48 或 09-11 06:00），原样输出识别到的数字；" +
            "如果画面上没有时间水印或看不清，就写：未识别到时间。\n" +
            "第二行：内容：详细描述画面里的人物、车辆、物品、动作、位置、周围环境等，尽量具体（不少于30字），不要评价画质。";

        private void AnalyzeWorker(string path, string url, string model, int interval)
        {
            try
            {
                _duration = ProbeDuration(path);
                int maxFrames = 120;
                List<double> times = MageCheck.PickFullTimes(_duration, interval, maxFrames);
                Ui(() => _statusLab.Text = "全片按需抽帧：" + times.Count + " 帧（间隔约 " + (times.Count > 1 ? (_duration / (times.Count - 1)).ToString("0.#") : "0") + "s）");
                Log.Info("内容理解开始：" + Path.GetFileName(path) + "，抽帧 " + times.Count + " 帧，间隔 " + interval + "s，模型 " + model);

                MageConfig cfg = new MageConfig();
                cfg.Url = url;
                cfg.Model = model;
                cfg.MaxTokens = 768;   // 思考型模型（qwen3-vl 等）需要足够预算，否则正文为空

                // 水印年份校准的锚点：文件的修改时间最接近录像时间
                DateTime anchor;
                try { anchor = File.GetLastWriteTime(path); }
                catch (Exception) { anchor = DateTime.Now; }
                if (anchor.Year < 1980) anchor = DateTime.Now;   // 取不到有效时间就退回系统时间

                for (int i = 0; i < times.Count; i++)
                {
                    if (_cancel) break;
                    double t = times[i];
                    int pct = (int)((double)i / times.Count * 100);
                    Ui(delegate()
                    {
                        SetProgress(pct);
                        _statusLab.Text = "正在分析第 " + (i + 1) + "/" + times.Count + " 帧（" + t.ToString("0.#") + "s）…";
                    });
                    byte[] jpg = null;
                    try { jpg = MageCheck.ExtractFrameJpeg(path, t, 1280, Cancelled); }
                    catch (MageCheck.StoppedException) { break; }        // 用户停止：立即结束，不记这一帧
                    catch (Exception) { jpg = null; }
                    if ((jpg == null || jpg.Length == 0) && t > 1)   // 末尾抽帧失败：往前 1s 重试一次
                    {
                        try { jpg = MageCheck.ExtractFrameJpeg(path, Math.Max(t - 1, 0), 1280, Cancelled); }
                        catch (MageCheck.StoppedException) { break; }
                        catch (Exception) { jpg = null; }
                    }
                    if (_cancel) break;
                    ContentNote note = new ContentNote();
                    note.Time = t;
                    if (jpg == null || jpg.Length == 0)
                    {
                        note.Status = "SKIP";
                        note.Text = "抽帧失败";
                    }
                    else
                    {
                        try
                        {
                            string answer = MageCheck.QueryOllama(url, model, jpg, ContentQuestion, cfg.MaxTokens);
                            ParseAnswer(answer, note, anchor);
                        }
                        catch (MageCheck.StoppedException) { break; }    // 用户停止
                        catch (Exception ex)
                        {
                            note.Status = "SKIP";
                            note.Text = "推理失败：" + Truncate(ex.Message, 60);
                        }
                    }
                    lock (_notes) { _notes.Add(note); InvalidateTlDots(); }
                    Ui(delegate()
                    {
                        int idx = _grid.Rows.Add(note.Time.ToString("0.#") + "s", ShortOsd(note.Osd), note.Status, note.Text);
                        _grid.Rows[idx].DefaultCellStyle.ForeColor = StatusColor(note.Status);
                        _grid.Rows[idx].Tag = note;
                        // 完整画面时间放进提示：列窄了只显示时分秒，但信息不能丢
                        _grid.Rows[idx].Cells[1].ToolTipText = note.Osd;
                        if (i == times.Count - 1 || i % 10 == 0) _tlPanel.Invalidate();
                    });
                }
                Ui(delegate()
                {
                    SetProgress(100);
                    _analyzeBtn.Enabled = true;
                    _stopBtn.Enabled = false;
                    _tlPanel.Invalidate();
                    _statusLab.Text = _cancel
                        ? ("已停止：保留已理解 " + _notes.Count + " 帧（可继续文搜与定位）")
                        : ("完成：共理解 " + _notes.Count + " 帧。可输入关键词文搜（含画面时间），点击时间轴/列表行/拖动进度条即可定位画面。");
                });
                Log.Info((_cancel ? "内容理解已停止：" : "内容理解完成：") + Path.GetFileName(path) + "，共 " + _notes.Count + " 帧");
            }
            catch (Exception ex)
            {
                Log.Error("内容理解异常：" + ex);
                Ui(delegate()
                {
                    _analyzeBtn.Enabled = true;
                    _stopBtn.Enabled = false;
                    _statusLab.Text = "分析出错：" + ex.Message;
                });
            }
            finally
            {
                MageCheck.Release("内容理解/文搜");   // 无论成功、失败还是被停止，都要放开 AI 名额
            }
        }

        /// <summary>解析 AI 回答：兼容「第一行：画面时间：xxx 第二行：内容：yyy」与换行两种格式，提取时间 OSD 与内容。
        /// anchor 是水印年份校准的锚点时间（文件用修改时间）。</summary>
        private static void ParseAnswer(string answer, ContentNote note, DateTime anchor)
        {
            if (string.IsNullOrEmpty(answer))
            {
                note.Status = "SKIP";
                note.Text = "模型未返回内容";
                return;
            }
            string t = answer.Trim();
            string osd = "";
            string content = "";

            // ① 首选「按行解析」。提示词要求第一行放时间、第二行放内容，模型绝大多数情况确实给两行，
            //    而且实测它更爱写「第一行：xxx / 第二行：yyy」，而不是提示词里要求的「画面时间：」「内容：」。
            //    按行取比找标记稳得多。
            string[] lines = t.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                string first = StripLeadLabel(lines[0]);
                // 首行必须"短"且像时间才认定为 OSD，避免模型把描述写在首行时被整段当成时间
                if (first.Length > 0 && first.Length <= 40
                    && (LooksLikeTime(first) || first.IndexOf("未识别", StringComparison.Ordinal) >= 0
                        || first.IndexOf("看不清", StringComparison.Ordinal) >= 0))
                {
                    osd = first;
                    System.Text.StringBuilder cb = new System.Text.StringBuilder();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string seg = StripLeadLabel(lines[i]);
                        if (seg.Length == 0) continue;
                        if (cb.Length > 0) cb.Append(' ');
                        cb.Append(seg);
                    }
                    content = cb.ToString().Trim();
                }
            }

            // ② 按行解析不成立（模型把答案挤成一行等）→ 退回标记法
            if (content.Length == 0)
            {
                foreach (string marker in new string[] { "画面时间：", "画面时间:", "时间：", "时间:" })
                {
                    int i = t.IndexOf(marker, StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        string tail = t.Substring(i + marker.Length);
                        int cut = int.MaxValue;
                        foreach (string stop in new string[] { "内容", "第二行", "\n", "\r" })
                        {
                            int j = tail.IndexOf(stop, StringComparison.Ordinal);
                            if (j >= 0 && j < cut) cut = j;
                        }
                        osd = (cut == int.MaxValue ? tail : tail.Substring(0, cut)).Trim().TrimStart('：', ':', ' ');
                        break;
                    }
                }
                if (osd.Length == 0)
                {
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(t,
                        @"\d{4}\s*[-年/]\s*\d{1,2}\s*[-月/]\s*\d{1,2}\s*[日号]?\s*\d{1,2}:\d{2}(?::\d{2})?");
                    if (m.Success) osd = m.Value;
                }
                foreach (string marker in new string[] { "内容：", "内容:" })
                {
                    int i = t.IndexOf(marker, StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        content = t.Substring(i + marker.Length).Trim().TrimStart('：', ':', ' ');
                        break;
                    }
                }
                if (content.Length == 0) content = t;   // 完全没按格式：整段作内容，下面会挖掉时间
            }
            // 无论走哪条分支，都把画面时间从内容里摘干净（时间已有独立的列）
            content = StripOsdFromContent(content, osd);
            // 年份校准：模型对年份数字常有系统性误读，用锚点时间（文件修改时间）判定
            osd = MageCheck.CalibrateOsdYear(osd, anchor);
            note.Osd = osd;
            note.Text = Truncate(content, 160);
            if (note.Text.Length == 0) note.Text = "（模型未给出内容描述）";
            note.Status = "PASS";
        }

        /// <summary>反复去掉行首的「第一行：」「第二行：」「画面时间：」「内容：」等标签。</summary>
        private static string StripLeadLabel(string s)
        {
            string r = (s ?? "").Trim();
            string[] labels = {
                "第一行：", "第一行:", "第一行", "第二行：", "第二行:", "第二行",
                "画面时间：", "画面时间:", "画面时间", "时间：", "时间:", "内容：", "内容:"
            };
            for (int guard = 0; guard < 5; guard++)
            {
                bool changed = false;
                foreach (string lb in labels)
                {
                    if (r.StartsWith(lb, StringComparison.Ordinal))
                    {
                        r = r.Substring(lb.Length).Trim();
                        changed = true;
                    }
                }
                if (!changed) break;
            }
            return r.Trim().TrimStart('：', ':', ' ', '　');
        }

        /// <summary>判断一段文本像不像时间。用于确认首行确实是画面时间而不是画面描述。</summary>
        private static bool LooksLikeTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string p = System.Text.RegularExpressions.Regex.IsMatch(s, @"\d{4}\s*[-年/]\s*\d{1,2}\s*[-月/]\s*\d{1,2}")
                     ? "y" : "";
            if (p == "y") return true;                                        // 2025-09-10 / 2025年09月10日
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\d{1,2}\s*[-月/]\s*\d{1,2}\s*[日号]?")) return true;  // 09-10 / 09月10日
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\d{1,2}:\d{2}(:\d{2})?")) return true;               // 16:07 / 16:07:17
            return false;
        }

        /// <summary>把画面时间从内容描述里摘掉。时间已经有独立的「画面时间」列，
        /// 在内容里再出现一次会让人以为解析错了。</summary>
        private static string StripOsdFromContent(string content, string osd)
        {
            string c = content;
            foreach (string junk in new string[] { "第一行：", "第一行:", "第二行：", "第二行:", "第一行", "第二行" })
                c = c.Replace(junk, " ");
            foreach (string mk in new string[] { "画面时间：", "画面时间:", "画面时间" })
                c = c.Replace(mk, " ");
            if (!string.IsNullOrEmpty(osd)) c = c.Replace(osd, " ");
            // 开头若还残留一个「时间：」标签，一并去掉
            c = System.Text.RegularExpressions.Regex.Replace(c, "^[\\s　]*时间[\\s　]*[:：]", " ");
            c = System.Text.RegularExpressions.Regex.Replace(c, "[\\s　]{2,}", " ");
            // 抹掉因挖掉时间而留下的悬空助词与重复标点（"…水印是 ，画面…" → "…水印，画面…"）
            c = System.Text.RegularExpressions.Regex.Replace(c, "(是|为|显示为|显示|水印是)\\s*[，,、]\\s*", "，");
            c = c.Replace(" ，", "，").Replace(" 。", "。").Replace("，，", "，").Replace("、、", "、");
            c = c.Trim().TrimStart('：', ':', '，', ',', '、', ' ', '　');
            return c;
        }

        private static double ProbeDuration(string path)
        {
            try
            {
                Dictionary<string, object> probe = Ffmpeg.ProbeMedia(path);
                if (probe != null && probe.ContainsKey("format"))
                {
                    Dictionary<string, object> fmt = probe["format"] as Dictionary<string, object>;
                    double? d = Ffmpeg.SafeF(MiniJson.GetStr(fmt, "duration"));
                    if (d.HasValue && d.Value > 0) return d.Value;
                }
            }
            catch (Exception) { }
            return 60;
        }

    }
}
