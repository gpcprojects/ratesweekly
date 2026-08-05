using System;
using System.Linq;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.Trades;
using Xunit;

namespace RateDesk.Tests
{
    public class EngineTests
    {
        private static readonly Date AsOf = new(8, Month.July, 2026);

        private static RatesSnapshot SyntheticQuotes(CurrencyConfig cfg, Func<string, double> rateFor)
        {
            var snap = new RatesSnapshot();
            foreach (var p in (cfg.Ois?.Curve ?? Enumerable.Empty<PillarDef>())
                     .Concat(cfg.Irs?.Curve ?? Enumerable.Empty<PillarDef>()))
            {
                var full = ConfigStore.ResolveTicker(p.Ticker, cfg.DefaultSource);
                snap.Update(full, null, null, rateFor(p.Tenor));
            }
            return snap;
        }

        /// <summary>THE core invariant: bootstrapped curve must reprice every input pillar to its quote.</summary>
        [Theory]
        [InlineData("USD")]
        [InlineData("GBP")]
        [InlineData("AUD")]
        [InlineData("MXN")]
        public void Bootstrap_RoundTrips_All_Pillars(string ccy)
        {
            var cfg = ccy switch
            {
                "USD" => TestConfigs.Usd(),
                "GBP" => TestConfigs.Gbp(),
                "AUD" => TestConfigs.Aud(),
                "MXN" => TestConfigs.Mxn(),
                _ => throw new ArgumentException(ccy),
            };
            // an upward sloping synthetic curve: 3% + 20bp per year of tenor, capped
            double RateFor(string tenor) =>
                Math.Min(3.0 + 0.2 * TenorUtil.ApproxMonths(TenorUtil.Parse(tenor)) / 12.0, 5.0);

            var snap = SyntheticQuotes(cfg, RateFor);
            var curves = CurveBuilder.Build(cfg, cfg.DefaultSource, snap, AsOf);

            foreach (var pillar in curves.Pillars)
            {
                var spec = new TradeSpec
                {
                    Ccy = cfg.Ccy,
                    Product = pillar.CurveName == "OIS" ? ProductKind.OIS : ProductKind.IRS,
                    Tenor = TenorUtil.Parse(pillar.Label),
                    Notional = 1_000_000,
                };
                var (swap, _, _, _) = Pricer.BuildTrade(spec, spec.Product, curves, pillar.MarketRatePct / 100.0);
                swap.setPricingEngine(new DiscountingSwapEngine(curves.DiscountHandle));
                double npv = swap.NPV();
                // NPV of a pillar traded at its own market rate must be ~0 (tolerance: 1e-6 of notional)
                Assert.True(Math.Abs(npv) < 1.0,
                    $"{ccy} {pillar.CurveName} {pillar.Label}: NPV {npv:F6} not ~0");

                double par = Pricer.FairRate(swap) * 100.0;
                Assert.True(Math.Abs(par - pillar.MarketRatePct) < 1e-7,
                    $"{ccy} {pillar.Label}: par {par:F8} vs quote {pillar.MarketRatePct:F8}");
            }
        }

        [Fact]
        public void Usd_Par_Trade_Has_Zero_Npv_And_Sane_Ladder_Sign()
        {
            var cfg = TestConfigs.Usd();
            var snap = SyntheticQuotes(cfg, _ => 4.0);
            var curves = CurveBuilder.Build(cfg, cfg.DefaultSource, snap, AsOf);

            var spec = new TradeSpec { Ccy = "USD", Product = ProductKind.OIS, Tenor = TenorUtil.Parse("5Y"), Notional = 10_000_000, PayFixed = true };
            var result = Pricer.Price(spec, curves);
            Assert.True(Math.Abs(result.Npv) < 1.0, $"par NPV should be ~0, got {result.Npv}");
            Assert.InRange(result.ParRatePct, 3.9, 4.1);

            // annuity of 5y 10mm should be roughly 10mm * ~4.6y * 1bp ~= 4,600
            Assert.InRange(result.Annuity01, 3_000, 6_000);

            // payer of fixed at BELOW par must have positive NPV
            var spec2 = new TradeSpec { Ccy = "USD", Product = ProductKind.OIS, Tenor = TenorUtil.Parse("5Y"), Notional = 10_000_000, PayFixed = true, FixedRate = 0.035 };
            var r2 = Pricer.Price(spec2, curves);
            Assert.True(r2.Npv > 0, $"payer below par should be ITM, npv={r2.Npv}");
        }

        [Fact]
        public void Aud_Tenor_Switch_Selects_3s_Then_6s()
        {
            var cfg = TestConfigs.Aud();
            var leg3y = SwapBuilder.SelectIrsLeg(cfg.Irs!, TenorUtil.Parse("3Y"), null);
            var leg4y = SwapBuilder.SelectIrsLeg(cfg.Irs!, TenorUtil.Parse("4Y"), null);
            var leg10y = SwapBuilder.SelectIrsLeg(cfg.Irs!, TenorUtil.Parse("10Y"), null);
            Assert.Equal("3M", leg3y.FloatTenor);
            Assert.Equal("Quarterly", leg3y.FixedFreq);
            Assert.Equal("6M", leg4y.FloatTenor);
            Assert.Equal("Semiannual", leg4y.FixedFreq);
            Assert.Equal("6M", leg10y.FloatTenor);
        }

        [Fact]
        public void Spot_Lags_Respect_Calendar()
        {
            // 2026-07-08 is a Wednesday
            var usd = TestConfigs.Usd();
            var gbp = TestConfigs.Gbp();
            var aud = TestConfigs.Aud();
            var calU = RateDesk.Core.QL.QlMaps.MakeCalendar("USD");
            var calG = RateDesk.Core.QL.QlMaps.MakeCalendar("GBP");
            var calA = RateDesk.Core.QL.QlMaps.MakeCalendar("AUD");
            Assert.Equal(new Date(10, Month.July, 2026), SwapBuilder.SpotDate(usd, calU, AsOf));
            Assert.Equal(AsOf, SwapBuilder.SpotDate(gbp, calG, AsOf));
            Assert.Equal(new Date(9, Month.July, 2026), SwapBuilder.SpotDate(aud, calA, AsOf));
        }

        [Fact]
        public void Mxn_Maturity_Follows_28Day_Chain()
        {
            var cfg = TestConfigs.Mxn();
            var snap = SyntheticQuotes(cfg, _ => 8.0);
            var curves = CurveBuilder.Build(cfg, cfg.DefaultSource, snap, AsOf);

            var spec = new TradeSpec { Ccy = "MXN", Product = ProductKind.OIS, Tenor = TenorUtil.Parse("65P"), Notional = 100_000_000 };
            var res = Pricer.Price(spec, curves);
            // 65 * 28 = 1820 days from effective 2026-07-09 => 2031-07-03 (+ adjust)
            Assert.Equal(2031, res.Maturity.year());
            Assert.True(Math.Abs(res.Npv) < 100, $"par MXN NPV {res.Npv}");
            // coupon count: 65 periods
            int fixedCoupons = res.Cashflows.Count(c => c.Leg == "Fixed");
            Assert.Equal(65, fixedCoupons);
        }

        [Fact]
        public void Forward_And_Imm_Starts_Work()
        {
            var cfg = TestConfigs.Usd();
            var snap = SyntheticQuotes(cfg, _ => 4.0);
            var curves = CurveBuilder.Build(cfg, cfg.DefaultSource, snap, AsOf);

            var fwd = new TradeSpec
            {
                Ccy = "USD", Product = ProductKind.OIS, StartKind = StartKind.Forward,
                ForwardStart = TenorUtil.Parse("1Y"), Tenor = TenorUtil.Parse("5Y"),
            };
            var rf = Pricer.Price(fwd, curves);
            Assert.True(rf.Effective > new Date(1, Month.July, 2027));

            var imm = new TradeSpec
            {
                Ccy = "USD", Product = ProductKind.OIS, StartKind = StartKind.Imm,
                ImmDate = ImmUtil.ThirdWednesday(9, 2026), ImmCode = "U26", Tenor = TenorUtil.Parse("2Y"),
            };
            var ri = Pricer.Price(imm, curves);
            Assert.Equal(new Date(16, Month.September, 2026), ri.Effective);
        }
    }
}
