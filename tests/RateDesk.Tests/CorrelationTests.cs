using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Analytics;
using RateDesk.Core.Market;
using Xunit;

namespace RateDesk.Tests
{
    public class CorrelationTests
    {
        private static List<HistPoint> Series(DateTime start, params double[] values) =>
            values.Select((v, i) => new HistPoint(start.AddDays(i), v)).ToList();

        [Fact]
        public void Pearson_PerfectAndInverse()
        {
            var x = Enumerable.Range(0, 60).Select(i => Math.Sin(i * 0.7) + i * 0.01).ToArray();
            var y = x.Select(v => 3.0 * v + 1.0).ToArray();
            var yInv = x.Select(v => -2.0 * v).ToArray();
            Assert.Equal(1.0, Correlation.Pearson(x, y)!.Value, 6);
            Assert.Equal(-1.0, Correlation.Pearson(x, yInv)!.Value, 6);
            Assert.Null(Correlation.Pearson(x, new double[60])); // constant → degenerate
            Assert.Null(Correlation.Pearson(x.Take(10).ToArray(), y.Take(10).ToArray())); // too short
        }

        [Fact]
        public void Pearson_LastNWindow()
        {
            // first 100 obs perfectly correlated, last 50 perfectly anti-correlated
            var rnd = new Random(42);
            var x = new double[150];
            var y = new double[150];
            for (int i = 0; i < 150; i++)
            {
                x[i] = rnd.NextDouble() - 0.5;
                y[i] = i < 100 ? x[i] : -x[i];
            }
            Assert.Equal(-1.0, Correlation.Pearson(x, y, 50)!.Value, 6);  // pure anti-correlated era
            Assert.Equal(1.0, Correlation.Pearson(
                x.Take(100).ToArray(), y.Take(100).ToArray())!.Value, 6); // pure correlated era
            var mixed = Correlation.Pearson(x, y, 150)!.Value;            // both eras net out
            Assert.InRange(mixed, -0.6, 0.8);
        }

        [Fact]
        public void AlignedChanges_InnerJoinsAndDifferences()
        {
            var start = new DateTime(2025, 1, 1);
            var a = Series(start, 1.0, 2.0, 4.0, 7.0);
            // b is missing the second date — the 2.0→4.0 change in a must pair with 10→40 in b,
            // never with a phantom gap value
            var b = new List<HistPoint>
            {
                new(start, 10.0), new(start.AddDays(2), 40.0), new(start.AddDays(3), 80.0),
            };
            var (dates, dx, dy) = Correlation.AlignedChanges(a, b, logA: false, logB: false);
            Assert.Equal(2, dx.Length);
            Assert.Equal(3.0, dx[0], 9);  // 1 → 4 across the aligned gap
            Assert.Equal(30.0, dy[0], 9);
            Assert.Equal(3.0, dx[1], 9);
            Assert.Equal(40.0, dy[1], 9);
            Assert.Equal(start.AddDays(3), dates[^1]);
        }

        [Fact]
        public void AlignedChanges_LogSpace()
        {
            var start = new DateTime(2025, 1, 1);
            var a = Series(start, 100.0, 110.0);
            var b = Series(start, 50.0, 55.0);
            var (_, dx, dy) = Correlation.AlignedChanges(a, b, logA: true, logB: true);
            Assert.Equal(Math.Log(1.1) * 100.0, dx[0], 9);
            Assert.Equal(Math.Log(1.1) * 100.0, dy[0], 9);
        }

        [Fact]
        public void Rolling_DetectsBreakdown()
        {
            // 400 aligned changes: first 300 y follows x, last 100 y is independent noise
            var rnd = new Random(7);
            int n = 401;
            var start = new DateTime(2024, 1, 1);
            var dates = Enumerable.Range(0, n - 1).Select(i => start.AddDays(i)).ToArray();
            var dx = new double[n - 1];
            var dy = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                dx[i] = rnd.NextDouble() - 0.5;
                dy[i] = i < 300 ? dx[i] + 0.05 * (rnd.NextDouble() - 0.5)
                                : rnd.NextDouble() - 0.5;
            }
            var roll = Correlation.Rolling(dates, dx, dy, window: 63, step: 5);
            Assert.True(roll.Count > 20);
            Assert.True(roll.First(p => p.Date <= start.AddDays(280)).Value > 0.9);
            Assert.True(Math.Abs(roll[^1].Value) < 0.5); // the link has dissolved by the end
        }

        [Fact]
        public void BreakScore_ScoresCollapseAndFlip()
        {
            Assert.Null(Correlation.BreakScore(0.2, -0.2));            // never a real link
            Assert.Equal(0.0, Correlation.BreakScore(0.8, 0.8)!.Value, 9);
            Assert.Equal(0.8, Correlation.BreakScore(0.8, 0.0)!.Value, 9);   // collapsed
            Assert.Equal(1.6, Correlation.BreakScore(0.8, -0.8)!.Value, 9);  // fully flipped
            Assert.Equal(0.7, Correlation.BreakScore(-0.7, 0.0)!.Value, 9);  // negative links too
            Assert.Equal(-0.2, Correlation.BreakScore(0.7, 0.9)!.Value, 9);  // strengthening = negative
        }
    }
}
