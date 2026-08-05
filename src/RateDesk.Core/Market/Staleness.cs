using System;
using System.Collections.Generic;
using System.Linq;

namespace RateDesk.Core.Market
{
    /// <summary>One quote flagged as not price discovery.</summary>
    public sealed class StaleQuote
    {
        public string Ticker { get; init; } = "";
        public string Label { get; init; } = "";
        /// <summary>Minutes since the quote's own LAST_UPDATE, when Bloomberg published one.</summary>
        public double? AgeMinutes { get; init; }
        public double? WidthBp { get; init; }
        /// <summary>Why it was flagged, for the tooltip — always a measured number, never a guess.</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>Flags quotes that are being re-stamped rather than traded.
    ///
    /// <para>Measured on the live terminal 2026-07-28 ~09:24 London: GBP 5y (BPSWS5) last updated
    /// 09:23:16 on a 0.4bp spread, while NZD 5y (NDSWAP5) last updated 07:59 on a 3.8bp spread. Every
    /// NZD bank contributor — BARX, UBSW, RBCX, MSTX and all CMP* composites — was frozen at 06:28,
    /// Wellington's close, all showing an identical 4.0525. BGN was simply re-stamping a widened
    /// indicative spread around a frozen mid: a re-quote, not price discovery. There is no alternative
    /// live NZD contributor to switch to.</para>
    ///
    /// <para>Deliberately NOT special-cased to NZD, and deliberately NOT a hardcoded per-currency
    /// threshold table. Width is judged RELATIVE to the median width of that currency's own live curve,
    /// so each market self-calibrates from today's data: a 2bp GBP spread is alarming while a 2bp NZD
    /// spread is its best case, and a table of guessed constants would rot the moment liquidity moved.
    /// Age is absolute, because a quote that has not moved for hours is stale in any currency — but the
    /// threshold lives in config so a market with a genuinely slow session can be tuned without a code
    /// change.</para></summary>
    public static class Staleness
    {
        /// <summary>A width this many times the currency's own median is anomalous.</summary>
        public const double WidthMultiple = 2.5;
        /// <summary>A width-only flag needs a genuinely dealer-wide market, not merely a wide multiple.
        ///
        /// <para>Calibrated against the live curves on 2026-07-28 rather than guessed. At a 1bp floor
        /// this flagged USD 40/45/50Y (1.3–1.9bp against a 0.3bp median) and GBP 1M (1.1bp against
        /// 0.4bp) — all perfectly live markets that are simply wider at the tail and the front of a
        /// curve, since a curve's own median is a poor yardstick across tenors. Those are false
        /// positives, and a box that cries wolf on USD every time is a box nobody reads. At 2bp both go
        /// quiet while a genuine 4bp-quote-on-a-0.4bp-curve still flags.</para>
        ///
        /// <para>Age remains the load-bearing signal — it is what actually caught NZD (7–8h since last
        /// update, i.e. Wellington's close). Width is the secondary one, for a market that widens out
        /// without going quiet.</para></summary>
        public const double MinWidthBp = 2.0;

        /// <param name="quotes">Label + quote for every pillar behind the result being shown.</param>
        /// <param name="staleMinutes">Age at which a quote is called stale; 0 disables the age test.</param>
        public static List<StaleQuote> Assess(
            IReadOnlyList<(string Ticker, string Label, QuoteData? Q)> quotes, double staleMinutes)
        {
            var flagged = new List<StaleQuote>();
            var widths = quotes
                .Select(x => Width(x.Q))
                .Where(w => w.HasValue)
                .Select(w => w!.Value)
                .OrderBy(w => w)
                .ToList();
            double? median = widths.Count >= 3 ? widths[widths.Count / 2] : null;

            foreach (var (ticker, label, q) in quotes)
            {
                if (q == null) continue;
                double? width = Width(q);
                var reasons = new List<string>();

                if (staleMinutes > 0 && q.AgeMinutes is double age && age >= staleMinutes)
                    reasons.Add($"last update {Describe(age)} ago");

                if (median is double med && width is double w
                    && w >= MinWidthBp && w >= med * WidthMultiple)
                    reasons.Add($"{w:0.0}bp wide vs {med:0.0}bp median on this curve");

                if (reasons.Count > 0)
                    flagged.Add(new StaleQuote
                    {
                        Ticker = ticker, Label = label, AgeMinutes = q.AgeMinutes,
                        WidthBp = width, Reason = string.Join("; ", reasons),
                    });
            }
            return flagged;
        }

        private static double? Width(QuoteData? q) =>
            q?.Bid is double b && q.Ask is double a && a > b ? (a - b) * 100.0 : null;

        private static string Describe(double minutes) =>
            minutes >= 120 ? $"{minutes / 60.0:0.#}h" : $"{minutes:0}m";
    }
}
