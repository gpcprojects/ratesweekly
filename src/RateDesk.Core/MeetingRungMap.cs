namespace RateDesk.Core
{
    /// <summary>THE one derivation of "which rolling generic pointed at this contract on that
    /// date" (fresh-eyes review 2026-08-26). Five call sites — the stitcher, the daily history
    /// books, the manual-override ingest, the dashboard strips and the movers scan — previously
    /// each built their own boundary list over four different date sets, and the copies drifted:
    /// the SEK period-start fix landed in three of five, and the settled-announcement boundary
    /// (ECB 23-Jul-26) was missing everywhere the future-only decisionDates list was read raw.
    ///
    /// Rules, in one place:
    ///   · candidates = the period grid (Dates) + settled history (PastDates) + any caller-
    ///     supplied ticker-derived dates (the live run's own rows — config grids drift, BOJ's
    ///     2027 entries sat 8-11 days late);
    ///   · decision-renumbering families (every family but SKSF) also take every documented
    ///     ANNOUNCEMENT — recorded decisionDates plus start−lag derivations for stable-lag
    ///     families (MeetingCalendar.AnnouncementDates); the feed re-points at the announcement,
    ///     verified off store closes (EESF jumped between the 24-Jul and 27-Jul closes around
    ///     the 23-Jul-26 ECB decision, six days before the period start);
    ///   · start-renumbering families (rollsAtPeriodStart: SKSF, probed 2026-08-25) never take
    ///     announcements — boundaries stay ON the start dates;
    ///   · 14-day clustering keeping the EARLIEST of each cluster (announcement beats start —
    ///     no two real meetings of one bank sit within 14 days, a tested config invariant);
    ///   · a lookup landing ON a boundary maps from the day before (the families re-point
    ///     NON-uniformly intraday, so a boundary-day close is unattributable — the stitcher's
    ///     16:30-probe rule);
    ///   · zero boundaries between the day and the contract means the contract's own period has
    ///     already started — there is NO rung for it, and the answer is null, never rung 1.</summary>
    public sealed class MeetingRungMap
    {
        public const int ClusterDays = 14;

        public IReadOnlyList<DateTime> Boundaries { get; }
        private readonly HashSet<DateTime> _set;

        public MeetingRungMap(MeetingScheduleDef sched, IEnumerable<DateTime>? tickerDates = null)
        {
            var cand = sched.Dates.AsEnumerable()
                .Concat(sched.PastDates)
                .Concat(tickerDates ?? Enumerable.Empty<DateTime>());
            if (!sched.RollsAtPeriodStart)
                cand = cand.Concat(MeetingCalendar.AnnouncementDates(sched));
            var clustered = new List<DateTime>();
            foreach (var d in cand.Select(d => d.Date).Distinct().OrderBy(d => d))
                if (clustered.Count == 0 || (d - clustered[^1]).TotalDays > ClusterDays)
                    clustered.Add(d);
            Boundaries = clustered;
            _set = clustered.ToHashSet();
        }

        public bool IsBoundary(DateTime day) => _set.Contains(day.Date);

        /// <summary>1-based generic index that carried <paramref name="contract"/> on
        /// <paramref name="onDay"/>; null when the day sits inside the contract's own period
        /// (no rung exists) or past the probe cap (family exhausted).</summary>
        public int? RungFor(DateTime contract, DateTime onDay, int maxIndex = 13)
        {
            var d = onDay.Date;
            if (_set.Contains(d)) d = d.AddDays(-1);
            int n = 0;
            foreach (var b in Boundaries)
                if (b > d && b <= contract.Date) n++;
            return n >= 1 && n <= maxIndex ? n : null;
        }
    }
}
