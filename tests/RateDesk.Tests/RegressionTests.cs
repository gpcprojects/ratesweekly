using System;
using System.Linq;
using RateDesk.Core.Analytics;
using RateDesk.Core.Market;
using Xunit;

namespace RateDesk.Tests;

public class RegressionTests
{
    [Fact]
    public void Simple_recovers_exact_line()
    {
        var x = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
        var y = x.Select(v => 3.0 + 2.0 * v).ToArray();
        var r = Regression.Simple(y, x);
        Assert.NotNull(r);
        Assert.Equal(2.0, r!.Value.beta, 6);
        Assert.Equal(1.0, r.Value.r2, 6);
        Assert.Equal(0.0, r.Value.residZ, 4);
    }

    [Fact]
    public void Simple_flags_last_point_dislocation()
    {
        var rng = new Random(7);
        var x = Enumerable.Range(0, 200).Select(i => (double)i / 10).ToArray();
        var y = x.Select(v => 2.0 * v + (rng.NextDouble() - 0.5) * 0.2).ToArray();
        y[^1] += 3.0; // way off the fitted relationship
        var r = Regression.Simple(y, x);
        Assert.NotNull(r);
        Assert.True(r!.Value.residZ > 3, $"expected large positive resid z, got {r.Value.residZ}");
    }

    [Fact]
    public void Two_recovers_exact_plane()
    {
        var rng = new Random(11);
        var x1 = Enumerable.Range(0, 150).Select(_ => rng.NextDouble() * 10).ToArray();
        var x2 = Enumerable.Range(0, 150).Select(_ => rng.NextDouble() * 5).ToArray();
        var y = x1.Zip(x2, (a, b) => 1.0 + 2.0 * a - 3.0 * b).ToArray();
        var r = Regression.Two(y, x1, x2);
        Assert.NotNull(r);
        Assert.Equal(2.0, r!.Value.b1, 5);
        Assert.Equal(-3.0, r.Value.b2, 5);
        Assert.Equal(1.0, r.Value.r2, 6);
    }

    [Fact]
    public void AlignByDate_intersects()
    {
        var d0 = new DateTime(2026, 1, 1);
        var a = Enumerable.Range(0, 10).Select(i => new HistPoint(d0.AddDays(i), i)).ToList();
        var b = Enumerable.Range(5, 10).Select(i => new HistPoint(d0.AddDays(i), i * 2)).ToList();
        var (xa, xb) = Regression.AlignByDate(a, b);
        Assert.Equal(5, xa.Length);
        Assert.Equal(new double[] { 5, 6, 7, 8, 9 }, xa);
        Assert.Equal(new double[] { 10, 12, 14, 16, 18 }, xb);
    }

    [Fact]
    public void Changes_diffs()
    {
        Assert.Equal(new double[] { 2, 3 }, Regression.Changes(new double[] { 1, 3, 6 }));
    }
}
