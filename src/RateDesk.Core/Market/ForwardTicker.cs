using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Dates;

namespace RateDesk.Core.Market
{
    /// <summary>Which family of Bloomberg forward-swap ticker a curve id belongs to. The two families
    /// are not interchangeable and one is not a superset of the other, so the choice is per curve and
    /// lives in config beside the id.</summary>
    public enum FwdTickerStyle
    {
        /// <summary>FWCM curve surface: "S0490FS 10Y2Y BLC Curncy". Starts are quoted on a SPARSE annual
        /// grid (plus months inside ~2y), every point is always populated because the surface is derived,
        /// and quotes are one-way (bid = ask).</summary>
        Fwcm,

        /// <summary>Forward-swap securities keyed by a {start}{tenor} year pair, two digits each:
        /// "EUSA0505 BLC Curncy" = 5Y5Y, "NDFS1502 BLC Curncy" = 15Y2Y. Whole years only, no month
        /// starts. Verified field order against the terminal's own Forward Curve Matrix for NZD
        /// (2026-07-29): NDFS0110 = tenor 10Y x forward 1Y, NDFS1502 = tenor 2Y x forward 15Y — so the
        /// leading pair is the START, matching EUSA, even though Bloomberg NAMES them "15YX2Y".
        ///
        /// <para>Coverage varies by currency and is NOT implied by the pattern: EUSA quotes every year
        /// combination, NDFS only the <see cref="Starts"/> grid (NDFS1103 does not exist). The BLC
        /// qualifier matters — the plain and BGN forms of NDFS resolve by name with no price and no
        /// history at all.</para></summary>
        YearPair,
    }

    /// <summary>Builds forward-swap tickers for either family, so the rest of the codebase never
    /// hard-codes one shape.
    ///
    /// <para>Why both exist: a currency's forward reference must sit on the SAME index basis as the curve
    /// we bootstrap. EUR IRS is built from EUSA composites (Bloomberg names them "EUR SWAP ANN (VS 6M)"),
    /// so its forwards are the EUSA year-pair securities. Cross-checking it against FWCM S0201 — the
    /// vs-3M-Euribor surface — booked the 3s6s basis as our own error: +12.2bp at 1y1y tilting to -6.9bp
    /// at 20y10y in auditfwd, while the same points against EUSA come in under 1bp
    /// (measured 2026-07-29). ESTR OIS on S0514 was always correct and is unaffected.</para></summary>
    public static class ForwardTicker
    {
        /// <summary>Quoted start grid in whole years, used when a start has to be bracketed. ONE grid for
        /// both families: it is exactly what FWCM quotes, and the NDFS year-pair grid was probed to be
        /// the same set (2026-07-29). EUSA quotes every year on top of this, but that only ever makes
        /// <see cref="Exact"/> succeed — a bracket aimed at these points is quoted for EUSA too, so the
        /// sparse grid is the safe common subset rather than a limitation.</summary>
        private static readonly int[] Starts = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 15, 20, 25, 30 };

        public static int[] StartGrid(FwdTickerStyle style) => Starts;

        /// <summary>FWCM period code: "10Y" for whole years, else "117M". Null when the period is not a
        /// whole number of months.</summary>
        public static string? Code(Period p)
        {
            double months = TenorUtil.ApproxMonths(p);
            if (Math.Abs(months - Math.Round(months)) > 1e-6) return null;
            int m = (int)Math.Round(months);
            if (m <= 0) return null;
            return m % 12 == 0 ? $"{m / 12}Y" : $"{m}M";
        }

        private static int? WholeYears(Period p)
        {
            double months = TenorUtil.ApproxMonths(p);
            int m = (int)Math.Round(months);
            if (Math.Abs(months - m) > 1e-6 || m <= 0 || m % 12 != 0) return null;
            int y = m / 12;
            return y is >= 1 and <= 99 ? y : null;
        }

        /// <summary>Ticker for an exact start/tenor, or null when this family cannot express the point —
        /// a month start under <see cref="FwdTickerStyle.YearPair"/>, for instance. Callers treat null as
        /// "bracket it instead", never as an error.</summary>
        public static string? Exact(string id, FwdTickerStyle style, Period start, Period tenor)
        {
            if (id.Length == 0) return null;
            if (style == FwdTickerStyle.YearPair)
            {
                if (WholeYears(start) is not int sy || WholeYears(tenor) is not int ty) return null;
                return $"{id}{sy:00}{ty:00} BLC Curncy";
            }
            var s = Code(start); var t = Code(tenor);
            return s == null || t == null ? null : $"{id}FS {s}{t} BLC Curncy";
        }

        /// <summary>Ticker at a whole-year start with the given tenor — the bracketing form. Null when the
        /// tenor itself cannot be expressed.</summary>
        public static string? AtStartYears(string id, FwdTickerStyle style, int startYears, Period tenor)
        {
            if (id.Length == 0 || startYears < 1) return null;
            if (style == FwdTickerStyle.YearPair)
            {
                if (WholeYears(tenor) is not int ty || startYears > 99) return null;
                return $"{id}{startYears:00}{ty:00} BLC Curncy";
            }
            var t = Code(tenor);
            return t == null ? null : $"{id}FS {startYears}Y{t} BLC Curncy";
        }

        /// <summary>Human-readable point label for notes and the FWCM column ("S0201FS 5Y5Y", "EUSA 5Y5Y").</summary>
        public static string Label(string id, FwdTickerStyle style, Period start, Period tenor)
        {
            var s = Code(start) ?? "?"; var t = Code(tenor) ?? "?";
            return style == FwdTickerStyle.YearPair ? $"{id} {s}{t}" : $"{id}FS {s}{t}";
        }

        /// <summary>The two grid starts bracketing <paramref name="startYears"/>, or (0,0) when it sits
        /// outside the grid. Exact grid hits return (0,0) too — the caller should have used
        /// <see cref="Exact"/>.</summary>
        public static (int lo, int hi) Bracket(FwdTickerStyle style, double startYears)
        {
            var grid = StartGrid(style);
            int lo = grid.LastOrDefault(g => g < startYears - 1e-9);
            int hi = grid.FirstOrDefault(g => g > startYears + 1e-9);
            return (lo, hi);
        }

        /// <summary>Parses the config string. Unknown/blank means FWCM, which is what every currency used
        /// before the year-pair family was added.</summary>
        public static FwdTickerStyle Parse(string? s) =>
            s != null && s.Trim().Equals("yearPair", StringComparison.OrdinalIgnoreCase)
                ? FwdTickerStyle.YearPair : FwdTickerStyle.Fwcm;
    }
}
