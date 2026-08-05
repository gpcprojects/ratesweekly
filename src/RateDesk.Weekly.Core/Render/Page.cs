using System.Text;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>The page shell every dashboard shares: theme tokens, the grouped nav that reads as
    /// tabs, one hover script for every chart on the page, and a theme toggle. Entirely
    /// self-contained — no external CSS, fonts, scripts or images, so it renders identically on any
    /// static host and never phones home from a viewer's browser.</summary>
    public static class Page
    {
        public static readonly string[][] Groups =
        {
            new[] { "DM", "USD", "EUR", "GBP", "JPY", "CAD", "SEK", "NOK", "DKK", "CHF", "AUD", "NZD" },
            new[] { "EM", "HUF", "CZK", "PLN", "ZAR", "ILS" },
            new[] { "LATAM", "COP", "CLP", "MXN", "BRL" },
            new[] { "ASIA EM", "TWD", "THB", "MYR", "INR", "CNY", "HKD", "SGD", "KRW" },
        };

        public static string Nav(string current)
        {
            var sb = new StringBuilder("<nav class=\"rw-nav\"><a class=\"rw-hub");
            sb.Append(current.Equals("movers", StringComparison.OrdinalIgnoreCase) ? " on" : "");
            sb.Append("\" href=\"index.html\">◆ Movers</a>");
            foreach (var g in Groups)
            {
                sb.Append($"<span class=\"rw-grp\"><b>{Viz.Esc(g[0])}</b>");
                for (int i = 1; i < g.Length; i++)
                {
                    bool on = g[i].Equals(current, StringComparison.OrdinalIgnoreCase);
                    sb.Append($"<a class=\"rw-cc{(on ? " on" : "")}\" href=\"{g[i].ToLowerInvariant()}.html\">{g[i]}</a>");
                }
                sb.Append("</span>");
            }
            return sb.Append("</nav>").ToString();
        }

        public static string Shell(string title, string current, string heading, string sub, string body, string asOf)
        {
            // Token replacement, not interpolation: the CSS and JS below are full of braces, which
            // fight every raw-string interpolation form.
            return Template
                .Replace("%%TITLE%%", Viz.Esc(title))
                .Replace("%%THEMECSS%%", Viz.ThemeCss)
                .Replace("%%HEADING%%", Viz.Esc(heading))
                .Replace("%%SUB%%", Viz.Esc(sub))
                .Replace("%%NAV%%", Nav(current))
                .Replace("%%BODY%%", body)
                .Replace("%%ASOF%%", Viz.Esc(asOf));
        }

        private const string Template = """
                <!doctype html>
                <html lang="en">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <title>%%TITLE%%</title>
                <style>
                %%THEMECSS%%
                *{box-sizing:border-box}
                body{margin:0;background:var(--rw-plane);color:var(--rw-ink);
                  font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
                .rw-wrap{max-width:1180px;margin:0 auto;padding:20px 18px 56px}
                header.rw-head{display:flex;align-items:baseline;gap:12px;flex-wrap:wrap;margin-bottom:4px}
                header.rw-head h1{font-size:22px;margin:0;letter-spacing:-.01em}
                .rw-asof{color:var(--rw-muted);font-size:12px}
                .rw-sub{color:var(--rw-ink2);font-size:12px;margin:2px 0 0}
                .rw-nav{display:flex;flex-wrap:wrap;gap:6px 14px;align-items:center;margin:14px 0 20px;
                  padding:10px 12px;background:var(--rw-surface);border:1px solid var(--rw-border);border-radius:10px}
                .rw-grp{display:flex;gap:4px;align-items:center;flex-wrap:wrap}
                .rw-grp b{font-size:10px;letter-spacing:.08em;color:var(--rw-muted);text-transform:uppercase;margin-right:2px}
                .rw-nav a{text-decoration:none;color:var(--rw-ink2);padding:3px 7px;border-radius:6px;font-size:12px}
                .rw-nav a:hover{background:var(--rw-plane);color:var(--rw-ink)}
                .rw-nav a.on{background:var(--rw-today);color:var(--rw-surface);font-weight:600}
                .rw-hub{font-weight:600}
                .rw-grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(430px,1fr));gap:16px}
                .rw-card{margin:0;background:var(--rw-surface);border:1px solid var(--rw-border);
                  border-radius:12px;padding:14px 16px 12px;position:relative}
                .rw-card h3{margin:0;font-size:14px;font-weight:600}
                .rw-svg{width:100%;height:auto;display:block;overflow:visible;margin-top:6px}
                .rw-grid{stroke:var(--rw-grid);stroke-width:1}
                .rw-axis{stroke:var(--rw-axis);stroke-width:1}
                .rw-tick{fill:var(--rw-muted);font-size:10px;font-variant-numeric:tabular-nums}
                .rw-tick-y{text-anchor:end}.rw-tick-x{text-anchor:middle}
                .rw-endlab{fill:var(--rw-ink);font-size:11px;font-weight:600;font-variant-numeric:tabular-nums}
                .rw-cross{stroke:var(--rw-axis);stroke-width:1}
                .rw-legend{display:flex;gap:14px;flex-wrap:wrap;margin:8px 0 0}
                .rw-key{display:inline-flex;align-items:center;gap:6px;font-size:11px;color:var(--rw-ink2)}
                .rw-key i{width:14px;height:3px;border-radius:2px;display:inline-block}
                .rw-tip{position:absolute;pointer-events:none;background:var(--rw-surface);
                  border:1px solid var(--rw-border);border-radius:8px;padding:7px 9px;font-size:11px;
                  box-shadow:0 4px 14px rgba(0,0,0,.13);z-index:5;min-width:118px}
                .rw-tip b{display:block;margin-bottom:3px;font-size:11px}
                .rw-tip .r{display:flex;justify-content:space-between;gap:12px;font-variant-numeric:tabular-nums}
                .rw-tip .r i{width:9px;height:9px;border-radius:2px;display:inline-block;margin-right:5px}
                .rw-empty{color:var(--rw-muted);font-size:12px;padding:26px 0;text-align:center;font-style:italic}
                .rw-table{margin-top:8px}
                .rw-table summary{cursor:pointer;font-size:11px;color:var(--rw-muted)}
                .rw-table table{border-collapse:collapse;margin-top:8px;font-size:11px;width:100%}
                .rw-table th,.rw-table td{text-align:right;padding:2px 8px;border-bottom:1px solid var(--rw-grid);
                  font-variant-numeric:tabular-nums}
                .rw-table th:first-child,.rw-table td:first-child{text-align:left}
                .rw-note{color:var(--rw-muted);font-size:11px;margin:6px 0 0}
                .rw-pending{background:var(--rw-surface);border:1px dashed var(--rw-axis);border-radius:12px;
                  padding:20px;color:var(--rw-muted);font-size:12px}
                .rw-toggle{margin-left:auto;background:var(--rw-surface);color:var(--rw-ink2);cursor:pointer;
                  border:1px solid var(--rw-border);border-radius:8px;padding:5px 10px;font-size:12px}
                footer.rw-foot{margin-top:30px;color:var(--rw-muted);font-size:11px;
                  border-top:1px solid var(--rw-border);padding-top:12px}
                @media (max-width:520px){.rw-grid2{grid-template-columns:1fr}}
                </style>
                </head>
                <body>
                <div class="rw-wrap">
                <header class="rw-head">
                  <h1>%%HEADING%%</h1>
                  <span class="rw-asof">%%SUB%%</span>
                  <button class="rw-toggle" id="rwTheme" type="button">◑ theme</button>
                </header>
                %%NAV%%
                %%BODY%%
                <footer class="rw-foot">
                  Source: Bloomberg / RATESWEEKLY · levels are close-to-close; 1w and 1m lookbacks are the
                  last close at or before 7 and 31 calendar days back. Generated %%ASOF%%.
                </footer>
                </div>
                <script>
                (function(){
                  var r=document.documentElement, k='rw-theme', s=null;
                  try{s=localStorage.getItem(k)}catch(e){}
                  if(s)r.setAttribute('data-theme',s);
                  var b=document.getElementById('rwTheme');
                  if(b)b.addEventListener('click',function(){
                    var cur=r.getAttribute('data-theme');
                    if(!cur)cur=matchMedia('(prefers-color-scheme:dark)').matches?'dark':'light';
                    var nx=cur==='dark'?'light':'dark';
                    r.setAttribute('data-theme',nx);
                    try{localStorage.setItem(k,nx)}catch(e){}
                  });

                  document.querySelectorAll('.rw-card').forEach(function(card){
                    var el=card.querySelector('.rw-data'); if(!el)return;
                    var d; try{d=JSON.parse(el.textContent)}catch(e){return}
                    var svg=card.querySelector('.rw-svg'), hit=card.querySelector('.rw-hit'),
                        cross=card.querySelector('.rw-cross'), tip=card.querySelector('.rw-tip');
                    if(!svg||!hit||!tip)return;
                    var pw=d.W-d.ml-d.mr, ph=d.height-d.mt-d.mb;
                    function sx(x){return d.ml+(x-d.xMin)/(d.xMax-d.xMin)*pw}
                    function sy(y){return d.mt+(d.yMax-y)/(d.yMax-d.yMin)*ph}
                    var xs=[]; d.series.forEach(function(s){s.pts.forEach(function(p){
                      if(xs.indexOf(p[0])<0)xs.push(p[0])})}); xs.sort(function(a,b){return a-b});

                    function move(ev){
                      var r=svg.getBoundingClientRect();
                      var ux=(ev.clientX-r.left)/r.width*d.W;
                      var best=null,bd=1e9;
                      xs.forEach(function(x){var dd=Math.abs(sx(x)-ux); if(dd<bd){bd=dd;best=x}});
                      if(best===null)return;
                      var px=sx(best);
                      cross.setAttribute('x1',px); cross.setAttribute('x2',px);
                      cross.style.display='';
                      var h='<b>'+fmtX(best)+'</b>';
                      d.series.forEach(function(s){
                        var hitp=null;
                        s.pts.forEach(function(p){if(Math.abs(p[0]-best)<1e-9)hitp=p});
                        if(hitp)h+='<div class="r"><span><i style="background:'+s.color+'"></i>'+s.name+
                          '</span><span>'+hitp[1].toFixed(3)+'%</span></div>';
                      });
                      tip.innerHTML=h; tip.hidden=false;
                      var cw=card.clientWidth, tw=tip.offsetWidth;
                      var left=px/d.W*cw+12; if(left+tw>cw-8)left=px/d.W*cw-tw-12;
                      tip.style.left=Math.max(4,left)+'px';
                      tip.style.top=(ev.clientY-card.getBoundingClientRect().top+14)+'px';
                    }
                    function fmtX(v){
                      var f=card.dataset.xfmt;
                      if(f==='date'){var dt=new Date(v*86400000);
                        return dt.toLocaleDateString(undefined,{day:'2-digit',month:'short',year:'2-digit'})}
                      if(f==='fwd')return v+'y1y';
                      return (v%1?v.toFixed(2):v)+'y';
                    }
                    hit.addEventListener('pointermove',move);
                    hit.addEventListener('pointerleave',function(){tip.hidden=true;cross.style.display='none'});
                  });
                })();
                </script>
                </body>
                </html>
                """;
    }
}
