/* -*- coding: utf-8 -*-
 * AppInfo.cs — 程序版本与制作信息（唯一来源）
 *
 * 以前版本号散落在启动页、主窗体标题、报告页脚等多处，改一次要翻好几个文件，
 * 结果就是"好久没更新"。现在全部从这里取，改版本只改这一个文件。
 *
 * C# 5 兼容语法。
 */
using System;

namespace VideoChecker
{
    public static class AppInfo
    {
        /// <summary>
        /// 版本号。**改了功能记得升这里** ——
        /// 早先一直没升，用户改完装上新版也分不清装的是哪一版 ✗
        /// 现在这个号会同时出现在：启动页 / 窗口标题 / 设置页 / 说明书 / 报告 / 说明文件
        /// </summary>
        public const string Version = "v10.21";

        /// <summary>发布日期。</summary>
        public const string BuildDate = "2026-09-15";

        /// <summary>程序名。</summary>
        public const string Title = "视频核对工具";

        /// <summary>一行式版本串，用于启动页/标题栏。</summary>
        public static string Short()
        {
            return Title + "  " + Version + "  ｜  " + BuildDate;
        }

        /// <summary>完整版本串，用于关于/说明页脚。</summary>
        public static string Full()
        {
            return Title + " " + Version + "（" + BuildDate + "）";
        }

        // ==================== 免责声明与第三方组件 ====================
        // 只在这里维护一份文本，说明书、安装包说明、界面关于页都从这里取 ——
        // 免得改了 A 处忘了 B 处（这种不一致在交接文档里已经吃过亏）。

        /// <summary>本程序使用的第三方组件。</summary>
        public const string ThirdParty =
            "本程序使用 FFmpeg（https://ffmpeg.org）进行音视频分析与解码。\n"
          + "FFmpeg 以 GPLv3 许可证分发，源码可从 https://ffmpeg.org/download.html 获取。\n"
          + "FFmpeg 是独立可执行程序，本程序通过命令行调用它，未对其做任何修改。";

        /// <summary>
        /// 给窗体装上程序图标（标题栏左上角 + 任务栏都用它）。
        ///
        /// 为什么需要（2026-09-12 用户指出）：
        ///   编译时加的 /win32icon 只让 **exe 文件本身**有图标 ✓（资源管理器里能看到）
        ///   但**窗体**的 Icon 属性默认是 WinForms 自带的那个通用图标 ✗
        ///   → 标题栏左上角和任务栏显示的还是老图标 ✓ 用户一眼就看出来了 ✓
        ///   所以每个 Form 的构造函数里都要显式装一次 ✓
        ///
        /// 用 Icon.ExtractAssociatedIcon 从自身 exe 取 —— 与 exe 图标永远一致 ✓
        /// 取不到就算了（不影响功能）✓ 用 _iconTried 避免反复失败重试 ✓
        /// </summary>
        public static void SetFormIcon(System.Windows.Forms.Form f)
        {
            if (f == null) return;
            try
            {
                if (_appIcon == null && !_iconTried)
                {
                    _iconTried = true;
                    _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
                }
                if (_appIcon != null) f.Icon = _appIcon;
            }
            catch (Exception) { }
        }

        private static System.Drawing.Icon _appIcon;
        private static bool _iconTried;
        /// <summary>使用范围与隐私责任声明。</summary>
        public const string Disclaimer =
            "【用途说明】\n"
          + "本程序用于检测自有设备（监控摄像头、录像机、本地视频文件）的\n"
          + "音视频完整性与质量，供设备巡检、录像抽查等正当用途使用。\n"
          + "\n"
          + "※ 本程序仅供学习参考，严禁用于任何非法用途。\n"
          + "※ 严禁用于未经授权地获取、窥探、传播他人影像或隐私。\n"
          + "\n"
          + "【使用者的责任】\n"
          + "使用者应自行确保其录制、存储、查看视频的行为符合当地法律法规\n"
          + "关于视频监控与个人隐私保护的要求（例如《个人信息保护法》\n"
          + "《网络安全法》等）。本程序不对使用者的录制行为负责。\n"
          + "\n"
          + "【软件按现状提供】\n"
          + "本程序按「现状」提供，不附带任何明示或暗示的担保。检测结果\n"
          + "仅供参考，不能替代人工复核；因使用本程序产生的任何后果，\n"
          + "由使用者自行承担。\n"
          + "\n"
          + "【数据安全提示】\n"
          + "检测报告、归档视频、运行日志均为明文文件，保存在程序目录下。\n"
          + "若设备由多人共用或需要外借，请注意这些文件可能包含录像内容\n"
          + "与设备地址信息。";

        /// <summary>一行式的简短免责提示，用于安装包说明文件开头。</summary>
        public const string DisclaimerShort =
            "本程序仅供学习参考，严禁用于任何非法用途；严禁用于未经授权地获取、窥探、传播他人影像。\n"
          + "本程序供检测自有设备录像质量使用。检测报告与归档视频为明文文件，请自行妥善保管。\n"
          + "使用 FFmpeg（GPLv3，https://ffmpeg.org），仅作独立进程调用，未做修改。";

        /// <summary>这一版的主要更新内容（启动页/说明书用）。</summary>
        public static readonly string[] Highlights = new string[] {
            "★ 程序目录规范化：设置/流列表/巡检记录等数据文件统一放进 data\\ 子目录，旧文件自动迁移",
            "★ ONVIF 事件订阅：跨品牌拿到「画面变化」事件（海康 alertStream 拿不到移动侦测）",
            "★ 事件流水小窗：显示全部原始事件（含心跳），带空状态说明",
            "★ AI 实时巡检：报警主动推图 / 事件订阅 / 定时轮询三种方式，异常自动告警写文件",
            "★ 网络搜索摄像头：ONVIF + SSDP 自动发现，自动试出各家不同的 RTSP 取流地址",
            "修一个会丢数据的 bug：关 RTSP 页时无条件回写，可能用空列表覆盖掉流列表",
            "摄像机账号保护：连续认证失败会锁号，程序检测到 401 立即停止尝试",
            "打包安全措施：打包期间自动移走含密码的开发数据，打包后自动还原",
            "★ 播放流畅度大幅提升：抽帧改批量预取 + 专用时钟线程，实测 25 帧/秒、节奏标准差 1.3ms",
            "摄像头密码全面保护：界面/列表/报告打码、日志出口脱敏、流列表 DPAPI 加密",
            "RTSP 实时检测独立成页：多路流列表、备注、CSV 导入导出、一键轮巡",
            "拉流业务检测：实际帧率与丢帧、码率稳定性、关键帧间隔、画面动态性、时间水印",
            "判定逻辑改为「总评不优于任何单项结论」：有问题就判不出「通过」",
            "全新卡片式界面 + 左侧图标导航，19 套主题重新配色",
            "文搜页改左右分栏 + AI 内容时间轴拆成两条独立游标，互不干扰",
            "存储管理：查看报告/归档/日志占用，按保留数量清理（归档目录最占空间）",
        };
    }
}
