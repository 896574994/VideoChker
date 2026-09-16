/* -*- coding: utf-8 -*-
 * PdfExport.cs — 报告 PDF 导出（无第三方库）
 * 方案：GDI+ 把报告渲染成 A4 页位图 → 每页存 JPEG → 手写 PDF 规范把 JPEG 嵌入。
 * 中文用系统字体（微软雅黑）绘制成图，任何 Windows 都能正常显示。
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace VideoChecker
{
    public static class PdfExport
    {
        // A4 纵向 @150DPI
        private const int PW = 1240;
        private const int PH = 1754;
        private const int Margin = 60;
        private const int BottomMargin = 60;

        public static string ExportPdf(List<MediaReport> reports, string outPath, string modeDesc)
        {
            int[] g = new int[4];
            foreach (MediaReport rep in reports)
            {
                if (rep.Overall == "PASS") g[0]++;
                else if (rep.Overall == "WARN") g[1]++;
                else if (rep.Overall == "FAIL") g[2]++;
                else g[3]++;
            }
            Renderer r = new Renderer(modeDesc, g);
            foreach (MediaReport rep in reports)
                r.DrawReport(rep);
            List<byte[]> pages = r.Finish();
            WritePdf(pages, outPath);
            return outPath;
        }

        // ==================== 渲染 ====================

        private sealed class Renderer : IDisposable
        {
            private readonly List<byte[]> _pages = new List<byte[]>();
            private Bitmap _bmp;
            private Graphics _g;
            private int _y;
            private readonly string _modeDesc;
            private readonly int[] _globalCounts;

            private static readonly Font FTitle = new Font("Microsoft YaHei", 24F, FontStyle.Bold);
            private static readonly Font FSub = new Font("Microsoft YaHei", 11F);
            private static readonly Font FName = new Font("Microsoft YaHei", 13F, FontStyle.Bold);
            private static readonly Font FMeta = new Font("Microsoft YaHei", 10F);
            private static readonly Font FItem = new Font("Microsoft YaHei", 10F);
            private static readonly Font FItemName = new Font("Microsoft YaHei", 10F, FontStyle.Bold);
            /// <summary>
            /// 状态统计格里的数字字体。
            /// 早先是在 DrawText 调用里直接 new Font(...) —— 每画一个格子就新建一个 GDI+ 字体对象且从不释放，
            /// 报告页数一多就持续泄漏 ✗ 改成和上面几个一样做成静态只读字段。
            /// </summary>
            private static readonly Font FStatNum = new Font("Microsoft YaHei", 13F, FontStyle.Bold);

            private static readonly Color CPass = Color.FromArgb(22, 163, 74);
            private static readonly Color CWarn = Color.FromArgb(217, 119, 6);
            private static readonly Color CFail = Color.FromArgb(220, 38, 38);
            private static readonly Color CSkip = Color.FromArgb(107, 114, 128);
            private static readonly Color CLine = Color.FromArgb(229, 231, 235);
            private static readonly Color CText = Color.FromArgb(31, 41, 55);
            private static readonly Color CMeta = Color.FromArgb(107, 114, 128);

            public Renderer(string modeDesc, int[] globalCounts)
            {
                _modeDesc = modeDesc;
                _globalCounts = globalCounts;
                StartPage();
            }

            private void StartPage()
            {
                _bmp = new Bitmap(PW, PH);
                _g = Graphics.FromImage(_bmp);
                _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                _g.SmoothingMode = SmoothingMode.AntiAlias;
                _g.Clear(Color.White);
                _y = Margin;
                DrawPageHeader();
            }

            private void FinishPage()
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    _bmp.Save(ms, ImageFormat.Jpeg);
                    _pages.Add(ms.ToArray());
                }
                _bmp.Dispose();
                _g.Dispose();
                _bmp = null;
                _g = null;
            }

            private void EnsureSpace(int need)
            {
                if (_y + need > PH - BottomMargin)
                {
                    FinishPage();
                    StartPage();
                }
            }

            private void DrawPageHeader()
            {
                TextRenderer.DrawText(_g, "视频核对报告", FTitle, new Point(Margin, _y), CText);
                _y += 46;
                string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                TextRenderer.DrawText(_g, "生成时间：" + timeStr + "    检测模式：" + _modeDesc + "    工具：" + AppInfo.Title + " C# 图形版 " + AppInfo.Version,
                    FSub, new Point(Margin, _y), CMeta);
                _y += 24;
                DrawSummaryBar();
                _y += 62;
                DrawHLine();
                _y += 14;
            }

            private void DrawSummaryBar()
            {
                string[] labels = { "通过 PASS", "警告 WARN", "失败 FAIL", "无法分析" };
                Color[] colors = { CPass, CWarn, CFail, CSkip };
                int[] counts = _globalCounts;
                int bw = (PW - Margin * 2 - 3 * 16) / 4;
                int x = Margin;
                int bh = 44;
                for (int i = 0; i < 4; i++)
                {
                    using (SolidBrush br = new SolidBrush(colors[i]))
                    using (GraphicsPath gp = RoundedRect(new Rectangle(x, _y, bw, bh), 8))
                    {
                        _g.FillPath(br, gp);
                    }
                    TextRenderer.DrawText(_g, labels[i], FSub, new Rectangle(x, _y + 5, bw, 24), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(_g, counts[i].ToString(), FStatNum,
                        new Rectangle(x, _y + 20, bw, 24), Color.White, TextFormatFlags.HorizontalCenter);
                    x += bw + 16;
                }
            }

            private readonly List<MediaReport> _reportsSoFar = new List<MediaReport>();

            public void DrawReport(MediaReport rep)
            {
                // 徽章 + 文件名
                Color sc = StatusColor(rep.Overall);
                int badgeW = 96, badgeH = 28;
                EnsureSpace(44);
                using (SolidBrush br = new SolidBrush(sc))
                using (GraphicsPath gp = RoundedRect(new Rectangle(Margin, _y, badgeW, badgeH), 6))
                {
                    _g.FillPath(br, gp);
                }
                TextRenderer.DrawText(_g, rep.Overall, FName, new Rectangle(Margin + 4, _y - 2, badgeW - 8, badgeH + 4), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(_g, Path.GetFileName(rep.File), FName,
                    new Rectangle(Margin + badgeW + 14, _y, PW - Margin - 120 - badgeW, 30), CText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(_g, "合格率 " + rep.PassRateText + "（" + rep.PassCount + "/" + rep.TotalCount + "，及格线 " +
                    (int)Math.Round(MediaReport.PassThreshold * 100) + "%）", FMeta,
                    new Rectangle(Margin + badgeW + 14, _y + 17, PW - Margin - 120 - badgeW, 18), CText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                _y += 38;

                // 错误区块
                if (rep.Error.Length > 0)
                {
                    EnsureSpace(26);
                    TextRenderer.DrawText(_g, "无法分析：" + rep.Error, FMeta,
                        new Rectangle(Margin + 10, _y, PW - Margin * 2 - 20, 60), CFail, TextFormatFlags.WordBreak);
                    _y += 30;
                    _y += 14;
                    DrawHLine();
                    _y += 14;
                    return;
                }

                // 媒体信息
                EnsureSpace(24);
                Dictionary<string, object> info = rep.MediaInfo;
                object v;
                string meta = "容器：" + Str(info, "container");
                if (info != null && info.TryGetValue("duration", out v) && v != null)
                    meta += "    时长：" + Engine.FmtSec(Ffmpeg.SafeF(v));
                if (info != null && info.TryGetValue("size", out v) && v != null)
                    meta += "    大小：" + Engine.FmtSize(Ffmpeg.SafeF(v));
                Dictionary<string, object> vi = info != null && info.ContainsKey("video") ? info["video"] as Dictionary<string, object> : null;
                if (vi != null)
                    meta += "    视频：" + Str(vi, "codec") + " " + Str(vi, "width") + "x" + Str(vi, "height") + " @" + Str(vi, "fps") + "fps";
                TextRenderer.DrawText(_g, meta, FMeta, new Rectangle(Margin + 10, _y, PW - Margin * 2 - 20, 20), CMeta, TextFormatFlags.EndEllipsis);
                _y += 26;

                // 异常时间轴（黑帧/冻结/无声）
                DrawTimeline(rep);

                // 检查项表头
                EnsureSpace(24);
                TextRenderer.DrawText(_g, "检查项", FItemName, new Rectangle(Margin + 10, _y, 160, 20), CMeta);
                TextRenderer.DrawText(_g, "状态", FItemName, new Rectangle(Margin + 180, _y, 80, 20), CMeta);
                TextRenderer.DrawText(_g, "详情", FItemName, new Rectangle(Margin + 270, _y, PW - Margin * 2 - 280, 20), CMeta);
                _y += 22;
                DrawHLine();

                // 检查项
                foreach (CheckItem it in rep.Items)
                {
                    string detail = it.Name + "：" + it.Detail;
                    Size sz = TextRenderer.MeasureText(_g, detail, FItem, new Size(PW - Margin * 2 - 280, 200),
                        TextFormatFlags.WordBreak);
                    int rowH = Math.Max(24, sz.Height + 10);
                    EnsureSpace(rowH);
                    TextRenderer.DrawText(_g, it.Name, FItemName, new Rectangle(Margin + 10, _y + 3, 160, 20), CText);
                    TextRenderer.DrawText(_g, it.Status, FItem, new Rectangle(Margin + 180, _y + 3, 80, 20),
                        StatusColor(it.Status), TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(_g, detail, FItem, new Rectangle(Margin + 270, _y + 3, PW - Margin * 2 - 280, 200),
                        CText, TextFormatFlags.WordBreak);
                    _y += rowH;
                    DrawHLine();
                }

                _y += 10;
            }

            private static string Str(Dictionary<string, object> obj, string key)
            {
                object val;
                if (obj != null && obj.TryGetValue(key, out val) && val != null)
                    return Convert.ToString(val);
                return "?";
            }

            // ---- 异常时间轴（黑帧 / 冻结 / 无声段）----

            /// <summary>
            /// 取时间段对象的某个字段值，取不到返回 -1。
            /// 原实现用反射 GetField(字段名) —— 那在混淆改名后会直接失效，
            /// 改成按类型直接取值（与 ReportHtml 里的同名方法保持一致）。
            /// </summary>
            private static double GetSegNum(object o, string field)
            {
                Seg s1 = o as Seg;
                if (s1 != null)
                {
                    if (field == "Start") return s1.Start.HasValue ? s1.Start.Value : -1;
                    if (field == "End") return s1.End.HasValue ? s1.End.Value : -1;
                    return -1;
                }
                Seg3 s3 = o as Seg3;
                if (s3 != null)
                {
                    if (field == "Start") return s3.Start;
                    if (field == "End") return s3.End;
                    if (field == "Duration") return s3.Duration;
                    return -1;
                }
                return -1;
            }

            private static List<double[]> CollectSegs(MediaReport rep, string itemName)
            {
                List<double[]> res = new List<double[]>();
                foreach (CheckItem it in rep.Items)
                {
                    if (it.Name != itemName || it.Value == null || !it.Value.ContainsKey("segments")) continue;
                    System.Collections.IList list = it.Value["segments"] as System.Collections.IList;
                    if (list == null) continue;
                    foreach (object o in list)
                    {
                        double[] arr = o as double[];
                        if (arr != null && arr.Length >= 2)
                        {
                            res.Add(new double[] { arr[0], arr[1] });
                            continue;
                        }
                        double s = GetSegNum(o, "Start");
                        if (s < 0) continue;
                        double e = GetSegNum(o, "End");
                        if (e < 0)
                        {
                            double d = GetSegNum(o, "Duration");
                            e = d > 0 ? s + d : -1;
                        }
                        res.Add(new double[] { s, e });
                    }
                }
                return res;
            }

            private static List<double[]> CollectAiFrames(MediaReport rep)
            {
                List<double[]> res = new List<double[]>();
                foreach (CheckItem it in rep.Items)
                {
                    if (it.Name != "AI 画面理解" || it.Value == null || !it.Value.ContainsKey("ai_frames")) continue;
                    System.Collections.IList list = it.Value["ai_frames"] as System.Collections.IList;
                    if (list == null) continue;
                    foreach (object o in list)
                    {
                        double[] arr = o as double[];
                        if (arr != null && arr.Length >= 2) res.Add(new double[] { arr[0], arr[1] });
                    }
                }
                return res;
            }

            private void DrawTimeline(MediaReport rep)
            {
                double dur = 0;
                object v;
                if (rep.MediaInfo != null && rep.MediaInfo.TryGetValue("duration", out v) && v != null)
                    dur = Convert.ToDouble(v);
                if (dur <= 0.5) return;

                List<double[]> black = CollectSegs(rep, "黑帧检测");
                List<double[]> freeze = CollectSegs(rep, "冻结帧检测");
                List<double[]> silence = CollectSegs(rep, "静音分布");
                List<double[]> lowVol = CollectSegs(rep, "音量过低");
                List<double[]> flower = CollectSegs(rep, "视频解码");
                List<double[]> aiBad = new List<double[]>();
                foreach (double[] fr in CollectAiFrames(rep))
                    if (fr[1] == 1 || fr[1] == 2) aiBad.Add(fr);
                if (black.Count == 0 && freeze.Count == 0 && silence.Count == 0
                    && lowVol.Count == 0 && flower.Count == 0 && aiBad.Count == 0) return;

                int rows = (black.Count > 0 ? 1 : 0) + (freeze.Count > 0 ? 1 : 0) + (silence.Count > 0 ? 1 : 0)
                    + (lowVol.Count > 0 ? 1 : 0) + (flower.Count > 0 ? 1 : 0) + (aiBad.Count > 0 ? 1 : 0);
                EnsureSpace(24 + rows * 22 + 24);
                int tlY = _y;
                int tlW = PW - Margin * 2 - 20;
                int labelW = 110;   // 左侧标签区
                int barX = Margin + 10 + labelW;
                int barW = tlW - labelW - 10;
                int barH = 12;

                // 标题
                TextRenderer.DrawText(_g, "异常时间轴（黑帧 / 冻结 / 无声 / 花屏 / 低音量 / AI异常帧）", FItemName,
                    new Rectangle(Margin + 10, tlY, tlW, 20), CMeta);
                tlY += 22;

                AppendRowG(black, "黑帧", dur, barX, barW, barH, Color.FromArgb(23, 24, 28), ref tlY, false);
                AppendRowG(freeze, "冻结", dur, barX, barW, barH, Color.FromArgb(124, 58, 237), ref tlY, false);
                AppendRowG(silence, "无声", dur, barX, barW, barH, Color.FromArgb(203, 213, 225), ref tlY, false);
                AppendRowG(flower, "花屏", dur, barX, barW, barH, Color.FromArgb(220, 38, 38), ref tlY, false);
                AppendRowG(lowVol, "低音量", dur, barX, barW, barH, Color.FromArgb(245, 158, 11), ref tlY, false);
                AppendRowG(aiBad, "AI异常", dur, barX, barW, barH, Color.FromArgb(225, 29, 72), ref tlY, true);

                // 时间刻度
                TextRenderer.DrawText(_g, "0s", FMeta, new Rectangle(barX, tlY, 40, 16), CMeta);
                TextRenderer.DrawText(_g, (dur / 2).ToString("0.#") + "s", FMeta,
                    new Rectangle(barX + barW / 2 - 20, tlY, 40, 16), CMeta, TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(_g, dur.ToString("0.#") + "s", FMeta,
                    new Rectangle(barX + barW - 40, tlY, 40, 16), CMeta, TextFormatFlags.HorizontalCenter);

                _y = tlY + 20;
            }

            private void AppendRowG(List<double[]> segs, string label, double dur, int barX, int barW, int barH,
                Color c, ref int y, bool asPoints)
            {
                if (segs.Count == 0) return;
                // 标签
                TextRenderer.DrawText(_g, label + " " + segs.Count + (asPoints ? "帧" : "段"), FMeta,
                    new Rectangle(Margin + 10, y + 1, 106, 18), CText);
                // 底条
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(238, 241, 245)))
                    _g.FillRectangle(bg, barX, y, barW, barH);
                // 色块 / 点标记
                using (SolidBrush br = new SolidBrush(c))
                {
                    foreach (double[] sg in segs)
                    {
                        double s = sg[0];
                        if (asPoints)
                        {
                            float xp = (float)(barX + s / dur * barW);
                            _g.FillRectangle(br, xp - 2, y - 1, 4, barH + 2);
                            continue;
                        }
                        double e = sg[1] > 0 ? sg[1] : dur;
                        if (e <= s) continue;
                        double x = barX + s / dur * barW;
                        double w = (e - s) / dur * barW;
                        if (w < 1) w = 1;
                        _g.FillRectangle(br, (float)x, y, (float)w, barH);
                    }
                }
                y += 22;
            }

            private static Color StatusColor(string s)
            {
                if (s == "PASS") return CPass;
                if (s == "WARN") return CWarn;
                if (s == "FAIL") return CFail;
                return CSkip;
            }

            private void DrawHLine()
            {
                using (Pen p = new Pen(CLine))
                    _g.DrawLine(p, Margin, _y, PW - Margin, _y);
                _y += 1;
            }

            private static GraphicsPath RoundedRect(Rectangle r, int rad)
            {
                GraphicsPath gp = new GraphicsPath();
                int d = rad * 2;
                gp.AddArc(r.X, r.Y, d, d, 180, 90);
                gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                gp.CloseFigure();
                return gp;
            }

            public List<byte[]> Finish()
            {
                if (_g != null) FinishPage();
                return _pages;
            }

            public void Dispose()
            {
                if (_g != null) FinishPage();
            }
        }

        // ==================== PDF 写入 ====================

        private static void WritePdf(List<byte[]> pages, string outPath)
        {
            using (FileStream fs = File.Create(outPath))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                List<long> offsets = new List<long>();

                WriteAscii(bw, "%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");
                int nextObj = 1;

                // 1: Catalog
                offsets.Add(fs.Position);
                WriteAscii(bw, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                // 2: Pages
                offsets.Add(fs.Position);
                StringBuilder kids = new StringBuilder();
                for (int i = 0; i < pages.Count; i++)
                    kids.Append((3 + i * 3) + " 0 R ");
                WriteAscii(bw, "2 0 obj\n<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count " + pages.Count + " >>\nendobj\n");

                // 每页 3 个对象：Page / Image / Contents
                nextObj = 3;
                for (int i = 0; i < pages.Count; i++)
                {
                    int pageObj = nextObj++;
                    int imgObj = nextObj++;
                    int contentObj = nextObj++;
                    byte[] jpg = pages[i];

                    offsets.Add(fs.Position);
                    WriteAscii(bw, pageObj + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] "
                        + "/Resources << /XObject << /Im" + (i + 1) + " " + imgObj + " 0 R >> >> /Contents " + contentObj + " 0 R >>\nendobj\n");

                    offsets.Add(fs.Position);
                    WriteAscii(bw, imgObj + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + PW + " /Height " + PH
                        + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpg.Length + " >>\nstream\n");
                    bw.Write(jpg);
                    WriteAscii(bw, "\nendstream\nendobj\n");

                    offsets.Add(fs.Position);
                    string content = "q\n595.28 0 0 841.89 0 0 cm\n/Im" + (i + 1) + " Do\nQ\n";
                    byte[] cb = Encoding.ASCII.GetBytes(content);
                    WriteAscii(bw, contentObj + " 0 obj\n<< /Length " + cb.Length + " >>\nstream\n");
                    bw.Write(cb);
                    WriteAscii(bw, "endstream\nendobj\n");
                }

                // xref
                long xrefPos = fs.Position;
                WriteAscii(bw, "xref\n0 " + nextObj + "\n");
                WriteAscii(bw, "0000000000 65535 f \n");
                for (int i = 0; i < offsets.Count; i++)
                    WriteAscii(bw, offsets[i].ToString("D10") + " 00000 n \n");
                WriteAscii(bw, "trailer\n<< /Size " + nextObj + " /Root 1 0 R >>\nstartxref\n" + xrefPos + "\n%%EOF\n");
            }
        }

        private static void WriteAscii(BinaryWriter bw, string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            bw.Write(b);
        }
    }
}
