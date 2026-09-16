/* -*- coding: utf-8 -*-
 * StorageForm.cs — 存储管理：查看 reports / archive / logs 的占用，按需清理
 *
 * 背景：「归档分类」会把检测过的视频按结果复制到 archive\ 下，只增不减 ——
 * 监控视频动辄几百 MB，用不了多久就把磁盘吃满，而程序里没有任何地方能看到它占了多少。
 *
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace VideoChecker
{
    public class StorageForm : Form
    {
        /// <summary>一个存储区（目录）的描述。</summary>
        private class Area
        {
            public string Name;         // 显示名
            public string Dir;          // 完整路径
            public string Filter;       // 文件通配（null = 全部）
            public string Tip;          // 说明
            public long Bytes;
            public int Count;
            public Label Info;
        }

        private List<Area> _areas = new List<Area>();
        private Label _total;

        public StorageForm()
        {

            AppInfo.SetFormIcon(this);
            Text = "存储管理 — " + AppInfo.Title;
            Font = new Font("Microsoft YaHei", 9F);
            ClientSize = new Size(620, 380);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            Label title = new Label();
            title.Text = "存储管理";
            title.Font = new Font("Microsoft YaHei", 13F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 16);
            Controls.Add(title);

            Label pathLab = new Label();
            pathLab.Text = "程序目录：" + baseDir;
            pathLab.Font = new Font("Microsoft YaHei", 8.5F);
            pathLab.ForeColor = Color.Gray;
            pathLab.AutoSize = true;
            pathLab.Location = new Point(22, 48);
            Controls.Add(pathLab);

            _total = new Label();
            _total.Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold);
            _total.AutoSize = true;
            _total.Location = new Point(20, 74);
            Controls.Add(_total);

            // 三个存储区
            _areas.Add(MkArea("检测报告", Path.Combine(baseDir, "reports"), null,
                "每次检测生成的 HTML + PDF，删掉不影响程序运行"));
            _areas.Add(MkArea("归档文件", Path.Combine(baseDir, "archive"), null,
                "「归档分类」复制过来的视频，最占空间，建议定期清理"));
            _areas.Add(MkArea("运行日志", Path.Combine(baseDir, "logs"), null,
                "排障用；error.log 记录未处理异常，建议保留"));

            int y = 106;
            foreach (Area a in _areas)
            {
                Controls.Add(MkAreaRow(a, y));
                y += 66;
            }

            Button openDir = new RoundButton();
            openDir.Text = "打开程序目录";
            openDir.Font = Font;
            openDir.Location = new Point(20, ClientSize.Height - 46);
            openDir.Size = new Size(130, 32);
            openDir.Click += delegate(object s, EventArgs e)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", baseDir); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            };
            Controls.Add(openDir);

            Button close = new RoundButton();
            close.Text = "关闭";
            close.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            close.Location = new Point(ClientSize.Width - 106, ClientSize.Height - 46);
            close.Size = new Size(86, 32);
            close.Click += delegate(object s, EventArgs e) { Close(); };
            Controls.Add(close);
            CancelButton = close;

            Load += delegate(object s, EventArgs e)
            {
                Themes.ApplyTo(this);
                Themes.StylePrimary(close);
                Refresh2();
            };
        }

        private Area MkArea(string name, string dir, string filter, string tip)
        {
            Area a = new Area();
            a.Name = name; a.Dir = dir; a.Filter = filter; a.Tip = tip;
            return a;
        }

        private Control MkAreaRow(Area a, int y)
        {
            Panel p = new Panel();
            p.Location = new Point(16, y);
            p.Size = new Size(ClientSize.Width - 32, 60);

            Label name = new Label();
            name.Text = a.Name + "\\";
            name.Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold);
            name.AutoSize = true;
            name.Location = new Point(4, 2);
            p.Controls.Add(name);

            // 文件数/大小和名称同一行，说明文字放下面一行 ——
            // 早先把说明放在右侧、按钮也在右侧，两者直接叠在一起了。
            a.Info = new Label();
            a.Info.Font = new Font("Microsoft YaHei", 8.5F);
            a.Info.ForeColor = Color.Gray;
            a.Info.AutoSize = true;
            a.Info.Location = new Point(120, 5);
            p.Controls.Add(a.Info);

            Label tip = new Label();
            tip.Text = a.Tip;
            tip.Font = new Font("Microsoft YaHei", 8F);
            tip.ForeColor = Color.Silver;
            tip.AutoSize = true;
            tip.Location = new Point(4, 32);
            p.Controls.Add(tip);

            Button clean = new RoundButton();
            clean.Text = "清理…";
            clean.Font = Font;
            clean.Location = new Point(p.Width - 96, 14);
            clean.Size = new Size(88, 30);
            clean.Tag = a;
            clean.Click += OnClean;
            p.Controls.Add(clean);

            return p;
        }

        /// <summary>重新统计各目录占用。</summary>
        private void Refresh2()
        {
            long total = 0;
            int totalCount = 0;
            foreach (Area a in _areas)
            {
                a.Bytes = 0; a.Count = 0;
                try
                {
                    if (Directory.Exists(a.Dir))
                    {
                        foreach (string f in Directory.GetFiles(a.Dir, "*", SearchOption.AllDirectories))
                        {
                            try { a.Bytes += new FileInfo(f).Length; a.Count++; } catch { }
                        }
                    }
                }
                catch (Exception ex) { Log.Debug("统计 " + a.Name + " 失败：" + ex.Message); }

                total += a.Bytes; totalCount += a.Count;
                a.Info.Text = string.Format("{0} 个文件　{1}", a.Count, Human(a.Bytes));
            }
            _total.Text = string.Format("占用合计：{0}（{1} 个文件）", Human(total), totalCount);
        }

        /// <summary>格式化为易读的大小。</summary>
        public static string Human(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        private void OnClean(object sender, EventArgs e)
        {
            Area a = (Area)((Button)sender).Tag;
            if (!Directory.Exists(a.Dir)) { MessageBox.Show(this, a.Name + " 目录不存在。"); return; }

            List<string> files = new List<string>(Directory.GetFiles(a.Dir, "*", SearchOption.AllDirectories));
            if (files.Count == 0) { MessageBox.Show(this, a.Name + " 目录已经是空的。", "提示"); return; }

            using (CleanDialog dlg = new CleanDialog(a.Name, files.Count, a.Bytes))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int keep = dlg.KeepCount;
                bool byAge = dlg.ByAge;

                // 按时间从新到旧排序，保留前 keep 个
                files.Sort(delegate(string x, string y)
                {
                    DateTime a1 = SafeTime(x), b1 = SafeTime(y);
                    return b1.CompareTo(a1);
                });

                int deleted = 0; long freed = 0;
                for (int i = keep; i < files.Count; i++)
                {
                    try
                    {
                        long sz = new FileInfo(files[i]).Length;
                        File.Delete(files[i]);
                        deleted++; freed += sz;
                    }
                    catch (Exception ex) { Log.Debug("删除失败 " + files[i] + "：" + ex.Message); }
                }

                // 顺便清掉遗留的空目录
                try
                {
                    foreach (string d in Directory.GetDirectories(a.Dir, "*", SearchOption.AllDirectories))
                        if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d, false);
                }
                catch { }

                Log.Info(string.Format("清理{0}：删除 {1} 个文件，释放 {2}", a.Name, deleted, Human(freed)));
                MessageBox.Show(this, string.Format("已删除 {0} 个文件，释放 {1}。\n保留最近 {2} 个。",
                    deleted, Human(freed), keep), "清理完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Refresh2();
            }
        }

        private static DateTime SafeTime(string path)
        {
            try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
        }
    }

    /// <summary>清理确认对话框：选择保留多少个。</summary>
    internal class CleanDialog : Form
    {
        public int KeepCount = 10;
        public bool ByAge = false;

        private NumericUpDown _num;

        public CleanDialog(string areaName, int total, long bytes)
        {
            AppInfo.SetFormIcon(this);   // 标题栏和任务栏都用程序图标
            Text = "清理 " + areaName;
            Font = new Font("Microsoft YaHei", 9F);
            ClientSize = new Size(420, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            Label info = new Label();
            info.Text = string.Format("{0} 现有 {1} 个文件，共 {2}。", areaName, total, StorageForm.Human(bytes));
            info.Font = Font;
            info.AutoSize = true;
            info.Location = new Point(20, 20);
            Controls.Add(info);

            Label ask = new Label();
            ask.Text = "保留最近的：";
            ask.Font = Font;
            ask.AutoSize = true;
            ask.Location = new Point(20, 60);
            Controls.Add(ask);

            _num = new NumericUpDown();
            _num.Minimum = 0;
            _num.Maximum = Math.Max(1, total);
            _num.Value = Math.Min(10, total);
            _num.Font = Font;
            _num.Location = new Point(120, 56);
            _num.Size = new Size(70, 24);
            Controls.Add(_num);

            Label unit = new Label();
            unit.Text = "个（0 = 全部删除）";
            unit.Font = new Font("Microsoft YaHei", 8.5F);
            unit.ForeColor = Color.Gray;
            unit.AutoSize = true;
            unit.Location = new Point(198, 60);
            Controls.Add(unit);

            Label warn = new Label();
            warn.Text = "按文件修改时间排序，较早的会被删除，且不进回收站。";
            warn.Font = new Font("Microsoft YaHei", 8.5F);
            warn.ForeColor = Color.FromArgb(200, 120, 20);
            warn.AutoSize = true;
            warn.Location = new Point(20, 100);
            Controls.Add(warn);

            Button ok = new RoundButton();
            ok.Text = "开始清理";
            ok.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            ok.Location = new Point(ClientSize.Width - 206, 150);
            ok.Size = new Size(100, 32);
            ok.Click += delegate(object s, EventArgs e)
            {
                KeepCount = (int)_num.Value;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Font = Font;
            cancel.Location = new Point(ClientSize.Width - 98, 150);
            cancel.Size = new Size(78, 32);
            cancel.Click += delegate(object s, EventArgs e) { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;

            Load += delegate(object s, EventArgs e)
            {
                Themes.ApplyTo(this);
                Themes.StylePrimary(ok);
            };
        }
    }
}
