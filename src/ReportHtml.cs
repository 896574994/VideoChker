/* -*- coding: utf-8 -*-
 * ReportHtml.cs — 自包含 HTML 报告生成（离线可打开，带状态筛选）
 * C# 5 兼容语法。
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VideoChecker
{
    public static class ReportHtml
    {
        private static string HtmlEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        public static string Export(List<MediaReport> reports, string outPath, string modeDesc)
        {
            int nPass = 0, nWarn = 0, nFail = 0, nErr = 0;
            foreach (MediaReport r in reports)
            {
                if (r.Overall == "PASS") nPass++;
                else if (r.Overall == "WARN") nWarn++;
                else if (r.Overall == "FAIL") nFail++;
                else nErr++;
            }

            StringBuilder cards = new StringBuilder();
            foreach (MediaReport r in reports)
            {
                string status = r.Overall;
                cards.AppendLine("<div class=\"card\" data-status=\"" + status + "\">");
                cards.AppendLine("<div class=\"card-head\">");
                cards.AppendLine("<span class=\"badge badge-" + status.ToLowerInvariant() + "\">" + status + "</span>");
                cards.AppendLine("<span class=\"rate\">合格率 " + r.PassRateText + "（" + r.PassCount + "/" + r.TotalCount + " 项，及格线 " +
                    (int)Math.Round(MediaReport.PassThreshold * 100) + "%）</span>");
                cards.AppendLine("<span class=\"fname\">" + HtmlEscape(Path.GetFileName(r.File)) + "</span>");
                cards.AppendLine("</div>");
                if (r.Error.Length > 0)
                {
                    cards.AppendLine("<div class=\"err\">无法分析：" + HtmlEscape(r.Error) + "</div>");
                    cards.AppendLine("</div>");
                    continue;
                }
                cards.AppendLine("<div class=\"meta\">");
                Dictionary<string, object> info = r.MediaInfo;
                object v;
                cards.AppendLine("<span>容器：" + HtmlEscape(Str(info, "container")) + "</span>");
                if (info != null && info.TryGetValue("duration", out v) && v != null)
                    cards.AppendLine("<span>时长：" + Engine.FmtSec(Ffmpeg.SafeF(v)) + "</span>");
                if (info != null && info.TryGetValue("size", out v) && v != null)
                    cards.AppendLine("<span>大小：" + Engine.FmtSize(Ffmpeg.SafeF(v)) + "</span>");
                cards.AppendLine("</div>");

                Dictionary<string, object> vinfo = info != null && info.ContainsKey("video") ? info["video"] as Dictionary<string, object> : null;
                if (vinfo != null)
                    cards.AppendLine("<div class=\"meta\">视频：" + HtmlEscape(Str(vinfo, "codec")) + " " + HtmlEscape(Str(vinfo, "width")) + "x"
                        + HtmlEscape(Str(vinfo, "height")) + " @" + HtmlEscape(Str(vinfo, "fps")) + "fps</div>");
                Dictionary<string, object> ainfo = info != null && info.ContainsKey("audio") ? info["audio"] as Dictionary<string, object> : null;
                if (ainfo != null)
                    cards.AppendLine("<div class=\"meta\">音频：" + HtmlEscape(Str(ainfo, "codec")) + " " + HtmlEscape(Str(ainfo, "sample_rate")) + "Hz "
                        + HtmlEscape(Str(ainfo, "channels")) + "ch</div>");

                cards.AppendLine(BuildTimeline(r));

                cards.AppendLine("<ul class=\"items\">");
                foreach (CheckItem it in r.Items)
                {
                    cards.AppendLine("<li class=\"item-" + it.Status.ToLowerInvariant() + "\">");
                    cards.AppendLine("<span class=\"istatus\">" + it.Status + "</span>");
                    cards.AppendLine("<span class=\"iname\">" + HtmlEscape(it.Name) + "</span>");
                    cards.AppendLine("<span class=\"idetail\">" + HtmlEscape(it.Detail) + "</span>");
                    cards.AppendLine("</li>");
                }
                cards.AppendLine("</ul>");
                cards.AppendLine("</div>");
            }

            string html = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>视频核对报告</title>
<style>
/* ========== 主题调色板：所有颜色都收敛为变量，切换主题只改这一层 ========== */
:root{
--bg:#f5f6fa;--fg:#333;--card:#fff;--muted:#888;--border:#d8d8e0;
--accent:#2563eb;--accent-fg:#fff;--btn-bg:#fff;--btn-fg:#555;
--chip-bg:#f1f5f9;--chip-fg:#475569;--item-bg:#fafbfc;--item-border:#ccc;--item-detail:#555;
--tl-bg:#f8fafc;--tl-border:#eef1f5;--tl-track:#eef1f5;--tl-label:#475569;--tl-tick:#94a3b8;
--pass:#16a34a;--warn:#d97706;--fail:#dc2626;--skip:#64748b;--skip-soft:#94a3b8;
--err-bg:#fef2f2;--tip-bg:#1e293b;--tip-fg:#fff;--footer:#aaa;
--shadow:0 1px 3px rgba(0,0,0,.06);
--s-black:#17181c;--s-freeze:#7c3aed;--s-silence:#cbd5e1;--s-flower:#dc2626;--s-lowvol:#f59e0b;--s-ai:#e11d48;
}
html[data-theme=""dark""]{
--bg:#12151c;--fg:#dfe4ec;--card:#1a1f29;--muted:#8e98a8;--border:#333b48;
--accent:#3b82f6;--accent-fg:#fff;--btn-bg:#232936;--btn-fg:#c3cbd8;
--chip-bg:#232936;--chip-fg:#a8b3c4;--item-bg:#1e242f;--item-border:#3a4353;--item-detail:#b6bfcd;
--tl-bg:#161b24;--tl-border:#2a3240;--tl-track:#2a3240;--tl-label:#a8b3c4;--tl-tick:#7d8798;
--pass:#22c55e;--warn:#f59e0b;--fail:#f87171;--skip:#94a3b8;--skip-soft:#7d8798;
--err-bg:#3b1d1d;--tip-bg:#e8edf5;--tip-fg:#12151c;--footer:#6b7280;
--shadow:0 1px 3px rgba(0,0,0,.45);
--s-black:#e2e8f0;--s-freeze:#a78bfa;--s-silence:#475569;--s-flower:#f87171;--s-lowvol:#fbbf24;--s-ai:#fb7185;
}
html[data-theme=""sepia""]{
--bg:#f4ecd8;--fg:#43382a;--card:#fbf6ea;--muted:#8a7a63;--border:#ddcfb4;
--accent:#a8622a;--accent-fg:#fff;--btn-bg:#fbf6ea;--btn-fg:#6b5b45;
--chip-bg:#ece0c8;--chip-fg:#6b5b45;--item-bg:#f7f0e0;--item-border:#d6c8ab;--item-detail:#5c4f3c;
--tl-bg:#f0e7d3;--tl-border:#e0d3ba;--tl-track:#e3d7bf;--tl-label:#6b5b45;--tl-tick:#9b8a70;
--pass:#4f7a3a;--warn:#b07414;--fail:#a83a2a;--skip:#8a7a63;--skip-soft:#a2957d;
--err-bg:#f7e3dc;--tip-bg:#43382a;--tip-fg:#fbf6ea;--footer:#a2957d;
--shadow:0 1px 3px rgba(120,95,60,.14);
--s-black:#3d3327;--s-freeze:#7a4fa8;--s-silence:#cbbb9c;--s-flower:#a83a2a;--s-lowvol:#c98a1a;--s-ai:#b03a5b;
}
html[data-theme=""contrast""]{
--bg:#fff;--fg:#000;--card:#fff;--muted:#333;--border:#000;
--accent:#0000cc;--accent-fg:#fff;--btn-bg:#fff;--btn-fg:#000;
--chip-bg:#eee;--chip-fg:#000;--item-bg:#fff;--item-border:#000;--item-detail:#000;
--tl-bg:#fff;--tl-border:#000;--tl-track:#ddd;--tl-label:#000;--tl-tick:#000;
--pass:#006400;--warn:#8a4b00;--fail:#c00000;--skip:#333;--skip-soft:#333;
--err-bg:#ffe8e8;--tip-bg:#000;--tip-fg:#fff;--footer:#333;
--shadow:0 0 0 1px #000;
--s-black:#000;--s-freeze:#4b0082;--s-silence:#999;--s-flower:#c00000;--s-lowvol:#8a4b00;--s-ai:#a3003a;
}
html[data-theme=""sakura""]{
--bg:#fdf4f7;--fg:#4a363e;--card:#fffbfd;--muted:#9c7c88;--border:#f0d6e0;
--accent:#db588a;--accent-fg:#fff;--btn-bg:#fffbfd;--btn-fg:#4a363e;
--chip-bg:#f6f1f3;--chip-fg:#9c7c88;--item-bg:#faf5f7;--item-border:#f0d6e0;--item-detail:#735963;
--tl-bg:#f6f1f3;--tl-border:#f0d6e0;--tl-track:#f1ebee;--tl-label:#9c7c88;--tl-tick:#bea6af;
--pass:#3a9660;--warn:#c87814;--fail:#c83246;--skip:#9c7c88;--skip-soft:#bea6af;
--err-bg:#f8e6e9;--tip-bg:#1e293b;--tip-fg:#ffffff;--footer:#bea6af;
--shadow:0 1px 3px rgba(0,0,0,.06);
--s-black:#17181c;--s-freeze:#7c3aed;--s-silence:#cbd5e1;--s-flower:#c83246;--s-lowvol:#c87814;--s-ai:#e11d48;
}
html[data-theme=""teal""]{
--bg:#eef8f8;--fg:#1e3a3e;--card:#faffff;--muted:#6e9094;--border:#cce4e6;
--accent:#0d9488;--accent-fg:#fff;--btn-bg:#faffff;--btn-fg:#1e3a3e;
--chip-bg:#eff5f5;--chip-fg:#6e9094;--item-bg:#f3f9f9;--item-border:#cce4e6;--item-detail:#466569;
--tl-bg:#eff5f5;--tl-border:#cce4e6;--tl-track:#e8eff0;--tl-label:#6e9094;--tl-tick:#9bb4b7;
--pass:#16965a;--warn:#c87c0f;--fail:#cd322d;--skip:#6e9094;--skip-soft:#9bb4b7;
--err-bg:#f9e6e6;--tip-bg:#1e293b;--tip-fg:#ffffff;--footer:#9bb4b7;
--shadow:0 1px 3px rgba(0,0,0,.06);
--s-black:#17181c;--s-freeze:#7c3aed;--s-silence:#cbd5e1;--s-flower:#cd322d;--s-lowvol:#c87c0f;--s-ai:#e11d48;
}
html[data-theme=""amber""]{
--bg:#fdf7ee;--fg:#423220;--card:#fffcf6;--muted:#967e60;--border:#eedec6;
--accent:#c87819;--accent-fg:#fff;--btn-bg:#fffcf6;--btn-fg:#423220;
--chip-bg:#f6f2eb;--chip-fg:#967e60;--item-bg:#f9f6f0;--item-border:#eedec6;--item-detail:#6c5840;
--tl-bg:#f6f2eb;--tl-border:#eedec6;--tl-track:#f0ece5;--tl-label:#967e60;--tl-tick:#baa892;
--pass:#488c3c;--warn:#be780a;--fail:#c43c28;--skip:#967e60;--skip-soft:#baa892;
--err-bg:#f8e8e5;--tip-bg:#1e293b;--tip-fg:#ffffff;--footer:#baa892;
--shadow:0 1px 3px rgba(0,0,0,.06);
--s-black:#17181c;--s-freeze:#7c3aed;--s-silence:#cbd5e1;--s-flower:#c43c28;--s-lowvol:#be780a;--s-ai:#e11d48;
}
html[data-theme=""midnight""]{
--bg:#0d1524;--fg:#d8e2f0;--card:#141e30;--muted:#8092ac;--border:#2a3a52;
--accent:#3884ff;--accent-fg:#fff;--btn-bg:#141e30;--btn-fg:#d8e2f0;
--chip-bg:#283243;--chip-fg:#8092ac;--item-bg:#1a2436;--item-border:#2c3647;--item-detail:#acbace;
--tl-bg:#283243;--tl-border:#2c3647;--tl-track:#242e3f;--tl-label:#8092ac;--tl-tick:#58667c;
--pass:#22c55e;--warn:#f59e0b;--fail:#f87171;--skip:#8092ac;--skip-soft:#58667c;
--err-bg:#412935;--tip-bg:#e3e4e6;--tip-fg:#12151c;--footer:#58667c;
--shadow:0 1px 3px rgba(0,0,0,.45);
--s-black:#e2e8f0;--s-freeze:#a78bfa;--s-silence:#475569;--s-flower:#f87171;--s-lowvol:#f59e0b;--s-ai:#fb7185;
}
html[data-theme=""graphite""]{
--bg:#1a1a1c;--fg:#e2e2e6;--card:#232326;--muted:#8c8c94;--border:#3a3a3e;
--accent:#82aaff;--accent-fg:#fff;--btn-bg:#232326;--btn-fg:#e2e2e6;
--chip-bg:#363639;--chip-fg:#8c8c94;--item-bg:#29292c;--item-border:#3a3a3d;--item-detail:#b7b7bd;
--tl-bg:#363639;--tl-border:#3a3a3d;--tl-track:#323235;--tl-label:#8c8c94;--tl-tick:#64646a;
--pass:#4ac878;--warn:#eba53c;--fail:#f07878;--skip:#8c8c94;--skip-soft:#64646a;
--err-bg:#492f30;--tip-bg:#e5e5e5;--tip-fg:#12151c;--footer:#64646a;
--shadow:0 1px 3px rgba(0,0,0,.45);
--s-black:#e2e8f0;--s-freeze:#a78bfa;--s-silence:#475569;--s-flower:#f07878;--s-lowvol:#eba53c;--s-ai:#fb7185;
}
html[data-theme=""forest""]{
--bg:#121c16;--fg:#dcece0;--card:#1a271f;--muted:#829c8a;--border:#2c4434;
--accent:#34a862;--accent-fg:#fff;--btn-bg:#1a271f;--btn-fg:#dcece0;
--chip-bg:#2d3b32;--chip-fg:#829c8a;--item-bg:#202d25;--item-border:#313f36;--item-detail:#afc4b5;
--tl-bg:#2d3b32;--tl-border:#313f36;--tl-track:#2a372e;--tl-label:#829c8a;--tl-tick:#5b6f61;
--pass:#4ade80;--warn:#f0b43c;--fail:#f06e6e;--skip:#829c8a;--skip-soft:#5b6f61;
--err-bg:#432e29;--tip-bg:#e4e5e4;--tip-fg:#12151c;--footer:#5b6f61;
--shadow:0 1px 3px rgba(0,0,0,.45);
--s-black:#e2e8f0;--s-freeze:#a78bfa;--s-silence:#475569;--s-flower:#f06e6e;--s-lowvol:#f0b43c;--s-ai:#fb7185;
}
body{font-family:'Microsoft YaHei',sans-serif;margin:0;background:var(--bg);color:var(--fg)}
.wrap{max-width:980px;margin:0 auto;padding:24px 16px 60px}
h1{font-size:22px;margin:8px 0 4px}
.sub{color:var(--muted);font-size:13px;margin-bottom:12px}
.themebar{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin:0 0 16px}
.themebar span{font-size:12px;color:var(--muted)}
.themebar button{border:1px solid var(--border);background:var(--btn-bg);color:var(--btn-fg);border-radius:999px;padding:5px 14px;font-size:12px;cursor:pointer;font-family:inherit}
.themebar button.on{background:var(--accent);border-color:var(--accent);color:var(--accent-fg);font-weight:700}
.stats{display:flex;gap:12px;margin:16px 0 20px;flex-wrap:wrap}
.stat{flex:1;min-width:120px;background:var(--card);border-radius:10px;padding:14px 16px;text-align:center;box-shadow:var(--shadow)}
.stat .num{font-size:28px;font-weight:700}
.stat .lab{font-size:12px;color:var(--muted);margin-top:2px}
.c-pass .num{color:var(--pass)}.c-warn .num{color:var(--warn)}.c-fail .num{color:var(--fail)}.c-err .num{color:var(--skip)}
.filters{display:flex;gap:8px;margin-bottom:18px;flex-wrap:wrap}
.filters button{border:1px solid var(--border);background:var(--btn-bg);border-radius:999px;padding:6px 16px;font-size:13px;cursor:pointer;color:var(--btn-fg);font-family:inherit}
.filters button.on{background:var(--accent);border-color:var(--accent);color:var(--accent-fg)}
.card{background:var(--card);border-radius:12px;padding:16px 18px;margin-bottom:14px;box-shadow:var(--shadow)}
.card-head{display:flex;align-items:center;gap:10px;margin-bottom:8px}
.fname{font-size:15px;font-weight:600;word-break:break-all}
.rate{font-size:12px;color:var(--chip-fg);flex:none;padding:2px 10px;background:var(--chip-bg);border-radius:999px}
.badge{font-size:12px;font-weight:700;padding:3px 12px;border-radius:999px;color:#fff;flex:none}
.badge-pass{background:var(--pass)}.badge-warn{background:var(--warn)}.badge-fail{background:var(--fail)}.badge-error,.badge-skip{background:var(--skip)}
.meta{font-size:12px;color:var(--muted);margin:2px 0}
.items{list-style:none;margin:10px 0 0;padding:0}
.tl{margin:10px 0 2px;padding:10px 12px;background:var(--tl-bg);border:1px solid var(--tl-border);border-radius:8px}
.tl-title{font-size:12px;font-weight:700;color:var(--tl-label);margin-bottom:6px}
.tl-legend{display:flex;gap:16px;margin-top:4px;font-size:11px;color:var(--skip)}
.tl-legend span{display:inline-flex;align-items:center;gap:5px}
.tl-legend i{display:inline-block;width:12px;height:12px;border-radius:2px}
.items li{display:flex;gap:10px;align-items:baseline;padding:7px 10px;border-radius:8px;font-size:13px;border-left:3px solid var(--item-border);margin-bottom:4px;background:var(--item-bg)}
.istatus{font-size:11px;font-weight:700;flex:none;width:42px;text-align:center;padding:2px 0;border-radius:4px}
.item-pass{border-left-color:var(--pass)}.item-pass .istatus{color:var(--pass)}
.item-warn{border-left-color:var(--warn)}.item-warn .istatus{color:var(--warn)}
.item-fail{border-left-color:var(--fail)}.item-fail .istatus{color:var(--fail)}
.item-skip{border-left-color:var(--skip-soft)}.item-skip .istatus{color:var(--skip-soft)}
.iname{font-weight:600;flex:none;min-width:96px}
.idetail{color:var(--item-detail)}
.err{color:var(--fail);font-size:13px;background:var(--err-bg);padding:10px 12px;border-radius:8px}
#tip{position:fixed;z-index:99;display:none;background:var(--tip-bg);color:var(--tip-fg);font-size:12px;padding:6px 10px;border-radius:6px;pointer-events:none;max-width:340px;line-height:1.5;box-shadow:0 2px 10px rgba(0,0,0,.25);white-space:pre-line}
.tl rect[data-tip]{cursor:help}
.hidden{display:none}
footer{color:var(--footer);font-size:12px;text-align:center;margin-top:30px}
/* 打印时强制浅色，避免深色主题浪费墨水；主题条与筛选按钮不打印 */
@media print{
html[data-theme]{--bg:#fff;--fg:#000;--card:#fff;--muted:#444;--border:#999;--item-bg:#fff;--tl-bg:#fff;--chip-bg:#f2f2f2}
.themebar,.filters{display:none}
.card{break-inside:avoid}
}
</style>
</head>
<body>
<div class=""wrap"">
<h1>视频核对工具 — 检测报告</h1>
<div class=""sub"">生成时间：__GEN__ ｜ 模式：__MODE__ ｜ 文件数：__FILES__ ｜ __VER__</div>
<div class=""themebar"">
<span>主题</span>
<button data-th=""light"">浅色</button>
<button data-th=""sepia"">护眼</button>
<button data-th=""sakura"">樱花</button>
<button data-th=""teal"">青碧</button>
<button data-th=""amber"">琥珀</button>
<button data-th=""dark"">深色</button>
<button data-th=""midnight"">午夜蓝</button>
<button data-th=""graphite"">石墨</button>
<button data-th=""forest"">森林</button>
<button data-th=""contrast"">高对比</button>
</div>
<div class=""stats"">
<div class=""stat c-pass""><div class=""num"">__NPASS__</div><div class=""lab"">通过</div></div>
<div class=""stat c-warn""><div class=""num"">__NWARN__</div><div class=""lab"">一般</div></div>
<div class=""stat c-fail""><div class=""num"">__NFAIL__</div><div class=""lab"">失败</div></div>
<div class=""stat c-err""><div class=""num"">__NERR__</div><div class=""lab"">无法分析</div></div>
</div>
<div class=""filters"">
<button data-f=""all"" class=""on"">全部</button>
<button data-f=""PASS"">通过</button>
<button data-f=""WARN"">一般</button>
<button data-f=""FAIL"">失败</button>
<button data-f=""SKIP"">无法分析</button>
</div>
__CARDS__
<footer>__TITLE__ C# 图形版 __VER__ ｜ 适配 Windows 7 / 10 / 11</footer>
</div>
<script>
var btns=document.querySelectorAll('.filters button');
btns.forEach(function(b){b.onclick=function(){
  btns.forEach(function(x){x.classList.remove('on')});b.classList.add('on');
  var f=b.getAttribute('data-f');
  document.querySelectorAll('.card').forEach(function(c){
    c.classList.toggle('hidden', f!=='all' && c.getAttribute('data-status')!==f);
  });
}});
// ---- 时间轴悬停提示 ----
var tip=document.createElement('div');tip.id='tip';document.body.appendChild(tip);
document.addEventListener('mouseover',function(e){
  var t=e.target;
  var txt=t&&t.getAttribute?t.getAttribute('data-tip'):null;
  if(txt){tip.textContent=txt;tip.style.display='block';}
});
document.addEventListener('mousemove',function(e){
  if(tip.style.display!=='block')return;
  var x=e.clientX+14,y=e.clientY+14;
  if(x+tip.offsetWidth>window.innerWidth-8)x=e.clientX-tip.offsetWidth-10;
  if(y+tip.offsetHeight>window.innerHeight-8)y=e.clientY-tip.offsetHeight-10;
  tip.style.left=Math.max(4,x)+'px';tip.style.top=Math.max(4,y)+'px';
});
document.addEventListener('mouseout',function(e){
  var t=e.target;
  if(t&&t.getAttribute&&t.getAttribute('data-tip'))tip.style.display='none';
});
// ---- 主题切换：选择记在 localStorage，换一份报告文件也保持 ----
var THEME_KEY='vc_report_theme';
function applyTheme(t){
  document.documentElement.setAttribute('data-theme',t);
  document.querySelectorAll('.themebar button').forEach(function(b){
    if(b.getAttribute('data-th')===t)b.classList.add('on');else b.classList.remove('on');
  });
}
(function(){
  var t='';
  try{t=localStorage.getItem(THEME_KEY)||'';}catch(e){}
  if(!t){try{t=(window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)').matches)?'dark':'light';}catch(e){t='light';}}
  applyTheme(t);
  document.querySelectorAll('.themebar button').forEach(function(b){
    b.onclick=function(){
      var th=b.getAttribute('data-th');applyTheme(th);
      try{localStorage.setItem(THEME_KEY,th);}catch(e){}
    };
  });
})();
</script>
</body>
</html>";

            html = html.Replace("__GEN__", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            html = html.Replace("__MODE__", HtmlEscape(modeDesc));
            html = html.Replace("__VER__", HtmlEscape(AppInfo.Version + " (" + AppInfo.BuildDate + ")"));
            html = html.Replace("__TITLE__", HtmlEscape(AppInfo.Title));
            html = html.Replace("__FILES__", reports.Count.ToString());
            html = html.Replace("__NPASS__", nPass.ToString());
            html = html.Replace("__NWARN__", nWarn.ToString());
            html = html.Replace("__NFAIL__", nFail.ToString());
            html = html.Replace("__NERR__", nErr.ToString());
            html = html.Replace("__CARDS__", cards.ToString());

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, html, new UTF8Encoding(true));
            return outPath;
        }

        private static string Str(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v);
            return "?";
        }

        // ---- 时间轴可视化（黑帧 / 冻结 / 无声段）----

        /// <summary>
        /// 取时间段对象的某个字段值，取不到返回 -1。
        /// 原实现用反射 GetField(字段名) —— 那在混淆改名后会直接失效
        /// （混淆按"成员名"重命名，而反射是按字符串去找成员）。
        /// 改成按类型直接取值：既去掉了反射，也让这里在混淆后依然正确。
        /// nullable 参数保留是为了不改调用点，实际未使用。
        /// </summary>
        private static double GetSegNum(object o, string field, bool nullable)
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

        /// <summary>收集某检测项里的时间段 [[start, end], ...]（end 为 -1 表示未知，按时长截断）。</summary>
        private static List<double[]> CollectSegs(MediaReport r, string itemName)
        {
            List<double[]> res = new List<double[]>();
            foreach (CheckItem it in r.Items)
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
                    double s = GetSegNum(o, "Start", true);
                    if (s < 0) continue;
                    double e = GetSegNum(o, "End", true);
                    if (e < 0)
                    {
                        double d = GetSegNum(o, "Duration", false);
                        e = d > 0 ? s + d : -1;
                    }
                    res.Add(new double[] { s, e });
                }
            }
            return res;
        }

        /// <summary>收集 AI 画面理解的帧标注 [[time, code], ...]，code: 0=PASS 1=WARN 2=FAIL 3=SKIP。</summary>
        private static List<double[]> CollectAiFrames(MediaReport r)
        {
            List<double[]> res = new List<double[]>();
            foreach (CheckItem it in r.Items)
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

        private static string BuildTimeline(MediaReport r)
        {
            double dur = 0;
            object v;
            if (r.MediaInfo != null && r.MediaInfo.TryGetValue("duration", out v) && v != null)
                dur = Convert.ToDouble(v);
            if (dur <= 0.5) return "";

            List<double[]> black = CollectSegs(r, "黑帧检测");
            List<double[]> freeze = CollectSegs(r, "冻结帧检测");
            List<double[]> silence = CollectSegs(r, "静音分布");
            List<double[]> lowVol = CollectSegs(r, "音量过低");
            List<double[]> flower = CollectSegs(r, "视频解码");
            List<double[]> ai = CollectAiFrames(r);
            List<double[]> aiBad = new List<double[]>();
            foreach (double[] fr in ai)
                if (fr[1] == 1 || fr[1] == 2) aiBad.Add(fr);
            if (black.Count == 0 && freeze.Count == 0 && silence.Count == 0
                && lowVol.Count == 0 && flower.Count == 0 && aiBad.Count == 0) return "";

            const int W = 900;
            const int RowH = 22;
            const int LabelW = 58;
            const int BarY = 6;
            const int BarH = 10;
            int rows = (black.Count > 0 ? 1 : 0) + (freeze.Count > 0 ? 1 : 0) + (silence.Count > 0 ? 1 : 0)
                + (lowVol.Count > 0 ? 1 : 0) + (flower.Count > 0 ? 1 : 0) + (aiBad.Count > 0 ? 1 : 0);
            int svgH = rows * RowH + 22;   // 底部刻度区

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<div class=\"tl\"><div class=\"tl-title\">异常时间轴（黑帧 / 冻结 / 无声 / 花屏 / 低音量 / AI异常帧）</div>");
            sb.AppendLine("<svg viewBox=\"0 0 " + W + " " + svgH + "\" style=\"width:100%;height:auto;display:block\" xmlns=\"http://www.w3.org/2000/svg\">");
            int ry = 0;
            AppendRow(sb, "黑帧", black, dur, W, LabelW, BarY, BarH, "var(--s-black)", ref ry, RowH, false);
            AppendRow(sb, "冻结", freeze, dur, W, LabelW, BarY, BarH, "var(--s-freeze)", ref ry, RowH, false);
            AppendRow(sb, "无声", silence, dur, W, LabelW, BarY, BarH, "var(--s-silence)", ref ry, RowH, false);
            AppendRow(sb, "花屏", flower, dur, W, LabelW, BarY, BarH, "var(--s-flower)", ref ry, RowH, false);
            AppendRow(sb, "低音量", lowVol, dur, W, LabelW, BarY, BarH, "var(--s-lowvol)", ref ry, RowH, false);
            AppendRow(sb, "AI异常", aiBad, dur, W, LabelW, BarY, BarH, "var(--s-ai)", ref ry, RowH, true);
            // 时间刻度
            int tickY = rows * RowH + 15;
            sb.AppendLine("<text x=\"0\" y=\"" + tickY + "\" font-size=\"10\" fill=\"var(--tl-tick)\">0s</text>");
            sb.AppendLine("<text x=\"" + (W / 2) + "\" y=\"" + tickY + "\" font-size=\"10\" fill=\"var(--tl-tick)\" text-anchor=\"middle\">" + (dur / 2).ToString("0.#") + "s</text>");
            sb.AppendLine("<text x=\"" + W + "\" y=\"" + tickY + "\" font-size=\"10\" fill=\"var(--tl-tick)\" text-anchor=\"end\">" + dur.ToString("0.#") + "s</text>");
            sb.AppendLine("</svg>");
            sb.AppendLine("</div>");
            return sb.ToString();
        }

        private static void AppendRow(StringBuilder sb, string label, List<double[]> segs, double dur,
            int W, int labelW, int barY, int barH, string color, ref int y, int rowH, bool asPoints)
        {
            if (segs.Count == 0) return;
            int barX = labelW;
            sb.AppendLine("<text x=\"0\" y=\"" + (y + barY + 9) + "\" font-size=\"11\" fill=\"var(--tl-label)\">" + label + "(" + segs.Count + (asPoints ? "帧" : "段") + ")</text>");
            sb.AppendLine("<rect x=\"" + barX + "\" y=\"" + (y + barY) + "\" width=\"" + (W - barX) + "\" height=\"" + barH + "\" rx=\"2\" fill=\"var(--tl-track)\"/>");
            foreach (double[] sg in segs)
            {
                double s = sg[0];
                if (asPoints)
                {
                    // AI 异常帧：画一个竖条标记，带悬停提示
                    double xp = barX + s / dur * (W - barX);
                    string tipTxt = label + " 帧 @" + s.ToString("0.#") + "s（AI 判定：" + (sg[1] >= 2 ? "严重异常" : "疑似异常") + "）";
                    sb.AppendLine("<rect data-tip=\"" + HtmlEscape(tipTxt) + "\" x=\"" + (xp - 2).ToString("0.0") + "\" y=\"" + (y + barY - 1) + "\" width=\"4\" height=\"" + (barH + 2) + "\" rx=\"1\" fill=\"" + color + "\"/>");
                    continue;
                }
                double e = sg[1] > 0 ? sg[1] : dur;
                if (e <= s) continue;
                double x = barX + s / dur * (W - barX);
                double w = (e - s) / dur * (W - barX);
                if (w < 1) w = 1;
                string endTxt = sg[1] > 0 ? e.ToString("0.#") + "s" : "结尾";
                string tipSeg = label + " " + s.ToString("0.#") + "s - " + endTxt + "（时长 " + (e - s).ToString("0.#") + "s）";
                sb.AppendLine("<rect data-tip=\"" + HtmlEscape(tipSeg) + "\" x=\"" + x.ToString("0.0") + "\" y=\"" + (y + barY) + "\" width=\"" + w.ToString("0.0") + "\" height=\"" + barH + "\" rx=\"1.5\" fill=\"" + color + "\"/>");
            }
            y += rowH;
        }

        /// <summary>默认报告目录：exe 同目录 reports\。</summary>
        public static string DefaultReportDir()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reports");
        }

        /// <summary>生成带时间戳且不覆盖旧文件的路径：base_20260911_153012.ext；同名再追加序号。</summary>
        public static string StampedPath(string dir, string baseName, string ext)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string candidate = Path.Combine(dir, baseName + "_" + stamp + ext);
            int k = 2;
            while (File.Exists(candidate))
                candidate = Path.Combine(dir, baseName + "_" + stamp + "_" + (k++) + ext);
            return candidate;
        }
    }
}
