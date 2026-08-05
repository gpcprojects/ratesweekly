using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QLNet;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.Query;
using RateDesk.Core.Trades;
using Xunit;

namespace RateDesk.Tests
{
    public class SeriesStatsTests
    {
        [Fact]
        public void RisingSeries_Stats_AreConsistent()
        {
            var start = new DateTime(2025, 6, 1);
            var pts = new List<HistPoint>();
            for (int i = 0; i < 400; i++)
                pts.Add(new HistPoint(start.AddDays(i), 2.0 + i * 0.001)); // +0.1bp/day

            var s = SeriesStats.Compute(pts);
            Assert.Equal(400, s.Count);
            Assert.Equal(2.399, s.Last, 6);
            Assert.True(s.Chg1d.HasValue && Math.Abs(s.Chg1d.Value - 0.1) < 1e-6, $"1d chg {s.Chg1d}");
            Assert.Equal(100, s.Percentile1y!.Value, 0);     // last is the max
            Assert.True(s.ZScore1y > 0, "rising series ends above its mean");
            Assert.True(s.Max1y > s.Min1y);
        }

        [Fact]
        public void BpScale_SpreadSeries_ChangesNotDoubleScaled()
        {
            var start = new DateTime(2025, 6, 1);
            var pts = new List<HistPoint>();
            for (int i = 0; i < 300; i++) pts.Add(new HistPoint(start.AddDays(i), 10.0 + i * 0.5)); // bp levels
            var s = SeriesStats.Compute(pts, changeScale: 1.0);
            // last - yesterday = 0.5 bp with changeScale 1
            Assert.True(Math.Abs(s.Chg1d!.Value - 0.5) < 1e-6, $"{s.Chg1d}");
        }
    }

    public class QueryParserTests
    {
        private static (ConfigStore store, QueryParser parser) Build()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_q_cfg");
            Directory.CreateDirectory(dir);
            foreach (var cfg in new[] { TestConfigs.Usd(), TestConfigs.Gbp(), TestConfigs.Aud(), TestConfigs.Mxn() })
                File.WriteAllText(System.IO.Path.Combine(dir, cfg.Ccy.ToLowerInvariant() + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(cfg));
            var store = ConfigStore.LoadFromDirectory(dir);
            return (store, new QueryParser(new IndexRegistry(store)));
        }

        [Fact]
        public void ImmForward_Mid_Sofr()
        {
            var (_, p) = Build();
            var q = p.Parse("mid m31-5y sofr");
            Assert.Equal(QueryShape.Outright, q.Shape);
            Assert.Equal("USD", q.Target.Ccy);
            Assert.Equal(TargetKind.PrimaryOis, q.Target.Kind);
            Assert.Equal(Focus.Mid, q.Focus);
            Assert.Equal(StartKind.Imm, q.Main!.StartKind);
            Assert.Equal("5Y", TenorUtil.Format(q.Main.Tenor!));
            Assert.Equal(new Date(18, Month.June, 2031), q.Main.ImmDate);
        }

        [Fact]
        public void ProductForce_And_ExplicitFlag()
        {
            var (_, p) = Build();
            var q1 = p.Parse("usd ois 5y");
            Assert.Equal(TargetKind.PrimaryOis, q1.Target.Kind);
            Assert.True(q1.ProductExplicit);
            var q2 = p.Parse("usd 5y");
            Assert.False(q2.ProductExplicit);
            var q3 = p.Parse("mid m31-5y sofr"); // index alias also counts as explicit
            Assert.True(q3.ProductExplicit);
        }

        [Fact]
        public void Spread_And_Fly()
        {
            var (_, p) = Build();
            var sp = p.Parse("usd 2s10s");
            Assert.Equal(QueryShape.Spread, sp.Shape);
            Assert.Equal(2, sp.Legs.Count);

            var fly = p.Parse("usd 2s5s10s");
            Assert.Equal(QueryShape.Fly, fly.Shape);
            Assert.Equal(3, fly.Legs.Count);
        }

        [Fact]
        public void ImmDated_Spread_And_Fly()
        {
            var (_, p) = Build();
            Assert.True(ImmUtil.TryParse("u26", out var u26));

            // glued and separate forms are equivalent
            foreach (var txt in new[] { "usd u26 5s10s", "usd u26-5s10s" })
            {
                var q = p.Parse(txt);
                Assert.Equal(QueryShape.Spread, q.Shape);
                Assert.Equal(2, q.Legs.Count);
                Assert.All(q.Legs, l => Assert.Equal(StartKind.Imm, l.StartKind));
                Assert.All(q.Legs, l => Assert.Equal(u26, l.ImmDate));
                Assert.All(q.Legs, l => Assert.Equal("U26", l.ImmCode));
                Assert.Equal("5Y", TenorUtil.Format(q.Legs[0].Tenor!));
                Assert.Equal("10Y", TenorUtil.Format(q.Legs[1].Tenor!));
            }

            var fly = p.Parse("m31-5s10s20s gbp");
            Assert.Equal(QueryShape.Fly, fly.Shape);
            Assert.Equal(3, fly.Legs.Count);
            Assert.All(fly.Legs, l => Assert.Equal(StartKind.Imm, l.StartKind));
            Assert.All(fly.Legs, l => Assert.Equal(new Date(18, Month.June, 2031), l.ImmDate));
            Assert.Equal("20Y", TenorUtil.Format(fly.Legs[2].Tenor!));

            var z = p.Parse("gbp z26 5s30s");
            Assert.Equal(QueryShape.Spread, z.Shape);
            Assert.True(ImmUtil.TryParse("z26", out var z26));
            Assert.All(z.Legs, l => Assert.Equal(z26, l.ImmDate));
            Assert.Equal("30Y", TenorUtil.Format(z.Legs[1].Tenor!));

            // lone IMM + single tenor still folds into an outright (m31 5y)
            var o = p.Parse("usd m31 5y");
            Assert.Equal(QueryShape.Outright, o.Shape);
            Assert.Equal(StartKind.Imm, o.Main!.StartKind);
        }

        [Fact]
        public void ImmDated_Fly_Phrasings()
        {
            var (_, p) = Build();
            Assert.True(ImmUtil.TryParse("u26", out var u26));
            foreach (var txt in new[]
            {
                "aud u26 5s10s20s fly", "aud u26-5s10s20s", "u26 aud 5s10s20s",
                "aud 5s10s20s u26", "aud u26 start 5s10s20s", "aud imm u26 5s10s20s",
                "aud u26 - 5s10s20s", "aud u26 5s/10s/20s", "aud u26 fly 5s10s20s",
            })
            {
                var q = p.Parse(txt);
                Assert.Equal(QueryShape.Fly, q.Shape);
                Assert.Equal(3, q.Legs.Count);
                Assert.All(q.Legs, l => Assert.Equal(StartKind.Imm, l.StartKind));
                Assert.All(q.Legs, l => Assert.Equal(u26, l.ImmDate));
                Assert.Equal("5Y", TenorUtil.Format(q.Legs[0].Tenor!));
                Assert.Equal("20Y", TenorUtil.Format(q.Legs[2].Tenor!));
            }
            var c = p.Parse("aud u26 start 2s10s");
            Assert.Equal(QueryShape.Spread, c.Shape);
            Assert.All(c.Legs, l => Assert.Equal(StartKind.Imm, l.StartKind));
            var o = p.Parse("aud 5y from u26");
            Assert.Equal(QueryShape.Outright, o.Shape);
            Assert.Equal(StartKind.Imm, o.Main!.StartKind);
            Assert.Equal(u26, o.Main.ImmDate);
        }

        [Fact]
        public void ImmDated_MonthTenor_Spaced_Equals_Glued()
        {
            var (_, p) = Build();
            Assert.True(ImmUtil.TryParse("u26", out var u26));

            // "u26 3m" used to die with "Every leg needs a tenor": the month token was captured as
            // an ambiguous notional (m is also the millions suffix) and never offered to the
            // waiting IMM leg. Spaced and glued must be identical, incl. behind a 3s index tag.
            foreach (var (spaced, glued) in new[]
            {
                ("aud u26 3m", "aud u26-3m"),
                ("aud 3s u26 3m", "aud 3s u26-3m"),
                ("usd u26 18m", "usd u26-18m"),
            })
            {
                var qs = p.Parse(spaced);
                var qg = p.Parse(glued);
                Assert.Equal(QueryShape.Outright, qs.Shape);
                Assert.Equal(StartKind.Imm, qs.Main!.StartKind);
                Assert.Equal(u26, qs.Main.ImmDate);
                Assert.Equal(qg.Main!.ImmDate, qs.Main.ImmDate);
                Assert.Equal(qg.Main.ImmCode, qs.Main.ImmCode);
                Assert.Equal(TenorUtil.Format(qg.Main.Tenor!), TenorUtil.Format(qs.Main.Tenor!));
                // the month was NOT eaten as a size: an unsized trade gets the $25k desk default
                Assert.Equal(25e3, qs.Dv01Target);
            }
            Assert.Equal("3M", TenorUtil.Format(p.Parse("aud u26-3m").Main!.Tenor!));

            // one tenor broadcast across an IMM roll still works with a month tenor
            var roll = p.Parse("usd u26 z26 3m");
            Assert.Equal(QueryShape.Spread, roll.Shape);
            Assert.All(roll.Legs, l => Assert.Equal("3M", TenorUtil.Format(l.Tenor!)));

            // REGRESSION: once a tenor is present the ambiguous "Nm" stays a NOTIONAL
            var sized = p.Parse("usd u26 5y 20m");
            Assert.Equal("5Y", TenorUtil.Format(sized.Main!.Tenor!));
            Assert.Equal(20e6, sized.Notional);
            var big = p.Parse("usd u26 5y 100m"); // >36 was never ambiguous
            Assert.Equal("5Y", TenorUtil.Format(big.Main!.Tenor!));
            Assert.Equal(100e6, big.Notional);

            // REGRESSION: with no IMM leg, "3m 2y" is still months-then-tenor = forward start
            var fwd = p.Parse("usd 3m 2y");
            Assert.Equal(StartKind.Forward, fwd.Main!.StartKind);
            Assert.Equal("3M", TenorUtil.Format(fwd.Main.ForwardStart!));
            Assert.Equal("2Y", TenorUtil.Format(fwd.Main.Tenor!));
            // REGRESSION: notional after a plain tenor is unaffected
            var pn = p.Parse("usd 5y 25m");
            Assert.Equal("5Y", TenorUtil.Format(pn.Main!.Tenor!));
            Assert.Equal(25e6, pn.Notional);
        }

        [Fact]
        public void CustomDated_Fly_Phrasings()
        {
            var (_, p) = Build();
            var d = new Date(16, Month.September, 2026);
            foreach (var txt in new[]
            {
                "aud 16sep26 5s10s20s", "aud 16sep26-5s10s20s", "aud 16sep26 start 5s10s20s",
                "aud 16sep26 fly 5s10s20s", "aud 5s10s20s 16sep26", "aud 16sep26 5s/10s/20s",
            })
            {
                var q = p.Parse(txt);
                Assert.Equal(QueryShape.Fly, q.Shape);
                Assert.Equal(3, q.Legs.Count);
                Assert.All(q.Legs, l => Assert.Equal(StartKind.Date, l.StartKind));
                Assert.All(q.Legs, l => Assert.Equal(d, l.ExplicitStart));
            }
            var sp = p.Parse("aud 16sep26-2s10s");
            Assert.Equal(QueryShape.Spread, sp.Shape);
            Assert.All(sp.Legs, l => Assert.Equal(d, l.ExplicitStart));
            // spaced minus still means ENDING on the date
            var e = p.Parse("aud 16sep27 -1y");
            Assert.Equal(new Date(16, Month.September, 2027), e.Main!.ExplicitEnd);
            // ambiguous "5y - 10y" stays an error, not a silent forward/curve guess
            Assert.Throws<FormatException>(() => p.Parse("aud 5y - 10y"));
        }

        [Fact]
        public void Parser_Hardening_Batch()
        {
            var (_, p) = Build();
            var today = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);

            // bare "imm" = front IMM
            var fi = p.Parse("usd imm 5y");
            Assert.Equal(StartKind.Imm, fi.Main!.StartKind);
            Assert.True(fi.Main.ImmDate! > today);
            var fc = p.Parse("usd imm 2s10s");
            Assert.All(fc.Legs, l => Assert.Equal(StartKind.Imm, l.StartKind));

            // "vs" separates legs — a spread, never a 5y10y forward
            var vs = p.Parse("usd 5y vs 10y");
            Assert.Equal(QueryShape.Spread, vs.Shape);
            Assert.All(vs.Legs, l => Assert.Equal(StartKind.Spot, l.StartKind));

            // "1y fwd 2s10s" = forward-starting curve, not a fly
            var fw = p.Parse("usd 1y fwd 2s10s");
            Assert.Equal(QueryShape.Spread, fw.Shape);
            Assert.All(fw.Legs, l => Assert.Equal(StartKind.Forward, l.StartKind));
            Assert.Equal("1Y", TenorUtil.Format(fw.Legs[0].ForwardStart!));

            // tenor-before-date outright
            var td = p.Parse("usd 5y eff 16sep26");
            Assert.Equal(new Date(16, Month.September, 2026), td.Main!.ExplicitStart);

            // tokens after a trailing date keep working
            var tt = p.Parse("usd 2s5s 16sep26 100m roll");
            Assert.Equal(QueryShape.Spread, tt.Shape);
            Assert.All(tt.Legs, l => Assert.Equal(new Date(16, Month.September, 2026), l.ExplicitStart));
            Assert.Equal(100e6, tt.Notional);
            Assert.Equal(Focus.Roll, tt.Focus);

            // IMM rolls: broadcast one tenor, or pairwise
            var rl = p.Parse("usd u26 z26 5y");
            Assert.Equal(QueryShape.Spread, rl.Shape);
            Assert.All(rl.Legs, l => Assert.Equal("5Y", TenorUtil.Format(l.Tenor!)));
            var pr = p.Parse("usd u26 5y z26 10y");
            Assert.Equal("10Y", TenorUtil.Format(pr.Legs[1].Tenor!));

            // glued signed tenor completes an IMM leg
            var st = p.Parse("usd u26 +5y");
            Assert.Equal(StartKind.Imm, st.Main!.StartKind);
            Assert.Equal("5Y", TenorUtil.Format(st.Main.Tenor!));

            // "6s/3s" stays per-leg index tags, never a curve
            var ix = p.Parse("aud 5y2y 7y3y 6s/3s");
            Assert.Equal(2, ix.IndexOverrides!.Count);
            Assert.Equal(6, ix.IndexOverrides[0]!.length());

            // index alias + confirming ccy token keeps the alias product
            var al = p.Parse("aonia aud 5y");
            Assert.Equal(TargetKind.PrimaryOis, al.Target.Kind);

            // spaced dv01 + bare source
            var dv = p.Parse("usd 5y dv01 25k bgn");
            Assert.Equal(25000.0, dv.Dv01Target);
            Assert.Equal("BGN", dv.Source);

            // bare "k" amounts are USD dv01 by desk convention; fly dv01 defaults to WING risk
            var wn = p.Parse("usd 2s5s10s 50k wings");
            Assert.Equal(50e3, wn.Dv01Target);
            Assert.Equal("USD", wn.Dv01Ccy);
            Assert.True(wn.WingsSizing);
            var df = p.Parse("gbp 2s5s10s");
            Assert.Equal(25e3, df.Dv01Target); // unsized structures default to $25k wings
            Assert.Equal("USD", df.Dv01Ccy);
            Assert.True(df.WingsSizing);
            var bl = p.Parse("usd 2s5s10s 30k belly");
            Assert.True(bl.BellySizing);
            Assert.False(bl.WingsSizing);
            var se = p.Parse("usd 5y 25keur");
            Assert.Equal("EUR", se.Dv01Ccy);
            Assert.Equal(25e3, se.Dv01Target);

            // spaced month-forward: "3m 2y" = forward start, "5y 25m" = notional
            var mf = p.Parse("usd 3m 2y");
            Assert.Equal(StartKind.Forward, mf.Main!.StartKind);
            Assert.Equal("3M", TenorUtil.Format(mf.Main.ForwardStart!));
            Assert.Equal("2Y", TenorUtil.Format(mf.Main.Tenor!));
            var nt = p.Parse("usd 5y 25m");
            Assert.Equal(QueryShape.Outright, nt.Shape);
            Assert.Equal(StartKind.Spot, nt.Main!.StartKind);
            Assert.Equal(25e6, nt.Notional);
            var mfn = p.Parse("usd 6m 2y 50m");
            Assert.Equal(StartKind.Forward, mfn.Main!.StartKind);
            Assert.Equal(50e6, mfn.Notional);
        }

        [Fact]
        public void CustomDated_Spread_And_Fly()
        {
            var (_, p) = Build();
            var q = p.Parse("usd 20 jun 29 2s5s");
            Assert.Equal(QueryShape.Spread, q.Shape);
            Assert.Equal(2, q.Legs.Count);
            Assert.All(q.Legs, l => Assert.Equal(StartKind.Date, l.StartKind));
            Assert.All(q.Legs, l => Assert.Equal(new Date(20, Month.June, 2029), l.ExplicitStart));
            Assert.Equal("2Y", TenorUtil.Format(q.Legs[0].Tenor!));
            Assert.Equal("5Y", TenorUtil.Format(q.Legs[1].Tenor!));

            var r = p.Parse("usd 2s5s 20-jun-29"); // trailing date distributes too
            Assert.Equal(QueryShape.Spread, r.Shape);
            Assert.All(r.Legs, l => Assert.Equal(StartKind.Date, l.StartKind));
            Assert.All(r.Legs, l => Assert.Equal(new Date(20, Month.June, 2029), l.ExplicitStart));

            var fly = p.Parse("gbp 15-sep-27 2s5s10s");
            Assert.Equal(QueryShape.Fly, fly.Shape);
            Assert.Equal(3, fly.Legs.Count);
            Assert.All(fly.Legs, l => Assert.Equal(new Date(15, Month.September, 2027), l.ExplicitStart));

            // single-leg dated forms unchanged
            var o = p.Parse("aud 25-jun-31 +5y");
            Assert.Equal(QueryShape.Outright, o.Shape);
            Assert.Equal(StartKind.Date, o.Main!.StartKind);
            var dd = p.Parse("usd 25-jun-31 25-jun-36");
            Assert.Equal(QueryShape.Outright, dd.Shape);

            // GLUED date-tenor starts ON the date (like z27-1y) — was wrongly end-anchored
            var g2 = p.Parse("aud 15sep27-1y");
            Assert.Equal(StartKind.Date, g2.Main!.StartKind);
            Assert.Equal(new Date(15, Month.September, 2027), g2.Main.ExplicitStart);
            Assert.Equal("1Y", TenorUtil.Format(g2.Main.Tenor!));
            // SPACED minus still means "swap ENDING on the date"
            var ge = p.Parse("gbp 01/03/32 - 10y");
            Assert.Equal(new Date(1, Month.March, 2032), ge.Main!.ExplicitEnd);
        }

        [Fact]
        public void ForwardStart_And_Grid_And_Inflation()
        {
            var (_, p) = Build();
            var f = p.Parse("usd 5y5y");
            Assert.Equal(StartKind.Forward, f.Main!.StartKind);
            Assert.Equal("5Y", TenorUtil.Format(f.Main.ForwardStart!));

            var g = p.Parse("usd fwd");
            Assert.Equal(QueryShape.ForwardGrid, g.Shape);

            var cpi = p.Parse("5y us cpi");
            Assert.True(cpi.Target.IsLadder);
            Assert.Equal("CPI", cpi.Target.LadderName);
        }

        [Fact]
        public void Forward_Fly_Of_Forward_Legs()
        {
            var (_, p) = Build();
            var q = p.Parse("5y2y 7y3y 10y2y usd");
            Assert.Equal(QueryShape.Fly, q.Shape);
            Assert.Equal(3, q.Legs.Count);
            Assert.All(q.Legs, l => Assert.Equal(StartKind.Forward, l.StartKind));
            Assert.Equal("7Y", TenorUtil.Format(q.Legs[1].ForwardStart!));
            Assert.Equal("3Y", TenorUtil.Format(q.Legs[1].Tenor!));
        }

        [Theory]
        [InlineData("aud 25-jun-31 + 5y")]
        [InlineData("aud 25-jun-31 +5y")]
        [InlineData("aud 25jun31 5y")]
        [InlineData("aud 25/06/31 + 5y")]
        [InlineData("aud 25.06.2031 +5y")]
        [InlineData("aud 2031-06-25 + 5y")]
        [InlineData("aud 25 jun 31 + 5y")]
        public void Custom_Date_Start_Formats(string text)
        {
            var (_, p) = Build();
            var q = p.Parse(text);
            Assert.Equal(QueryShape.Outright, q.Shape);
            Assert.Equal(StartKind.Date, q.Main!.StartKind);
            Assert.Equal(new Date(25, Month.June, 2031), q.Main.ExplicitStart);
            Assert.Equal("5Y", TenorUtil.Format(q.Main.Tenor!));
        }

        [Fact]
        public void End_Anchored_Date()
        {
            var (_, p) = Build();
            var q = p.Parse("gbp 01/03/32 - 10y");
            Assert.Equal(StartKind.Date, q.Main!.StartKind);
            Assert.Equal(new Date(1, Month.March, 2032), q.Main.ExplicitEnd);
            Assert.Equal(new Date(1, Month.March, 2022), q.Main.ExplicitStart);
        }

        [Fact]
        public void Date_To_Date()
        {
            var (_, p) = Build();
            var q = p.Parse("usd 25-jun-31 25-jun-36");
            Assert.Equal(StartKind.Date, q.Main!.StartKind);
            Assert.Equal(new Date(25, Month.June, 2031), q.Main.ExplicitStart);
            Assert.Equal(new Date(25, Month.June, 2036), q.Main.ExplicitEnd);
        }

        [Fact]
        public void Dv01_Token()
        {
            var (_, p) = Build();
            var q = p.Parse("usd 5y dv01:25k");
            Assert.Equal(25_000, q.Dv01Target!.Value);
            var q2 = p.Parse("usd 5y 01=1m");
            Assert.Equal(1_000_000, q2.Dv01Target!.Value);
        }

        [Fact]
        public void Date_Without_Tenor_Fails_Loudly()
        {
            var (_, p) = Build();
            var ex = Assert.Throws<FormatException>(() => p.Parse("aud 25-jun-31"));
            Assert.Contains("tenor", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PerLeg_Notionals_With_X_Separators()
        {
            var (_, p) = Build();
            var q = p.Parse("usd 2s5s10s 33m x 50m x 20m");
            Assert.Equal(3, q.Legs.Count);
            Assert.Equal(new[] { 33e6, 50e6, 20e6 }, q.LegNotionals!.ToArray());

            var q2 = p.Parse("usd 2s5s10s 33mio 50mio 20mio");
            Assert.Equal(new[] { 33e6, 50e6, 20e6 }, q2.LegNotionals!.ToArray());
        }

        [Fact]
        public void PerLeg_Dv01s_And_Wings()
        {
            var (_, p) = Build();
            var q = p.Parse("usd 2s5s10s $20k x $40k x $20k");
            Assert.Equal(new[] { 20e3, 40e3, 20e3 }, q.LegDv01s!.ToArray());

            var w = p.Parse("usd 2s5s10s $20k wings");
            Assert.Equal(20e3, w.Dv01Target!.Value);
            Assert.True(w.WingsSizing);
        }

        [Fact]
        public void Bare_Month_Value_Still_A_Tenor_When_Alone()
        {
            var (_, p) = Build();
            var q = p.Parse("usd 18m");
            Assert.Equal(QueryShape.Outright, q.Shape);
            Assert.Equal(18, q.Main!.Tenor!.length());
            Assert.Null(q.LegNotionals);

            var q2 = p.Parse("usd 5y 20m"); // 5y swap, 20 million
            Assert.Equal(20e6, q2.Notional);
        }

        [Fact]
        public void Aud_Index_Tokens()
        {
            var (_, p) = Build();
            var q = p.Parse("aud 2y qq");
            Assert.Equal(3, q.IndexOverrides![0]!.length());

            var f = p.Parse("aud 2s5s10s qq/ss/ss");
            Assert.Equal(3, f.IndexOverrides!.Count);
            Assert.Equal(3, f.IndexOverrides[0]!.length());
            Assert.Equal(6, f.IndexOverrides[1]!.length());

            var s = p.Parse("aud 2y s/s");
            Assert.Equal(6, s.IndexOverrides![0]!.length());
        }

        [Fact]
        public void Brl_Dated_Codes()
        {
            var (_, p) = Build();
            foreach (var (txt, code) in new[] { ("usd f31", "F31"), ("usd jan27", "F27"), ("usd jul30", "N30"), ("usd n32", "N32") })
            {
                var q = p.Parse(txt);
                Assert.Equal(code, q.DatedCode);
            }
        }

        [Fact]
        public void Digit_Pair_Legs_And_Fly_Keyword()
        {
            var (_, p) = Build();
            var q = p.Parse("52 73 102 usd fly");
            Assert.Equal(QueryShape.Fly, q.Shape);
            Assert.Equal(3, q.Legs.Count);
            Assert.Equal("5Y", TenorUtil.Format(q.Legs[0].ForwardStart!));
            Assert.Equal("2Y", TenorUtil.Format(q.Legs[0].Tenor!));
            Assert.Equal("10Y", TenorUtil.Format(q.Legs[2].ForwardStart!));
            Assert.Equal("2Y", TenorUtil.Format(q.Legs[2].Tenor!));
            var q2 = p.Parse("usd 1010 curve 55");
            Assert.Equal(2, q2.Legs.Count);
            Assert.Equal("10Y", TenorUtil.Format(q2.Legs[0].ForwardStart!));
        }

        [Fact]
        public void Dv01_Currency_Tags()
        {
            var (_, p) = Build();
            Assert.Equal("USD", p.Parse("gbp 5y $25k").Dv01Ccy);
            Assert.Equal("USD", p.Parse("gbp 5y dv01:25k").Dv01Ccy);       // no sign -> USD default
            Assert.Equal("JPY", p.Parse("gbp 5y ¥25k").Dv01Ccy);
            Assert.Equal("JPY", p.Parse("gbp 2s5s10s jpy25k wings").Dv01Ccy);
            Assert.Equal("EUR", p.Parse("gbp 5y €1m").Dv01Ccy);
            Assert.Equal(25_000, p.Parse("gbp 5y ¥25k").Dv01Target!.Value);
            Assert.Throws<FormatException>(() => p.Parse("usd 2s10s $10k x ¥10k"));
        }

        [Fact]
        public void Custom_Weights()
        {
            var (_, p) = Build();
            var q = p.Parse("usd 2s10s w:1/1.5");
            Assert.Equal(new List<double> { 1, 1.5 }, q.Weights);
            var q2 = p.Parse("usd 2s5s10s w:-1/+2.5/-1");
            Assert.Equal(new List<double> { -1, 2.5, -1 }, q2.Weights);
            Assert.Throws<FormatException>(() => p.Parse("usd 2s10s w:1/2/1"));
        }
    }

    public class HistoryFilterTests
    {
        [Fact]
        public void Spike_Removed_Trend_Kept()
        {
            var start = new DateTime(2025, 6, 2);
            var pts = new List<HistPoint>();
            for (int i = 0; i < 60; i++)
            {
                double v = 4.0 + i * 0.002;             // gentle trend
                if (i == 30) v = 5.5;                    // bad print (+150bp for one day)
                pts.Add(new HistPoint(start.AddDays(i), v));
            }
            var f = HistoryFilter.Despike(pts);
            Assert.True(Math.Abs(f[30].Value - 4.06) < 0.05, $"spike not removed: {f[30].Value}");
            Assert.Equal(pts[10].Value, f[10].Value, 10);  // clean points untouched

            // a genuine persistent 40bp gap must NOT be smoothed away
            var jump = new List<HistPoint>();
            for (int i = 0; i < 60; i++)
                jump.Add(new HistPoint(start.AddDays(i), i < 30 ? 4.0 : 4.4));
            var fj = HistoryFilter.Despike(jump);
            Assert.Equal(4.4, fj[30].Value, 6);
            Assert.Equal(4.0, fj[29].Value, 6);
        }
    }

    public class ForwardGridTests
    {
        private static readonly Date AsOf = new(8, Month.July, 2026);

        [Fact]
        public void FlatCurve_AllForwardsEqualSpot()
        {
            var cfg = TestConfigs.Usd();
            var snap = new RatesSnapshot();
            foreach (var pil in cfg.Ois!.Curve)
                snap.Update(ConfigStore.ResolveTicker(pil.Ticker, cfg.DefaultSource), null, null, 4.0);
            var curves = CurveBuilder.Build(cfg, cfg.DefaultSource, snap, AsOf);

            var grid = ForwardGrid.Build(curves, ProductKind.OIS,
                starts: new[] { "0Y", "2Y", "5Y" }, tenors: new[] { "2Y", "5Y" });
            foreach (var c in grid.Cells.Where(c => c.Ok))
                Assert.True(Math.Abs(c.RatePct - 4.0) < 0.02, $"{c.Start}x{c.Tenor} = {c.RatePct}");

            // spot 5Y forward rate equals the outright par
            double f = ForwardGrid.ForwardRate(curves, ProductKind.OIS, null, TenorUtil.Parse("5Y"));
            Assert.True(Math.Abs(f - 4.0) < 0.02);
        }
    }

    /// <summary>Ticket sizing: the Cashflows/Risk-ladder tabs go through SpecFromQuery, which used to
    /// build its spec from ParsedQuery's flat 10mm default and so disagreed with the DV01-sized
    /// headline tiles for single-leg queries. Runs off SYNTHETIC quotes (no Bloomberg needed).</summary>
    public class SpecSizingTests
    {
        private static readonly CurrencyConfig[] Cfgs =
            { TestConfigs.Usd(), TestConfigs.Gbp(), TestConfigs.Aud(), TestConfigs.Mxn() };

        /// <summary>ConfigStore from TestConfigs (same shape as QueryParserTests.Build) plus a flat
        /// 4% synthetic snapshot for every curve pillar, wired into a live PricingService.</summary>
        private static RateDesk.Core.PricingService Build()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_sizing_cfg");
            Directory.CreateDirectory(dir);
            foreach (var cfg in Cfgs)
                File.WriteAllText(System.IO.Path.Combine(dir, cfg.Ccy.ToLowerInvariant() + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(cfg));
            var store = ConfigStore.LoadFromDirectory(dir);

            var snap = new RatesSnapshot();
            foreach (var cfg in Cfgs)
                foreach (var p in (cfg.Ois?.Curve ?? Enumerable.Empty<PillarDef>())
                         .Concat(cfg.Irs?.Curve ?? Enumerable.Empty<PillarDef>()))
                    snap.Update(ConfigStore.ResolveTicker(p.Ticker, cfg.DefaultSource), null, null, 4.0);
            return new RateDesk.Core.PricingService(store, snap);
        }

        [Fact]
        public void Unsized_Outright_Ticket_Spec_Is_Dv01_Sized_Not_Flat_10mm()
        {
            var svc = Build();
            var pq = svc.ParseQuery("usd 5y");

            // the parser defaults an unsized query to the desk dv01 but leaves Notional on its
            // legacy flat default — SpecFromQuery used to take the latter
            Assert.Equal(RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd, pq.Dv01Target!.Value, 6);
            Assert.Equal(10_000_000, pq.Notional, 6);

            var spec = svc.SpecFromQuery(pq);

            // density of the very same 1mm leg -> the notional that carries the default dv01
            var curves = svc.GetCurves("USD");
            double density = Pricer.Price(
                new TradeSpec { Ccy = "USD", Product = ProductKind.OIS, Tenor = TenorUtil.Parse("5Y"), Notional = 1_000_000 },
                curves).Annuity01;
            double expected = RateDesk.Core.Risk.RiskSizer.RoundNotional(
                RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd / density * 1_000_000.0);

            Assert.True(Math.Abs(spec.Notional - expected) < 1.0,
                $"ticket notional {spec.Notional:N0} should be the $25k-dv01 size {expected:N0}");
            Assert.Equal(0.0, spec.Notional % RateDesk.Core.Risk.RiskSizer.NotionalLot, 6); // tradeable lot
            Assert.True(Math.Abs(spec.Notional - 10_000_000) > 1_000_000,
                $"ticket notional {spec.Notional:N0} is still the flat 10mm default");

            // the ticket prices the ROUND LOT's risk: near the $25k target, deliberately not on it
            double halfLotDv01 = density * RateDesk.Core.Risk.RiskSizer.NotionalLot / 2 / 1_000_000.0;
            var priced = svc.PriceQuery(pq, withLadder: false);
            Assert.InRange(priced.Annuity01,
                RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd - halfLotDv01,
                RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd + halfLotDv01);
            Assert.True(Math.Abs(priced.Spec.Notional - expected) < 1.0);
        }

        [Fact]
        public void Explicit_Sizing_Still_Wins_Over_The_Dv01_Default()
        {
            var svc = Build();

            // a typed notional: parser puts it on Notional and leaves Dv01Target null
            var typed = svc.ParseQuery("usd 5y 50mm");
            Assert.Null(typed.Dv01Target);
            Assert.Equal(50_000_000, svc.SpecFromQuery(typed).Notional, 6);

            // a typed ODD lot rounds to a dealable one — and the ticket must land on the SAME lot the
            // headline shows, or the two panes disagree about the trade again
            var odd = svc.ParseQuery("usd 5y 16.47mm");
            Assert.Equal(16_500_000, svc.SpecFromQuery(odd).Notional, 6);
            Assert.Equal(16_500_000, svc.Analyze(svc.ParseQuery("usd 5y 16.47mm")).Legs[0].Notional, 6);

            // explicit per-leg notional (the blotter's exact-position channel) beats any dv01 target
            // AND is dealt exactly — a real 16,470,219 position must not be re-rounded
            var over = svc.ParseQuery("usd 5y");
            over.LegNotionals = new List<double> { 30_000_000 };
            Assert.Equal(30_000_000, svc.SpecFromQuery(over).Notional, 6);
            var exact = svc.ParseQuery("usd 5y");
            exact.LegNotionals = new List<double> { 16_470_219 };
            Assert.Equal(16_470_219, svc.SpecFromQuery(exact).Notional, 6);
            Assert.Equal(16_470_219, svc.Analyze(exact).Legs[0].Notional, 6);

            // explicit dv01 target sizes to THAT dv01, not the desk default — landing on a round lot,
            // so the realised risk sits within half a lot of 50k rather than exactly on it
            var dv = svc.ParseQuery("usd 5y $50k");
            var priced = svc.PriceQuery(dv, withLadder: false);
            Assert.Equal(0.0, priced.Spec.Notional % RateDesk.Core.Risk.RiskSizer.NotionalLot, 6);
            double density = priced.Annuity01 / (priced.Spec.Notional / 1_000_000.0);
            double halfLotDv01 = density * RateDesk.Core.Risk.RiskSizer.NotionalLot / 2 / 1_000_000.0;
            Assert.InRange(priced.Annuity01, 50_000 - halfLotDv01, 50_000 + halfLotDv01);
        }
    }

    /// <summary>Stale-price flagging (§9). Generalised, not special-cased to NZD: width is judged
    /// relative to the currency's OWN curve so each market self-calibrates, because a 2bp GBP spread is
    /// alarming while a 2bp NZD spread is its best case.</summary>
    public class StalenessTests
    {
        private static (string, string, QuoteData?) Q(string label, double bid, double ask, double? ageMin = null)
            => ($"{label} tk", label, new QuoteData { Bid = bid, Ask = ask, AgeMinutes = ageMin });

        [Fact]
        public void A_Wide_Quote_Is_Flagged_Against_Its_Own_Curves_Median()
        {
            // a tight curve with one 4bp outlier: 0.4/0.4/0.4/0.4/4.0 bp
            var quotes = new List<(string, string, QuoteData?)>
            {
                Q("2Y", 4.000, 4.004), Q("5Y", 4.100, 4.104), Q("10Y", 4.200, 4.204),
                Q("20Y", 4.300, 4.304), Q("30Y", 4.300, 4.340),
            };
            var flagged = Staleness.Assess(quotes, staleMinutes: 120);
            Assert.Single(flagged);
            Assert.Equal("30Y", flagged[0].Label);
            Assert.Contains("median", flagged[0].Reason);
        }

        [Fact]
        public void A_Uniformly_Wide_Curve_Flags_Nothing_On_Width()
        {
            // every NZD quote is ~4bp wide: that is the market, not an anomaly. A global bp threshold
            // would light up the whole curve here; a relative one correctly says nothing.
            var quotes = new List<(string, string, QuoteData?)>
            {
                Q("2Y", 4.00, 4.04), Q("5Y", 4.10, 4.14), Q("10Y", 4.20, 4.24), Q("30Y", 4.30, 4.34),
            };
            Assert.Empty(Staleness.Assess(quotes, staleMinutes: 120));
        }

        [Fact]
        public void Tight_Curves_Are_Not_Flagged_For_Relative_Noise()
        {
            // 0.1bp vs 0.3bp is 3x but both are rounding — the absolute floor suppresses it
            var quotes = new List<(string, string, QuoteData?)>
            {
                Q("2Y", 4.000, 4.001), Q("5Y", 4.100, 4.101), Q("10Y", 4.200, 4.203),
            };
            Assert.Empty(Staleness.Assess(quotes, staleMinutes: 120));
        }

        [Fact]
        public void Age_Flags_Independently_Of_Width()
        {
            var quotes = new List<(string, string, QuoteData?)>
            {
                Q("2Y", 4.000, 4.004, 5), Q("5Y", 4.100, 4.104, 400), Q("10Y", 4.200, 4.204, 3),
            };
            var flagged = Staleness.Assess(quotes, staleMinutes: 120);
            Assert.Single(flagged);
            Assert.Equal("5Y", flagged[0].Label);
            Assert.Contains("last update", flagged[0].Reason);
            Assert.Contains("6.7h", flagged[0].Reason);

            // and the age test can be turned off per currency without losing the width test
            Assert.Empty(Staleness.Assess(quotes, staleMinutes: 0));
        }

        [Fact]
        public void Missing_Quotes_And_Thin_Curves_Are_Safe()
        {
            Assert.Empty(Staleness.Assess(new List<(string, string, QuoteData?)>(), 120));
            // fewer than 3 widths = no meaningful median, so no width verdict is offered
            Assert.Empty(Staleness.Assess(new List<(string, string, QuoteData?)>
            {
                Q("2Y", 4.00, 4.40), ("gap tk", "5Y", null),
            }, 120));
        }
    }

    /// <summary>Meeting grammar (§2/§5). "usd july fomc $25k" used to die with a misleading
    /// "Cannot parse 'july'": the month was never the problem — any token that was neither a month nor
    /// a CB alias failed the WHOLE meeting parse, after which QueryParser (which has no CB vocabulary)
    /// blamed the month. No tests existed for this grammar at all.</summary>
    public class MeetingQueryTests
    {
        private static bool Try(string text, out string run, out List<(int Month, int? Year)> months,
            out string ccy, out Period? tenor)
        {
            var toks = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return MeetingQuery.TryParse(toks, out run, out months, out ccy, out tenor);
        }

        [Fact]
        public void Month_Words_Parse_Long_Or_Short()
        {
            foreach (var text in new[] { "jul fomc", "july fomc", "jul fed", "usd jul fomc" })
            {
                Assert.True(Try(text, out var run, out var months, out var ccy, out var tenor), text);
                Assert.Equal("FOMC", run);
                Assert.Equal("USD", ccy);
                Assert.Single(months);
                Assert.Equal(7, months[0].Month);
                Assert.Null(tenor);
            }
        }

        [Fact]
        public void Four_Grammar_Tokens_Plus_A_Tenor_Still_Fits()
        {
            // "jul sep dec boe" was already at the old hard cap of 4 tokens, leaving no headroom
            Assert.True(Try("jul sep dec boe", out var run, out var months, out _, out var tenor));
            Assert.Equal("MPC", run);
            Assert.Equal(3, months.Count);
            Assert.Null(tenor);
        }

        [Fact]
        public void A_Trailing_Tenor_Makes_It_An_Anchored_Swap()
        {
            Assert.True(Try("usd jul fomc 5y", out var run, out var months, out _, out var tenor));
            Assert.Equal("FOMC", run);
            Assert.Single(months);
            Assert.NotNull(tenor);
            Assert.Equal(60, (int)TenorUtil.ApproxMonths(tenor!));

            // one anchor by definition — a spread of anchored swaps is a semantic error, not a misparse
            Assert.Throws<FormatException>(() => Try("jul sep fomc 5y", out _, out _, out _, out _));
        }

        [Fact]
        public void Sizing_Tokens_Must_Be_Stripped_First()
        {
            // unstripped, a size token fails the whole parse — this is exactly the old bug
            Assert.False(Try("usd july fomc $25k", out _, out _, out _, out _));

            // stripped by the shared extractor, it parses AND the size survives
            var req = SizingTokens.Extract("usd july fomc $25k".Split(' '), out var grammar);
            Assert.Equal(25_000, req.Dv01);
            Assert.True(MeetingQuery.TryParse(grammar, out var run, out var months, out _, out _));
            Assert.Equal("FOMC", run);
            Assert.Equal(7, months[0].Month);
        }

        [Fact]
        public void Non_Meeting_Queries_Are_Still_Rejected()
        {
            Assert.False(Try("usd 5y", out _, out _, out _, out _));
            Assert.False(Try("usd 2s5s10s", out _, out _, out _, out _));
            Assert.False(Try("aud 3x6 fra", out _, out _, out _, out _));
            Assert.False(Try("fomc", out _, out _, out _, out _));          // a month is required
        }
    }

    /// <summary>FRA in the analytics query bar (§6). It only ever worked via the ticket bar; QueryParser
    /// had no FRA concept. Two market shapes: rolling AxB (AUD/CZK/HUF/NZD/PLN) and IMM-dated quarterly
    /// (SEK, and NOK/DKK whose strip lives in FrontFromOis). Runs off SYNTHETIC quotes.</summary>
    public class FraQueryTests
    {
        private static readonly CurrencyConfig[] Cfgs =
            { TestConfigs.Usd(), TestConfigs.Gbp(), TestConfigs.AudWithFras(), TestConfigs.Sek(), TestConfigs.Nok() };

        private static ConfigStore Store()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_fra_cfg");
            Directory.CreateDirectory(dir);
            foreach (var cfg in Cfgs)
                File.WriteAllText(System.IO.Path.Combine(dir, cfg.Ccy.ToLowerInvariant() + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(cfg));
            return ConfigStore.LoadFromDirectory(dir);
        }

        private static QueryParser Parser() => new(new IndexRegistry(Store()));

        [Fact]
        public void Rolling_Fra_Parses_Either_Word_Order()
        {
            var p = Parser();
            foreach (var text in new[] { "aud 3x6 fra", "aud fra 3x6" })
            {
                var q = p.Parse(text);
                Assert.Single(q.Legs);
                Assert.True(q.Main!.IsFra);
                Assert.Equal(3, q.Main.FraStartMonths);
                Assert.Equal(6, q.Main.FraEndMonths);
                Assert.Equal(ProductKind.FRA, q.Target.Product);
                Assert.Equal("3x6 FRA", q.Main.Describe());
            }
        }

        [Fact]
        public void Imm_Fra_Keeps_Its_Contract_Date_And_Needs_No_Tenor()
        {
            var q = Parser().Parse("sek u26 fra");
            Assert.Single(q.Legs);
            Assert.True(q.Main!.IsFra);
            Assert.Equal(StartKind.Imm, q.Main.StartKind);
            Assert.Equal("U26", q.Main.ImmCode);
            Assert.NotNull(q.Main.ImmDate);
            Assert.Null(q.Main.Tenor);          // the "every leg needs a tenor" guard must exempt it
            Assert.Equal("U26 FRA", q.Main.Describe());
        }

        [Fact]
        public void The_Fra_Marker_Is_Mandatory_And_Errors_Are_Actionable()
        {
            var p = Parser();
            // a bare IMM code is already valid grammar (a tenor-less swap start), which is WHY the
            // marker has to be mandatory
            Assert.False(p.Parse("aud u26 5y").Main!.IsFra);
            Assert.Throws<FormatException>(() => p.Parse("aud 3x6"));        // no marker
            Assert.Throws<FormatException>(() => p.Parse("aud 6x3 fra"));    // end before start
            Assert.Throws<FormatException>(() => p.Parse("aud fra"));        // marker with nothing to price
            Assert.Throws<FormatException>(() => p.Parse("aud 5y 3x6 fra")); // a FRA is priced alone
        }

        [Fact]
        public void Regression_Months_Before_A_Tenor_Still_Forward_Starts()
        {
            // the AxB token must not have disturbed the months-vs-millions machinery
            var q = Parser().Parse("usd 3m 2y");
            Assert.Equal(StartKind.Forward, q.Main!.StartKind);
            Assert.Equal(3, (int)TenorUtil.ApproxMonths(q.Main.ForwardStart!));
            Assert.Equal(24, (int)TenorUtil.ApproxMonths(q.Main.Tenor!));
            Assert.False(q.Main.IsFra);
        }

        [Fact]
        public void Fra_Shape_Classifiers_Come_From_The_Config()
        {
            var store = Store();
            Assert.True(CurveBuilder.HasRollingFraPillars(store.Get("AUD")));
            Assert.False(CurveBuilder.HasImmFraStrip(store.Get("AUD")));

            Assert.True(CurveBuilder.HasImmFraStrip(store.Get("SEK")));
            Assert.False(CurveBuilder.HasRollingFraPillars(store.Get("SEK")));
            Assert.Equal("3M", CurveBuilder.ImmFraIndexTenor(store.Get("SEK")));

            // NOK has no FRA pillars at all — the strip is in FrontFromOis, and its period is 3M even
            // though the only IRS leg is 6M NIBOR. Reading the LEG here would price a 6M contract.
            Assert.True(CurveBuilder.HasImmFraStrip(store.Get("NOK")));
            Assert.False(CurveBuilder.HasRollingFraPillars(store.Get("NOK")));
            Assert.Equal("3M", CurveBuilder.ImmFraIndexTenor(store.Get("NOK")));
            Assert.Equal("6M", store.Get("NOK").Irs!.Legs[0].FloatTenor);

            // GBP has no IRS curve at all, so neither shape
            Assert.False(CurveBuilder.HasRollingFraPillars(store.Get("GBP")));
            Assert.False(CurveBuilder.HasImmFraStrip(store.Get("GBP")));
        }

        [Fact]
        public void Imm_Fra_Prices_As_A_Real_Single_Period_Swap()
        {
            var store = Store();
            var cfg = store.Get("NOK");
            var snap = new RatesSnapshot();
            foreach (var p in cfg.Irs!.Curve)
                snap.Update(ConfigStore.ResolveTicker(p.Ticker, cfg.DefaultSource), null, null, 4.0);
            var curves = CurveBuilder.Build(cfg, cfg.DefaultSource, snap, AsOf);

            var imm = ImmUtil.TryParse("z26", out var immDate) ? immDate : null;
            Assert.NotNull(imm);
            var spec = new TradeSpec
            {
                Ccy = "NOK", Product = ProductKind.FRA, StartKind = StartKind.Imm,
                ImmDate = imm, ImmCode = "Z26", Notional = 1_000_000,
            };
            var priced = Pricer.Price(spec, curves);

            // a real instrument: par rate, NPV and a non-zero annuity, not a bare forwardRate()
            Assert.Equal(imm, priced.Effective);
            Assert.True(priced.Annuity01 > 0);
            Assert.InRange(priced.ParRatePct, 3.0, 5.0);
            // the period is the STRIP's 3M, not the 6M swap leg
            double months = (priced.Maturity.serialNumber() - priced.Effective.serialNumber()) / 30.44;
            Assert.InRange(months, 2.7, 3.3);
        }

        private static readonly Date AsOf = new(28, Month.July, 2026);
    }

    /// <summary>Path C: the ticket grammar had no dv01 concept at all, so "price" always used the flat
    /// 10mm default and reported about a fifth of the risk the analytics bar showed for the same trade.
    /// Runs off SYNTHETIC quotes (no Bloomberg needed).</summary>
    public class CommandSizingTests
    {
        private static readonly CurrencyConfig[] Cfgs =
            { TestConfigs.Usd(), TestConfigs.Gbp(), TestConfigs.Aud(), TestConfigs.Mxn() };

        private static RateDesk.Core.PricingService Build()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_cmdsizing_cfg");
            Directory.CreateDirectory(dir);
            foreach (var cfg in Cfgs)
                File.WriteAllText(System.IO.Path.Combine(dir, cfg.Ccy.ToLowerInvariant() + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(cfg));
            var snap = new RatesSnapshot();
            foreach (var cfg in Cfgs)
                foreach (var p in (cfg.Ois?.Curve ?? Enumerable.Empty<PillarDef>())
                         .Concat(cfg.Irs?.Curve ?? Enumerable.Empty<PillarDef>()))
                    snap.Update(ConfigStore.ResolveTicker(p.Ticker, cfg.DefaultSource), null, null, 4.0);
            return new RateDesk.Core.PricingService(ConfigStore.LoadFromDirectory(dir), snap);
        }

        private static ConfigStore Store()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_cmdsizing_cfg");
            Directory.CreateDirectory(dir);
            foreach (var cfg in Cfgs)
                File.WriteAllText(System.IO.Path.Combine(dir, cfg.Ccy.ToLowerInvariant() + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(cfg));
            return ConfigStore.LoadFromDirectory(dir);
        }

        [Fact]
        public void Unsized_Price_Command_Targets_The_Desk_Dv01_Not_Flat_10mm()
        {
            var svc = Build();
            var r = svc.PriceCommand("usd 5y", withLadder: false);

            double density = r.Annuity01 / (r.Spec.Notional / 1_000_000.0);
            double halfLot = density * RateDesk.Core.Risk.RiskSizer.NotionalLot / 2 / 1e6;
            Assert.InRange(r.Annuity01,
                RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd - halfLot,
                RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd + halfLot);
            Assert.Equal(0.0, r.Spec.Notional % RateDesk.Core.Risk.RiskSizer.NotionalLot, 6);
            Assert.True(Math.Abs(r.Spec.Notional - 10_000_000) > 1_000_000,
                $"notional {r.Spec.Notional:N0} is still the flat 10mm default");
        }

        [Fact]
        public void Typed_Notional_And_Dv01_Both_Reach_The_Price()
        {
            var svc = Build();
            // a typed notional is dealt EXACTLY — never re-rounded to a 500k lot. (This grammar has
            // always required a suffix, so an odd size is written "16.47mm", not "16470000".)
            Assert.Equal(16_470_000, svc.PriceCommand("usd 5y 16.47mm", withLadder: false).Spec.Notional, 6);
            Assert.Equal(50_000_000, svc.PriceCommand("usd 5y 50mm", withLadder: false).Spec.Notional, 6);

            var dv = svc.PriceCommand("usd 5y dv01:50k", withLadder: false);
            double density = dv.Annuity01 / (dv.Spec.Notional / 1_000_000.0);
            double halfLot = density * RateDesk.Core.Risk.RiskSizer.NotionalLot / 2 / 1e6;
            Assert.InRange(dv.Annuity01, 50_000 - halfLot, 50_000 + halfLot);
        }

        [Fact]
        public void Ticket_Grammar_Keeps_Bare_k_As_A_Notional()
        {
            // deliberate divergence from the analytics bar: "gbp 10y 250k" has always meant a 250,000
            // NOTIONAL here, and silently rereading it as a 250,000 dv01 would turn a tiny trade into
            // a huge one. Risk needs an explicit marker on this grammar.
            var spec = RateDesk.Core.Trades.CommandParser.Parse("gbp 10y 250k", Store());
            Assert.Equal(250_000, spec.ExplicitNotional);
            Assert.Null(spec.Dv01Target);

            foreach (var marked in new[] { "gbp 10y dv01:250k", "gbp 10y dv01 250k", "gbp 10y $250k" })
            {
                var s = RateDesk.Core.Trades.CommandParser.Parse(marked, Store());
                Assert.Equal(250_000, s.Dv01Target);
                Assert.Equal("USD", s.Dv01Ccy);
                Assert.Null(s.ExplicitNotional);
            }
            Assert.Equal("EUR", RateDesk.Core.Trades.CommandParser.Parse("gbp 10y €25k", Store()).Dv01Ccy);
        }

        [Fact]
        public void Notional_And_Dv01_Together_Is_An_Error()
        {
            Assert.Throws<FormatException>(() =>
                RateDesk.Core.Trades.CommandParser.Parse("usd 5y 50mm dv01:25k", Store()));
            Assert.Throws<FormatException>(() =>
                RateDesk.Core.Trades.CommandParser.Parse("usd 5y dv01", Store()));
        }

        [Fact]
        public void SizingTokens_Extract_Strips_Sizes_And_Keeps_Grammar_Order()
        {
            var req = RateDesk.Core.Query.SizingTokens.Extract(
                new[] { "usd", "jul", "$25k", "fomc" }, out var rest);
            Assert.Equal(25_000, req.Dv01);
            Assert.Equal("USD", req.Dv01Ccy);
            Assert.Equal(new[] { "usd", "jul", "fomc" }, rest);

            var n = RateDesk.Core.Query.SizingTokens.Extract(new[] { "jul", "boe", "25mm" }, out var rest2);
            Assert.Equal(25_000_000, n.Notional);
            Assert.Null(n.Dv01);
            Assert.Equal(new[] { "jul", "boe" }, rest2);

            // a month word is never a dv01 currency tag
            Assert.Equal(new[] { "may" },
                RateDesk.Core.Query.SizingTokens.Extract(new[] { "may" }, out _) is { Any: false }
                    ? new[] { "may" } : Array.Empty<string>());

            // spaced form, and the honest error when the size never arrives
            Assert.Equal(25_000, RateDesk.Core.Query.SizingTokens
                .Extract(new[] { "dv01", "25k" }, out _).Dv01);
            Assert.Throws<FormatException>(() =>
                RateDesk.Core.Query.SizingTokens.Extract(new[] { "jul", "dv01" }, out _));
        }
    }

    /// <summary>Zero-coupon inflation swap (ZCIIS) DV01. The instrument settles as ONE net cashflow at
    /// maturity, so its NPV carries a single discount factor off the nominal OIS curve and
    /// dNPV/dK = -N.DF(T).T.(1+K)^(T-1). The code used to omit DF(T) entirely, overstating the risk by
    /// 23% at 5y and 267% at 30y on live levels. See ZcInflationDensityPerMm for the sourced
    /// derivation. Runs off SYNTHETIC quotes (no Bloomberg needed).</summary>
    public class InflationDv01Tests
    {
        /// <summary>USD fixture (SOFR OIS 1M-30Y + a CPI ladder) with a flat 4% nominal curve and the
        /// CPI ladder marked at cpiPct.</summary>
        private static RateDesk.Core.PricingService Build(double cpiPct)
        {
            var cfg = TestConfigs.Usd();
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_infl_cfg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "usd.json"),
                System.Text.Json.JsonSerializer.Serialize(cfg));
            var store = ConfigStore.LoadFromDirectory(dir);

            var snap = new RatesSnapshot();
            foreach (var p in cfg.Ois!.Curve)
                snap.Update(ConfigStore.ResolveTicker(p.Ticker, cfg.DefaultSource), null, null, 4.0);
            foreach (var p in cfg.Ladders[0].Pillars)
                snap.Update(ConfigStore.ResolveTicker(p.Ticker, ""), cpiPct - 0.02, cpiPct + 0.02, cpiPct);
            return new RateDesk.Core.PricingService(store, snap);
        }

        /// <summary>Undiscounted per-mm-per-bp density — what the code used to report.</summary>
        private static double Undiscounted(double tYears, double ratePct) =>
            100.0 * tYears * Math.Pow(1 + ratePct / 100.0, tYears - 1);

        [Theory]
        [InlineData("usd cpi 5y", 5.0)]
        [InlineData("usd cpi 10y", 10.0)]
        public void Zc_Inflation_Dv01_Is_Discounted_On_The_Nominal_Ois_Curve(string query, double tYears)
        {
            const double k = 2.5;
            var svc = Build(k);
            var r = svc.Analyze(query);

            var curves = svc.GetCurves("USD");
            double df = curves.Ois!.discount(r.Maturity!, true);
            Assert.InRange(df, 0.5, 1.0);                      // a real DF off the 4% curve, not 1.0

            double expected = Undiscounted(tYears, k) * df * r.Legs[0].Notional / 1_000_000.0;
            Assert.Equal(expected, r.Dv01!.Value, 6);
            Assert.Equal(Undiscounted(tYears, k) * df, r.Legs[0].DensityPerMm, 9);

            // the whole point: materially below the old undiscounted figure
            Assert.True(r.Dv01!.Value < Undiscounted(tYears, k) * r.Legs[0].Notional / 1_000_000.0 * 0.95,
                $"{query} dv01 {r.Dv01:N0} looks undiscounted");
            Assert.DoesNotContain(r.Notes, n => n.Contains("UNDISCOUNTED"));
        }

        [Fact]
        public void Longer_Tenors_Are_Discounted_Harder()
        {
            const double k = 2.5;
            var svc = Build(k);
            double Ratio(string q, double t)
            {
                var r = svc.Analyze(q);
                return r.Dv01!.Value / (Undiscounted(t, k) * r.Legs[0].Notional / 1_000_000.0);
            }
            // DF(10y) < DF(5y), so the correction grows with tenor
            Assert.True(Ratio("usd cpi 10y", 10.0) < Ratio("usd cpi 5y", 5.0) - 0.05);
        }

        [Fact]
        public void Inflation_Ladder_Sizes_Off_The_Dv01_Target()
        {
            // path D: dv01: on a ladder was parsed then silently ignored, and an unsized ladder point
            // sat on the flat 10mm while an unsized SWAP showed $25k of risk.
            var svc = Build(2.5);
            var r = svc.Analyze("usd cpi 5y");
            double halfLot = r.Legs[0].DensityPerMm * RateDesk.Core.Risk.RiskSizer.NotionalLot / 2 / 1e6;

            Assert.NotEqual(10_000_000.0, r.Legs[0].Notional);
            Assert.Equal(0.0, r.Legs[0].Notional % RateDesk.Core.Risk.RiskSizer.NotionalLot, 6);
            Assert.InRange(r.Dv01!.Value,
                RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd - halfLot,
                RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd + halfLot);
            // same ccy as the dv01 input, so no FX quote is touched
            Assert.DoesNotContain(r.Notes, n => n.Contains("dv01 input in"));

            var fifty = svc.Analyze("usd cpi 5y dv01:50k");
            Assert.InRange(fifty.Dv01!.Value, 50_000 - halfLot, 50_000 + halfLot);
            Assert.Equal(100_000_000.0, svc.Analyze("usd cpi 5y 100mm").Legs[0].Notional, 6);

            // the blotter's exact channel is never resized or rounded
            var pq = svc.ParseQuery("usd cpi 5y");
            pq.LegNotionals = new List<double> { 16_470_219 };
            Assert.Equal(16_470_219, svc.Analyze(pq).Legs[0].Notional, 6);
        }

        [Fact]
        public void Exponent_Is_The_Whole_Year_Count_Not_The_Ladder_Day_Count()
        {
            const double k = 2.5;
            var svc = Build(k);
            var r = svc.Analyze("usd cpi 5y");
            var curves = svc.GetCurves("USD");
            double df = curves.Ois!.discount(r.Maturity!, true);

            // ACT/360 on the ladder's Dcc would give T = 5.0694 and inflate the risk ~1.4%
            double onDcc = 100.0 * (5 * 365.0 / 360.0) * Math.Pow(1 + k / 100.0, 5 * 365.0 / 360.0 - 1) * df;
            Assert.NotEqual(onDcc, r.Legs[0].DensityPerMm, 3);
            Assert.Equal(Undiscounted(5.0, k) * df, r.Legs[0].DensityPerMm, 9);
        }

        [Fact]
        public void Par_Rate_Ladder_Dv01_Is_A_Real_Annuity_Not_N_Times_T()
        {
            // A RATE ladder quoting par swap rates (the Fed Funds USSO* strip) has a real fixed-leg
            // annuity, bootstrapped from the ladder's OWN pillars. It must not report the undiscounted
            // 100.T, which is ~11% too big at 5y.
            var cfg = TestConfigs.Usd();
            cfg.Ladders[0].Kind = "RATE";
            cfg.Ladders[0].Dcc = "ACT/360";
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_rateladder_cfg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "usd.json"),
                System.Text.Json.JsonSerializer.Serialize(cfg));
            var svc = new RateDesk.Core.PricingService(ConfigStore.LoadFromDirectory(dir), Snap(cfg));

            var r = svc.Analyze("usd cpi 5y");
            double dens = r.Legs[0].DensityPerMm;

            // a 5y annuity at a 4% flat curve is ~4.5 years, never the undiscounted 5.0
            Assert.InRange(dens, 100.0 * 4.2, 100.0 * 4.7);
            Assert.True(dens < 100.0 * 5.0 * 0.97, $"density {dens:N1} still looks undiscounted");
            Assert.DoesNotContain(r.Notes, n => n.Contains("UNDISCOUNTED"));

            static RatesSnapshot Snap(CurrencyConfig c)
            {
                var s = new RatesSnapshot();
                foreach (var p in c.Ois!.Curve)
                    s.Update(ConfigStore.ResolveTicker(p.Ticker, c.DefaultSource), null, null, 4.0);
                // ladder pillars live under the EMPTY-source ticker, which is also what the ladder's
                // own bootstrap must read — a mismatch here silently misses every pillar
                foreach (var p in c.Ladders[0].Pillars)
                    s.Update(ConfigStore.ResolveTicker(p.Ticker, ""), 3.98, 4.02, 4.0);
                return s;
            }
        }
    }

    /// <summary>BRL pré x DI. A par zero-coupon DI swap self-discounts, so its DV01 collapses to
    /// N.(du/252)/(1+r) with no DI-strip bootstrap — see DiDensityPerMm for the sourced derivation.
    /// The ladder used to report the undiscounted 100.T (14.5% too big at 5y on live levels) and the
    /// dated contracts reported no leg, hence no notional/dv01/$01, at all.</summary>
    public class BrlDiDv01Tests
    {
        private const double DiPct = 14.5, BrlPerUsd = 5.4;

        private static RateDesk.Core.PricingService Build()
        {
            var cfg = TestConfigs.Brl();
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_brl_cfg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "brl.json"),
                System.Text.Json.JsonSerializer.Serialize(cfg));
            var snap = new RatesSnapshot();
            foreach (var p in cfg.Ladders[0].Pillars)
                snap.Update(ConfigStore.ResolveTicker(p.Ticker, ""), DiPct - 0.05, DiPct + 0.05, DiPct);
            // the dated contract the test queries
            snap.Update("ODF31 Comdty", DiPct - 0.05, DiPct + 0.05, DiPct);
            // ladder sizing converts the $25k default into BRL/bp, so the spot has to be loaded
            snap.Update("BRL Curncy", null, null, BrlPerUsd);
            return new RateDesk.Core.PricingService(ConfigStore.LoadFromDirectory(dir), snap);
        }

        private static double Bus252(Date from, Date to) =>
            new Business252(new Brazil()).yearFraction(from, to);

        [Fact]
        public void Di_Ladder_Dv01_Is_The_Zero_Coupon_Closed_Form()
        {
            var svc = Build();
            var r = svc.Analyze("brl 5y");

            double du252 = Bus252(r.Legs[0].Effective, r.Legs[0].Maturity);
            Assert.InRange(du252, 4.8, 5.1);                       // ~252 business days a year
            double expected = 100.0 * du252 / (1 + DiPct / 100.0);

            Assert.Equal(expected, r.Legs[0].DensityPerMm, 9);
            Assert.Equal(expected * r.Legs[0].Notional / 1_000_000.0, r.Dv01!.Value, 6);

            // strictly below the undiscounted 100.T it used to report
            Assert.True(r.Legs[0].DensityPerMm < 100.0 * 5.0 * 0.9,
                $"density {r.Legs[0].DensityPerMm:N1} still looks undiscounted");
            Assert.DoesNotContain(r.Notes, n => n.Contains("UNDISCOUNTED"));
        }

        [Fact]
        public void Di_Dated_Contract_Now_Has_A_Sized_Leg()
        {
            var svc = Build();
            var r = svc.Analyze("brl f31");

            // used to add no leg at all, so notional/dv01/$01 were blank on screen
            Assert.Single(r.Legs);
            Assert.True(r.Dv01!.Value > 0);
            double du252 = Bus252(r.Legs[0].Effective, r.Legs[0].Maturity);
            Assert.Equal(100.0 * du252 / (1 + DiPct / 100.0), r.Legs[0].DensityPerMm, 9);
            Assert.Equal(0.0, r.Legs[0].Notional % RateDesk.Core.Risk.RiskSizer.NotionalLot, 6);
        }

        [Fact]
        public void Di_Dv01_Falls_With_The_Level()
        {
            // dv01 = N.(du/252)/(1+r): higher rates discount harder, so the same tenor risks less.
            // BRL at 14.5% is where this matters — it is a 13% effect, not a rounding one.
            var svc = Build();
            var r = svc.Analyze("brl 5y");
            double undiscounted = 100.0 * Bus252(r.Legs[0].Effective, r.Legs[0].Maturity);
            Assert.Equal(undiscounted / 1.145, r.Legs[0].DensityPerMm, 9);
        }

        [Fact]
        public void Di_Ladder_Sizes_Off_The_Dv01_Target_In_Brl()
        {
            // path D: dv01: on a ladder used to be parsed and silently dropped. The $25k default is
            // a USD input on a BRL trade, so it converts at spot first.
            var svc = Build();
            var r = svc.Analyze("brl 5y");
            double target = RateDesk.Core.Risk.RiskSizer.DefaultDv01Usd * BrlPerUsd;   // BRL/bp
            double halfLot = r.Legs[0].DensityPerMm * RateDesk.Core.Risk.RiskSizer.NotionalLot / 2 / 1e6;

            Assert.NotEqual(10_000_000.0, r.Legs[0].Notional);
            Assert.Equal(0.0, r.Legs[0].Notional % RateDesk.Core.Risk.RiskSizer.NotionalLot, 6);
            Assert.InRange(r.Dv01!.Value, target - halfLot, target + halfLot);
            Assert.Contains(r.Notes, n => n.Contains("dv01 input in USD"));

            // an explicit dv01, and an explicit notional, both still win
            var fifty = svc.Analyze("brl 5y dv01:50k");
            Assert.InRange(fifty.Dv01!.Value,
                50_000 * BrlPerUsd - halfLot, 50_000 * BrlPerUsd + halfLot);
            Assert.Equal(100_000_000.0, svc.Analyze("brl 5y 100mm").Legs[0].Notional, 6);
        }
    }

    /// <summary>CROSS-MARKET (BETA) grammar: separators, ccy adjacency, glued pairs, shape
    /// inheritance — and that every intra-ccy meaning ("5y vs 10y", size "x", FRAs) survives.</summary>
    public class CrossMarketQueryTests
    {
        private static ConfigStore Store()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_cross_cfg");
            Directory.CreateDirectory(dir);
            foreach (var cfg in new[] { TestConfigs.Usd(), TestConfigs.Gbp(), TestConfigs.Aud(), TestConfigs.Mxn() })
                File.WriteAllText(System.IO.Path.Combine(dir, cfg.Ccy.ToLowerInvariant() + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(cfg));
            return ConfigStore.LoadFromDirectory(dir);
        }

        private static QueryParser Parser() => new(new IndexRegistry(Store()));

        [Theory]
        [InlineData("aud vs usd 5y5y")]
        [InlineData("aud v usd 5y5y")]
        [InlineData("aud over usd 5y5y")]
        [InlineData("aud against usd 5y5y")]
        [InlineData("aud minus usd 5y5y")]
        [InlineData("aud x usd 5y5y")]
        public void Separators_Split_And_Inherit_Shape(string text)
        {
            var q = Parser().Parse(text);
            Assert.NotNull(q.Cross);
            Assert.Equal("AUD", q.Target.Ccy);
            Assert.Equal("USD", q.Cross!.Target.Ccy);
            Assert.Single(q.Legs);
            Assert.Single(q.Cross.Legs);
            // side A inherited the 5y5y shape
            Assert.Equal(StartKind.Forward, q.Legs[0].StartKind);
            Assert.Equal(StartKind.Forward, q.Cross.Legs[0].StartKind);
        }

        [Fact]
        public void Bare_Ccy_Adjacency_Is_A_Cross()
        {
            var q = Parser().Parse("aud usd 10y");
            Assert.NotNull(q.Cross);
            Assert.Equal("AUD", q.Target.Ccy);
            Assert.Equal("USD", q.Cross!.Target.Ccy);
            Assert.Equal(new Period(10, TimeUnit.Years), q.Legs[0].Tenor);
            Assert.Equal(new Period(10, TimeUnit.Years), q.Cross.Legs[0].Tenor);
        }

        [Fact]
        public void Glued_Ccy_Pair_Is_A_Cross()
        {
            var q = Parser().Parse("aud/usd 10y");
            Assert.NotNull(q.Cross);
            Assert.Equal("AUD", q.Target.Ccy);
            Assert.Equal("USD", q.Cross!.Target.Ccy);
        }

        [Fact]
        public void Cross_Of_Structures_Keeps_Both_Sides_Shapes()
        {
            var q = Parser().Parse("usd v gbp 5s10s");
            Assert.NotNull(q.Cross);
            Assert.Equal(2, q.Legs.Count);
            Assert.Equal(2, q.Cross!.Legs.Count);
        }

        [Fact]
        public void Intra_Ccy_Vs_Stays_A_Spread()
        {
            var q = Parser().Parse("usd 5y vs 10y");
            Assert.Null(q.Cross);
            Assert.Equal(2, q.Legs.Count);
            Assert.All(q.Legs, l => Assert.Equal(StartKind.Spot, l.StartKind));
        }

        [Fact]
        public void Size_List_X_Is_Not_A_Cross()
        {
            var q = Parser().Parse("aud 2s5s10s 33m x 50m x 20m");
            Assert.Null(q.Cross);
            Assert.Equal(3, q.Legs.Count);
        }

        [Fact]
        public void Fra_Grammar_Unaffected()
        {
            var q = Parser().Parse("aud 3x6 fra");
            Assert.Null(q.Cross);
            Assert.True(q.Legs[0].IsFra);
        }

        [Fact]
        public void Two_Bare_Markets_With_No_Shape_Are_Not_A_Cross()
        {
            // nothing to price — must fall through (and then error or grid, never a silent cross)
            var q = Parser();
            var parsed = Record.Exception(() => q.Parse("aud usd"));
            // whatever the fallback does, it must NOT produce a cross with empty legs
            if (parsed == null)
                Assert.Null(q.Parse("aud usd").Cross);
        }

        [Fact]
        public void Unconsumed_Fwd_Marker_Errors_Not_Silently_Spot()
        {
            Assert.Throws<FormatException>(() => Parser().Parse("usd 1y fwd"));
        }
    }
}
