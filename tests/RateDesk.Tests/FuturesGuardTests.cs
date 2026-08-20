using RateDesk.Core;
using RateDesk.Core.Market;

namespace RateDesk.Tests
{
    /// <summary>The exchange-settled futures cross-check (desk 2026-08-20): a month-average or
    /// IMM-quarter future settling on the SAME overnight index must reconcile with the
    /// day-weighted blend of the meeting rows. A breach is a loud TRIGGERED flag, never silence.</summary>
    public class FuturesGuardTests
    {
        private static PricingService Service(RatesSnapshot snap)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ratedesk_guard_cfg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "usd.json"),
                System.Text.Json.JsonSerializer.Serialize(TestConfigs.Usd()));
            return new PricingService(RateDesk.Core.Config.ConfigStore.LoadFromDirectory(dir), snap);
        }

        private static void Rung(RatesSnapshot snap, string p, int n, DateTime eff, DateTime mat, double? mid)
        {
            var tk = p.Replace("{N}", n.ToString()) + " Curncy";
            snap.SetMaturity(tk, mat);
            snap.SetEffective(tk, eff);
            if (mid is { } m) snap.Update(tk, null, null, m);
        }

        private static string My(DateTime m) => "FGHJKMNQUVXZ"[m.Month - 1] + (m.Year % 10).ToString();

        /// <summary>Fixture: the guard's first candidate month (next calendar month), with periods
        /// 2.00% for its first 10 days and 3.00% after, and full coverage on both ends.</summary>
        private static (PricingService svc, MeetingScheduleDef sched, DateTime month, double blend)
            MonthAvgFixture(double? futPx, out RatesSnapshot snap)
        {
            snap = new RatesSnapshot();
            const string p = "TESTGF{N}";
            var m = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
            var d1 = m.AddDays(-5);
            var d2 = m.AddDays(10);
            var d3 = m.AddMonths(1).AddDays(5);

            Rung(snap, p, 0, d1.AddDays(-42), d1, null);
            Rung(snap, p, 1, d1, d2, 2.00);
            Rung(snap, p, 2, d2, d3, 3.00);
            Rung(snap, p, 3, d3, d3.AddDays(42), 3.10);

            int n = DateTime.DaysInMonth(m.Year, m.Month);
            double blend = (10 * 2.00 + (n - 10) * 3.00) / n;
            if (futPx is { } px) snap.Update("TESTAVG" + My(m) + " Comdty", null, null, px);

            var sched = new MeetingScheduleDef
            {
                Name = "TESTGF", Ccy = "USD", Header = "t",
                Tickers = new List<string> { p },
                GuardFutures = "TESTAVG{MY} Comdty",
                GuardFuturesKind = "monthavg",
                Dates = new List<DateTime> { d1, d2, d3, d3.AddDays(42) },
            };
            return (Service(snap), sched, m, blend);
        }

        [Fact]
        public void MonthAverage_Reconciles_WhenTheBlendMatches()
        {
            var (svc, sched, _, blend) = MonthAvgFixture(null, out var snap);
            snap.Update("TESTAVG" + My(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1)) + " Comdty",
                null, null, 100.0 - blend);

            var line = FuturesGuard.CheckRun(svc, sched);

            Assert.StartsWith("futures guard TESTGF ok", line);
            Assert.DoesNotContain(FuturesGuard.TriggerPrefix, line);
        }

        [Fact]
        public void MonthAverage_Triggers_WhenTheRowsDisagreeWithTheFuture()
        {
            // a 20bp gap — the size a mis-rolled front or wrong re-base produces, never basis
            var (svc, sched, _, blend) = MonthAvgFixture(null, out var snap);
            snap.Update("TESTAVG" + My(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1)) + " Comdty",
                null, null, 100.0 - blend - 0.20);

            var line = FuturesGuard.CheckRun(svc, sched);

            Assert.StartsWith(FuturesGuard.TriggerPrefix, line);
            Assert.Contains("verify_strip_changes", line);
        }

        [Fact]
        public void NoQuotedContract_IsAnHonestSkip_NeverAVerdict()
        {
            var (svc, sched, _, _) = MonthAvgFixture(null, out _);   // no futures quote at all

            var line = FuturesGuard.CheckRun(svc, sched);

            Assert.Contains("skipped", line);
            Assert.DoesNotContain(FuturesGuard.TriggerPrefix, line);
            Assert.DoesNotContain(" ok:", line);
        }

        [Fact]
        public void ImmQuarter_CompoundsThePeriods_AndReconciles()
        {
            // one constant 2.50% across the whole IMM window: the annualized compounded rate IS
            // 2.50 exactly, so the future at 97.50 must reconcile with zero gap
            var snap = new RatesSnapshot();
            const string p = "TESTGI{N}";
            var q = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            do { q = q.AddMonths(1); }
            while (q.Month % 3 != 0 || FuturesGuard.ThirdWednesday(q) <= DateTime.Today);
            var a = FuturesGuard.ThirdWednesday(q);
            var b = FuturesGuard.ThirdWednesday(q.AddMonths(3));

            Rung(snap, p, 0, a.AddDays(-52), a.AddDays(-10), null);
            Rung(snap, p, 1, a.AddDays(-10), b.AddDays(5), 2.50);
            Rung(snap, p, 2, b.AddDays(5), b.AddDays(47), 2.60);
            snap.Update("TESTIMM" + My(q) + " Comdty", null, null, 97.50);

            var sched = new MeetingScheduleDef
            {
                Name = "TESTGI", Ccy = "USD", Header = "t",
                Tickers = new List<string> { p },
                GuardFutures = "TESTIMM{MY} Comdty",
                GuardFuturesKind = "imm3m",
                Dates = new List<DateTime> { a.AddDays(-10), b.AddDays(5), b.AddDays(47) },
            };

            var line = FuturesGuard.CheckRun(Service(snap), sched);

            Assert.StartsWith("futures guard TESTGI ok", line);
        }

        [Fact]
        public void BasisBearingGuard_JudgesAgainstTheExpectedSpread_NotZero()
        {
            // The EUR shape: Euribor futures settle ~14bp over the ESTR meeting blend. At the
            // expected basis the guard is quiet; the SAME price with no basis configured trips —
            // proving the knob is what keeps a basis-bearing family honest rather than a wide
            // tolerance that would also swallow real faults.
            var snap = new RatesSnapshot();
            const string p = "TESTGB{N}";
            var q = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            do { q = q.AddMonths(1); }
            while (q.Month % 3 != 0 || FuturesGuard.ThirdWednesday(q) <= DateTime.Today);
            var a = FuturesGuard.ThirdWednesday(q);
            var b = FuturesGuard.ThirdWednesday(q.AddMonths(3));

            Rung(snap, p, 0, a.AddDays(-52), a.AddDays(-10), null);
            Rung(snap, p, 1, a.AddDays(-10), b.AddDays(5), 2.50);   // constant → blend = 2.50 exactly
            Rung(snap, p, 2, b.AddDays(5), b.AddDays(47), 2.60);
            snap.Update("TESTGX" + My(q) + " Comdty", null, null, 100.0 - 2.64);  // 14bp over the blend

            var sched = new MeetingScheduleDef
            {
                Name = "TESTGB", Ccy = "USD", Header = "t",
                Tickers = new List<string> { p },
                GuardFutures = "TESTGX{MY} Comdty",
                GuardFuturesKind = "imm3m",
                GuardFuturesDcc = 360,
                GuardFuturesBasisBp = 14.0,
                GuardFuturesTolBp = 10.0,
                Dates = new List<DateTime> { a.AddDays(-10), b.AddDays(5), b.AddDays(47) },
            };
            Assert.StartsWith("futures guard TESTGB ok", FuturesGuard.CheckRun(Service(snap), sched));

            sched.GuardFuturesBasisBp = 0.0;   // same price, no expected basis → the 14bp is a fault
            Assert.StartsWith(FuturesGuard.TriggerPrefix, FuturesGuard.CheckRun(Service(snap), sched));
        }

        [Fact]
        public void CompoundedBlend_IsExactForAConstantRate_AndOrdersSegmentsCorrectly()
        {
            var rows = new List<MeetingRow>
            {
                new() { Date = new DateTime(2026, 9, 1), MidPct = 2.00 },
                new() { Date = new DateTime(2026, 10, 1), MidPct = 4.00 },
            };
            var a = new DateTime(2026, 9, 1);
            var b = new DateTime(2026, 11, 1);

            // constant slice reproduces the rate exactly
            Assert.Equal(2.00, FuturesGuard.CompoundedBlend(rows, a, new DateTime(2026, 10, 1)), 10);
            // two-segment slice sits between the legs, near the day-weighted mean (30d @2, 31d @4)
            double v = FuturesGuard.CompoundedBlend(rows, a, b);
            Assert.InRange(v, 3.00, 3.03);
            // and the average blend for the same slice is the plain day-weighted mean
            Assert.Equal((30 * 2.00 + 31 * 4.00) / 61.0, FuturesGuard.AverageBlend(rows, a, b), 10);
        }
    }
}
