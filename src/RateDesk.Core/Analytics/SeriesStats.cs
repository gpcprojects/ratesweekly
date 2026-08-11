using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Market;

namespace RateDesk.Core.Analytics
{
    /// <summary>Descriptive statistics on a daily time series (levels in %). Changes reported in bp.</summary>
    public sealed class SeriesStats
    {
        public int Count { get; init; }
        public DateTime? FirstDate { get; init; }
        public DateTime? LastDate { get; init; }
        public double Last { get; init; }

        /// <summary>Settable: Analyze overrides the history-derived value with an exact
        /// prev-close reprice when available — the history's last point can predate today
        /// (e.g. NZD during London hours), making a purely history-based 1d wrong.</summary>
        public double? Chg1d { get; set; }
        public double? Chg1w { get; init; }
        public double? Chg1m { get; init; }
        public double? Chg3m { get; init; }
        public double? Chg6m { get; init; }
        public double? Chg1y { get; init; }
        public double? ChgYtd { get; init; }

        public double? Min1y { get; init; }
        public double? Max1y { get; init; }
        public double? Percentile1y { get; init; }   // 0..100, rank of Last within 1y window
        public double? Range1yPct { get; init; }      // position in [min,max] as 0..100

        /// <summary>The series' own most recent close, in series units. Kept so a consumer can test
        /// the live level against the series it is about to be ranked in.</summary>
        public double? LastClose { get; init; }
        /// <summary>Report-unit scale this instance was computed with (100 for a %-level series, 1 for bp).</summary>
        public double ChangeScale { get; init; } = 100.0;
        /// <summary>Live level minus the series' last close, in REPORT units — one day's move when the
        /// two are on the same basis, and the basis gap when they are not.</summary>
        public double? BasisGap { get; init; }
        /// <summary>Set when the live level and the history are NOT on the same basis, so every
        /// statistic that ranks one against the other has been withheld. Null = stats are comparable.
        ///
        /// <para>This exists because the failure is silent and total: a −1/+2/−1 fly of IMM-dated legs
        /// has no forward-ticker history, falls to the annuity-less par approximation, and (before the
        /// combined-series anchor) sat ~5.4bp under our curve mid. Ranking a 10.55bp mid inside a
        /// series that spent the year between 1.4 and 6.3 produced %ile 100, z 7.75/11.04 and an
        /// AT RANGE of 186% — a position-in-range that cannot exceed 100 by construction. Every one
        /// of those numbers was arithmetically correct and completely meaningless.</para></summary>
        public string? SuppressReason { get; init; }
        /// <summary>True when <see cref="SuppressReason"/> is set — level-comparison stats are null.</summary>
        public bool Suppressed => SuppressReason != null;

        public double? ZScore3m { get; init; }
        public double? ZScore6m { get; init; }
        public double? ZScore1y { get; init; }

        public double? Mean1y { get; init; }
        public double? Std1yBp { get; init; }

        /// <summary>Annualized realized vol of daily changes over ~3m, in bp/yr (std * sqrt(252)).</summary>
        public double? RealizedVol3mBp { get; init; }
        public double? RealizedVol1yBp { get; init; }

        /// <summary>OLS-estimated mean-reversion half-life in trading days over 1y (null if not mean-reverting).</summary>
        public double? HalfLifeDays { get; init; }
        /// <summary>AR(1) coefficient φ over 1y (S_t ≈ μ + φ(S_{t−1} − μ)); null when the fit is degenerate.
        /// φ ≥ 1 (or missing) means no in-sample evidence of mean reversion — treat the series as trending.</summary>
        public double? Ar1Phi { get; init; }

        public static SeriesStats Empty(double last) => new() { Count = 0, Last = last };

        /// <summary>Compute from an ascending daily series of levels. changeScale converts a level
        /// difference to the reported change unit: 100 for a %-level series (→ bp), 1 for a bp-level series.
        ///
        /// <para><paramref name="basisRef"/> is the level the BASIS GUARD tests the history against —
        /// pass the true curve mid when <paramref name="liveLast"/> is a hypothetical (MID O'RIDE),
        /// so that entering a level outside the year's range re-scores the stats as intended instead
        /// of being mistaken for a broken history. Defaults to <paramref name="liveLast"/>.</para></summary>
        public static SeriesStats Compute(IReadOnlyList<HistPoint> series, double? liveLast = null,
            double changeScale = 100.0, double? basisRef = null)
        {
            if (series == null || series.Count == 0)
                return Empty(liveLast ?? double.NaN);

            var vals = series.Select(p => p.Value).ToArray();
            var dates = series.Select(p => p.Date).ToArray();
            double last = liveLast ?? vals[^1];
            DateTime lastDate = dates[^1];

            double? ChgDaysAgo(int approxCalDays)
            {
                var target = lastDate.AddDays(-approxCalDays);
                int idx = LastIndexOnOrBefore(dates, target);
                if (idx < 0) return null;
                return (last - vals[idx]) * changeScale;
            }

            var oneYear = Window(series, lastDate.AddDays(-366));
            var threeM = Window(series, lastDate.AddDays(-93));
            var sixM = Window(series, lastDate.AddDays(-186));

            double? min1y = oneYear.Count > 0 ? oneYear.Min() : null;
            double? max1y = oneYear.Count > 0 ? oneYear.Max() : null;
            double? pct1y = oneYear.Count > 2 ? 100.0 * oneYear.Count(v => v <= last) / oneYear.Count : null;
            double? rng = (min1y.HasValue && max1y.HasValue && max1y.Value > min1y.Value)
                ? 100.0 * (last - min1y.Value) / (max1y.Value - min1y.Value) : null;

            var (m1y, s1y) = MeanStd(oneYear);
            var (m3m, s3m) = MeanStd(threeM);
            var (m6m, s6m) = MeanStd(sixM);

            double? Z(double? mean, double? std) =>
                (mean.HasValue && std.HasValue && std.Value > 1e-12) ? (last - mean.Value) / std.Value : null;

            // YTD
            double? ytd = null;
            var jan1 = new DateTime(lastDate.Year, 1, 1);
            int ytdIdx = FirstIndexOnOrAfter(dates, jan1);
            if (ytdIdx >= 0) ytd = (last - vals[ytdIdx]) * changeScale;

            double vol1y = RealizedVol(oneYear, changeScale) ?? double.NaN;
            double lastClose = vals[^1];
            double basis = basisRef ?? last;
            double gap = (basis - lastClose) * changeScale;
            // position-in-range of the BASIS level, which is what detector 1 has to judge: under a
            // mid o'ride `last` is deliberately hypothetical and may sit anywhere.
            double? basisRng = (min1y.HasValue && max1y.HasValue && max1y.Value > min1y.Value)
                ? 100.0 * (basis - min1y.Value) / (max1y.Value - min1y.Value) : null;

            // ---- BASIS GUARD ----------------------------------------------------------------
            // Everything below the line ranks `last` (the LIVE level) inside `series` (the
            // HISTORY). That is only meaningful when the two are the same quantity. When they are
            // not, the arithmetic still succeeds and prints a confident, wrong number — so the
            // check has to happen here, once, where the two meet, rather than in each window.
            //
            // Two independent detectors, both scale-free:
            //  1. IMPOSSIBLE BY CONSTRUCTION — position-in-range outside [0,100]. `last` is not
            //     merely at an extreme, it is outside the sample altogether. No legitimate input
            //     produces this.
            //  2. IMPLAUSIBLE AS A DAY'S MOVE — the live level sits more than 10 daily sigmas from
            //     the series' own last close. One day cannot move a series ten times its own
            //     typical day; a gap that size is a different basis, not a rally. The floor keeps
            //     a genuinely quiet series (sigma -> 0) from tripping on rounding.
            // Detector 2 catches what detector 1 cannot: a basis gap small enough to land inside a
            // wide range still poisons every z-score, and nothing about the output looks wrong.
            string? suppress = null;
            if (basisRng is > 100.0 or < 0.0)
                suppress = $"live level sits outside its own 1y range (position {basisRng:0}% of "
                         + "min..max) — history and mid are not the same basis";
            else if (!double.IsNaN(vol1y) && vol1y > 0)
            {
                double dailySigma = vol1y / Math.Sqrt(252.0);
                double tol = Math.Max(2.0, 10.0 * dailySigma);   // report units are bp either way
                if (Math.Abs(gap) > tol)
                    suppress = $"live level is {gap:+0.0;-0.0} from the history's last close "
                             + $"({Math.Abs(gap) / dailySigma:0} daily sigma) — history and mid are "
                             + "not the same basis";
            }
            bool ok = suppress == null;

            return new SeriesStats
            {
                Count = series.Count,
                FirstDate = dates[0],
                LastDate = lastDate,
                Last = last,
                LastClose = lastClose,
                ChangeScale = changeScale,
                BasisGap = gap,
                SuppressReason = suppress,
                // Δ1d is history-derived here and just as cross-basis as the rest, so it goes too.
                // Callers overwrite it with an exact prev-close reprice that needs no history — but
                // that overwrite is conditional on the reprice succeeding, and when it doesn't, n/a
                // is the honest answer rather than the offset wearing a one-day label.
                Chg1d = ok ? ChgDaysAgo(1) : null,
                Chg1w = ok ? ChgDaysAgo(7) : null,
                Chg1m = ok ? ChgDaysAgo(31) : null,
                Chg3m = ok ? ChgDaysAgo(93) : null,
                Chg6m = ok ? ChgDaysAgo(186) : null,
                Chg1y = ok ? ChgDaysAgo(366) : null,
                ChgYtd = ok ? ytd : null,
                // min/max are honest for the SERIES but are displayed beside the live mid, so a
                // suppressed instance must not publish them either — a range the headline sits
                // outside of reads as a range the headline sits inside.
                Min1y = ok ? min1y : null,
                Max1y = ok ? max1y : null,
                Percentile1y = ok ? pct1y : null,
                // bounded unconditionally, not just when the basis guard is happy: a mid o'ride can
                // legitimately enter a level above the year's high, and "position inside min..max"
                // has no meaning outside [0,100] whatever put it there. Blank, never 186%.
                Range1yPct = ok && rng is >= 0.0 and <= 100.0 ? rng : null,
                Mean1y = ok ? m1y : null,
                Std1yBp = ok && s1y.HasValue ? s1y * changeScale : null,
                ZScore1y = ok ? Z(m1y, s1y) : null,
                ZScore6m = ok ? Z(m6m, s6m) : null,
                ZScore3m = ok ? Z(m3m, s3m) : null,
                // shift-invariant: built from DIFFERENCES (vol) or an AR(1) slope (half-life), so a
                // constant basis offset cannot touch them and they stay valid when suppressed.
                RealizedVol3mBp = RealizedVol(threeM, changeScale),
                RealizedVol1yBp = double.IsNaN(vol1y) ? null : vol1y,
                HalfLifeDays = HalfLife(oneYear),
                Ar1Phi = Ar1(oneYear),
            };
        }

        /// <summary>AR(1) φ = 1 + slope of (S_t − S_{t−1}) on S_{t−1}.</summary>
        private static double? Ar1(List<double> xs)
        {
            if (xs.Count < 30) return null;
            int n = xs.Count - 1;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double x = xs[i];
                double y = xs[i + 1] - xs[i];
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-12) return null;
            return 1.0 + (n * sxy - sx * sy) / denom;
        }

        private static List<double> Window(IReadOnlyList<HistPoint> s, DateTime from)
        {
            var list = new List<double>();
            foreach (var p in s) if (p.Date >= from) list.Add(p.Value);
            return list;
        }

        private static (double? mean, double? std) MeanStd(List<double> xs)
        {
            if (xs.Count < 3) return (xs.Count > 0 ? xs.Average() : (double?)null, null);
            double m = xs.Average();
            double var = xs.Sum(v => (v - m) * (v - m)) / (xs.Count - 1);
            return (m, Math.Sqrt(var));
        }

        private static double? RealizedVol(List<double> levels, double changeScale)
        {
            if (levels.Count < 5) return null;
            var diffs = new List<double>();
            for (int i = 1; i < levels.Count; i++)
                diffs.Add((levels[i] - levels[i - 1]) * changeScale); // per-day change in report unit
            if (diffs.Count < 4) return null;
            double m = diffs.Average();
            double var = diffs.Sum(d => (d - m) * (d - m)) / (diffs.Count - 1);
            return Math.Sqrt(var) * Math.Sqrt(252.0);
        }

        private static double? HalfLife(List<double> xs)
        {
            // AR(1): dx_t = a + b*x_{t-1}; half-life = -ln(2)/ln(1+b) if -2<b<0
            if (xs.Count < 30) return null;
            int n = xs.Count - 1;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double x = xs[i];
                double y = xs[i + 1] - xs[i];
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-12) return null;
            double b = (n * sxy - sx * sy) / denom;
            if (b >= 0 || b <= -1) return null;
            double hl = -Math.Log(2.0) / Math.Log(1.0 + b);
            return (hl > 0 && hl < 5000) ? hl : null;
        }

        private static int LastIndexOnOrBefore(DateTime[] dates, DateTime target)
        {
            int lo = 0, hi = dates.Length - 1, res = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (dates[mid] <= target) { res = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return res;
        }

        private static int FirstIndexOnOrAfter(DateTime[] dates, DateTime target)
        {
            int lo = 0, hi = dates.Length - 1, res = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (dates[mid] >= target) { res = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return res;
        }
    }
}
