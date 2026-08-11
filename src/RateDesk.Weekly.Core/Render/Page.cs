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
        /// (desk layout, 2026-08-05). <paramref name="href"/> resolves a target ("movers" or a
        /// ccy) to a link — file-per-page by default; the single-file edition passes hash
        /// anchors so its router can switch sections in place.</summary>
        public static string Nav(string current, Func<string, string>? href = null)
        {
            href ??= c => c.Equals("movers", StringComparison.OrdinalIgnoreCase)
                ? "index.html" : c.ToLowerInvariant() + ".html";
            var sb = new StringBuilder("<nav class=\"rw-nav\">");

            sb.Append("<div class=\"rw-navrow\"><a class=\"rw-hub");
            sb.Append(current.Equals("movers", StringComparison.OrdinalIgnoreCase) ? " on" : "");
            sb.Append($"\" href=\"{href("movers")}\">◆ Movers</a>");
            sb.Append(GroupHtml(Groups[0], current, href));
            sb.Append("</div>");

            sb.Append("<div class=\"rw-navrow\">");
            for (int g = 1; g < Groups.Length; g++) sb.Append(GroupHtml(Groups[g], current, href));
            sb.Append("</div>");

            return sb.Append("</nav>").ToString();
        }

        private static string GroupHtml(string[] g, string current, Func<string, string> href)
        {
            var sb = new StringBuilder($"<span class=\"rw-grp\"><b>{Viz.Esc(g[0])}</b>");
            for (int i = 1; i < g.Length; i++)
            {
                bool on = g[i].Equals(current, StringComparison.OrdinalIgnoreCase);
                sb.Append($"<a class=\"rw-cc{(on ? " on" : "")}\" href=\"{href(g[i])}\">{g[i]}</a>");
            }
            return sb.Append("</span>").ToString();
        }

        public static string Shell(string title, string current, string heading, string body,
            Func<string, string>? navHref = null, bool wrapPanels = true)
        {
            // Token replacement, not interpolation: the CSS and JS below are full of braces, which
            // fight every raw-string interpolation form.
            return Template
                .Replace("%%TITLE%%", Viz.Esc(title))
                .Replace("%%THEMECSS%%", Viz.ThemeCss)
                .Replace("%%HEADING%%", Viz.Esc(heading))
                .Replace("%%NAV%%", Nav(current, navHref))
                // the single-file edition carries several rw-panels grids of its own, one per page
                .Replace("%%BODY%%", wrapPanels ? $"<div class=\"rw-panels\">{body}</div>" : body);
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
                /* Sized for a full 1920x1080 desk display: the wrap runs near the full width and
                   panels tile two-up, so a currency's whole story fits without hunting. Narrower
                   screens collapse to one column through the same auto-fit rule. */
                .rw-wrap{max-width:1860px;margin:0 auto;padding:18px 22px 48px}
                header.rw-head{position:relative;display:flex;align-items:baseline;justify-content:center;
                  gap:12px;flex-wrap:wrap;margin-bottom:2px;text-align:center}
                header.rw-head h1{font-size:24px;margin:0;letter-spacing:-.01em}
                .rw-sub{color:var(--rw-ink2);font-size:12px;margin:2px 0 0}
                .rw-nav{display:flex;flex-direction:column;gap:8px;margin:14px 0 18px;
                  padding:10px 14px;background:var(--rw-surface);border:1px solid var(--rw-border);border-radius:10px}
                .rw-navrow{display:flex;flex-wrap:wrap;gap:6px 18px;align-items:center;justify-content:center}
                .rw-navrow+.rw-navrow{border-top:1px solid var(--rw-border);padding-top:8px}
                .rw-grp{display:flex;gap:4px;align-items:center;flex-wrap:wrap}
                .rw-grp b{font-size:13px;letter-spacing:.06em;color:var(--rw-ink);text-transform:uppercase;
                  font-weight:700;margin-right:4px}
                .rw-nav a{text-decoration:none;color:var(--rw-ink2);padding:3px 7px;border-radius:6px;font-size:12px}
                .rw-nav a:hover{background:var(--rw-plane);color:var(--rw-ink)}
                .rw-nav a.on{background:var(--rw-today);color:var(--rw-surface);font-weight:600}
                .rw-hub{font-weight:700;font-size:13px;color:var(--rw-ink);letter-spacing:.02em}
                /* Two panels per row on a widescreen; one on anything narrower. 820px is the point
                   below which a panel's table+chart split stops being readable. */
                .rw-panels{display:grid;grid-template-columns:repeat(auto-fit,minmax(820px,1fr));
                  gap:16px;align-items:stretch}
                .rw-panel{background:var(--rw-surface);border:1px solid var(--rw-border);border-radius:12px;
                  padding:12px 12px 10px}
                .rw-panel-head h3{margin:0;font-size:14px;font-weight:600}
                /* Table stays as narrow as its numbers allow; the chart takes every remaining pixel
                   and fills the panel's height, so panels in a row end up the same size. */
                .rw-panel{display:flex;flex-direction:column}
                /* FIXED table width, not auto. With an auto column the track sized to the widest
                   label in that currency (4 chars for "1Y", 18 for "23-Dec-26 (interp)"), which
                   pushed the outer grid wider and made the 2-pane breakpoint drift per currency.
                   128px fits the longest label in any market, so every page is now identical. */
                .rw-panel-body{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:10px;
                  align-items:start;margin-top:8px;flex:1}
                .rw-panel-body>*{min-width:0}
                .rw-panel,.rw-panels>*{min-width:0}
                .rw-tblwrap{overflow-x:visible;overflow-y:auto;max-height:460px}
                table.rw-lvl{border-collapse:collapse;width:max-content;font-size:11.5px;white-space:nowrap}
                table.rw-lvl th{position:sticky;top:0;background:var(--rw-surface);text-align:right;
                  font-weight:500;font-size:9.5px;letter-spacing:.03em;text-transform:uppercase;
                  color:var(--rw-muted);padding:2px 6px;border-bottom:1px solid var(--rw-grid)}
                table.rw-lvl th:first-child{text-align:left}
                table.rw-lvl td{padding:2px 6px;text-align:right;font-variant-numeric:tabular-nums;
                  border-bottom:1px solid var(--rw-grid)}
                table.rw-lvl td.rw-lab{text-align:left;color:var(--rw-ink2);white-space:nowrap;padding-left:2px}
                td.rw-val{font-weight:600}
                td.rw-bp{font-size:11px}
                .rw-upbp{color:var(--rw-up)}.rw-downbp{color:var(--rw-down)}
                .rw-flatbp,.rw-nil{color:var(--rw-muted)}
                tr.rw-row{cursor:default}
                tr.rw-row:hover,tr.rw-row.on,tr.rw-row:focus{background:var(--rw-plane);outline:none}
                tr.rw-row.on td.rw-lab{color:var(--rw-ink);font-weight:600}
                .rw-chartwrap{position:relative;min-width:0;display:flex}
                circle.rw-pt{opacity:0;transition:opacity .08s}
                circle.rw-pt.on{opacity:1}
                .rw-cross{stroke:var(--rw-axis);stroke-width:1}
                .rw-klab{fill:var(--rw-ink2);font-size:17px}
                .rw-tip .r.sep{border-top:1px solid var(--rw-border);margin-top:4px;padding-top:4px}
                .rw-svg{width:100%;height:auto;display:block;overflow:visible}
                .rw-grid{stroke:var(--rw-grid);stroke-width:1}
                .rw-axis{stroke:var(--rw-axis);stroke-width:1}
                .rw-tick{fill:var(--rw-ink2);font-size:17px;font-variant-numeric:tabular-nums}
                .rw-tick-y{text-anchor:end}.rw-tick-x{text-anchor:middle}
                .rw-tip{position:absolute;pointer-events:none;background:var(--rw-surface);
                  border:1px solid var(--rw-border);border-radius:8px;padding:7px 9px;font-size:11px;
                  box-shadow:0 4px 14px rgba(0,0,0,.13);z-index:5;min-width:118px}
                .rw-tip b{display:block;margin-bottom:3px;font-size:11px}
                .rw-tip .r{display:flex;justify-content:space-between;gap:12px;font-variant-numeric:tabular-nums}
                .rw-tip .r i{width:9px;height:9px;border-radius:2px;display:inline-block;margin-right:5px}
                .rw-empty{color:var(--rw-muted);font-size:12px;padding:26px 0;text-align:center;font-style:italic}
                .rw-pending{background:var(--rw-surface);border:1px dashed var(--rw-axis);border-radius:12px;
                  padding:20px;color:var(--rw-muted);font-size:12px}
                .rw-toggle{position:absolute;right:0;top:0;background:var(--rw-surface);color:var(--rw-ink2);
                  cursor:pointer;border:1px solid var(--rw-border);border-radius:8px;padding:5px 10px;font-size:12px}
                /* movers hub */
                .rw-wide{grid-column:1/-1}
                .rw-ctx{color:var(--rw-ink2);font-size:12.5px;line-height:1.55;margin-top:6px}
                .rw-ctx p{margin:0 0 4px}
                .rw-heroes{display:grid;grid-template-columns:repeat(auto-fit,minmax(330px,1fr));gap:12px;margin:10px 0 14px}
                .rw-hero{background:var(--rw-plane);border:1px solid var(--rw-border);border-radius:10px;
                  padding:12px 14px;min-width:0}
                .rw-hero-top{display:flex;justify-content:space-between;align-items:baseline;gap:8px}
                .rw-hero-name{font-size:15px;font-weight:700;color:var(--rw-ink);text-decoration:none}
                .rw-hero-name:hover{text-decoration:underline}
                .rw-kind{font-size:9.5px;letter-spacing:.05em;color:var(--rw-muted);text-transform:uppercase;
                  border:1px solid var(--rw-border);border-radius:5px;padding:1px 6px;white-space:nowrap}
                .rw-hero-move{margin:6px 0 6px;font-size:13px;color:var(--rw-ink2)}
                .rw-hero-move b{font-size:17px;font-variant-numeric:tabular-nums}
                .rw-est{font-size:9.5px;color:var(--rw-muted);vertical-align:super;margin-left:2px}
                .rw-spark{width:100%;height:64px;display:block;margin:2px 0 4px}
                .rw-stat-row{display:flex;gap:18px;flex-wrap:wrap;margin-top:6px}
                .rw-stat b{display:block;font-size:13.5px;font-variant-numeric:tabular-nums}
                .rw-stat span{font-size:9.5px;color:var(--rw-muted);text-transform:uppercase;letter-spacing:.04em}
                table.rw-mv{border-collapse:collapse;width:100%;font-size:12px;white-space:nowrap}
                table.rw-mv th{text-align:right;font-weight:500;font-size:9.5px;letter-spacing:.03em;
                  text-transform:uppercase;color:var(--rw-muted);padding:3px 8px;border-bottom:1px solid var(--rw-grid)}
                table.rw-mv th.l,table.rw-mv td.l{text-align:left}
                table.rw-mv td{padding:3px 8px;text-align:right;font-variant-numeric:tabular-nums;
                  border-bottom:1px solid var(--rw-grid)}
                table.rw-mv a{color:var(--rw-ink);text-decoration:none;font-weight:600}
                table.rw-mv a:hover{text-decoration:underline}
                @media (max-width:900px){.rw-panel-body{grid-template-columns:1fr}}
                </style>
                </head>
                <body>
                <div class="rw-wrap">
                <header class="rw-head">
                  <h1>%%HEADING%%</h1>
                  <button class="rw-toggle" id="rwTheme" type="button">◑ theme</button>
                </header>
                %%NAV%%
                %%BODY%%
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
                    var cross=panel.querySelector('.rw-cross');
                    var pw=d.W-d.ml-d.mr;
                    function sx(i){return d.n<=1?d.ml+pw/2:d.ml+i/(d.n-1)*pw}
                    function fmt(v){return v===null||v===undefined?'--':v.toFixed(d.dp)+d.suffix}
                    function bp(v){return (v>0?'+':'')+v.toFixed(1)+'bp'}

                    function show(i,clientY){
                      pts.forEach(function(c){c.classList.toggle('on',+c.dataset.i===i)});
                      rows.forEach(function(r){r.classList.toggle('on',+r.dataset.i===i)});
                      if(cross){var px=sx(i); cross.setAttribute('x1',px); cross.setAttribute('x2',px); cross.style.display='';}
                      var h='<b>'+d.labels[i]+'</b>';
                      h+='<div class="r"><span><i style="background:var(--rw-today)"></i>today</span><span>'+fmt(d.now[i])+'</span></div>';
                      if(d.week[i]!==null&&d.week[i]!==undefined)
                        h+='<div class="r"><span><i style="background:var(--rw-week)"></i>1w ago</span><span>'+fmt(d.week[i])+'</span></div>';
                      if(d.month[i]!==null&&d.month[i]!==undefined)
                        h+='<div class="r"><span><i style="background:var(--rw-month)"></i>1m ago</span><span>'+fmt(d.month[i])+'</span></div>';
                      if(d.w1&&d.w1[i]!==null&&d.w1[i]!==undefined)
                        h+='<div class="r sep"><span>1w change</span><span>'+bp(d.w1[i])+'</span></div>';
                      if(d.m1&&d.m1[i]!==null&&d.m1[i]!==undefined)
                        h+='<div class="r"><span>1m change</span><span>'+bp(d.m1[i])+'</span></div>';
                      tip.innerHTML=h; tip.hidden=false;
                      var wr=wrap.getBoundingClientRect(), tw=tip.offsetWidth;
                      var px2=sx(i)/d.W*wr.width;
                      var left=px2+12; if(left+tw>wr.width-6)left=px2-tw-12;
                      tip.style.left=Math.max(2,left)+'px';
                      var top=(clientY===undefined?wr.top+wr.height/2:clientY)-wr.top+12;
                      tip.style.top=Math.max(2,Math.min(top,wr.height-10))+'px';
                    }
                    function clear(){
                      pts.forEach(function(c){c.classList.remove('on')});
                      rows.forEach(function(r){r.classList.remove('on')});
                      if(cross)cross.style.display='none';
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
