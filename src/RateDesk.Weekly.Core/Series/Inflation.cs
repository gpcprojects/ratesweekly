using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Weekly.Core.Render;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>Zero-coupon inflation: the quoted par ladder, the quoted forward ladder, and the
    /// published index fixings that both settle against.
    ///
    /// FIXING LAG. A ZC inflation swap does not reference today's index — that print does not exist
    /// yet. It references the index some months back, and that reference month is what determines
    /// which fixing is the swap's FIRST (base) fixing. The lags below are the standard market
    /// conventions for the three markets we quote:
    ///   USD CPI (CPURNSA)   3 months
    ///   GBP RPI (UKRPI)     2 months   — note gilt linkers moved to 3m in 2005; the RPI SWAP
    ///                                    market stayed on 2m, which is the convention that matters here
    ///   EUR HICPxT (CPTFEMU) 3 months
    /// ⚠ These are conventions, not config — CONFIRM with the desk before anyone trades off the
    /// base-fixing label (DESIGN.md §10 backlog). Everything else on the page is quoted data and
    /// does not depend on the lag; only the "base fixing" marker does.</summary>
    public static class Inflation
    {
        public static int LagMonths(string ccy) => ccy.ToUpperInvariant() switch
        {
            "GBP" => 2,
            _ => 3,     // USD CPI, EUR HICPxT
        };

        public static string LagNote(string ccy, DateTime asOf)
        {
            int lag = LagMonths(ccy);
            var baseMonth = new DateTime(asOf.Year, asOf.Month, 1).AddMonths(-lag);
            return $"{lag}-month lag — a swap starting now bases off the {baseMonth:MMMM yyyy} print";
        }

        /// <summary>The published index prints, oldest to newest, with the base fixing for a swap
        /// starting today marked. Monthly data, so a 45-day store holds only one or two points —
        /// this section deepens naturally as the history does.</summary>
        public static List<LadderPoint> Fixings(
            Ladder lad, string ccy, HistoryStore store, DateTime asOf, int months = 24)
        {
            var h = store.GetDaily(lad.FixingTicker, months * 31 + 40);
            if (h.Count == 0) return new();

            int lag = LagMonths(ccy);
            var baseMonth = new DateTime(asOf.Year, asOf.Month, 1).AddMonths(-lag);

            // one row per print (the index only moves when a new figure publishes)
            var rows = new List<LadderPoint>();
            double? prev = null;
            foreach (var p in h)
            {
                if (prev.HasValue && Math.Abs(p.Value - prev.Value) < 1e-9) continue;
                bool isBase = p.Date.Year == baseMonth.Year && p.Date.Month == baseMonth.Month;
                rows.Add(new LadderPoint(p.Date.ToString("MMM yy") + (isBase ? "*" : ""), p.Value, null, null));
                prev = p.Value;
            }
            return rows;
        }

        /// <summary>Quoted ZC par ladder, 1y-30y, at asOf / -1w / -1m.</summary>
        public static List<LadderPoint> ParCurve(Ladder lad, HistoryStore store, DateTime asOf)
        {
            var rows = new List<LadderPoint>();
            foreach (var p in lad.Pillars.Where(p => p.Enabled))
            {
                var tk = ConfigStore.ResolveTicker(p.Ticker, "");
                var now = store.ValueAsOf(tk, asOf);
                if (now is null) continue;
                rows.Add(new LadderPoint(p.Tenor, now,
                    store.ValueAsOf(tk, asOf.AddDays(-WeeklyCurves.WeekDays)),
                    store.ValueAsOf(tk, WeeklyCurves.MonthAgo(asOf))));
            }
            return rows;
        }

        /// <summary>Inflation forward code: US / EU / UK (not the currency code).</summary>
        public static string? FwdCode(string ccy) => ccy.ToUpperInvariant() switch
        {
            "USD" => "US",
            "EUR" => "EU",
            "GBP" => "UK",
            _ => null,
        };

        /// <summary>The quoted inflation forward ladder, `.{CC}{start}{tenor}IN G Index` — desk
        /// convention (2026-08-05), e.g. `.US1010IN G Index` is 10y10y and `.US22IN G Index` is
        /// 2y2y. Start and tenor are concatenated as bare integers.
        ///
        /// The grid below is the intersection of what the desk reads and what actually resolves:
        /// every point was probed by NAME against the terminal, and all three markets carry the
        /// same set. It deliberately mirrors the NOMINAL forward ladder wherever the family quotes
        /// it (1y1y, 2y1y, 3y1y, 4y1y, 5y2y, 10y2y, 15y5y, 20y10y) so the two panels can be read
        /// against each other, plus the market's own benchmarks (2y2y, 5y5y, 10y10y). Probed and
        /// NOT available in any of the three: 7y3y, 12y3y, 30y20y, 3y3y, 5y10y, 30y10y.
        ///
        /// These are calculated indices: price-only, no bid/ask and no maturity.</summary>
        public static readonly (int Start, int Tenor)[] FwdGrid =
        {
            (1, 1), (2, 1), (2, 2), (3, 1), (4, 1), (5, 2), (5, 5),
            (10, 2), (10, 10), (15, 5), (20, 10),
        };

        public static string Ticker(string code, int start, int tenor) => $".{code}{start}{tenor}IN G Index";

        public static IEnumerable<string> ForwardTickers(string ccy)
        {
            if (FwdCode(ccy) is not { } code) yield break;
            foreach (var (s, t) in FwdGrid) yield return Ticker(code, s, t);
        }

        public static List<LadderPoint> Forwards(string ccy, HistoryStore store, DateTime asOf)
        {
            if (FwdCode(ccy) is not { } code) return new();
            var rows = new List<LadderPoint>();
            foreach (var (s, t) in FwdGrid)
            {
                var tk = Ticker(code, s, t);
                var now = store.ValueAsOf(tk, asOf);
                if (now is null) continue;
                rows.Add(new LadderPoint($"{s}y{t}y", now,
                    store.ValueAsOf(tk, asOf.AddDays(-WeeklyCurves.WeekDays)),
                    store.ValueAsOf(tk, WeeklyCurves.MonthAgo(asOf))));
            }
            return rows;
        }
    }
}
