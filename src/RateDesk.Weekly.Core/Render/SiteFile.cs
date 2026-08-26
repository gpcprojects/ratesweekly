using System.Text;
using RateDesk.Core.Config;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>The whole site in ONE self-contained file — the email attachment. Every dashboard
    /// (movers hub + 28 currencies) sits in the document as a hidden section behind the shared
    /// nav, and a hash router switches between them. Opens from disk in any browser, fully
    /// offline: no hosting, no sign-in, nothing external — the "all-localised" delivery the desk
    /// asked for (2026-08-11). ~1.2 MB for the full set, comfortably inside mail limits.
    /// KNOWN RISK: some receiving mail gateways quarantine .html attachments — the first send to
    /// an external recipient is the acceptance test; the PDF pack is the fallback if one bites.</summary>
    public static class SiteFile
    {
        public const string FileName = "RatesWeekly_Dashboards.html";

        public static string Build(
            ConfigStore configs, Func<string, string> srcFor, HistoryStore store,
            DateTime asOf, MoversResult movers,
            Func<RateDesk.Core.MeetingScheduleDef, string>? meetingSource = null)
        {
            var body = new StringBuilder();
            body.Append("<section class=\"rw-page\" id=\"pg-movers\"><div class=\"rw-panels\">")
                .Append(MoversPage.Body(movers, m => "#" + m.Ccy.ToLowerInvariant()))
                .Append("</div></section>");

            foreach (var cfg in configs.Enabled)
            {
                if (cfg.Ois == null && cfg.Irs == null && cfg.Ladders.Count == 0) continue;
                string id = cfg.Ccy.ToLowerInvariant();
                body.Append($"<section class=\"rw-page\" id=\"pg-{id}\" hidden><div class=\"rw-panels\">")
                    .Append(CurrencyPage.Body(cfg, srcFor(cfg.Ccy), store, asOf, meetingSource))
                    .Append("</div></section>");
            }
            body.Append(RouterJs);

            return Page.Shell(
                $"DRAX Swaps — Weekly Rates Analysis — week to {asOf:dd MMM yyyy}", "movers",
                $"DRAX Swaps - Weekly Rates Analysis - {asOf:dd MMM yy}",
                body.ToString(),
                navHref: c => "#" + c.ToLowerInvariant(),
                wrapPanels: false);
        }

        // Hash router: #usd shows that page; no hash, or an unknown one, shows the movers hub;
        // the nav highlight follows. Plain DOM only, so it runs from file:// with nothing external.
        private const string RouterJs = """
            <script>
            (function(){
              var pages=[].slice.call(document.querySelectorAll('.rw-page'));
              function show(id){
                var hit=false;
                pages.forEach(function(s){var on=s.id==='pg-'+id;s.hidden=!on;hit=hit||on;});
                if(!hit){pages.forEach(function(s){s.hidden=s.id!=='pg-movers';});id='movers';}
                [].slice.call(document.querySelectorAll('.rw-nav a')).forEach(function(a){
                  a.classList.toggle('on',a.getAttribute('href')==='#'+id);});
                window.scrollTo(0,0);
              }
              function cur(){return (location.hash||'#movers').replace('#','')||'movers';}
              window.addEventListener('hashchange',function(){show(cur());});
              show(cur());
            })();
            </script>
            """;
    }
}
