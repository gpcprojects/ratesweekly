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
        /// difference to the reported change unit: 100 for a %-level series (→ bp), 1 for a bp-level series.</summary>
        public static SeriesStats Compute(IReadOnlyList<HistPoint> series, double? liveLast = null, double changeScale = 100.0)
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

            return new SeriesStats
            {
                Count = series.Count,
                FirstDate = dates[0],
                LastDate = lastDate,
                Last = last,
                Chg1d = ChgDaysAgo(1),
                Chg1w = ChgDaysAgo(7),
                Chg1m = ChgDaysAgo(31),
                Chg3m = ChgDaysAgo(93),
                Chg6m = ChgDaysAgo(186),
                Chg1y = ChgDaysAgo(366),
                ChgYtd = ytd,
                Min1y = min1y,
                Max1y = max1y,
                Percentile1y = pct1y,
                Range1yPct = rng,
                Mean1y = m1y,
                Std1yBp = s1y.HasValue ? s1y * changeScale : null,
                ZScore1y = Z(m1y, s1y),
                ZScore6m = Z(m6m, s6m),
                ZScore3m = Z(m3m, s3m),
                RealizedVol3mBp = RealizedVol(Window(series, lastDate.AddDays(-93)), changeScale),
                RealizedVol1yBp = RealizedVol(Window(series, lastDate.AddDays(-366)), changeScale),
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
