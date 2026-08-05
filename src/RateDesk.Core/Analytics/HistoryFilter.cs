using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Market;

namespace RateDesk.Core.Analytics
{
    /// <summary>Removes obvious single-print data anomalies from daily history (bad ticks, stale
    /// re-prints) without clipping genuine regime moves: a point is only replaced when it deviates
    /// hard from the local median AND its neighbours agree with each other (classic spike signature).</summary>
    public static class HistoryFilter
    {
        /// <summary>Hampel-style despike. window = one-sided width; k = MAD multiples.
        /// MAD is floored at 0.5bp so flat series don't flag micro-ticks.
        /// passes > 1 re-runs on the cleaned data — clears 2-3 day spike CLUSTERS, where each bad
        /// print shields its bad neighbour from the single-pass neighbour test.</summary>
        public static IReadOnlyList<HistPoint> Despike(IReadOnlyList<HistPoint> series,
            int window = 5, double k = 6.0, double madFloorPct = 0.005, int passes = 1)
        {
            if (series == null || series.Count < 2 * window + 3) return series ?? Array.Empty<HistPoint>();
            var vals = series.Select(p => p.Value).ToArray();
            bool any = false;

            for (int pass = 0; pass < passes; pass++)
            {
                var outVals = (double[])vals.Clone();
                int replaced = 0;
                for (int i = 1; i < vals.Length - 1; i++)
                {
                    int lo = Math.Max(0, i - window), hi = Math.Min(vals.Length - 1, i + window);
                    var win = new List<double>(hi - lo);
                    for (int j = lo; j <= hi; j++) if (j != i) win.Add(vals[j]);
                    win.Sort();
                    double med = Median(win);
                    double mad = Median(win.Select(v => Math.Abs(v - med)).OrderBy(x => x).ToList());
                    double thr = k * 1.4826 * Math.Max(mad, madFloorPct);

                    double dev = Math.Abs(vals[i] - med);
                    if (dev > thr)
                    {
                        // spike if neighbours agree with each other (a persistent jump keeps both sides
                        // moved), OR the deviation is so extreme no genuine one-day move explains it
                        double neighbourGap = Math.Abs(vals[i + 1] - vals[i - 1]);
                        if (neighbourGap < thr || dev > 2.5 * thr)
                        {
                            outVals[i] = med;
                            replaced++;
                        }
                    }
                }
                if (replaced == 0) break;
                vals = outVals;
                any = true;
            }
            if (!any) return series;
            var result = new HistPoint[series.Count];
            for (int i = 0; i < series.Count; i++) result[i] = new HistPoint(series[i].Date, vals[i]);
            return result;
        }

        private static double Median(IReadOnlyList<double> sorted) =>
            sorted.Count == 0 ? 0
            : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
            : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);
    }
}
