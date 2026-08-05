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
        /// <summary>Currencies whose FRA strip is quoted but not really traded, so it earns no
        /// panel. EUR: the desk's call, 2026-08-05.</summary>
        private static readonly HashSet<string> NoFraPanel =
            new(StringComparer.OrdinalIgnoreCase) { "EUR" };

        public static string Build(CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf)
        {
            var body = new StringBuilder();
            var par = WeeklyCurves.ParCurve(cfg, src, store, asOf);
            // The ladder's fallback interpolates off the par curve, so give it every quoted pillar
            // rather than the trimmed display set.
            var parFull = WeeklyCurves.ParCurve(cfg, src, store, asOf, standardOnly: false);
            var ladder = ForwardLadder.Build(cfg, src, store, asOf, parFull);

            body.Append(Panels.Linked("levels", $"{cfg.Ccy} par swaps", Panels.From(par)));

            body.Append(Panels.Linked("fwd", $"{cfg.Ccy} forward ladder", Panels.From(ladder)));

            foreach (var sched in MeetingsStore.Schedules)
            {
                if (!sched.Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)) continue;
                if (sched.Kind.Equals("fra", StringComparison.OrdinalIgnoreCase)) continue;
                var rows = Panels.From(RollingStrip.ForMeetings(sched, store, asOf));
                if (rows.Count == 0) continue;
                body.Append(Panels.Linked($"mtg-{sched.Name.ToLowerInvariant()}",
                    $"{sched.Name} meeting-dated OIS", rows));
            }

            if (!NoFraPanel.Contains(cfg.Ccy))
            {
                var (fraRows, _) = FraRun.Build(cfg, src, store, asOf);
                if (fraRows.Count > 0)
                    body.Append(Panels.Linked("fra", $"{cfg.Ccy} FRA run", fraRows));
            }

            foreach (var lad in cfg.Ladders.Where(l =>
                         l.Kind.Equals("INFLATION", StringComparison.OrdinalIgnoreCase)))
            {
                var infPar = Inflation.ParCurve(lad, store, asOf);
                if (infPar.Count > 0)
                    body.Append(Panels.Linked("infpar", $"{cfg.Ccy} {lad.Name} zero-coupon curve", infPar));

                var infFwd = Inflation.Forwards(cfg.Ccy, store, asOf);
                if (infFwd.Count > 0)
                    body.Append(Panels.Linked("inffwd", $"{cfg.Ccy} {lad.Name} forwards", infFwd));

                if (CpiFixings.For(cfg.Ccy) is { } fam)
                {
                    var fx = CpiFixings.Build(fam, store, asOf);
                    body.Append(Panels.Linked("inffix",
                        $"{cfg.Ccy} {fam.Name} monthly fixings ({fx.ValueLabel})",
                        fx.Rows, valueDp: fx.Dp,
                        valueSuffix: fam.Unit == CpiFixings.FixUnit.YoYBp ? "%" : ""));
                }

                var pub = Inflation.Fixings(lad, cfg.Ccy, store, asOf);
                if (pub.Count > 0)
                    body.Append(Panels.Linked("infpub", $"{cfg.Ccy} {lad.Name} published prints",
                        pub, valueDp: 2, valueSuffix: ""));
            }

            body.Append(Pending("Rolling correlations",
                "needs ~2.5 years of history; the store currently holds about a month. " +
                "This section fills itself in once the history is deepened — no code change."));

            return Page.Shell(
                $"DRAX Swaps — Weekly Rates Analysis — {cfg.Ccy}", cfg.Ccy,
                $"DRAX Swaps - Weekly Rates Analysis - {cfg.Ccy}", body.ToString());
        }

        private static string Pending(string title, string why) =>
            $"<section class=\"rw-panel\"><header class=\"rw-panel-head\"><h3>{Viz.Esc(title)}</h3></header>"
          + $"<div class=\"rw-pending\">{Viz.Esc(why)}</div></section>";
    }
}
