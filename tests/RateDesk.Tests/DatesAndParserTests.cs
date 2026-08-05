using QLNet;
using RateDesk.Core.Dates;
using RateDesk.Core.Trades;
using Xunit;

namespace RateDesk.Tests
{
    public class ImmTests
    {
        [Theory]
        [InlineData("M26", 17, 6, 2026)]  // 3rd Wed June 2026
        [InlineData("H27", 17, 3, 2027)]
        [InlineData("U26", 16, 9, 2026)]
        [InlineData("Z26", 16, 12, 2026)]
        [InlineData("m28", 21, 6, 2028)]
        [InlineData("IMMZ27", 15, 12, 2027)]
        public void Imm_Codes_Give_Third_Wednesday(string code, int d, int m, int y)
        {
            Assert.True(ImmUtil.TryParse(code, out var date));
            Assert.Equal(new Date(d, (Month)m, y), date);
        }

        [Fact]
        public void Non_Imm_Tokens_Rejected()
        {
            Assert.False(ImmUtil.TryParse("5y", out _));
            Assert.False(ImmUtil.TryParse("X26", out _));
        }
    }

    public class TenorTests
    {
        [Theory]
        [InlineData("5Y", 5, TimeUnit.Years)]
        [InlineData("18m", 18, TimeUnit.Months)]
        [InlineData("1w", 1, TimeUnit.Weeks)]
        [InlineData("13P", 52, TimeUnit.Weeks)] // 13 * 4 weeks
        public void Tenors_Parse(string s, int n, TimeUnit u)
        {
            var p = TenorUtil.Parse(s);
            Assert.Equal(n, p.length());
            Assert.Equal(u, p.units());
        }
    }

    public class ParserTests
    {
        private static RateDesk.Core.Config.ConfigStore Store()
        {
            // minimal in-memory store via temp dir round-trip
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_test_cfg");
            System.IO.Directory.CreateDirectory(dir);
            foreach (var cfg in new[] { TestConfigs.Usd(), TestConfigs.Gbp(), TestConfigs.Aud(), TestConfigs.Mxn() })
            {
                var json = System.Text.Json.JsonSerializer.Serialize(cfg);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, cfg.Ccy.ToLowerInvariant() + ".json"), json);
            }
            return RateDesk.Core.Config.ConfigStore.LoadFromDirectory(dir);
        }

        [Fact]
        public void Basic_Command()
        {
            var s = CommandParser.Parse("usd 5y", Store());
            Assert.Equal("USD", s.Ccy);
            Assert.Equal("5Y", TenorUtil.Format(s.Tenor!));
            Assert.Equal(StartKind.Spot, s.StartKind);
        }

        [Fact]
        public void Imm_With_Tenor_And_Direction()
        {
            var s = CommandParser.Parse("aud m26 5y pay 100m", Store());
            Assert.Equal("AUD", s.Ccy);
            Assert.Equal(StartKind.Imm, s.StartKind);
            Assert.Equal(new Date(17, Month.June, 2026), s.ImmDate);
            Assert.Equal("5Y", TenorUtil.Format(s.Tenor!));
            Assert.True(s.PayFixed);
            Assert.Equal(100_000_000, s.Notional);
        }

        [Fact]
        public void Combined_Imm_Tenor_Token()
        {
            var s = CommandParser.Parse("aud m26-5y", Store());
            Assert.Equal(StartKind.Imm, s.StartKind);
            Assert.Equal("5Y", TenorUtil.Format(s.Tenor!));
        }

        [Fact]
        public void Forward_Combined_Token()
        {
            var s = CommandParser.Parse("gbp 1y5y rec @4.5", Store());
            Assert.Equal(StartKind.Forward, s.StartKind);
            Assert.Equal("1Y", TenorUtil.Format(s.ForwardStart!));
            Assert.Equal("5Y", TenorUtil.Format(s.Tenor!));
            Assert.False(s.PayFixed);
            Assert.Equal(0.045, s.FixedRate!.Value, 10);
        }

        [Fact]
        public void Fra_And_Source()
        {
            var s = CommandParser.Parse("usd 3x6 fra src:BMOD", Store());
            Assert.Equal(ProductKind.FRA, s.Product);
            Assert.Equal(3, s.FraStartMonths);
            Assert.Equal(6, s.FraEndMonths);
            Assert.Equal("BMOD", s.Source);
        }

        [Fact]
        public void Index_Override()
        {
            var s = CommandParser.Parse("aud 5y 3s", Store());
            Assert.Equal(3, s.FloatTenorOverride!.length());
        }
    }
}
