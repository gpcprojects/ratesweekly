using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Market;

namespace RateDesk.Core.Analytics
{
    /// <summary>OLS helpers for hedge ratios and factor-conditional rich/cheap.</summary>
    public static class Regression
    {
        /// <summary>y = a + b·x. Returns slope, R², and the z-score of the LAST residual
        /// (how far today sits from the fitted relationship, in residual sigmas).</summary>
        public static (double beta, double r2, double residZ)? Simple(double[] y, double[] x)
        {
            int n = Math.Min(y.Length, x.Length);
            if (n < 30) return null;
            double mx = x.Take(n).Average(), my = y.Take(n).Average();
            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                sxx += (x[i] - mx) * (x[i] - mx);
                sxy += (x[i] - mx) * (y[i] - my);
                syy += (y[i] - my) * (y[i] - my);
            }
            if (sxx < 1e-12 || syy < 1e-12) return null;
            double b = sxy / sxx;
            double a = my - b * mx;
            double ssRes = 0;
            var resid = new double[n];
            for (int i = 0; i < n; i++)
            {
                resid[i] = y[i] - (a + b * x[i]);
                ssRes += resid[i] * resid[i];
            }
            double r2 = 1 - ssRes / syy;
            double sd = Math.Sqrt(ssRes / Math.Max(1, n - 2));
            return (b, r2, sd > 1e-9 ? resid[n - 1] / sd : 0.0);
        }

        /// <summary>y = a + b1·x1 + b2·x2 (normal equations). Returns R² and last-residual z.</summary>
        public static (double b1, double b2, double r2, double residZ)? Two(double[] y, double[] x1, double[] x2)
        {
            int n = new[] { y.Length, x1.Length, x2.Length }.Min();
            if (n < 30) return null;
            double m1 = x1.Take(n).Average(), m2 = x2.Take(n).Average(), my = y.Take(n).Average();
            double s11 = 0, s22 = 0, s12 = 0, s1y = 0, s2y = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double d1 = x1[i] - m1, d2 = x2[i] - m2, dy = y[i] - my;
                s11 += d1 * d1; s22 += d2 * d2; s12 += d1 * d2;
                s1y += d1 * dy; s2y += d2 * dy; syy += dy * dy;
            }
            double det = s11 * s22 - s12 * s12;
            if (Math.Abs(det) < 1e-12 || syy < 1e-12) return null;
            double b1 = (s22 * s1y - s12 * s2y) / det;
            double b2 = (s11 * s2y - s12 * s1y) / det;
            double a = my - b1 * m1 - b2 * m2;
            double ssRes = 0;
            var resid = new double[n];
            for (int i = 0; i < n; i++)
            {
                resid[i] = y[i] - (a + b1 * x1[i] + b2 * x2[i]);
                ssRes += resid[i] * resid[i];
            }
            double r2 = 1 - ssRes / syy;
            double sd = Math.Sqrt(ssRes / Math.Max(1, n - 3));
            return (b1, b2, r2, sd > 1e-9 ? resid[n - 1] / sd : 0.0);
        }

        /// <summary>Intersect two dated series into aligned value arrays (dates both have).</summary>
        public static (double[] a, double[] b) AlignByDate(IReadOnlyList<HistPoint> sa, IReadOnlyList<HistPoint> sb)
        {
            var mb = sb.ToDictionary(p => p.Date, p => p.Value);
            var la = new List<double>();
            var lb = new List<double>();
            foreach (var p in sa)
                if (mb.TryGetValue(p.Date, out var v)) { la.Add(p.Value); lb.Add(v); }
            return (la.ToArray(), lb.ToArray());
        }

        /// <summary>Day-on-day changes of a series.</summary>
        public static double[] Changes(double[] v)
        {
            if (v.Length < 2) return Array.Empty<double>();
            var c = new double[v.Length - 1];
            for (int i = 1; i < v.Length; i++) c[i - 1] = v[i] - v[i - 1];
            return c;
        }
    }
}
