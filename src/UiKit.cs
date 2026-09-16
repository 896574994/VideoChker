/* -*- coding: utf-8 -*-
 * UiKit.cs — 现代卡片式界面控件集
 *
 * WinForms 的系统控件改不了圆角（TextBox/ProgressBar/CheckBox 都是系统绘制的），
 * 所以要做参考图那种圆角卡片风格，必须自绘。本文件提供：
 *   CardPanel    圆角卡片容器（可选标题/副标题）
 *   FlatProgress 圆角进度条（替代系统 ProgressBar —— 它连颜色都改不了）
 *   FieldBox     圆角输入框容器（内部放无边框 TextBox）
 *   IconRail     左侧图标导航栏（自绘矢量图标 + 选中高亮）
 *   RoundCheck   圆角勾选框（自绘，方形圆角 + 对勾）
 *   Pill         药丸标签（状态徽章）
 *
 * 全部颜色取自 Themes.Current，跟随主题切换。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace VideoChecker
{
    /// <summary>矢量图标种类（自绘，不依赖图片资源）。</summary>
    public enum Glyph
    {
        Video,      // 本地视频：圆角框 + 播放三角
        Stream,     // 实时流：信号塔
        Search,     // 文搜：放大镜
        Live,       // AI 实时巡检：眼睛（盯着看）
        Log,        // 日志：横线
        Settings,   // 设置：齿轮
        Chart,      // 结果：柱状
        Help        // 帮助：问号圆
    }

    internal static class Draw
    {
        /// <summary>圆角矩形路径。</summary>
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>画矢量图标（方法名不能叫 Glyph，会和上面的枚举同名导致 CS0119）。</summary>
        public static void Icon(Graphics g, Glyph kind, Rectangle r, Color c)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Math.Max(1.6f, r.Width / 12f);
            using (Pen p = new Pen(c, w))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
                float s = Math.Min(r.Width, r.Height);
                switch (kind)
                {
                    case Glyph.Video:
                        {
                            Rectangle fr = new Rectangle(r.X, r.Y + (int)(s * 0.10f), r.Width, (int)(s * 0.80f));
                            using (GraphicsPath gp = Round(fr, (int)(s * 0.18f))) g.DrawPath(p, gp);
                            using (SolidBrush b = new SolidBrush(c))
                            {
                                float t = s * 0.20f;
                                PointF[] tri = new PointF[] {
                                    new PointF(cx - t * 0.45f, cy - t),
                                    new PointF(cx - t * 0.45f, cy + t),
                                    new PointF(cx + t * 0.70f, cy) };
                                g.FillPolygon(b, tri);
                            }
                        }
                        break;
                    case Glyph.Stream:
                        {
                            // 信号塔：底座方框 + 上方两道弧
                            g.DrawLine(p, cx, cy + s * 0.10f, cx, cy + s * 0.42f);
                            using (SolidBrush b = new SolidBrush(c))
                                g.FillEllipse(b, cx - s * 0.09f, cy + s * 0.02f, s * 0.18f, s * 0.18f);
                            g.DrawArc(p, cx - s * 0.26f, cy - s * 0.18f, s * 0.52f, s * 0.36f, 200, 140);
                            g.DrawArc(p, cx - s * 0.44f, cy - s * 0.34f, s * 0.88f, s * 0.62f, 205, 130);
                        }
                        break;
                    case Glyph.Search:
                        {
                            float rr = s * 0.30f;
                            g.DrawEllipse(p, cx - rr * 1.15f, cy - rr * 1.25f, rr * 2f, rr * 2f);
                            g.DrawLine(p, cx + rr * 0.35f, cy + rr * 0.30f, cx + rr * 1.30f, cy + rr * 1.25f);
                        }
                        break;
                    case Glyph.Live:
                        {
                            // 眼睛：上下两道弧 + 中间瞳孔（"盯着看" = 实时巡检）
                            float ew = s * 0.46f, eh = s * 0.30f;
                            g.DrawArc(p, cx - ew, cy - eh, ew * 2, eh * 2, 200, 140);   // 上眼皮
                            g.DrawArc(p, cx - ew, cy - eh, ew * 2, eh * 2, 20, 140);    // 下眼皮
                            using (SolidBrush b = new SolidBrush(c))
                                g.FillEllipse(b, cx - s * 0.11f, cy - s * 0.11f, s * 0.22f, s * 0.22f);
                        }
                        break;
                    case Glyph.Log:
                        {
                            for (int i = -1; i <= 1; i++)
                            {
                                float y = cy + i * s * 0.22f;
                                float len = (i == 1) ? s * 0.22f : s * 0.36f;
                                g.DrawLine(p, cx - s * 0.34f, y, cx - s * 0.34f + len * 2f, y);
                            }
                        }
                        break;
                    case Glyph.Settings:
                        {
                            float ro = s * 0.30f;
                            g.DrawEllipse(p, cx - ro, cy - ro, ro * 2f, ro * 2f);
                            float ri = s * 0.10f;
                            g.DrawEllipse(p, cx - ri, cy - ri, ri * 2f, ri * 2f);
                            for (int i = 0; i < 6; i++)
                            {
                                double a = Math.PI / 3 * i;
                                float x1 = cx + (float)Math.Cos(a) * ro, y1 = cy + (float)Math.Sin(a) * ro;
                                float x2 = cx + (float)Math.Cos(a) * (ro + s * 0.10f), y2 = cy + (float)Math.Sin(a) * (ro + s * 0.10f);
                                g.DrawLine(p, x1, y1, x2, y2);
                            }
                        }
                        break;
                    case Glyph.Chart:
                        {
                            g.DrawLine(p, cx - s * 0.34f, cy + s * 0.34f, cx + s * 0.34f, cy + s * 0.34f);
                            g.DrawLine(p, cx - s * 0.22f, cy + s * 0.34f, cx - s * 0.22f, cy - s * 0.02f);
                            g.DrawLine(p, cx, cy + s * 0.34f, cx, cy - s * 0.26f);
                            g.DrawLine(p, cx + s * 0.22f, cy + s * 0.34f, cx + s * 0.22f, cy + s * 0.10f);
                        }
                        break;
                    case Glyph.Help:
                        {
                            float rr = s * 0.40f;
                            g.DrawEllipse(p, cx - rr, cy - rr, rr * 2f, rr * 2f);
                            Font f = new Font("Segoe UI", s * 0.42f, FontStyle.Bold);
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                                using (SolidBrush b = new SolidBrush(c))
                                    g.DrawString("?", f, b, new RectangleF(cx - rr, cy - rr, rr * 2f, rr * 2f), sf);
                            }
                            f.Dispose();
                        }
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        /// <summary>画文字（统一字体与对齐）。</summary>
        /// <summary>
        /// 画文字。用 TextRenderer（GDI）并**不加任何特殊 flags** —— 这样渲染结果与
        /// WinForms 标准 Label 完全一致。
        ///
        /// 踩过的坑：加 TextFormatFlags.NoPadding 会改变字形栅格化方式，
        /// 同样是白字，自绘控件里的字会比旁边的标签明显偏粗、发糊（用户反馈"文字糊成一团"）。
        /// ClearType 下这种差异在大字号缩放后尤其刺眼。
        /// </summary>
        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, ContentAlignment align)
        {
            TextFormatFlags fl = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (align == ContentAlignment.TopLeft || align == ContentAlignment.TopCenter || align == ContentAlignment.TopRight)
                fl |= TextFormatFlags.Top;
            else if (align == ContentAlignment.BottomLeft || align == ContentAlignment.BottomCenter || align == ContentAlignment.BottomRight)
                fl |= TextFormatFlags.Bottom;
            else fl |= TextFormatFlags.VerticalCenter;

            if (align == ContentAlignment.TopLeft || align == ContentAlignment.MiddleLeft || align == ContentAlignment.BottomLeft)
                fl |= TextFormatFlags.Left;
            else if (align == ContentAlignment.TopRight || align == ContentAlignment.MiddleRight || align == ContentAlignment.BottomRight)
                fl |= TextFormatFlags.Right;
            else fl |= TextFormatFlags.HorizontalCenter;

            TextRenderer.DrawText(g, s, f, r, c, fl);
        }

        /// <summary>量文字宽度。与 Text() 使用完全相同的度量方式（都不加特殊 flags）。</summary>
        public static int Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f).Width;
        }
    }

    // ==================== 圆角卡片 ====================

    public class CardPanel : Panel
    {
        public string Title = "";
        public string Subtitle = "";
        private Label _titleLab;      // 标题用原生 Label 显示，不自绘文字
        private Label _subLab;
        public int Radius = 14;
        public bool ShowBorder = true;
        /// <summary>卡片内边距（标题下方的内容区从这里开始算）。</summary>
        public int PadX = 18;
        public int PadTop = 16;
        /// <summary>标题占用高度；为 0 时不出标题。</summary>
        public int HeaderHeight = 0;

        public CardPanel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        /// <summary>设置标题（并自动算出标题区高度）。</summary>
        /// <summary>
        /// 设置标题。用原生 Label 显示文字 —— 自绘文字（Draw.Text）在多个自绘控件共存时
        /// 会出现跨行串位（用户实测：勾选框文字里混进了别的行的字），交给系统渲染就彻底没有这个问题。
        /// </summary>
        public void SetTitle(string title, string subtitle)
        {
            Title = title; Subtitle = subtitle;
            HeaderHeight = (subtitle != null && subtitle.Length > 0) ? 52 : 34;

            if (_titleLab != null) { Controls.Remove(_titleLab); _titleLab.Dispose(); _titleLab = null; }
            if (_subLab != null) { Controls.Remove(_subLab); _subLab.Dispose(); _subLab = null; }

            _titleLab = new Label();
            _titleLab.Text = title;
            _titleLab.Font = new Font("Microsoft YaHei", 10.5F, FontStyle.Bold);
            _titleLab.AutoSize = true;
            _titleLab.BackColor = Themes.Current.Card;
            _titleLab.ForeColor = Themes.Current.Fg;
            _titleLab.Location = new Point(PadX, 12);
            Controls.Add(_titleLab);

            if (subtitle != null && subtitle.Length > 0)
            {
                _subLab = new Label();
                _subLab.Text = subtitle;
                _subLab.Font = new Font("Microsoft YaHei", 8.5F);
                // ★ 定宽 + 超长省略（2026-09-13 用户反馈"字没有换行，被遮挡了"）
                //   原来 AutoSize = true ✗ 没有宽度限制 ✓
                //   → 副标题**永远不会折行** ✓ 太长就直接伸出卡片右边被裁掉 ✓
                //   → 而且卡片下面的内容从固定 y 开始 ✓ 长副标题还会被它压住 ✓
                //   现在改成定宽 + AutoEllipsis ✓
                //   **无论副标题多长，都只占一行、都不会超出卡片** ✓✓
                _subLab.AutoSize = false;
                _subLab.AutoEllipsis = true;
                _subLab.Height = 17;
                _subLab.Width = Math.Max(60, Width - PadX * 2);
                _subLab.BackColor = Themes.Current.Card;
                _subLab.ForeColor = Themes.Current.Muted;
                _subLab.Location = new Point(PadX, 32);
                Controls.Add(_subLab);
                // 卡片变宽变窄时跟着调（不然定宽就没意义了）
                Resize += delegate (object s, EventArgs e)
                {
                    try { if (_subLab != null) _subLab.Width = Math.Max(60, Width - PadX * 2); }
                    catch (Exception) { }
                };
            }
            Invalidate();
        }

        /// <summary>
        /// 主题切换后重新给标题/副标题着色。
        ///
        /// 为什么需要这个方法（2026-09-12 修的真实 bug）：
        ///   这两个 Label 的 BackColor/ForeColor 是 SetTitle 时按"当时的主题"设的，
        ///   切主题后不会自动更新 ✗ 结果从深色主题切到浅色时，
        ///   白色卡片上留着深色底 + 浅色字 —— 用户截图反馈"文字背景色太丑"，
        ///   实测底色正是上一套主题的卡片色（#1B3126，森林主题的）。
        ///
        ///   之前想靠"值匹配"在通用的 Walk 里修 ✗ 太脆弱（主题切两次就对不上了）✓
        ///   改成让卡片自己同步 —— 确定性 ✓ 不依赖任何猜测 ✓
        /// </summary>
        public void SyncTheme()
        {
            Theme t = Themes.Current;
            if (_titleLab != null) { _titleLab.BackColor = t.Card; _titleLab.ForeColor = t.Fg; }
            if (_subLab != null) { _subLab.BackColor = t.Card; _subLab.ForeColor = t.Muted; }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Themes.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 先铺满窗体底色（含圆角外的四个角），再画圆角卡片本体 ——
            // 这样卡片是真正不透明的，不依赖 WinForms 的透明父容器重绘链。
            using (SolidBrush bg = new SolidBrush(t.Bg)) g.FillRectangle(bg, ClientRectangle);

            // 卡片底
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Draw.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(t.Card)) g.FillPath(b, gp);
                if (ShowBorder)
                    using (Pen p = new Pen(t.Border, 1f)) g.DrawPath(p, gp);
            }

            base.OnPaint(e);
        }
    }

    // ==================== 圆角进度条 ====================

    /// <summary>圆角进度条：系统 ProgressBar 连颜色都改不了，只能用自绘的替代。</summary>
    public class FlatProgress : Control
    {
        private int _value;
        private string _text = "";
        public int Radius = 6;
        public bool ShowText = true;
        /// <summary>文字显示在条内还是条右侧。</summary>
        public bool TextInside = false;

        public FlatProgress()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Height = 10;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value < 0 ? 0 : (value > 100 ? 100 : value);
                if (v != _value) { _value = v; Invalidate(); }
            }
        }

        public new string Text
        {
            get { return _text; }
            set { if (value != _text) { _text = value ?? ""; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Themes.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int barW = ShowText && !TextInside ? Width - 52 : Width;
            Rectangle track = new Rectangle(0, (Height - 8) / 2, Math.Max(1, barW), 8);
            using (GraphicsPath gp = Draw.Round(track, Radius))
            using (SolidBrush b = new SolidBrush(Mix(t.Card, t.Fg, 0.14f)))
                g.FillPath(b, gp);

            int fillW = (int)Math.Round(track.Width * (_value / 100.0));
            if (fillW > 0)
            {
                Rectangle fill = new Rectangle(track.X, track.Y, Math.Max(8, fillW), track.Height);
                using (GraphicsPath gp = Draw.Round(fill, Radius))
                {
                    if (_value > 0 && _value < 100)
                    {
                        using (LinearGradientBrush lg = new LinearGradientBrush(
                            fill, t.Accent, Mix(t.Accent, Color.White, 0.30f), LinearGradientMode.Horizontal))
                            g.FillPath(lg, gp);
                    }
                    else
                    {
                        using (SolidBrush b = new SolidBrush(t.Accent)) g.FillPath(b, gp);
                    }
                }
            }

            // 文字不由本控件绘制（自绘文字会跨行串位）；百分比由外部 Label 显示
        }

        internal static Color Mix(Color a, Color b, float k)
        {
            if (k < 0) k = 0; if (k > 1) k = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * k),
                (int)(a.G + (b.G - a.G) * k),
                (int)(a.B + (b.B - a.B) * k));
        }
    }

    // ==================== 圆角输入框容器 ====================

    /// <summary>
    /// 把无边框 TextBox 放进自绘的圆角容器里 —— 系统 TextBox 的方角改不掉，
    /// 只能让它透明无边框、由外层画出圆角底。
    /// </summary>
    /// <summary>
    /// 双缓冲面板（2026-09-12 加）。
    ///
    /// 为什么需要：Panel 默认**不做双缓冲** ✗ 每次重绘都直接画到屏幕 ✓
    /// 一旦绘制分成几步（清屏 → 贴图 → 画游标），中间状态就会被看到 → 闪烁 ✓
    /// 时间轴就是这样：用户反馈"AI 内容时间轴一闪一闪的" ✓
    /// 用 SetStyle 打开 OptimizedDoubleBuffer + AllPaintingInWmPaint 即可根治 ✓
    /// （Panel 的 DoubleBuffered 属性是 protected，外部设不了，只能自己派生一个 ✓）
    /// </summary>
    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
        }
    }
    public class FieldBox : Panel
    {
        private TextBox _box;
        private bool _focused;
        public int Radius = 10;

        public FieldBox(int height)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Height = height;

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.Font = new Font("Microsoft YaHei", 9.5F);
            _box.GotFocus += delegate(object s, EventArgs e) { _focused = true; Invalidate(); };
            _box.LostFocus += delegate(object s, EventArgs e) { _focused = false; Invalidate(); };
            _box.TextChanged += delegate(object s, EventArgs e) { if (TextChanged2 != null) TextChanged2(this, e); };
            Controls.Add(_box);
            LayoutBox();
        }

        /// <summary>文本变化转发（外部订阅这个，而不是内部的 TextBox）。</summary>
        public event EventHandler TextChanged2;

        public new string Text
        {
            get { return _box != null ? _box.Text : ""; }
            set { if (_box != null) _box.Text = value; }
        }

        public TextBox Inner { get { return _box; } }
        public bool ReadOnlyField
        {
            get { return _box.ReadOnly; }
            set { _box.ReadOnly = value; }
        }
        public bool UseSystemPasswordChar
        {
            get { return _box.UseSystemPasswordChar; }
            set { _box.UseSystemPasswordChar = value; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutBox();
        }

        private void LayoutBox()
        {
            if (_box == null) return;
            _box.BackColor = Themes.Current.Card;
            int h = _box.PreferredHeight;
            _box.SetBounds(14, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - 28), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Themes.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Draw.Round(r, Radius))
            {
                // 填充色（2026-09-12 定稿）：
                //   **直接用卡片色，只靠边框区分** —— 不分主题明暗。
                //
                //   走过的弯路：原来写的是 Mix(Card, Fg, 0.07)，往卡片色里掺 7% 文字色。
                //   · 第一次只修了浅色主题（以为深色下"提亮=凹陷感"是对的）✗
                //   · 用户又反馈深色下同样不搭 ✗ 取样发现：
                //       输入框填充 #2B3341，内部真 TextBox 背景 #1E2634（卡片色）
                //       → 亮框里套暗条，和浅色那个"白框套灰边"是同一个病，只是反着来 ✓
                //   根因是"容器填充色 ≠ 内部 TextBox 背景色"，跟明暗无关 ✓
                //   现在两者都用 t.Card，彻底一致；边框负责表现"这是个输入框" ✓
                using (SolidBrush b = new SolidBrush(t.Card)) g.FillPath(b, gp);
                using (Pen p = new Pen(_focused ? t.Accent : t.Border, _focused ? 1.6f : 1f))
                    g.DrawPath(p, gp);
            }
        }

        /// <summary>把内层 TextBox 背景色同步到当前主题（主题切换时调用）。</summary>
        public void SyncTheme()
        {
            if (_box != null) _box.BackColor = Themes.Current.Card;
            Invalidate();
        }

        internal static Color Mix(Color a, Color b, float k) { return FlatProgress.Mix(a, b, k); }
    }

    // ==================== 圆角勾选框 ====================

    /// <summary>
    /// 圆角勾选框：方框自绘（系统画不了圆角），但**文字交给标准 Label 画**。
    ///
    /// 为什么不用自绘文字：试过 Graphics.DrawString、TextRenderer（含/不含 NoPadding）、
    /// ClearTypeGridFit 等各种组合，自绘出来的字始终比旁边的标准标签偏粗发糊
    /// （用户反馈"文字糊成一团"）。交给 Label 后渲染方式与界面其它文字完全相同，问题从根上消失。
    /// </summary>
    public class RoundCheck : CheckBox
    {
        private Label _lab;

        public RoundCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Themes.Current.Card;
            Font = new Font("Microsoft YaHei", 9F);
            AutoSize = false;
            Height = 22;

            _lab = new Label();
            _lab.AutoSize = true;
            _lab.BackColor = Themes.Current.Card;
            _lab.ForeColor = Themes.Current.Fg;
            _lab.Font = new Font("Microsoft YaHei", 9F);
            _lab.Location = new Point(24, 4);
            Controls.Add(_lab);
        }

        /// <summary>文字存在子 Label 里（CheckBox.Text 不可覆写，这里用 new 隐藏）。</summary>
        public new string Text
        {
            get { return _lab == null ? "" : _lab.Text; }
            set { if (_lab != null) _lab.Text = value; }
        }

        /// <summary>主题切换后同步子 Label 的配色。</summary>
        public void SyncLabel()
        {
            if (_lab == null) return;
            _lab.BackColor = Themes.Current.Card;
            _lab.ForeColor = Enabled ? Themes.Current.Fg : Themes.Current.Muted;
            BackColor = Themes.Current.Card;
            Invalidate(true);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_lab != null) _lab.Font = Font;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (_lab != null) _lab.ForeColor = Enabled ? Themes.Current.Fg : Themes.Current.Muted;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 只画方框与对勾；文字由子 Label 负责，不在这里画
            Theme t = Themes.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int box = 16;
            int y = (Height - box) / 2;
            Rectangle r = new Rectangle(0, y, box, box);
            using (GraphicsPath gp = Draw.Round(r, 5))
            {
                if (Checked)
                {
                    using (SolidBrush b = new SolidBrush(t.Accent)) g.FillPath(b, gp);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(FlatProgress.Mix(t.Card, t.Fg, 0.10f))) g.FillPath(b, gp);
                    using (Pen p = new Pen(t.Border, 1.2f)) g.DrawPath(p, gp);
                }
            }
            if (Checked)
            {
                using (Pen p = new Pen(Color.White, 2f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new PointF[] {
                        new PointF(r.X + 3.6f, r.Y + 8.4f),
                        new PointF(r.X + 6.6f, r.Y + 11.4f),
                        new PointF(r.X + 12.4f, r.Y + 5.0f) });
                }
            }
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }
    }

    // ==================== 左侧图标导航栏 ====================

    public class RailItem
    {
        public string Key = "";
        public string Tip = "";
        public Glyph Icon = Glyph.Video;
        public bool Enabled = true;
    }

    /// <summary>左侧图标导航栏：自绘矢量图标，选中项带圆角高亮块。</summary>
    public class IconRail : Control
    {
        private readonly List<RailItem> _items = new List<RailItem>();
        private int _sel = -1;
        private int _hover = -1;
        private readonly ToolTip _tip = new ToolTip();
        public int ItemHeight = 46;
        public int TopPad = 16;

        public event EventHandler Selected;

        public IconRail()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Width = 62;
        }

        public void Add(string key, string tip, Glyph icon)
        {
            RailItem it = new RailItem();
            it.Key = key; it.Tip = tip; it.Icon = icon;
            _items.Add(it);
            if (_sel < 0) _sel = 0;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _sel; }
            set { SetSel(value, true); }
        }

        public string SelectedKey
        {
            get { return (_sel >= 0 && _sel < _items.Count) ? _items[_sel].Key : ""; }
        }

        private void SetSel(int i, bool fire)
        {
            if (i < 0 || i >= _items.Count || i == _sel) return;
            _sel = i;
            Invalidate();
            if (fire && Selected != null) Selected(this, EventArgs.Empty);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                int y = TopPad + i * ItemHeight;
                if (p.Y >= y && p.Y < y + ItemHeight) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != _hover)
            {
                _hover = h;
                _tip.SetToolTip(this, (h >= 0 && _items[h].Tip.Length > 0) ? _items[h].Tip : "");
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1; Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            SetSel(HitTest(e.Location), true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Themes.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            for (int i = 0; i < _items.Count; i++)
            {
                int y = TopPad + i * ItemHeight;
                Rectangle slot = new Rectangle(9, y + 3, Width - 18, ItemHeight - 8);
                bool sel = (i == _sel);
                bool hov = (i == _hover);

                if (sel)
                {
                    using (GraphicsPath gp = Draw.Round(slot, 12))
                    using (SolidBrush b = new SolidBrush(t.Accent))
                        g.FillPath(b, gp);
                }
                else if (hov)
                {
                    using (GraphicsPath gp = Draw.Round(slot, 12))
                    using (SolidBrush b = new SolidBrush(FlatProgress.Mix(t.Card, t.Fg, 0.10f)))
                        g.FillPath(b, gp);
                }

                Color ic = sel ? t.AccentFg : (hov ? t.Fg : t.Muted);
                int gs = 22;
                Draw.Icon(g, _items[i].Icon,
                    new Rectangle(slot.X + (slot.Width - gs) / 2, slot.Y + (slot.Height - gs) / 2, gs, gs), ic);
            }
        }

        public void SyncTheme() { Invalidate(); }
    }

    // ==================== 流式排列容器 ====================

    /// <summary>
    /// 横向流式容器：子控件按加入顺序自动排开，前一个多宽后一个就自动往后让。
    ///
    /// 用来替代「给每个控件手算 x 坐标」—— 手算坐标在字体、DPI、文字长度任何一个变化时
    /// 都可能重叠（已经踩过两次坑）。流式布局下兄弟控件由布局引擎依次摆放，
    /// 结构上不可能相交，也不用再维护坐标常量。
    /// </summary>
    public class FlowRow : FlowLayoutPanel
    {
        public FlowRow()
        {
            // 关键：这里绝不能用 BackColor = Transparent。
            // WinForms 的透明容器是靠"把父容器背景画到自己身上"实现的；FlowRow(透明) 套在
            // CardPanel(自绘) 里、再放透明的子控件，会形成链式重绘 ——
            // 表现就是子控件的文字被叠印两遍（窗口拉宽后尤其明显，实测 1920 宽时明显糊成一团）。
            // 用实心卡片色既和卡片融为一体，又彻底避开这个问题。
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            FlowDirection = FlowDirection.LeftToRight;
            WrapContents = false;          // 单行：永远排成一行（换行会顶到下面那一行去）
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0);
            Padding = new Padding(0);
            BackColor = Themes.Current.Card;
        }

        /// <summary>主题切换后同步底色（实心色，不是透明）。</summary>
        public void SyncTheme()
        {
            BackColor = Themes.Current.Card;
            Invalidate(true);
        }

        /// <summary>按顺序放入控件，控件之间留 gap 像素（末个不留）。</summary>
        public void Pack(int gap, Control[] items)
        {
            Controls.Clear();
            for (int i = 0; i < items.Length; i++)
            {
                items[i].Margin = new Padding(0, 0, i == items.Length - 1 ? 0 : gap, 0);
                Controls.Add(items[i]);
            }
        }
    }

    /// <summary>建控件的便捷方法。</summary>
    public static class Flow
    {
        /// <summary>建一个横向流式行，把 items 依次放入，行放在 parent 的 (x,y)。</summary>
        public static FlowRow Row(Control parent, int x, int y, int gap, Control[] items)
        {
            FlowRow r = new FlowRow();
            r.Location = new Point(x, y);
            r.Pack(gap, items);
            parent.Controls.Add(r);
            return r;
        }
    }

    // ==================== 药丸标签 ====================

    /// <summary>状态药丸（如「PASS」「一般」「失败」），圆角填充 + 同色系文字。</summary>
    public class Pill : Control
    {
        private string _text = "";
        private Color _color = Color.Gray;

        public Pill()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            Size = new Size(64, 22);
        }

        public void Set(string text, Color color)
        {
            _text = text ?? ""; _color = color;
            // 按文字实际宽度自适应 —— 药丸比文字窄的话文字会溢出去压到旁边
            int need = Draw.Measure(_text, PillFont());
            if (Width < need + 26) Width = need + 26;
            Invalidate();
        }

        /// <summary>药丸字体（绘制与测量必须用同一个）。</summary>
        private static Font PillFont()
        {
            return new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Draw.Round(r, Height / 2))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(38, _color))) g.FillPath(b, gp);
                using (Pen p = new Pen(Color.FromArgb(90, _color), 1f)) g.DrawPath(p, gp);
            }
            // 文字由原生 Label 承担（见下方 _lab），这里只画底与描边

        }
    }
}
