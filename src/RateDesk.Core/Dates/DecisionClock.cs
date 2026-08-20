namespace RateDesk.Core.Dates
{
    /// <summary>When a central-bank decision counts as ANNOUNCED — the time-gated failsafe behind
    /// decision-day front rolls (desk 2026-08-20). The meeting generics re-point at the decision,
    /// but Bloomberg does it NON-uniformly through the announcement day (probed on the 30-Jul-26
    /// MPC at 16:30 London: #1 had rolled, #2-#4 had not), and a snapshot taken minutes after the
    /// statement can still carry entirely old-numbered maturities. The boards must not wait for
    /// the feed: config carries each bank's announcement time on the London clock
    /// (meetings.json decisionTimeLondon), and once that time passes the just-decided period is
    /// no longer market pricing of a decision — it rolls off the front regardless of what the
    /// tickers say.</summary>
    public static class DecisionClock
    {
        /// <summary>The settlement-lag bound used everywhere a decision is paired with the period
        /// it decides (ECB ~6d, BOJ up to 6d, RBA 1d).</summary>
        public const int PairToleranceDays = 10;

        /// <summary>Now on the London clock. Falls back to UTC when the Windows tz id is missing —
        /// UTC never runs AHEAD of London, so the fallback can only roll late (≤1h in summer),
        /// never early.</summary>
        public static DateTime LondonNow()
        {
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"));
            }
            catch { return DateTime.UtcNow; }
        }

        /// <summary>True once the statement is out: any day after the decision date, or on the
        /// decision date itself from the configured announcement time onward. An empty or
        /// unparseable time means the intraday state is unknowable — announced only from the NEXT
        /// day, which is the pre-2026-08-20 behaviour.</summary>
        public static bool Announced(DateTime decisionDate, string decisionTimeLondon, DateTime nowLondon)
        {
            if (nowLondon.Date > decisionDate.Date) return true;
            if (nowLondon.Date < decisionDate.Date) return false;
            return TimeSpan.TryParse(decisionTimeLondon, out var t) && nowLondon.TimeOfDay >= t;
        }

        /// <summary>The decision that BELONGS to the period starting <paramref name="periodStart"/>:
        /// the latest one at or before the start, within the settlement lag. Null when the calendar
        /// carries none — the caller degrades honestly (no time-gated roll, ticker re-points still
        /// govern).</summary>
        public static DateTime? DecisionFor(IEnumerable<DateTime> decisionDates, DateTime periodStart)
        {
            DateTime? best = null;
            foreach (var d in decisionDates)
                if (d.Date <= periodStart.Date && (periodStart.Date - d.Date).TotalDays <= PairToleranceDays
                    && (best is null || d.Date > best.Value))
                    best = d.Date;
            return best;
        }
    }
}
