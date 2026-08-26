using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RateDesk.Core.Market
{
    public sealed class QuoteData
    {
        public double? Bid { get; set; }
        public double? Ask { get; set; }
        public double? Last { get; set; }
        /// <summary>Previous business day's close (PX_CLOSE_1D) — for change-on-day.</summary>
        public double? PrevClose { get; set; }
        /// <summary>Instrument maturity (meeting-dated OIS: the NEXT meeting date).</summary>
        public DateTime? Maturity { get; set; }
        /// <summary>Instrument effective date (SW_EFF_DT). For a meeting-dated OIS this is the START
        /// of the period it quotes. Every family except BOJ starts one period exactly where the
        /// previous one matured, so the two are interchangeable there — the BOJ's periods begin at
        /// the settlement date a business day or more AFTER the decision its rate responds to
        /// (2026-10-30 decision, 2026-11-02 start), which is why the start has to be read and not
        /// inferred from the neighbour's maturity.</summary>
        public DateTime? Effective { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        /// <summary>Minutes since the QUOTE's own LAST_UPDATE as Bloomberg reports it — not since we
        /// received it. A frozen market keeps re-stamping, so our receive time says nothing about
        /// whether the price is still being discovered. Null when the field isn't published.</summary>
        public double? AgeMinutes { get; set; }

        /// <summary>Mid in PERCENT (as published, e.g. 4.0362). Bid/ask mid preferred, else last, else
        /// single side. Some indices publish junk 0/0 bid-ask alongside a real last (NOWA) — a zero
        /// two-sided market with a non-zero last is treated as sides-missing.</summary>
        public double? Mid =>
            Bid.HasValue && Ask.HasValue && !(Bid == 0 && Ask == 0 && Last is not (null or 0))
                ? (Bid.Value + Ask.Value) / 2.0
                : Last ?? Bid ?? Ask;

        /// <summary>Change on day in bp (mid vs previous close).</summary>
        public double? CoDBp => Mid.HasValue && PrevClose.HasValue ? (Mid.Value - PrevClose.Value) * 100.0 : null;
    }

    /// <summary>Thread-safe store of live/snapshot quotes keyed by full Bloomberg ticker.</summary>
    public sealed class RatesSnapshot
    {
        private readonly ConcurrentDictionary<string, QuoteData> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private long _version;

        public long Version => System.Threading.Interlocked.Read(ref _version);

        public event Action<string>? QuoteChanged;

        public void Update(string ticker, double? bid, double? ask, double? last, DateTime? tsUtc = null)
        {
            var q = _quotes.GetOrAdd(ticker, _ => new QuoteData());
            lock (q)
            {
                if (bid.HasValue) q.Bid = bid;
                if (ask.HasValue) q.Ask = ask;
                if (last.HasValue) q.Last = last;
                q.UpdatedUtc = tsUtc ?? DateTime.UtcNow;
            }
            System.Threading.Interlocked.Increment(ref _version);
            QuoteChanged?.Invoke(ticker);
        }

        public void SetPrevClose(string ticker, double prevClose)
        {
            var q = _quotes.GetOrAdd(ticker, _ => new QuoteData());
            lock (q) q.PrevClose = prevClose;
        }

        public void SetMaturity(string ticker, DateTime maturity)
        {
            var q = _quotes.GetOrAdd(ticker, _ => new QuoteData());
            lock (q) q.Maturity = maturity;
        }

        public void SetEffective(string ticker, DateTime effective)
        {
            var q = _quotes.GetOrAdd(ticker, _ => new QuoteData());
            lock (q) q.Effective = effective;
        }

        /// <summary>Age of the quote per Bloomberg's own LAST_UPDATE, in minutes.</summary>
        public void SetAgeMinutes(string ticker, double ageMinutes)
        {
            var q = _quotes.GetOrAdd(ticker, _ => new QuoteData());
            lock (q) q.AgeMinutes = ageMinutes;
        }

        public bool TryGetMid(string ticker, out double midPct)
        {
            midPct = double.NaN;
            if (_quotes.TryGetValue(ticker, out var q) && q.Mid.HasValue) { midPct = q.Mid.Value; return true; }
            return false;
        }

        public QuoteData? Get(string ticker) => _quotes.TryGetValue(ticker, out var q) ? q : null;

        /// <summary>The AGE BASELINE for this snapshot: the 10th percentile of recorded quote
        /// ages. Raw ages are (machine clock − terminal-basis stamp) and carry one systematic
        /// offset per machine (desk 2026-08-26: a GTB/UTC+3 Windows clock beside a London
        /// terminal read every liquid front as "120m quiet") — in a 1000+-ticker snapshot the
        /// freshest cohort ticked seconds before the request, so the low percentile IS the
        /// offset. A PERCENTILE, not the minimum: one instrument with a malformed or
        /// future-dated stamp must not poison the floor. Staleness is always judged as
        /// (age − baseline), never against the raw number.</summary>
        public double? BaselineAgeMinutes()
        {
            var ages = new List<double>();
            foreach (var q in _quotes.Values)
                if (q.AgeMinutes is double a) ages.Add(a);
            if (ages.Count == 0) return null;
            ages.Sort();
            return ages[Math.Min(ages.Count - 1, ages.Count / 10)];
        }

        public IReadOnlyDictionary<string, QuoteData> All => _quotes;
    }
}
