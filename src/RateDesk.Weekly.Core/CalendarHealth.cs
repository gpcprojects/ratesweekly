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

                // 2b. the decision-day front roll and intraday priced re-base are gated on the
                // announcement time — a run that keeps decisions but no time degrades to rolling
                // the morning after (honest, but a decision day reads stale all afternoon)
                // NOT gated on decisions.Count any more (fix 2026-08-27, scenario 06): the one
                // configuration that turns the decision-day machinery off entirely - no calendar
                // at all - used to raise no warning of any kind, because every check here sat
                // behind that gate.
                if (decisions.Count == 0)
                    warnings.Add($"{OutlierGuard.Prefix}: {sched.Name} has NO decisionDates in " +
                                 "config\\meetings.json - the decision-day front roll AND the " +
                                 "Priced re-base are both disabled for this run");
                else if (!TimeSpan.TryParse(sched.DecisionTimeLondon, out _))
                    warnings.Add($"{OutlierGuard.Prefix}: {sched.Name} has no decisionTimeLondon " +
                                 "in config\\meetings.json - the decision-day front roll degrades " +
                                 "to the next morning, so a decision day publishes an already-" +
                                 "delivered move as still priced in");

                // 3. observed rolls must be explained by the calendar. Updates are WEEKLY, so the
                // re-point is only known to lie in the OBSERVATION WINDOW (prev update, this one]
                // — a boundary anywhere in that window (±ε) explains it; judging the observation
                // date alone false-flagged every roll seen late (live RBA/NORGES, 2026-08-20).
                foreach (var (seen, prevSeen) in store.MaturityChanges(pat.Replace("{N}", "1") + " Curncy", 120))
                {
                    if (!bounds.Any(b => b > prevSeen.AddDays(-RollMatchDays)
                                      && b <= seen.AddDays(RollMatchDays)))
                        warnings.Add($"{sched.Name}: ticker re-pointed between {prevSeen:dd-MMM-yy} and " +
                                     $"{seen:dd-MMM-yy} with no calendar boundary in that window — check " +
                                     "the calendars against tools\\..\\dodgeball probe output");
                }
            }
            return warnings;
        }
    }
}
