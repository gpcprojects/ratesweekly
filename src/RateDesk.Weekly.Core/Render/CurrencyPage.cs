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

        public static string Build(CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf,
            Func<MeetingScheduleDef, string>? meetingSource = null)
            => Page.Shell(
                $"DRAX Swaps — Weekly Rates Analysis — {cfg.Ccy}", cfg.Ccy,
                $"DRAX Swaps - Weekly Rates Analysis - {cfg.Ccy}", Body(cfg, src, store, asOf, meetingSource));

        /// <summary>The page's panels without the shell — the single-file edition hosts one of
        /// these per currency inside a single document. <paramref name="meetingSource"/>: the
        /// ACTIVE per-run contributor (SOURCES selection) — the dashboards price meetings off
        /// the same feed as the email (desk 2026-08-26).</summary>
        public static string Body(CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf,
            Func<MeetingScheduleDef, string>? meetingSource = null)
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
                // the strip's decision gate rides the MARKS' clock, not the wall clock (audit
                // 2026-08-31, scenario 106): this page renders stored closes as of asOf, so a
                // statement made after that close must not roll a meeting off here while the
                // blast built from the same marks keeps it — same rule as svc.MarksAsOfLondon
                var rows = Panels.From(RollingStrip.ForMeetings(sched, store, asOf,
                    nowLondon: asOf.Date + SnapDiscipline.SnapAt,
                    source: meetingSource?.Invoke(sched)));
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
                    bool idx = fam.Unit == CpiFixings.FixUnit.IndexLevel;
                    // An index-quoted family moves in index points, not basis points — scaling by
                    // 100 would print "-55.0" for a 0.55-point move under a column every other
                    // panel reads as bp.
                    body.Append(Panels.Linked("inffix",
                        $"{cfg.Ccy} {fam.Name} monthly fixings ({fx.ValueLabel})",
                        fx.Rows, valueDp: fx.Dp, valueSuffix: idx ? "" : "%",
                        changeScale: idx ? 1.0 : 100.0, changeDp: idx ? 2 : 1,
                        changeUnit: idx ? " idx" : ""));
                }

                var pub = Inflation.Fixings(lad, cfg.Ccy, store, asOf);
                if (pub.Count > 0)
                    body.Append(Panels.Linked("infpub", $"{cfg.Ccy} {lad.Name} published prints",
                        pub, valueDp: 2, valueSuffix: "",
                        changeScale: 1.0, changeDp: 2, changeUnit: " idx"));
            }

            body.Append(Pending("Rolling correlations",
                "needs ~2.5 years of history; the store currently holds about a month. " +
                "This section fills itself in once the history is deepened — no code change."));

            return body.ToString();
        }

        private static string Pending(string title, string why) =>
            $"<section class=\"rw-panel\"><header class=\"rw-panel-head\"><h3>{Viz.Esc(title)}</h3></header>"
          + $"<div class=\"rw-pending\">{Viz.Esc(why)}</div></section>";
    }
}
