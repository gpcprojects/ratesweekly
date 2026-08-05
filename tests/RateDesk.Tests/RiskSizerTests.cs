using System;
using RateDesk.Core.Risk;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>The one sizing rule every product goes through: density -> notional/dv01, the $25k
    /// desk default, and round-lot notionals.</summary>
    public class RiskSizerTests
    {
        [Fact]
        public void RoundNotional_Snaps_To_The_Nearest_Lot()
        {
            // the desk deals 16.5mm, never 16,470,219
            Assert.Equal(16_500_000, RiskSizer.RoundNotional(16_470_219));
            Assert.Equal(16_500_000, RiskSizer.RoundNotional(16_700_000));
            Assert.Equal(16_000_000, RiskSizer.RoundNotional(16_200_000));
            Assert.Equal(1_463_000_000, RiskSizer.RoundNotional(1_463_094_274)); // short-dated, huge
            Assert.Equal(0.0, RiskSizer.RoundNotional(12_345_678) % RiskSizer.NotionalLot);

            // exact halves round away from zero, so a lot boundary never silently drops a lot
            Assert.Equal(17_000_000, RiskSizer.RoundNotional(16_750_000));

            // a sub-lot size must not vanish to a zero-notional trade, nor inflate to a full lot
            Assert.Equal(120_000, RiskSizer.RoundNotional(120_000));
            Assert.Equal(0.0, RiskSizer.RoundNotional(0.0));
        }

        [Fact]
        public void Explicit_Notional_Is_Traded_Exactly_And_Sets_Dv01()
        {
            // 450/bp per mm -> 20mm carries 9,000/bp
            var r = RiskSizer.Resolve(densityPerMm: 450.0, explicitNotional: 20_000_000);
            Assert.Equal(20_000_000, r.Notional, 6);
            Assert.Equal(9_000, r.Dv01, 6);

            // NOT rounded — a typed notional is dealt as typed, odd lot or not
            var odd = RiskSizer.Resolve(densityPerMm: 450.0, explicitNotional: 20_123_456);
            Assert.Equal(20_123_456, odd.Notional, 6);
        }

        [Fact]
        public void Dv01_Target_Backs_Out_A_Round_Lot_And_Reports_Its_Real_Dv01()
        {
            var r = RiskSizer.Resolve(densityPerMm: 450.0, explicitDv01: 25_000);

            // 25,000/450 * 1mm = 55,555,555 -> 55.5mm
            Assert.Equal(55_500_000, r.Notional, 6);
            Assert.Equal(0.0, r.Notional % RiskSizer.NotionalLot, 6);

            // dv01 is RE-DERIVED from the round lot: slightly off the 25k target, by design
            Assert.Equal(450.0 * 55.5, r.Dv01, 6);
            Assert.NotEqual(25_000.0, r.Dv01);
            Assert.True(Math.Abs(r.Dv01 - 25_000) < 450.0 * RiskSizer.NotionalLot / 2 / 1_000_000.0 + 1e-9,
                $"realised dv01 {r.Dv01:N2} should be within half a lot of the 25k target");
        }

        [Fact]
        public void Unsized_Uses_The_Desk_Default()
        {
            var r = RiskSizer.Resolve(densityPerMm: 450.0);
            var same = RiskSizer.Resolve(densityPerMm: 450.0, explicitDv01: RiskSizer.DefaultDv01Usd);
            Assert.Equal(same.Notional, r.Notional, 6);
            Assert.Equal(same.Dv01, r.Dv01, 6);
            Assert.Equal(25_000.0, RiskSizer.DefaultDv01Usd);
        }

        [Fact]
        public void Degenerate_Density_Does_Not_Produce_Infinity()
        {
            // a zero-length/unbounded leg must not blow the notional up to Infinity or NaN
            var r = RiskSizer.Resolve(densityPerMm: 0.0, explicitDv01: 25_000);
            Assert.False(double.IsInfinity(r.Notional) || double.IsNaN(r.Notional));
            Assert.False(double.IsInfinity(r.Dv01) || double.IsNaN(r.Dv01));

            // a negative density (sign convention slip upstream) is treated by magnitude
            var neg = RiskSizer.Resolve(densityPerMm: -450.0, explicitDv01: 25_000);
            Assert.True(neg.Notional > 0);
        }
    }
}
