/* -*- coding: utf-8 -*-
 * RtspForm.cs — RTSP 实时取流分析（独立页面）
 *
 * 功能：
 *   ① 单路分析：填地址 → 开始分析 → 结果 + 报告
 *   ② 流列表：每路可起「备注」（机位名），地址长不用记
 *   ③ CSV/TSV 导入导出：把已知的 RTSP 地址批量整理进来
 *   ④ 轮巡：按列表顺序逐路分析，结果回填到列表
 *
 * 判定逻辑复用 RtspBiz（实时流专用判定），本文件只负责界面、列表与线程调度。
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
    /// <summary>待检流配置（可从 CSV 导入）。</summary>
    public class StreamEntry
    {
        public bool Enabled = true;
        public string Remark = "";      // 备注 / 机位名，解决"地址太长记不住"
        public string Url = "";
        public string Overall = "";     // 最近一次总评
        public string Summary = "";     // 最近一次摘要
        public MediaReport LastReport;

        public string Display
        {
            get
            {
                if (Remark.Length > 0) return Remark + "　" + Url;
                return Url;
            }
        }
    }

    public class RtspForm : Form
    {
        /// <summary>上次使用的流地址。</summary>
        public static string LastUrl = "";

        private readonly List<StreamEntry> _streams = new List<StreamEntry>();

        private FieldBox _urlBox, _remarkBox;   // 圆角输入框
        private Button _showUrlBtn;             // 流地址「显示/隐藏」切换

        /// <summary>
        /// 真实流地址（含摄像头密码）。
        /// ★ 界面上默认显示的是**打码版本**，程序内部一律读这个字段 ——
        ///   不要直接读 _urlBox.Text，那可能拿到的是 "rtsp://admin:***@..." 这种假地址，
        ///   存进列表或拿去拉流都会失败。
        /// </summary>
        private string _realUrl = "";
        /// <summary>用户是否点了「显示」临时看明文。</summary>
        private bool _urlRevealed;
        private NumericUpDown _secBox;
        private ComboBox _modelBox;
        private CheckBox _ckRate, _ckBitrate, _ckGop, _ckMotion, _ckAudio, _ckPickup, _ckQuiet, _ckWatermark;
        private Button _addBtn, _startBtn, _stopBtn, _previewBtn;

        // ★ 「网络搜索」按钮（2026-09-15 用户要求）
        //   用户说：「这里我想像AI实时巡检一样要个网络搜索工具ONVIF那种」
        //   ★ 这一页管的是**一长串流列表**（他那边 97 路）✓
        //     而地址要手打 ✗ 机位名也要手打 ✗
        //     网络搜索能直接把摄像机找出来 + 试出取流地址 ✓
        //     → 顺便把设备名带回来预填「备注(机位)」✓
        private RoundButton _searchBtn;
        private Button _importBtn, _exportBtn, _tplBtn, _delBtn, _clearBtn, _patrolBtn;
        private Button _reportBtn, _closeBtn;
        private Label _statusLab, _pctLab, _aiHint;
        // ★ 流列表"改没改过"的标记（2026-09-13 加，修一个会丢数据的 bug）
        //   原来 FormClosed 里**无条件** SaveStreams() ✗
        //   → 只要打开一次 RTSP 页再关掉 ✓ 就会把当前列表写回去 ✓
        //   → 如果那一刻数据文件因为任何原因读不到（缺失/被移走/解密失败）
        //     就会**把空列表写回去，用户的摄像头全没了** ✓✓
        //   （这个 bug 是我做打包脱敏时触发的：文件被临时挪走期间跑了窗体测试 ✓）
        //   现在只有"真的改过"才写 ✓ 没改过关窗体一个字都不动 ✓
        private bool _dirtyStreams;
        private ToolTip _statusTip;              // 状态栏全文（定宽省略后悬停可看全）
        private FlatProgress _progress;         // 自绘圆角进度条
        private Panel _previewPanel;
        private PictureBox _frameBox;
        private DataGridView _streamGrid, _itemGrid;

        private Thread _worker;
        private volatile bool _cancel;
        private bool _aiAcquired;
        private MediaReport _lastReport;
        private string _lastHtmlPath = "";
        private int _previewSeq;

        public RtspForm()
        {

            AppInfo.SetFormIcon(this);
            Text = "RTSP 实时取流分析 — 视频核对工具";
            Font = new Font("Microsoft YaHei", 9F);
            ClientSize = new Size(1120, 880);
            MinimumSize = new Size(1020, 720);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;   // 界面全部代码布局，禁止自动缩放（它会在显示后二次移动控件，造成重绘残留/重影）
            BuildUi();
            ApplyTheme();
            UpdateAiUi();
            LoadStreams();
            RefreshList();
            // 首次从明文 CSV 迁移过来时提示一次（原文件已抹除）
            if (_migratedFromPlaintext)
            {
                _statusLab.Text = "已把原来的明文流列表迁移为加密存储，原 csv 文件已安全抹除";
                Log.Info("流列表已从明文 CSV 迁移为加密存储");
            }
            // 地址框默认带出列表第一路，省得每次都手敲
            // （判断依据用 _realUrl，不能用 _urlBox.Text —— 那里是打码后的显示文本）
            if (_streams.Count > 0 && _realUrl.Length == 0)
                SetUrl(_streams[0].Url);
            else
                SetUrl(_realUrl.Length > 0 ? _realUrl : LastUrl);
            Themes.Changed += OnThemeChanged;
            // 窗体显示后再确认一次焦点不在地址框上（否则密码会明文显示）
            Shown += delegate(object s, EventArgs e) { EnsureUrlNotFocused(); };
            FormClosed += delegate(object s, FormClosedEventArgs e)
            {
                Themes.Changed -= OnThemeChanged;
                // ★ 只有真改过才写。原来无条件写 ✗
                //   只要"打开 RTSP 页 → 关掉"就会把当前列表写回去 ✓
                //   如果那一刻数据文件读不到（缺失/被移走/解密失败）✓
                //   就会把空列表覆盖上去 ✓ 用户的摄像头全丢 ✓
                if (_dirtyStreams) SaveStreams();
                _cancel = true;
                MageCheck.AbortCurrent();
                Ffmpeg.KillRunning();
            };
        }

        // ==================== 流列表持久化 ====================

        /// <summary>
        /// 流列表落盘位置。**加密存储**（Windows DPAPI，当前用户范围）。
        /// 地址里含摄像头账号密码，明文摊在程序目录里，拷贝文件夹 / 发报告 / 截图
        /// 都可能把密码带出去；DPAPI 加密后换电脑、换 Windows 用户都解不开。
        /// </summary>
        public static string ListFile
        {
            get { return AppPaths.Data("rtsp_streams.dat"); }
        }

        /// <summary>旧版明文列表文件（首次运行会自动迁移内容并抹除）。</summary>
        public static string LegacyCsvFile
        {
            get { return AppPaths.Data("rtsp_streams.csv"); }
        }

        /// <summary>载入流列表：优先读加密文件；若有旧版明文 CSV 则迁移过来并抹除。</summary>
        private void LoadStreams()
        {
            try
            {
                // ① 加密文件存在 → 直接读
                string text = Secret.UnprotectFromFile(ListFile);
                if (text != null)
                {
                    int b1, d1;
                    List<StreamEntry> got = ParseStreamLines(SplitLines(text), _streams, out b1, out d1);
                    _streams.AddRange(got);
                    Log.Info("已载入流列表 " + got.Count + " 路（加密文件 " + Path.GetFileName(ListFile) + "）");
                    return;
                }

                // ② 有旧版明文 CSV → 迁移：读出内容 → 加密另存 → 抹除明文
                if (File.Exists(LegacyCsvFile))
                {
                    string[] lines = File.ReadAllLines(LegacyCsvFile, Encoding.UTF8);
                    int b2, d2;
                    List<StreamEntry> got = ParseStreamLines(lines, _streams, out b2, out d2);
                    _streams.AddRange(got);
                    _dirtyStreams = true;
                    SaveStreams();
                    Secret.ShredFile(LegacyCsvFile);
                    Log.Info("已把明文流列表迁移为加密存储并抹除原文件：" + got.Count + " 路");
                    _migratedFromPlaintext = true;
                    return;
                }

                // ③ 都没有 → 空列表（不再预置示例地址：预置意味着带着一个真实密码发布）
                Log.Info("流列表为空。可点「导入 CSV」批量添加，或手动填地址后点「添加/更新」。");
            }
            catch (Exception ex) { Log.Warn("载入流列表失败：" + ex.Message); }
        }

        /// <summary>是否刚从明文 CSV 迁移过来（用于给用户一次性提示）。</summary>
        private bool _migratedFromPlaintext;

        /// <summary>把文本按行拆开（兼容 \r\n / \n）。</summary>
        private static string[] SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        /// <summary>把当前列表加密写回磁盘（增删、导入、关闭时调用）。</summary>
        private void SaveStreams()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("备注,流地址");
                foreach (StreamEntry e in _streams)
                    sb.AppendLine(Csv(e.Remark) + "," + Csv(e.Url));
                if (!Secret.ProtectToFile(ListFile, sb.ToString()))
                    Log.Warn("流列表加密保存失败，本次改动可能未落盘");
            }
            catch (Exception ex) { Log.Warn("保存流列表失败：" + ex.Message); }
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            Font f = new Font("Microsoft YaHei", 9F);
            Font sf = new Font("Microsoft YaHei", 8.5F);
            int W = ClientSize.Width;
            int H = ClientSize.Height;
            int CW = W - 24;                       // 卡片宽度

            // ==================== 卡片一：实时流检测 ====================
            CardPanel c1 = new CardPanel();
            c1.Location = new Point(12, 12);
            c1.Size = new Size(CW, 170);
            c1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            c1.SetTitle("实时流检测", "填地址可单路分析；加入下方列表后可多路轮巡");
            Controls.Add(c1);

            Label ul = new Label();
            ul.Text = "流地址"; ul.Font = f; ul.AutoSize = true; ul.Location = new Point(20, 70);
            c1.Controls.Add(ul);

            // 流地址框：默认显示打码地址（rtsp://admin:***@...），
            // 点进编辑或按「显示」才露出明文。
            // 早先这里一直是明文 —— 屏幕上明摆着摄像头密码，旁边的人、截图都能看到 ✗
            _urlBox = new FieldBox(34);
            _urlBox.Location = new Point(96, 62);
            // ★ 宽度从 CW-606 收到 CW-694（2026-09-15）
            //   因为右边要插一个「网络搜索」按钮，而这一行原来是排满的 ✗
            //   重排后（从右往左，间距 8）：
            //     开始分析[96] 添加/更新[104] 备注框[156] 备注[~36] 网络搜索[104] 显示[38] 地址框
            //   → 地址框从 96 到 CW-598，宽度 CW-694 ✓
            _urlBox.Size = new Size(CW - 694, 34);
            _urlBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            c1.Controls.Add(_urlBox);

            _showUrlBtn = new RoundButton();
            _showUrlBtn.Text = "显示";
            _showUrlBtn.Font = new Font("Microsoft YaHei", 8.5F);
            _showUrlBtn.Location = new Point(CW - 590, 62);   // ★ 右移让出「网络搜索」的位置
            _showUrlBtn.Size = new Size(38, 34);
            _showUrlBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _showUrlBtn.Click += delegate(object s, EventArgs e)
            {
                _urlRevealed = !_urlRevealed;
                SyncUrlDisplay();
            };
            c1.Controls.Add(_showUrlBtn);
            // ★ 「网络搜索」按钮（ONVIF + SSDP，2026-09-15 用户要求）
            //   和「AI 实时巡检」页那个是**同一个对话框**（NetSearchForm）✓
            //   行为也照抄那一页：**取到地址就填进地址框** ✓
            //   另外多做一件事：把设备名预填到「备注(机位)」✓（那一页没有这个字段 ✓）
            //   ★ 照抄而不是重写：那一页的流程是实测过的（预填账号密码、掩码显示…）✓
            _searchBtn = new RoundButton();
            _searchBtn.Text = "网络搜索"; _searchBtn.Font = f;
            _searchBtn.Location = new Point(CW - 544, 62); _searchBtn.Size = new Size(104, 34);
            _searchBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _searchBtn.Click += OnOnvifSearch;
            c1.Controls.Add(_searchBtn);

            HookUrlBox();   // 挂焦点事件：编辑时露明文、离开时收回打码

            Label rl = new Label();
            rl.Text = "备注"; rl.Font = f; rl.AutoSize = true;
            rl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            rl.Location = new Point(CW - 432, 70);
            c1.Controls.Add(rl);

            _remarkBox = new FieldBox(34);
            _remarkBox.Location = new Point(CW - 388, 62);
            _remarkBox.Size = new Size(156, 34);
            _remarkBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            c1.Controls.Add(_remarkBox);

            _addBtn = new RoundButton();
            _addBtn.Text = "添加/更新"; _addBtn.Font = f;
            _addBtn.Location = new Point(CW - 224, 62); _addBtn.Size = new Size(104, 34);
            _addBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _addBtn.Click += OnAddStream;
            c1.Controls.Add(_addBtn);

            _startBtn = new RoundButton();
            _startBtn.Text = "开始分析"; _startBtn.Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold);
            _startBtn.Location = new Point(CW - 112, 62); _startBtn.Size = new Size(96, 34);
            _startBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _startBtn.Click += OnStartSingle;
            Themes.StylePrimary(_startBtn);
            c1.Controls.Add(_startBtn);

            Label sl = new Label();
            sl.Text = "拉流秒数"; sl.Font = f; sl.AutoSize = true; sl.Location = new Point(20, 110);
            c1.Controls.Add(sl);

            _secBox = new NumericUpDown();
            _secBox.Font = f; _secBox.Minimum = 5; _secBox.Maximum = 3600; _secBox.Value = 30;
            _secBox.Location = new Point(100, 106); _secBox.Size = new Size(70, 24);
            c1.Controls.Add(_secBox);

            Label cl = new Label();
            cl.Text = "检测项"; cl.Font = f; cl.AutoSize = true; cl.Location = new Point(188, 110);
            c1.Controls.Add(cl);

            _ckRate = MkCk("帧率丢帧", 252, 108, true, c1);
            _ckBitrate = MkCk("码率", 348, 108, true, c1);
            _ckGop = MkCk("关键帧", 418, 108, true, c1);
            _ckMotion = MkCk("画面动态", 501, 108, true, c1);
            _ckAudio = MkCk("音频", 597, 108, true, c1);
            _ckPickup = MkCk("拾音器", 673, 108, true, c1);
            _ckQuiet = MkCk("仅安静段判定", 748, 108, true, c1);
            _ckWatermark = MkCk("时间水印(AI)", 872, 108, true, c1);
            _ckWatermark.CheckedChanged += delegate(object s, EventArgs e) { UpdateAiUi(); };
            // 检测项：打包进流式容器。原来 7 个勾选框手算坐标，出过两次问题
            // （控件矩形重叠、以及绘制字体与测量字体不一致导致文字溢出）。
            Flow.Row(c1, 252, 106, 20, new Control[]
                { _ckRate, _ckBitrate, _ckGop, _ckMotion, _ckAudio, _ckPickup, _ckQuiet, _ckWatermark });

            Label ml = new Label();
            ml.Text = "AI 模型"; ml.Font = f; ml.AutoSize = true; ml.Location = new Point(20, 144);
            c1.Controls.Add(ml);

            _modelBox = new ComboBox();
            _modelBox.Font = f; _modelBox.DropDownStyle = ComboBoxStyle.DropDown;
            _modelBox.Items.Add("qwen2.5vl:7b"); _modelBox.Items.Add("qwen3-vl:8b");
            _modelBox.Text = AppSettings.OllamaModel;
            _modelBox.Location = new Point(100, 140); _modelBox.Size = new Size(170, 24);
            _modelBox.TextChanged += delegate(object s, EventArgs e) { AppSettings.SetModel(_modelBox.Text.Trim()); };
            c1.Controls.Add(_modelBox);

            _previewBtn = new RoundButton();
            _previewBtn.Text = "预览一帧"; _previewBtn.Font = f;
            _previewBtn.Location = new Point(282, 140); _previewBtn.Size = new Size(94, 26);
            _previewBtn.Click += OnPreview;
            c1.Controls.Add(_previewBtn);

            _aiHint = new Label();
            _aiHint.Font = sf; _aiHint.AutoSize = true; _aiHint.Location = new Point(388, 146);
            c1.Controls.Add(_aiHint);

            // ==================== 卡片二：待检流列表 ====================
            CardPanel c2 = new CardPanel();
            c2.Location = new Point(12, 196);
            c2.Size = new Size(CW, 330);
            c2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            c2.SetTitle("待检流列表", "勾选的参与轮巡；双击某行载入上方；列表加密保存于程序目录 rtsp_streams.dat（密码不落明文）");
            Controls.Add(c2);

            _importBtn = MkBtn("导入 CSV", 20, 62, 100, 32, f); c2.Controls.Add(_importBtn);
            _importBtn.Click += OnImport;
            _tplBtn = MkBtn("导出模板", 128, 62, 100, 32, f); c2.Controls.Add(_tplBtn);
            _tplBtn.Click += OnSaveTemplate;
            _exportBtn = MkBtn("导出列表", 236, 62, 100, 32, f); c2.Controls.Add(_exportBtn);
            _exportBtn.Click += OnExport;
            _delBtn = MkBtn("删除选中", 344, 62, 100, 32, f); c2.Controls.Add(_delBtn);
            _delBtn.Click += OnDeleteSelected;
            _clearBtn = MkBtn("清空", 452, 62, 76, 32, f); c2.Controls.Add(_clearBtn);
            _clearBtn.Click += OnClearList;

            _patrolBtn = MkBtn("开始轮巡", 552, 62, 108, 32, f); c2.Controls.Add(_patrolBtn);
            _patrolBtn.Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold);
            _patrolBtn.Click += OnStartPatrol;
            Themes.StylePrimary(_patrolBtn);

            _stopBtn = MkBtn("停止", 668, 62, 76, 32, f); c2.Controls.Add(_stopBtn);
            // 工具栏按钮：同样交给流式布局，不再手算 x 坐标
            Flow.Row(c2, 20, 62, 8, new Control[]
                { _importBtn, _tplBtn, _exportBtn, _delBtn, _clearBtn, _patrolBtn, _stopBtn });
            _stopBtn.Enabled = false;
            _stopBtn.Click += OnStop;

            _statusLab = new Label();
            _statusLab.Text = "就绪：可单路分析，也可导入 CSV 后轮巡。";
            // ★ 定宽 + 超长省略（2026-09-13 用户截图指出：报错文字撑出卡片被裁掉）
            //   原来是 AutoSize=true ✗ 而它定位在 x=756 ✓ 右边只剩约 124px ✓
            //   报错稍微长一点就撑出卡片边界 ✓ 后面的字直接被裁掉看不见 ✓
            //   改成定宽 + AutoEllipsis ✓ 再长的文字也只会在末尾显示省略号 ✓
            //   全文放进 ToolTip ✓ 鼠标悬停能看完整 ✓
            _statusLab.Font = sf;
            _statusLab.AutoSize = false;
            _statusLab.AutoEllipsis = true;
            _statusLab.TextAlign = ContentAlignment.MiddleLeft;
            // ★ 挪到进度条右边（2026-09-13 修第二次）
            //   原来在 (756, 68) —— 和那排按钮同行 ✓ 按钮占满了 ✗ 只剩约 204px ✓
            //   短到"连不上摄像头"（231px）都放不下 ✓ 报错必然被省略掉一半 ✓
            //   而进度条右边那块一直空着 ✓ 有 600px 以上 ✓ 挪过来就够用了 ✓
            _statusLab.Location = new Point(430, 100);
            _statusLab.Size = new Size(CW - 430 - 20, 20);
            _statusLab.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _statusTip = new ToolTip();
            // ★ 用 TextChanged 统一同步 tooltip（2026-09-13）
            //   这个标签有 26 处赋值 ✗ 逐个加同步太容易漏 ✓
            //   挂一次事件就能覆盖所有赋值点 ✓
            _statusLab.TextChanged += delegate (object s, EventArgs e)
            {
                try { _statusTip.SetToolTip(_statusLab, _statusLab.Text); } catch (Exception) { }
            };
            _statusTip.SetToolTip(_statusLab, _statusLab.Text);
            c2.Controls.Add(_statusLab);

            _progress = new FlatProgress();
            _progress.Location = new Point(20, 104); _progress.Size = new Size(360, 14);
            _progress.ShowText = false;
            c2.Controls.Add(_progress);

            _pctLab = new Label();
            _pctLab.Text = "0%"; _pctLab.Font = sf; _pctLab.AutoSize = true;
            _pctLab.Location = new Point(388, 102);
            c2.Controls.Add(_pctLab);

            _streamGrid = new DataGridView();
            _streamGrid.AllowUserToAddRows = false; _streamGrid.RowHeadersVisible = false;
            _streamGrid.BorderStyle = BorderStyle.FixedSingle;
            _streamGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _streamGrid.MultiSelect = true;
            _streamGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _streamGrid.Columns.Add(new DataGridViewCheckBoxColumn());
            _streamGrid.Columns[0].HeaderText = "轮巡"; _streamGrid.Columns[0].Width = 44;
            _streamGrid.Columns[0].SortMode = DataGridViewColumnSortMode.NotSortable;
            _streamGrid.Columns.Add("cRemark", "备注（机位）"); _streamGrid.Columns[1].Width = 130;
            _streamGrid.Columns.Add("cUrl", "流地址"); _streamGrid.Columns[2].Width = 420;
            _streamGrid.Columns.Add("cOverall", "总评"); _streamGrid.Columns[3].Width = 60;
            _streamGrid.Columns.Add("cRate", "合格率"); _streamGrid.Columns[4].Width = 70;
            _streamGrid.Columns.Add("cSum", "摘要"); _streamGrid.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _streamGrid.Columns[5].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _streamGrid.CellDoubleClick += OnStreamDoubleClick;
            _streamGrid.CurrentCellDirtyStateChanged += delegate(object s, EventArgs e)
            {
                if (_streamGrid.IsCurrentCellDirty) _streamGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _streamGrid.SelectionChanged += delegate(object s, EventArgs e) { ShowSelectedDetail(); };
            _streamGrid.Location = new Point(20, 130);
            _streamGrid.Size = new Size(CW - 40, 182);
            _streamGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            c2.Controls.Add(_streamGrid);

            // ==================== 卡片三：预览与明细 ====================
            CardPanel c3 = new CardPanel();
            c3.Location = new Point(12, 540);
            c3.Size = new Size(CW, H - 552);
            c3.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            c3.SetTitle("画面预览与检测明细", "预览可直观确认画面是否正常；明细显示选中那一路的逐项结果");
            Controls.Add(c3);

            int bodyH = H - 552 - 62 - 56;

            _previewPanel = new Panel();
            _previewPanel.BorderStyle = BorderStyle.FixedSingle;
            _previewPanel.BackColor = Color.Black;
            _previewPanel.Location = new Point(20, 62);
            _previewPanel.Size = new Size(430, bodyH);
            _previewPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            c3.Controls.Add(_previewPanel);

            _frameBox = new PictureBox();
            _frameBox.Dock = DockStyle.Fill;
            _frameBox.SizeMode = PictureBoxSizeMode.Zoom;
            _frameBox.BackColor = Color.Black;
            _previewPanel.Controls.Add(_frameBox);

            _itemGrid = new DataGridView();
            _itemGrid.AllowUserToAddRows = false; _itemGrid.ReadOnly = true;
            _itemGrid.RowHeadersVisible = false; _itemGrid.BorderStyle = BorderStyle.FixedSingle;
            _itemGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _itemGrid.MultiSelect = false;
            _itemGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _itemGrid.Columns.Add("cName", "检测项"); _itemGrid.Columns[0].Width = 150;
            _itemGrid.Columns.Add("cStatus", "状态"); _itemGrid.Columns[1].Width = 66;
            _itemGrid.Columns.Add("cDetail", "详情"); _itemGrid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _itemGrid.Columns[2].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _itemGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            _itemGrid.Location = new Point(462, 62);
            _itemGrid.Size = new Size(CW - 482, bodyH);
            _itemGrid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            c3.Controls.Add(_itemGrid);

            _reportBtn = MkBtn("打开最近报告", 20, bodyH + 76, 120, 32, f);
            _reportBtn.Enabled = false;
            _reportBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _reportBtn.Click += OnOpenReport;
            c3.Controls.Add(_reportBtn);

            _closeBtn = MkBtn("关闭", CW - 116, bodyH + 76, 96, 32, f);
            _closeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeBtn.Click += delegate(object s, EventArgs e) { Close(); };
            c3.Controls.Add(_closeBtn);
        }

        private Label MkLabel(string text, int x, int y, Font f)
        {
            Label l = new Label();
            l.Text = text; l.Font = f; l.AutoSize = true; l.Location = new Point(x, y);
            Controls.Add(l);
            return l;
        }

        private Button MkBtn(string text, int x, int y, int w, int h, Font f)
        {
            Button b = new RoundButton();
            b.Text = text; b.Font = f; b.Location = new Point(x, y); b.Size = new Size(w, h);
            return b;
        }

        private CheckBox MkCk(string text, int x, int y, bool on, Control parent)
        {
            // 用系统原生 CheckBox：全部由系统渲染，杜绝自绘控件的文字串位问题。
            // FlatStyle.Standard 走系统视觉样式（蓝底白勾），深色主题下也清晰可辨 ——
            // Flat 画的是浅色方框+浅色勾，对比度太低，用户会以为"勾不上"。
            CheckBox c = new CheckBox();
            c.Text = text; c.Font = new Font("Microsoft YaHei", 8.5F);
            c.Checked = on; c.AutoSize = true; c.FlatStyle = FlatStyle.Standard;
            c.Location = new Point(x, y);
            (parent != null ? parent : this).Controls.Add(c);
            return c;
        }

        /// <summary>AI 开关的可见反馈：关掉时把模型框置灰并写明不会调用 AI。</summary>
        private void UpdateAiUi()
        {
            bool on = _ckWatermark != null && _ckWatermark.Checked;
            if (_modelBox != null) _modelBox.Enabled = on;
            if (_aiHint != null)
                _aiHint.Text = on
                    ? "勾选「时间水印(AI)」时会调用本机 Ollama；取消勾选即完全不调用 AI，只做常规拉流检测"
                    : "AI 已关闭 —— 本次分析不会调用 Ollama，只做帧率/码率/关键帧/动态性等常规检测";
        }

        // ==================== 主题 ====================

        /// <summary>递归通知自绘控件（卡片/进度条/输入框）重绘。</summary>
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
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke((MethodInvoker)OnThemeChanged); return; }
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            Theme t = Themes.Current;
            Themes.ApplyTo(this);
            _previewPanel.BackColor = Color.Black;
            _frameBox.BackColor = Color.Black;
            if (_modelBox != null) Themes.StyleCombo(_modelBox);
            if (_startBtn != null) Themes.StylePrimary(_startBtn);
            if (_patrolBtn != null) Themes.StylePrimary(_patrolBtn);
            if (_stopBtn != null) Themes.StyleTextOnly(_stopBtn, t.Danger);
            if (_streamGrid != null) Themes.StyleGrid(_streamGrid);
            if (_itemGrid != null) Themes.StyleGrid(_itemGrid);
            SyncCustom(this);
            UpdateAiUi();
        }

        // ==================== 流列表 ====================

        private void RefreshList()
        {
            _streamGrid.Rows.Clear();
            foreach (StreamEntry e in _streams)
            {
                // 列表里一律显示打码后的地址：这个界面最容易被截图/共享，不能把密码露出去
                int idx = _streamGrid.Rows.Add(e.Enabled, e.Remark, Secret.MaskUrl(e.Url), e.Overall, "", e.Summary);
                if (e.Overall.Length > 0)
                {
                    _streamGrid.Rows[idx].Cells[3].Style.ForeColor = Themes.StatusColor(e.Overall);
                    _streamGrid.Rows[idx].Cells[3].Style.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
                    if (e.LastReport != null && e.LastReport.TotalCount > 0)
                        _streamGrid.Rows[idx].Cells[4].Value = e.LastReport.PassRateText
                            + "(" + e.LastReport.PassCount + "/" + e.LastReport.TotalCount + ")";
                }
            }
        }

        private void SyncListFromGrid()
        {
            for (int i = 0; i < _streams.Count && i < _streamGrid.Rows.Count; i++)
            {
                object v = _streamGrid.Rows[i].Cells[0].Value;
                _streams[i].Enabled = (v != null && v is bool) ? (bool)v : true;
            }
        }

        // ==================== 流地址的明文/打码处理 ====================

        /// <summary>
        /// 取真实流地址。**不要直接读 _urlBox.Text** —— 那里可能是打码后的假地址。
        /// 输入框获得焦点时里面是真实地址（否则没法编辑），所以焦点状态要额外同步一次。
        /// </summary>
        private string ReadUrl()
        {
            if (_urlBox != null && _urlBox.Inner != null && _urlBox.Inner.Focused)
                _realUrl = _urlBox.Text.Trim();
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
            bool reveal = _urlRevealed || (_urlBox.Inner != null && _urlBox.Inner.Focused);
            string shown = reveal ? _realUrl : Secret.MaskUrl(_realUrl);
            if (_urlBox.Text != shown) _urlBox.Text = shown;
            if (_showUrlBtn != null) _showUrlBtn.Text = _urlRevealed ? "隐藏" : "显示";
        }

        /// <summary>给地址框挂上焦点事件：编辑时露明文，离开时收回去。</summary>
        private void HookUrlBox()
        {
            // 关键：不让它自动获得焦点。
            // 地址框是页面上第一个可聚焦控件，窗体一打开就会自动聚焦它，
            // 于是「聚焦即显示明文」的规则立刻生效 —— 屏幕上还是明摆着密码 ✗
            // 设成 TabStop=false 后，只有用户**主动点进去**才显示明文 ✓
            _urlBox.Inner.TabStop = false;

            _urlBox.Inner.GotFocus += delegate(object s, EventArgs e) { SyncUrlDisplay(); };
            _urlBox.Inner.LostFocus += delegate(object s, EventArgs e)
            {
                // 编辑期间用户看到并改的就是真实地址，离开时存下来再打码
                _realUrl = _urlBox.Text.Trim();
                SyncUrlDisplay();
            };
        }

        /// <summary>窗体显示后确认焦点不在地址框上（双保险）。</summary>
        private void EnsureUrlNotFocused()
        {
            if (_urlBox == null || _urlBox.Inner == null) return;
            if (_urlBox.Inner.Focused)
            {
                ActiveControl = null;
                _urlBox.Inner.TabStop = false;
                SyncUrlDisplay();
            }
        }

        private void OnAddStream(object sender, EventArgs e)
        {
            string url = ReadUrl();
            string remark = _remarkBox.Text.Trim();
            if (url.Length < 6) { _statusLab.Text = "请先填写流地址"; return; }
            for (int i = 0; i < _streams.Count; i++)
            {
                if (string.Equals(_streams[i].Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    if (remark.Length > 0) _streams[i].Remark = remark;
                    RefreshList();
                    _dirtyStreams = true;
                    SaveStreams();
                    _statusLab.Text = "已更新第 " + (i + 1) + " 路（同地址不重复添加）";
                    return;
                }
            }
            StreamEntry ne = new StreamEntry();
            ne.Url = url; ne.Remark = remark;
            _streams.Add(ne);
            RefreshList();
            _dirtyStreams = true;
            SaveStreams();
            _statusLab.Text = "已添加第 " + _streams.Count + " 路（列表已保存，下次启动自动载入）";
        }

        private void OnDeleteSelected(object sender, EventArgs e)
        {
            if (_streamGrid.SelectedRows.Count == 0) { _statusLab.Text = "请先在上表选中要删除的行"; return; }
            List<int> idx = new List<int>();
            foreach (DataGridViewRow r in _streamGrid.SelectedRows) idx.Add(r.Index);
            idx.Sort();
            for (int i = idx.Count - 1; i >= 0; i--)
                if (idx[i] >= 0 && idx[i] < _streams.Count) _streams.RemoveAt(idx[i]);
            RefreshList();
            _dirtyStreams = true;
            SaveStreams();
            _statusLab.Text = "已删除 " + idx.Count + " 路";
        }

        private void OnClearList(object sender, EventArgs e)
        {
            if (_streams.Count == 0) return;
            if (MessageBox.Show(this, "确定清空整个流列表？", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _streams.Clear();
            RefreshList();
            _dirtyStreams = true;
            SaveStreams();
            _itemGrid.Rows.Clear();
            _statusLab.Text = "列表已清空";
        }

        private void OnStreamDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _streams.Count) return;
            SetUrl(_streams[e.RowIndex].Url);
            _remarkBox.Text = _streams[e.RowIndex].Remark;
            _statusLab.Text = "已载入第 " + (e.RowIndex + 1) + " 路到上方";
        }

        /// <summary>
        /// 「网络搜索」：ONVIF + SSDP 找摄像机，取到取流地址后填进地址框。
        ///
        /// ★ 2026-09-15 用户要求：「这里我想像AI实时巡检一样要个网络搜索工具ONVIF那种」
        ///   ★ 实现在**照抄** AI 实时巡检页那套（NetSearchForm + 预填账号密码）✓
        ///     那一套是实测过的 ✓ 重新发明只会漏掉细节 ✓
        ///
        /// ★ 这一页多做两件事（那一页没有的东西）：
        ///   ① 把设备名预填到「备注(机位)」✓
        ///      用户那边有 97 路，"大门口半球1"这种机位名是手打的 ✓
        ///      搜索里能读到摄像头的友好名 → 顺手填上 ✓ 但他**仍可改** ✓
        ///      （只在备注为空时填 ✓ 不覆盖他已经写好的东西 ✗）
        ///   ② 明确告诉他**下一步点「添加/更新」** ✓
        ///      填进地址框 ≠ 进了列表 ✗ 不说清楚他会以为已经加上了 ✓
        /// </summary>
        private void OnOnvifSearch(object sender, EventArgs e)
        {
            try
            {
                using (NetSearchForm dlg = new NetSearchForm())
                {
                    // 地址框里若已有账号密码 → 预填，省得再打一遍
                    string u, p;
                    if (TryPickCredential(ReadUrl(), out u, out p))
                        dlg.PresetCredential(u, p);

                    if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ResultUrl.Length == 0) return;

                    // ★ 填进去的动作抽成 ApplySearchResult()（2026-09-15）
                    //   为什么抽：**验收脚本要能测到这里** ✓
                    //   对话框是方法内部 new 出来的 ✗ 没法替身 ✗
                    //   而"填地址 / 别覆盖备注 / 状态栏说什么"才是**有判据的部分** ✓
                    ApplySearchResult(dlg.ResultUrl, dlg.ResultName);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("网络搜索失败：" + ex.Message);
                MessageBox.Show(this, "网络搜索出错：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 把网络搜索的结果填进界面。
        ///
        /// ★ 三条规矩（都有理由，别顺手改）：
        ///   ① **地址填进去，但不算"已加入列表"** ✗
        ///      状态栏必须写明"点「添加/更新」" ✓
        ///      不然用户以为已经加上了 ✓ 一关窗口就发现列表里没有 ✓
        ///   ② **备注只在空的时候填** ✗ 不覆盖他手打的机位名 ✓
        ///      （他那边 97 路，机位名是人工整理过的 ✓ 被程序覆盖会很难受 ✓）
        ///   ③ **日志和状态栏里的地址要打码** ✓（和这一页别处一致 ✓）
        /// </summary>
        private void ApplySearchResult(string url, string deviceName)
        {
            if (url == null || url.Length == 0) return;
            SetUrl(url);
            if (deviceName != null && deviceName.Length > 0 && _remarkBox.Text.Trim().Length == 0)
                _remarkBox.Text = deviceName;

            string shown = Secret.MaskUrl(url);
            _statusLab.Text = "已从网络搜索取到地址：" + shown
                + "　→ 确认没问题就点「添加/更新」加入下面的列表。";
            Log.Info("网络搜索取到取流地址：" + shown
                + ((deviceName != null && deviceName.Length > 0) ? "（设备名 " + deviceName + "）" : ""));
        }

        /// <summary>从一条 rtsp 地址里拆出账号密码（用于预填搜索框）。照抄 AI 实时巡检页。</summary>
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

        private void ShowSelectedDetail()
        {
            _itemGrid.Rows.Clear();
            if (_streamGrid.SelectedRows.Count == 0) return;
            int i = _streamGrid.SelectedRows[0].Index;
            if (i < 0 || i >= _streams.Count) return;
            MediaReport rep = _streams[i].LastReport;
            if (rep == null) { _statusLab.Text = "该路还没有检测结果"; return; }
            foreach (CheckItem it in rep.Items)
            {
                int idx = _itemGrid.Rows.Add(it.Name, it.Status, it.Detail);
                _itemGrid.Rows[idx].Cells[1].Style.ForeColor = Themes.StatusColor(it.Status);
                _itemGrid.Rows[idx].Cells[1].Style.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            }
            _lastReport = rep;
        }

        // ==================== CSV 导入导出 ====================

        /// <summary>把一行拆成字段：支持逗号/制表符/分号分隔，也支持带引号的 CSV。</summary>
        private static List<string> SplitCsv(string line)
        {
            List<string> fields = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuote && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuote = !inQuote;
                }
                else if (!inQuote && (c == ',' || c == '\t' || c == ';'))
                {
                    fields.Add(cur.ToString().Trim());
                    cur.Length = 0;
                }
                else cur.Append(c);
            }
            fields.Add(cur.ToString().Trim());
            return fields;
        }

        private static bool LooksLikeUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.StartsWith("rtsp://") || l.StartsWith("rtmp://") || l.StartsWith("http://")
                || l.StartsWith("https://") || l.StartsWith("udp://");
        }

        private void OnImport(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "流列表 (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|所有文件|*.*";
                dlg.Title = "导入 RTSP 流列表";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                List<StreamEntry> added = new List<StreamEntry>();
                int bad = 0, dup = 0;
                try
                {
                    string[] lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8);
                    added = ParseStreamLines(lines, _streams, out bad, out dup);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "读取失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (added.Count == 0)
                {
                    MessageBox.Show(this,
                        "没有从文件中解析出任何流地址。\n\n"
                        + "文件应为 CSV / TSV 文本，每行一路，格式：\n"
                        + "    备注,流地址\n"
                        + "例如：\n"
                        + "    大门,rtsp://admin:密码@192.168.1.64:554/Streaming/Channels/101\n\n"
                        + "（Excel 请先「另存为 CSV UTF-8」再导入）",
                        "未解析到地址", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _streams.AddRange(added);
                RefreshList();
                _dirtyStreams = true;
                SaveStreams();
                _statusLab.Text = "已导入 " + added.Count + " 路"
                    + (dup > 0 ? "，跳过重复 " + dup + " 路" : "")
                    + (bad > 0 ? "，忽略无法识别的 " + bad + " 行" : "");
                Log.Info("RTSP 列表导入：" + added.Count + " 路，来自 " + Path.GetFileName(dlg.FileName));
            }
        }

        private void OnExport(object sender, EventArgs e)
        {
            if (_streams.Count == 0) { MessageBox.Show(this, "列表为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            // 导出的是明文（含摄像头密码），必须先明确告知 ——
            // 程序自身的列表已经是 DPAPI 加密存储，不需要靠这个文件备份。
            if (MessageBox.Show(this,
                "导出的 CSV 是明文文件，里面包含摄像头账号和密码。\n\n"
                + "· 只保存在安全位置，不要随程序文件夹一起拷贝给别人；\n"
                + "· 程序自己的流列表已加密存储，不需要用它做备份。\n\n"
                + "确定继续导出吗？",
                "导出明文文件", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV 文件|*.csv";
                dlg.FileName = "rtsp_streams_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("备注,流地址,总评,合格率,摘要");
                    foreach (StreamEntry s in _streams)
                    {
                        string rate = (s.LastReport != null && s.LastReport.TotalCount > 0)
                            ? s.LastReport.PassRateText + "(" + s.LastReport.PassCount + "/" + s.LastReport.TotalCount + ")" : "";
                        sb.AppendLine(Csv(s.Remark) + "," + Csv(s.Url) + "," + Csv(s.Overall) + "," + Csv(rate) + "," + Csv(s.Summary));
                    }
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    _statusLab.Text = "已导出 " + _streams.Count + " 路：" + Path.GetFileName(dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void OnSaveTemplate(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV 文件|*.csv";
                dlg.FileName = "rtsp_流列表模板.csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("备注,流地址");
                    sb.AppendLine("大门,rtsp://admin:密码@192.168.1.64:554/Streaming/Channels/101");
                    sb.AppendLine("后巷,rtsp://admin:密码@192.168.1.65:554/Streaming/Channels/101");
                    sb.AppendLine("# 以 # 开头的行会被忽略；地址列可以放在任意位置；Excel 请另存为 CSV UTF-8");
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    _statusLab.Text = "模板已保存：" + Path.GetFileName(dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// 把文本行解析成流条目：兼容 CSV / TSV，地址列可放在任意位置，
        /// 自动跳过表头、注释行（# 开头）与重复地址。抽成静态方法以便单元自检。
        /// </summary>
        internal static List<StreamEntry> ParseStreamLines(string[] lines, List<StreamEntry> existing,
            out int bad, out int dup)
        {
            List<StreamEntry> added = new List<StreamEntry>();
            bad = 0; dup = 0;
            if (lines == null) return added;
            foreach (string raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#")) continue;
                List<string> fs = SplitCsv(line);

                string url = "", remark = "";
                for (int i = 0; i < fs.Count; i++)
                    if (LooksLikeUrl(fs[i])) { url = fs[i]; fs.RemoveAt(i); break; }
                if (url.Length == 0) { bad++; continue; }        // 表头/说明行
                for (int i = 0; i < fs.Count; i++)
                {
                    string t = fs[i];
                    if (t.Length == 0) continue;
                    if (t == "备注" || t == "名称" || t == "机位" || t == "remark" || t == "name") continue;
                    if (t == "流地址" || t == "地址" || t == "url") continue;
                    remark = t; break;
                }

                bool isDup = false;
                if (existing != null)
                    foreach (StreamEntry ex in existing)
                        if (string.Equals(ex.Url, url, StringComparison.OrdinalIgnoreCase)) { isDup = true; break; }
                if (!isDup)
                    foreach (StreamEntry ex in added)
                        if (string.Equals(ex.Url, url, StringComparison.OrdinalIgnoreCase)) { isDup = true; break; }
                if (isDup) { dup++; continue; }

                StreamEntry ne = new StreamEntry();
                ne.Url = url; ne.Remark = remark;
                added.Add(ne);
            }
            return added;
        }

        // ==================== 预览 ====================

        private void OnPreview(object sender, EventArgs e)
        {
            string url = ReadUrl();
            if (url.Length < 6) { _statusLab.Text = "请先填写流地址"; return; }
            if (_worker != null && _worker.IsAlive) { _statusLab.Text = "分析进行中，请等结束后再预览"; return; }

            _previewBtn.Enabled = false;
            _statusLab.Text = "正在抓取预览帧…";
            int seq = ++_previewSeq;
            Thread th = new Thread(delegate()
            {
                byte[] jpg = null;
                string err = "";
                try { jpg = MageCheck.ExtractFrameJpeg(url, 0, 1280); }
                catch (Exception ex) { err = ex.Message; }
                Ui(delegate()
                {
                    if (seq != _previewSeq) return;
                    _previewBtn.Enabled = true;
                    if (jpg != null && jpg.Length > 0) ShowFrame(jpg);
                    _statusLab.Text = (jpg != null && jpg.Length > 0)
                        ? "预览已更新（抓取于 " + DateTime.Now.ToString("HH:mm:ss") + "）"
                        : ("预览失败：" + (err.Length > 0 ? err : "未取到画面"));
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private void ShowFrame(byte[] jpg)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(jpg))
                {
                    Image img = Image.FromStream(ms);
                    Image old = _frameBox.Image;
                    _frameBox.Image = img;
                    if (old != null) old.Dispose();
                }
            }
            catch (Exception ex)
            {
                // 不能静默吞掉：调用方会把状态栏写成「预览已更新」，
                // 而实际画面没显示出来 —— 用户会以为程序坏了却没有任何线索 ✗
                Log.Warn("显示预览画面失败：" + ex.Message);
                _statusLab.Text = "画面解码失败，无法预览（详见日志）";
            }
        }

        // ==================== 分析 ====================

        private void OnStartSingle(object sender, EventArgs e)
        {
            string url = ReadUrl();
            if (url.Length < 6) { MessageBox.Show(this, "请填写 RTSP / 网络流地址", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!BeginBusy("RTSP 实时取流分析")) return;
            LastUrl = url;
            double sec = (double)_secBox.Value;
            CheckOptions opts = BuildOptions();
            _worker = new Thread(delegate()
            {
                try
                {
                    MediaReport rep = Engine.CheckRtsp(url, sec, opts, Cancelled);
                    _lastReport = rep;
                    Ui(delegate()
                    {
                        FillItemGrid(rep);
                        SetProgress(100);
                        _statusLab.Text = "分析完成：总评 " + rep.Overall + "（" + rep.PassCount + "/" + rep.TotalCount + " 项通过）";
                    });
                    List<MediaReport> one = new List<MediaReport>();
                    one.Add(rep);
                    SaveReport(one, "RTSP 拉流 " + sec.ToString("0") + " 秒");
                }
                catch (MageCheck.StoppedException) { Ui(delegate() { _statusLab.Text = "已停止"; }); }
                catch (Exception ex) { OnWorkError(ex); }
                finally { EndBusy(); }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void OnStartPatrol(object sender, EventArgs e)
        {
            SyncListFromGrid();
            List<int> targets = new List<int>();
            for (int i = 0; i < _streams.Count; i++)
                if (_streams[i].Enabled && _streams[i].Url.Length > 5) targets.Add(i);
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "没有勾选任何待检流。\n\n可在上表「轮巡」列勾选，或用「导入 CSV」批量加入。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!BeginBusy("RTSP 轮巡")) return;

            double sec = (double)_secBox.Value;
            CheckOptions opts = BuildOptions();
            _worker = new Thread(delegate()
            {
                List<MediaReport> all = new List<MediaReport>();
                int done = 0, fail = 0;
                try
                {
                    foreach (int i in targets)
                    {
                        if (_cancel) break;
                        StreamEntry en = _streams[i];
                        string label = en.Remark.Length > 0 ? en.Remark : ("第 " + (i + 1) + " 路");
                        Ui(delegate() { _statusLab.Text = "轮巡中 [" + (done + 1) + "/" + targets.Count + "] " + label + " …"; });

                        MediaReport rep;
                        try { rep = Engine.CheckRtsp(en.Url, sec, opts, Cancelled); }
                        catch (MageCheck.StoppedException) { break; }
                        catch (Exception ex)
                        {
                            rep = new MediaReport(en.Url);
                            rep.Error = ex.Message;
                            fail++;
                        }
                        en.LastReport = rep;
                        en.Overall = rep.Overall;
                        en.Summary = Summarize(rep);
                        all.Add(rep);

                        int cur = i, n = done + 1, total = targets.Count;
                        Ui(delegate()
                        {
                            UpdateRow(cur);
                            SetProgress((int)((double)n / total * 100));
                        });
                        done++;
                    }

                    // 轮巡报告：所有流汇总成一份
                    if (all.Count > 0)
                        SaveReport(all, "RTSP 轮巡 " + all.Count + " 路 / 每路 " + sec.ToString("0") + " 秒");

                    int nd = done, nf = fail;
                    Ui(delegate()
                    {
                        SetProgress(100);
                        _statusLab.Text = (_cancel ? "轮巡已停止：" : "轮巡完成：")
                            + "共 " + nd + " 路" + (nf > 0 ? "（其中 " + nf + " 路异常）" : "")
                            + "，报告 " + (_lastHtmlPath.Length > 0 ? Path.GetFileName(_lastHtmlPath) : "生成失败");
                    });
                }
                catch (Exception ex) { OnWorkError(ex); }
                finally { EndBusy(); }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private static string Summarize(MediaReport rep)
        {
            if (rep.Error.Length > 0) return rep.Error;
            List<string> bad = new List<string>();
            foreach (CheckItem it in rep.Items)
                if (it.Status == "FAIL" || it.Status == "WARN") bad.Add(it.Name);
            if (bad.Count == 0) return "各项正常";
            return string.Join("、", bad.ToArray()) + " 存在问题";
        }

        /// <summary>轮巡时只刷新一行，避免整表重建导致勾选状态丢失。</summary>
        private void UpdateRow(int i)
        {
            if (i < 0 || i >= _streams.Count || i >= _streamGrid.Rows.Count) return;
            StreamEntry en = _streams[i];
            DataGridViewRow row = _streamGrid.Rows[i];
            row.Cells[3].Value = en.Overall;
            row.Cells[3].Style.ForeColor = Themes.StatusColor(en.Overall);
            row.Cells[3].Style.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            row.Cells[4].Value = (en.LastReport != null && en.LastReport.TotalCount > 0)
                ? en.LastReport.PassRateText + "(" + en.LastReport.PassCount + "/" + en.LastReport.TotalCount + ")" : "";
            row.Cells[5].Value = en.Summary;
            _streamGrid.InvalidateRow(i);
        }

        /// <summary>占用 AI 名额（仅在水印检测勾选时）并禁用按钮。</summary>
        private bool BeginBusy(string who)
        {
            if (_worker != null && _worker.IsAlive)
            {
                MessageBox.Show(this, "正在分析中，请先点「停止」", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (_ckWatermark.Checked)
            {
                string holder;
                if (!MageCheck.TryAcquire(who, out holder))
                {
                    MessageBox.Show(this,
                        "已有一个 AI 任务在运行（" + (holder.Length > 0 ? holder : "其它窗口") + "）。\n\n"
                        + "两个任务同时调用本机模型会把显存和推理队列挤满。\n"
                        + "可先取消勾选「时间水印(AI)」只做常规拉流检测，或等其它任务结束。",
                        "AI 正忙", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                _aiAcquired = true;
            }
            _cancel = false;
            _startBtn.Enabled = false; _patrolBtn.Enabled = false;
            _stopBtn.Enabled = true; _previewBtn.Enabled = false; _reportBtn.Enabled = false;
            SetProgress(0);
            return true;
        }

        private void EndBusy()
        {
            if (_aiAcquired) { MageCheck.Release("RTSP 实时取流分析"); _aiAcquired = false; }
            Ui(delegate()
            {
                _startBtn.Enabled = true; _patrolBtn.Enabled = true;
                _stopBtn.Enabled = false; _previewBtn.Enabled = true;
            });
        }

        private void OnWorkError(Exception ex)
        {
            Log.Error("RTSP 分析异常：" + ex);
            Ui(delegate() { _statusLab.Text = "分析出错：" + ex.Message; });
        }

        private CheckOptions BuildOptions()
        {
            bool wantWm = _ckWatermark.Checked;
            CheckOptions opts = new CheckOptions();
            opts.RtspBizMode = true;
            opts.RtspRate = _ckRate.Checked;
            opts.RtspBitrate = _ckBitrate.Checked;
            opts.RtspGop = _ckGop.Checked;
            opts.RtspMotion = _ckMotion.Checked;
            opts.RtspAudio = _ckAudio.Checked;
            opts.RtspPickup = _ckPickup.Checked;
            opts.Rtsp.PickupQuietGateEnabled = _ckQuiet.Checked;   // 拾音器「仅安静段判定」开关
            opts.RtspWatermark = wantWm;
            opts.CheckMage = wantWm;          // 取消勾选即完全不调用 AI
            opts.CheckVideo = true;
            opts.CheckAudio = _ckAudio.Checked;
            opts.CheckSync = false;
            opts.Mage.Url = AppSettings.OllamaUrl;      // 与「设置」页共用一份
            opts.Mage.Model = _modelBox.Text.Trim();
            return opts;
        }

        private void SaveReport(List<MediaReport> reports, string modeDesc)
        {
            try
            {
                string dir = ReportHtml.DefaultReportDir();
                Directory.CreateDirectory(dir);
                string htmlPath = ReportHtml.StampedPath(dir, "rtsp_check_report", ".html");
                ReportHtml.Export(reports, htmlPath, modeDesc);
                PdfExport.ExportPdf(reports, Path.ChangeExtension(htmlPath, ".pdf"), modeDesc);
                _lastHtmlPath = htmlPath;
                Ui(delegate()
                {
                    _reportBtn.Enabled = true;
                    _statusLab.Text = "报告已生成：" + Path.GetFileName(htmlPath);
                });
            }
            catch (Exception ex) { Log.Warn("RTSP 报告生成失败：" + ex.Message); }
        }

        private void OnStop(object sender, EventArgs e)
        {
            _cancel = true;
            // 光置标志位不够：必须打断在途的 ffmpeg 抽帧与推理请求
            MageCheck.AbortCurrent();
            Ffmpeg.KillRunning();
            _stopBtn.Enabled = false;
            _statusLab.Text = "正在停止…";
            Log.Info("RTSP 分析：用户请求停止");
        }

        private void FillItemGrid(MediaReport rep)
        {
            _itemGrid.Rows.Clear();
            foreach (CheckItem it in rep.Items)
            {
                int idx = _itemGrid.Rows.Add(it.Name, it.Status, it.Detail);
                _itemGrid.Rows[idx].Cells[1].Style.ForeColor = Themes.StatusColor(it.Status);
                _itemGrid.Rows[idx].Cells[1].Style.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            }
            if (rep.Error.Length > 0)
            {
                int idx = _itemGrid.Rows.Add("错误", "FAIL", rep.Error);
                _itemGrid.Rows[idx].Cells[1].Style.ForeColor = Themes.Current.Fail;
            }
        }

        private void SetProgress(int pct)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            _progress.Value = pct;
            if (_pctLab != null) _pctLab.Text = pct + "%";
        }

        private void Ui(Action a)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        /// <summary>供 RtspBiz 在步骤边界检查的取消回调。</summary>
        private bool Cancelled() { return _cancel; }

        private void OnOpenReport(object sender, EventArgs e)
        {
            if (_lastHtmlPath.Length == 0 || !File.Exists(_lastHtmlPath))
            {
                MessageBox.Show(this, "报告尚未生成", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { System.Diagnostics.Process.Start(_lastHtmlPath); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开报告：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
