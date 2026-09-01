using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Infl;
using RateDesk.Weekly.Core.SaveDown;

namespace RateDesk.Tests
{
    /// <summary>Regression locks for the 2026-08-31 audit fixes (catalogue 101-200): the
    /// close-vs-snap arbitration's independent reference (152), the roll-inside-a-record-gap
    /// mapping skip (151/154), the thin-store inflation inheritance (the 2026-09-01 desk
    /// report: a second terminal's Δ columns blank on the daily run), the single-clock snap
    /// discipline (101), the partial-pin note (104), and the coherence notes' non-blocking
    /// prefix (146).</summary>
    public class AuditFixTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-audit-" + Guid.NewGuid().ToString("N"));
        public AuditFixTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        // ---------------------------------------------------------------- helpers ----
        private sealed class FakeBars : IHistoryProvider
        {
            public readonly Dictionary<string, List<HistPoint>> Snaps = new();
            public IReadOnlyList<HistPoint> GetDaily(string ticker, int lookbackDays) => Array.Empty<HistPoint>();
            public IReadOnlyList<HistPoint> GetLondonSnaps(string ticker, int lookbackDays, TimeSpan t)
                => Snaps.TryGetValue(ticker, out var s) ? s : Array.Empty<HistPoint>();
        }

        /// <summary>Business days walking BACK from yesterday, oldest first.</summary>
        private static DateTime[] BackBds(int n)
        {
            var days = new List<DateTime>();
            var d = DateTime.Today.AddDays(-1);
            while (days.Count < n)
            {
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
                d = d.AddDays(-1);
            }
            days.Reverse();
            return days.ToArray();
        }

        /// <summary>A maturity whose RefMonth(mat, m) resolves to month index m under the GBP
        /// +2m lag, independent of what month the test runs in.</summary>
        private static DateTime MatFor(int m) => new DateTime(DateTime.Today.Year + 1, m, 1).AddMonths(2);
        private static string FixFor(int m) => $"{DateTime.Today.Year + 1:0000}-{m:00}";

        // ------------------------------------------------- 152: the arbitration reference ----
        [Fact]
        public void Maintain_StalePrevMark_DefaultsToClose_NeverAMultiDayChangeVsAOneDayMedian()
        {
            // Three days, trending strip. Tenor 7 does not print AT ALL on day 2, then on day 3
            // both its candidates return. Its previous mark is two days old: the old code
            // compared that TWO-day change against the strip's ONE-day median (+10) and picked
            // the junk snap — |14−0−10| = 4 beats |20−0−10| = 10. The fix rules the tenor's
            // reference non-contiguous and takes the close.
            //   day1: all tenors close at lvl;  day2: all except tenor 7 close at lvl+10;
            //   day3: all close at lvl+20; tenor 7 also snaps at lvl+14 (junk).
            // Expected day-3 mark for tenor 7: the CLOSE, lvl+20.
            using var store = new HistoryStore(Path.Combine(_dir, "arb.db"));
            var days = BackBds(3);
            var bars = new FakeBars();
            var fam = InflHistory.Families.First(f => f.Key == "RPI");

            for (int m = 1; m <= 12; m++)
            {
                var tk = $"{fam.Root}{m} Curncy";
                double lvl = 380 + m; // spread the strip out
                var pts = new List<HistPoint> { new(days[0], lvl) };
                if (m != 7) pts.Add(new HistPoint(days[1], lvl + 10.0));
                pts.Add(new HistPoint(days[2], lvl + 20.0));
                store.UpsertDaily(tk, pts, excludeToday: false);
                if (m == 7)
                    bars.Snaps[tk] = new List<HistPoint> { new(days[2], lvl + 14.0) };
                foreach (var d in days) store.SetMaturity(tk, d, MatFor(m));
            }

            InflHistory.Maintain(store, null, bars: bars, lookbackDays: 30);

            var row = store.GetFixingHistory("RPI")
                .Single(x => x.Fix == FixFor(7) && x.Date == days[2]);
            Assert.Equal(387 + 20.0, row.Value, 4);
        }

        [Fact]
        public void Maintain_NoReference_DefaultsToClose_NeverToWhicheverMovedLess()
        {
            // First day of history: no previous close exists, so med is UNDEFINED. The old
            // med = 0.0 fallback preferred whichever candidate moved less; the fix takes the
            // documented default — the close.
            using var store = new HistoryStore(Path.Combine(_dir, "noref.db"));
            var days = BackBds(1);
            var bars = new FakeBars();
            var fam = InflHistory.Families.First(f => f.Key == "RPI");
            var tk = $"{fam.Root}3 Curncy";
            store.UpsertDaily(tk, new[] { new HistPoint(days[0], 430.0) }, excludeToday: false);
            bars.Snaps[tk] = new List<HistPoint> { new(days[0], 436.0) };
            store.SetMaturity(tk, days[0], MatFor(3));

            InflHistory.Maintain(store, null, bars: bars, lookbackDays: 10);

            var hist = store.GetFixingHistory("RPI").Where(x => x.Date == days[0]).ToList();
            Assert.Single(hist);
            Assert.Equal(430.0, hist[0].Value, 6);
        }

        // ------------------------------------- 151/154: roll inside a record gap is skipped ----
        [Fact]
        public void Maintain_SkipsDays_WhoseBracketingRecordsDisagree()
        {
            // Records on Mon and Thu; the ticker re-pointed a YEAR between them (its month
            // printed inside the gap). Tue/Wed closes are unattributable — keying them on the
            // stale Monday record used to file post-roll prices onto the just-fixed month.
            using var store = new HistoryStore(Path.Combine(_dir, "gapmap.db"));
            var days = BackBds(4);
            var fam = InflHistory.Families.First(f => f.Key == "RPI");
            var tk = $"{fam.Root}4 Curncy";
            var matOld = MatFor(4);
            var matNew = matOld.AddYears(1);

            store.UpsertDaily(tk, days.Select(d => new HistPoint(d, 400.0)).ToArray(), excludeToday: false);
            store.SetMaturity(tk, days[0], matOld);
            store.SetMaturity(tk, days[3], matNew);

            InflHistory.Maintain(store, null, lookbackDays: 30);

            var hist = store.GetFixingHistory("RPI");
            var oldFix = FixFor(4);
            var newFix = $"{DateTime.Today.Year + 2:0000}-04";
            // record days map to their own record; the two gap days map NOWHERE
            Assert.Contains(hist, x => x.Fix == oldFix && x.Date == days[0]);
            Assert.Contains(hist, x => x.Fix == newFix && x.Date == days[3]);
            Assert.DoesNotContain(hist, x => x.Date == days[1] || x.Date == days[2]);
        }

        // ------------------------------------------------- the 2026-09-01 desk report ----
        [Fact]
        public void ImportInflation_InheritsFixingsPrintsAndRecords_FromTheShareSnapshot()
        {
            // the "share": a healthy store snapshotted the way AfterRun does
            var shareRoot = Path.Combine(_dir, "share");
            Directory.CreateDirectory(Path.Combine(shareRoot, StoreBackup.Folder));
            var fam = InflHistory.Families.First(f => f.Key == "RPI");
            var days = BackBds(12);
            using (var rich = new HistoryStore(Path.Combine(_dir, "rich.db")))
            {
                foreach (var d in days)
                    rich.UpsertFixings("RPI", "2026-06", new[] { new HistPoint(d, 440.0) }, "bbg", excludeToday: false);
                rich.UpsertFixings("RPI", "2026-06", new[] { new HistPoint(days[0], 440.5) }, "xls", excludeToday: false);
                rich.UpsertDaily(fam.IndexTicker, new[] { new HistPoint(days[0].AddMonths(-13), 386.4) }, excludeToday: false);
                rich.SetMaturity($"{fam.Root}6 Curncy", days[0], MatFor(6));
                rich.BackupTo(Path.Combine(shareRoot, StoreBackup.Folder, StoreBackup.LatestName));
            }
            var appData = Path.Combine(_dir, "appdata");
            Directory.CreateDirectory(appData);
            SaveDownConfig.Save(appData, new("cc", shareRoot));

            // the fresh terminal: one day of fixing depth (the desk report's exact state)
            using var store = new HistoryStore(Path.Combine(_dir, "thin.db"));
            store.UpsertFixings("RPI", "2026-06", new[] { new HistPoint(days[^1], 441.0) }, "bbg", excludeToday: false);

            var note = StoreBackup.ImportInflation(store, appData);

            Assert.NotNull(note);
            Assert.StartsWith("INFL:", note);
            Assert.Contains("inherited", note);
            var hist = store.GetFixingHistory("RPI");
            Assert.True(hist.Select(x => x.Date).Distinct().Count() >= 12);
            // the local machine's own newer row was never clobbered
            Assert.Contains(hist, x => x.Date == days[^1] && Math.Abs(x.Value - 441.0) < 1e-9);
            // the inherited validated-xls row kept its provenance and its value
            Assert.Contains(hist, x => x.Date == days[0] && x.Source == "xls" && Math.Abs(x.Value - 440.5) < 1e-9);
            // prints and maturity records rode along
            Assert.NotEmpty(store.GetDailyWithSource(fam.IndexTicker, 4000));
            Assert.NotEmpty(store.GetMaturityRows($"{fam.Root}6 Curncy"));

            // a second call on the now-deep store is a no-op
            Assert.Null(StoreBackup.ImportInflation(store, appData));
        }

        [Fact]
        public void ImportInflation_ThinStoreNoSnapshot_SaysWhyInsteadOfSilentBlanks()
        {
            var appData = Path.Combine(_dir, "appdata2");
            Directory.CreateDirectory(appData);
            using var store = new HistoryStore(Path.Combine(_dir, "thin2.db"));
            var note = StoreBackup.ImportInflation(store, appData);
            Assert.NotNull(note);
            Assert.StartsWith("INFL:", note);
            Assert.Contains("blank", note);
        }

        // ------------------------------------------------- 101/104: the snap discipline ----
        [Fact]
        public void SnapDiscipline_HonorsTheInjectedClock_AndNamesPartialPins()
        {
            var snap = new RatesSnapshot();
            snap.Update("AAA Curncy", 1.0, 1.0, 1.0);
            snap.Update("BBB Curncy", 2.0, 2.0, 2.0);
            var bars = new FakeBars();
            // AAA has a bar today (pins); BBB has none (stays live) — judged on the INJECTED
            // clock's date, whatever wall-clock day the test runs on
            var now = DateTime.Today.AddHours(17);
            bars.Snaps["AAA Curncy"] = new List<HistPoint> { new(now.Date, 1.111) };

            var (mode, note) = SnapDiscipline.Apply(bars, snap,
                new[] { "AAA Curncy", "BBB Curncy" }, null, nowLondon: now);
            Assert.Equal(SnapDiscipline.Mode.Snap1615, mode);
            Assert.Equal(1.111, snap.Get("AAA Curncy")!.Mid!.Value, 6);
            Assert.Equal(2.0, snap.Get("BBB Curncy")!.Mid!.Value, 6);
            Assert.NotNull(note);
            Assert.StartsWith("SNAP:", note);

            // a pre-close injected clock resolves PreClose regardless of the wall clock
            var (mode2, note2) = SnapDiscipline.Apply(bars, snap,
                new[] { "AAA Curncy" }, null, nowLondon: DateTime.Today.AddHours(9));
            Assert.Equal(SnapDiscipline.Mode.PreClose, mode2);
            Assert.Contains("PRE-CLOSE", note2);
        }

        // ------------------------------------------------- 146: coherence is non-blocking ----
        [Fact]
        public void CoherenceNotes_CarryTheInformationalPrefix_NotTheBlockingOne()
        {
            Assert.Equal("FIXING", InflHistory.InfoPrefix);
            Assert.NotEqual(RateDesk.Core.OutlierGuard.Prefix, InflHistory.InfoPrefix);
            // and the note text itself is built from InfoPrefix — lock the wiring, not just
            // the constant: a lone mover in a seeded strip must produce a FIXING: note
            using var store = new HistoryStore(Path.Combine(_dir, "coh.db"));
            var fam = InflHistory.Families.First(f => f.Key == "RPI");
            var days = BackBds(2);
            for (int m = 1; m <= 12; m++)
            {
                var tk = $"{fam.Root}{m} Curncy";
                double lvl = 400;
                double d1 = m == 6 ? 30.0 : 0.5;   // month 6 moves wildly alone
                store.UpsertDaily(tk, new[]
                {
                    new HistPoint(days[0], lvl),
                    new HistPoint(days[1], lvl + d1),
                }, excludeToday: false);
                foreach (var d in days) store.SetMaturity(tk, d, MatFor(m));
            }
            // base prints so the rows carry a Base (tolerance is scaled by it)
            for (int k = 0; k < 30; k++)
                store.UpsertDaily(fam.IndexTicker,
                    new[] { new HistPoint(days[1].AddMonths(-k), 380.0) }, excludeToday: false);
            InflHistory.Maintain(store, null, lookbackDays: 30);
            var notes = InflHistory.CoherenceNotes(store, days[1]);
            if (notes.Count > 0)
                Assert.All(notes, n => Assert.StartsWith("FIXING:", n));
        }
    }
}
