/* -*- coding: utf-8 -*-
 * RoundButton.cs — 圆角按钮（完全自绘）
 * WinForms 原生按钮只能画直角，这里用 GraphicsPath 自绘抗锯齿圆角，
 * 并支持悬停 / 按下 / 禁用三态。配色仍由 Themes.Style* 统一驱动（读 BackColor/ForeColor）。
 * C# 5 兼容语法。
 */
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VideoChecker
{
    /// <summary>圆角按钮。颜色由 Themes 设置，本类只负责绘制与状态反馈。</summary>
    public class RoundButton : Button
    {
        /// <summary>圆角半径。会自动限制为高度的一半，小按钮不会画成胶囊。</summary>
        public int Radius = 8;

        /// <summary>是否描边（主按钮通常不描边）。</summary>
        public bool UseBorder = true;

        /// <summary>描边颜色。</summary>
        public Color BorderColor = Color.Gray;

        /// <summary>悬停/按下的明暗变化幅度。</summary>
        public double HoverShift = 0.10;

        private bool _hover;
        private bool _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        /// <summary>把控件裁剪成圆角，这样四角之外的点不会被按钮接收。</summary>
        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath p = RoundedPath(new Rectangle(0, 0, Width, Height), EffRadius()))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        private int EffRadius()
        {
            int r = Radius;
            int half = Math.Min(Width, Height) / 2;
            if (r > half) r = half;
            if (r < 0) r = 0;
            return r;
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) { _hover = false; _down = false; }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color parentBg = (Parent != null) ? Parent.BackColor : BackColor;
            Color back = BackColor;
            Color fore = ForeColor;
            Color border = BorderColor;

            if (!Enabled)
            {
                // 禁用态：整体向父级底色淡出
                back = Mix(back, parentBg, 0.55);
                fore = Mix(fore, parentBg, 0.55);
                border = Mix(border, parentBg, 0.55);
            }
            else
            {
                // 深底提亮、浅底压暗 —— 保证四套主题下悬停都看得见
                double lum = (back.R * 0.299 + back.G * 0.587 + back.B * 0.114) / 255.0;
                Color toward = (lum < 0.5) ? Color.White : Color.Black;
                if (_down)
                {
                    back = Mix(back, toward, HoverShift * 1.7);
                    border = Mix(border, toward, HoverShift * 1.7);
                }
                else if (_hover)
                {
                    back = Mix(back, toward, HoverShift);
                    border = Mix(border, toward, HoverShift);
                }
            }

            // 先铺父级底色，避免圆角外残留上一帧
            using (SolidBrush pb = new SolidBrush(parentBg))
                g.FillRectangle(pb, ClientRectangle);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = RoundedPath(r, EffRadius()))
            {
                using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, path);
                if (UseBorder)
                {
                    using (Pen p = new Pen(border, 1f)) g.DrawPath(p, path);
                }
            }

            // 键盘操作时的焦点提示（内描边）
            if (Focused && Enabled && ShowFocusCues)
            {
                Rectangle fr = new Rectangle(2, 2, Width - 5, Height - 5);
                if (fr.Width > 2 && fr.Height > 2)
                {
                    using (GraphicsPath fp = RoundedPath(fr, Math.Max(0, EffRadius() - 2)))
                    using (Pen p = new Pen(Mix(fore, back, 0.55), 1f))
                        g.DrawPath(p, fp);
                }
            }

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
