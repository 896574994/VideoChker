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
        // ==================== 关键帧模式（ffmpeg 抽帧显示，任何编码都支持） ====================

        private void LoadVideo(string path)
        {
            KeyframePause();          // 换视频先停掉上一条的关键帧播放，避免定时器继续推进新视频
            _kfPos = 0;
            _duration = ProbeDuration(path);
            SetTimeLabel(0);
            _seekBar.Value = 0;
            _notes.Clear();
            _grid.Rows.Clear();
            _tlPanel.Invalidate();
            // 换视频：先把旧系统播放器画面隐藏，确保显示新视频的关键帧
            if (_wmp != null)
            {
                try { _wmp.Visible = false; } catch (Exception) { }
            }
            _frameBox.Visible = true;
            _frameBox.BringToFront();
            if (_stateLab != null) _stateLab.BringToFront();
            // 立即显示首帧关键帧
            RequestFrame(0);
            _statusLab.Text = "已加载：" + Path.GetFileName(path) + "（" + FmtSec(_duration) + "）。点「开始理解」分析全片；拖动进度条/点击时间轴可定位画面。";
            // 附加：尝试用系统播放器连续播放（H.264 等可播；H.265 播不了会自动降级，不影响关键帧）
            TryAttachWmp(path);
            // 兜底：关键帧画面保持置顶
            _frameBox.BringToFront();
            if (_stateLab != null) _stateLab.BringToFront();
        }

        private void TryAttachWmp(string path)
        {
            try
            {
                if (_wmp == null)
                {
                    _wmp = new WmpHost();
                    _wmp.Dock = DockStyle.Fill;
                    _playerPanel.Controls.Add(_wmp);
                    _wmp.CreateControl();
                    try { _wmp.Player.uiMode = "none"; } catch (Exception) { }
                }
                _wmp.Player.URL = path;
                _wmp.Player.settings.autoStart = false;
                _wmpUsable = true;
                _wmpPlayable = false;
                _wmp.Visible = false;   // 默认隐藏，用关键帧画面；可播时才接管
                if (_stateLab != null) _stateLab.BringToFront();
                Log.Info("系统播放器已挂载：" + Path.GetFileName(path));
                // 后台探测该编码能否真的播放（H.265 常无解码器，探测后点击时间轴才不会黑屏）
                bool wasVis = _wmp.Visible;   // UI 线程读取当前显示状态
                string p = path;
                Thread th = new Thread(delegate() { ProbeWmpPlayable(p, wasVis); });
                th.IsBackground = true;
                th.Start();
            }
            catch (Exception ex)
            {
                _wmpUsable = false;
                _wmpPlayable = false;
                Log.Debug("系统播放器不可用（自动使用关键帧模式）：" + ex.Message);
            }
        }

        /// <summary>后台探测：尝试播放并轮询 playState 是否进入"播放中"，以此判断该编码能否真播。
        /// 探测时 WMP 短暂显示但被关键帧盖住，用户看不到闪烁；结束后恢复原显示状态。</summary>
        private void ProbeWmpPlayable(string path, bool wasVisible)
        {
            bool ok = false;
            try
            {
                Ui(delegate()
                {
                    _wmp.Visible = true;
                    _wmp.BringToFront();
                    _wmpBehind = false;   // 播放器回到最前了
                    _frameBox.BringToFront();   // 关键帧盖在上面，用户看不到 WMP
                    if (_stateLab != null) _stateLab.BringToFront();
                });
                _wmp.Player.controls.play();
                for (int i = 0; i < 20; i++)   // 最长 2 秒
                {
                    System.Threading.Thread.Sleep(100);
                    int ps = 0;
                    try { ps = (int)_wmp.Player.playState; } catch (Exception) { break; }
                    if (ps == 3) { ok = true; break; }
                    if (ps == 1 || ps == 6 || ps == 0) break;   // 停止/结束/未定义 → 无解码器
                }
                try { _wmp.Player.controls.stop(); } catch (Exception) { }
            }
            catch (Exception) { ok = false; }
            bool okFinal = ok;
            Ui(delegate()
            {
                _wmpPlayable = okFinal;
                if (wasVisible)   // 用户探测前就在用播放器：保持播放器显示
                {
                    _wmp.BringToFront();
                }
                else             // 默认状态：切回关键帧画面
                {
                    _wmp.Visible = false;
                    _frameBox.BringToFront();
                }
                if (_stateLab != null) _stateLab.BringToFront();
                Log.Debug("播放器解码探测：" + Path.GetFileName(path) + " → " + (okFinal ? "可播放（H.264 等）" : "不可播（H.265 等，走关键帧模式）"));
            });
        }

        private void RequestFrame(double sec)
        {
            if (sec < 0 || _duration <= 0) return;
            if (sec > _duration) sec = _duration;
            string path = _pathBox.Text.Trim();
            if (path.Length == 0 || !File.Exists(path)) return;
            lock (_reqLock) { _reqTime = sec; _reqPath = path; }
            if (_frameBusy) return;
            _frameBusy = true;
            Thread t = new Thread(delegate () { FrameWorker(); });
            t.IsBackground = true;
            t.Start();
        }

        private void FrameWorker()
        {
            while (true)
            {
                double sec;
                string path;
                lock (_reqLock) { sec = _reqTime; path = _reqPath; _reqTime = -1; _reqPath = ""; }
                if (sec < 0 || path.Length == 0) break;
                byte[] jpg = GetFrameCached(path, sec);
                if (jpg == null || jpg.Length == 0)
                {
                    Ui(() => _statusLab.Text = "抽帧失败 @" + sec.ToString("0.#") + "s（该位置可能无关键帧）");
                    break;
                }
                double shown = sec;
                Ui(delegate() { ShowFrame(jpg, shown); });
                lock (_reqLock)
                {
                    if (_reqTime < 0) { _frameBusy = false; break; }
                }
            }
        }

        private byte[] GetFrameCached(string path, double sec)
        {
            string k = path + "|" + Math.Round(sec, 1).ToString("0.#");
            lock (_frameCache)
                if (_frameCache.ContainsKey(k)) return _frameCache[k];
            byte[] jpg;
            try { jpg = MageCheck.ExtractFrameJpeg(path, sec, 1280); }
            catch (Exception) { jpg = new byte[0]; }
            // 抽帧兜底（2026-09-12 加强）：
            //   原来只在失败时往前 1s 重试一次 ✗ 遇到"该位置恰好没有可解码帧"（损坏片段、
            //   关键帧稀疏、末尾）就仍然失败 → 画面框空白，用户看到的就是黑屏 ✓
            //   现在依次试几个邻近位置，任一成功就用 —— 用户的目的是"看看这一帧附近长什么样"，
            //   偏不到 1 秒完全可以接受 ✓
            if (jpg == null || jpg.Length == 0)
            {
                double[] fallbacks = new double[] { -1, +1, -2, +2, -0.5 };
                foreach (double d in fallbacks)
                {
                    double t2 = sec + d;
                    if (t2 < 0 || (_duration > 0 && t2 > _duration)) continue;
                    try { jpg = MageCheck.ExtractFrameJpeg(path, t2, 1280); }
                    catch (Exception) { jpg = new byte[0]; }
                    if (jpg != null && jpg.Length > 0)
                    {
                        if (_duration > 0 && sec > 1.5)   // 有偏差时在状态栏说明，不假装精确
                            Ui(delegate () { _statusLab.Text = "该位置没有可解码的画面，已显示 " + FmtSec(t2) + " 附近的帧"; });
                        break;
                    }
                }
            }
            if (jpg != null && jpg.Length > 0)
            {
                lock (_frameCache)
                {
                    if (_frameCache.Count > 40) _frameCache.Clear();
                    if (!_frameCache.ContainsKey(k)) _frameCache[k] = jpg;
                }
            }
            return jpg;
        }

        /// <summary>时间标签：当前时间 / 总时长（进度百分比）。</summary>
        private void SetTimeLabel(double cur)
        {
            int pct = _duration > 0 ? (int)(cur / _duration * 100) : 0;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            _timeLab.Text = FmtSec(cur) + " / " + FmtSec(_duration) + "  (" + pct + "%)";
        }

        private void ShowFrame(byte[] jpg, double sec)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(jpg))
                {
                    Image img = Image.FromStream(ms);
                    // 记录视频真实宽高比：布局用它给左侧画面区定宽，
                    // 免得按 Zoom 居中后左右各留一大条黑边 ✗
                    if (img.Height > 0)
                    {
                        double a = (double)img.Width / img.Height;
                        if (Math.Abs(a - _videoAspect) > 0.02)
                        {
                            _videoAspect = a;
                            Ui(delegate () { LayoutRows(); });   // 比例变了才重排，不是每帧都排
                        }
                    }
                    Image old = _frameBox.Image;
                    _frameBox.Image = img;
                    // ★ 只释放"自己造的"那张（2026-09-12）：
                    //   现在 _frameBox.Image 可能是后台解码环形缓冲里的图 ✗
                    //   无条件 Dispose 会把缓冲里的图释放掉，之后再用就是访问已释放对象 ✓
                    if (old != null && !object.ReferenceEquals(old, img) && !KfImgOwns(old)) old.Dispose();
                }
                // ★ 关键帧连播期间不碰进度条和时间标签（2026-09-12 修"滑块抖动"）：
                //   那两个由 KfTick 按 _kfPos 统一维护 ✓
                //   而这里的 sec 是"这一帧实际对应的时间"，总比 _kfPos 落后一点 ✗
                //   两个写者用不同的值交替写同一个滑块 → 看起来一直抖 ✓
                if (!_kfPlaying)
                {
                    SetTimeLabel(sec);
                    if (!_syncBusy && !_userDrag && _duration > 0)
                    {
                        int v = (int)(sec / _duration * 1000);
                        if (v < 0) v = 0;
                        if (v > 1000) v = 1000;
                        _seekBar.Value = v;
                    }
                }
            }
            catch (Exception) { }
        }

        // ==================== 进度条 / 播放控制 ====================

        private void OnSeekChanged(object sender, EventArgs e)
        {
            if (_duration <= 0) return;
            double sec = (double)_seekBar.Value / 1000 * _duration;
            if (_userDrag)
            {
                // 拖动中：实时更新画面（关键帧）
                if (_wmpUsable && _wmp != null && _wmp.Visible)
                {
                    try { _wmp.Player.currentPosition = sec; } catch (Exception) { }
                }
                RequestFrame(sec);
            }
        }

        /// <summary>关键帧模式下的「播放」。系统播放器可用时优先用它；否则用定时器按真实时间
        /// 推进播放头并不断请求画面，借已有的抽帧合并机制自动丢中间帧，得到约 8~15 帧/秒的播放。</summary>
        private void KeyframePlay()
        {
            if (_duration <= 0) { _statusLab.Text = "请先选择视频文件"; return; }
            if (_kfPlaying) return;

            // ★ 需要「从中间开始播」时不用系统播放器，改走关键帧连播（2026-09-12 定案）
            //
            //   试过三种办法让 WMP 跳到指定位置起播，全部无效 ✗
            //     · 播放前设 currentPosition
            //     · 隐藏改 z 序（避免丢媒体状态）
            //     · 反复推送定位 + 宽限期
            //   监控录像多是 H.265/大关键帧间隔 ✗ WMP 对这类素材的 seek 本就不可靠 ✓
            //
            //   而关键帧连播的起点**就是进度条的值** —— 从来没出过错 ✓
            //   所以定成：进度条不在开头 → 走关键帧连播（起点准 ✓ 帧率低一些但可接受）；
            //             进度条在开头 → 照常用播放器（本来就从头播，没歧义）✓
            double playStartSec = (_duration > 0) ? (double)_seekBar.Value / 1000 * _duration : 0;
            bool fromStart = playStartSec < 0.5 || (_duration > 0 && playStartSec >= _duration - 0.05);
            if (!fromStart && _wmpUsable && _wmp != null && _wmpPlayable)
            {
                _statusLab.Text = "从 " + FormatTime(playStartSec) + " 开始播放（用关键帧推进；系统播放器对监控素材定位不可靠）";
                UpdateStateBadge();
                // 落到下面的关键帧连播分支
            }
            else if (_wmpUsable && _wmp != null && _wmpPlayable)
            {
                try
                {
                    _wmp.Visible = true;
                    _wmp.BringToFront();
                    _wmpBehind = false;   // 播放器回到最前了
                    double rate = 1;
                    if (_rateBox.SelectedItem != null)
                        double.TryParse(_rateBox.SelectedItem.ToString().TrimEnd('x'), out rate);
                    try { _wmp.Player.settings.rate = rate; } catch (Exception) { }
                    // ★ 播放前对齐位置（2026-09-12 修）：
                    //   原来直接 play() —— 播放器从它自己的内部位置开始 ✗
                    //   用户刚点过「跳到这一帧」（那一步会把播放器藏起来并暂停），
                    //   一按播放就"又跳回开头" ✗
                    //
                    //   但不能无条件 reposition ✗ 否则「暂停 → 继续」也会被拽回进度条的值，
                    //   手感会很怪。所以只在【确实不一致】时才纠正：
                    //     · 播放器被藏起来过（刚跳帧）→ 需要对齐
                    //     · 播放器内部位置和进度条差得明显（> 1.5s）→ 需要对齐
                    //     · 正常暂停后继续 → 位置本来就一致，不打扰 ✓
                    double startSec = (_duration > 0) ? (double)_seekBar.Value / 1000 * _duration : 0;
                    if (_duration > 0 && startSec >= _duration - 0.05) startSec = 0;   // 已在末尾则从头播
                    // 播放器被压到画面框后面过（点过「跳到这一帧」）→ 它的位置可能是旧的，必须重新定位
                    bool needSeek = _wmpBehind;
                    if (!needSeek)
                    {
                        try
                        {
                            double cur = (double)_wmp.Player.currentPosition;
                            if (Math.Abs(cur - startSec) > 1.5) needSeek = true;
                        }
                        catch (Exception) { needSeek = true; }
                    }
                    if (needSeek) { try { _wmp.Player.currentPosition = startSec; } catch (Exception) { } }
                    BeginWmpSeekGrace(startSec);   // 之后 1.6s 不信播放器报的位置，见 OnTick 说明
                    _wmp.Player.controls.play();
                    SetTimeLabel(startSec);
                    _statusLab.Text = "系统播放器播放中（倍率 " + _rateBox.Text + "）";
                    UpdateStateBadge();
                    return;
                }
                catch (Exception) { _wmpUsable = false; }
            }

            // 关键帧连续播放（这条路径不用播放器，标记清掉免得下次误判需要重新定位）
            _wmpBehind = false;
            double start = (double)_seekBar.Value / 1000 * _duration;
            if (start >= _duration - 0.05) start = 0;      // 已在末尾则从头播
            _kfPos = start;
            _kfClockPos = start;    // 时钟线程从同一位置起算
            _kfPlaying = true;
            _kfLastMs = Environment.TickCount;
            if (_kfTimer == null)
            {
                _kfTimer = new System.Windows.Forms.Timer();
                _kfTimer.Tick += KfTick;
            }
            // ★ 间隔每次显式设（2026-09-12 修）：
            //   原来只在创建时设 80ms ✗ 而「预告片」用同一个 _kfTimer 对象设的是 1400ms ✗
            //   先点过「预告片」再点播放，就会变成每 1.4 秒才走一帧 ✓
            //   另外 80ms 本身就是流畅度瓶颈：上限只有 12.5 次/秒 ✗
            //   实测缓冲有 25 帧/秒，但显示被这个间隔卡在 10 帧/秒 ✓
            //   源素材 25 帧/秒 = 40ms 一帧 ✓ 定时器取 20ms（比一帧更细）
            //   这样才咬得住每一帧 ✗ 33ms 会一会取两次、一会跳两帧 → 看起来跳帧 ✓
            _kfTimer.Interval = 20;   // 仅供「预告片」使用；正常播放走时钟线程
            // 播放期间用低延迟 GC 模式：把阻塞式的 Gen2 回收推迟到播放结束
            // （实测普通模式下 10 秒内 Gen2 3 次，每次都可能停几百毫秒 ✗）
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; }
            catch (Exception) { }
            // ★ 把系统计时精度提到 1ms（2026-09-12 修"节奏不匀"）：
            //   Windows 默认计时粒度是 15.6ms 的倍数 ✗ 设 20ms 实际会跑成约 31ms ✓
            //   而源素材 40ms 一帧 ✓ 两者不整除 → 帧的显示时刻忽早忽晚（20ms/60ms 交替）✗
            //   帧率统计看着是对的（25.0 帧/秒 ✓）但节奏不匀 → 肉眼就是顿 ✓
            //   实测：不开这个，帧间隔标准差 14.5ms ✗ 开了以后应该降到几毫秒 ✓
            // ★ 用专用时钟线程驱动帧的节拍（2026-09-12 定稿）
            //   不再用 WinForms Timer：它的回调排在消息队列里，前面有重绘/输入就被推迟 ✗
            //   帧周期 40ms、定时器 20ms，40 能整除 20，理论上很均匀 ✓
            //   但实测帧间隔在中位 32ms 和 60ms 之间跳、标准差 14ms ✗
            //   加 timeBeginPeriod(1) 也没用（14.5 → 14.2）✗ 因为瓶颈是**排队**不是精度 ✓
            //   时钟线程自己按 Stopwatch 计时、最后 1~2ms 自旋等 ✓ 唤醒精度 ±2ms ✓
            BeginPreciseTiming();
            KfClockStart();
            _frameBox.Visible = true;
            // ★ 缓冲已覆盖起点就别清（2026-09-12 修最后那次顿挫）：
            //   JumpToFrame 里已经提前预取好一段 ✓ 这里再清一次等于白扔 ✗
            //   清完要重新抽 500ms，那段时间没缓冲 → 退回逐帧（375ms/帧）→ 起步必卡一次 ✓
            if (KfBufIndexOf(start) < 0)
            {
                KfBufClear();
                KfPrefetch(start);
            }
            else
            {
                KfDecodeKick();   // 缓冲现成的，催后台继续解码就行
            }
            // 把具体起点写进状态栏 —— 用户回报问题时能给出确切数字，
            // 一眼就能判断"起点对不对"（这是排查"从头播"最直接的证据）
            _statusLab.Text = "关键帧连续播放中 —— 起点 " + FormatTime(start) + " / " + FormatTime(_duration)
                + "（系统播放器对监控素材定位不可靠，从中间起播走这条路）；点「暂停」停止";
            UpdateStateBadge();
        }

        private void KeyframePause()
        {
            if (!_kfPlaying) return;
            _kfPlaying = false;
            KfClockStop();
            if (_kfTimer != null) _kfTimer.Stop();
            EndPreciseTiming();     // 还原系统计时精度，别一直占着
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive; }
            catch (Exception) { }
            KfBufClear();   // 停播就释放缓冲（几十帧 JPEG 占几 MB）
            UpdateStateBadge();
            _statusLab.Text = "已暂停（关键帧模式）——点「播放」继续，或拖动进度条自由定位";
        }

        /// <summary>停止：退回开头并停掉关键帧播放。</summary>
        private void KeyframeStop()
        {
            KeyframePause();
            _kfPos = 0;
            if (_duration > 0)
            {
                _syncBusy = true;
                _seekBar.Value = 0;
                _syncBusy = false;
                SetTimeLabel(0);
                RequestFrame(0);
            }
            _statusLab.Text = "已停止（关键帧模式）";
        }

        // ==================== 专用播放时钟 ====================
        //
        // 帧的节拍由这个线程负责 ✓ 不再依赖 WinForms Timer 的排队回调 ✗
        // 计时用 Stopwatch（单调、高精度）✓ 最后 1~2ms 自旋等 ✓ 唤醒精度 ±2ms ✓

        private Thread _kfClock;
        private volatile bool _kfClockRun;

        private void KfClockStart()
        {
            if (_kfClockRun) return;
            _kfClockRun = true;
            _kfClock = new Thread(KfClockLoop);
            _kfClock.IsBackground = true;
            _kfClock.Priority = ThreadPriority.AboveNormal;   // 保证按时唤醒，不被普通线程挤掉
            _kfClock.Start();
        }

        private void KfClockStop() { _kfClockRun = false; }

        private void KfClockLoop()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            double frameMs = 1000.0 / KfPlayFps;      // 40ms（25 帧/秒）
            double nextMs = sw.Elapsed.TotalMilliseconds + frameMs;
            while (_kfClockRun)
            {
                if (!_kfPlaying) break;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                double wait = nextMs - nowMs;
                if (wait > 2.0)
                {
                    // 离得还远：让出 CPU（睡少 1ms，避免睡过头）
                    int ms = (int)(wait - 1);
                    if (ms > 0) Thread.Sleep(ms);
                    continue;
                }
                if (wait > 0)
                {
                    // 最后 1~2ms：自旋等，保证准点
                    Thread.SpinWait(120);
                    continue;
                }
                // ── 到点：推进一帧 ──
                nextMs += frameMs;
                if (nextMs < nowMs - 200) nextMs = nowMs + frameMs;   // 落后太多（卡过）就重新对齐，不追赶
                double pos = _kfClockPos;
                _kfClockPos = pos + frameMs / 1000.0;
                try { BeginInvoke((MethodInvoker)delegate () { KfAdvanceTo(pos); }); }
                catch (Exception) { break; }   // 窗体已关
            }
        }

        private double _kfClockPos;      // 时钟线程内部推进的播放位置（只在时钟线程写）

        /// <summary>UI 线程：把播放位置更新到 pos，并显示该位置的帧。</summary>
        private void KfAdvanceTo(double pos)
        {
            if (!_kfPlaying || _duration <= 0) return;
            if (pos >= _duration)
            {
                _kfPos = _duration;
                KeyframePause();
                SetTimeLabel(_duration);
                _statusLab.Text = "关键帧播放到末尾";
                return;
            }
            _kfPos = pos;

            int v = (int)(_kfPos / _duration * 1000);
            if (v < 0) v = 0;
            if (v > 1000) v = 1000;
            _syncBusy = true;
            _seekBar.Value = v;
            _syncBusy = false;
            SetTimeLabel(_kfPos);

            int idx = KfBufIndexOf(_kfPos);
            if (idx >= 0 && _kfShownIdx >= 0)
            {
                int lag = idx - _kfShownIdx;
                int step = 1;
                if (lag > 3) step = 2;
                if (lag > 6) step = 3;
                if (lag > step) idx = _kfShownIdx + step;
                if (idx < _kfShownIdx) idx = _kfShownIdx;
            }
            if (idx >= 0)
            {
                if (idx != _kfShownIdx)
                {
                    double frameSec = _kfBufStartOf(idx) + idx / KfPlayFps;
                    Image ready = KfImgGetAt(frameSec);
                    _kfShownIdx = idx;
                    if (ready != null) ShowImage(ready);
                    else
                    {
                        byte[] fr = KfBufAt(idx);
                        if (fr != null) ShowFrame(fr, _kfPos);
                    }
                }
            }
            else RequestFrame(_kfPos);

            if (KfBufEndNow < _kfPos + KfPrefetchAheadSec) KfPrefetch(_kfPos);
            KfDecodeKick();
        }

        private void KfTick(object sender, EventArgs e)
        {
            if (!_kfPlaying || _duration <= 0) return;
            int now = Environment.TickCount;
            int dt = now - _kfLastMs;
            _kfLastMs = now;
            if (dt < 0) dt = 0;
            if (dt > 1000) dt = 1000;                 // 卡顿之后不要一下跳太远
            double rate = 1;
            if (_rateBox.SelectedItem != null)
                double.TryParse(_rateBox.SelectedItem.ToString().TrimEnd('x'), out rate);
            if (rate <= 0) rate = 1;

            _kfPos += dt / 1000.0 * rate;
            if (_kfPos >= _duration)
            {
                _kfPos = _duration;
                KeyframePause();
                _statusLab.Text = "关键帧播放到末尾";
            }
            _syncBusy = true;
            int v = (int)(_kfPos / _duration * 1000);
            if (v < 0) v = 0;
            if (v > 1000) v = 1000;
            _seekBar.Value = v;
            _syncBusy = false;
            SetTimeLabel(_kfPos);

            // ==================== 播放取帧：优先从预取缓冲拿（2026-09-12 改）====================
            // 以前是每次 tick 都 RequestFrame(_kfPos) ——
            // 每帧都要起一个 ffmpeg 进程（实测约 200ms）→ 只有 4~5 帧/秒 ✗ 一卡一卡 ✓
            // 现在改成：后台一次抽一小段放进 _kfBuf，前台按时间从内存取 ✓
            // 缓冲没覆盖到才退化成逐帧请求（起步那一两秒会用到）✓
            int idx = KfBufIndexOf(_kfPos);
            // ★ 每次最多前进一帧（2026-09-12 修"偶尔漏一帧的顿挫"）：
            //   索引是按**真实时间**算的 ✗ 而 WinForms Timer 有约 15.6ms 的粒度抖动 ✓
            //   偶尔晚一拍 → 时间跑多了 → 索引一次跳 2 → 用户看到画面漏一帧 ✗
            //   实测最大帧间隔 78~79ms（正好 2 帧）就是这个原因 ✓ 和抽帧进程无关
            //   （对比：抽帧在跑时平均 41.6ms / 不在跑 41.8ms —— 完全一样 ✓）
            //   限制 +1 后：晚一拍就只是"这一帧稍晚一点显示" ✓ 不会漏帧 ✓
            //   定时器 20ms 一跳、上限 50 帧/秒 ✓ 足够追上 25 帧/秒的源 ✓ 不会越拖越远 ✓
            if (idx >= 0 && _kfShownIdx >= 0)
            {
                // 自适应追赶（2026-09-12 修"画面像瞬移"）：
                //   原来固定「最多 +1」✗ 但 Windows 定时器在 UI 忙时会**跳过**回调（不是排队）✓
                //   于是画面会越播越落后 ✓ 等缓冲一换、索引重算到真实位置 ✓
                //   → 一次性向前跳一大截 → 用户看到"像瞬移" ✓
                //   现在按落后量分档追赶：小差距逐帧追 ✓ 大差距多追一帧 ✓
                //   只有落后超过 8 帧（0.32 秒）才直接跟上 ✓ 这样任何跳动都不超过 0.32 秒 ✓
                // 渐进追赶，**永不直接跳**（2026-09-12 再修"像瞬移"）：
                //   原来落后超过 8 帧就直接跟上 ✗ —— 实测第 13 秒落后 0.45 秒（11 帧）✓
                //   于是画面一次向前跳了 0.45 秒 → 用户看到"人像瞬移" ✓
                //   现在按落后量分摊到几跳里追完 ✓ 单跳最多 3 帧（0.12 秒）✓
                //   代价：追赶期间播放速度略快一点点（几十毫秒级）✓ 肉眼不可见 ✓
                //   换来的是**任何时刻都不会出现向前的大跳** ✓
                int lag = idx - _kfShownIdx;
                int step = 1;
                if (lag > 3) step = 2;
                if (lag > 6) step = 3;
                if (lag > step) idx = _kfShownIdx + step;
                if (idx < _kfShownIdx) idx = _kfShownIdx;   // 不倒着走
            }
            if (idx >= 0)
            {
                if (idx != _kfShownIdx)          // 索引没变就不重复显示
                {
                    // ★ 优先取「后台已解码好」的 Image（2026-09-12 修顿挫）：
                    //   实测每帧在 UI 线程 Image.FromStream 要分配 3.7MB（1280x724 ARGB），
                    //   25 帧/秒 = 92MB/秒的分配压力 → 频繁 Gen2 回收 → 全局停顿 391ms ✗
                    //   现在解码放到后台线程，UI 只换引用 —— 零分配 ✓
                    // 已解码缓存按**绝对时间**查（不再按缓冲内索引）——
                    // 缓冲换了索引含义会变 ✗ 时间含义不会 ✓ 详见 KfImgGetAt 的说明
                    double frameSec = _kfBufStartOf(idx) + idx / KfPlayFps;
                    Image ready = KfImgGetAt(frameSec);
                    _kfShownIdx = idx;
                    if (ready != null) ShowImage(ready);
                    else
                    {
                        byte[] fr = KfBufAt(idx);    // 解码还没跟上，用字节兜一帧
                        if (fr != null) ShowFrame(fr, _kfPos);
                    }
                }
            }
            else
            {
                RequestFrame(_kfPos);            // 缓冲还没就绪，先逐帧兜着
            }
            KfDecodeKick();                      // 催一下后台解码
            // 缓冲快用完了 → 提前预取下一段（后台线程，不阻塞界面）
            if (KfBufEndNow < _kfPos + KfPrefetchAheadSec) KfPrefetch(_kfPos);
        }

        // ==================== 关键帧播放的预取缓冲 ====================
        //
        // 实测（见 MageCheck.ExtractFrameBatch 的说明）：
        //   逐帧抽 200ms/帧（4~5 帧/秒）✗    批量抽 24ms/帧（可达 40 帧/秒）✓
        // 所以播放时后台一次抽一整段（默认 5 秒 @ 10 帧/秒 = 50 帧），
        // 前台只做「按时间取帧 + 显示」，完全不碰 ffmpeg ✓ 画面就顺了 ✓

        // 参数是实测选出来的（源素材 25 帧/秒 H.265 2688x1520，4 秒一批）：
        //   帧率  宽度    出图数  抽取耗时   内存
        //    25   720     100    497ms     7.7 MB
        //    25  1280     100    535ms    22.2 MB   ← 选这组
        // 关键发现 1：耗时几乎不随帧率变化 —— 瓶颈是「解码这 4 秒源素材」，
        //             输出多少帧几乎不额外花钱 ✓ 所以用原生 25 帧/秒 ✓
        // 关键发现 2：宽度从 720 提到 1280 只多 8% 时间（497→535ms）✗
        //             我曾为了省这 8% 把宽度压到 720，结果画面明显变糊 ✓
        //             **清晰度远比这 8% 值钱** —— 现在恢复 1280 ✓
        private const double KfPlayFps = 25.0;             // 播放帧率（原生，最顺）
        private const double KfBatchSec = 3.0;             // 每批覆盖的时长（秒）—— 缩短以降低内存占用
        // 预取要**留足余量**：一批约抽 500ms，但关键帧密集的片段会慢些 ✗
        // 原来只留 1.2 秒 → 缓冲先耗尽 → 退回逐帧抽(200ms/帧) → 一次可见顿挫 ✓
        private const double KfPrefetchAheadSec = 2.0;     // 剩余不足这么多就预取下一段（一批 3 秒，留 2 秒余量）
        private const int KfPlayWidth = 1280;              // 播放用抽帧宽度（保清晰度）

        private readonly object _kfBufLock = new object();
        private List<byte[]> _kfBuf = new List<byte[]>();
        private double _kfBufStart = -1;                   // 缓冲第一帧对应的时间
        private double _kfBufEnd = -1;                     // 缓冲覆盖到的最后时间
        private int _kfShownIdx = -1;                      // 当前显示的缓冲索引（避免重复解码）
        private Thread _kfPrefetch;
        private volatile bool _kfPrefetchBusy;

        /// <summary>缓冲里能覆盖到的时间；-1 表示没有缓冲。</summary>
        private double KfBufEndNow { get { return _kfBufEnd; } }

        /// <summary>时间 → 缓冲索引；不在覆盖范围内返回 -1。</summary>
        private int KfBufIndexOf(double sec)
        {
            lock (_kfBufLock)
            {
                if (_kfBuf.Count == 0 || _kfBufStart < 0) return -1;
                double rel = sec - _kfBufStart;
                if (rel < -0.2) return -1;
                if (rel > (_kfBuf.Count - 1) / KfPlayFps + 0.2) return -1;
                int i = (int)Math.Round(rel * KfPlayFps);
                if (i < 0) i = 0;
                if (i >= _kfBuf.Count) i = _kfBuf.Count - 1;
                return i;
            }
        }

        private byte[] KfBufAt(int idx)
        {
            lock (_kfBufLock)
            {
                if (idx < 0 || idx >= _kfBuf.Count) return null;
                return _kfBuf[idx];
            }
        }

        /// <summary>启动后台预取（同一时间只有一个在跑）。</summary>
        private void KfPrefetch(double fromSec)
        {
            if (_kfPrefetchBusy) return;
            string path = _pathBox.Text.Trim();
            if (path.Length == 0 || !File.Exists(path) || _duration <= 0) return;
            if (fromSec < 0) fromSec = 0;
            if (fromSec >= _duration) return;
            _kfPrefetchBusy = true;
            double from = fromSec;
            string p = path;
            _kfPrefetch = new Thread(delegate () { KfPrefetchWorker(p, from); });
            _kfPrefetch.IsBackground = true;
            _kfPrefetch.Start();
        }

        private void KfPrefetchWorker(string path, double fromSec)
        {
            try
            {
                double span = KfBatchSec;
                if (fromSec + span > _duration) span = _duration - fromSec;
                if (span <= 0.2) return;
                List<byte[]> frames = MageCheck.ExtractFrameBatch(path, fromSec, span, KfPlayFps, KfPlayWidth);
                if (frames == null || frames.Count == 0) return;
                // ★ 更换缓冲时必须把「已解码帧」一起作废（2026-09-12 修"画面循环播放"）：
                //   _kfImgs 是按**缓冲内索引**存的 ✗ 而缓冲起点每次都在往后移 ✓
                //   于是新缓冲的"索引 11"和旧缓冲的"索引 11"是**不同的时间** ✓
                //   显示时按新索引取出旧图 → 画面跳回之前的内容 → 看起来就是在循环 ✓
                //   表现：用户反馈"画面变成循环播放 2 秒内的奇怪现象" ✓
                lock (_kfBufLock)
                {
                    // ★ 换缓冲时把 _kfShownIdx 按【绝对时间】换算过来，而不是置 -1
                    //   （2026-09-12 修"像瞬移"的最后一处）：
                    //   置 -1 会让下面那句"最多前进 N 帧"的限制失效 ✗
                    //   因为限制条件是 if (_kfShownIdx >= 0) ✓ 为 -1 时直接采用真实索引 ✓
                    //   → 换缓冲的那一跳会直接向前跳（实测最大 0.51 秒 = 13 帧）✓
                    //   用户看到的就是"人像瞬移" ✗
                    //   换算之后索引的含义保持连续 ✓ 限制继续生效 ✓ 永远不会跳 ✓
                    double shownAbs = (_kfShownIdx >= 0 && _kfBufStart >= 0)
                        ? _kfBufStart + _kfShownIdx / KfPlayFps : -1;
                    _kfBuf = frames;
                    _kfBufStart = fromSec;
                    _kfBufEnd = fromSec + (frames.Count - 1) / KfPlayFps;
                    if (shownAbs >= 0)
                    {
                        int ni = (int)Math.Round((shownAbs - fromSec) * KfPlayFps);
                        _kfShownIdx = (ni >= 0 && ni < frames.Count) ? ni : -1;
                    }
                    else _kfShownIdx = -1;
                }
                // ★ 这里原来调 KfImgClear()（2026-09-12 去掉）：
                //   换缓冲时把已解码的帧全扔掉 ✗ → 解码器要从零重来 ✓
                //   空窗期退回 UI 线程解码 → 用户看到"流畅播 3 秒就顿一下" ✓
                //   （3 秒正是 KfBatchSec，每次换缓冲就顿一次 ✓）
                //   现在缓存按**绝对时间**存 ✓ 换缓冲不影响它的有效性 ✓ 所以不用清 ✓
                // 缓冲一到就催后台开始解码（不等按播放）—— 起步就不会卡
                KfDecodeKick();
            }
            catch (Exception) { }
            finally { _kfPrefetchBusy = false; }
        }

        /// <summary>清空缓冲（暂停/换视频/手动定位时调用）。</summary>
        private void KfBufClear()
        {
            lock (_kfBufLock)
            {
                _kfBuf = new List<byte[]>();
                _kfBufStart = -1;
                _kfBufEnd = -1;
                _kfShownIdx = -1;
            }
            KfImgClear();
        }

        // ==================== 后台解码环形缓冲 ====================
        //
        // 为什么要有它（2026-09-12 实测）：
        //   在 UI 线程解码一帧 1280x724 的 JPEG → Image.FromStream 分配约 3.7MB ✗
        //   25 帧/秒 = 92MB/秒分配压力 → 10 秒内 Gen0 12 次、Gen1 4 次、Gen2 3 次 ✗
        //   Gen2 会暂停**所有**线程 → 最长停顿实测 391ms ✓ 就是用户说的"突然卡一下" ✓
        //   现在：后台线程解码，UI 线程只做「换引用」这一个动作 → 零分配 ✓

        // 缓存几张要精打细算（2026-09-12 实测）：
        //   一张 1280x724 解码后是 3.7MB ✗ 而 >85KB 的分配进的是**大对象堆(LOH)** ✓
        //   LOH 分配会**直接触发 Gen2** ✗ 而 Gen2 暂停所有线程 → 顿挫 ✓
        //   原来留 24 张 = 89MB ✗ 内存冲到 161MB ✓ 内存压力一大，低延迟 GC 模式就失效 ✗
        //   解码本身只要 ~5ms/帧 ✓ 落后 8 帧（0.32 秒）完全够用 ✓ 所以压到 8 张 ✓
        private const int KfImgKeep = 8;                   // 最多缓存多少张已解码帧（约 30MB）

        private readonly object _kfImgLock = new object();
        private readonly Dictionary<int, Image> _kfImgs = new Dictionary<int, Image>();
        private Thread _kfDecode;
        private volatile bool _kfDecodeBusy;
        private int _kfImgWant = -1;                       // 想解到哪个索引（后台循环看它）
        private Image _kfShownImg;                         // 当前显示的那张（不归环形缓冲管）

        /// <summary>读当前缓冲的起点（锁内读，避免读到一半被换掉）。</summary>
        private double _kfBufStartOf(int dummy)
        {
            lock (_kfBufLock) return _kfBufStart;
        }

        /// <summary>时间 → 缓存键（按 1/帧率 的刻度取整）。</summary>
        private static int KfImgKey(double sec) { return (int)Math.Round(sec * KfPlayFps); }

        /// <summary>
        /// 把解出来的帧缩放成「显示框刚好放得下」的尺寸（保持比例，居中留黑边）。
        ///
        /// 为什么要在后台做这件事（2026-09-12）：
        ///   PictureBox 的 Zoom 会在 UI 线程把图缩到控件大小 ✓
        ///   1280px → 约 590px 的高质量缩放一次约 5~15ms ✗
        ///   25 帧/秒就占掉 UI 线程四分之一的时间 → 定时器被拖晚 → 节奏不匀 ✓
        ///   在解码线程先缩好，UI 就只剩一次 1:1 贴图 ✓
        ///   顺带：尺寸完全对上了，画面不会因为缩放而变模糊 ✓
        /// </summary>
        private Bitmap ScaleToBox(Image src)
        {
            int bw = 0, bh = 0;
            try { if (_frameBox != null) { bw = _frameBox.ClientSize.Width; bh = _frameBox.ClientSize.Height; } }
            catch (Exception) { }
            if (bw < 16 || bh < 16) return new Bitmap(src);      // 还没布局好：原样返回
            Bitmap dst = new Bitmap(bw, bh);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                double sc = Math.Min((double)bw / src.Width, (double)bh / src.Height);
                int w = Math.Max(1, (int)Math.Round(src.Width * sc));
                int h = Math.Max(1, (int)Math.Round(src.Height * sc));
                g.DrawImage(src, (bw - w) / 2, (bh - h) / 2, w, h);
            }
            return dst;
        }

        /// <summary>按绝对时间取已解码的帧；没有返回 null。</summary>
        private Image KfImgGetAt(double sec)
        {
            lock (_kfImgLock)
            {
                Image im;
                if (_kfImgs.TryGetValue(KfImgKey(sec), out im) && im != null) return im;
            }
            return null;
        }

        /// <summary>这张图是不是环形缓冲持有的（持有的话调用方不能 Dispose）。</summary>
        private bool KfImgOwns(Image im)
        {
            if (im == null) return false;
            lock (_kfImgLock)
            {
                foreach (KeyValuePair<int, Image> kv in _kfImgs)
                    if (object.ReferenceEquals(kv.Value, im)) return true;
            }
            return false;
        }

        /// <summary>取后台已解码好的帧；没有返回 null。</summary>
        private Image KfImgGet(int idx)
        {
            lock (_kfImgLock)
            {
                Image im;
                if (_kfImgs.TryGetValue(idx, out im) && im != null) return im;
            }
            return null;
        }

        /// <summary>告诉后台解码线程"往前解到这个索引"。</summary>
        private void KfDecodeKick()
        {
            int idx = KfBufIndexOf(_kfPos);
            if (idx < 0) idx = 0;              // 未开播 → 从缓冲头开始预解码
            int want = idx + KfImgKeep;
            int cnt;
            lock (_kfBufLock) cnt = _kfBuf.Count;
            if (want > cnt - 1) want = cnt - 1;
            _kfImgWant = want;
            if (_kfDecodeBusy) return;
            _kfDecodeBusy = true;
            _kfDecode = new Thread(delegate () { KfDecodeWorker(); });
            _kfDecode.IsBackground = true;
            _kfDecode.Start();
        }

        private void KfDecodeWorker()
        {
            try
            {
                while (true)
                {
                    // 没在播时也从缓冲头开始预解码 —— 这样一点「播放」就已经有解码好的帧，
                    // 不会在起步那几十毫秒退回 UI 线程解码（实测那样会卡 391ms ✗）
                    int idx = KfBufIndexOf(_kfPos);
                    if (idx < 0) idx = 0;
                    int cnt;
                    lock (_kfBufLock) cnt = _kfBuf.Count;
                    if (cnt == 0) break;                 // 缓冲被清了 → 收工
                    if (idx > cnt - 1) idx = cnt - 1;
                    bool did = false;
                    // 从"当前显示的前一张"往后解，最多补到 want
                    double bStart = _kfBufStartOf(0);
                    int curKey = KfImgKey(bStart + idx / KfPlayFps);
                    for (int i = idx; i <= _kfImgWant; i++)
                    {
                        // 缓存键用**绝对时间刻度**，不用缓冲内索引 ✗
                        // 缓冲一换索引含义就变 ✓ 时间刻度不会 ✓ 所以换缓冲时不必清空缓存 ✓
                        int key = KfImgKey(bStart + i / KfPlayFps);
                        bool have;
                        lock (_kfImgLock) have = _kfImgs.ContainsKey(key);
                        if (have) continue;
                        byte[] b = KfBufAt(i);
                        if (b == null) break;
                        Image im = null;
                        try
                        {
                            // 关键 1：拷一份脱离 MemoryStream —— 否则 GDI+ 会一直引用那块流内存 ✗
                            // 关键 2：**顺便缩放成显示框的尺寸**（2026-09-12 修"顿挫感"）
                            //   原来解码出 1280px 的原图 ✗ UI 线程每帧要把它缩到约 590px ✓
                            //   GDI+ 高质量缩放一次约 5~15ms ✗ 25 帧/秒就吃掉 UI 线程 25% 的时间 ✓
                            //   → 定时器被拖晚 → 帧间隔在中位 32ms 和 60ms 之间跳（标准差 14ms）✗
                            //   在后台缩好之后，UI 只做 1:1 贴图 ✓ 既快又不会因为缩放出错 ✗
                            using (MemoryStream ms = new MemoryStream(b))
                            using (Image tmp = Image.FromStream(ms))
                                im = ScaleToBox(tmp);
                        }
                        catch (Exception) { im = null; }
                        if (im == null) break;
                        lock (_kfImgLock)
                        {
                            if (!_kfImgs.ContainsKey(key)) _kfImgs.Add(key, im);
                            else im.Dispose();
                            // 淘汰：落后当前时间 1 秒以上的（留着也回不去了）
                            List<int> drop = new List<int>();
                            foreach (KeyValuePair<int, Image> kv in _kfImgs)
                                if (kv.Key < curKey - (int)KfPlayFps) drop.Add(kv.Key);
                            foreach (int k in drop)
                            {
                                Image old = _kfImgs[k];
                                _kfImgs.Remove(k);
                                if (old != null && !object.ReferenceEquals(old, _kfShownImg)) old.Dispose();
                            }
                        }
                        did = true;
                    }
                    if (!did)
                    {
                        if (!_kfPlaying) break;      // 追平了又没在播 → 收工（等下次 kick）
                        Thread.Sleep(15);            // 播放中追上了就歇一会儿，别空转烧 CPU
                    }
                }
            }
            catch (Exception) { }
            finally { _kfDecodeBusy = false; }
        }

        private void KfImgClear()
        {
            lock (_kfImgLock)
            {
                foreach (KeyValuePair<int, Image> kv in _kfImgs)
                    if (kv.Value != null && !object.ReferenceEquals(kv.Value, _kfShownImg)) kv.Value.Dispose();
                _kfImgs.Clear();
            }
            _kfImgWant = -1;
        }

        /// <summary>直接把已解码好的图贴上画面（UI 线程零分配）。</summary>
        private void ShowImage(Image img)
        {
            try
            {
                if (img == null || _frameBox == null) return;
                _kfShownImg = img;                  // 记下：这张不归环形缓冲回收（它正被显示）
                if (!object.ReferenceEquals(_frameBox.Image, img)) _frameBox.Image = img;
            }
            catch (Exception) { }
        }

        private void PlayOrResume()
        {
            // 同 KeyframePlay：不在开头起播就走关键帧连播（系统播放器定位不可靠）
            double playStartSec = (_duration > 0) ? (double)_seekBar.Value / 1000 * _duration : 0;
            bool fromStart = playStartSec < 0.5 || (_duration > 0 && playStartSec >= _duration - 0.05);
            if (!fromStart)
            {
                KeyframePlay();
                return;
            }
            if (_wmpUsable && _wmp != null && _wmpPlayable)
            {
                try
                {
                    _wmp.Visible = true;
                    _wmp.BringToFront();
                    _wmpBehind = false;   // 播放器回到最前了
                    // 应用倍率
                    double rate = 1;
                    if (_rateBox.SelectedItem != null)
                        double.TryParse(_rateBox.SelectedItem.ToString().TrimEnd('x'), out rate);
                    try { _wmp.Player.settings.rate = rate; } catch (Exception) { }
                    // ★ 同 KeyframePlay：位置不一致时才对齐进度条，
                    //   避免"暂停 → 继续"被无谓地拽回（详见上面的说明）
                    double startSec = (_duration > 0) ? (double)_seekBar.Value / 1000 * _duration : 0;
                    if (_duration > 0 && startSec >= _duration - 0.05) startSec = 0;
                    // 播放器被压到画面框后面过（点过「跳到这一帧」）→ 它的位置可能是旧的，必须重新定位
                    bool needSeek = _wmpBehind;
                    if (!needSeek)
                    {
                        try
                        {
                            double cur = (double)_wmp.Player.currentPosition;
                            if (Math.Abs(cur - startSec) > 1.5) needSeek = true;
                        }
                        catch (Exception) { needSeek = true; }
                    }
                    if (needSeek) { try { _wmp.Player.currentPosition = startSec; } catch (Exception) { } }
                    BeginWmpSeekGrace(startSec);   // 同上
                    _wmp.Player.controls.play();
                    SetTimeLabel(startSec);
                    _statusLab.Text = "系统播放器播放中（倍率 " + _rateBox.Text + "；画面不支持时请用关键帧拖动定位）";
                    return;
                }
                catch (Exception) { _wmpUsable = false; }
            }
            // 系统播放器不可用（典型：H.265 监控素材 WMP 解不了）—— 改用关键帧连续播放。
            _wmpBehind = false;   // 这条路不用播放器，标记清掉
            // 这里原来是"只写一句状态提示就返回"，所以用户点「播放」毫无反应、也退不出关键帧模式。
            KeyframePlay();
        }

        /// <summary>快进/快退：相对当前播放位置跳转 delta 秒（关键帧显示 + 若系统播放器在播则同步 seek 并继续播放）。</summary>
        /// <summary>画面右下角播放状态角标：播放中 / 已暂停 / 已停止 / 拖动定位 / 关键帧浏览。</summary>
        private void UpdateStateBadge()
        {
            if (_stateLab == null) return;
            string s;
            if (_duration <= 0) s = "未加载";
            else if (_userDrag) s = "拖动定位中";
            else if (_wmpUsable && _wmp != null && _wmp.Visible)
            {
                int ps = 0;
                try { ps = (int)_wmp.Player.playState; } catch (Exception) { ps = 0; }
                if (ps == 3) s = "播放中";
                else if (ps == 2) s = "已暂停";
                else if (ps == 1) s = "已停止";
                else s = "加载中";
            }
            else if (_kfPlaying) s = "关键帧播放中";
            else s = "关键帧浏览";
            if (_stateLab.Text != s) _stateLab.Text = s;
        }

        private void OnSkip(double delta)
        {
            if (_duration <= 0) return;
            double cur = 0;
            if (_wmpUsable && _wmp != null && _wmp.Visible)
            {
                try { cur = (double)_wmp.Player.currentPosition; } catch (Exception) { cur = 0; }
            }
            else
            {
                cur = (double)_seekBar.Value / 1000 * _duration;
            }
            double t = cur + delta;
            if (t < 0) t = 0;
            if (t > _duration) t = _duration;
            if (_wmpUsable && _wmp != null && _wmp.Visible)
            {
                try { _wmp.Player.currentPosition = t; _wmp.Player.controls.play(); } catch (Exception) { }
            }
            LocateTo(t);
            _statusLab.Text = "跳转 " + (delta > 0 ? "+" : "") + delta + "s → " + FmtSec(t);
        }

        private void SafeWmp(string op)
        {
            if (!_wmpUsable || _wmp == null) return;
            try
            {
                if (op == "pause") _wmp.Player.controls.pause();
                else if (op == "stop") { _wmp.Player.controls.stop(); _wmp.Visible = false; _frameBox.Visible = true; }
            }
            catch (Exception) { }
        }

        private void OnTick(object sender, EventArgs e)
        {
            // 时间轴限制到约 6 次/秒就够（游标位置肉眼跟不上更快的刷新）
            // 原来每次 tick 都重绘 ✗ 几百个内容点时这是纯粹的浪费 ✓
            if (_tlPanel != null && (int)(Environment.TickCount - _tlLastPaint) >= 160)
            {
                _tlLastPaint = Environment.TickCount;
                _tlPanel.Invalidate();
            }
            UpdateStateBadge();
            if (_wmpUsable && _wmp != null && _wmp.Visible)
            {
                try
                {
                    // ★ 定位宽限期（2026-09-12 修 "点播放滑块跳回开头"）：
                    //   WMP 在媒体就绪前会把 currentPosition 报成 0 ✗
                    //   下面那句 _seekBar.Value = v 一读到 0 就把滑块拽回开头 ✓
                    //   宽限期内不信它报的位置：滑块保持在用户要求的位置，
                    //   并在中途把定位指令再推一次（刚 play() 时设位置常被忽略）✓
                    if (InWmpSeekGrace())
                    {
                        // 反复推送而不是只推一次（2026-09-12 二次修）：
                        //   原来 450ms 推一次就完事 ✗ 但大体积监控录像 WMP 加载要 1~3 秒，
                        //   那时候推的定位还是会被忽略 ✓
                        //   改成每次 tick 看一下：播放器报的位置离目标还远就再推一次，
                        //   直到它接近目标或宽限期结束 ✓
                        if (_wmpSeekTarget >= 0)
                        {
                            double curNow = 0;
                            try { curNow = (double)_wmp.Player.currentPosition; } catch (Exception) { }
                            if (!_wmpSeekPushed || Math.Abs(curNow - _wmpSeekTarget) > 1.5)
                            {
                                try { _wmp.Player.currentPosition = _wmpSeekTarget; } catch (Exception) { }
                                _wmpSeekPushed = true;
                            }
                        }
                        if (_wmpSeekTarget >= 0)
                        {
                            SetTimeLabel(_wmpSeekTarget);
                            if (!_userDrag && !_syncBusy && _duration > 0)
                            {
                                int v2 = (int)(_wmpSeekTarget / _duration * 1000);
                                if (v2 < 0) v2 = 0;
                                if (v2 > 1000) v2 = 1000;
                                _seekBar.Value = v2;
                            }
                        }
                        return;
                    }

                    double cur = 0;
                    try { cur = (double)_wmp.Player.currentPosition; } catch (Exception) { cur = 0; }
                    if (!_userDrag && !_syncBusy && _duration > 0)
                    {
                        int v = (int)(cur / _duration * 1000);
                        if (v < 0) v = 0;
                        if (v > 1000) v = 1000;
                        _seekBar.Value = v;
                    }
                    SetTimeLabel(cur);
                }
                catch (Exception) { }
            }
        }

        // ==================== 系统计时精度 ====================
        //
        // 为什么需要（2026-09-12 修"节奏不匀的顿挫"）：
        //   Windows 默认的计时器粒度是 **15.6ms 的倍数** ✗
        //   我设 Timer.Interval = 20ms → 实际约 31ms ✓
        //   而源素材是 25 帧/秒 = 40ms 一帧 ✓ 两者不整除 ✓
        //   于是帧的显示时刻忽早忽晚：有时候一帧显示 20ms、下一帧 60ms ✗
        //   实测帧率是对的（24.9 帧/秒 ✓ 平均间隔 40.2ms ✓）但节奏不匀 → 肉眼看就是顿 ✓
        //   timeBeginPeriod(1) 把精度提到 1ms → 20ms 的定时器真按 20ms 走 ✓
        //   → 每 2 跳正好一帧，节奏均匀 ✓
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint ms);
        private bool _timePrecise;

        private void BeginPreciseTiming()
        {
            if (_timePrecise) return;
            try { TimeBeginPeriod(1); _timePrecise = true; } catch (Exception) { }
        }

        private void EndPreciseTiming()
        {
            if (!_timePrecise) return;
            try { TimeEndPeriod(1); } catch (Exception) { }
            _timePrecise = false;
        }

        // ==================== 播放器定位宽限期 ====================

        private const int WmpSeekGraceMs = 2500;
        private int _wmpSeekGrace;            // 宽限期截止（TickCount）；0 = 不在宽限期
        private int _tlLastPaint;             // 时间轴上次重绘（限制重绘频率用）
        private double _wmpSeekTarget = -1;   // 宽限期内要落到的时间（秒）
        private bool _wmpSeekPushed;          // 是否已经二次推送过定位指令
        private bool _wmpBehind;              // 播放器被压到画面框后面（点过「跳到这一帧」）—— 恢复播放时需重新定位

        /// <summary>
        /// 记下"刚给播放器下了一条定位 + 播放指令"，随后 1.6 秒内不接受它报的位置。
        /// 插在 KeyframePlay / PlayOrResume / LocateTo 收到播放器分支的地方。
        /// </summary>
        private void BeginWmpSeekGrace(double targetSec)
        {
            _wmpSeekGrace = Environment.TickCount + WmpSeekGraceMs;
            _wmpSeekTarget = targetSec;
            _wmpSeekPushed = false;
        }

        /// <summary>是否还在宽限期内（用减法比较，避免 TickCount 回绕出问题）。</summary>
        private bool InWmpSeekGrace()
        {
            return _wmpSeekGrace != 0 && (int)(Environment.TickCount - _wmpSeekGrace) < 0;
        }

    }
}
