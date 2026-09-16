/* -*- coding: utf-8 -*-
 * VideoSearchForm 的一部分（partial class，编译方式不变：csc src\*.cs）
 *
 * 拆分说明（2026-09-13 用户要求「规范代码结构」）：
 *   原来 VideoSearchForm.cs 有 2731 行 ✗ 太长了不好维护 ✓
 *   按功能区域拆成三个 partial 文件 ✓ **纯文本搬运，零行为改变** ✓
 *     VideoSearchForm.cs            字段/构造/布局/事件/AI 分析
 *     VideoSearchForm.Playback.cs   关键帧/播放控制/时钟/预取/解码
 *     VideoSearchForm.Timeline.cs   文搜/时间轴/导出/工具
 *   用 partial class 而不是拆成多个类 ✓ 因为字段有 81 个 ✓
 *   拆类要处理大量交叉引用 ✗ 风险大 ✓ partial 只挪文本 ✓ 风险为零 ✓
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
    public partial class VideoSearchForm : Form
    {
        // ==================== 文搜 ====================

        private void DoSearch()
        {
            string q = _searchBox.Text.Trim();
            List<ContentNote> notes;
            lock (_notes) notes = new List<ContentNote>(_notes);
            if (q.Length == 0)
            {
                for (int r = 0; r < _grid.Rows.Count; r++) _grid.Rows[r].Visible = true;
                _statusLab.Text = "已清除筛选（共 " + notes.Count + " 条内容）";
                _tlPanel.Invalidate();
                return;
            }
            string[] keys = q.Split(new char[] { ' ', '　', ',', '，', ';', '；', ':', '：' }, StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length == 0)
            {
                for (int r = 0; r < _grid.Rows.Count; r++) _grid.Rows[r].Visible = true;
                _statusLab.Text = "未识别到关键词";
                return;
            }
            // 模糊搜索：任一关键词命中即显示；命中词数越多排越靠前（按匹配度排序）
            List<DataGridViewRow> hits = new List<DataGridViewRow>();
            List<int> scores = new List<int>();
            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                ContentNote n = _grid.Rows[r].Tag as ContentNote;
                if (n == null) continue;
                string hay = n.Text + " " + n.Osd + " " + n.Status;
                int score = 0;
                foreach (string k in keys)
                    if (k.Length > 0 && hay.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) score++;
                if (score > 0) { hits.Add(_grid.Rows[r]); scores.Add(score); }
            }
            foreach (DataGridViewRow row in _grid.Rows) row.Visible = false;
            if (hits.Count > 1)
            {
                int[] idx = new int[hits.Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                Array.Sort(idx, delegate(int a, int b) { return scores[b].CompareTo(scores[a]); });
                DataGridViewRow[] sorted = new DataGridViewRow[hits.Count];
                for (int i = 0; i < sorted.Length; i++) sorted[i] = hits[idx[i]];
                int pos = 0;
                foreach (DataGridViewRow row in sorted)
                {
                    _grid.Rows.Remove(row);
                    _grid.Rows.Insert(pos++, row);
                    row.Visible = true;
                }
            }
            else if (hits.Count == 1)
            {
                hits[0].Visible = true;
            }
            _statusLab.Text = "模糊搜索「" + q + "」命中 " + hits.Count + " 条（共 " + notes.Count + " 条，按匹配度排序）。双击行或点时间轴可定位画面。";
            Log.Info("文搜（模糊）：「" + q + "」命中 " + hits.Count + " 条");
            _tlPanel.Invalidate();
            if (hits.Count == 1 && hits[0].Cells.Count > 0)
                _grid.CurrentCell = hits[0].Cells[0];
        }

        // ==================== 时间轴 ====================
        //
        // 两条独立的游标（2026-09-12 改）：
        //   · 蓝竖线   = 视频当前位置，只读，跟着播放走
        //   · 橙手柄   = AI 时间轴游标，可拖动，**不动视频**
        // 早先只有一个位置（时间轴读视频进度条的值），拖动时两者一起动，
        // 想沿 AI 结果滑动查看就会把视频也拖走 ✗ 现在拆开了 ✓

        /// <summary>时间轴上的横向映射：时间 → 像素 x。</summary>
        private float TlX(double sec, int W)
        {
            if (_duration <= 0) return 8f;
            return 8f + (float)(sec / _duration) * (W - 16);
        }

        /// <summary>像素 x → 时间（秒）。</summary>
        private double TlSec(int x, int W)
        {
            if (_duration <= 0 || W <= 16) return 0;
            double sec = (double)(x - 8) / (W - 16) * _duration;
            if (sec < 0) sec = 0;
            if (sec > _duration) sec = _duration;
            return sec;
        }

        /// <summary>找离指定时间最近的 AI 内容点（用于拖动游标时显示"那一刻 AI 看到了什么"）。</summary>
        private ContentNote NearestNote(double sec)
        {
            List<ContentNote> notes;
            lock (_notes) notes = new List<ContentNote>(_notes);
            ContentNote best = null;
            double bestD = double.MaxValue;
            foreach (ContentNote n in notes)
            {
                double d = Math.Abs(n.Time - sec);
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }

        /// <summary>时间轴点位缓存：x / 半径 / 颜色ARGB。只有依赖项变了才重算。</summary>
        private List<float[]> _tlDots;
        private string _tlDotsKey = "";
        private Bitmap _tlDotLayer;      // 静态点位的离屏位图（贴图代替重画）
        private const int TlDotY = 19;   // 点位所在行（与 OnTlPaint 一致）

        private void EnsureTlDots(int W, List<ContentNote> notes)
        {
            string q = _searchBox != null ? _searchBox.Text.Trim() : "";
            string key = notes.Count + "|" + W + "|" + _duration.ToString("0.0") + "|" + q;
            if (key == _tlDotsKey && _tlDots != null) return;
            _tlDotsKey = key;
            List<float[]> list = new List<float[]>(notes.Count);
            string[] keys = q.Length > 0 ? q.Split(new char[] { ' ', '　', ',', '，', ';', '；', ':', '：' }, StringSplitOptions.RemoveEmptyEntries) : null;
            foreach (ContentNote n in notes)
            {
                float x = TlX(n.Time, W);
                Color c = StatusColor(n.Status);
                bool hit = false;
                if (keys != null)
                {
                    string hay = n.Text + " " + n.Osd + " " + n.Status;
                    foreach (string k in keys)
                        if (k.Length > 0 && hay.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                }
                float r = hit ? 6f : 4f;
                Color use = hit ? Color.Red : c;
                list.Add(new float[] { x, r, use.ToArgb() });
            }
            _tlDots = list;
            BuildTlDotLayer(W, TlDotY);
        }

        /// <summary>
        /// 把静态点位渲染到一张离屏位图（2026-09-12 修最后的大头）。
        ///
        /// 为什么必须这么做：点位是**静态的** ✗ 只有两条游标在动 ✓
        /// 但原来每次重绘都要现场画 400 个 FillEllipse —— 实测 **59ms** ✗
        /// 播放时时序器每 33ms 就重绘一次 ✓ 这个 59ms 直接把画面卡住 ✓
        /// 现在渲染一次存成位图 ✓ 每帧只做一次 0.5ms 的贴图 ✓ 差 100 倍 ✓
        /// </summary>
        private void BuildTlDotLayer(int W, int dotY)
        {
            try
            {
                if (_tlDotLayer != null) { _tlDotLayer.Dispose(); _tlDotLayer = null; }
                if (W <= 16 || _tlDots == null || _tlDots.Count == 0) return;
                Bitmap bmp = new Bitmap(W, TlDotLayerH);
                using (Graphics bg = Graphics.FromImage(bmp))
                {
                    bg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    bg.Clear(Color.Transparent);
                    using (SolidBrush track = new SolidBrush(Themes.Current.BgAlt))
                        bg.FillRectangle(track, 8, dotY - 3, W - 16, 6);
                    Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>(8);
                    try
                    {
                        foreach (float[] d in _tlDots)
                        {
                            float x = d[0], r = d[1];
                            int argb = (int)d[2];
                            SolidBrush br;
                            if (!brushes.TryGetValue(argb, out br))
                            {
                                br = new SolidBrush(Color.FromArgb(argb));
                                brushes.Add(argb, br);
                            }
                            bg.FillEllipse(br, x - r, dotY - r, r * 2, r * 2);
                        }
                    }
                    finally
                    {
                        foreach (KeyValuePair<int, SolidBrush> kv in brushes) kv.Value.Dispose();
                    }
                }
                _tlDotLayer = bmp;
            }
            catch (Exception) { _tlDotLayer = null; }
        }

        private const int TlDotLayerH = 58;   // 与时间轴面板同高

        /// <summary>内容点或搜索词变了 → 点位缓存作废。</summary>
        private void InvalidateTlDots() { _tlDotsKey = ""; if (_tlDotLayer != null) { _tlDotLayer.Dispose(); _tlDotLayer = null; } }

        private void OnTlPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = _tlPanel.ClientSize.Width;
            int H = _tlPanel.ClientSize.Height;
            g.Clear(Color.White);
            List<ContentNote> notes;
            lock (_notes) notes = new List<ContentNote>(_notes);
            if (notes.Count == 0 || _duration <= 0)
            {
                g.DrawString("尚未分析：点击「开始理解」后，这里会按时间显示 AI 理解到的内容点（绿=正常 橙=异常 红=严重 灰=无法判断）",
                    Font, Brushes.Gray, 8, H / 2 - 8);
                return;
            }

            int dotY = 19;          // 内容点所在行
            int lineTop = 2, lineBot = 32;   // 游标竖线范围
            int infoY = 36;         // 底部信息文字

            using (SolidBrush bg = new SolidBrush(Themes.Current.BgAlt))
                g.FillRectangle(bg, 8, dotY - 3, W - 16, 6);

            // ★ 点位走缓存（2026-09-12 修"做完理解后播放卡顿"）：
            //   原来每次重绘都要遍历**全部**内容点、逐条拼字符串做关键词匹配 ✗
            //   做完"开始理解"后动辄几百条 ✓ 而播放时定时器每 33ms 就重绘一次 ✓
            //   实测这让播完帧间隔从 94ms 恶化到 172ms ✓
            //   点位只跟「内容点数 / 面板宽度 / 时长 / 搜索词」有关 ✓ 缓存起来重算一次就够 ✓
            // 静态点位直接贴离屏位图（渲染一次，之后每帧只贴图）
            EnsureTlDots(W, notes);
            if (_tlDotLayer != null) g.DrawImageUnscaled(_tlDotLayer, 0, 0);

            // ---- 视频当前位置（只读，蓝线）----
            double cur = -1;
            if (_wmpUsable && _wmp != null && _wmp.Visible)
            {
                try { cur = (double)_wmp.Player.currentPosition; } catch (Exception) { }
            }
            else
            {
                cur = (double)_seekBar.Value / 1000 * _duration;
            }
            if (cur >= 0 && _duration > 0)
            {
                float xp = TlX(cur, W);
                using (Pen pen = new Pen(Color.FromArgb(160, Themes.Current.Accent), 2f))
                    g.DrawLine(pen, xp, lineTop, xp, lineBot);
            }

            // ---- AI 时间轴游标（橙色手柄，可拖）----
            if (_tlCursorSec >= 0 && _duration > 0)
            {
                float xs = TlX(_tlCursorSec, W);
                Color oc = Color.FromArgb(234, 120, 20);
                using (Pen pen = new Pen(oc, 2f))
                    g.DrawLine(pen, xs, lineTop, xs, lineBot);
                // 顶部三角手柄，明确告诉用户"这个能拖"
                PointF[] tri = new PointF[] {
                    new PointF(xs - 6, lineTop), new PointF(xs + 6, lineTop), new PointF(xs, lineTop + 9)
                };
                using (SolidBrush br = new SolidBrush(oc)) g.FillPolygon(br, tri);

                // 游标读数 + 该时刻 AI 识别到的内容
                string info = FormatTime(_tlCursorSec) + "  AI：";
                ContentNote nn = NearestNote(_tlCursorSec);
                if (nn != null)
                {
                    string txt = nn.Text;
                    if (txt.Length > 46) txt = txt.Substring(0, 46) + "…";
                    info += txt;
                }
                else info += "（该时刻附近没有内容点）";
                using (SolidBrush br = new SolidBrush(oc))
                    g.DrawString(info, Font, br, 8, infoY);
            }
            else
            {
                g.DrawString("橙色手柄可拖动查看 AI 内容（不影响视频播放）；双击时间轴才跳转画面",
                    Font, Brushes.Gray, 8, infoY);
            }
        }

        // ---- 时间轴的鼠标处理：拖动只动 AI 游标，不动视频 ----

        private void OnTlMouseDown(object sender, MouseEventArgs e)
        {
            if (_duration <= 0 || e.Button != MouseButtons.Left) return;
            if (_tlPanel.ClientSize.Width <= 16) return;
            _tlDragging = true;
            _tlCursorSec = TlSec(e.X, _tlPanel.ClientSize.Width);
            _tlPanel.Invalidate();
            _tlPanel.Focus();
            // 提示用户这是独立游标，不是视频进度条
            _statusLab.Text = "AI 游标已移到 " + FormatTime(_tlCursorSec)
                + "（只移动了游标，视频没动；要跳过去请双击时间轴或点「跳到这一帧」）";
        }

        private void OnTlMouseMove(object sender, MouseEventArgs e)
        {
            if (!_tlDragging) return;
            _tlCursorSec = TlSec(e.X, _tlPanel.ClientSize.Width);
            _tlPanel.Invalidate();
            _tlPanel.Update();   // 拖动时立即重绘，避免拖影
        }

        private void OnTlMouseUp(object sender, MouseEventArgs e)
        {
            if (!_tlDragging) return;
            _tlDragging = false;
            if (_tlCursorSec >= 0)
            {
                ContentNote nn = NearestNote(_tlCursorSec);
                _statusLab.Text = "AI 游标停在 " + FormatTime(_tlCursorSec)
                    + (nn != null ? "：" + (nn.Text.Length > 40 ? nn.Text.Substring(0, 40) + "…" : nn.Text) : "（附近没有内容点）");
            }
        }

        /// <summary>双击时间轴 = 把视频跳到 AI 游标处（这是两条游标之间的"桥"，由用户主动触发）。</summary>
        private void OnTlDoubleClick(object sender, MouseEventArgs e)
        {
            if (_duration <= 0) return;
            _tlCursorSec = TlSec(e.X, _tlPanel.ClientSize.Width);
            _tlPanel.Invalidate();
            JumpToFrame(_tlCursorSec);   // 双击 = 我要看这一帧（和「跳到这一帧」按钮同语义）
        }

        /// <summary>「预告片」：从游标位置开始，把后面的内容点依次跳一遍。</summary>
        private void PlayPreviewFrom(double startSec)
        {
            List<ContentNote> notes;
            lock (_notes) notes = new List<ContentNote>(_notes);
            List<double> times = new List<double>();
            foreach (ContentNote n in notes)
                if (n.Time >= startSec && times.Count < 12) times.Add(n.Time);
            if (times.Count == 0) { _statusLab.Text = "游标之后没有内容点了"; return; }

            _kfPreviewList = times;
            _kfPreviewIdx = 0;
            if (_kfTimer != null) _kfTimer.Stop();
            // 和「跳到这一帧」同理：预告片是逐帧看图，
            // 播放器如果顶在最前且解码失败，就是一片黑 ✗ 先藏起来 ✓
            SafeWmp("pause");
            if (_wmp != null) _wmp.Visible = false;
            if (_kfTimer == null)
            {
                _kfTimer = new System.Windows.Forms.Timer();
                _kfTimer.Tick += delegate(object s, EventArgs e)
                {
                    if (_kfPreviewList == null || _kfPreviewIdx >= _kfPreviewList.Count)
                    {
                        _kfTimer.Stop();
                        _statusLab.Text = "预览结束";
                        return;
                    }
                    double t = _kfPreviewList[_kfPreviewIdx++];
                    _seekBar.Value = (int)(t / _duration * 1000);
                    RequestFrame(t);
                    SetTimeLabel(t);
                    _statusLab.Text = "预览 " + _kfPreviewIdx + "/" + _kfPreviewList.Count + "  @" + FormatTime(t);
                };
            }
            _kfTimer.Interval = 1400;   // 预告片是"每点停 1.4 秒"，和正常播放共用定时器，必须显式设
            _kfTimer.Start();
            _statusLab.Text = "开始预览 " + times.Count + " 个内容点（每点停 1.4 秒）";
        }

        private List<double> _kfPreviewList;
        private int _kfPreviewIdx;

        /// <summary>秒 → mm:ss 或 h:mm:ss。</summary>
        private static string FormatTime(double sec)
        {
            if (sec < 0) sec = 0;
            int t = (int)Math.Round(sec);
            int h = t / 3600, m = (t % 3600) / 60, ss = t % 60;
            if (h > 0) return string.Format("{0}:{1:00}:{2:00}", h, m, ss);
            return string.Format("{0:00}:{1:00}", m, ss);
        }

        /// <summary>
        /// 「2026-09-11 00:06:48」→「00:06:48」。
        /// 列表挪到右栏后「画面时间」列从 132px 收到 84px，日期部分放不下也没必要重复
        /// （同一天录的，位置列已经能看出差别）✓
        /// 完整值仍然保留在：单元格提示、CSV 导出、AI 详情窗 ✓
        /// </summary>
        private static string ShortOsd(string osd)
        {
            if (string.IsNullOrEmpty(osd)) return "";
            int sp = osd.IndexOf(' ');
            if (sp > 0 && sp < osd.Length - 1) return osd.Substring(sp + 1).Trim();
            return osd;
        }

        private void OnTlClick(object sender, EventArgs e)
        {
            // 保留旧入口（不作为主交互）：单击时间轴不再直接跳转 ——
            // 跳转改成双击或按钮，避免"只想看看 AI 内容却把视频拖走了"。
            MouseEventArgs me = e as MouseEventArgs;
            if (me == null || _duration <= 0) return;
            _tlCursorSec = TlSec(me.X, _tlPanel.ClientSize.Width);
            _tlPanel.Invalidate();
        }

        private void OnGridJump(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            ContentNote n = _grid.Rows[e.RowIndex].Tag as ContentNote;
            if (n != null) JumpToFrame(n.Time);   // 双击列表行 = 看那一帧的画面
        }

        /// <summary>定位到某时间：显示关键帧 + 时间轴同步；系统播放器能解码时自动从该位置播放（autoPlay）。</summary>
        private void LocateTo(double sec, bool autoPlay = true)
        {
            if (sec < 0) sec = 0;
            if (sec > _duration) sec = _duration;
            _syncBusy = true;

            // ★ 进度条先定好（2026-09-12 修）：
            //   原来只有 else 分支设 _seekBar，走播放器分支时**根本不设** ✗
            //   于是进度条靠定时器去读 _wmp.currentPosition ——
            //   播放器定位失败或还在加载时读到 0，用户就看到"滑块跳回开头" ✗
            if (_duration > 0)
            {
                int v = (int)(sec / _duration * 1000);
                if (v < 0) v = 0;
                if (v > 1000) v = 1000;
                _seekBar.Value = v;
            }
            SetTimeLabel(sec);

            if (_wmpUsable && _wmp != null && _wmpPlayable)
            {
                try
                {
                    _wmp.Visible = true;
                    _wmp.BringToFront();
                    _wmpBehind = false;   // 播放器回到最前了
                    _wmp.Player.currentPosition = sec;
                    if (autoPlay) { BeginWmpSeekGrace(sec); _wmp.Player.controls.play(); }
                    else _wmp.Player.controls.pause();
                }
                catch (Exception) { _wmpUsable = false; }
            }
            RequestFrame(sec);   // 关键帧兜底（H.265 无播放器时就是画面本身）
            _syncBusy = false;
        }

        /// <summary>
        /// 「跳到这一帧」专用：**确定性地把那一帧显示出来**。
        ///
        /// 为什么不直接用 LocateTo（2026-09-12 修的真实问题）：
        ///   用户点「跳到这一帧」看到的是**黑屏** ✗ 取样排查后确认：
        ///   LocateTo 会把内置播放器 BringToFront 顶到最前，
        ///   而播放器定位失败/仍在加载时显示纯黑 ——
        ///   把下面已经抽好的关键帧画面**整个盖住了** ✗
        ///
        ///   这个按钮的语义是"我要看这一帧"，不是"从这儿开始播" ✓
        ///   所以这里：停播放 → 把播放器压到画面框后面 → 请求该时刻的帧。
        ///   全程不用播放器解码，H.264/H.265 都一样可靠 ✓
        ///
        /// ★ 用「压到后面」而不是「Visible = false」（2026-09-12 二次修）：
        ///   用户反馈点了「跳到这一帧」再点播放，**视频还是从头播** ✗
        ///   原因之一就是隐藏 AxHost 后再显示，播放器会丢掉已加载的媒体状态，
        ///   于是 play() 等于重新加载 → 从 0 开始 ✓
        ///   改成只调 z 序（画面框 BringToFront）—— 播放器仍然活着、位置仍然有效 ✓
        /// </summary>
        private void JumpToFrame(double sec)
        {
            if (_duration <= 0) { _statusLab.Text = "请先选择视频文件"; return; }
            if (sec < 0) sec = 0;
            if (sec > _duration) sec = _duration;

            SafeWmp("pause");
            KeyframePause();                 // 停掉连续播放，避免和这一帧抢画面
            // ★ 不隐藏播放器 —— 只把画面框调到最前（见上面的说明）
            _frameBox.BringToFront();
            if (_stateLab != null) _stateLab.BringToFront();
            if (_wmp != null) _wmp.SendToBack();
            _wmpBehind = true;               // 播放器在画面框后面，恢复播放时要重新定位

            _syncBusy = true;
            int v = (int)(sec / _duration * 1000);
            if (v < 0) v = 0;
            if (v > 1000) v = 1000;
            _seekBar.Value = v;
            SetTimeLabel(sec);
            _syncBusy = false;

            KfBufClear();        // 位置变了，旧缓冲作废
            KfPrefetch(sec);     // ★ 提前备好缓冲：这样一点「播放」就能立刻流畅起播，
                                 //   不用等前台逐帧抽（那样起步会顿一下）
            RequestFrame(sec);
            ContentNote nn = NearestNote(sec);
            _statusLab.Text = "已跳到 " + FormatTime(sec)
                + (nn != null ? "：" + (nn.Text.Length > 40 ? nn.Text.Substring(0, 40) + "…" : nn.Text) : "（附近没有内容点）");
        }

        // ==================== 竖分隔条：左右拖动调整「视频 / AI 列表」宽度比（2026-09-12 改版）====================
        // 原来是横的、调列表高度 ✗ 现在列表在右侧与视频等高，改调宽度比例 ✓

        private void OnSplitterDown(object sender, MouseEventArgs e)
        {
            _splitDrag = true;
            _splitStartX = Cursor.Position.X;
            _splitStartRatio = _splitRatio;
        }

        private void OnSplitterMove(object sender, MouseEventArgs e)
        {
            if (!_splitDrag) return;
            int M = 12, colGap = 10;
            int availW = ClientSize.Width - M * 2 - colGap;
            if (availW < 360) return;
            double deltaRatio = (double)(Cursor.Position.X - _splitStartX) / availW;
            double r = _splitStartRatio + deltaRatio;
            // 两侧都留出可用下限，避免拖成一条缝
            double minR = 260.0 / availW;
            double maxR = 1.0 - 200.0 / availW;
            if (minR > 0.75) minR = 0.75;
            if (maxR < 0.25) maxR = 0.25;
            if (r < minR) r = minR;
            if (r > maxR) r = maxR;
            _splitRatio = r;
            LayoutRows();
        }

        // ==================== 导出列表（CSV，Excel 可打开） ====================

        /// <summary>打开"放大查看"独立大窗口：大字号看 AI 理解内容，双击行定位主窗口画面。</summary>
        private void OpenDetail()
        {
            List<ContentNote> notes;
            lock (_notes) notes = new List<ContentNote>(_notes);
            if (notes.Count == 0)
            {
                MessageBox.Show(this, "还没有内容数据：先选视频并点「开始理解」再放大查看。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                ContentDetailForm cf = new ContentDetailForm(notes, delegate(double t) { JumpToFrame(t); });
                cf.Show(this);   // 非模态：可同时操作主窗口
                Log.Info("打开放大查看窗口：" + notes.Count + " 条");
            }
            catch (Exception ex)
            {
                Log.Error("放大查看打开失败：" + ex);
                MessageBox.Show(this, "打开放大窗口失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnExportList()
        {
            List<ContentNote> notes;
            lock (_notes) notes = new List<ContentNote>(_notes);
            if (notes.Count == 0)
            {
                MessageBox.Show(this, "还没有内容数据：先选视频并点「开始理解」再导出。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "导出 AI 内容理解列表（CSV，可用 Excel 打开）";
                sfd.Filter = "CSV 表格|*.csv";
                sfd.FileName = "内容理解_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("\ufeff");   // UTF-8 BOM，Excel 直接打开不乱码
                    sb.Append("序号,位置(秒),画面时间,判定,AI理解内容\r\n");
                    for (int i = 0; i < notes.Count; i++)
                    {
                        ContentNote n = notes[i];
                        sb.Append(i + 1).Append(',');
                        sb.Append(CsvEscape(n.Time.ToString("0.#"))).Append(',');
                        sb.Append(CsvEscape(n.Osd)).Append(',');
                        sb.Append(CsvEscape(StatusZh(n.Status))).Append(',');
                        sb.Append(CsvEscape(n.Text)).Append("\r\n");
                    }
                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    _statusLab.Text = "已导出 " + notes.Count + " 条 → " + sfd.FileName;
                    Log.Info("内容列表导出：" + sfd.FileName + "，" + notes.Count + " 条");
                }
                catch (Exception ex)
                {
                    Log.Error("导出失败：" + ex);
                    MessageBox.Show(this, "导出失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string StatusZh(string s)
        {
            if (s == "PASS") return "通过";
            if (s == "WARN") return "一般";
            if (s == "FAIL") return "失败";
            return "无法分析";
        }

        // ==================== 工具 ====================

        private static Color StatusColor(string s)
        {
            return Themes.StatusColor(s);
        }

        private static string FmtSec(double s)
        {
            if (s < 0) s = 0;
            int total = (int)Math.Round(s);
            return (total / 3600).ToString("00") + ":" + ((total % 3600) / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private void Ui(Action a)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (Exception) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cancel = true;
            // 关窗时把在途的推理请求与抽帧进程一并打断，否则后台线程会带着 ffmpeg 一起留下来
            MageCheck.AbortCurrent();
            Ffmpeg.KillRunning();
            try { _tm.Stop(); } catch (Exception) { }
            try { if (_kfTimer != null) _kfTimer.Stop(); } catch (Exception) { }
            base.OnFormClosing(e);
        }
    }
}
