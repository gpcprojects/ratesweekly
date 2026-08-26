namespace RateDesk.Core
{
    /// <summary>Derivations over a schedule's OWN date lists (config-documented, no inference
    /// beyond the config's paired decision↔start meetings). Shared by the roll-day CoD
    /// correction, the compounded-fixing window, and anything else that needs "when did the
    /// current period start" or "when was the announcement".</summary>
    public static class MeetingCalendar
    {
        /// <summary>Median calendar-day gap decision → period start over the config's paired
        /// future meetings (each decision matched to the nearest start within 14d): 0 for
        /// FOMC/MPC, +1 for RBA/RBNZ/BOC/NORGES, +6 for ECB/RIKSBANK, +1..6 BOJ.</summary>
        public static int DecisionToStartLagDays(MeetingScheduleDef sched)
        {
            var lags = new List<int>();
            foreach (var dec in sched.DecisionDates)
            {
                var start = sched.Dates
                    .Where(d => d.Date >= dec.Date && (d.Date - dec.Date).TotalDays <= 14)
                    .OrderBy(d => d).Cast<DateTime?>().FirstOrDefault();
                if (start is { } s) lags.Add((int)(s.Date - dec.Date).TotalDays);
            }
            if (lags.Count == 0) return 0;
            lags.Sort();
            return lags[lags.Count / 2];
        }

        /// <summary>The current period's START (effective date): latest boundary ≤ asOf.
        /// sched.Dates entries ARE period starts; HAND-ENTERED PastDates are decision dates
        /// (config contract) and get the derived lag; PastDates that also appear in Dates were
        /// auto-migrated starts and pass as-is.</summary>
        public static DateTime? CurrentPeriodStart(MeetingScheduleDef sched, DateTime asOf)
        {
            var dateSet = sched.Dates.Select(d => d.Date).ToHashSet();
            int lag = DecisionToStartLagDays(sched);
            var candidates = sched.Dates.Select(d => d.Date)
                .Concat(sched.PastDates.Select(p => dateSet.Contains(p.Date) ? p.Date : p.Date.AddDays(lag)))
                .Where(d => d <= asOf.Date)
                .ToList();
            return candidates.Count > 0 ? candidates.Max() : null;
        }

        /// <summary>Every ANNOUNCEMENT date the schedule documents: decisionDates verbatim,
        /// plus start−lag for period starts with no recorded decision within the prior 14d
        /// (grids extend past the hand-curated decision list).</summary>
        public static IEnumerable<DateTime> AnnouncementDates(MeetingScheduleDef sched)
        {
            var decs = sched.DecisionDates.Select(d => d.Date).ToHashSet();
            foreach (var d in decs) yield return d;
            int lag = DecisionToStartLagDays(sched);
            foreach (var s in sched.Dates)
                if (!decs.Any(d => d <= s.Date && (s.Date - d).TotalDays <= 14))
                    yield return s.Date.AddDays(-lag);
        }
    }
}
