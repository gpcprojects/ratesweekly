using System.Globalization;
using System.Text;
using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>Builds one currency's dashboard page from the history store. Sections auto-drop
    /// where the market doesn't exist (no meetings run, no FRA strip, no inflation ladder), and a
    /// section that needs more history than the store holds renders an explicit "pending" panel —
    /// never a fabricated series.</summary>
    public static class CurrencyPage
    {
        public static string Build(CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf)
        {
            var body = new StringBuilder();
            var par = WeeklyCurves.ParCurve(cfg, src, store, asOf);
            var ladder = ForwardLadder.Build(cfg, src, store, asOf, par);

            body.Append(Panels.Linked("levels", $"{cfg.Ccy} par swaps",
                "quoted par rate by tenor · level, and change in bp vs 1 week and 1 month ago",
                Panels.From(par), par.Notes));

            body.Append(Panels.Linked("fwd", $"{cfg.Ccy} forward ladder",
                "spot 1y and the quoted forward grid out to 30y20y",
                Panels.From(ladder), ladder.Notes));

            // ---- central bank meetings -------------------------------------------------
            foreach (var sched in MeetingsStore.Schedules)
            {
                if (!sched.Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)) continue;
                if (sched.Kind.Equals("fra", StringComparison.OrdinalIgnoreCase)) continue;
                var strip = RollingStrip.ForMeetings(sched, store, asOf);
                var rows = Panels.From(strip);
                if (rows.Count == 0) continue;
                body.Append(Panels.Linked($"mtg-{sched.Name.ToLowerInvariant()}",
                    $"{sched.Name} meeting-dated OIS",
                    "one row per scheduled decision · changes follow the meeting through ticker rolls",
                    rows, strip.Notes));
            }

            // ---- FRA strip --------------------------------------------------------------
            var (fraRows, fraNotes) = FraRun.Build(cfg, src, store, asOf);
            if (fraRows.Count > 0)
                body.Append(Panels.Linked("fra", $"{cfg.Ccy} FRA run",
                    "quoted forward rate agreements", fraRows, fraNotes));

            // ---- inflation ---------------------------------------------------------------
            foreach (var lad in cfg.Ladders.Where(l =>
                         l.Kind.Equals("INFLATION", StringComparison.OrdinalIgnoreCase)))
            {
                var infPar = Inflation.ParCurve(lad, store, asOf);
                if (infPar.Count > 0)
                    body.Append(Panels.Linked("infpar", $"{cfg.Ccy} {lad.Name} zero-coupon curve",
                        "quoted breakeven by tenor", infPar, new List<string>()));

                var infFwd = Inflation.Forwards(lad, store, asOf);
                if (infFwd.Count > 0)
                    body.Append(Panels.Linked("inffwd", $"{cfg.Ccy} {lad.Name} forwards",
                        "quoted inflation forwards", infFwd, new List<string>()));

                var fix = Inflation.Fixings(lad, cfg.Ccy, store, asOf);
                body.Append(Panels.Linked("inffix", $"{cfg.Ccy} {lad.Name} fixings",
                    $"published index prints · {Inflation.LagNote(cfg.Ccy, asOf)}",
                    fix, new List<string>
                    {
                        "index prints monthly, so this section fills in as the stored history deepens",
                        "⚠ lag convention pending desk confirmation",
                    }, valueDp: 2, valueSuffix: ""));
            }

            body.Append(Pending("Rolling correlations",
                "2y vs oil, and 10y vs US 10y and DXY, on a 63-day window over ~2 years",
                "needs ~2.5 years of history; the store currently holds about a month. " +
                "This section fills itself in once the history is deepened — no code change."));

            return Page.Shell(
                $"{cfg.Ccy} — RatesWeekly", cfg.Ccy, cfg.Ccy,
                $"close of {asOf:dddd d MMMM yyyy}", body.ToString(),
                DateTime.Now.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture));
        }

        private static string Pending(string title, string sub, string why) =>
            $"<section class=\"rw-panel\"><header class=\"rw-panel-head\"><h3>{Viz.Esc(title)}</h3>"
          + $"<p class=\"rw-sub\">{Viz.Esc(sub)}</p></header>"
          + $"<div class=\"rw-pending\">{Viz.Esc(why)}</div></section>";
    }
}
