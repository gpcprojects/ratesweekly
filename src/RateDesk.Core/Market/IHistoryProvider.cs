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

        /// <summary>Daily FIXED-LONDON-TIME values built from intraday bars (the desk convention:
        /// 4:30pm London snaps), ascending, at most lookbackDays back — intraday depth is limited,
        /// so this complements GetDaily rather than replacing it. Empty when the backend has no
        /// intraday data (tests, thin tickers): callers must degrade to closes.</summary>
        IReadOnlyList<HistPoint> GetLondonSnaps(string ticker, int lookbackDays, TimeSpan londonTimeOfDay)
            => Array.Empty<HistPoint>();

        /// <summary>The EFFECTIVE date this ticker itself published on that day — Bloomberg's own
        /// record of which contract the rolling generic pointed at, rather than an inference from
        /// a calendar. Null when the day was never recorded (older history, a backend that keeps
        /// no such record), and callers then fall back to the boundary derivation.
        ///
        /// <para>Added 2026-08-27 for the announced-but-not-yet-effective re-base, which has to
        /// find the just-decided period's own mark on a day when it can prove that rung WAS that
        /// contract. Reading a close from before the decision cannot contain the surprise.</para></summary>
        DateTime? EffectiveOn(string ticker, DateTime day) => null;
    }
}
