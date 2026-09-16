/* -*- coding: utf-8 -*-
 * EventLogForm.cs — 事件流水小窗
 *
 * 为什么单独开一个窗（2026-09-13 用户要求）：
 *   主界面的「告警记录」是给"要看的结论"用的 ✓
 *   但摄像头的报警流本身是有价值的**证据**：它证明链路通不通、推得勤不勤 ✓
 *   videoloss 每 10 秒一条 ✓ 全堆在主记录里就把真正的告警挤到看不见了 ✗
 *
 *   所以：主记录只留【触发 / AI 结论】✓
 *         所有原始事件（含心跳）进这个小窗 ✓ 想看的时候打开，不看就关掉 ✓
 *
 * C# 5 兼容语法。
 */

using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace VideoChecker
{
    public class EventLogForm : Form
    {
        private TextBox _box;
        private CheckBox _top;
        private Label _count;
        private RoundButton _clearBtn, _copyBtn;
        private int _lines;
        private bool _placeholder;      // 当前显示的是不是"空状态提示"（不是真实事件）
        private const int MaxLines = 3000;      // 上限：太多会拖慢 TextBox

        public EventLogForm()
        {
            AppInfo.SetFormIcon(this);
            Text = "事件流水 - " + AppInfo.Title;
            Font = new Font("Microsoft YaHei", 9F);
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(560, 380);
            MinimumSize = new Size(380, 220);
            ShowInTaskbar = false;
            MaximizeBox = false;

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.WordWrap = false;                 // 一行一条，便于对齐看时间
            _box.Font = new Font("Consolas", 8.5F);
            _box.BackColor = Color.FromArgb(24, 26, 30);
            _box.ForeColor = Color.Gainsboro;
            _box.BorderStyle = BorderStyle.None;
            // ★ 别让它抢焦点（2026-09-13 修）
            //   WinForms 的 ReadOnly 文本框**一获得焦点就全选** ✗
            //   用户打开窗口看到"整片蓝色" ✓ 以为出问题了 ✓
            _box.TabStop = false;
            _box.ShortcutsEnabled = true;      // 还能 Ctrl+C 复制
            _box.Location = new Point(8, 8);
            _box.Size = new Size(ClientSize.Width - 16, ClientSize.Height - 48);
            _box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_box);

            _clearBtn = new RoundButton();
            _clearBtn.Text = "清空";
            _clearBtn.Font = new Font("Microsoft YaHei", 8.5F);
            _clearBtn.Location = new Point(8, ClientSize.Height - 34);
            _clearBtn.Size = new Size(64, 26);
            _clearBtn.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _clearBtn.Click += delegate (object s, EventArgs e) { _box.Clear(); _lines = 0; _placeholder = false; UpdateCount(); };
            Controls.Add(_clearBtn);

            _copyBtn = new RoundButton();
            _copyBtn.Text = "复制全部";
            _copyBtn.Font = new Font("Microsoft YaHei", 8.5F);
            _copyBtn.Location = new Point(80, ClientSize.Height - 34);
            _copyBtn.Size = new Size(80, 26);
            _copyBtn.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _copyBtn.Click += delegate (object s, EventArgs e)
            {
                try { if (_box.Text.Length > 0) Clipboard.SetText(_box.Text); } catch (Exception) { }
            };
            Controls.Add(_copyBtn);

            _top = new CheckBox();
            _top.Text = "窗口置顶";
            _top.Font = new Font("Microsoft YaHei", 8.5F);
            _top.AutoSize = true;
            _top.Location = new Point(174, ClientSize.Height - 31);
            _top.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _top.CheckedChanged += delegate (object s, EventArgs e) { TopMost = _top.Checked; };
            Controls.Add(_top);

            _count = new Label();
            _count.Font = new Font("Microsoft YaHei", 8.5F);
            _count.AutoSize = true;
            _count.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _count.Location = new Point(ClientSize.Width - 130, ClientSize.Height - 27);
            _count.Text = "0 条";
            Controls.Add(_count);

            Resize += delegate (object s, EventArgs e)
            {
                _box.Size = new Size(ClientSize.Width - 16, ClientSize.Height - 48);
                _clearBtn.Location = new Point(8, ClientSize.Height - 34);
                _copyBtn.Location = new Point(80, ClientSize.Height - 34);
                _top.Location = new Point(174, ClientSize.Height - 31);
                _count.Location = new Point(ClientSize.Width - 130, ClientSize.Height - 27);
            };

            // ★ 空状态提示（2026-09-13 加）
            //   用户反馈："事件流水干嘛用的？一点文字都没有" ✗
            //   窗口空着的时候必须说明自己是干什么的 ✓ 否则打开的人一头雾水 ✓
            ShowPlaceholder();
            // 显示之后把选中清掉（构造期间设的 Text 会让光标落在末尾）
            Shown += delegate (object s, EventArgs e)
            {
                try { _box.SelectionStart = 0; _box.SelectionLength = 0; } catch (Exception) { }
            };

            Themes.Changed += OnThemeChanged;
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                try { Themes.Changed -= OnThemeChanged; } catch (Exception) { }
            };
        }

        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke((MethodInvoker)OnThemeChanged); } catch (Exception) { } return; }
            try { BackColor = Themes.Current.Bg; } catch (Exception) { }
        }

        /// <summary>
        /// 显示空状态提示。窗口还没收到任何事件时用它说明自己是干什么的。
        /// （用户截图反馈："事件流水干嘛用的？一点文字都没有"）
        /// </summary>
        private void ShowPlaceholder()
        {
            try
            {
                _box.Text =
                    "  （暂无事件）" + "\r\n"
                  + "\r\n"
                  + "  这个小窗显示的是 「AI 实时巡检」页收到的全部原始事件 ——\r\n"
                  + "  包括「心跳」（摄像头每 10 秒报一次的状态），主记录里不显示它们。\r\n"
                  + "\r\n"
                  + "  怎么让它有内容：" + "\r\n"
                  + "    1. 回到主界面，进「AI 实时巡检」页" + "\r\n"
                  + "    2. 选好摄像头地址，点「开始巡检」" + "\r\n"
                  + "    3. 摄像头推来的事件就会实时出现在这里" + "\r\n"
                  + "\r\n"
                  + "  它有什么用：" + "\r\n"
                  + "    · 看链路通不通 —— 心跳一直在跳，说明摄像头和你通着" + "\r\n"
                  + "    · 看摄像头在推什么 —— 事件类型、频率、有没有异常" + "\r\n"
                  + "    · 排障 —— 收不到事件时，看是没连上还是真的没动静" + "\r\n";
                _placeholder = true;
                _lines = 0;
                UpdateCount();
            }
            catch (Exception) { }
        }

        /// <summary>追加一行（**任何线程都能调** ✓ 自己会切线程）。</summary>
        public void Append(string tag, string text, Color color)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try
                {
                    if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate () { Append(tag, text, color); });
                }
                catch (Exception) { }
                return;
            }
            try
            {
                // ★ 第一条真实事件到来时，清掉空状态提示（2026-09-13 加）
                //   用户截图反馈："事件流水干嘛用的？一点文字都没有" ✗
                //   空着的时候什么都不说 ✓ 打开的人根本不知道这窗口是干什么的 ✓
                if (_placeholder)
                {
                    _box.Clear();
                    _placeholder = false;
                    _lines = 0;
                }
                if (_lines >= MaxLines)
                {
                    // 超出上限就砍掉前一半，避免 TextBox 越来越卡
                    string all = _box.Text;
                    int cut = all.IndexOf('\n', all.Length / 2);
                    if (cut > 0) _box.Text = all.Substring(cut + 1);
                    _lines = _box.Lines.Length;
                }
                _box.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + tag.PadRight(4) + "  " + text + "\r\n");
                _lines++;
                _box.SelectionStart = _box.TextLength;
                _box.SelectionLength = 0;          // ★ 别留选中（不然是一片蓝）
                _box.ScrollToCaret();
                UpdateCount();
            }
            catch (Exception) { }
        }

        private void UpdateCount()
        {
            try { _count.Text = _lines + " 条"; } catch (Exception) { }
        }
    }
}
