using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>Fresh-eyes-review regressions (2026-08-26): the surfaces that claim to show the
    /// same numbers must actually agree, the frozen report must carry EVERYTHING the offline
    /// paths rebuild from, and synthetic values are always marked.</summary>
    public class CrossSurfaceTests
    {
        private static WeeklyReport Fixture()
        {
            var rep = new WeeklyReport();
            var run = new WeeklyRun
            {
                Title = "MPC · GBP", RefName = "SONIO/N Index", RefPct = 3.731,
            };
            run.Source = "";
            run.CompoundedPct = 3.736;
            run.CompoundedFrom = new DateTime(2026, 7, 30);
            run.Rows.Add(new WeeklyMeeting
            {
                Date = new(2026, 9, 17), EndDate = new(2026, 11, 5), MidPct = 3.775,
                PricedBp = 4.4, StepBp = null, D1Bp = -0.5, W1Bp = -2.5, M1Bp = -12.1,
                MidSource = "ticker",
            });
            run.Rows.Add(new WeeklyMeeting
            {
                Date = new(2026, 11, 5), EndDate = new(2026, 12, 17), MidPct = 3.890,
                PricedBp = 15.9, StepBp = 11.5, D1Bp = 0.0, W1Bp = -3.1, M1Bp = -15.8,
                MidSource = "ticker",
            });
            rep.Runs.Add(run);
            return rep;
        }

        [Fact]
        public void EmailBlastAndWorkbook_AgreeOnEveryPublishedNumber()
        {
            var rep = Fixture();
            var email = WeeklyEmail.Html(rep);
            var blast = RateDesk.Weekly.Core.Daily.DailyBlast.Html(rep);
            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Runs");
            RateDesk.Weekly.Core.Daily.DailyBook.WriteRunsSheet(ws, rep);
            var cells = ws.CellsUsed()
                .Select(c => c.DataType == ClosedXML.Excel.XLDataType.Number
                    ? c.GetDouble().ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)
                    : c.GetString())
                .ToHashSet();

            foreach (var m in rep.Runs[0].Rows)
            {
                var mid = m.MidPct.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                Assert.Contains(mid, email);
                Assert.Contains(mid, blast);
                Assert.Contains(m.MidPct.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture), cells);
                foreach (var v in new[] { m.PricedBp, m.StepBp, m.D1Bp, m.W1Bp, m.M1Bp })
                    if (v is { } x)
                    {
                        var s = x.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);
                        // U+2011 replaces the minus in the email; normalize before comparing
                        Assert.Contains(s, email.Replace('‑', '-'));
                        Assert.Contains(s, blast);
                        Assert.Contains(x.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)
                            .Replace("+", ""), cells);
                    }
            }
            // the flat 0.0 change prints identically everywhere — never "+0.0"
            Assert.DoesNotContain(">+0.0<", email);
            Assert.DoesNotContain(">+0.0<", blast);
        }

        [Fact]
        public void ReportStore_RoundTrips_TheTrialFields()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-rs-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var rep = Fixture();
                rep.Runs[0].Source = "NABZ";
                rep.Runs[0].RefRebased = true;
                var p = System.IO.Path.Combine(dir, "r.json");
                ReportStore.Save(rep, p);
                var back = ReportStore.Load(p);
                Assert.NotNull(back);
                var r = back!.Runs[0];
                Assert.Equal("NABZ", r.Source);          // history pages read the SAME source offline
                Assert.Equal(3.736, r.CompoundedPct!.Value, 6);
                Assert.Equal(new DateTime(2026, 7, 30), r.CompoundedFrom);
                Assert.True(r.RefRebased);               // the † marker survives a restart
                Assert.Equal("ticker", r.Rows[0].MidSource);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void SyntheticValues_AreAlwaysMarked()
        {
            var rep = Fixture();
            rep.Runs[0].RefRebased = true;
            rep.Runs[0].Rows[0].MidSource = "interp (ticker +137.0bp off — rejected)";
            var html = WeeklyEmail.Html(rep);
            Assert.Contains("3.775†", html);             // guard-synthesized mid carries the dagger
            // an adjusted fixing renders as the starred number in italics, with the one shared
            // disclaimer line under the OIS tables (desk 2026-09-02 — the wordy label retired)
            Assert.Contains("*</i>", html);
            Assert.Contains("has been adjusted to reflect hike/cut", html);
            var text = WeeklyEmail.PlainText(rep);
            Assert.Contains("*", text);
            Assert.Contains("has been adjusted to reflect hike/cut", text);
        }

        [Fact]
        public void Compound_RefusesAnInteriorHole()
        {
            // fixings stop for 12 days mid-window and resume — fill-forwarding across the hole
            // would manufacture a number
            var fix = new List<HistPoint>();
            for (var d = new DateTime(2026, 7, 1); d <= new DateTime(2026, 7, 20); d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    fix.Add(new HistPoint(d, 2.50));
            for (var d = new DateTime(2026, 8, 3); d <= new DateTime(2026, 8, 24); d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    fix.Add(new HistPoint(d, 2.50));
            Assert.Null(CompoundedFixing.Compound(fix, new(2026, 7, 9), new(2026, 8, 25), 365));
        }

        [Fact]
        public void ColumnOrder_IsMidPricedStep_OnEverySurface()
        {
            // desk 2026-08-26: "mid/priced/step everywhere *everywhere*"
            var rep = Fixture();
            var email = WeeklyEmail.Html(rep);
            Assert.True(email.IndexOf(">Priced<", StringComparison.Ordinal)
                        < email.IndexOf(">Step<", StringComparison.Ordinal));
            var blast = RateDesk.Weekly.Core.Daily.DailyBlast.Html(rep);
            Assert.True(blast.IndexOf("Priced (bp)", StringComparison.Ordinal)
                        < blast.IndexOf("Step (bp)", StringComparison.Ordinal));
            var text = RateDesk.Weekly.Core.Daily.DailyBlast.Render(rep);
            var hdr = text.Split('\n').First(l => l.Contains("StartDate"));
            Assert.True(hdr.IndexOf("Priced", StringComparison.Ordinal)
                        < hdr.IndexOf("Step", StringComparison.Ordinal));
        }

        [Fact]
        public void RungMap_DerivesSettledAnnouncements_AndKeepsSksfOnStarts()
        {
            // the live wrong number (fresh-eyes review 2026-08-26): ECB's settled 23-Jul-26
            // announcement must be a boundary — the config's decision list is future-only
            var ecb = MeetingsStore.Schedules.First(s => s.Name == "ECB");
            var ecbMap = new MeetingRungMap(ecb);
            Assert.Contains(new DateTime(2026, 7, 23), ecbMap.Boundaries);
            Assert.DoesNotContain(new DateTime(2026, 7, 29), ecbMap.Boundaries); // clustered into the announcement
            // on 27-Jul (post-announcement) the 16-Sep meeting was already rung 1
            Assert.Equal(1, ecbMap.RungFor(new(2026, 9, 16), new(2026, 7, 27)));
            // on 22-Jul (pre-announcement) it was still rung 2
            Assert.Equal(2, ecbMap.RungFor(new(2026, 9, 16), new(2026, 7, 22)));

            // SKSF keeps boundaries ON the period starts — no announcement snap
            var sek = MeetingsStore.Schedules.First(s => s.Name == "RIKSBANK");
            var sekMap = new MeetingRungMap(sek);
            Assert.Contains(new DateTime(2026, 8, 26), sekMap.Boundaries);
            Assert.DoesNotContain(new DateTime(2026, 8, 20), sekMap.Boundaries); // the decision is NOT a boundary
            // inside the decision→start window the 30-Sep meeting was still rung 2 (the
            // +65.7bp phantom's regression, now on the shared map)
            Assert.Equal(2, sekMap.RungFor(new(2026, 9, 30), new(2026, 8, 24)));
            // a day inside the contract's own period has NO rung — never rung 1
            Assert.Null(sekMap.RungFor(new(2026, 8, 26), new(2026, 8, 27)));
        }

        [Fact]
        public void StoreBackup_SnapshotRoundTrips_AndNeverReplacesAnExistingStore()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-bk-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var dbPath = System.IO.Path.Combine(dir, "history.db");
                var bkPath = System.IO.Path.Combine(dir, "history_backup.db");
                using (var store = new HistoryStore(dbPath))
                {
                    store.UpsertDaily("TEST1A Curncy", new[] { new HistPoint(new(2026, 8, 20), 1.234) },
                        excludeToday: false, source: "xls");
                    store.BackupTo(bkPath);   // consistent snapshot off a LIVE connection
                }
                using (var back = new HistoryStore(bkPath))
                {
                    var rows = back.GetDailyWithSource("TEST1A Curncy", 400);
                    Assert.Single(rows);
                    Assert.Equal(1.234, rows[0].Value, 6);
                    Assert.Equal("xls", rows[0].Source);   // the irreplaceable provenance travels
                }
                // restore NEVER overwrites an existing local store
                Assert.False(RateDesk.Weekly.Core.SaveDown.StoreBackup.Restore(bkPath, dbPath));
                // ...and restores cleanly onto an empty machine
                var fresh = System.IO.Path.Combine(dir, "fresh", "history.db");
                Assert.True(RateDesk.Weekly.Core.SaveDown.StoreBackup.Restore(bkPath, fresh));
                Assert.True(System.IO.File.Exists(fresh));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void MixedStateDays_NeverSourceAnAnchor()
        {
            // the ECB +24.3bp Δ1m (desk 2026-08-26): EESF re-pointed BETWEEN the 24-Jul and
            // 27-Jul closes (announcement 23-Jul, start 29-Jul) — every day strictly between
            // announcement and start is per-rung ambiguous and must source nothing
            var ecb = MeetingsStore.Schedules.First(s => s.Name == "ECB");
            var map = new MeetingRungMap(ecb);
            Assert.False(map.IsMixedState(new(2026, 7, 23)));   // the announcement (a boundary)
            Assert.True(map.IsMixedState(new(2026, 7, 24)));    // the trap day
            Assert.True(map.IsMixedState(new(2026, 7, 27)));
            Assert.True(map.IsMixedState(new(2026, 7, 28)));
            Assert.False(map.IsMixedState(new(2026, 7, 29)));   // the period start
            // SKSF renumbers wholly at the start — its decision→start window is CLEAN by probe
            var sek = MeetingsStore.Schedules.First(s => s.Name == "RIKSBANK");
            var sekMap = new MeetingRungMap(sek);
            Assert.False(sekMap.IsMixedState(new(2026, 8, 24)));

            // end to end through the history walk: a poisoned old-numbering close on the trap
            // day must be SKIPPED, the anchor walking back to the last clean pre-announcement day
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-mixed-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                using var store = new HistoryStore(System.IO.Path.Combine(dir, "s.db"));
                store.UpsertDaily("EESF2A Curncy", new[]
                {
                    new HistPoint(new(2026, 7, 21), 2.400),
                    new HistPoint(new(2026, 7, 22), 2.405),   // last clean pre-announcement close
                }, excludeToday: false);
                store.UpsertDaily("EESF1A Curncy", new[]
                {
                    new HistPoint(new(2026, 7, 24), 2.190),   // OLD July period — the +24.3 trap
                }, excludeToday: false);
                var run = new WeeklyRun { Title = "ECB · EUR" };
                run.Rows.Add(new WeeklyMeeting
                    { Date = new(2026, 9, 16), MidPct = 2.43, EndDate = new(2026, 11, 4) });
                var rows = RateDesk.Weekly.Core.Daily.DailyBook.BankHistoryRows(
                    store, ecb, run, "EESF{N}A", new DateTime(2026, 7, 29), 4);
                Assert.NotEmpty(rows);
                Assert.All(rows.Where(r => r.Start == new DateTime(2026, 9, 16)),
                    r => Assert.Equal(2.405, r.Rate, 6));     // never 2.190
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void FrontRow_IsExemptFromTheCrossSectionalGuard_ButNotTheAbsoluteBars()
        {
            // the RBNZ -1.0-vs--20.3 false flag (desk 2026-08-26): a front converging on the
            // fixing legitimately decouples from the strip
            var rep = new WeeklyReport();
            var run = new WeeklyRun { Title = "RBNZ · NZD" };
            double?[] d1m = { -1.0, -20.3, -21.0, -19.5, -22.1 };
            for (int i = 0; i < d1m.Length; i++)
                run.Rows.Add(new WeeklyMeeting
                    { Date = new DateTime(2026, 9, 3).AddDays(i * 42), MidPct = 2.7 + i * 0.1, M1Bp = d1m[i] });
            rep.Runs.Add(run);
            var notes = OutlierGuard.Check(rep);
            Assert.DoesNotContain(notes, n => n.Contains("03-Sep-26"));      // front not flagged
            // ...but the ABSOLUTE bar still covers the front
            run.Rows[0].M1Bp = -60.0;
            notes = OutlierGuard.Check(rep);
            Assert.Contains(notes, n => n.Contains("03-Sep-26") && n.Contains("sanity bar"));
        }

        [Fact]
        public void VariableLagFamilies_DeriveNoAnnouncements()
        {
            // BOJ's decision→start lag runs 1-6 days (Tokyo settlement) — a median-derived
            // announcement there is a guess, and a roll correction fired on a guessed date
            // manufactures a step of CoD. Only RECORDED BOJ decisions may appear.
            var boj = MeetingsStore.Schedules.First(s => s.Name == "BOJ");
            Assert.False(MeetingCalendar.LagIsStable(boj));
            var recorded = boj.DecisionDates.Select(d => d.Date).ToHashSet();
            Assert.All(MeetingCalendar.AnnouncementDates(boj), d => Assert.Contains(d.Date, recorded));
            // ECB's lag is a constant 6 — its derivation stands (23-Jul from the 29-Jul start)
            Assert.True(MeetingCalendar.LagIsStable(
                MeetingsStore.Schedules.First(s => s.Name == "ECB")));
        }
    }
}
