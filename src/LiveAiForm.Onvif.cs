/* -*- coding: utf-8 -*-
 * LiveAiForm.Onvif.cs — 「ONVIF 订阅」巡检方式（LiveAiForm 的一部分，partial class）
 *
 * ★ 为什么加这个（2026-09-13）
 *
 *   用户问「海康不是有画面变化推送么」，一路查下来发现三种方式各有缺口：
 *     · 报警接收  —— 好用 ✓ 但只有海康/大华支持 ✗ TP-Link 没有 ISAPI ✓
 *     · 事件订阅  —— 实测海康的 alertStream **只推异常类事件** ✗ 移动侦测拿不到 ✓
 *     · 定时轮询  —— 都能用 ✓ 但两次采样之间的动作会漏 ✗
 *
 *   **ONVIF PullPoint 把三个缺口都补上了** ✓（两台都实测过 ✓）
 *     · 跨品牌 ✓（海康 / TP-Link 都支持 ✓）
 *     · **能拿到移动侦测** ✓（tns1:VideoSource/MotionAlarm ✓）
 *     · 事件**排队** ✓ 断线期间的不丢 ✓
 *
 * ★ 但是 ONVIF 有一堆坑，全部实测过（都是这个文件要处理的）✗
 *
 *   ① **事件不带图片** ✗
 *        事件里只有 Topic 和时间 ✓ 必须**另外抓图** ✓
 *   ② **抓图接口不保证有** ✗
 *        海康   GetSnapshotUri → 实测拿到 JPEG 629966 字节 ✓
 *        TP-Link GetSnapshotUri → **HTTP 500** ✗ 私有接口也 404 ✗
 *        → 所以必须**降级到 RTSP 抽帧** ✓
 *   ③ **订阅会到期** ✗
 *        一般 300 秒 ✓ 到期就没了 ✓ 事件静默丢失 ✓
 *        → 需要看门狗：快到期先 Renew ✓ 失败就**重建订阅** ✓
 *   ④ **Renew 不一定成功** ✗（各家实现差异大）
 *        → 所以 Renew 要**比对到期时间有没有真的往后走** ✓
 *          没走就当失败 ✓ 走重建 ✓
 *   ⑤ **端口非默认** ✗
 *        TP-Link 的 ONVIF 在 **2020** ✗ 标准是 80 ✓
 *        → 所以**必须走发现协议** ✗ 不能猜端口 ✓
 *   ⑥ **鉴权表现不规范** ✗
 *        TP-Link 用 **400** 表示密码错 ✗ 标准是 401 ✓
 *   ⑦ **一次变化会推好几条事件** ✗
 *        MotionAlarm + CellMotionDetector/Motion + Tamper 一起来 ✓
 *        → 必须**合并** ✓ 不然一次变化跑三次 AI ✓✓
 *
 * ★ 本文件只做海康这一条（用户同意先做通的再扩展）
 *   因为海康的抓图实测可用 ✓ 收到事件**不建 RTSP 会话** ✓ 最省摄像头 ✓
 *   TP-Link 要等"抓图降级"这条路验证过再开 ✓
 *
 * C# 5 兼容语法。
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;

namespace VideoChecker
{
    public partial class LiveAiForm
    {
        // ==================== ONVIF 订阅的状态 ====================

        private Thread _onvifThread;
        private bool _onvifRunning;

        /// <summary>上次送去分析的时间（TickCount），用于去抖。</summary>
        private int _onvifLastAnalyze;

        /// <summary>ONVIF 侧统计，显示在界面上用。</summary>
        private int _onvifRecv;          // 收到多少条事件
        private int _onvifMerged;        // 被合并掉多少条
        private int _onvifRebuild;       // 重建过几次订阅

        /// <summary>缓存的抓图地址（只问一次设备 ✓ 不要每个事件都问 ✗）。</summary>
        private string _onvifSnapUri = "";

        /// <summary>抓图地址可用吗（false 时直接走 RTSP 抽帧，不再白问一遍）。</summary>
        private bool _onvifSnapOk = true;

        // ==================== 入口 ====================

        /// <summary>
        /// 「ONVIF 订阅」模式：挂着设备的 PullPoint 订阅，**有变化才分析**。
        /// </summary>
        private void StartOnvifMode(string url)
        {
            if (_onvifRunning) return;
            _onvifRunning = true;

            string rtspUrl, user, pwd;
            ParseUrl(url, out rtspUrl, out user, out pwd);

            // 从 rtsp://user:pwd@1.2.3.4:554/... 里扒出 IP
            string ip = HostOf(url);
            if (ip.Length == 0)
            {
                SetStoppedBecause("ONVIF 订阅需要能从地址里解析出 IP。地址格式：rtsp://账号:密码@192.168.1.10:554/…");
                return;
            }

            Log.Info("AI 实时巡检：ONVIF 订阅模式启动，目标 " + ip);

            _onvifThread = new Thread(delegate ()
            {
                try { OnvifLoop(ip, user, pwd); }
                catch (Exception ex)
                {
                    string m = "";
                    try { m = ex.Message; } catch (Exception) { }
                    Log.Warn("ONVIF 订阅循环出错：" + m);
                    Ui(delegate () { _statusLab.Text = "ONVIF 订阅出错：" + m; });
                }
                finally { _onvifRunning = false; }
            });
            _onvifThread.IsBackground = true;
            _onvifThread.Start();
        }

        /// <summary>失败时把界面恢复成"可以重新开始"的样子，并说明原因。</summary>
        private void SetStoppedBecause(string why)
        {
            _onvifRunning = false;
            Ui(delegate ()
            {
                _running = false;
                _stop = true;
                SetRunLock(false);
                _statusLab.Text = why;
                AppendAlert(DateTime.Now, "无法开始", why, Color.Firebrick);
            });
        }

        // ==================== 主循环 ====================

        private void OnvifLoop(string ip, string user, string pwd)
        {
            // ── ① 找设备服务地址 ──
            //    ★ **必须走发现协议** ✗ 不能猜端口 ✓
            //      TP-Link 的 ONVIF 在 2020 ✗ 标准是 80 ✓ 猜不出来 ✓
            Ui(delegate () { _statusLab.Text = "ONVIF：正在发现设备…"; });
            string svc = "";
            foreach (OnvifDevice d in OnvifClient.Discover(6000))
            {
                if (d.Host == ip) { svc = d.Address; break; }
            }
            if (svc.Length == 0)
            {
                // 发现失败不要马上放弃 ✓ 组播跨网段会失效 ✗ 但手填 IP 仍然能用 ✓
                svc = "http://" + ip + "/onvif/device_service";
                Log.Info("ONVIF：组播没发现设备，改用标准端口猜一次 " + svc);
            }
            Log.Info("ONVIF：设备服务 " + svc);

            // ── ② 拿事件服务地址 ──
            Ui(delegate () { _statusLab.Text = "ONVIF：正在查询事件服务…"; });
            string ev = OnvifEvents.GetEventService(svc, user, pwd);
            if (ev.Length == 0)
            {
                // ★ 设备不支持事件订阅 —— 这是**正常情况** ✓ 不是错误 ✓
                //   便宜的摄像头 ONVIF 经常是半成品 ✗ 能发现但没事件服务 ✓
                SetStoppedBecause("这台设备的 ONVIF 没有事件服务，用不了「ONVIF 订阅」。"
                    + "建议改用「定时轮询」，或者（海康/大华）用「报警接收」。");
                return;
            }
            Log.Info("ONVIF：事件服务 " + ev);

            // ── ③ 建订阅 ──
            OnvifSubscription sub = OnvifEvents.Create(ev, user, pwd, 300);
            if (sub == null)
            {
                SetStoppedBecause("ONVIF 订阅建立失败。账号密码不对，或者设备拒绝了订阅请求。"
                    + "（注：有些设备用 HTTP 400 表示密码错，不代表协议有问题）");
                return;
            }
            _onvifRebuild = 0;
            Log.Info("ONVIF：订阅成功 " + sub.Address);

            // ── ④ 主循环 ──
            int lastWatchdog = Environment.TickCount;
            while (!_stop && _running)
            {
                // 4a 看门狗：订阅会到期 ✗ 到期就没事件了 ✓ 必须续期或重建 ✓
                int now = Environment.TickCount;
                if ((int)(now - lastWatchdog) > 20000)
                {
                    lastWatchdog = now;
                    if (sub.NeedsRefresh)
                    {
                        bool renewed = OnvifEvents.Renew(sub, user, pwd, 300);
                        if (!renewed)
                        {
                            // ★ 续期失败 → **重建**（比依赖 Renew 可靠 ✓ 各家实现差异大 ✓）
                            Log.Info("ONVIF：续期没成功，重建订阅");
                            OnvifEvents.Unsubscribe(sub, user, pwd);
                            OnvifSubscription ns = OnvifEvents.Create(ev, user, pwd, 300);
                            if (ns == null)
                            {
                                _onvifRebuild++;
                                Ui(delegate () { _statusLab.Text = "ONVIF：订阅重建失败，10 秒后再试…"; });
                                SleepInterruptible(10000);
                                continue;
                            }
                            sub = ns;
                            _onvifRebuild++;
                            Log.Info("ONVIF：订阅已重建 " + sub.Address);
                        }
                    }
                }

                // 4b 拉消息（阻塞最多 8 秒；有事件会立刻返回 ✓）
                List<OnvifEvent> evs = OnvifEvents.Pull(sub, user, pwd, 8, 20);
                if (_stop || !_running) break;

                if (evs.Count > 0)
                {
                    HandleOnvifEvents(evs, user, pwd);
                }
                else
                {
                    // 没事件也要让界面知道"还活着" ✓ 不然用户以为卡死了 ✓
                    Ui(delegate ()
                    {
                        _previewLab.Text = "ONVIF 订阅中 · 已收到 " + _onvifRecv + " 条事件"
                            + (_onvifMerged > 0 ? "（合并掉 " + _onvifMerged + " 条）" : "")
                            + (_onvifRebuild > 0 ? " · 订阅重建 " + _onvifRebuild + " 次" : "");
                    });
                }
            }

            OnvifEvents.Unsubscribe(sub, user, pwd);
            Log.Info("ONVIF 订阅已停止");
        }

        /// <summary>可被打断的等待（用户点停止要能立刻退出 ✓ 不能干睡 10 秒 ✗）。</summary>
        private void SleepInterruptible(int ms)
        {
            int step = 200;
            int left = ms;
            while (left > 0 && !_stop && _running)
            {
                Thread.Sleep(Math.Min(step, left));
                left -= step;
            }
        }

        // ==================== 处理一批事件 ====================

        /// <summary>
        /// ★ 一次"画面变化"会推好几条事件 ✗
        ///   MotionAlarm + CellMotionDetector/Motion + Tamper 一起来 ✓
        ///   如果每条都送 AI，一次变化要跑三次推理 ✓ AI 一次 1~3 秒 ✓ 直接堵住 ✓✓
        ///   所以：**先合并成一次**，再决定要不要分析 ✓
        /// </summary>
        private void HandleOnvifEvents(List<OnvifEvent> evs, string user, string pwd)
        {
            List<OnvifEvent> worth = new List<OnvifEvent>();
            for (int i = 0; i < evs.Count; i++)
            {
                OnvifEvent e = evs[i];
                _onvifRecv++;
                string line = e.Chinese + "（" + e.Topic + "）";
                if (e.WorthAnalyzing)
                {
                    worth.Add(e);
                    AppendAlert(DateTime.Now, "触发", line, Color.DarkOrange);
                }
                else
                {
                    // 设备状态类（CPU、时钟同步、重启…）—— **不占主记录** ✓ 进小窗 ✓
                    Trace("心跳", line, Color.Gray);
                }
            }

            if (worth.Count == 0)
            {
                Ui(delegate ()
                {
                    _statusLab.Text = "ONVIF 订阅中 · 已收到 " + _onvifRecv + " 条事件（最近都是设备状态类，不分析）";
                });
                return;
            }

            // 合并：这批里多个"画面变化"事件只算一次
            if (worth.Count > 1) _onvifMerged += worth.Count - 1;
            OnvifEvent lead = worth[0];
            string names = lead.Chinese;
            if (worth.Count > 1) names += " 等 " + worth.Count + " 类";

            if (!_running || _stop) return;

            // 去抖：和分析上一轮至少隔 3 秒（AI 一次 1~3 秒，来太快会堵 ✓）
            int now = Environment.TickCount;
            if (_onvifLastAnalyze != 0 && (int)(now - _onvifLastAnalyze) < 3000)
            {
                _onvifMerged += worth.Count;
                return;
            }
            _onvifLastAnalyze = now;

            Ui(delegate ()
            {
                _statusLab.Text = "ONVIF 事件「" + names + "」→ 正在抓图分析…";
                _previewLab.Text = "ONVIF 订阅中 · 已收到 " + _onvifRecv + " 条事件 · 最近 " + names;
            });
            Log.Info("AI 实时巡检：ONVIF 事件 " + lead.Topic + "（本批 " + worth.Count + " 条）");

            string question = _promptBox.Text.Trim();
            if (question.Length == 0) question = DefaultPrompt;
            string url = ReadUrl();      // ★ 读真实值 ✗ 不能读 _urlBox.Text（那里是打码的 ✓）

            Thread t = new Thread(delegate ()
            {
                try { AnalyzeOnvifEvent(names, question, url, user, pwd); }
                catch (Exception ex)
                {
                    string m = "";
                    try { m = ex.Message; } catch (Exception) { }
                    Ui(delegate () { _statusLab.Text = "ONVIF 事件分析出错：" + m; });
                    Log.Warn("ONVIF 事件分析出错：" + m);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>抓图 → 送 AI → 写告警。</summary>
        private void AnalyzeOnvifEvent(string evName, string question, string url, string user, string pwd)
        {
            byte[] jpg = GrabFrameForOnvif(url, user, pwd);
            if (jpg == null || jpg.Length == 0)
            {
                Ui(delegate ()
                {
                    _statusLab.Text = "ONVIF 事件「" + evName + "」抓图失败（ONVIF 抓图和 RTSP 抽帧都没拿到）";
                    AppendAlert(DateTime.Now, "抓图失败", "事件：" + evName + "，两条抓图路都不通", Color.Firebrick);
                });
                return;
            }
            Ui(delegate () { ShowPreview(jpg); });

            // ★★ 必须缩放 ✗ —— 这一步我漏了，实测 AI 要跑 30 秒 ✓ 用户会以为卡死 ✓
            //
            //   海康的 ONVIF 抓图返回的是**全分辨率原图** ✗
            //   实测日志：图片=615KB(base64 820KB) → 耗时 30021 ms ✗
            //   本地 7B 模型处理这么大的图要几十秒 ✓
            //
            //   （RTSP 那条路用的是 ExtractFrameJpeg(url, 0, 1280) ✓ 内部已经缩过了 ✓
            //     所以只有 ONVIF 这条路少了这一步 ✓）
            //
            //   ★ 讽刺的是：报警接收那条路**早就解决过同一个问题** ✗
            //     那里的注释写着「实测 2688x1520 = 289KB，直接送 AI 要 29.7 秒，
            //     缩到 1280 宽后通常 2~4 秒」✓
            //     —— 我做 ONVIF 这条路时忘了照抄 ✓ 于是又踩了一遍 ✓
            //     教训：新写一段"和已有路径做同样事"的代码时，
            //           **去把那条路径完整读一遍** ✗ 别只照着大概写 ✓
            byte[] smallJpg = ResizeJpeg(jpg, 1280);
            if (smallJpg != null && smallJpg.Length > 0) jpg = smallJpg;

            string holder;
            if (!MageCheck.TryAcquire("AI 实时巡检", out holder))
            {
                Ui(delegate () { _statusLab.Text = "AI 正被「" + holder + "」占用，本条事件跳过"; });
                return;
            }
            string ans; int ms;
            try
            {
                DateTime t0 = DateTime.Now;
                ans = MageCheck.QueryOllama(AppSettings.OllamaUrl, _runModel, jpg, question, 768);
                ms = (int)(DateTime.Now - t0).TotalMilliseconds;
            }
            finally { MageCheck.Release("AI 实时巡检"); }

            bool alert; string desc = ParseAnswer(ans, out alert);
            if (_stop) return;
            string n2 = evName;
            Ui(delegate () { FinishRound("【ONVIF·" + n2 + "】" + desc, alert, ms, url); });
        }

        /// <summary>
        /// 抓一帧。**两条路，按顺序试** ✓
        ///   ① ONVIF 标准抓图（海康实测可用 ✓ 不建 RTSP 会话 ✓ 最省摄像头 ✓）
        ///   ② RTSP 抽帧（ONVIF 抓图不支持的设备走这条 ✗ 比如 TP-Link ✓）
        /// ★ 抓图地址**只问一次** ✓ 不要每个事件都问 ✗
        /// </summary>
        private byte[] GrabFrameForOnvif(string rtspUrl, string user, string pwd)
        {
            // ① ONVIF 抓图
            if (_onvifSnapOk)
            {
                if (_onvifSnapUri.Length == 0)
                {
                    // 第一次：问设备要抓图地址
                    string svc = "";
                    string ip = HostOf(rtspUrl);
                    foreach (OnvifDevice d in OnvifClient.Discover(5000))
                        if (d.Host == ip) { svc = d.Address; break; }
                    if (svc.Length == 0) svc = "http://" + ip + "/onvif/device_service";

                    // ★ 媒体服务 + profile token：从 OnvifClient 正经拿（不在这里重写一遍 ✗）
                    string media, token, err2;
                    if (!OnvifClient.GetMediaAndProfile(svc, user, pwd, out media, out token, out err2))
                    {
                        _onvifSnapOk = false;
                        Log.Info("ONVIF 抓图不可用（" + err2 + "），改用 RTSP 抽帧");
                    }
                    else
                    {
                        _onvifSnapUri = OnvifSnapshot.GetSnapshotUri(media, token, user, pwd, out err2);
                        if (_onvifSnapUri.Length == 0)
                        {
                            _onvifSnapOk = false;
                            Log.Info("ONVIF 抓图不可用（" + err2 + "），改用 RTSP 抽帧");
                        }
                        else
                        {
                            Log.Info("ONVIF 抓图地址 " + _onvifSnapUri);
                        }
                    }
                }
                if (_onvifSnapUri.Length > 0)
                {
                    string err;
                    byte[] j = OnvifSnapshot.Grab(_onvifSnapUri, user, pwd, out err);
                    if (j != null && j.Length > 0) return j;
                    // 抓图突然失败（设备重启/改配置）→ 下次重新问地址
                    _onvifSnapUri = "";
                    Log.Info("ONVIF 抓图失败（" + err + "），这次改用 RTSP 抽帧");
                }
            }

            // ② RTSP 抽帧兜底
            try
            {
                return MageCheck.ExtractFrameJpeg(rtspUrl, 0, 1280);
            }
            catch (Exception e)
            {
                string m = "";
                try { m = e.Message; } catch (Exception) { }
                Log.Warn("RTSP 抽帧也失败：" + m);
                return null;
            }
        }

        // ==================== 小工具 ====================


        private static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int ss = url.IndexOf("://");
            if (ss < 0) return "";
            string rest = url.Substring(ss + 3);
            int at = rest.IndexOf('@');
            if (at >= 0) rest = rest.Substring(at + 1);
            int slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest.Substring(0, slash);
            int colon = rest.IndexOf(':');
            if (colon >= 0) rest = rest.Substring(0, colon);
            return rest.Trim();
        }

        /// <summary>从 rtsp://user:pwd@ip:port/… 里拆出三部分。</summary>
        private static void ParseUrl(string url, out string clean, out string user, out string pwd)
        {
            clean = url; user = ""; pwd = "";
            if (string.IsNullOrEmpty(url)) return;
            int ss = url.IndexOf("://");
            if (ss < 0) return;
            string rest = url.Substring(ss + 3);
            int at = rest.IndexOf('@');
            if (at < 0) return;
            string ui = rest.Substring(0, at);
            int c = ui.IndexOf(':');
            if (c >= 0) { user = ui.Substring(0, c); pwd = ui.Substring(c + 1); }
            else user = ui;
            try
            {
                user = Uri.UnescapeDataString(user);
                pwd = Uri.UnescapeDataString(pwd);
            }
            catch (Exception) { }
            clean = url.Substring(0, ss + 3) + rest.Substring(at + 1);
        }
    }
}
