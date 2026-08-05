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

        /// <summary>DM occupies the first row on its own; EM, LATAM and ASIA EM share the second
        /// (desk layout, 2026-08-05).</summary>
        public static string Nav(string current)
        {
            var sb = new StringBuilder("<nav class=\"rw-nav\">");

            sb.Append("<div class=\"rw-navrow\"><a class=\"rw-hub");
            sb.Append(current.Equals("movers", StringComparison.OrdinalIgnoreCase) ? " on" : "");
            sb.Append("\" href=\"index.html\">◆ Movers</a>");
            sb.Append(GroupHtml(Groups[0], current));
            sb.Append("</div>");

            sb.Append("<div class=\"rw-navrow\">");
            for (int g = 1; g < Groups.Length; g++) sb.Append(GroupHtml(Groups[g], current));
            sb.Append("</div>");

            return sb.Append("</nav>").ToString();
        }

        private static string GroupHtml(string[] g, string current)
        {
            var sb = new StringBuilder($"<span class=\"rw-grp\"><b>{Viz.Esc(g[0])}</b>");
            for (int i = 1; i < g.Length; i++)
            {
                bool on = g[i].Equals(current, StringComparison.OrdinalIgnoreCase);
                sb.Append($"<a class=\"rw-cc{(on ? " on" : "")}\" href=\"{g[i].ToLowerInvariant()}.html\">{g[i]}</a>");
            }
            return sb.Append("</span>").ToString();
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
                .rw-nav{display:flex;flex-direction:column;gap:8px;margin:14px 0 20px;
                  padding:10px 12px;background:var(--rw-surface);border:1px solid var(--rw-border);border-radius:10px}
                .rw-navrow{display:flex;flex-wrap:wrap;gap:6px 16px;align-items:center}
                .rw-navrow+.rw-navrow{border-top:1px solid var(--rw-border);padding-top:8px}
                .rw-grp{display:flex;gap:4px;align-items:center;flex-wrap:wrap}
                .rw-grp b{font-size:10px;letter-spacing:.08em;color:var(--rw-muted);text-transform:uppercase;margin-right:2px}
                .rw-nav a{text-decoration:none;color:var(--rw-ink2);padding:3px 7px;border-radius:6px;font-size:12px}
                .rw-nav a:hover{background:var(--rw-plane);color:var(--rw-ink)}
                .rw-nav a.on{background:var(--rw-today);color:var(--rw-surface);font-weight:600}
                .rw-hub{font-weight:600}
                .rw-grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(430px,1fr));gap:16px}
                .rw-panel{background:var(--rw-surface);border:1px solid var(--rw-border);border-radius:12px;
                  padding:14px 16px 12px;margin-bottom:16px}
                .rw-panel-head h3{margin:0;font-size:14px;font-weight:600}
                .rw-panel-body{display:grid;grid-template-columns:minmax(210px,300px) 1fr;gap:18px;
                  align-items:start;margin-top:10px}
                .rw-tblwrap{overflow-x:auto;max-height:340px;overflow-y:auto}
                table.rw-lvl{border-collapse:collapse;width:100%;font-size:12px}
                table.rw-lvl th{position:sticky;top:0;background:var(--rw-surface);text-align:right;
                  font-weight:500;font-size:10px;letter-spacing:.04em;text-transform:uppercase;
                  color:var(--rw-muted);padding:3px 7px;border-bottom:1px solid var(--rw-grid)}
                table.rw-lvl th:first-child{text-align:left}
                table.rw-lvl td{padding:3px 7px;text-align:right;font-variant-numeric:tabular-nums;
                  border-bottom:1px solid var(--rw-grid)}
                td.rw-lab{text-align:left;color:var(--rw-ink2);white-space:nowrap}
                td.rw-val{font-weight:600}
                td.rw-bp{font-size:11px}
                .rw-upbp{color:var(--rw-up)}.rw-downbp{color:var(--rw-down)}
                .rw-flatbp,.rw-nil{color:var(--rw-muted)}
                tr.rw-row{cursor:default}
                tr.rw-row:hover,tr.rw-row.on,tr.rw-row:focus{background:var(--rw-plane);outline:none}
                tr.rw-row.on td.rw-lab{color:var(--rw-ink);font-weight:600}
                .rw-chartwrap{position:relative;min-width:0}
                circle.rw-pt{opacity:0;transition:opacity .08s}
                circle.rw-pt.on{opacity:1}
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

                  // Linked table + chart: hovering either side highlights the same point in
                  // the other. The table is the primary surface (every value is text), so the
                  // chart interaction supplements it rather than gating anything.
                  document.querySelectorAll('.rw-panel').forEach(function(panel){
                    var el=panel.querySelector('.rw-data'); if(!el)return;
                    var d; try{d=JSON.parse(el.textContent)}catch(e){return}
                    var svg=panel.querySelector('.rw-svg'), hit=panel.querySelector('.rw-hit'),
                        tip=panel.querySelector('.rw-tip'), wrap=panel.querySelector('.rw-chartwrap');
                    if(!svg||!hit||!tip||!wrap)return;
                    var pts=[].slice.call(panel.querySelectorAll('circle.rw-pt'));
                    var rows=[].slice.call(panel.querySelectorAll('tr.rw-row'));
                    var pw=d.W-d.ml-d.mr;
                    function sx(i){return d.n<=1?d.ml+pw/2:d.ml+i/(d.n-1)*pw}
                    function fmt(v){return v===null||v===undefined?'--':v.toFixed(d.dp)+d.suffix}

                    function show(i,clientY){
                      pts.forEach(function(c){c.classList.toggle('on',+c.dataset.i===i)});
                      rows.forEach(function(r){r.classList.toggle('on',+r.dataset.i===i)});
                      var h='<b>'+d.labels[i]+'</b>';
                      h+='<div class="r"><span><i style="background:var(--rw-today)"></i>latest</span><span>'+fmt(d.now[i])+'</span></div>';
                      if(d.week[i]!==null&&d.week[i]!==undefined)
                        h+='<div class="r"><span><i style="background:var(--rw-week)"></i>1w ago</span><span>'+fmt(d.week[i])+'</span></div>';
                      if(d.month[i]!==null&&d.month[i]!==undefined)
                        h+='<div class="r"><span><i style="background:var(--rw-month)"></i>1m ago</span><span>'+fmt(d.month[i])+'</span></div>';
                      tip.innerHTML=h; tip.hidden=false;
                      var wr=wrap.getBoundingClientRect(), tw=tip.offsetWidth;
                      var px=sx(i)/d.W*wr.width;
                      var left=px+12; if(left+tw>wr.width-6)left=px-tw-12;
                      tip.style.left=Math.max(2,left)+'px';
                      var top=(clientY===undefined?wr.top+wr.height/2:clientY)-wr.top+12;
                      tip.style.top=Math.max(2,Math.min(top,wr.height-10))+'px';
                    }
                    function clear(){
                      pts.forEach(function(c){c.classList.remove('on')});
                      rows.forEach(function(r){r.classList.remove('on')});
                      tip.hidden=true;
                    }

                    hit.addEventListener('pointermove',function(ev){
                      var r=svg.getBoundingClientRect();
                      var ux=(ev.clientX-r.left)/r.width*d.W, best=0, bd=1e9;
                      for(var i=0;i<d.n;i++){var dd=Math.abs(sx(i)-ux); if(dd<bd){bd=dd;best=i}}
                      show(best,ev.clientY);
                    });
                    hit.addEventListener('pointerleave',clear);
                    rows.forEach(function(r){
                      var i=+r.dataset.i;
                      r.addEventListener('pointerenter',function(ev){show(i,ev.clientY)});
                      r.addEventListener('focus',function(){show(i)});
                    });
                    panel.querySelector('.rw-tblwrap').addEventListener('pointerleave',clear);
                  });
                })();
                </script>
                </body>
                </html>
                """;
    }
}
