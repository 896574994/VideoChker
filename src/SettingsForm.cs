/* -*- coding: utf-8 -*-
 * SettingsForm.cs — 共享设置页
 *
 * 把原先散落在三个地方的 AI 配置（主窗体「AI 配置」行、内容理解页、RTSP 页）
 * 集中到一页。保存后写入 settings.ini，所有页面立即同步。
 *
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VideoChecker
{
    public class SettingsForm : Form
    {
        private TextBox _urlBox;
        private ComboBox _modelBox, _strategyBox, _themeBox;
        private NumericUpDown _frames;
        private Button _fetchBtn, _customBtn, _saveBtn, _cancelBtn;
        private Label _hint, _customTip;

        public SettingsForm()
        {

            AppInfo.SetFormIcon(this);
            Text = "设置 — 视频核对工具";
            Font = new Font("Microsoft YaHei", 9F);
            // 高度要够：四张卡片最下面那张底边在 520（428+92），按钮必须落在它下方，
            // 早先设成 556 → 按钮 y=512，正好被存储卡片压住。
            ClientSize = new Size(620, 584);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;   // 界面全部代码布局，禁止自动缩放（它会在显示后二次移动控件，造成重绘残留/重影）
            BuildUi();
            LoadFromSettings();
            ApplyTheme();
            Themes.Changed += OnThemeChanged;
            FormClosed += delegate(object s, FormClosedEventArgs e) { Themes.Changed -= OnThemeChanged; };
        }

        /// <summary>
        /// 构建设置界面。
        /// 改为与主窗体一致的卡片式布局（四个区块各一张卡）——
        /// 注意 CardPanel 的标题+副标题占顶部约 44px，卡片内控件必须放在这之下，否则会叠字。
        /// </summary>
        private void BuildUi()
        {
            Font f = new Font("Microsoft YaHei", 9F);
            Font sf = new Font("Microsoft YaHei", 8.5F);
            int W = ClientSize.Width;

            // ================= 卡片一：AI 配置 =================
            CardPanel c1 = new CardPanel();
            c1.SetTitle("AI 配置（Ollama）", "三个页面共用这一份配置；不用 AI 功能可跳过");
            c1.Location = new Point(12, 10);
            c1.Size = new Size(W - 24, 210);
            Controls.Add(c1);

            MkLabel(c1, "服务地址：", 18, 54, f);
            _urlBox = new TextBox();
            _urlBox.Font = f; _urlBox.Location = new Point(96, 51); _urlBox.Size = new Size(300, 24);
            c1.Controls.Add(_urlBox);

            MkLabel(c1, "视觉模型：", 18, 88, f);
            _modelBox = new ComboBox();
            _modelBox.Font = f; _modelBox.DropDownStyle = ComboBoxStyle.DropDown;
            _modelBox.Items.Add("qwen2.5vl:7b"); _modelBox.Items.Add("qwen3-vl:8b");
            _modelBox.Location = new Point(96, 85); _modelBox.Size = new Size(220, 24);
            c1.Controls.Add(_modelBox);
            _fetchBtn = new RoundButton();
            _fetchBtn.Text = "自动获取模型"; _fetchBtn.Font = sf;
            _fetchBtn.Location = new Point(324, 83); _fetchBtn.Size = new Size(110, 28);
            _fetchBtn.Click += OnFetchModels; c1.Controls.Add(_fetchBtn);

            MkLabel(c1, "抽帧帧数：", 18, 122, f);
            _frames = new NumericUpDown();
            _frames.Font = f; _frames.Minimum = 1; _frames.Maximum = 200;
            _frames.Location = new Point(96, 119); _frames.Size = new Size(70, 24);
            c1.Controls.Add(_frames);

            MkLabel(c1, "抽帧策略：", 196, 122, f);
            _strategyBox = new ComboBox();
            _strategyBox.Font = f; _strategyBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _strategyBox.Items.Add("智能(动静优先)");
            _strategyBox.Items.Add("仅动静");
            _strategyBox.Items.Add("均匀");
            _strategyBox.Items.Add("全片分析");
            _strategyBox.Items.Add("自定义(输入时间点)");
            _strategyBox.Location = new Point(276, 119); _strategyBox.Size = new Size(150, 24);
            _strategyBox.SelectedIndexChanged += delegate(object s, EventArgs e) { UpdateCustomBtn(); };
            c1.Controls.Add(_strategyBox);

            _customBtn = new RoundButton();
            _customBtn.Text = "设置时间点"; _customBtn.Font = sf;
            _customBtn.Location = new Point(434, 117); _customBtn.Size = new Size(100, 28);
            _customBtn.Click += OnEditCustomTimes; c1.Controls.Add(_customBtn);

            _customTip = new Label();
            _customTip.Font = sf; _customTip.ForeColor = Color.Gray; _customTip.AutoSize = true;
            _customTip.Location = new Point(96, 150);
            c1.Controls.Add(_customTip);

            _hint = new Label();
            _hint.Text = "建议用带 vl 的视觉模型：qwen2.5vl:7b 快且水印读数稳定；qwen3-vl:8b 描述更细但慢很多。";
            _hint.Font = sf; _hint.ForeColor = Color.Gray;
            _hint.AutoSize = false; _hint.Size = new Size(W - 60, 20);
            _hint.Location = new Point(18, 176);
            c1.Controls.Add(_hint);

            // ================= 卡片二：界面 =================
            CardPanel c2 = new CardPanel();
            c2.SetTitle("界面", "主题也可在主窗体右上角直接切换");
            c2.Location = new Point(12, 228);
            c2.Size = new Size(W - 24, 92);
            Controls.Add(c2);

            MkLabel(c2, "主题：", 18, 58, f);
            _themeBox = new ComboBox();
            _themeBox.Font = f; _themeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < Themes.All.Length; i++) _themeBox.Items.Add(Themes.All[i].Name);
            _themeBox.Location = new Point(96, 55); _themeBox.Size = new Size(120, 24);
            _themeBox.SelectedIndexChanged += delegate(object s, EventArgs e)
            {
                if (_themeBox.SelectedIndex >= 0) Themes.Apply(Themes.All[_themeBox.SelectedIndex].Key);
            };
            c2.Controls.Add(_themeBox);

            // ================= 卡片三：安全 =================
            CardPanel c3 = new CardPanel();
            c3.SetTitle("安全", "开屏密码：默认不设，打开程序直接进入");
            c3.Location = new Point(12, 328);
            c3.Size = new Size(W - 24, 92);
            Controls.Add(c3);

            bool pwdOn = PassLock.IsEnabled;
            Button pwdBtn = new RoundButton();
            pwdBtn.Text = pwdOn ? "修改开屏密码" : "设置开屏密码";
            pwdBtn.Font = f;
            pwdBtn.Location = new Point(18, 54);
            pwdBtn.Size = new Size(150, 30);
            pwdBtn.Click += delegate(object s2, EventArgs e2)
            {
                OnChangePassword(pwdOn ? PasswordChangeForm.Mode.Change : PasswordChangeForm.Mode.Set);
            };
            c3.Controls.Add(pwdBtn);

            if (pwdOn)
            {
                Button clearBtn = new RoundButton();
                clearBtn.Text = "取消密码";
                clearBtn.Font = f;
                clearBtn.Location = new Point(176, 54);
                clearBtn.Size = new Size(100, 30);
                clearBtn.Click += delegate(object s2, EventArgs e2)
                {
                    if (MessageBox.Show(this, "取消后打开程序将直接进入，不再要求密码。确定吗？",
                        "取消开屏密码", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                        return;
                    OnChangePassword(PasswordChangeForm.Mode.Clear);
                };
                c3.Controls.Add(clearBtn);
            }

            Label pwdTip = new Label();
            pwdTip.Text = pwdOn
                ? "已启用（哈希存储，文件里看不到明文）"
                : "当前未设置 —— 打开程序会直接进入";
            pwdTip.Font = sf;
            pwdTip.ForeColor = pwdOn ? Color.Gray : Color.FromArgb(200, 120, 20);
            pwdTip.AutoSize = true;
            pwdTip.Location = new Point(288, 61);
            c3.Controls.Add(pwdTip);

            // ================= 卡片四：存储 =================
            CardPanel c4 = new CardPanel();
            c4.SetTitle("存储", "归档目录只增不减，最容易吃满磁盘");
            c4.Location = new Point(12, 428);
            c4.Size = new Size(W - 24, 92);
            Controls.Add(c4);

            Button storeBtn = new RoundButton();
            storeBtn.Text = "存储管理…";
            storeBtn.Font = f;
            storeBtn.Location = new Point(18, 54);
            storeBtn.Size = new Size(150, 30);
            storeBtn.Click += delegate(object s2, EventArgs e2)
            {
                using (StorageForm sf2 = new StorageForm()) sf2.ShowDialog(this);
            };
            c4.Controls.Add(storeBtn);

            Label storeTip = new Label();
            storeTip.Text = "查看报告 / 归档 / 日志各占多少，按保留数量清理";
            storeTip.Font = sf;
            storeTip.ForeColor = Color.Gray;
            storeTip.AutoSize = true;
            storeTip.Location = new Point(178, 61);
            c4.Controls.Add(storeTip);

            // ================= 底部按钮 =================
            // 注意：by 必须先声明 —— 版本号标签和按钮都要用它 ✓
            int by = ClientSize.Height - 44;

            // 版本号放在底部左侧（用户会来这里确认装的是哪一版）
            Label ver = new Label();
            ver.Text = AppInfo.Title + "  " + AppInfo.Version + "（" + AppInfo.BuildDate + "）";
            ver.Font = new Font("Microsoft YaHei", 8.5F);
            ver.ForeColor = Color.Gray;
            ver.AutoSize = true;
            ver.Location = new Point(16, by + 8);
            Controls.Add(ver);
            _saveBtn = new RoundButton();
            _saveBtn.Text = "保存"; _saveBtn.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            _saveBtn.Location = new Point(W - 200, by); _saveBtn.Size = new Size(88, 30);
            _saveBtn.Click += OnSave; Controls.Add(_saveBtn);
            AcceptButton = _saveBtn;

            _cancelBtn = new RoundButton();
            _cancelBtn.Text = "取消"; _cancelBtn.Font = f;
            _cancelBtn.Location = new Point(W - 104, by); _cancelBtn.Size = new Size(88, 30);
            _cancelBtn.Click += delegate(object s, EventArgs e) { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancelBtn);
            CancelButton = _cancelBtn;
        }

        /// <summary>弹出开屏密码对话框（设置 / 修改 / 取消三种模式）。</summary>
        private void OnChangePassword(PasswordChangeForm.Mode mode)
        {
            using (PasswordChangeForm dlg = new PasswordChangeForm(mode))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string msg =
                    mode == PasswordChangeForm.Mode.Set ? "开屏密码已设置。\n\n下次启动需要输入密码。"
                  : mode == PasswordChangeForm.Mode.Clear ? "已取消开屏密码。\n\n下次启动将直接进入，不再要求密码。"
                  : "开屏密码已修改。\n\n下次启动请用新密码。";
                // 刻意不在这里写恢复方法：界面文字会被使用本程序的人看到，
                // 把后门密码或"删哪个文件能解锁"写在界面上，等于把钥匙挂在锁上。
                // 恢复方法只记录在 docs\ 下（不随程序分发）。
                MessageBox.Show(this, msg, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        /// <summary>在指定卡片里放一个标签（卡片化后控件不再直接挂在窗体上）。</summary>
        private Label MkLabel(Control parent, string text, int x, int y, Font f)
        {
            Label l = new Label();
            l.Text = text; l.Font = f; l.AutoSize = true; l.Location = new Point(x, y);
            parent.Controls.Add(l);
            return l;
        }

        private void LoadFromSettings()
        {
            _urlBox.Text = AppSettings.OllamaUrl;
            _modelBox.Text = AppSettings.OllamaModel;
            _frames.Value = Math.Max(_frames.Minimum, Math.Min(_frames.Maximum, AppSettings.MageFrames));
            _strategyBox.Text = AppSettings.StrategyToText(AppSettings.MageStrategy);
            _themeBox.SelectedIndex = IndexOfTheme(Themes.Current.Key);
            UpdateCustomBtn();
        }

        private static int IndexOfTheme(string key)
        {
            for (int i = 0; i < Themes.All.Length; i++)
                if (Themes.All[i].Key == key) return i;
            return 0;
        }

        private void UpdateCustomBtn()
        {
            bool custom = AppSettings.TextToStrategy(_strategyBox.Text) == "custom";
            _customBtn.Enabled = custom;
            _customTip.Text = custom
                ? ("当前自定义时间点：" + (AppSettings.CustomTimes.Count > 0
                    ? string.Join("s, ", ShowTimes()) + "s" : "（尚未设置，点右侧按钮）"))
                : "";
        }

        private string[] ShowTimes()
        {
            string[] a = new string[AppSettings.CustomTimes.Count];
            for (int i = 0; i < a.Length; i++) a[i] = AppSettings.CustomTimes[i].ToString("0.#");
            return a;
        }

        private void OnFetchModels(object sender, EventArgs e)
        {
            _fetchBtn.Enabled = false;
            string url = _urlBox.Text.Trim();
            try
            {
                List<string> names = MageCheck.FetchModels(url);
                if (names.Count == 0)
                {
                    MessageBox.Show(this, "未获取到模型。请确认 Ollama 已启动、地址正确。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // 视觉模型排前面
                List<string> vl = new List<string>(), other = new List<string>();
                foreach (string n in names)
                    if (n.ToLowerInvariant().Contains("vl")) vl.Add(n); else other.Add(n);
                _modelBox.Items.Clear();
                foreach (string n in vl) _modelBox.Items.Add(n);
                foreach (string n in other) _modelBox.Items.Add(n);
                if (vl.Count > 0) _modelBox.Text = vl[0];
                MessageBox.Show(this, "获取到 " + names.Count + " 个模型（带 vl 的视觉模型 " + vl.Count + " 个已排在最前）",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "获取失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _fetchBtn.Enabled = true; }
        }

        private void OnEditCustomTimes(object sender, EventArgs e)
        {
            string init = string.Join(", ", ShowTimes());
            string input = Prompt.Show(this, "自定义抽帧时间点", "输入抽帧时间点（秒），用逗号或空格分隔，如：5, 12, 30, 45", init);
            if (input == null) return;
            List<double> ts = new List<double>();
            foreach (string p in input.Split(new char[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double t;
                if (double.TryParse(p, out t) && t >= 0) ts.Add(t);
            }
            ts.Sort();
            AppSettings.CustomTimes = ts;
            UpdateCustomBtn();
        }

        private void OnSave(object sender, EventArgs e)
        {
            string url = _urlBox.Text.Trim();
            string model = _modelBox.Text.Trim();
            if (url.Length == 0) { MessageBox.Show(this, "请填写 Ollama 服务地址", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (model.Length == 0) { MessageBox.Show(this, "请填写或选择一个视觉模型", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            AppSettings.Apply(url, model, (int)_frames.Value,
                AppSettings.TextToStrategy(_strategyBox.Text), AppSettings.CustomTimes);
            Log.Info("设置已保存：模型 " + model + "，地址 " + url + "，帧数 " + _frames.Value
                + "，策略 " + _strategyBox.Text);
            DialogResult = DialogResult.OK;
            Close();
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
            if (_modelBox != null) Themes.StyleCombo(_modelBox);
            if (_strategyBox != null) Themes.StyleCombo(_strategyBox);
            if (_themeBox != null) Themes.StyleCombo(_themeBox);
            if (_saveBtn != null) Themes.StylePrimary(_saveBtn);
        }
    }

    /// <summary>极简输入框（WinForms 没有内置的 InputBox）。</summary>
    internal static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string label, string init)
        {
            Form f = new Form();
            f.Text = title;
            f.Font = new Font("Microsoft YaHei", 9F);
            f.ClientSize = new Size(430, 130);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterParent;

            Label l = new Label();
            l.Text = label; l.AutoSize = false; l.Size = new Size(400, 34);
            l.Location = new Point(14, 12);
            f.Controls.Add(l);

            TextBox tb = new TextBox();
            tb.Text = init ?? ""; tb.Location = new Point(14, 50); tb.Size = new Size(400, 24);
            f.Controls.Add(tb);

            Button ok = new RoundButton();
            ok.Text = "确定"; ok.Location = new Point(240, 86); ok.Size = new Size(84, 28);
            ok.Click += delegate(object s, EventArgs e) { f.DialogResult = DialogResult.OK; f.Close(); };
            f.Controls.Add(ok); f.AcceptButton = ok;

            Button cancel = new RoundButton();
            cancel.Text = "取消"; cancel.Location = new Point(330, 86); cancel.Size = new Size(84, 28);
            cancel.Click += delegate(object s, EventArgs e) { f.DialogResult = DialogResult.Cancel; f.Close(); };
            f.Controls.Add(cancel); f.CancelButton = cancel;

            Themes.ApplyTo(f);
            Themes.StylePrimary(ok);
            return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
        }
    }
}
