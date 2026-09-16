/* -*- coding: utf-8 -*-
 * AppSettings.cs — 全局共享设置（单一来源，落盘持久化）
 *
 * 解决的问题：原先 Ollama 地址 / 模型 / 抽帧参数在三个地方各配一遍
 * （主窗体「AI 配置」行、内容理解页、RTSP 页），改一处另外两处不知道。
 * 现在统一放这里，任何页面改动都会同步到其它页面并写入 settings.ini。
 *
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VideoChecker
{
    public static class AppSettings
    {
        /// <summary>Ollama 服务地址。</summary>
        public static string OllamaUrl = "http://127.0.0.1:11434";

        /// <summary>视觉模型名（需带 vl）。</summary>
        public static string OllamaModel = "qwen2.5vl:7b";

        /// <summary>AI 画面理解 / 内容理解的抽帧张数。</summary>
        public static int MageFrames = 4;

        /// <summary>抽帧策略：auto / motion / uniform / full / custom。</summary>
        public static string MageStrategy = "auto";

        /// <summary>自定义抽帧时间点（策略为 custom 时使用）。</summary>
        public static List<double> CustomTimes = new List<double>();

        /// <summary>
        /// AI 实时巡检页的提示词。留空表示用 LiveAiForm 里的默认提示词。
        /// 放在设置里是为了让用户改完能记住 —— 不用每次开程序重打一遍。
        /// </summary>
        public static string LivePrompt = "";

        /// <summary>
        /// 「报警接收」用哪个本机地址填进摄像头的「上传中心」。
        ///
        /// ★ 2026-09-14 加（用户要求：自动获取IP + 手动设置IP）
        ///   用户说：**「能不能做一个自动获取IP和手动设置IP的功能进去，
        ///             这样可以防止局域网地址不对导致连不上的问题」**
        ///   ★ 原来只有"自动挑第一个"✗ 一台机器好几个网卡时，
        ///     挑错的那个 → 摄像头往本机收不到的地址推 → 一条报警都收不到 ✓
        ///   **空串 = 自动**（保持老行为，也保持默认）✓
        ///   非空 = 用户指定的那个地址 ✓ 换网络后它不在了**也不偷偷换** ✓
        /// </summary>
        public static string UploadIp = "";

        /// <summary>
        /// 记不记协议报文（"抓包文本"，2026-09-15 用户要求）。
        /// ★ 默认**开**：出问题时才发现没开就晚了 ✗
        ///   有 4MB/天 的上限 ✓ 也不会拖慢程序 ✓
        ///   （真嫌它占地方/嫌泄密的，可以在「日志」窗口里关掉 ✓）
        /// </summary>
        public static bool RecordTrace = true;

        /// <summary>设置变化通知（各页面据此刷新自己界面上的显示）。</summary>
        public static event Action Changed;

        private static string IniPath
        {
            get { return AppPaths.Data("settings.ini"); }
        }

        static AppSettings() { Load(); }

        /// <summary>策略的界面上名称 ↔ 内部代号。</summary>
        public static string StrategyToText(string key)
        {
            if (key == "motion") return "仅动静";
            if (key == "uniform") return "均匀";
            if (key == "full") return "全片分析";
            if (key == "custom") return "自定义(输入时间点)";
            return "智能(动静优先)";
        }

        public static string TextToStrategy(string text)
        {
            if (text == null) return "auto";
            if (text.IndexOf("仅动静", StringComparison.Ordinal) >= 0) return "motion";
            if (text.IndexOf("全片分析", StringComparison.Ordinal) >= 0) return "full";
            if (text.IndexOf("均匀", StringComparison.Ordinal) >= 0) return "uniform";
            if (text.IndexOf("自定义", StringComparison.Ordinal) >= 0) return "custom";
            return "auto";
        }

        /// <summary>改设置并通知所有页面。</summary>
        public static void Apply(string url, string model, int frames, string strategy, List<double> times)
        {
            bool changed = (OllamaUrl != url) || (OllamaModel != model)
                        || (MageFrames != frames) || (MageStrategy != strategy);
            OllamaUrl = url;
            OllamaModel = model;
            MageFrames = frames;
            MageStrategy = strategy;
            if (times != null) CustomTimes = new List<double>(times);
            Save();
            if (changed)
            {
                Action h = Changed;
                if (h != null) h();
            }
        }

        /// <summary>只改模型（页面上直接切换模型时用）。</summary>
        public static void SetModel(string model)
        {
            if (string.IsNullOrEmpty(model) || model == OllamaModel) return;
            OllamaModel = model;
            Save();
            Action h = Changed;
            if (h != null) h();
        }

        /// <summary>从 settings.ini 读取（读不到就用默认值）。</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                foreach (string raw in File.ReadAllLines(IniPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "ollama_url" && v.Length > 0) OllamaUrl = v;
                    else if (k == "ollama_model" && v.Length > 0) OllamaModel = v;
                    else if (k == "mage_frames")
                    {
                        int n;
                        if (int.TryParse(v, out n) && n >= 1 && n <= 200) MageFrames = n;
                    }
                    else if (k == "mage_strategy" && v.Length > 0) MageStrategy = v;
                    else if (k == "upload_ip") UploadIp = v;      // 允许为空 = 自动 ✓
                    else if (k == "net_trace") RecordTrace = (v != "0");
                    else if (k == "custom_times")
                    {
                        List<double> ts = new List<double>();
                        foreach (string p in v.Split(new char[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            double t;
                            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out t) && t >= 0) ts.Add(t);
                        }
                        ts.Sort();
                        CustomTimes = ts;
                    }
                }
                // ★ 把"记不记报文"应用到真正干活的那个开关（2026-09-15）
                //   为什么不直接在 NetTrace 里读 AppSettings ✗
                //     那样 NetTrace 就依赖 AppSettings ✓ 而 AppSettings 的静态构造
                //     又可能在别的地方被触发 ✓ 容易绕成环 ✓
                //   → 单向：设置 → 应用 → NetTrace ✓ 简单可预测 ✓
                NetTrace.Enabled = RecordTrace;   // ★ 字段叫 RecordTrace ✗ 别写成 NetTrace（会和类名撞）
                Log.Info("已载入设置：模型 " + OllamaModel + "，地址 " + OllamaUrl
                    + "，帧数 " + MageFrames + "，策略 " + StrategyToText(MageStrategy));
            }
            catch (Exception ex) { Log.Warn("读取 settings.ini 失败：" + ex.Message); }
        }

        /// <summary>写入 settings.ini。</summary>
        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 视频核对工具 设置（手工编辑亦可，改完重启程序生效）");
                sb.AppendLine("ollama_url=" + OllamaUrl);
                sb.AppendLine("ollama_model=" + OllamaModel);
                sb.AppendLine("mage_frames=" + MageFrames.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("mage_strategy=" + MageStrategy);
                StringBuilder t = new StringBuilder();
                for (int i = 0; i < CustomTimes.Count; i++)
                {
                    if (i > 0) t.Append(',');
                    t.Append(CustomTimes[i].ToString("0.###", CultureInfo.InvariantCulture));
                }
                sb.AppendLine("custom_times=" + t.ToString());
                sb.AppendLine("# 报警接收时填进摄像头的本机地址。留空 = 自动挑（推荐）。");
                sb.AppendLine("# 机器上有多个网卡、且自动挑的不是摄像头能连上的那个时，把它写成固定值。");
                sb.AppendLine("upload_ip=" + UploadIp);
                sb.AppendLine("# 记不记协议报文（1=记 0=不记）—— 排障用，见「日志」窗口里的「记录报文」");
                sb.AppendLine("net_trace=" + (RecordTrace ? "1" : "0"));
                File.WriteAllText(IniPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Warn("保存 settings.ini 失败：" + ex.Message); }
        }
    }
}
