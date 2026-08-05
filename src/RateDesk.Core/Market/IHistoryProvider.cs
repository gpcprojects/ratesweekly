using System;
using System.Collections.Generic;

namespace RateDesk.Core.Market
{
    public readonly record struct HistPoint(DateTime Date, double Value);

    /// <summary>Daily historical series provider (implemented by the Bloomberg layer via BDH).
    /// Core depends only on this abstraction so it stays free of BLPAPI.</summary>
    public interface IHistoryProvider
    {
        /// <summary>Ascending daily PX_LAST for a full Bloomberg ticker over the last N calendar days.
        /// Returns an empty list on failure. Implementations should cache per session.</summary>
        IReadOnlyList<HistPoint> GetDaily(string ticker, int lookbackDays);

        /// <summary>Warm the cache for many tickers in as few round-trips as the backend allows.
        /// Optional — callers still get correct (slower) behaviour from per-ticker GetDaily.</summary>
        void Prefetch(IEnumerable<string> tickers, int lookbackDays) { }
    }
}
