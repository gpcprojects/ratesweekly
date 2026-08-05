using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Market;

namespace RateDesk.Core.Analytics
{
    /// <summary>Correlation machinery for the CORR screen and the RV bubble map. All correlations
    /// are Pearson on day-on-day changes (rates: bp; fx/commodities/equities: log·100 ≈ daily %),
    /// which makes them unit-free and comparable across asset classes.</summary>
    public static class Correlation
    {
        /// <summary>Inner-join two dated level series, then difference: changes are computed on
        /// ALIGNED dates so a holiday gap in one market can't lag the pairing. Log series are
        /// differenced in log space ×100.</summary>
        public static (DateTime[] dates, double[] dx, double[] dy) AlignedChanges(
            IReadOnlyList<HistPoint> a, IReadOnlyList<HistPoint> b, bool logA, bool logB)
        {
            var mb = new Dictionary<DateTime, double>(b.Count);
            foreach (var p in b) mb[p.Date.Date] = p.Value;
            var dates = new List<DateTime>();
            var va = new List<double>();
            var vb = new List<double>();
            foreach (var p in a)
                if (mb.TryGetValue(p.Date.Date, out var v))
                {
                    if ((logA && p.Value <= 0) || (logB && v <= 0)) continue; // log needs positive levels
                    dates.Add(p.Date.Date);
                    va.Add(logA ? Math.Log(p.Value) * 100.0 : p.Value);
                    vb.Add(logB ? Math.Log(v) * 100.0 : v);
                }
            if (dates.Count < 2) return (Array.Empty<DateTime>(), Array.Empty<double>(), Array.Empty<double>());
            var dx = new double[dates.Count - 1];
            var dy = new double[dates.Count - 1];
            for (int i = 1; i < dates.Count; i++)
            {
                dx[i - 1] = va[i] - va[i - 1];
                dy[i - 1] = vb[i] - vb[i - 1];
            }
            return (dates.Skip(1).ToArray(), dx, dy);
        }

        /// <summary>Pearson ρ over the LAST n observations (all when n ≤ 0). Null when degenerate
        /// or shorter than minN (weekly-basis windows legitimately run on fewer points).</summary>
        public static double? Pearson(double[] x, double[] y, int lastN = 0, int minN = 20)
        {
            int total = Math.Min(x.Length, y.Length);
            int n = lastN > 0 ? Math.Min(lastN, total) : total;
            if (n < minN) return null;
            int off = total - n;
            double mx = 0, my = 0;
            for (int i = off; i < total; i++) { mx += x[i]; my += y[i]; }
            mx /= n; my /= n;
            double sxx = 0, syy = 0, sxy = 0;
            for (int i = off; i < total; i++)
            {
                double dx = x[i] - mx, dy = y[i] - my;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            if (sxx < 1e-12 || syy < 1e-12) return null;
            return sxy / Math.Sqrt(sxx * syy);
        }

        /// <summary>Rolling ρ (window of daily changes, stepped) — the evolution series the
        /// CORR chart draws. Each point is dated at its window END.</summary>
        public static List<HistPoint> Rolling(DateTime[] dates, double[] dx, double[] dy,
            int window = 63, int step = 5)
        {
            var outp = new List<HistPoint>();
            int n = Math.Min(dates.Length, Math.Min(dx.Length, dy.Length));
            for (int end = window; end <= n; end += step)
            {
                double mx = 0, my = 0;
                for (int i = end - window; i < end; i++) { mx += dx[i]; my += dy[i]; }
                mx /= window; my /= window;
                double sxx = 0, syy = 0, sxy = 0;
                for (int i = end - window; i < end; i++)
                {
                    double a = dx[i] - mx, b = dy[i] - my;
                    sxx += a * a; syy += b * b; sxy += a * b;
                }
                if (sxx < 1e-12 || syy < 1e-12) continue;
                outp.Add(new HistPoint(dates[end - 1], sxy / Math.Sqrt(sxx * syy)));
            }
            return outp;
        }

        /// <summary>How much a relationship has WEAKENED vs its norm: sign(ρ_LR)·(ρ_LR − ρ_now).
        /// 0 = intact, ≈1 = fully collapsed, ≈2 = fully flipped. Null unless the long-run link
        /// was meaningful to begin with (|ρ_LR| ≥ 0.35).</summary>
        public static double? BreakScore(double? lr, double? now)
        {
            if (lr is not double l || now is not double c || Math.Abs(l) < 0.35) return null;
            return Math.Sign(l) * (l - c);
        }

        /// <summary>Lag-1 autocorrelation of the last n observations (all when n ≤ 0).</summary>
        public static double Autocorr1(double[] x, int lastN = 0)
        {
            int total = x.Length;
            int n = lastN > 0 ? Math.Min(lastN, total) : total;
            if (n < 10) return 0;
            int off = total - n;
            double m = 0;
            for (int i = off; i < total; i++) m += x[i];
            m /= n;
            double num = 0, den = 0;
            for (int i = off; i < total; i++)
            {
                double d = x[i] - m;
                den += d * d;
                if (i > off) num += d * (x[i - 1] - m);
            }
            return den < 1e-12 ? 0 : num / den;
        }

        /// <summary>Fisher-z test of ρ_now vs ρ_LR with autocorrelation-corrected effective sample
        /// sizes n_eff = n(1−r1)/(1+r1). Negative T·sign(ρ_LR) = the relationship has weakened;
        /// |T| ≥ 2.5 is a significant break rather than 3m-window noise.</summary>
        public static double? FisherT(double? rhoNow, double? rhoLr, int nNow, int nLr, double r1)
        {
            if (rhoNow is not double rn || rhoLr is not double rl) return null;
            rn = Math.Clamp(rn, -0.9999, 0.9999);
            rl = Math.Clamp(rl, -0.9999, 0.9999);
            double shrink = (1 - Math.Max(r1, 0)) / (1 + Math.Max(r1, 0));
            double nNowEff = nNow * shrink, nLrEff = nLr * shrink;
            if (nNowEff <= 4 || nLrEff <= 4) return null;
            double z = Atanh(rn) - Atanh(rl);
            double se = Math.Sqrt(1.0 / (nNowEff - 3) + 1.0 / (nLrEff - 3));
            return se < 1e-12 ? null : z / se;
        }

        private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));

        /// <summary>Pearson of x(t) vs y(t+lag) over the last n valid pairs — POSITIVE lag means
        /// x LEADS y by `lag` observations (y reacts later). Catches relationships that
        /// contemporaneous correlation misses (oil moves today, NOK front end reprices tomorrow).</summary>
        public static double? PearsonLagged(double[] x, double[] y, int lag, int lastN = 0, int minN = 20)
        {
            if (lag == 0) return Pearson(x, y, lastN, minN);
            int len = Math.Min(x.Length, y.Length);
            var xs = new List<double>(len);
            var ys = new List<double>(len);
            for (int i = Math.Max(0, -lag); i < len && i + lag < len; i++)
            {
                xs.Add(x[i]);
                ys.Add(y[i + lag]);
            }
            return Pearson(xs.ToArray(), ys.ToArray(), lastN, minN);
        }

        /// <summary>AR(1) coefficient φ of a series (null when degenerate/too short).</summary>
        public static double? Ar1Phi(double[] xs)
        {
            if (xs.Length < 30) return null;
            int n = xs.Length - 1;
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
    }
}
