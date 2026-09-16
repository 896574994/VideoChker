/* -*- coding: utf-8 -*-
 * LogForm.cs — 运行日志窗口（左侧导航栏的「日志」图标打开）
 * 级别切换：常规（INFO/WARN/ERROR）/ 排障 Debug（含逐帧细节）。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace VideoChecker
{
    public class LogForm : Form
    {
        private ComboBox _levelBox;
        // ★ 「导出诊断包」（2026-09-14 用户要求）
        //   用户说：「你在这个程序里面的日志功能中的 debug 搞一个能够收集到
        //           你需要的报错日志信息的工具或代码，
        //           然后我才好弄报错的信息回来给你分析」
        //   按钮放在日志窗口最自然：**用户遇到问题时本来就会来这儿看日志** ✓
        private RoundButton _diagBtn;
        // ★ 「记录报文」开关（2026-09-15 用户要求"抓包报文"）
        //   默认开 ✓ 出问题时才发现没开就晚了 ✗
        private CheckBox _traceChk;
        private Button _clearBtn;
        private CheckBox _autoScroll;
        private TextBox _box;

        private bool _showDebug = false;
        private readonly object _uiLock = new object();

        public LogForm()
        {

            AppInfo.SetFormIcon(this);
            Text = "运行日志";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 460);
            MinimumSize = new Size(560, 300);
            Font = new Font("Microsoft YaHei", 9F);
            AutoScaleMode = AutoScaleMode.None;

            // 与主窗体统一的卡片式布局：顶部工具条一张卡，日志正文一张卡
            // 注意：CardPanel 的标题+副标题占顶部约 44px，控件必须放在这之下，否则会叠字。
            CardPanel toolbar = new CardPanel();
            toolbar.SetTitle("日志筛选", "级别 / 自动滚动 / 落盘位置");
            toolbar.Location = new Point(12, 10);
            toolbar.Size = new Size(ClientSize.Width - 24, 100);
            toolbar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(toolbar);

            Label lab = new Label();
            lab.Text = "级别:";
            lab.AutoSize = true;
            lab.Location = new Point(18, 56);
            toolbar.Controls.Add(lab);

            _levelBox = new ComboBox();
            _levelBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _levelBox.Items.Add("常规（不含调试）");
            _levelBox.Items.Add("排障 Debug（全部）");
            _levelBox.SelectedIndex = 0;
            _levelBox.Location = new Point(56, 53);
            _levelBox.Size = new Size(120, 24);
            _levelBox.SelectedIndexChanged += OnLevelChanged;
            toolbar.Controls.Add(_levelBox);

            _clearBtn = new RoundButton();
            _clearBtn.Text = "清空";
            _clearBtn.Font = Font;
            _clearBtn.Location = new Point(190, 52);
            _clearBtn.Size = new Size(60, 26);
            _clearBtn.Click += (s, e) => { _box.Clear(); };
            toolbar.Controls.Add(_clearBtn);

            // ★ 「导出诊断包」按钮（2026-09-14 加）
            //   一次点击 → 往程序目录写一个「诊断_日期时间.txt」✓
            //   里面：环境+运行库+ffmpeg退出码 / ERROR·WARN 摘录 / 完整日志(含 DEBUG) /
            //         设置(已脱敏) / 目录清单 ✓
            //   ★ 全程已脱敏 ✓ 用户**直接发给开发者**就行 ✓
            _diagBtn = new RoundButton();
            _diagBtn.Text = "导出诊断包";
            _diagBtn.Font = Font;
            _diagBtn.Location = new Point(260, 52);
            _diagBtn.Size = new Size(96, 26);
            _diagBtn.Click += OnExportDiag;
            toolbar.Controls.Add(_diagBtn);

            // ★ 「记录报文」开关（2026-09-15 加）
            //   报文 = 协议原文（摄像头推了什么、我们发了什么）✓ 排障最有用 ✓
            //   为什么放在这一页：用户遇到问题**本来就会来这儿看日志** ✓
            _traceChk = new CheckBox();
            _traceChk.Text = "记录报文";
            _traceChk.Checked = NetTrace.Enabled;
            _traceChk.AutoSize = true;
            _traceChk.Location = new Point(370, 56);
            _traceChk.CheckedChanged += delegate (object s, EventArgs e)
            {
                NetTrace.Enabled = _traceChk.Checked;
                try
                {
                    AppSettings.RecordTrace = _traceChk.Checked;
                    AppSettings.Save();
                }
                catch (Exception) { }
                Log.Info("协议报文记录：" + (_traceChk.Checked ? "已开启" : "已关闭"));
            };
            toolbar.Controls.Add(_traceChk);

            _autoScroll = new CheckBox();
            _autoScroll.Text = "自动滚动";
            _autoScroll.Checked = true;
            _autoScroll.AutoSize = true;
            _autoScroll.Location = new Point(470, 56);
            toolbar.Controls.Add(_autoScroll);

            // 日志文件落盘位置提示
            Label fileLab = new Label();
            fileLab.Text = "遇到问题请点「导出诊断包」——日志和报文都会打包进去";
            fileLab.AutoSize = true;
            fileLab.ForeColor = Color.Gray;
            fileLab.Location = new Point(566, 57);
            toolbar.Controls.Add(fileLab);

            CardPanel body = new CardPanel();
            body.SetTitle("日志正文", "Debug 级别会显示全部细节；常规级别隐藏 [DEBUG] 行");
            body.Location = new Point(12, 118);
            body.Size = new Size(ClientSize.Width - 24, ClientSize.Height - 130);
            body.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(body);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.WordWrap = false;
            _box.BackColor = Color.FromArgb(24, 26, 32);
            _box.ForeColor = Color.FromArgb(220, 224, 232);
            _box.Font = new Font("Consolas", 9F);
            _box.Location = new Point(14, 52);
            _box.Size = new Size(body.Width - 28, body.Height - 66);
            _box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            body.Controls.Add(_box);

            // 先灌入历史
            // ★ 必须**按当前级别过滤** ✗（2026-09-13 用户指出"日志打开默认是 debug"）
            //   这里原来是 `foreach (string s in snap) sb.AppendLine(s);` ✗
            //   **没过滤，全灌进去** ✓
            //   而构造里前面那句 `_levelBox.SelectedIndex = 0`（第 54 行）
            //   会触发 OnLevelChanged ✓ 那次过滤是**对的** ✓
            //   结果第 107 行又把它**覆盖成全部** ✓✓
            //   → 症状：第一次打开时，明明选着「常规」，正文里却全是 [DEBUG] 行 ✗
            //
            //   ★ 改成调同一个方法 ✓ 两处共用一份过滤逻辑（族 2：同一个事实别写两遍 ✓）
            ReplayHistory();

            // 订阅实时日志（可能来自工作线程，需 Invoke）
            Log.OnLog += OnNewLog;

            ApplyTheme();
            Themes.Changed += OnThemeChanged;
            FormClosed += delegate(object s, FormClosedEventArgs e) { Themes.Changed -= OnThemeChanged; };
        }

        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;   // 已释放的窗体不得再着色（否则访问违例）
            if (InvokeRequired) { BeginInvoke((MethodInvoker)OnThemeChanged); return; }
            ApplyTheme();
        }

        /// <summary>日志框保持深色终端风格（不随主题变）—— 长日志更易读，也符合控制台惯例。</summary>
        private void ApplyTheme()
        {
            Themes.ApplyTo(this);
            _box.BackColor = Color.FromArgb(24, 26, 32);
            _box.ForeColor = Color.FromArgb(220, 224, 232);
        }

        /// <summary>
        /// 把级别重置成「常规（不含调试）」。
        ///
        /// ★ 为什么需要这个方法（2026-09-13 用户指出）
        ///   用户：「我发现运行日志打开默认用的是 debug，而不是用户 info 级别」
        ///
        ///   代码里 `_levelBox.SelectedIndex = 0` 是对的 ✓ 但它**只在构造时生效一次** ✗
        ///   而 MainForm 打开日志窗口时是**复用同一个实例**的：
        ///     if (_logForm == null || _logForm.IsDisposed) _logForm = new LogForm();
        ///     _logForm.Show(this);          // ← 复用 ✗
        ///   所以：用户切到 Debug ✓ 关掉窗口 ✓ 实例还在 ✓ 再打开还是 Debug ✗
        ///
        ///   现在每次打开前调一下这个方法 ✓
        ///   想临时看 Debug 的话当场切 ✓ **不影响下次打开** ✓
        ///   （日志默认应该给"用户级别" ✓ Debug 是排障用的 ✓
        ///     一打开就是满屏 Ollama 请求/返回，真正的 WARN 会被淹掉 ✗）
        /// </summary>
        public void ResetLevelToNormal()
        {
            try
            {
                if (_levelBox != null && _levelBox.SelectedIndex != 0)
                    _levelBox.SelectedIndex = 0;      // 会触发 OnLevelChanged ✓ 自动重放历史
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 按**当前级别**重放历史日志。
        ///
        /// ★ 为什么抽成方法（2026-09-13）
        ///   构造函数和 OnLevelChanged 里**各写了一遍过滤逻辑** ✗
        ///   而构造函数里那份**忘了过滤** ✓ 结果第一次打开时，
        ///   明明选着「常规」，正文里却全是 [DEBUG] 行 ✗
        ///   → 抽成一个方法 ✓ 两处共用 ✓（族 2：同一个事实别写两遍）
        /// </summary>
        private void ReplayHistory()
        {
            try
            {
                if (_box == null) return;
                List<string> snap = Log.Snapshot();
                StringBuilder sb = new StringBuilder();
                foreach (string s in snap)
                {
                    if (_showDebug || s.IndexOf(" [DEBUG] ", StringComparison.Ordinal) < 0)
                        sb.AppendLine(s);
                }
                _box.Text = sb.ToString();
                ScrollToEnd();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 「导出诊断包」：收集排障信息 → 写一个文件 → 告诉用户发哪个文件。
        ///
        /// ★ 为什么要把"结果路径"显示出来 ✗ 而不是默默写完了事（2026-09-14）：
        ///   用户要的动作是"**把这个文件发给我**" ✓
        ///   那他就必须知道**是哪个文件、在哪儿** ✓
        ///   所以：弹窗里给完整路径 ✓ 再问一句要不要直接打开那个目录 ✓
        ///
        /// ★ 为什么"导出"这个动作本身也要记日志 ✓：
        ///   导出的那一刻也是"现场"的一部分 ✓（用户点了几次、什么时候点的 ✓）
        /// </summary>
        private void OnExportDiag(object sender, EventArgs e)
        {
            string path = "";
            try
            {
                path = Diagnostics.ExportBundle();
            }
            catch (Exception ex)
            {
                Log.Error("导出诊断包失败：" + ex.Message);
                MessageBox.Show(this,
                    "导出失败：" + ex.Message + "\n\n"
                    + "可以退而求其次：手动把下面这个文件夹里的日志发过来\n"
                    + AppPaths.LogsDir,
                    "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 顺手记一行：日志文件里有"什么时候导出的"也是一条线索
            Log.Info("用户点了「导出诊断包」→ " + path);

            string size = "";
            try { size = "（" + (new FileInfo(path).Length / 1024) + " KB）"; } catch (Exception) { }

            DialogResult r = MessageBox.Show(this,
                "诊断包已生成" + size + "：\n\n" + path + "\n\n"
                + "★ 里面**没有密码**（日志和设置都做过脱敏），可以直接发给开发者。\n"
                + "★ 内容：这台机器和运行库 / ffmpeg 与 ffprobe 的退出码 /\n"
                + "   错误与警告摘录 / 完整日志（含 DEBUG）/ 当前设置 / 程序目录清单。\n\n"
                + "要现在打开它所在的文件夹吗？",
                "导出诊断包", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch (Exception ex2)
                {
                    Log.Warn("打开诊断包所在目录失败：" + ex2.Message);
                }
            }
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            _showDebug = _levelBox.SelectedIndex == 1;
            ReplayHistory();
        }

        private void OnNewLog(string line)
        {
            try
            {
                if (_box.IsDisposed || _box.Disposing) return;
                _box.BeginInvoke((MethodInvoker)delegate
                {
                    if (_showDebug || line.IndexOf(" [DEBUG] ", StringComparison.Ordinal) < 0)
                    {
                        lock (_uiLock)
                        {
                            _box.AppendText(line + Environment.NewLine);
                            if (_box.Lines.Length > 2000)
                            {
                                // 截断到最近 1500 行，避免卡顿
                                int cut = _box.Lines.Length - 1500;
                                _box.Select(0, _box.GetFirstCharIndexFromLine(cut));
                                _box.SelectedText = "";
                            }
                        }
                    }
                    if (_autoScroll.Checked) ScrollToEnd();
                });
            }
            catch { }
        }

        private void ScrollToEnd()
        {
            _box.SelectionStart = _box.Text.Length;
            _box.ScrollToCaret();
        }
    }
}
