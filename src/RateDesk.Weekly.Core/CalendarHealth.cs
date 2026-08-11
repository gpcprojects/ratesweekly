using RateDesk.Core;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>The zero-touch guarantee that meeting handling KEEPS working as dates roll off
    /// and new ones arrive (desk 2026-08-11). Correctness on any given day already comes from
    /// the code (SW_EFF_DT labelling, decision-date boundaries, the announced-not-yet-effective
    /// re-base); what needs policing over time is the DATA those rules read. Every UPDATE runs
    /// these checks and surfaces failures as run warnings, so calendar work is demanded loudly
    /// weeks before anything can misprint — and everything degrades honestly meanwhile.
    ///
    /// 1. GRID vs TICKER TRUTH — every live rung's own SW_EFF_DT must appear in the run's
    ///    date grid (within the settlement-lag pairing). A drifted or missing period is the
    ///    phantom-meeting fault class.
    /// 2. DECISION RUNWAY — the decision calendar must cover the next ~90 days of periods;
    ///    past its end the front table degrades to "start *" (honest, but worth fixing early).
    /// 3. OBSERVED ROLLS — days the STORE saw a rung's recorded maturity change must sit near a
    ///    calendar boundary. A re-point the calendar doesn't know about is flagged on the very
    ///    next update, not discovered in a wrong lookback.</summary>
    public static class CalendarHealth
    {
        public const int DecisionRunwayDays = 90;
        private const int PairToleranceDays = 10;   // the settlement-lag bound used everywhere
        private const int RollMatchDays = 2;

        public static List<string> Check(RatesSnapshot snap, HistoryStore store, DateTime asOf)
            => Check(MeetingsStore.Schedules, snap, store, asOf);

        public static List<string> Check(
            IEnumerable<MeetingScheduleDef> schedules, RatesSnapshot snap, HistoryStore store, DateTime asOf)
        {
            var warnings = new List<string>();
            foreach (var sched in schedules)
            {
                if (!string.IsNullOrEmpty(sched.Kind)) continue;   // FRA strips key on IMM, not calendars
                var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (pat == null) continue;

                var grid = sched.Dates.Concat(sched.PastDates).Select(d => d.Date).ToList();
                var decisions = sched.DecisionDates.Select(d => d.Date).OrderBy(d => d).ToList();
                var bounds = decisions.Concat(grid).OrderBy(d => d).ToList();

                // 1. every live rung's own period start must pair with a grid date
                for (int n = 1; n <= 13; n++)
                {
                    var q = snap.Get(pat.Replace("{N}", n.ToString()) + " Curncy");
                    if (q?.Effective is not { } eff) continue;
                    if (eff.Date <= asOf.Date || eff.Date > asOf.Date.AddYears(2)) continue;
                    if (!grid.Any(g => Math.Abs((g - eff.Date).TotalDays) <= PairToleranceDays))
                        warnings.Add($"{sched.Name}: ticker period starting {eff:dd-MMM-yy} is not in " +
                                     "config\\meetings.json — grid drifted or missing (phantom-meeting risk)");
                }

                // 2. decision runway — only for runs that maintain a decision calendar at all
                if (decisions.Count > 0)
                {
                    var horizon = asOf.Date.AddDays(DecisionRunwayDays);
                    var uncovered = sched.Dates
                        .Where(d => d.Date > asOf.Date && d.Date <= horizon)
                        .Where(start => !decisions.Any(dec => dec <= start.Date
                            && (start.Date - dec).TotalDays <= PairToleranceDays))
                        .OrderBy(d => d).ToList();
                    if (uncovered.Count > 0)
                        warnings.Add($"{sched.Name}: no decision date for the period starting " +
                                     $"{uncovered[0]:dd-MMM-yy} — top up decisionDates from the official " +
                                     "calendar (front table shows start* until then)");
                }

                // 3. observed rolls must be explained by the calendar
                foreach (var change in store.MaturityChanges(pat.Replace("{N}", "1") + " Curncy", 120))
                {
                    if (!bounds.Any(b => Math.Abs((b - change.Date).TotalDays) <= RollMatchDays))
                        warnings.Add($"{sched.Name}: ticker re-pointed on {change:dd-MMM-yy} with no " +
                                     "calendar boundary within 2 days — check the calendars against " +
                                     "tools\\..\\dodgeball probe output");
                }
            }
            return warnings;
        }
    }
}
