using RateDesk.Core;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>THE COMPOUNDED FIXING (trial, desk 2026-08-26): each run's overnight fixing
    /// compounded over the CURRENT meeting period — [period start, asOf), i.e. the realized
    /// floating leg of the front run-down swap, which is what the desk pricer anchors its front
    /// Step/Priced on ("Compounded SONIA 3.74 | Base Rate 3.75").
    ///
    /// Convention — validated 2026-08-26 against the desk pricer's own header values with real
    /// fixing history (RBNZ reproduced EXACTLY: flat 2.50 OCR since the 09-Jul-26 period start
    /// compounds to 2.5039 → their "2.504"; MPC/FOMC/ECB/BOJ all agree to display rounding;
    /// simple averaging fails — it prints 2.500):
    ///   · TRUE daily compounding, Π(1 + r·n/D) − 1, annualized ×D/N over the window,
    ///   · calendar-day weighted: a fixing applies from its own date until the next published
    ///     fixing (Friday spans 3 days; holidays roll into the prior fixing),
    ///   · D = the INDEX's own basis (FixingDcc: 360 for EFFR/ESTR/SWESTR/SARON, 365 else),
    ///   · window START = the current period's EFFECTIVE date, never the announcement — the
    ///     fixing regime changes at the effective date (using the RBNZ decision day instead
    ///     provably breaks the reproduction by the one old-rate day),
    ///   · window END = asOf (exclusive): today's fixing does not exist yet,
    ///   · fill-forward only across the publication lag — a fixing more than
    ///     <see cref="MaxStaleDays"/> old publishes NOTHING (hard-data rule; never guessed).</summary>
    public static class CompoundedFixing
    {
        public const int MaxStaleDays = 7;

        public sealed record Result(double Pct, DateTime From, int Days, int Dcc, string Ticker);

        /// <summary>The current period's start (effective date) — MeetingCalendar's derivation,
        /// re-exported for the tests and callers that reached it here.</summary>
        public static DateTime? CurrentPeriodStart(MeetingScheduleDef sched, DateTime asOf) =>
            MeetingCalendar.CurrentPeriodStart(sched, asOf);

        /// <summary>The compounding itself over [from, to) — pure math over a fixing series
        /// (percent levels, dated on the day each fixing is FOR). Null when the series cannot
        /// cover the window (no fixing on/before `from`, or the last fixing is further than
        /// MaxStaleDays behind `to`).</summary>
        public static double? Compound(IReadOnlyList<HistPoint> fixings, DateTime from, DateTime to, int dcc)
        {
            if (fixings.Count == 0 || (to - from).TotalDays <= 0) return null;
            var pts = fixings.OrderBy(p => p.Date).ToList();
            if ((to.Date - pts[^1].Date.Date).TotalDays > MaxStaleDays) return null;

            double growth = 1.0;
            int total = (int)(to.Date - from.Date).TotalDays;
            var cur = from.Date;
            int i = pts.FindLastIndex(p => p.Date.Date <= cur);
            if (i < 0) return null;
            while (cur < to.Date)
            {
                double r = pts[i].Value / 100.0;
                var next = i + 1 < pts.Count ? pts[i + 1].Date.Date : to.Date;
                if (next > to.Date) next = to.Date;
                if (next <= cur) { i++; continue; }   // superseded same-day duplicates
                int n = (int)(next - cur).TotalDays;
                growth *= 1 + r * n / dcc;
                cur = next;
                if (i + 1 < pts.Count && pts[i + 1].Date.Date <= cur) i++;
            }
            return (growth - 1) * dcc / total * 100.0;
        }

        /// <summary>Stamp every run in a freshly-built report with its ACTIVE pricing source and
        /// its compounded fixing — called by both the daily and weekly builders right after
        /// BuildWeekly, so the frozen report carries everything the surfaces need. A compounded
        /// value more than 15bp from the spot fixing earns a CHECK note (that gap is drift +
        /// compounding only — anything bigger is a data fault or a mid-window rate change worth
        /// eyes either way).</summary>
        public static void Stamp(WeeklyReport rep, PricingService svc,
            RateDesk.Core.Config.ConfigStore configs, Action<string>? log = null)
        {
            foreach (var run in rep.Runs)
            {
                var name = run.Title.Split('·')[0].Trim();
                var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                    string.IsNullOrEmpty(s.Kind) && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (sched == null) continue;
                run.Source = svc.MeetingSrc(sched);
                try
                {
                    if (svc.History != null
                        && ComputeFor(sched, configs, svc.History, DateTime.Today) is { } cf)
                    {
                        run.CompoundedPct = cf.Pct;
                        run.CompoundedFrom = cf.From;
                        log?.Invoke($"cmpd: {sched.Name} {cf.Pct:0.0000} " +
                                    $"({cf.Ticker}, {cf.From:dd-MMM-yy} → , {cf.Days}d, ACT/{cf.Dcc})");
                        if (run.RefPct is { } spot && Math.Abs(cf.Pct - spot) * 100.0 > 15.0)
                            rep.Notes.Add($"{OutlierGuard.Prefix}: {sched.Name} compounded fixing " +
                                          $"{cf.Pct:0.000} vs spot {spot:0.000} — gap " +
                                          $"{(cf.Pct - spot) * 100.0:+0.0;-0.0}bp exceeds 15bp; " +
                                          "verify the fixing history before distribution");
                    }
                    else log?.Invoke($"cmpd: {sched.Name} — no publishable value (fixing history " +
                                     "missing or stale; column stays blank)");
                }
                catch (Exception ex) { log?.Invoke($"! cmpd: {sched.Name}: {ex.Message}"); }
            }
        }

        /// <summary>Resolve the run's fixing ticker (schedule refTicker, else the currency's
        /// OIS overnight fixing — the boards' own rule), pull its history, and compound over
        /// the current period. Null on any gap — display-only trial, never a guess.</summary>
        public static Result? ComputeFor(MeetingScheduleDef sched,
            RateDesk.Core.Config.ConfigStore configs, IHistoryProvider history, DateTime asOf)
        {
            var refTicker = sched.RefTicker
                ?? configs.Enabled.FirstOrDefault(c =>
                    c.Ccy.Equals(sched.Ccy, StringComparison.OrdinalIgnoreCase))?.Ois?.OnFixingTicker;
            if (string.IsNullOrEmpty(refTicker)) return null;
            if (CurrentPeriodStart(sched, asOf) is not { } from) return null;
            int lookback = Math.Max(30, (int)(asOf.Date - from).TotalDays + 15);
            List<HistPoint> hist;
            try { hist = history.GetDaily(refTicker, lookback).ToList(); }
            catch { return null; }
            return Compound(hist, from, asOf.Date, sched.FixingDcc) is { } pct
                ? new Result(pct, from, (int)(asOf.Date - from).TotalDays, sched.FixingDcc, refTicker)
                : null;
        }
    }
}
