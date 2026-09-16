/* -*- coding: utf-8 -*-
 * ContentDetailForm.cs — AI 理解内容放大查看窗口
 * 大窗口 + 大字号列表，双击行可定位主窗口画面；字号可调。C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VideoChecker
{
    public class ContentDetailForm : Form
    {
        private DataGridView _grid;
        private int _fontSize = 13;
        private readonly Action<double> _locate;

        public ContentDetailForm(List<ContentNote> notes, Action<double> locate)
        {

            AppInfo.SetFormIcon(this);
            _locate = locate;
            Text = "AI 理解内容（放大查看） — 视频核对工具";
            Font = new Font("Microsoft YaHei", 12F);
            ClientSize = new Size(1200, 820);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 246, 250);

            // 顶部工具条
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 44;
            top.BackColor = Color.FromArgb(238, 241, 245);
            Controls.Add(top);

            Label tip = new Label();
            tip.Text = "AI 理解内容（共 " + notes.Count + " 条）— 双击行可回到主窗口定位画面，拖拽窗口可放大";
            tip.Font = new Font("Microsoft YaHei", 10F);
            tip.ForeColor = Color.Gray;
            tip.AutoSize = true;
            tip.Location = new Point(12, 13);
            top.Controls.Add(tip);

            Button minus = new RoundButton();
            minus.Text = "字号-";
            minus.Font = new Font("Microsoft YaHei", 9F);
            minus.Size = new Size(64, 28);
            minus.Location = new Point(700, 8);
            minus.Click += delegate(object s, EventArgs e) { ChangeFont(-1); };
            top.Controls.Add(minus);

            Button plus = new RoundButton();
            plus.Text = "字号+";
            plus.Font = new Font("Microsoft YaHei", 9F);
            plus.Size = new Size(64, 28);
            plus.Location = new Point(770, 8);
            plus.Click += delegate(object s, EventArgs e) { ChangeFont(1); };
            top.Controls.Add(plus);

            Button close = new RoundButton();
            close.Text = "关闭";
            close.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            close.BackColor = Color.FromArgb(220, 38, 38);
            close.ForeColor = Color.White;
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderSize = 0;
            close.Size = new Size(80, 28);
            close.Location = new Point(1100, 8);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Click += delegate(object s, EventArgs e) { Close(); };
            top.Controls.Add(close);

            // 大字号列表
            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.Columns.Add("cTime", "位置");
            _grid.Columns.Add("cOsd", "画面时间");
            _grid.Columns.Add("cStatus", "判定");
            _grid.Columns.Add("cText", "AI 理解内容");
            _grid.Columns[0].Width = 90;
            _grid.Columns[1].Width = 210;
            _grid.Columns[2].Width = 80;
            _grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _grid.Columns[3].DefaultCellStyle.WrapMode = DataGridViewTriState.True;   // 内容列自动换行，长文本完整显示
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;     // 行高随内容自动扩展（只算可见行，性能好）
            _grid.CellDoubleClick += OnJump;
            Controls.Add(_grid);
            // 关键：Fill 控件必须最后 Add / 提到最前，才会停在 Top 面板下方。
            // 否则停靠顺序反了，顶部工具条会压住表格第一行（第 0s 那条）。
            _grid.BringToFront();

            foreach (ContentNote n in notes)
            {
                int idx = _grid.Rows.Add(n.Time.ToString("0.#") + "s", n.Osd, StatusZh(n.Status), n.Text);
                _grid.Rows[idx].DefaultCellStyle.ForeColor = StatusColor(n.Status);
                _grid.Rows[idx].Tag = n;
            }

            ApplyFont();
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

        private void ApplyTheme()
        {
            Themes.ApplyTo(this);
            foreach (DataGridViewRow r in _grid.Rows)
            {
                ContentNote n = r.Tag as ContentNote;
                if (n != null) r.DefaultCellStyle.ForeColor = Themes.StatusColor(n.Status);
            }
        }

        private void ChangeFont(int d)
        {
            _fontSize += d;
            if (_fontSize < 10) _fontSize = 10;
            if (_fontSize > 22) _fontSize = 22;
            ApplyFont();
        }

        private void ApplyFont()
        {
            _grid.DefaultCellStyle.Font = new Font("Microsoft YaHei", _fontSize);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", _fontSize, FontStyle.Bold);
            _grid.RowTemplate.Height = (int)(_fontSize * 2.2);
        }

        private void OnJump(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            ContentNote n = _grid.Rows[e.RowIndex].Tag as ContentNote;
            if (n != null && _locate != null)
            {
                try { _locate(n.Time); } catch (Exception) { }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Dispose();
        }

        private static Color StatusColor(string s)
        {
            return Themes.StatusColor(s);
        }

        private static string StatusZh(string s)
        {
            if (s == "PASS") return "通过";
            if (s == "WARN") return "一般";
            if (s == "FAIL") return "失败";
            return "无法分析";
        }
    }
}
