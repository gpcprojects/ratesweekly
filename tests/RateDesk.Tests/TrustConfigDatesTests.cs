using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core;
using RateDesk.Core.Market;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>The Riksbank extension (desk 2026-08-25): SKSF5A/6A quote REAL prices with no
    /// eff/maturity fields, and the desk verified the period grid against Bloomberg's own swap
    /// table — trustConfigDates lets those desk-confirmed config dates carry the priced rows,
    /// so the run extends past where the ticker fields end. Modelled EXACTLY on the live SKSF
    /// field state probed 2026-08-25 (rungs 1-3 fully dated, 4 eff-only, 5-6 price-only,
    /// 7+ absent), with the front period's decision already announced (the 20-Aug/26-Aug
    /// Riksbank shape) so the front roll is exercised too.</summary>
    public class TrustConfigDatesTests
    {
        private static PricingService Service(RatesSnapshot snap)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_tcd_cfg");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "usd.json"),
                System.Text.Json.JsonSerializer.Serialize(TestConfigs.Usd()));
            return new PricingService(RateDesk.Core.Config.ConfigStore.LoadFromDirectory(dir), snap);
        }

        private static void Rung(RatesSnapshot snap, string pattern, int n,
            DateTime? eff, DateTime? mat, double? px)
        {
            var tk = pattern.Replace("{N}", n.ToString()) + " Curncy";
            if (mat is { } m) snap.SetMaturity(tk, m);
            if (eff is { } e) snap.SetEffective(tk, e);
            if (px is { } p) snap.Update(tk, p, p, p);
        }

        [Fact]
        public void DeskConfirmedConfigDates_ExtendTheRun_PastTheTickerDatedRungs()
        {
            var snap = new RatesSnapshot();
            const string pat = "TESTSEK{N}A";
            var t = DateTime.Today;
            // period grid: b1 starts tomorrow (decision already announced 5 days ago — the
            // 26-Aug Riksbank shape), then ~6-week spacing
            DateTime b1 = t.AddDays(1), b2 = t.AddDays(37), b3 = t.AddDays(79), b4 = t.AddDays(121),
                     b5 = t.AddDays(170), b6 = t.AddDays(219), b7 = t.AddDays(261), b8 = t.AddDays(310);

            Rung(snap, pat, 0, b1.AddDays(1), b1.AddDays(2), null);   // run-down artifact
            Rung(snap, pat, 1, b1, b2, 1.65);                          // fully dated
            Rung(snap, pat, 2, b2, b3, 1.715);
            Rung(snap, pat, 3, b3, b4, 1.78);
            Rung(snap, pat, 4, b4, null, 1.393);                       // eff-only (SKSF4A)
            Rung(snap, pat, 5, null, null, 2.059);                     // price-only (SKSF5A)
            Rung(snap, pat, 6, null, null, 2.155);                     // price-only (SKSF6A)
            snap.Update("TESTSEKFIX Index", null, null, 1.641);

            var sched = new MeetingScheduleDef
            {
                Name = "TESTSEK", Ccy = "USD", Header = "t",
                Tickers = new List<string> { pat },
                RefTicker = "TESTSEKFIX Index",
                DecisionTimeLondon = "08:30",
                TrustConfigDates = true,
                DecisionDates = new List<DateTime> { t.AddDays(-5), b2.AddDays(-6), b3.AddDays(-6), b4.AddDays(-6) },
                Dates = new List<DateTime> { b1, b2, b3, b4, b5, b6, b7, b8 },
            };

            var run = Service(snap).MeetingRun(sched);

            // the announced front (b1) rolls off; the published rows must run b2..b6:
            // b2/b3/b4 ticker-dated, b5/b6 desk-confirmed config dates carrying REAL prices,
            // and b7 must NOT publish (no price — trustConfigDates never invents a quote)
            var dates = run.Rows.Select(r => r.Date.Date).ToList();
            Assert.Contains(b2.Date, dates);
            Assert.Contains(b4.Date, dates);
            Assert.Contains(b5.Date, dates);            // the extension
            Assert.Contains(b6.Date, dates);
            Assert.DoesNotContain(b7.Date, dates);      // priceless rung never publishes
            var r5 = run.Rows.First(r => r.Date.Date == b5.Date);
            Assert.Equal(2.059, r5.MidPct, 6);
            Assert.Equal(b6.Date, r5.EndDate?.Date);    // end from the confirmed grid
        }

        [Fact]
        public void RiksbankHistory_MapsRungsOnPeriodStarts_InsideTheDecisionToStartWindow()
        {
            // the +65.7bp phantom CoD (desk 2026-08-25): SKSF renumbers at the period START,
            // so on 24-Aug (decision 20-Aug announced, period starts 26-Aug) the 30-Sep-26
            // meeting was STILL rung 2. Boundaries snapped to the decision mapped it to rung 1
            // and every SEK change read one rung low. This locks the start-based mapping.
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rw-tcd3-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                using var store = new RateDesk.Weekly.Core.HistoryStore(System.IO.Path.Combine(dir, "s.db"));
                var day = new DateTime(2026, 8, 24);   // inside the 20→26-Aug window
                store.UpsertDaily("SKSF1A Curncy", new[] { new HistPoint(day, 1.650) }, excludeToday: false);
                store.UpsertDaily("SKSF2A Curncy", new[] { new HistPoint(day, 1.725) }, excludeToday: false);
                var sched = MeetingsStore.Schedules.First(s => s.Name == "RIKSBANK");
                Assert.True(sched.RollsAtPeriodStart);
                var run = new WeeklyRun { Title = "RIKSBANK · SEK" };
                run.Rows.Add(new WeeklyMeeting
                    { Date = new DateTime(2026, 9, 30), MidPct = 1.715, EndDate = new DateTime(2026, 11, 11) });

                var rows = RateDesk.Weekly.Core.Daily.DailyBook.BankHistoryRows(
                    store, sched, run, "SKSF{N}A", new DateTime(2026, 8, 25), 5);

                var r = rows.First(x => x.Day == day && x.Start == new DateTime(2026, 9, 30));
                Assert.Equal(1.725, r.Rate, 6);   // SKSF2A — start-based; 1.650 = the old fault
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void RealRiksbankSchedule_WithTheProbedSksfState_ExtendsPastTheTurn()
        {
            // the EXACT live field state probed 2026-08-25 against the REAL embedded schedule —
            // this is what the 17:19 desk run should have produced
            var snap = new RatesSnapshot();
            void R(int n, DateTime? eff, DateTime? mat, double? px)
            {
                var tk = $"SKSF{n}A Curncy";
                if (mat is { } m) snap.SetMaturity(tk, m);
                if (eff is { } e) snap.SetEffective(tk, e);
                if (px is { } p) snap.Update(tk, p, p, p);
            }
            R(0, new(2026, 8, 27), new(2026, 8, 28), null);
            R(1, new(2026, 8, 26), new(2026, 9, 30), 1.65);
            R(2, new(2026, 9, 30), new(2026, 11, 11), 1.715);
            R(3, new(2026, 11, 11), new(2026, 12, 23), 1.78);
            R(4, new(2026, 12, 23), null, 1.393);
            R(5, null, null, 2.059);
            R(6, null, null, 2.155);

            var sched = MeetingsStore.Schedules.First(s => s.Name == "RIKSBANK");
            Assert.True(sched.TrustConfigDates);   // the config flag must actually deserialize

            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_tcd2_cfg");
            System.IO.Directory.CreateDirectory(dir);
            var sek = TestConfigs.Usd();
            sek.Ccy = "SEK";
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "sek.json"),
                System.Text.Json.JsonSerializer.Serialize(sek));
            var svc = new PricingService(RateDesk.Core.Config.ConfigStore.LoadFromDirectory(dir), snap);

            var run = svc.MeetingRun(sched);

            var dates = run.Rows.Select(r => r.Date.Date).ToList();
            Assert.Contains(new DateTime(2026, 12, 23), dates);   // the turn period
            Assert.Contains(new DateTime(2027, 2, 10), dates);    // the extension (SKSF5A price)
            Assert.Contains(new DateTime(2027, 3, 31), dates);    // corrected start (SKSF6A price)
            Assert.DoesNotContain(new DateTime(2027, 5, 12), dates);  // 7A has no price
            var feb = run.Rows.First(r => r.Date.Date == new DateTime(2027, 2, 10));
            Assert.Equal(2.059, feb.MidPct, 6);
            var turn = run.Rows.First(r => r.Date.Date == new DateTime(2026, 12, 23));
            Assert.True(turn.TurnPeriod);
            Assert.Equal(new DateTime(2027, 2, 10), turn.EndDate?.Date);
        }
    }
}
