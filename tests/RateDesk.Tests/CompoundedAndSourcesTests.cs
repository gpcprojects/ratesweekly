using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>The compounded-fixing trial (desk 2026-08-26) — convention locked against the
    /// desk pricer's own header values: TRUE compounding (not averaging), calendar-day
    /// weighted, index day count, window = current period start → asOf. RBNZ was the exact
    /// reproduction: flat 2.50 OCR since the 09-Jul-26 period start compounds to 2.5039 →
    /// their "2.504"; a simple average prints 2.500 and fails.</summary>
    public class CompoundedAndSourcesTests
    {
        private static List<HistPoint> Weekdays(DateTime from, DateTime to, double value)
        {
            var l = new List<HistPoint>();
            for (var d = from; d <= to; d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    l.Add(new HistPoint(d, value));
            return l;
        }

        [Fact]
        public void Rbnz_Reproduction_FlatOcrCompoundsToTheDeskPricersValue()
        {
            // flat 2.50 from the 09-Jul-26 period start; window ends 25-Aug-26 (47 days)
            var fix = Weekdays(new(2026, 7, 1), new(2026, 8, 24), 2.50);
            var v = CompoundedFixing.Compound(fix, new(2026, 7, 9), new(2026, 8, 25), 365);
            Assert.NotNull(v);
            Assert.InRange(v!.Value, 2.5038, 2.5040);   // the pricer's "2.504" — NOT 2.5000
        }

        [Fact]
        public void SimpleAverageWouldFail_TheUpliftIsCompounding()
        {
            var fix = Weekdays(new(2026, 7, 1), new(2026, 8, 24), 2.50);
            var v = CompoundedFixing.Compound(fix, new(2026, 7, 9), new(2026, 8, 25), 365)!.Value;
            Assert.True(v > 2.5025, $"uplift missing ({v}) — averaging instead of compounding");
        }

        [Fact]
        public void FridayFixing_SpansTheWeekend()
        {
            // one week, everything 0 except Friday 3.65: Friday must weight 3 calendar days
            var mon = new DateTime(2026, 8, 10);
            var fix = new List<HistPoint>();
            for (int i = 0; i < 5; i++)
                fix.Add(new HistPoint(mon.AddDays(i), i == 4 ? 3.65 : 0.0001));
            var v = CompoundedFixing.Compound(fix, mon, mon.AddDays(7), 365)!.Value;
            Assert.InRange(v, 3.65 * 3 / 7.0 - 0.001, 3.65 * 3 / 7.0 + 0.002);
        }

        [Fact]
        public void FlatSeries_ReturnsTheRatePlusCompoundingUplift_OnItsOwnDcc()
        {
            var fix = Weekdays(new(2026, 7, 1), new(2026, 8, 24), 3.60);
            var v360 = CompoundedFixing.Compound(fix, new(2026, 7, 26), new(2026, 8, 25), 360)!.Value;
            // 30 days at flat 3.60 ACT/360: 3.60 · (1 + 0.036·30/720) ≈ 3.6054
            Assert.InRange(v360, 3.6050, 3.6058);
        }

        [Fact]
        public void StaleFixing_PublishesNothing()
        {
            var fix = Weekdays(new(2026, 7, 1), new(2026, 8, 14), 2.50);   // stops 11 days early
            Assert.Null(CompoundedFixing.Compound(fix, new(2026, 7, 9), new(2026, 8, 25), 365));
        }

        [Fact]
        public void NoFixingBeforeTheWindow_PublishesNothing()
        {
            var fix = Weekdays(new(2026, 8, 12), new(2026, 8, 24), 2.50);
            Assert.Null(CompoundedFixing.Compound(fix, new(2026, 7, 9), new(2026, 8, 25), 365));
        }

        // ---- the window start comes from the schedule's own documented dates ----

        [Theory]
        [InlineData("FOMC", 2026, 7, 29)]     // grid start, decision == start
        [InlineData("ECB", 2026, 7, 29)]      // grid start (decision was 23-Jul, lag +6)
        [InlineData("MPC", 2026, 7, 30)]
        [InlineData("BOC", 2026, 7, 16)]
        [InlineData("RBA", 2026, 8, 12)]
        [InlineData("RBNZ", 2026, 7, 9)]      // hand-entered pastDate 08-Jul is the DECISION; +1d lag
        [InlineData("BOJ", 2026, 7, 31)]
        [InlineData("NORGES", 2026, 8, 14)]
        [InlineData("RIKSBANK", 2026, 7, 1)]  // pastDate 24-Jun decision; +7d median lag (config pairs: 6,6,7,7,7,7,7,8)
        public void CurrentPeriodStart_ResolvesTheEffectiveDate(string run, int y, int m, int d)
        {
            var sched = MeetingsStore.Schedules.First(s => s.Name == run);
            Assert.Equal(new DateTime(y, m, d),
                MeetingCalendar.CurrentPeriodStart(sched, new DateTime(2026, 8, 25)));
        }

        [Theory]
        [InlineData("FOMC", 0)]
        [InlineData("MPC", 0)]
        [InlineData("ECB", 6)]
        [InlineData("RBNZ", 1)]
        [InlineData("RBA", 1)]
        [InlineData("BOC", 1)]
        [InlineData("NORGES", 1)]
        [InlineData("RIKSBANK", 7)]
        public void DecisionToStartLag_DerivesFromTheConfigsOwnPairs(string run, int lag)
        {
            var sched = MeetingsStore.Schedules.First(s => s.Name == run);
            Assert.Equal(lag, MeetingCalendar.DecisionToStartLagDays(sched));
        }

        [Fact]
        public void AnnouncementDates_DeriveTheUnpairedStarts()
        {
            // ECB's decision list starts 10-Sep-26; the 29-Jul-26 start must derive 23-Jul
            var ecb = MeetingsStore.Schedules.First(s => s.Name == "ECB");
            Assert.Contains(new DateTime(2026, 7, 23), MeetingCalendar.AnnouncementDates(ecb));
            Assert.Contains(new DateTime(2026, 9, 10), MeetingCalendar.AnnouncementDates(ecb));
        }

        [Fact]
        public void FixingDcc_DeserializesFromConfig()
        {
            Assert.Equal(360, MeetingsStore.Schedules.First(s => s.Name == "FOMC").FixingDcc);
            Assert.Equal(360, MeetingsStore.Schedules.First(s => s.Name == "ECB").FixingDcc);
            Assert.Equal(360, MeetingsStore.Schedules.First(s => s.Name == "RIKSBANK").FixingDcc);
            Assert.Equal(365, MeetingsStore.Schedules.First(s => s.Name == "MPC").FixingDcc);
            Assert.Equal(365, MeetingsStore.Schedules.First(s => s.Name == "RBNZ").FixingDcc);
        }

        // ---- source selection (trial): persistence + apply semantics ----

        [Fact]
        public void SourceStore_RoundTrips_AndAppliesOnlyNonDefaults()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-src-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                SourceStore.Save(dir, new Dictionary<string, string>
                {
                    ["BOJ"] = "MTRT",          // real override
                    ["RBA"] = "NABZ",          // == config default, must NOT override
                    ["NOSUCH"] = "XXXX",       // unknown run, ignored
                });
                var loaded = SourceStore.Load(dir);
                Assert.Equal("MTRT", loaded["BOJ"]);

                var cfgDir = System.IO.Path.Combine(dir, "cfg");
                System.IO.Directory.CreateDirectory(cfgDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(cfgDir, "usd.json"),
                    System.Text.Json.JsonSerializer.Serialize(TestConfigs.Usd()));
                var svc = new PricingService(
                    RateDesk.Core.Config.ConfigStore.LoadFromDirectory(cfgDir), new RatesSnapshot());
                SourceStore.Apply(svc, dir);
                Assert.Equal("MTRT", svc.MeetingSourceOverrides["BOJ"]);
                Assert.False(svc.MeetingSourceOverrides.ContainsKey("RBA"));   // default = no override
                Assert.False(svc.MeetingSourceOverrides.ContainsKey("NOSUCH"));
                // and the run resolution reflects it
                var boj = MeetingsStore.Schedules.First(s => s.Name == "BOJ");
                Assert.Equal("MTRT", svc.MeetingSrc(boj));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        // ---- surfaces carry the trial values ----

        [Fact]
        public void CompoundedValue_ReachesEmailCard_Blast_AndWorkbook()
        {
            var rep = new WeeklyReport();
            var run = new WeeklyRun { Title = "MPC · GBP", RefName = "SONIO/N Index", RefPct = 3.731 };
            run.CompoundedPct = 3.736;
            run.CompoundedFrom = new DateTime(2026, 7, 30);
            run.Rows.Add(new WeeklyMeeting { Date = new(2026, 9, 17), MidPct = 3.775, PricedBp = 3.9 });
            rep.Runs.Add(run);

            var html = WeeklyEmail.Html(rep);
            Assert.Contains("cmpd 3.736", html);
            Assert.Contains("30‑Jul", html);   // U+2011 in the window label — Word must not break it

            var text = WeeklyEmail.PlainText(rep);
            Assert.Contains("compounded 3.736 (since 30-Jul-26)", text);

            var blastHtml = RateDesk.Weekly.Core.Daily.DailyBlast.Html(rep);
            Assert.Contains("compounded", blastHtml);
            Assert.Contains("3.736", blastHtml);

            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Runs");
            RateDesk.Weekly.Core.Daily.DailyBook.WriteRunsSheet(ws, rep);
            var vals = ws.CellsUsed().Select(c => c.GetString()).ToList();
            Assert.Contains("compounded", vals);
            Assert.Contains("since 30-Jul-26", vals);
        }

        [Fact]
        public void FlatChange_PrintsUnsignedZero_EverySurface()
        {
            // audit 2026-08-26: two-section "+0.0;-0.0" printed zero as "+0.0" in the email
            // while the xls/blast (three sections) printed "0.0"
            var rep = new WeeklyReport();
            var run = new WeeklyRun { Title = "MPC · GBP", RefPct = 3.731 };
            run.Rows.Add(new WeeklyMeeting
                { Date = new(2026, 9, 17), MidPct = 3.775, PricedBp = 4.4, StepBp = 0.0, D1Bp = 0.0 });
            rep.Runs.Add(run);
            var html = WeeklyEmail.Html(rep);
            Assert.DoesNotContain(">+0.0<", html);
        }

        [Fact]
        public void EmailRendering_IsCultureInvariant()
        {
            var was = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                var rep = new WeeklyReport();
                var run = new WeeklyRun { Title = "MPC · GBP", RefPct = 3.731 };
                run.Rows.Add(new WeeklyMeeting
                    { Date = new(2026, 5, 7), MidPct = 3.775, PricedBp = 4.4, D1Bp = -1.5 });
                rep.Runs.Add(run);
                var html = WeeklyEmail.Html(rep);
                Assert.Contains("3.775", html);           // not 3,775
                Assert.Contains("07-May-26", html);       // not 07-Mai-26
                Assert.DoesNotContain("3,775", html);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
        }
    }
}
