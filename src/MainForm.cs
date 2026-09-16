/* -*- coding: utf-8 -*-
 * MainForm.cs — 主窗体（文件/文件夹检测 + RTSP 实时检测 + AI 画面理解）
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace VideoChecker
{
    public class MainForm : Form
    {
        private FieldBox _pathBox;        // 圆角输入框（系统 TextBox 改不了圆角）
        private Button _browseBtn;
        private CheckBox _ckAudio;
        private CheckBox _ckVideo;
        private CheckBox _ckSync;
        private CheckBox _ckQuality;
        private CheckBox _ckMage;
        private CheckBox _ckCritical;    // 关键项保护：致命项 FAIL 一票否决（默认开）
        private Label _aiHintLab;        // AI 画面理解的配置提示（真实配置在「设置」页）
        private IconRail _rail;          // 左侧图标导航（卡片式界面）
        private FlowRow _ckRow;         // 检测内容流式行（用来反推最小窗口宽度）
        // 说明：主窗体原先自带一套「AI 配置」行和一行 RTSP 输入框，与设置页 / RTSP 页重复。
        // 现已收敛：AI 配置统一进「设置」页（AppSettings），RTSP 统一进「RTSP 实时检测」页面，
        // 主窗体只负责本地文件检测这一件事。
        private RadioButton _rbFull;
        private RadioButton _rbFast;
        private Button _startBtn;
        private Button _reportBtn;
        private Button _archiveBtn;    // 归档分类（按 通过/警告/失败 复制视频）
        private Button _pauseBtn;
        private Button _stopBtn;
        private FlatProgress _progress;   // 自绘圆角进度条（系统 ProgressBar 连颜色都改不了）
        private Label _pctLabel;
        private Label _statusLabel;
        private DataGridView _grid;

        // 暂停/停止控制
        private volatile bool _pauseRequested = false;
        private readonly System.Threading.ManualResetEvent _pauseGate = new System.Threading.ManualResetEvent(true);

        private List<MediaReport> _lastReports = new List<MediaReport>();
        private string _lastHtmlPath = "";
        private bool _aiAcquired;      // 本次检测是否占用了 AI 名额（结束时需释放）
        private BackgroundWorker _worker;
        private LogForm _logForm;
        private VideoSearchForm _searchForm;
        private RtspForm _rtspForm;
        private LiveAiForm _liveForm;
        private ComboBox _themeBox;

        /// <summary>次要说明文字（灰色）。主题切换时需要重新着色，故留下引用。</summary>
        private readonly List<Label> _mutedLabels = new List<Label>();

        public MainForm()
        {

            AppInfo.SetFormIcon(this);
            Text = AppInfo.Title + " - 图形版 " + AppInfo.Version;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;   // 界面全部代码布局，禁止自动缩放（它会在显示后二次移动控件，造成重绘残留/重影）
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(900, 640);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            BuildUi();
            ApplyTheme();
            Themes.Changed += OnThemeChanged;
            AppSettings.Changed += OnSettingsChanged;
            FormClosed += delegate(object s, FormClosedEventArgs e)
            {
                Themes.Changed -= OnThemeChanged;
                AppSettings.Changed -= OnSettingsChanged;
            };
        }

        /// <summary>设置变化 → 刷新本页的 AI 提示文字。</summary>
        private void OnSettingsChanged()
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke((MethodInvoker)OnSettingsChanged); return; }
            UpdateMageUi();

            // 最小窗口宽度按「检测内容」那一行的实际宽度反推。
            // 流式布局能保证控件之间不重叠，但保证不了"内容比窗口宽"——
            // 窗口缩得太小时整行会跑出卡片外，所以这里把下限顶到刚好放得下。
            if (_ckRow != null)
            {
                int needClient = 110 + _ckRow.Width + 56;
                if (needClient + 18 > MinimumSize.Width)
                    MinimumSize = new Size(needClient + 18, MinimumSize.Height);
                if (ClientSize.Width < needClient + 18)
                    ClientSize = new Size(needClient + 18, ClientSize.Height);
            }
        }

        /// <summary>
        /// 左侧导航的一项。
        /// ★ 为什么做成数组（2026-09-13 用户指出"第四个图标功能说明跑哪去了"）
        ///   原来 MainForm 里手写 7 行 _rail.Add(...) ✗
        ///   使用说明里又手写一份"第 1 个图标 → … 后 3 个图标 → …" ✗
        ///   **两处各写各的** ✓ 我加了「AI 实时巡检」这个图标 ✓
        ///   只改了导航 ✓ 忘了改说明 ✓ 于是说明里从第 3 个直接跳到"后 3 个" ✓✓
        ///   → 现在导航定义在这里一份 ✓ 界面和说明**都从这里读** ✓ 永远不会再漂移 ✓
        /// </summary>
        public class NavEntry
        {
            public string Key;        // 内部标识（OnRailSelected 用它分支）
            public string Title;      // 界面上显示的短标题
            public string HelpDesc;   // 使用说明里那句解释
            public Glyph Icon;
            public NavEntry(string key, string title, string helpDesc, Glyph icon)
            { Key = key; Title = title; HelpDesc = helpDesc; Icon = icon; }
        }

        /// <summary>左侧导航的定义（唯一来源：界面和使用说明都读它）。</summary>
        public static readonly NavEntry[] NavItems = new NavEntry[] {
            new NavEntry("file",     "本地视频检测",   "文件 / 文件夹批量检测",             Glyph.Video),
            new NavEntry("rtsp",     "RTSP 实时检测",  "监控摄像头拉流检测",                Glyph.Stream),
            new NavEntry("search",   "内容理解与文搜", "AI 读画面 + 关键词搜索",             Glyph.Search),
            new NavEntry("live",     "AI 实时巡检",    "摄像头有变化才分析，异常自动告警",    Glyph.Live),
            new NavEntry("log",      "日志",           "运行日志，排障时看",                Glyph.Log),
            new NavEntry("help",     "使用说明",       "打开本窗口",                        Glyph.Help),
            new NavEntry("settings", "设置",           "AI 配置 / 主题 / 密码 / 存储",       Glyph.Settings),
        };

        /// <summary>左侧导航栏点击：切换工作流页面或打开工具窗口。</summary>
        private void OnRailSelected(object sender, EventArgs e)
        {
            string key = _rail.SelectedKey;
            if (key == "rtsp") OnOpenRtspWin(null, EventArgs.Empty);
            else if (key == "live") OnOpenLiveWin(null, EventArgs.Empty);
            else if (key == "search") OnOpenSearchWin(null, EventArgs.Empty);
            else if (key == "settings") OnOpenSettings(null, EventArgs.Empty);
            else if (key == "log")
            {
                // 必须复用 OnOpenLog 的防御逻辑：窗口已可见时再调 Show(owner)
                // 会抛「已经可见的窗体不能显示为模式对话框」——
                // 之前这里漏了这个判断，连点两次「日志」就会崩（error.log 里有记录）。
                OnOpenLog(null, EventArgs.Empty);
            }
            else if (key == "help")
            {
                using (HelpForm hf = new HelpForm(false)) hf.ShowDialog(this);
            }
            else if (key == "file")
            {
                _statusLabel.Text = "当前已是「本地视频检测」";
            }
            // 这些入口进的是独立窗口，导航栏高亮回到本页
            if (key != "file") _rail.SelectedIndex = 0;
        }

        /// <summary>
        /// 未设置开屏密码时提示一次是否设置。
        /// 用标记文件记住"已经问过"，避免每次启动都弹 ——
        /// 用户想要时可以随时在「设置」页里补设。
        /// </summary>
        private void PromptSetPasswordOnce()
        {
            try
            {
                if (PassLock.IsEnabled) return;
                string mark = AppPaths.Data(".pwd_prompted");
                if (File.Exists(mark)) return;
                File.WriteAllText(mark, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    new System.Text.UTF8Encoding(false));

                DialogResult r = MessageBox.Show(this,
                    "当前未设置开屏密码，任何人在这台电脑上都能直接打开本程序。\n\n"
                    + "要现在设置一个吗？\n"
                    + "（随时可以在「设置」页里设置、修改或取消）",
                    "是否设置开屏密码", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                OnOpenSettings(null, EventArgs.Empty);
            }
            catch (Exception ex) { Log.Warn("提示设置密码失败：" + ex.Message); }
        }

        /// <summary>打开设置页（AI 模型 / 地址 / 抽帧参数 / 主题，三个页面共用一份）。</summary>'
        private void OnOpenSettings(object sender, EventArgs e)
        {
            using (SettingsForm sf = new SettingsForm())
                sf.ShowDialog(this);
            UpdateMageUi();

            // 最小窗口宽度按「检测内容」那一行的实际宽度反推。
            // 流式布局能保证控件之间不重叠，但保证不了"内容比窗口宽"——
            // 窗口缩得太小时整行会跑出卡片外，所以这里把下限顶到刚好放得下。
            if (_ckRow != null)
            {
                int needClient = 110 + _ckRow.Width + 56;
                if (needClient + 18 > MinimumSize.Width)
                    MinimumSize = new Size(needClient + 18, MinimumSize.Height);
                if (ClientSize.Width < needClient + 18)
                    ClientSize = new Size(needClient + 18, ClientSize.Height);
            }
        }

        /// <summary>登记一个次要说明文字（跟随主题的柔和文字色）。</summary>
        private Label Muted(Label l)
        {
            _mutedLabels.Add(l);
            l.ForeColor = Themes.Current.Muted;
            return l;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 首次启动弹出使用说明（勾选"下次不再显示"后写标志文件）
            try
            {
                string flag = AppPaths.Data(".help_shown");
                if (!File.Exists(flag))
                {
                    using (HelpForm hf = new HelpForm(true)) hf.ShowDialog(this);
                }
            }
            catch (Exception) { }
        }

        private void BuildUi()
        {
            Font uiFont = new Font("Microsoft YaHei", 9F);
            Font smallFont = new Font("Microsoft YaHei", 8.5F);
            int W = ClientSize.Width;
            int H = ClientSize.Height;

            // ==================== 左侧图标导航 ====================
            _rail = new IconRail();
            _rail.Location = new Point(10, 10);
            _rail.Size = new Size(62, H - 20);
            _rail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            // ★ 从 NavItems 数组生成（唯一来源 ✓ 加了新图标这里自动就有 ✓）
            for (int i = 0; i < NavItems.Length; i++)
                _rail.Add(NavItems[i].Key, NavItems[i].Title, NavItems[i].Icon);
            _rail.Selected += OnRailSelected;
            Controls.Add(_rail);

            int X = 88;                  // 内容区左边界
            int CW = W - X - 14;         // 内容区宽度

            // ==================== 顶栏 ====================
            Label tl = new Label();
            tl.Text = "视频核对工具";
            tl.Font = new Font("Microsoft YaHei", 14F, FontStyle.Bold);
            tl.AutoSize = true;
            tl.Location = new Point(X, 16);
            Controls.Add(tl);

            Label sl = new Label();
            sl.Text = "音频完整性 · 视频完整性 · 音画同步 · 音频质量 · AI 画面理解";
            sl.Font = smallFont;
            Muted(sl);
            sl.AutoSize = true;
            sl.Location = new Point(X + 2, 46);
            Controls.Add(sl);

            Label thLab = new Label();
            thLab.Text = "主题";
            thLab.Font = smallFont;
            Muted(thLab);
            thLab.AutoSize = true;
            thLab.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            thLab.Location = new Point(W - 214, 26);
            Controls.Add(thLab);

            _themeBox = new ComboBox();
            _themeBox.Font = smallFont;
            _themeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _themeBox.Location = new Point(W - 180, 22);
            _themeBox.Size = new Size(166, 24);
            _themeBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            for (int i = 0; i < Themes.All.Length; i++) _themeBox.Items.Add(Themes.All[i].Name);
            _themeBox.SelectedIndex = IndexOfTheme(Themes.Current.Key);
            _themeBox.SelectedIndexChanged += OnThemePicked;   // 先设好初值再挂事件，避免误触发
            Controls.Add(_themeBox);
            _themeBox.BringToFront();

            // ==================== 卡片一：本地视频检测 ====================
            CardPanel work = new CardPanel();
            work.Location = new Point(X, 78);
            work.Size = new Size(CW, 330);
            work.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            work.SetTitle("本地视频检测", "支持文件 / 文件夹，也可直接拖拽进来");
            Controls.Add(work);

            Label pathLab = new Label();
            pathLab.Text = "待检视频";
            pathLab.Font = uiFont;
            pathLab.AutoSize = true;
            pathLab.Location = new Point(20, 76);
            work.Controls.Add(pathLab);

            _pathBox = new FieldBox(34);
            _pathBox.Location = new Point(110, 66);
            _pathBox.Size = new Size(CW - 264, 34);
            _pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            work.Controls.Add(_pathBox);

            _browseBtn = new RoundButton();
            _browseBtn.Text = "浏览...";
            _browseBtn.Font = uiFont;
            _browseBtn.Location = new Point(CW - 142, 66);
            _browseBtn.Size = new Size(120, 34);
            _browseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseBtn.Click += OnBrowse;
            work.Controls.Add(_browseBtn);

            Label ckLab = new Label();
            ckLab.Text = "检测内容";
            ckLab.Font = uiFont;
            ckLab.AutoSize = true;
            ckLab.Location = new Point(20, 118);
            work.Controls.Add(ckLab);

            _ckAudio = NewCk("音频完整性", 110, 116, true, work);
            _ckVideo = NewCk("视频完整性", 262, 116, true, work);
            _ckSync = NewCk("音画同步", 414, 116, true, work);
            _ckQuality = NewCk("音频质量（电流麦/底噪/爆音）", 110, 144, false, work);
            _ckMage = NewCk("AI 画面理解", 414, 144, false, work);
            _ckMage.CheckedChanged += OnMageToggled;

            // 检测内容：打包进流式容器，由布局引擎依次排列。
            // 之前是手算 x 坐标，字体/文字长度一变就会重叠 —— 流式布局结构上不会。
            _ckRow = Flow.Row(work, 110, 112, 22, new Control[] { _ckAudio, _ckVideo, _ckSync, _ckQuality, _ckMage });

            _aiHintLab = new Label();
            _aiHintLab.Font = smallFont;
            Muted(_aiHintLab);
            _aiHintLab.AutoSize = true;
            _aiHintLab.Location = new Point(110, 141);
            work.Controls.Add(_aiHintLab);

            Label modeLab = new Label();
            modeLab.Text = "检测模式";
            modeLab.Font = uiFont;
            modeLab.AutoSize = true;
            modeLab.Location = new Point(20, 178);
            work.Controls.Add(modeLab);

            _rbFull = new RadioButton();
            _rbFull.Text = "全片分析（最准确，长视频较慢）";
            _rbFull.Checked = true;
            _rbFull.Font = uiFont;
            _rbFull.AutoSize = true;
            _rbFull.Location = new Point(110, 175);
            work.Controls.Add(_rbFull);

            _rbFast = new RadioButton();
            _rbFast.Text = "快速（前 60 秒采样）";
            _rbFast.Font = uiFont;
            _rbFast.AutoSize = true;
            _rbFast.Location = new Point(372, 175);
            work.Controls.Add(_rbFast);
            Flow.Row(work, 110, 166, 28, new Control[] { _rbFull, _rbFast });

            Label judgeLab = new Label();
            judgeLab.Text = "判定方式";
            judgeLab.Font = uiFont;
            judgeLab.AutoSize = true;
            judgeLab.Location = new Point(20, 208);
            work.Controls.Add(judgeLab);

            _ckCritical = NewCk("关键项保护（黑屏/静音/花屏等致命项一票否决）", 110, 194, true, work);

            // 按钮行
            _startBtn = new RoundButton();
            _startBtn.Text = "开始检测";
            _startBtn.Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold);
            _startBtn.Location = new Point(110, 240);
            _startBtn.Size = new Size(116, 36);
            _startBtn.Click += OnStart;
            Themes.StylePrimary(_startBtn);
            work.Controls.Add(_startBtn);

            _pauseBtn = new RoundButton();
            _pauseBtn.Text = "暂停";
            _pauseBtn.Font = uiFont;
            _pauseBtn.Location = new Point(234, 240);
            _pauseBtn.Size = new Size(80, 36);
            _pauseBtn.Enabled = false;
            _pauseBtn.Click += OnPause;
            work.Controls.Add(_pauseBtn);

            _stopBtn = new RoundButton();
            _stopBtn.Text = "停止";
            _stopBtn.Font = uiFont;
            _stopBtn.Location = new Point(322, 240);
            _stopBtn.Size = new Size(80, 36);
            _stopBtn.Enabled = false;
            _stopBtn.Click += OnStop;
            work.Controls.Add(_stopBtn);

            _reportBtn = new RoundButton();
            _reportBtn.Text = "打开报告";
            _reportBtn.Font = uiFont;
            _reportBtn.Location = new Point(410, 240);
            _reportBtn.Size = new Size(100, 36);
            _reportBtn.Enabled = false;
            _reportBtn.Click += OnOpenReport;
            work.Controls.Add(_reportBtn);

            _archiveBtn = new RoundButton();
            _archiveBtn.Text = "归档分类";
            _archiveBtn.Font = uiFont;
            _archiveBtn.Location = new Point(518, 240);
            _archiveBtn.Size = new Size(100, 36);
            _archiveBtn.Enabled = false;
            _archiveBtn.Click += OnArchive;
            work.Controls.Add(_archiveBtn);
            Flow.Row(work, 110, 228, 12,
                new Control[] { _startBtn, _pauseBtn, _stopBtn, _reportBtn, _archiveBtn });

            _statusLabel = new Label();
            _statusLabel.Text = "就绪";
            _statusLabel.Font = smallFont;
            Muted(_statusLabel);
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _statusLabel.Location = new Point(110, 268);
            _statusLabel.Size = new Size(CW - 132, 20);
            work.Controls.Add(_statusLabel);

            // 进度（自绘圆角条 —— 系统 ProgressBar 连颜色都改不了）
            _progress = new FlatProgress();
            _progress.Location = new Point(110, 296);
            _progress.Size = new Size(CW - 250, 14);
            _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _progress.ShowText = false;      // 百分比由右侧 Label 显示，避免重复
            work.Controls.Add(_progress);

            _pctLabel = new Label();
            _pctLabel.Text = "0%";
            _pctLabel.Font = smallFont;
            _pctLabel.AutoSize = true;
            _pctLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _pctLabel.Location = new Point(CW - 126, 294);
            work.Controls.Add(_pctLabel);

            // ==================== 卡片二：检测结果 ====================
            CardPanel res = new CardPanel();
            res.Location = new Point(X, 418);
            res.Size = new Size(CW, H - 432);
            res.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            res.SetTitle("检测结果", "双击某行可查看该文件的 AI 理解内容");
            Controls.Add(res);

            _grid = new DataGridView();
            _grid.Location = new Point(16, 60);
            _grid.Size = new Size(CW - 32, H - 432 - 76);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 28;
            _grid.CellDoubleClick += OnCellDoubleClick;
            _grid.DataBindingComplete += (s, e) => ColorizeGrid();
            res.Controls.Add(_grid);

            _grid.Columns.Add("colStatus", "总体");
            _grid.Columns.Add("colFile", "文件名 / 流地址");
            _grid.Columns.Add("colMedia", "媒体信息");
            _grid.Columns.Add("colDetail", "关键结论");
            _grid.Columns[0].Width = 60;
            _grid.Columns[1].Width = 250;
            _grid.Columns[2].Width = 230;
            _grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            _worker = new BackgroundWorker();
            _worker.WorkerReportsProgress = true;
            _worker.WorkerSupportsCancellation = true;
            _worker.DoWork += OnDoWork;
            _worker.ProgressChanged += OnProgress;
            _worker.RunWorkerCompleted += OnCompleted;

            // 首次进入且未设置开屏密码时，提示一次是否设置
            Shown += delegate(object s2, EventArgs e2) { PromptSetPasswordOnce(); };

            UpdateMageUi();

            // 最小窗口宽度按「检测内容」那一行的实际宽度反推。
            // 流式布局能保证控件之间不重叠，但保证不了"内容比窗口宽"——
            // 窗口缩得太小时整行会跑出卡片外，所以这里把下限顶到刚好放得下。
            if (_ckRow != null)
            {
                int needClient = 110 + _ckRow.Width + 56;
                if (needClient + 18 > MinimumSize.Width)
                    MinimumSize = new Size(needClient + 18, MinimumSize.Height);
                if (ClientSize.Width < needClient + 18)
                    ClientSize = new Size(needClient + 18, ClientSize.Height);
            }
        }

        private CheckBox NewCk(string text, int x, int y, bool checkedState)
        {
            return NewCk(text, x, y, checkedState, null);
        }

        /// <summary>建一个圆角勾选框。parent 为 null 时挂到窗体；卡片内的控件必须挂卡片，坐标才是相对卡片的。</summary>
        private CheckBox NewCk(string text, int x, int y, bool checkedState, Control parent)
        {
            // 用系统原生 CheckBox：全部由系统渲染，杜绝自绘控件的文字串位问题
            // （自绘版本实测出现"每个勾选框左边多一个字、下方串进别的行的字"）
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = checkedState;
            c.Font = new Font("Microsoft YaHei", 9F);
            c.AutoSize = true;
            // FlatStyle.Flat 在深色底上画的是"浅色方框+浅色勾"，对比度极低，
            // 用户根本看不出勾没勾上（会以为点不动）。Standard 走系统视觉样式，
            // 白底深色勾，在任何背景上都清晰可辨。
            c.FlatStyle = FlatStyle.Standard;
            c.Location = new Point(x, y);
            (parent != null ? parent : this).Controls.Add(c);
            return c;
        }

        /// <summary>AI 勾选状态 → 提示文字。真实配置在「设置」页，这里只做引导。</summary>
        private void UpdateMageUi()
        {
            if (_aiHintLab == null) return;
            _aiHintLab.Text = _ckMage.Checked
                ? ("将调用 " + AppSettings.OllamaModel + "（模型/地址/帧数在左侧导航栏的「设置」里改）")
                : "勾选后才会调用 AI；模型与抽帧参数在左侧导航栏的「设置」里配置";
        }

        /// <summary>打开"AI 内容理解与文搜"窗口（独立功能：播放 + 内容时间轴 + 文搜）。</summary>
        /// <summary>打开 RTSP 实时取流分析页（单例，重复点击只激活已有窗口）。</summary>
        private void OnOpenRtspWin(object sender, EventArgs e)
        {
            if (_rtspForm != null && !_rtspForm.IsDisposed)
            {
                _rtspForm.Activate();
                return;
            }
            // RTSP 页面自己维护持久化的流列表，这里不需要带种子
            _rtspForm = new RtspForm();
            _rtspForm.Show(this);
            Log.Info("打开 RTSP 实时取流分析页");
        }

        /// <summary>AI 实时巡检页（单实例：已经开着就激活，不再开第二个）。</summary>
        private void OnOpenLiveWin(object sender, EventArgs e)
        {
            try
            {
                if (_liveForm == null || _liveForm.IsDisposed)
                    _liveForm = new LiveAiForm();
                if (_liveForm.Visible) _liveForm.Activate();
                else _liveForm.Show(this);
                Log.Info("打开 AI 实时巡检页");
            }
            catch (Exception ex)
            {
                Log.Error("打开 AI 实时巡检页失败：" + ex);
                MessageBox.Show(this, "打开 AI 实时巡检页失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenSearchWin(object sender, EventArgs e)
        {
            try
            {
                if (_searchForm == null || _searchForm.IsDisposed)
                    _searchForm = new VideoSearchForm();
                if (_searchForm.Visible)
                {
                    _searchForm.Activate();
                    return;
                }
                _searchForm.Show(this);
                _searchForm.BringToFront();
                Log.Info("打开「AI 内容理解与文搜」窗口");
            }
            catch (Exception ex)
            {
                Log.Error("打开内容理解窗口失败：" + ex.Message);
                MessageBox.Show(this, "打开内容理解窗口失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>打开日志窗口（左侧导航栏「日志」图标；防御：已可见则激活，异常则重建一次）。</summary>
        private void OnOpenLog(object sender, EventArgs e)
        {            try
            {
                if (_logForm == null || _logForm.IsDisposed)
                    _logForm = new LogForm();
                // ★ 每次打开都把级别重置成「常规」（2026-09-13 用户指出）
                //   用户：「我发现运行日志打开默认用的是 debug，而不是用户 info 级别」
                //   根因：日志窗口是**复用同一个实例**的 ✓
                //     代码里 `_levelBox.SelectedIndex = 0` 只在**构造时生效一次** ✗
                //     用户切到 Debug 之后关掉 ✓ 实例还在 ✓ 再打开还是 Debug ✓
                //     → 满屏 Ollama 请求/返回，真正的 WARN 全被淹掉了 ✗
                //   现在每次打开前重置 ✓ 想临时看 Debug 当场切 ✓ 不影响下次 ✓
                _logForm.ResetLevelToNormal();
                if (_logForm.Visible)
                {
                    _logForm.Activate();
                    return;
                }
                _logForm.Show(this);
                _logForm.BringToFront();
            }
            catch (Exception ex)
            {
                Log.Debug("打开日志窗口失败(重试): " + ex.Message);
                try
                {
                    _logForm = new LogForm();
                    _logForm.Show(this);
                }
                catch (Exception ex2)
                {
                    Log.Debug("打开日志窗口再次失败: " + ex2.Message);
                    MessageBox.Show(this, "打开日志窗口失败：" + ex2.Message + "\n详情见 exe 目录 logs\\error.log",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OnMageToggled(object sender, EventArgs e)
        {
            UpdateMageUi();

            // 最小窗口宽度按「检测内容」那一行的实际宽度反推。
            // 流式布局能保证控件之间不重叠，但保证不了"内容比窗口宽"——
            // 窗口缩得太小时整行会跑出卡片外，所以这里把下限顶到刚好放得下。
            if (_ckRow != null)
            {
                int needClient = 110 + _ckRow.Width + 56;
                if (needClient + 18 > MinimumSize.Width)
                    MinimumSize = new Size(needClient + 18, MinimumSize.Height);
                if (ClientSize.Width < needClient + 18)
                    ClientSize = new Size(needClient + 18, ClientSize.Height);
            }
        }

        /// <summary>选中"自定义(输入时间点)"时弹出时间点输入框。</summary>

        private static string FmtTimes(List<double> ts)
        {
            if (ts == null || ts.Count == 0) return "（未设置）";
            List<string> parts = new List<string>();
            foreach (double t in ts) parts.Add(t.ToString("0.#") + "s");
            return string.Join(", ", parts.ToArray());
        }

        private static List<double> ParseTimes(string text)
        {
            List<double> res = new List<double>();
            if (string.IsNullOrWhiteSpace(text)) return res;
            string[] parts = text.Split(new char[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                double t;
                if (double.TryParse(p, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out t))
                {
                    if (t >= 0 && !res.Contains(t)) res.Add(t);
                }
            }
            res.Sort();
            if (res.Count > 20) res = res.GetRange(0, 20);
            return res;
        }

        // ---------- 交互 ----------
        private void OnBrowse(object sender, EventArgs e)
        {
            DialogResult dlg = MessageBox.Show("选择类型：\n\n是 = 选择文件夹（批量检测）\n否 = 选择单个视频文件", "选择检测对象",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (dlg == DialogResult.Yes)
            {
                FolderBrowserDialog fb = new FolderBrowserDialog();
                fb.Description = "选择要检测的视频文件夹";
                if (fb.ShowDialog() == DialogResult.OK) _pathBox.Text = fb.SelectedPath;
            }
            else if (dlg == DialogResult.No)
            {
                OpenFileDialog of = new OpenFileDialog();
                of.Title = "选择要检测的视频文件";
                of.Filter = "视频/音频文件|*.mp4;*.mov;*.mkv;*.avi;*.flv;*.ts;*.m2ts;*.mts;*.webm;*.wmv;*.mpg;*.mpeg;*.m4v;*.3gp;*.m4a;*.mp3;*.wav;*.flac;*.aac|所有文件|*.*";
                if (of.ShowDialog() == DialogResult.OK) _pathBox.Text = of.FileName;
            }
        }


        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) _pathBox.Text = files[0];
        }

        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _lastReports.Count) return;
            ShowDetail(_lastReports[e.RowIndex]);
        }

        private void ShowDetail(MediaReport rep)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("文件：" + rep.File);
            sb.AppendLine("总体：" + rep.Overall);
            sb.AppendLine("----------------------------------------");
            if (rep.Error.Length > 0)
            {
                sb.AppendLine("[错误] " + rep.Error);
            }
            else
            {
                object v;
                if (rep.MediaInfo.TryGetValue("container", out v)) sb.AppendLine("容器: " + v);
                if (rep.MediaInfo.TryGetValue("duration", out v) && v != null) sb.AppendLine("时长: " + Engine.FmtSec(Ffmpeg.SafeF(v)));
                if (rep.MediaInfo.TryGetValue("size", out v) && v != null) sb.AppendLine("大小: " + Engine.FmtSize(Ffmpeg.SafeF(v)));
                sb.AppendLine("----------------------------------------");
                foreach (CheckItem it in rep.Items)
                    sb.AppendLine("[" + it.Status + "] " + it.Name + "：" + it.Detail);
            }
            MessageBox.Show(sb.ToString(), "检测明细 - " + Path.GetFileName(rep.File), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- 检测流程 ----------
        private void OnStart(object sender, EventArgs e)
        {
            string path = _pathBox.Text.Trim();
            if (path.Length == 0)
            {
                MessageBox.Show("请先填写待检视频路径（文件或文件夹）", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 本页只做本地文件检测；网络流归「RTSP 实时检测」页面，避免两处行为不一致
            if (Ffmpeg.IsStreamUrl(path))
            {
                MessageBox.Show(this,
                    "检测到这是网络流地址（rtsp:// 等）。\n\n"
                    + "实时流请在「RTSP 实时检测」页面里做 —— 那里支持多路轮巡、拉流业务检测与画面预览。",
                    "请使用 RTSP 页面", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // ★★ 开跑之前先确认 ffmpeg **真的跑得起来**（2026-09-14 加）
            //
            //   用户在公司的 Win7 上实测：**每个文件 0.1 秒就报"无法分析"** ✗
            //   点了 6 次、换 3 个目录、换 2 个盘，全是 0.1 秒 ERROR ✓
            //   ★ 根因是那台机器缺「通用 C 运行库」(UCRT) ✓
            //     随程序带的 ffmpeg.exe **起不来** ✓ 而文件明明在 ✓
            //     原来的提示还写着「请确认程序目录下有 ffmpeg\bin\ 文件夹」✗ 越查越远 ✓
            //
            //   ★ 所以：**在开始之前就问一次** ✓
            //     真的把 ffmpeg 跑一遍（不是看文件在不在 ✗）
            //     跑不起来就把原因**当场**说清楚 ✓ 而不是让用户对着 20 行
            //     "ERROR（耗时 0.1s）"自己去猜哪一步卡了 ✓
            string pre = Ffmpeg.PreflightCheck();
            if (pre.Length > 0)
            {
                Log.Error("ffmpeg 自检未通过：" + pre.Replace("\n", " "));
                MessageBox.Show(this,
                    "视频检测要用到的 ffmpeg **起不来**，所以现在跑只会得到"
                    + "「无法分析」。\n\n" + pre,
                    "ffmpeg 起不来", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            CheckOptions opts = new CheckOptions();
            opts.CheckAudio = _ckAudio.Checked;
            opts.CheckVideo = _ckVideo.Checked;
            opts.CheckSync = _ckSync.Checked;
            opts.CheckQuality = _ckQuality.Checked;
            opts.CheckMage = _ckMage.Checked;
            opts.FastMode = _rbFast.Checked;
            // AI 参数统一取自「设置」页（AppSettings）—— 原先本页自己维护一份，与文搜页、RTSP 页三处重复
            opts.Mage.Url = AppSettings.OllamaUrl;
            opts.Mage.Model = AppSettings.OllamaModel;
            opts.Mage.Frames = AppSettings.MageFrames;
            opts.Mage.Strategy = AppSettings.MageStrategy;
            opts.Mage.CustomTimes = new List<double>(AppSettings.CustomTimes);

            if (!opts.CheckAudio && !opts.CheckVideo && !opts.CheckSync && !opts.CheckQuality && !opts.CheckMage)
            {
                MessageBox.Show("请至少勾选一项检测内容", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 关键项保护开关：致命项 FAIL 一票否决（默认开启）
            MediaReport.CriticalVeto = _ckCritical.Checked;

            // AI 调用全局互斥：与「内容理解/文搜」「RTSP 检测」共用同一台 Ollama，只允许一个任务在跑
            if (opts.CheckMage)
            {
                string holder;
                if (!MageCheck.TryAcquire("视频核对·AI 画面理解", out holder))
                {
                    MessageBox.Show(this,
                        "已有一个 AI 任务在运行（" + (holder.Length > 0 ? holder : "其它窗口") + "）。\n\n"
                        + "两个任务同时调用本机模型会把显存和推理队列挤满，两边都会变得极慢甚至超时。\n"
                        + "请等它结束，或先在那边点「停止」再回来重试。",
                        "AI 正忙", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _aiAcquired = true;
            }
            Log.Info("关键项保护：" + (_ckCritical.Checked ? "开启（黑屏/静音/花屏等致命项一票否决）" : "关闭（纯及格率制）"));

            _pauseRequested = false;
            _pauseGate.Set();
            Log.Info("开始检测：文件/文件夹 " + path + "，模式=" + (_rbFast.Checked ? "快速(前60秒)" : "全片")
                + (opts.CheckMage ? "，AI 开启（" + opts.Mage.Model + "）" : ""));

            List<string> files = Engine.CollectFiles(path);
            if (files.Count == 0)
            {
                MessageBox.Show("所选路径下没有找到可检测的视频/音频文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _grid.Rows.Clear();
            _startBtn.Enabled = false;
            _reportBtn.Enabled = false;
            if (_archiveBtn != null) _archiveBtn.Enabled = false;
            _pauseBtn.Enabled = true;
            _pauseBtn.Text = "暂停";
            _stopBtn.Enabled = true;
            _progress.Value = 0;
            _pctLabel.Text = "0%";
            _statusLabel.Text = "准备检测 " + files.Count + " 个文件...";
            _worker.RunWorkerAsync(new object[] { files, opts, null, 0.0 });
        }

        /// <summary>暂停 / 继续（AI 帧间与文件间生效）。</summary>
        private void OnPause(object sender, EventArgs e)
        {
            if (_pauseRequested)
            {
                _pauseRequested = false;
                _pauseGate.Set();
                _pauseBtn.Text = "暂停";
                _statusLabel.Text = "已继续";
            }
            else
            {
                _pauseRequested = true;
                _pauseGate.Set();      // 先放行当前已挂起的等待，再挂起下一轮
                _pauseGate.Reset();
                _pauseBtn.Text = "继续";
                _statusLabel.Text = "已暂停（当前一步完成后生效，点「继续」恢复）";
            }
        }

        /// <summary>停止检测（中断 ffmpeg 进程 + 取消剩余任务）。</summary>
        private void OnStop(object sender, EventArgs e)
        {
            _pauseGate.Set();          // 释放可能挂起的暂停等待
            _worker.CancelAsync();
            Ffmpeg.KillRunning();      // 强制结束正在运行的 ffmpeg（拉流/分析/抽帧）
            _stopBtn.Enabled = false;
            _statusLabel.Text = "正在停止...";
        }

        /// <summary>
        /// 正在跑的这一批的结果（**边跑边往里加**）✓
        ///
        /// ★ 为什么需要它（2026-09-16）：
        ///   取消时**不能**去读 `e.Result` ✗（一读就抛 InvalidOperationException ✓）
        ///   而用户点停止时，已经完成的那几个文件的结果是要保留的 ✓
        ///   → 所以让 OnDoWork 把同一个 List 对象挂在这里 ✓
        ///     OnCompleted 在取消分支里读它 ✓ 既拿到部分结果、又不碰 e.Result ✓
        /// </summary>
        private List<MediaReport> _partialReports;

        private void OnDoWork(object sender, DoWorkEventArgs e)
        {
            object[] args = e.Argument as object[];
            List<string> files = args[0] as List<string>;
            CheckOptions opts = args[1] as CheckOptions;
            string rtspUrl = args[2] as string;
            double rtspSec = (double)args[3];

            List<MediaReport> reports = new List<MediaReport>();
            _partialReports = reports;          // ★ 取消时 OnCompleted 从这里取部分结果 ✓
            if (rtspUrl != null)
            {
                _worker.ReportProgress(10, new object[] { 1, 1, "RTSP 流" });
                reports.Add(Engine.CheckRtsp(rtspUrl, rtspSec, opts));
            }
            else
            {
                for (int i = 0; i < files.Count; i++)
                {
                    _pauseGate.WaitOne();                     // 暂停门（文件间）
                    if (_worker.CancellationPending)
                    {
                        e.Cancel = true;                      // 用户点了停止
                        break;
                    }
                    string f = files[i];
                    Log.Info("处理文件 [" + (i + 1) + "/" + files.Count + "] " + Path.GetFileName(f));
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    _worker.ReportProgress((int)((double)i / files.Count * 100), new object[] { i + 1, files.Count, Path.GetFileName(f) });
                    int idx = i;
                    try
                    {
                        MediaReport rep = Engine.CheckOneFile(f, opts,
                            (cur, total) =>
                            {
                                _pauseGate.WaitOne();             // 暂停门（AI 帧间）
                                double fp = (double)cur / total;
                                int pct = (int)(((double)idx + fp) / files.Count * 100);
                                if (pct > 100) pct = 100;
                                _worker.ReportProgress(pct, new object[] { idx + 1, files.Count, Path.GetFileName(f) + "（AI 抽帧分析 " + cur + "/" + total + "）" });
                            },
                            () => _worker.CancellationPending,
                            (stage) => _worker.ReportProgress(_progress.Value, new object[] { idx + 1, files.Count, Path.GetFileName(f) + "：" + stage }));
                        sw.Stop();
                        Log.Info("文件完成 [" + (i + 1) + "/" + files.Count + "] " + Path.GetFileName(f) + " → " + rep.Overall
                            + "（耗时 " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s）");
                        reports.Add(rep);
                    }
                    catch (MageCheck.StoppedException)
                    {
                        // ★★★ 用户**中途**点了停止 —— 这是**正常停止**，不是错误 ✗
                        //
                        //  以前这里没有接住 ✗ → StoppedException 冒到 BackgroundWorker
                        //  → 被塞进 `e.Error` ✗ → OnCompleted 弹出
                        //     「检测过程中发生错误：用户已停止」✗
                        //
                        //  ★ 用户那些文件**一个要 20 多分钟**（实测 1289~1434 秒 ✓）
                        //    → 他几乎不可能在"文件与文件之间"那一瞬间按停止 ✗
                        //      必然是在文件中途按 ✓ → 所以这条其实比上面那条更常发生 ✓
                        e.Cancel = true;
                        break;
                    }
                }
            }
            e.Result = reports;
        }

        private void OnProgress(object sender, ProgressChangedEventArgs e)
        {
            _progress.Value = e.ProgressPercentage;
            _pctLabel.Text = e.ProgressPercentage + "%";
            object[] info = e.UserState as object[];
            if (info != null)
                _statusLabel.Text = "正在检测 [" + info[0] + "/" + info[1] + "] " + info[2] + " ...";
        }

        private void OnCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (_aiAcquired) { MageCheck.Release("视频核对·AI 画面理解"); _aiAcquired = false; }
            _startBtn.Enabled = true;
            _pauseBtn.Enabled = false;
            _pauseBtn.Text = "暂停";
            _stopBtn.Enabled = false;
            _pauseRequested = false;
            _pauseGate.Set();

            if (e.Error != null)
            {
                _statusLabel.Text = "检测出错：" + e.Error.Message;
                MessageBox.Show("检测过程中发生错误：" + e.Error.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ★★★ 顺序：**先判 e.Cancelled，再碰 e.Result**（2026-09-16 修）
            //
            //  原来这里是反的 ✗：
            //      List<MediaReport> reports = e.Result as List<MediaReport>;   ← 先读 ✗
            //      if (e.Cancelled) { ... }
            //  而 AsyncCompletedEventArgs 在 Cancelled=true 时读 Result **必抛**
            //      InvalidOperationException: Operation has been cancelled ✗
            //  → **每次点「停止」都会崩一次** ✗ →
            //    全局兜底弹「程序遇到问题已记录」+ 写 logs\error.log ✗
            //
            //  用户 2026-09-16 在**公司的 Win7** 上跑文件夹检测时点的停止 ✓
            //  error.log 里就是这一条（10:42:25，和"文件完成 [4/15]"同一秒 ✓）
            //
            //  ★ 取消时的部分结果改从 _partialReports 拿 ✓ 不再碰 e.Result ✓
            if (e.Cancelled)
            {
                List<MediaReport> partial = _partialReports;
                if (partial == null) partial = new List<MediaReport>();
                _lastReports = partial;
                Log.Info("检测已停止（完成 " + partial.Count + " 个文件，部分结果已保留）");
                _progress.Value = 100;
                _pctLabel.Text = "已停止";
                _statusLabel.Text = "已停止：完成 " + partial.Count + " 个文件（部分结果已保留）";
                if (partial.Count == 0)
                {
                    _grid.Rows.Clear();
                    return;
                }
                FillGrid(partial);
                GenerateReport(partial);
                // ★ GenerateReport 会把状态栏改成「报告已生成：xxx.html」✗（2026-09-16 修）
                //   而用户刚点了「停止」✓ 状态栏第一眼应该说**已停止** ✓
                //   （报告确实也生成了 ✓ 就写在同一行里 ✓ 不丢信息 ✓）
                _statusLabel.Text = "已停止：完成 " + partial.Count + " 个文件（报告已生成）";
                return;
            }

            List<MediaReport> reports = e.Result as List<MediaReport>;
            if (reports == null) reports = new List<MediaReport>();
            _lastReports = reports;

            _progress.Value = 100;
            _pctLabel.Text = "100%";
            FillGrid(reports);
            GenerateReport(reports);
            if (_archiveBtn != null) _archiveBtn.Enabled = true;   // 检测完成即可归档分类

            int nPass = 0, nWarn = 0, nFail = 0, nErr = 0;
            foreach (MediaReport r in reports)
            {
                if (r.Overall == "PASS") nPass++;
                else if (r.Overall == "WARN") nWarn++;
                else if (r.Overall == "FAIL") nFail++;
                else nErr++;
            }
            _statusLabel.Text = "完成：共 " + reports.Count + " 个 | 通过 " + nPass + " | 警告 " + nWarn + " | 失败 " + nFail
                + (nErr > 0 ? " | 无法分析 " + nErr : "") + " | 报告：" + (_lastHtmlPath.Length > 0 ? _lastHtmlPath : "生成中");
            Log.Info("检测完成：共 " + reports.Count + " 个 | PASS " + nPass + " | WARN " + nWarn + " | FAIL " + nFail + " | 无法分析 " + nErr);
        }

        private void FillGrid(List<MediaReport> reports)
        {
            _grid.Rows.Clear();
            foreach (MediaReport rep in reports)
            {
                string media = "容器?";
                object v;
                if (rep.MediaInfo.TryGetValue("container", out v)) media = "容器:" + v;
                if (rep.MediaInfo.TryGetValue("duration", out v) && v != null) media += " 时长:" + Engine.FmtSec(Ffmpeg.SafeF(v));
                if (rep.MediaInfo.TryGetValue("size", out v) && v != null) media += " " + Engine.FmtSize(Ffmpeg.SafeF(v));
                if (rep.MediaInfo.ContainsKey("video"))
                {
                    Dictionary<string, object> vi = rep.MediaInfo["video"] as Dictionary<string, object>;
                    if (vi != null) media += " | " + Str(vi, "codec") + " " + Str(vi, "width") + "x" + Str(vi, "height");
                }
                string detail = "";
                if (rep.Error.Length > 0) detail = "无法分析：" + rep.Error;
                else if (rep.Items.Count > 0) detail = rep.Items[rep.Items.Count - 1].Name + "：" + rep.Items[rep.Items.Count - 1].Detail;
                _grid.Rows.Add(rep.Overall, rep.File.StartsWith("rtsp://") ? rep.File : Path.GetFileName(rep.File), media, detail);
            }
            ColorizeGrid();
        }

        private void GenerateReport(List<MediaReport> reports)
        {
            string modeDesc = _rbFast.Checked ? "前 60 秒采样" : "全片分析";
            string reportDir = ReportHtml.DefaultReportDir();
            try
            {
                Directory.CreateDirectory(reportDir);
                string htmlPath = ReportHtml.StampedPath(reportDir, "video_check_report", ".html");
                ReportHtml.Export(reports, htmlPath, modeDesc);
                string pdfPath = PdfExport.ExportPdf(reports, Path.ChangeExtension(htmlPath, ".pdf"), modeDesc);
                _lastHtmlPath = htmlPath;
                _reportBtn.Enabled = true;
                Log.Info("报告已生成（不覆盖旧报告）：" + Path.GetFileName(htmlPath) + " + " + Path.GetFileName(pdfPath));
                _statusLabel.Text = "报告已生成：" + Path.GetFileName(htmlPath) + "（PDF 同目录）";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "报告生成失败：" + ex.Message;
                Log.Debug("报告生成失败: " + ex);
            }
        }

        /// <summary>归档分类：按总评把已检测的视频文件复制到 archive\通过|警告|失败|无法分析 文件夹。</summary>
        private void OnArchive(object sender, EventArgs e)
        {
            if (_lastReports.Count == 0)
            {
                MessageBox.Show(this, "还没有检测结果：先完成一次检测再归档。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "archive");
            int copied = 0, skipped = 0;
            try
            {
                foreach (MediaReport r in _lastReports)
                {
                    if (r.File == null || r.File.StartsWith("rtsp://") || !File.Exists(r.File))
                    {
                        skipped++;
                        continue;
                    }
                    string cat;
                    if (r.Overall == "PASS") cat = "通过";
                    else if (r.Overall == "WARN") cat = "警告";
                    else if (r.Overall == "FAIL") cat = "失败";
                    else cat = "无法分析";
                    string dstDir = Path.Combine(root, cat);
                    Directory.CreateDirectory(dstDir);
                    string dst = Path.Combine(dstDir, Path.GetFileName(r.File));
                    int n = 2;
                    while (File.Exists(dst))
                        dst = Path.Combine(dstDir, Path.GetFileNameWithoutExtension(r.File) + "_" + (n++) + Path.GetExtension(r.File));
                    File.Copy(r.File, dst, false);
                    copied++;
                }
                _statusLabel.Text = "归档完成：已复制 " + copied + " 个视频 → " + root + "（按 通过/警告/失败/无法分析 分类）" +
                    (skipped > 0 ? "，跳过 " + skipped + " 个（RTSP/文件不存在）" : "");
                Log.Info("归档分类完成：复制 " + copied + " 个 → archive，跳过 " + skipped);
                MessageBox.Show(this, "归档完成：已复制 " + copied + " 个视频到\n" + root + "\n按 通过 / 警告 / 失败 / 无法分析 四个文件夹分类。\n" +
                    (skipped > 0 ? "（跳过 " + skipped + " 个 RTSP 或文件不存在的）" : ""), "归档分类", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Error("归档失败：" + ex);
                _statusLabel.Text = "归档失败：" + ex.Message;
                MessageBox.Show(this, "归档失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenReport(object sender, EventArgs e)
        {
            string htmlPath = _lastHtmlPath.Length > 0 ? _lastHtmlPath
                : Path.Combine(ReportHtml.DefaultReportDir(), "video_check_report.html");
            if (File.Exists(htmlPath))
            {
                try { System.Diagnostics.Process.Start(htmlPath); }
                catch { MessageBox.Show("无法打开报告文件：" + htmlPath, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
            else
            {
                MessageBox.Show("报告尚未生成", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static string Str(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "?";
        }

        // ==================== 主题 ====================

        private static int IndexOfTheme(string key)
        {
            for (int i = 0; i < Themes.All.Length; i++)
                if (Themes.All[i].Key == key) return i;
            return 0;
        }

        private void OnThemePicked(object sender, EventArgs e)
        {
            if (_themeBox == null || _themeBox.SelectedIndex < 0) return;
            Themes.Apply(Themes.All[_themeBox.SelectedIndex].Key);
        }

        /// <summary>递归通知自绘控件（卡片/进度条/输入框等）重绘。</summary>
        private static void SyncCustom(Control root)
        {
            foreach (Control c in root.Controls)
            {
                CardPanel cp = c as CardPanel;
                if (cp != null) cp.Invalidate();
                FieldBox fb = c as FieldBox;
                if (fb != null) fb.SyncTheme();
                if (c.HasChildren) SyncCustom(c);
            }
        }

        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;   // 已释放的窗体不得再着色（否则访问违例）
            if (InvokeRequired) { BeginInvoke((MethodInvoker)OnThemeChanged); return; }
            int idx = IndexOfTheme(Themes.Current.Key);
            if (_themeBox != null && _themeBox.SelectedIndex != idx) _themeBox.SelectedIndex = idx;
            ApplyTheme();
            ColorizeGrid();
            Log.Info("界面主题切换为：" + Themes.Current.Name);
        }

        /// <summary>按当前主题给整窗着色：先递归覆盖，再对需要区别对待的控件做覆盖。</summary>
        private void ApplyTheme()
        {
            Theme t = Themes.Current;
            Themes.ApplyTo(this);

            // ―― 递归之后需要区别对待的控件 ――
            foreach (Label l in _mutedLabels) if (l != null) l.ForeColor = t.Muted;
            if (_pctLabel != null) _pctLabel.ForeColor = t.Accent;
            if (_startBtn != null) Themes.StylePrimary(_startBtn);
            if (_stopBtn != null) Themes.StyleTextOnly(_stopBtn, t.Danger);
            if (_themeBox != null) Themes.StyleCombo(_themeBox);
            if (_grid != null) Themes.StyleGrid(_grid);
            if (_rail != null) _rail.SyncTheme();
            foreach (Control c in Controls) SyncCustom(c);
        }

        private void ColorizeGrid()
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Cells.Count == 0) continue;
                row.Cells[0].Style.ForeColor = Themes.StatusColor(Convert.ToString(row.Cells[0].Value));
                row.Cells[0].Style.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            }
        }
    }
}
