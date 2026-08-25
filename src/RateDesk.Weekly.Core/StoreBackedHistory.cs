using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>History reads for the email/daily builds, STORE-FIRST (desk 2026-08-25,
    /// "pull as much as possible from history"): the maintained store already holds every
    /// settled close the boards' lookbacks need, so serving them from disk both minimises
    /// Bloomberg API load and guarantees the changes are computed off the SAME marks every
    /// run and across every surface (email, dashboards, workbooks — one set of closes).
    ///
    /// Bloomberg is touched for exactly two things: a GAP-FILL when a ticker's stored history
    /// ends before the last business day (the fetch covers the gap plus a 5-day overlap so a
    /// restated print still self-heals, and what it fetches is upserted — the store keeps
    /// getting better), and the 16:30-London INTRADAY SNAPS, which pass straight through
    /// (they are the desk's meeting-change convention and the store keeps daily closes only).
    /// Prefetch is deliberately a no-op — the old behaviour re-pulled ~220 days for the whole
    /// universe on every run to compute lookbacks the store could already answer.</summary>
    public sealed class StoreBackedHistory : IHistoryProvider
    {
        private readonly HistoryStore _store;
        private readonly IHistoryProvider _live;
        private readonly Action<string>? _log;
        // one live attempt per ticker per session: a ticker that legitimately doesn't print
        // daily (gappy BGN quoting) stays "stale" after a gap-fill — without this it would
        // re-hit Bloomberg on every GetDaily call in the same run
        private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);

        public int ServedFromStore { get; private set; }
        public int GapFilled { get; private set; }

        public StoreBackedHistory(HistoryStore store, IHistoryProvider live, Action<string>? log = null)
        {
            _store = store;
            _live = live;
            _log = log;
        }

        public IReadOnlyList<HistPoint> GetDaily(string ticker, int lookbackDays)
        {
            var have = _store.GetDaily(ticker, lookbackDays);
            var last = have.Count > 0 ? have[^1].Date : (DateTime?)null;
            bool fresh = last is { } l && l >= PrevBd(DateTime.Today);
            lock (_attempted)
            {
                if (fresh || !_attempted.Add(ticker))
                {
                    ServedFromStore++;
                    return have;
                }
            }
            try
            {
                int gap = last is { } l2 ? (int)(DateTime.Today - l2).TotalDays + 5 : lookbackDays;
                var pulled = _live.GetDaily(ticker, Math.Min(lookbackDays, Math.Max(10, gap)));
                if (pulled.Count > 0)
                {
                    // settled closes only — today's in-progress PX_LAST never enters the store,
                    // and the boards read today from the live snapshot anyway
                    _store.UpsertDaily(ticker, pulled);
                    GapFilled++;
                    return _store.GetDaily(ticker, lookbackDays);
                }
            }
            catch (Exception ex) { _log?.Invoke($"  ! gap-fill {ticker}: {ex.Message}"); }
            return have;   // store-served even when the terminal is down — the offline failsafe
        }

        /// <summary>No-op by design: bulk warm-ups are exactly the API load this class removes.</summary>
        public void Prefetch(IEnumerable<string> tickers, int lookbackDays) { }

        /// <summary>Intraday 16:30-London snaps stay live — the store keeps settled closes only,
        /// and the snap convention (uniformly-old numbering at roll boundaries) needs the bars.</summary>
        public IReadOnlyList<HistPoint> GetLondonSnaps(string ticker, int lookbackDays, TimeSpan londonTimeOfDay)
            => _live.GetLondonSnaps(ticker, lookbackDays, londonTimeOfDay);

        public string Stats => $"history: {ServedFromStore} ticker(s) served from the store, " +
                               $"{GapFilled} gap-filled from Bloomberg";

        private static DateTime PrevBd(DateTime d)
        {
            var p = d.AddDays(-1);
            while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
            return p;
        }
    }
}
