using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Market;

namespace RateDesk.Core.Pricing
{
    /// <summary>Past index fixings, so a SEASONED trade can be priced.
    ///
    /// <para>A swap whose effective date has passed has already accrued: QLNet needs the actual published
    /// overnight fixing for every elapsed business day or it refuses to price. Without them the pricer
    /// rejected the trade outright, which stranded a blotter row the moment its start date rolled into the
    /// past — it froze on whatever dv01 was last stamped and marked itself off that stale number.</para>
    ///
    /// <para>Fixings come from the same Bloomberg history the charts use (BDH on the config's
    /// <c>onFixingTicker</c>), held in QLNet's global <c>IndexManager</c> which is keyed by index name, so
    /// every later instance of the same index sees them. That global is safe here for the same reason
    /// <c>Settings.evaluationDate</c> is: all pricing runs under PricingService's single gate.</para>
    ///
    /// <para><b>The publication lag is not an error.</b> Every overnight index prints T+1 — SOFR for the
    /// 29th publishes on the 30th around 08:00 ET — so a trade that started yesterday is ALWAYS missing its
    /// most recent fixing, and before the New York morning it is missing two. Those days are filled by
    /// carrying the last published rate forward, which is what the market itself assumes intraday. One
    /// carried day on a 49-day trade moves the realised average by at most a few hundredths of a basis
    /// point. A gap wider than <see cref="MaxQuietGapDays"/> business days is a different thing — a stale or
    /// broken series rather than the normal lag — and is reported through
    /// <see cref="LastGapBusinessDays"/>.</para></summary>
    public static class Fixings
    {
        /// <summary>Set by PricingService whenever its History provider changes. Null = no history wired
        /// (tests, the CLI's non-history commands), in which case a seasoned trade fails loudly rather
        /// than silently mispricing.</summary>
        public static IHistoryProvider? Source { get; set; }

        /// <summary>Business days of carried-forward fixing that count as the normal T+1 publication lag.
        /// Two, not one, because before the New York print a European morning is legitimately two days
        /// behind (yesterday's fixing plus a Monday looking back at Friday).</summary>
        public const int MaxQuietGapDays = 2;

        /// <summary>Business days that had to be carried forward on the last load, per index name. Anything
        /// above <see cref="MaxQuietGapDays"/> means the series itself is stale, not merely lagged.</summary>
        public static readonly Dictionary<string, int> LastGapBusinessDays = new();

        /// <summary>index name -> (day loaded, earliest date covered).</summary>
        private static readonly Dictionary<string, (DateTime Day, DateTime From)> _loaded = new();

        /// <summary>Publishes fixings covering <paramref name="from"/>..today for this index. Best-effort: a
        /// failure must not break a spot-starting trade that needs no fixings at all.</summary>
        public static void Ensure(InterestRateIndex idx, string fixingTicker, DateTime from)
        {
            if (Source == null || string.IsNullOrWhiteSpace(fixingTicker)) return;
            try
            {
                var key = idx.name();
                var today = DateTime.Today;
                if (_loaded.TryGetValue(key, out var got) && got.Day == today && got.From <= from) return;

                // +5 calendar days of slack so the first accrual day is inside the window even when it fell
                // on a weekend or a holiday
                int days = Math.Max(10, (int)Math.Ceiling((today - from).TotalDays) + 5);
                var hist = Source.GetDaily(fixingTicker, days);
                if (hist.Count == 0) return;

                var cal = idx.fixingCalendar();
                var published = new Dictionary<Date, double>();
                Date? first = null;
                foreach (var p in hist.OrderBy(p => p.Date))
                {
                    var d = new Date(p.Date.Day, (Month)p.Date.Month, p.Date.Year);
                    published[d] = p.Value / 100.0;           // Bloomberg publishes percent
                    first ??= d;
                }
                if (first == null) return;

                // Walk EVERY day the index's own calendar calls a fixing day, from the first print up to
                // and including yesterday, and carry the last known rate into any hole. Two different holes
                // occur and both must be filled or QLNet refuses the trade:
                //
                //   the publication lag  - every ON index prints T+1, so the most recent day (two, before
                //                          the New York morning) is simply not out yet;
                //   a calendar mismatch  - Bloomberg does not publish when the rate's own fixing calendar
                //                          is shut but QLNet's currency calendar disagrees. Good Friday is
                //                          the one that bites: no SOFR print for 03-Apr-26, yet the calendar
                //                          counts it a business day.
                //
                // Today is deliberately left alone: QLNet projects it off the curve unless
                // Settings.enforcesTodaysHistoricFixings says otherwise, and a projected today beats a
                // guessed one.
                var yesterday = cal.advance(
                    cal.adjust(new Date(today.Day, (Month)today.Month, today.Year), BusinessDayConvention.Preceding),
                    -1, TimeUnit.Days);
                var dates = new List<Date>();
                var rates = new List<double>();
                double? lastRate = null;
                int carried = 0;
                for (var d = first; d <= yesterday; d = cal.advance(d, 1, TimeUnit.Days))
                {
                    if (published.TryGetValue(d, out var v)) lastRate = v;
                    else if (lastRate != null) carried++;
                    if (lastRate is not double r) continue;
                    dates.Add(d);
                    rates.Add(r);
                }
                if (dates.Count == 0) return;

                idx.addFixings(dates, rates, true);           // forceOverwrite: yesterday's load may hold a provisional print
                _loaded[key] = (today, from);
                LastGapBusinessDays[key] = carried;
            }
            catch { /* seasoned pricing is a bonus — never break a live trade over a history fetch */ }
        }

        /// <summary>Business days carried forward for this index, or 0. For a caller that wants to warn.</summary>
        public static int GapFor(string indexName) =>
            LastGapBusinessDays.TryGetValue(indexName, out var n) ? n : 0;

        /// <summary>Forgets what has been loaded so the next Ensure refetches. For tests, and for a manual
        /// refresh after a fixing restatement.</summary>
        public static void Reset() { _loaded.Clear(); LastGapBusinessDays.Clear(); }
    }
}
