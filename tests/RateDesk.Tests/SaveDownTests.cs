using System.IO.Compression;
using ClosedXML.Excel;
using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Infl;
using RateDesk.Weekly.Core.SaveDown;

namespace RateDesk.Tests
{
    /// <summary>Store-first history (desk 2026-08-25, "pull as much as possible from
    /// history"): a fresh ticker costs no Bloomberg call; a stale one gap-fills and upserts;
    /// a dead terminal still serves whatever the store holds.</summary>
    public class StoreBackedHistoryTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-sbh-" + Guid.NewGuid().ToString("N"));
        public StoreBackedHistoryTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private sealed class FakeLive : RateDesk.Core.Market.IHistoryProvider
        {
            public int Calls;
            public List<HistPoint> Data = new();
            public IReadOnlyList<HistPoint> GetDaily(string t, int d) { Calls++; return Data; }
        }

        private static DateTime PrevBd(DateTime d)
        {
            var p = d.AddDays(-1);
            while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
            return p;
        }

        [Fact]
        public void FreshTicker_IsServedFromTheStore_WithNoBloombergCall()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "f.db"));
            store.UpsertDaily("X Curncy", new[] { new HistPoint(PrevBd(DateTime.Today), 1.5) },
                excludeToday: false);
            var live = new FakeLive();
            var sbh = new StoreBackedHistory(store, live);

            var h = sbh.GetDaily("X Curncy", 30);

            Assert.Single(h);
            Assert.Equal(0, live.Calls);
            Assert.Equal(1, sbh.ServedFromStore);
            sbh.Prefetch(new[] { "X Curncy", "Y Curncy" }, 220);   // deliberate no-op
            Assert.Equal(0, live.Calls);
        }

        [Fact]
        public void StaleTicker_GapFillsUpsertsAndServesTheMerge()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "s.db"));
            var old = PrevBd(PrevBd(PrevBd(DateTime.Today)));
            store.UpsertDaily("X Curncy", new[] { new HistPoint(old.AddDays(-7), 1.0) }, excludeToday: false);
            var live = new FakeLive { Data = { new HistPoint(PrevBd(DateTime.Today), 2.0) } };
            var sbh = new StoreBackedHistory(store, live);

            var h = sbh.GetDaily("X Curncy", 30);

            Assert.Equal(1, live.Calls);
            Assert.Equal(1, sbh.GapFilled);
            Assert.Equal(2, h.Count);                       // old row + gap-filled row, merged
            Assert.Equal(2.0, h[^1].Value, 6);
            Assert.Equal(2, store.GetDaily("X Curncy", 30).Count);   // and it PERSISTED
        }

        [Fact]
        public void DeadTerminal_StillServesTheStore()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "d.db"));
            var old = PrevBd(PrevBd(PrevBd(DateTime.Today)));
            store.UpsertDaily("X Curncy", new[] { new HistPoint(old, 1.0) }, excludeToday: false);
            var sbh = new StoreBackedHistory(store, new FakeLive());   // live returns nothing

            var h = sbh.GetDaily("X Curncy", 30);

            Assert.Single(h);
            Assert.Equal(1.0, h[0].Value, 6);
        }
    }

    /// <summary>The close discipline (desk 2026-08-25): 15:30–16:14:59 London = live mid saves
    /// as the close; from 16:15 marks pin to the 16:15 snap; earlier runs flag PRE-CLOSE.</summary>
    public class SnapDisciplineTests
    {
        [Fact]
        public void Band_Boundaries_AreExact()
        {
            var d = new DateTime(2026, 8, 25);
            Assert.Equal(SnapDiscipline.Mode.PreClose, SnapDiscipline.Resolve(d.AddHours(9)));
            Assert.Equal(SnapDiscipline.Mode.PreClose, SnapDiscipline.Resolve(d.Add(new TimeSpan(15, 29, 59))));
            Assert.Equal(SnapDiscipline.Mode.LiveAsClose, SnapDiscipline.Resolve(d.Add(new TimeSpan(15, 30, 0))));
            Assert.Equal(SnapDiscipline.Mode.LiveAsClose, SnapDiscipline.Resolve(d.Add(new TimeSpan(16, 14, 59))));
            Assert.Equal(SnapDiscipline.Mode.Snap1615, SnapDiscipline.Resolve(d.Add(new TimeSpan(16, 15, 0))));
            Assert.Equal(SnapDiscipline.Mode.Snap1615, SnapDiscipline.Resolve(d.Add(new TimeSpan(23, 0, 0))));
        }

        private sealed class FakeBars : RateDesk.Core.Market.IHistoryProvider
        {
            public Dictionary<string, HistPoint> Today = new();
            public IReadOnlyList<HistPoint> GetDaily(string t, int d) => Array.Empty<HistPoint>();
            public IReadOnlyList<HistPoint> GetLondonSnaps(string t, int d, TimeSpan at) =>
                Today.TryGetValue(t, out var p) ? new[] { p } : Array.Empty<HistPoint>();
        }

        [Fact]
        public void Snap1615Mode_PinsQuotedTickersToTheSnap_LeavesBarlessOnesLive()
        {
            // only meaningful to assert when the real London clock is past 16:15 — otherwise
            // Apply correctly does nothing to the quotes; the mode logic itself is tested above
            if (SnapDiscipline.Resolve(RateDesk.Core.Dates.DecisionClock.LondonNow())
                != SnapDiscipline.Mode.Snap1615) return;
            var snap = new RateDesk.Core.Market.RatesSnapshot();
            snap.Update("A Curncy", 2.40, 2.44, null);
            snap.Update("B Curncy", 1.10, 1.14, null);
            var bars = new FakeBars { Today = { ["A Curncy"] = new HistPoint(DateTime.Today, 2.50) } };

            var (mode, note) = SnapDiscipline.Apply(bars, snap, new[] { "A Curncy", "B Curncy" });

            Assert.Equal(SnapDiscipline.Mode.Snap1615, mode);
            Assert.Null(note);
            Assert.Equal(2.50, snap.Get("A Curncy")!.Mid!.Value, 6);   // pinned to the snap
            Assert.Equal(1.12, snap.Get("B Curncy")!.Mid!.Value, 6);   // no bars — stays live
        }
    }

    /// <summary>The macro-enabled save-down system (desk 2026-08-25): outlier CHECK notes,
    /// the salix/C+C detection helpers, and the two .xlsm store books — including the load-
    /// bearing property that ClosedXML preserves the templates' VBA project and buttons.</summary>
    public class SaveDownTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-sd-" + Guid.NewGuid().ToString("N"));
        public SaveDownTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static WeeklyReport BojReport(params double[] m1s)
        {
            var rep = new WeeklyReport { AsOf = new DateTime(2026, 8, 25, 16, 30, 0) };
            var run = new WeeklyRun { Title = "BOJ · JPY", RefPct = 0.977 };
            for (int i = 0; i < m1s.Length; i++)
                run.Rows.Add(new WeeklyMeeting
                {
                    Date = new DateTime(2026, 9, 24).AddMonths(i), MidPct = 1.2 + i * 0.07,
                    M1Bp = m1s[i],
                });
            rep.Runs.Add(run);
            return rep;
        }

        [Fact]
        public void OutlierGuard_FlagsTheOneRowFarOffItsRunMedian()
        {
            // the live BOJ case of 2026-08-25: +4.9 in a strip of ~+11 — exactly one CHECK note
            var notes = OutlierGuard.Check(BojReport(11.0, 4.87, 10.62, 11.0, 11.13, 13.28, 13.38));
            var check = Assert.Single(notes);
            Assert.StartsWith("CHECK: BOJ", check);
            Assert.Contains("+4.9bp", check);
            Assert.Contains("verify before distribution", check);
        }

        [Fact]
        public void OutlierGuard_StaysQuiet_OnCalmAndOnUniformlyVolatileRuns()
        {
            Assert.Empty(OutlierGuard.Check(BojReport(0.4, -0.2, 0.1, 0.5, -0.3, 0.2)));   // calm
            Assert.Empty(OutlierGuard.Check(BojReport(5, -3, 8, -6, 2, 9)));               // all jumpy
            Assert.Empty(OutlierGuard.Check(BojReport(11.0, 4.87, 10.62)));                // <4 rows
        }

        [Fact]
        public void FindCc_AcceptsBothSpellings_AndPrefersTheRunsHome()
        {
            var root = Path.Combine(_dir, "drive");
            var cc = Path.Combine(root, "Coverage & Counterparties");
            Directory.CreateDirectory(Path.Combine(root, "Other"));
            Directory.CreateDirectory(cc);
            Assert.Equal(cc, SaveDownConfig.FindCc(root));
            var home = Path.Combine(cc, "OIS and Inflation Runs");
            Directory.CreateDirectory(home);
            Assert.Equal(home, SaveDownConfig.FindCc(root));

            var root2 = Path.Combine(_dir, "drive2");
            var cc2 = Path.Combine(root2, "shared", "Coverage and Counterparties");
            Directory.CreateDirectory(cc2);
            Assert.Equal(cc2, SaveDownConfig.FindCc(root2));   // one level down, "and" spelling
        }

        [Fact]
        public void OisStoreBook_KeepsTheVbaProjectAndButtons_AndFillsTheTables()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "s.db"));
            var rep = new WeeklyReport { AsOf = new DateTime(2026, 8, 20, 16, 30, 0) };
            var ecb = new WeeklyRun { Title = "ECB · EUR", RefPct = 2.188 };
            ecb.Rows.Add(new WeeklyMeeting
                { Date = new DateTime(2026, 9, 16), MidPct = 2.432, PricedBp = 24.4, D1Bp = 0.3 });
            ecb.Rows.Add(new WeeklyMeeting
                { Date = new DateTime(2026, 11, 4), MidPct = 2.490, PricedBp = 30.2, StepBp = 5.8 });
            rep.Runs.Add(ecb);

            var path = StoreBooks.WriteOis(rep, store, _dir);

            Assert.Equal("OIS_Runs_20August26.xlsm", Path.GetFileName(path));
            // the whole point of the format: the store machinery must SURVIVE the app's fill
            using (var zip = ZipFile.OpenRead(path))
            {
                Assert.Contains(zip.Entries, e => e.FullName == "xl/vbaProject.bin");
                Assert.Contains(zip.Entries, e => e.FullName.StartsWith("xl/ctrlProps/"));
            }
            using var wb = new XLWorkbook(path);
            // the INCUMBENT entry pages travel intact (desk 2026-08-25: replicate exactly) —
            // the app never writes them, so their tables and formulas must simply exist
            Assert.NotNull(wb.Worksheet("Current").Table("current_eu"));
            Assert.NotNull(wb.Worksheet("Current").Table("current_sek"));
            Assert.True(wb.TryGetWorksheet("Vandit", out _));   // the manual-generation page
            // the app's history fill CLEARED the incumbent's own rows (empty store here)
            var histEu = wb.Worksheet("Historical_EU").Table("history_eu");
            Assert.Equal(1, histEu.DataRange.RowCount());
            Assert.True(histEu.DataRange.Cell(1, 1).IsEmpty());
            var runsText = string.Join("\n", wb.Worksheet("Runs").CellsUsed().Select(c => c.GetString()));
            Assert.Contains("ECB closing run", runsText);
        }

        [Fact]
        public void InflStoreBook_KeepsVba_WritesCopyBlocksAndHistory()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "i.db"));
            // a print so Base/Mid derive, and one stored close so history writes
            store.UpsertDaily("CPURNSA Index",
                new[] { new HistPoint(new DateTime(2025, 8, 31), 323.976) }, excludeToday: false);
            store.UpsertFixings("CPI", "2026-08",
                new[] { new HistPoint(new DateTime(2026, 8, 19), 336.8) }, "bbg", excludeToday: false);
            var marks = new Dictionary<string, List<InflHistory.Mark>>
            {
                ["CPI"] = new() { new InflHistory.Mark(new DateTime(2026, 8, 1), 337.02) },
            };

            var path = StoreBooks.WriteInfl(store, _dir, new DateTime(2026, 8, 20), marks);

            Assert.Equal("Inflation_Runs_20August26.xlsm", Path.GetFileName(path));
            using (var zip = ZipFile.OpenRead(path))
                Assert.Contains(zip.Entries, e => e.FullName == "xl/vbaProject.bin");
            using var wb = new XLWorkbook(path);
            // the incumbent entry pages travel intact — the app never writes Copy or Table
            Assert.True(wb.TryGetWorksheet("Copy", out _));
            Assert.True(wb.TryGetWorksheet("Table", out _));
            // the History page is REWRITTEN by the app: incumbent rows out, unified record in,
            // every column populated (Date | Month | Base | Mid | %yoy | %mom)
            var hist = wb.Worksheet("CPI_History");
            Assert.Equal("Aug", hist.Cell(2, 2).GetString());
            Assert.Equal(323.976, hist.Cell(2, 3).GetDouble(), 6);               // Base = print
            Assert.Equal(336.8, hist.Cell(2, 4).GetDouble(), 6);                 // stored close
            Assert.Equal((336.8 / 323.976 - 1) * 100, hist.Cell(2, 5).GetDouble(), 6);   // %yoy
            Assert.True(hist.Cell(3, 1).IsEmpty());          // exactly one unified row remains
        }
    }
}
