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
        private readonly HashSet<DateTime> _mixed;
        private readonly Func<int, DateTime, DateTime?>? _recorded;

        /// <param name="recordedEffective">Bloomberg's OWN record of what rung n pointed at on a
        /// given day (the store's maturity table, via IHistoryProvider.EffectiveOn). When it
        /// answers, it WINS: a recorded field is evidence, a boundary count is inference. This is
        /// what makes the map survive a calendar that gained a meeting after the fact — an
        /// unscheduled decision re-numbers every historical day under a derivation, but it cannot
        /// change what Bloomberg published at the time (fix 2026-08-27, scenario 21).</param>
        public MeetingRungMap(MeetingScheduleDef sched, IEnumerable<DateTime>? tickerDates = null,
            Func<int, DateTime, DateTime?>? recordedEffective = null)
        {
            _recorded = recordedEffective;
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

            // MIXED-STATE WINDOW (desk 2026-08-26, the ECB +24.3bp Δ1m): renumbering is not
            // instantaneous at the announcement — the EESF composite re-pointed between the
            // 24-Jul and 27-Jul CLOSES (announcement 23-Jul, start 29-Jul), and the GPSF probe
            // on the 30-Jul MPC found rungs 2+ still old-numbered at the close. So every day
            // STRICTLY BETWEEN a boundary (the announcement) and its period start is
            // per-rung-ambiguous: no close or snap from those days may source a stitched value
            // or an anchor — the walk-back steps to the last clean pre-announcement day.
            // Start-renumbering families (SKSF) renumber wholly at the start (probed
            // 2026-08-25) and have no such window.
            _mixed = new HashSet<DateTime>();
            // AN UNSTABLE LAG IS AN UNKNOWN, NOT A ZERO (fix 2026-08-27, scenario 56). When the
            // decision→start gap varies, MeetingCalendar refuses to derive a past announcement —
            // correctly, it would be a guess — and the boundary falls back to the PERIOD START,
            // one to six days after the family actually renumbered. Nothing then masked the days
            // in between, so anchors landing there were read under the wrong numbering. The gap
            // is bounded even when the date is not: mask the whole window it must lie in, and
            // lookbacks step back to the last day that is certainly clean.
            if (!sched.RollsAtPeriodStart && !MeetingCalendar.LagIsStable(sched)
                && MeetingCalendar.LagRange(sched) is { } lag)
                foreach (var s0 in sched.Dates.Select(d => d.Date))
                    // from the EARLIEST the announcement could have been, right up to the day
                    // before the period starts: the renumber happened somewhere in there and
                    // nothing inside it can be attributed to a rung
                    for (int k = 1; k <= lag.Max; k++)
                        _mixed.Add(s0.AddDays(-k));
            if (!sched.RollsAtPeriodStart)
                foreach (var b in clustered)
                {
                    var start = sched.Dates.Select(d => d.Date)
                        .Where(s => s > b && (s - b).TotalDays <= ClusterDays)
                        .OrderBy(s => s).Cast<DateTime?>().FirstOrDefault();
                    if (start is not { } s) continue;
                    for (var d = b.AddDays(1); d < s; d = d.AddDays(1)) _mixed.Add(d);
                }
        }

        public bool IsBoundary(DateTime day) => _set.Contains(day.Date);

        /// <summary>True on a day between an announcement and its period start — the family's
        /// rungs renumber non-uniformly across those days, so nothing dated then may source a
        /// stitched value or an anchor.</summary>
        public bool IsMixedState(DateTime day) => _mixed.Contains(day.Date);

        /// <summary>1-based generic index that carried <paramref name="contract"/> on
        /// <paramref name="onDay"/>; null when the day sits inside the contract's own period
        /// (no rung exists) or past the probe cap (family exhausted).</summary>
        public int? RungFor(DateTime contract, DateTime onDay, int maxIndex = 13)
        {
            var d = onDay.Date;

            // EVIDENCE BEFORE INFERENCE, AND BEFORE THE BOUNDARY-DAY RULE. Ask the record for the
            // day actually asked about, FIRST. The step-back below exists because a boundary day's
            // numbering is ambiguous when all we have is a calendar - but a recorded SW_EFF_DT for
            // that very day settles it, and stepping back past the record throws the answer away.
            //
            // Live case that proved it (SKSF, 27-Aug-26): the family renumbers at the period
            // start, which was 26-Aug. The store holds SKSF1A on 26-Aug with eff 30-Sep - so the
            // 30-Sep contract was plainly on rung 1 that day. Stepping back to 25-Aug first found
            // no record, fell through to the boundary count, and answered rung 2 - so the front
            // row's change-on-day anchored on SKSF2A's 1.815 instead of SKSF1A's 1.715 and
            // published -10.6bp where the truth was -0.6bp.
            if (_recorded != null)
                for (int n0 = 1; n0 <= maxIndex; n0++)
                    if (_recorded(n0, d) is { } exact && exact.Date == contract.Date)
                        return n0;

            // ...and when the day is on the record but THIS contract's rung is not, the record
            // still fixes the family's numbering that day - so use it to calibrate, rather than
            // falling back to a rule that assumes we know nothing.
            //
            // Live case (SKSF, 27-Aug-26): only rungs 0-3 carry date fields; 4A, 5A and 6A are
            // price-only, so the contracts beyond the year-end turn are never on the record. The
            // rungs that ARE recorded show the family had already renumbered on 26-Aug, but the
            // step-back below assumed otherwise and counted one boundary too many for every
            // unrecorded contract - the 10-Feb row anchored on SKSF5A's 2.147 instead of SKSF4A's
            // 2.014 and published -8.7bp where the truth was +4.6bp.
            //
            // Every recorded rung must agree on the offset, or this is not evidence and we say so
            // by falling through.
            if (_recorded != null)
            {
                int? offset = null;
                bool consistent = true;
                for (int n0 = 1; n0 <= maxIndex && consistent; n0++)
                {
                    if (_recorded(n0, d) is not { } eff0) continue;
                    int naive = 0;
                    foreach (var b in Boundaries) if (b > d && b <= eff0.Date) naive++;
                    int off = n0 - naive;
                    if (offset is { } prev && prev != off) consistent = false;
                    else offset = off;
                }
                if (consistent && offset is { } o)
                {
                    int n1 = 0;
                    foreach (var b in Boundaries) if (b > d && b <= contract.Date) n1++;
                    n1 += o;
                    if (n1 >= 1 && n1 <= maxIndex) return n1;
                }
            }

            if (_set.Contains(d)) d = d.AddDays(-1);
            if (_recorded != null)
                for (int n0 = 1; n0 <= maxIndex; n0++)
                    if (_recorded(n0, d) is { } eff && eff.Date == contract.Date)
                        return n0;
            int n = 0;
            foreach (var b in Boundaries)
                if (b > d && b <= contract.Date) n++;
            return n >= 1 && n <= maxIndex ? n : null;
        }

        /// <summary>True when the store can speak for that day at all — used to tell "no record"
        /// apart from "recorded, and it says something else".</summary>
        public bool HasRecordFor(DateTime day, int maxIndex = 13)
        {
            if (_recorded == null) return false;
            for (int n = 1; n <= maxIndex; n++)
                if (_recorded(n, day.Date) is not null) return true;
            return false;
        }
    }
}
