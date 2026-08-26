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
        public static DateTime? CurrentPeriodStart(MeetingScheduleDef sched, DateTime asOf) =>
            CurrentPeriodStartEx(sched, asOf)?.Start;

        /// <summary>As <see cref="CurrentPeriodStart"/>, plus whether the winning boundary was
        /// DERIVED (a hand-entered decision shifted by the median lag) rather than an exact
        /// grid date — derived starts can be a day or two off for variable-lag families, and
        /// consumers whose output is sensitive to that must guard (fresh-eyes review 2026-08-26).</summary>
        public static (DateTime Start, bool Derived)? CurrentPeriodStartEx(
            MeetingScheduleDef sched, DateTime asOf)
        {
            var dateSet = sched.Dates.Select(d => d.Date).ToHashSet();
            int lag = DecisionToStartLagDays(sched);
            var candidates = sched.Dates.Select(d => (Date: d.Date, Derived: false))
                .Concat(sched.PastDates.Select(p => dateSet.Contains(p.Date)
                    ? (Date: p.Date, Derived: false)
                    : (Date: p.Date.AddDays(lag), Derived: true)))
                .Where(c => c.Date <= asOf.Date)
                .ToList();
            if (candidates.Count == 0) return null;
            var best = candidates.OrderBy(c => c.Date).ThenBy(c => c.Derived).Last();
            // an exact grid date on the same day beats a derived one
            var exact = candidates.Where(c => c.Date == best.Date && !c.Derived).ToList();
            return exact.Count > 0 ? (best.Date, false) : (best.Date, best.Derived);
        }

        /// <summary>True when the schedule's decision→start lag is CONSISTENT across its paired
        /// meetings (spread ≤ 1 day) — only then is start−lag a trustworthy stand-in for an
        /// unrecorded announcement. BOJ's lag runs 1–6 days (settlement is "next Tokyo business
        /// day", holiday-dependent), so a derived date there can be days off — and a roll
        /// correction fired on a phantom date manufactures a full step of CoD (fresh-eyes
        /// review 2026-08-26).</summary>
        public static bool LagIsStable(MeetingScheduleDef sched)
        {
            var lags = new List<int>();
            foreach (var dec in sched.DecisionDates)
            {
                var start = sched.Dates
                    .Where(d => d.Date >= dec.Date && (d.Date - dec.Date).TotalDays <= 14)
                    .OrderBy(d => d).Cast<DateTime?>().FirstOrDefault();
                if (start is { } s) lags.Add((int)(s.Date - dec.Date).TotalDays);
            }
            return lags.Count > 0 && lags.Max() - lags.Min() <= 1;
        }

        /// <summary>Every ANNOUNCEMENT date the schedule documents: decisionDates verbatim,
        /// plus start−lag for period starts with no recorded decision within the prior 14d
        /// (grids extend past the hand-curated decision list) — the derivation only offered
        /// when the lag is stable (see <see cref="LagIsStable"/>); a variable-lag family
        /// yields nothing it cannot stand behind.</summary>
        public static IEnumerable<DateTime> AnnouncementDates(MeetingScheduleDef sched)
        {
            var decs = sched.DecisionDates.Select(d => d.Date).ToHashSet();
            foreach (var d in decs) yield return d;
            if (!LagIsStable(sched)) yield break;
            int lag = DecisionToStartLagDays(sched);
            foreach (var s in sched.Dates)
                if (!decs.Any(d => d <= s.Date && (s.Date - d).TotalDays <= 14))
                    yield return s.Date.AddDays(-lag);
        }
    }
}
