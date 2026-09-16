/* -*- coding: utf-8 -*-
 * Theme.cs — 界面主题（与 HTML 报告共用同一套配色）
 * 四套主题：浅色 / 深色 / 护眼 / 高对比，色值与 ReportHtml.cs 的 CSS 变量一一对应。
 * 选择保存在 exe 同目录的 .theme 文件，下次启动沿用。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoChecker
{
    /// <summary>一套主题的全部颜色。</summary>
    public class Theme
    {
        public string Key = "light";
        public string Name = "浅色";

        // 基础
        public Color Bg;          // 窗体底色
        public Color BgAlt;       // 次级面板底（工具条 / 表头 / 分隔区）
        public Color Card;        // 卡片 / 输入框 / 列表底
        public Color Fg;          // 主文字
        public Color Muted;       // 次要文字（提示 / 灰色说明）
        public Color Border;      // 边框 / 分隔线
        public Color Accent;      // 主色（开始检测、选中态）
        public Color AccentFg;    // 主色上的文字
        public Color BtnBg;       // 普通按钮底
        public Color BtnFg;       // 普通按钮文字
        public Color Danger;      // 危险色（停止、关闭、失败）
        public Color SelBg;       // 选中项底（下拉框高亮）

        // 表格
        public Color GridBg;
        public Color GridAlt;     // 交替行
        public Color GridHeaderBg;
        public Color GridHeaderFg;
        public Color GridLine;

        // 状态
        public Color Pass;
        public Color Warn;
        public Color Fail;
        public Color Skip;
        public Color SkipSoft;

        // 启动页（封面，各主题各有一套）
        public Color SplashBg1;
        public Color SplashBg2;
        public Color SplashFg;
        public Color SplashSub;
        public Color SplashAccent;

        public Theme(string key, string name) { Key = key; Name = name; }
    }

    /// <summary>主题注册表：当前主题、切换、持久化。</summary>
    public static class Themes
    {
        public static readonly Theme[] All = new Theme[] {
            // —— 浅色系 ——
            MakeLight(), MakeSepia(), MakeSakura(), MakeTeal(), MakeAmber(),
            // —— 马卡龙系（浅色 · 柔和高调）——
            MakeMint(), MakePeach(), MakeLavender(), MakeSky(),
            // —— 深色系 ——
            MakeDark(), MakeMidnight(), MakeGraphite(), MakeForest(), MakeViolet(),
            // —— 科技系（深色 · 霓虹强调）——
            MakeCyber(), MakeMatrix(), MakeDeepSpace(), MakeAurora(),
            // —— 无障碍 ——
            MakeContrast()
        };

        private static Theme _current;
        private static readonly string _file = AppPaths.Data(".theme");

        /// <summary>主题变化通知（已打开的窗体据此重新着色）。</summary>
        public static event Action Changed;

        public static Theme Current
        {
            get { return _current == null ? (_current = All[0]) : _current; }
        }

        static Themes() { Load(); }

        public static Theme ByKey(string key)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Key == key) return All[i];
            return All[0];
        }

        /// <summary>从 .theme 文件读取上次选择（读不到就用浅色）。</summary>
        public static void Load()
        {
            string key = "";
            try
            {
                if (File.Exists(_file))
                    key = File.ReadAllText(_file).Trim();
            }
            catch { }
            _current = ByKey(key);
        }

        /// <summary>切换主题并写盘；随后通知所有已打开的窗体重新着色。</summary>
        public static void Apply(string key)
        {
            Theme t = ByKey(key);
            bool same = (_current != null && _current.Key == t.Key);
            if (_current != null) _prevMuted = _current.Muted;
            // ★ 记下上一套主题的"文字三色"：
            //   Label 的 ForeColor 不像按钮/输入框那样每次都重设 ——
            //   卡片标题创建时按当时的主题定色，切主题后如果不管，就会
            //   保留上一套的文字颜色（深色主题的浅字留在浅色卡片上 → 几乎看不见）。
            //   所以这里记下来，着色时把"旧值"映射成"新值"。
            if (_current != null) _prevFg = _current.Fg;
            if (_current != null) _prevCard = _current.Card;
            _current = t;
            try { File.WriteAllText(_file, t.Key); }
            catch { }
            if (!same)
            {
                Action h = Changed;
                if (h != null) h();
            }
        }

        // ==================== 四套色板 ====================

        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        private static Theme MakeLight()
        {
            Theme t = new Theme("light", "浅色");
            t.Bg = C(228, 233, 242); t.BgAlt = C(234, 239, 246); t.Card = C(255, 255, 255);
            t.Fg = C(31, 41, 55); t.Muted = C(107, 122, 143); t.Border = C(201, 210, 226);
            t.Accent = C(37, 99, 235); t.AccentFg = C(255, 255, 255);
            t.BtnBg = C(255, 255, 255); t.BtnFg = C(85, 85, 85);
            t.Danger = C(220, 38, 38); t.SelBg = C(219, 234, 254);
            t.GridBg = C(255, 255, 255); t.GridAlt = C(248, 250, 252);
            t.GridHeaderBg = C(238, 241, 245); t.GridHeaderFg = C(71, 85, 105);
            t.GridLine = C(232, 236, 241);
            t.Pass = C(22, 163, 74); t.Warn = C(217, 119, 6);
            t.Fail = C(220, 38, 38); t.Skip = C(100, 116, 139); t.SkipSoft = C(148, 163, 184);
            t.SplashBg1 = C(24, 28, 40); t.SplashBg2 = C(15, 42, 74);
            t.SplashFg = C(255, 255, 255); t.SplashSub = C(150, 160, 180); t.SplashAccent = C(120, 190, 255);
            return t;
        }

        private static Theme MakeDark()
        {
            Theme t = new Theme("dark", "深色");
            t.Bg = C(13, 16, 23); t.BgAlt = C(22, 28, 38); t.Card = C(30, 38, 52);
            t.Fg = C(227, 233, 242); t.Muted = C(148, 162, 184); t.Border = C(53, 65, 90);
            t.Accent = C(59, 130, 246); t.AccentFg = C(255, 255, 255);
            t.BtnBg = C(38, 47, 63); t.BtnFg = C(205, 214, 228);
            t.Danger = C(248, 113, 113); t.SelBg = C(30, 58, 95);
            t.GridBg = C(30, 38, 52); t.GridAlt = C(35, 44, 60);
            t.GridHeaderBg = C(40, 50, 68); t.GridHeaderFg = C(176, 188, 206);
            t.GridLine = C(48, 58, 76);
            t.Pass = C(34, 197, 94); t.Warn = C(245, 158, 11);
            t.Fail = C(248, 113, 113); t.Skip = C(148, 163, 184); t.SkipSoft = C(125, 135, 152);
            t.SplashBg1 = C(18, 21, 28); t.SplashBg2 = C(30, 41, 59);
            t.SplashFg = C(232, 237, 245); t.SplashSub = C(142, 152, 168); t.SplashAccent = C(96, 165, 250);
            return t;
        }

        private static Theme MakeSepia()
        {
            Theme t = new Theme("sepia", "护眼");
            t.Bg = C(234, 223, 197); t.BgAlt = C(240, 231, 209); t.Card = C(252, 248, 238);
            t.Fg = C(61, 50, 34); t.Muted = C(122, 106, 78); t.Border = C(205, 187, 148);
            t.Accent = C(168, 98, 42); t.AccentFg = C(255, 255, 255);
            t.BtnBg = C(251, 246, 234); t.BtnFg = C(107, 91, 69);
            t.Danger = C(168, 58, 42); t.SelBg = C(233, 216, 186);
            t.GridBg = C(251, 246, 234); t.GridAlt = C(247, 240, 224);
            t.GridHeaderBg = C(236, 224, 200); t.GridHeaderFg = C(107, 91, 69);
            t.GridLine = C(224, 211, 186);
            t.Pass = C(79, 122, 58); t.Warn = C(176, 116, 20);
            t.Fail = C(168, 58, 42); t.Skip = C(138, 122, 99); t.SkipSoft = C(162, 149, 125);
            t.SplashBg1 = C(61, 51, 39); t.SplashBg2 = C(92, 74, 51);
            t.SplashFg = C(251, 246, 234); t.SplashSub = C(190, 175, 150); t.SplashAccent = C(214, 168, 106);
            return t;
        }

        private static Theme MakeContrast()
        {
            Theme t = new Theme("contrast", "高对比");
            t.Bg = C(255, 255, 255); t.BgAlt = C(242, 242, 242); t.Card = C(255, 255, 255);
            t.Fg = C(0, 0, 0); t.Muted = C(51, 51, 51); t.Border = C(0, 0, 0);
            t.Accent = C(0, 0, 204); t.AccentFg = C(255, 255, 255);
            t.BtnBg = C(255, 255, 255); t.BtnFg = C(0, 0, 0);
            t.Danger = C(192, 0, 0); t.SelBg = C(204, 204, 255);
            t.GridBg = C(255, 255, 255); t.GridAlt = C(245, 245, 245);
            t.GridHeaderBg = C(238, 238, 238); t.GridHeaderFg = C(0, 0, 0);
            t.GridLine = C(204, 204, 204);
            t.Pass = C(0, 100, 0); t.Warn = C(138, 75, 0);
            t.Fail = C(192, 0, 0); t.Skip = C(51, 51, 51); t.SkipSoft = C(51, 51, 51);
            t.SplashBg1 = C(0, 0, 0); t.SplashBg2 = C(26, 26, 26);
            t.SplashFg = C(255, 255, 255); t.SplashSub = C(200, 200, 200); t.SplashAccent = C(255, 255, 0);
            return t;
        }

        // ==================== 派生色工具 ====================

        private static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>
        /// 由"主色 + 深/浅"派生出一整套主题，避免每套手写二十多个颜色。
        /// 面板底、表头、交替行、选中态、封面等都由这九个基础色推出来。
        /// </summary>
        private static Theme Build(string key, string name, bool dark,
            Color bg, Color card, Color fg, Color muted, Color border, Color accent,
            Color pass, Color warn, Color fail, Color splash1, Color splash2)
        {
            Theme t = new Theme(key, name);
            t.Bg = bg; t.Card = card; t.Fg = fg; t.Muted = muted; t.Border = border;
            t.Accent = accent; t.AccentFg = Color.White;
            t.BgAlt = Mix(card, fg, dark ? 0.10 : 0.05);
            t.BtnBg = card; t.BtnFg = fg;
            t.Danger = fail;
            t.SelBg = Mix(card, accent, dark ? 0.38 : 0.22);
            t.GridBg = card;
            t.GridAlt = Mix(card, fg, dark ? 0.05 : 0.03);
            t.GridHeaderBg = Mix(card, fg, dark ? 0.14 : 0.07);
            t.GridHeaderFg = muted;
            t.GridLine = border;
            t.Pass = pass; t.Warn = warn; t.Fail = fail;
            t.Skip = muted; t.SkipSoft = Mix(muted, bg, 0.35);
            t.SplashBg1 = splash1; t.SplashBg2 = splash2;
            t.SplashFg = Color.White;
            t.SplashSub = Mix(Color.White, splash1, 0.45);
            t.SplashAccent = Mix(accent, Color.White, 0.32);
            return t;
        }

        // ==================== 浅色系：樱花 / 青碧 / 琥珀 ====================

        // ==================== 15 套派生主题 ====================
        // 基色经 Build() 派生出面板底 / 表头 / 交替行 / 选中态 / 封面等约 25 个颜色。
        // 配色要点：卡片与背景的明度差保持在 8~10，让卡片明显浮起来；
        // 以前这个差值只有 2~4，整个界面是一片平色，看着廉价。

        private static Theme MakeSakura()
        {
            return Build("sakura", "樱花", false,
                C(249,220,231), C(255,251,253), C(74,42,54), C(154,108,126), C(239,192,210),
                C(226,74,127), C(22,163,74), C(217,119,6), C(200,50,70), C(74,44,58),
                C(128,70,96));
        }

        private static Theme MakeTeal()
        {
            return Build("teal", "青碧", false,
                C(213,235,235), C(255,255,255), C(18,54,56), C(78,122,126), C(179,214,216),
                C(13,148,136), C(22,163,74), C(217,119,6), C(200,50,70), C(14,54,56),
                C(20,92,94));
        }

        private static Theme MakeAmber()
        {
            return Build("amber", "琥珀", false,
                C(245,230,204), C(255,251,242), C(64,51,30), C(133,112,78), C(219,196,154),
                C(217,119,6), C(22,163,74), C(180,83,9), C(200,50,70), C(62,46,24),
                C(120,84,30));
        }

        private static Theme MakeMidnight()
        {
            return Build("midnight", "午夜蓝", true,
                C(8,18,42), C(20,42,92), C(214,226,245), C(126,147,184), C(36,64,110),
                C(76,140,255), C(34,197,94), C(245,158,11), C(248,113,113), C(10,26,62),
                C(22,52,112));
        }

        private static Theme MakeGraphite()
        {
            return Build("graphite", "石墨", true,
                C(20,20,22), C(38,38,44), C(230,230,234), C(150,150,158), C(60,60,68),
                C(240,160,48), C(74,222,128), C(251,191,36), C(248,113,113), C(28,28,32),
                C(46,46,54));
        }

        private static Theme MakeForest()
        {
            return Build("forest", "森林", true,
                C(10,20,16), C(27,49,38), C(218,238,225), C(132,164,146), C(46,82,64),
                C(34,197,94), C(74,222,128), C(245,158,11), C(248,113,113), C(12,30,22),
                C(22,54,38));
        }

        private static Theme MakeViolet()
        {
            return Build("violet", "紫夜", true,
                C(16,12,30), C(42,33,80), C(237,232,250), C(154,143,192), C(69,58,112),
                C(167,139,250), C(52,211,153), C(251,191,36), C(248,113,113), C(18,14,36),
                C(40,28,78));
        }

        private static Theme MakeMint()
        {
            return Build("mint", "薄荷", false,
                C(210,237,226), C(255,255,255), C(30,74,60), C(85,131,111), C(169,216,196),
                C(16,185,129), C(22,163,74), C(217,119,6), C(200,50,70), C(20,90,72),
                C(16,120,96));
        }

        private static Theme MakePeach()
        {
            return Build("peach", "蜜桃", false,
                C(252,224,213), C(255,252,250), C(78,46,34), C(154,104,82), C(240,191,168),
                C(249,115,22), C(22,163,74), C(202,138,4), C(200,50,70), C(180,70,30),
                C(214,110,60));
        }

        private static Theme MakeLavender()
        {
            return Build("lavender", "薰衣草", false,
                C(232,226,250), C(255,255,255), C(58,45,92), C(117,104,160), C(201,188,238),
                C(124,92,224), C(22,163,74), C(217,119,6), C(200,50,70), C(84,62,160),
                C(120,96,200));
        }

        private static Theme MakeSky()
        {
            return Build("sky", "天空", false,
                C(217,233,250), C(255,255,255), C(27,58,92), C(90,124,160), C(175,205,238),
                C(46,139,230), C(22,163,74), C(217,119,6), C(200,50,70), C(30,90,160),
                C(46,124,200));
        }

        private static Theme MakeCyber()
        {
            return Build("cyber", "赛博", true,
                C(6,6,14), C(24,24,56), C(230,244,255), C(122,139,181), C(42,42,92),
                C(34,211,238), C(52,211,153), C(251,191,36), C(248,113,113), C(8,8,20),
                C(26,26,60));
        }

        private static Theme MakeMatrix()
        {
            return Build("matrix", "矩阵", true,
                C(4,10,5), C(16,36,21), C(200,255,212), C(94,156,108), C(30,68,38),
                C(34,197,94), C(74,222,128), C(245,158,11), C(248,113,113), C(6,14,7),
                C(18,42,24));
        }

        private static Theme MakeDeepSpace()
        {
            return Build("deepspace", "深空", true,
                C(4,8,20), C(20,32,62), C(214,228,255), C(110,134,181), C(34,53,94),
                C(91,155,255), C(52,211,153), C(251,191,36), C(248,113,113), C(6,10,26),
                C(22,36,70));
        }

        private static Theme MakeAurora()
        {
            return Build("aurora", "极光", true,
                C(4,16,14), C(12,34,32), C(216,245,240), C(106,158,150), C(26,62,56),
                C(45,212,191), C(52,211,153), C(251,191,36), C(248,113,113), C(6,20,18),
                C(14,40,38));
        }

        // ==================== 通用着色辅助 ====================

        /// <summary>上一套主题的柔和文字色。用于识别"这个 Label 是灰色说明文字"从而跟随主题更新。</summary>
        private static Color _prevMuted = Color.Empty;
        private static Color _prevFg = Color.Empty;
        private static Color _prevCard = Color.Empty;

        // 标签在主题里的"角色"：记住一次，之后照着更新。
        // 用 ConditionalWeakTable 是为了跟着控件一起被回收，不会越积越多。
        private static readonly object RoleMuted = new object();
        private static readonly object RoleFg = new object();
        private static readonly object RoleFixed = new object();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _labelRoles
            = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>();

        /// <summary>把当前主题应用到整个窗体（递归覆盖子控件）。各窗体再按需做特殊覆盖。</summary>
        public static void ApplyTo(Form f)
        {
            Theme t = Current;
            f.BackColor = t.Bg;
            f.ForeColor = t.Fg;
            Walk(f, t);
        }

        /// <summary>递归着色。Label 不主动设色（走环境继承），但识别出的灰色说明文字会跟随主题。</summary>
        private static void Walk(Control parent, Theme t)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is DataGridView) StyleGrid((DataGridView)c);
                else if (c is ComboBox) StyleCombo((ComboBox)c);
                else if (c is TextBox || c is RichTextBox || c is NumericUpDown || c is ListBox)
                { c.BackColor = t.Card; c.ForeColor = t.Fg; }
                else if (c is Button) StyleButton((Button)c);
                else if (c is Label)
                {
                    // ★ 标签着色（2026-09-12 重写）：
                    //   旧写法靠"值匹配"判断这个标签是不是跟随主题的 ——
                    //   第一次切主题能对上，**第二次就失效了** ✗
                    //   （因为第一次已经把颜色改成了主题色，第二次的"上一次的颜色"又变了）
                    //   现在改成：第一次遇到时判定它的角色并记住，之后每次按角色着色 ✓
                    //   角色只三种：次要文字(muted) / 正文(fg) / 语义色(固定，永不动)
                    object role;
                    if (!_labelRoles.TryGetValue(c, out role))
                    {
                        if (c.ForeColor == Color.Gray
                            || (_prevMuted != Color.Empty && c.ForeColor == _prevMuted)
                            || c.ForeColor == t.Muted) role = RoleMuted;
                        else if ((_prevFg != Color.Empty && c.ForeColor == _prevFg)
                              || c.ForeColor == t.Fg) role = RoleFg;
                        else role = RoleFixed;      // 百分比、错误提示、视频角标这类语义色
                        _labelRoles.Add(c, role);
                    }
                    if (role == RoleMuted) c.ForeColor = t.Muted;
                    else if (role == RoleFg) c.ForeColor = t.Fg;

                    // 背景也要跟着换：卡片标题的 BackColor 是创建时按当时的主题设的，
                    // 切主题后不更新就会在浅色卡片上留一块深色底（用户截图反馈过的真实问题）。
                    // 透明背景的保持透明（本来就露出父容器底色）。
                    if (c.BackColor != Color.Transparent)
                        c.BackColor = NearestBackColor(parent, t);
                }
                else if (c is CheckBox || c is RadioButton) { c.ForeColor = t.Fg; }
                else if (c is ProgressBar) { /* 系统控件，无法着色 */ }
                else if (c is AxHost) { /* 嵌入式播放器，保持原样 */ }
                else if (c is FlowRow) { ((FlowRow)c).SyncTheme(); }
                else if (c is RoundCheck) { ((RoundCheck)c).SyncLabel(); }
                else if (c is CardPanel || c is FlatProgress || c is RoundCheck
                      || c is IconRail || c is Pill || c is FieldBox)
                {
                    // UiKit 自绘控件：背景由它们自己画（透明，露出父容器底色）。
                    // 若落到下面的 else 分支会被强行涂成窗体底色，圆角卡片就毁了 —— 只通知重绘。
                    c.Invalidate();
                    FieldBox fb = c as FieldBox;
                    if (fb != null) fb.SyncTheme();
                    // 卡片的标题/副标题是子 Label，它们的颜色由卡片自己负责同步 ——
                    // 否则切主题后会留下上一套主题的底色和文字色（真实 bug）。
                    CardPanel cp = c as CardPanel;
                    if (cp != null) cp.SyncTheme();
                }
                else { c.BackColor = t.Bg; c.ForeColor = t.Fg; }

                if (c.HasChildren) Walk(c, t);
            }
        }

        /// <summary>
        /// 从指定控件往上找，返回"实际会被画出来的底色"。
        /// 卡片（CardPanel）自己画圆角底，它的 BackColor 是透明 —— 这时要用主题的卡片色；
        /// 其余容器直接用它的 BackColor（窗体是主题底色、面板是次级底色…）。
        /// </summary>
        private static Color NearestBackColor(Control c, Theme t)
        {
            Control cur = c;
            int guard = 0;
            while (cur != null && guard++ < 16)
            {
                if (cur is CardPanel) return t.Card;
                if (cur.BackColor != Color.Transparent) return cur.BackColor;
                cur = cur.Parent;
            }
            return t.Bg;
        }

        /// <summary>把一个下拉框改成自绘（原生下拉列表不跟随主题，必须自绘才能着色）。</summary>
        public static void StyleCombo(ComboBox cb)
        {
            Theme t = Current;
            cb.BackColor = t.Card;
            cb.ForeColor = t.Fg;
            cb.FlatStyle = FlatStyle.Flat;
            cb.DrawMode = DrawMode.OwnerDrawFixed;
            cb.ItemHeight = 20;
            cb.DrawItem -= ComboDrawItem;
            cb.DrawItem += ComboDrawItem;
        }

        private static void ComboDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            ComboBox cb = sender as ComboBox;
            if (cb == null) return;
            Theme t = Current;
            bool sel = ((e.State & DrawItemState.Selected) == DrawItemState.Selected);
            Color bg = sel ? t.SelBg : t.Card;
            Color fg = sel ? t.Fg : t.Fg;
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            string txt = Convert.ToString(cb.Items[e.Index]);
            TextRenderer.DrawText(e.Graphics, txt, cb.Font, e.Bounds, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        /// <summary>把一个表格改成跟随主题（含表头 —— 需关掉系统视觉样式表头）。</summary>
        public static void StyleGrid(DataGridView g)
        {
            Theme t = Current;
            g.BackgroundColor = t.GridBg;
            g.GridColor = t.GridLine;
            g.BorderStyle = BorderStyle.FixedSingle;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = t.GridHeaderBg;
            g.ColumnHeadersDefaultCellStyle.ForeColor = t.GridHeaderFg;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = t.GridHeaderBg;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = t.GridHeaderFg;
            g.RowHeadersDefaultCellStyle.BackColor = t.GridHeaderBg;
            g.RowHeadersDefaultCellStyle.ForeColor = t.GridHeaderFg;
            g.DefaultCellStyle.BackColor = t.GridBg;
            g.DefaultCellStyle.ForeColor = t.Fg;
            g.DefaultCellStyle.SelectionBackColor = t.SelBg;
            g.DefaultCellStyle.SelectionForeColor = t.Fg;
            g.AlternatingRowsDefaultCellStyle.BackColor = t.GridAlt;
            g.AlternatingRowsDefaultCellStyle.ForeColor = t.Fg;
            g.AlternatingRowsDefaultCellStyle.SelectionBackColor = t.SelBg;
            g.AlternatingRowsDefaultCellStyle.SelectionForeColor = t.Fg;
        }

        /// <summary>圆角按钮的额外参数（普通 Button 会安全跳过）。</summary>
        private static void TuneRound(Button b, Color border, bool useBorder)
        {
            RoundButton rb = b as RoundButton;
            if (rb == null) return;
            rb.BorderColor = border;
            rb.UseBorder = useBorder;
            rb.Invalidate();
        }

        /// <summary>主按钮（开始检测 / 确认）。</summary>
        public static void StylePrimary(Button b)
        {
            Theme t = Current;
            b.BackColor = t.Accent;
            b.ForeColor = t.AccentFg;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = t.Accent;
            TuneRound(b, t.Accent, false);
            b.Invalidate();
        }

        /// <summary>普通按钮。</summary>
        public static void StyleButton(Button b)
        {
            Theme t = Current;
            b.BackColor = t.BtnBg;
            b.ForeColor = t.BtnFg;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = t.Border;
            TuneRound(b, t.Border, true);
            b.Invalidate();
        }

        /// <summary>只改前景色的文字按钮（停止 / 暂停这类）。</summary>
        public static void StyleTextOnly(Button b, Color fg)
        {
            Theme t = Current;
            b.BackColor = t.BtnBg;
            b.ForeColor = fg;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = t.Border;
            TuneRound(b, t.Border, true);
            b.Invalidate();
        }

        /// <summary>状态 → 颜色（全工程统一，避免报告与界面配色分叉）。</summary>
        public static Color StatusColor(string status)
        {
            Theme t = Current;
            if (status == "PASS") return t.Pass;
            if (status == "WARN") return t.Warn;
            if (status == "FAIL") return t.Fail;
            return t.Skip;
        }

        /// <summary>状态 → 中文（界面统一用「一般」而非「警告」）。</summary>
        public static string StatusZh(string status)
        {
            if (status == "PASS") return "通过";
            if (status == "WARN") return "一般";
            if (status == "FAIL") return "失败";
            return "跳过";
        }
    }
}
