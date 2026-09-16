/* -*- coding: utf-8 -*-
 * PasswordChangeForm.cs — 开屏密码对话框（三种模式：首次设置 / 修改 / 取消密码）
 * C# 5 兼容语法。
 */
using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoChecker
{
    public class PasswordChangeForm : Form
    {
        public enum Mode { Set, Change, Clear }

        private Mode _mode;
        private TextBox _old, _new1, _new2;

        /// <summary>mode：Set=首次设置（不要原密码）/ Change=修改 / Clear=取消密码。</summary>
        public PasswordChangeForm(Mode mode)
        {
            AppInfo.SetFormIcon(this);
            _mode = mode;
            Text = mode == Mode.Set ? "设置开屏密码"
                 : (mode == Mode.Change ? "修改开屏密码" : "取消开屏密码");
            Font = new Font("Microsoft YaHei", 9F);
            ClientSize = new Size(440, mode == Mode.Clear ? 170 : 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            int y = 18;
            if (mode != Mode.Set)
            {
                Mk("原密码：", y); _old = MkBox(y); y += 42;
            }
            if (mode != Mode.Clear)
            {
                Mk(mode == Mode.Set ? "设置密码：" : "新密码：", y); _new1 = MkBox(y); y += 42;
                Mk("确认密码：", y); _new2 = MkBox(y); y += 40;
            }

            Label tip = new Label();
            tip.Text = BuildTip();
            tip.Font = new Font("Microsoft YaHei", 8.5F);
            tip.ForeColor = Color.Gray;
            tip.AutoSize = false;
            tip.Size = new Size(ClientSize.Width - 32, 34);
            tip.Location = new Point(16, y);
            Controls.Add(tip);
            y += 36;

            Button ok = new RoundButton();
            ok.Text = mode == Mode.Set ? "确定设置" : (mode == Mode.Change ? "确定修改" : "确定取消密码");
            ok.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            ok.Location = new Point(ClientSize.Width - 210, y); ok.Size = new Size(106, 30);
            ok.Click += OnOk;
            Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = new RoundButton();
            cancel.Text = "返回"; cancel.Font = Font;
            cancel.Location = new Point(ClientSize.Width - 96, y); cancel.Size = new Size(80, 30);
            cancel.Click += delegate(object s, EventArgs e) { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;

            Load += delegate(object s, EventArgs e)
            {
                Themes.ApplyTo(this);
                Themes.StylePrimary(ok);
                if (_old != null) _old.Focus();
                else if (_new1 != null) _new1.Focus();
            };
        }

        private string BuildTip()
        {
            if (_mode == Mode.Clear)
                return "取消后打开程序将直接进入，不再要求密码。随时可以在设置页重新设置。";
            // 这里刻意不写任何"忘记了怎么办"的说明：
            // 界面文字会被所有使用本程序的人看到，把恢复方法写在界面上等于把钥匙挂在锁上。
            // 恢复方法只记录在 docs\ 下的说明文件里（不随程序分发）。
            return "密码至少 4 位，建议使用不容易被猜到的组合。";
        }

        private void Mk(string text, int y)
        {
            Label l = new Label();
            l.Text = text; l.Font = Font; l.AutoSize = true;
            l.Location = new Point(16, y + 4);
            Controls.Add(l);
        }

        private TextBox MkBox(int y)
        {
            TextBox t = new TextBox();
            t.Font = new Font("Microsoft YaHei", 10F);
            t.UseSystemPasswordChar = true;
            t.Location = new Point(120, y);
            t.Size = new Size(ClientSize.Width - 140, 26);
            Controls.Add(t);
            return t;
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (PassLock.IsLocked)
            {
                MessageBox.Show(this, "密码锁当前处于锁定状态（等待 " + PassLock.LockRemaining() + "），稍后再试。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string err;
            if (_mode == Mode.Set)
                err = PassLock.SetPassword(_new1.Text, _new2.Text);
            else if (_mode == Mode.Change)
                err = PassLock.ChangePassword(_old.Text, _new1.Text, _new2.Text);
            else
                err = PassLock.ClearPassword(_old.Text);

            if (err.Length > 0)
            {
                MessageBox.Show(this, err, "未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // 原密码错误也计一次失败，防止从这里反复试密码
                if (err == "原密码不正确" || err == "密码不正确") PassLock.RecordFailure();
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
