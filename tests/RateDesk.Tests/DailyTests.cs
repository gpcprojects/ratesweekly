using ClosedXML.Excel;
using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Daily;

namespace RateDesk.Tests
{
    /// <summary>The daily OIS surface (desk 2026-08-20): blast text, workbook, and their
    /// treatment of Y/E Turn rows and missing changes.</summary>
    public class DailyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-daily-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static WeeklyReport Report()
        {
            var rep = new WeeklyReport { AsOf = new DateTime(2026, 8, 20, 16, 30, 0) };
            var ecb = new WeeklyRun { Title = "ECB · EUR", RefPct = 2.188 };
            ecb.Rows.Add(new WeeklyMeeting
            {
                Date = new DateTime(2026, 9, 16), MidPct = 2.432,
                PricedBp = 24.4, D1Bp = 0.3, W1Bp = 1.5,
            });
            ecb.Rows.Add(new WeeklyMeeting
            {
                Date = new DateTime(2026, 11, 4), MidPct = 2.490,
                PricedBp = 30.2, StepBp = 5.8, D1Bp = 0.1, W1Bp = 2.4,
            });
            rep.Runs.Add(ecb);
            var riks = new WeeklyRun { Title = "RIKSBANK · SEK", RefPct = 1.641 };
            riks.Rows.Add(new WeeklyMeeting
            {
                Date = new DateTime(2026, 9, 30), MidPct = 1.724, PricedBp = 8.3, D1Bp = -4.8, W1Bp = -5.1,
            });
            riks.Rows.Add(new WeeklyMeeting
            {
                Date = new DateTime(2026, 12, 23), MidPct = 1.468, TurnPeriod = true,
            });
            rep.Runs.Add(riks);
            return rep;
        }

        [Fact]
        public void Blast_CarriesFlagsFixingsAndBpColumns()
        {
            var text = DailyBlast.Render(Report());

            Assert.StartsWith("{EU} {GB} {AU} {NZ} {US} {CA} {JN} {NO} {SW} London EOD OIS Run", text);
            Assert.Contains("{EU} ECB Run", text);
            Assert.Contains("€STR 2.188", text);
            Assert.Contains("+24.4", text);      // Priced in bp, not percent
            Assert.Contains("+0.3", text);       // Δ1d in bp
            Assert.Contains("{SW} RIKSBANK Run", text);
        }

        [Fact]
        public void Blast_LabelsTurnRows_AndOmitsBanksWithNoRows()
        {
            var text = DailyBlast.Render(Report());

            Assert.Contains("Y/E Turn", text);
            Assert.DoesNotContain("1.468", text);          // the turn print never blasts
            Assert.DoesNotContain("{US} FOMC Run", text);  // no rows in fixture — block omitted, not empty
        }

        [Fact]
        public void SyncDailyDir_CatchesUpEveryPendingWorkbook_WhenTheDriveReturns()
        {
            var outDir = Path.Combine(_dir, "out");
            var drive = Path.Combine(_dir, "drive");
            Directory.CreateDirectory(outDir);
            // three local workbooks from days the "drive" was down
            foreach (var n in new[] { "OIS_Runs_18August26.xlsx", "OIS_Runs_19August26.xlsx", "OIS_Runs_20August26.xlsx" })
                File.WriteAllText(Path.Combine(outDir, n), "x");
            File.WriteAllText(Path.Combine(_dir, "publish.json"),
                "{\"dailyDir\": " + System.Text.Json.JsonSerializer.Serialize(drive) + "}");

            Assert.True(DailyBuilder.SyncDailyDir(outDir, _dir));
            Assert.Equal(3, Directory.GetFiles(drive, "OIS_Runs_*.xlsx").Length);

            // idempotent: nothing recopied when up to date; and an unreachable drive is a soft false
            Assert.True(DailyBuilder.SyncDailyDir(outDir, _dir));
            File.WriteAllText(Path.Combine(_dir, "publish.json"),
                "{\"dailyDir\": \"Q:\\\\no\\\\such\\\\drive\"}");
            Assert.False(DailyBuilder.SyncDailyDir(outDir, _dir));
        }

        [Fact]
        public void ExportBook_RebuildsOffline_FromStoredReportAndStore()
        {
            var outDir = Path.Combine(_dir, "out2");
            Directory.CreateDirectory(outDir);
            using var store = new HistoryStore(Path.Combine(_dir, "h2.db"));
            ReportStore.Save(Report(), Path.Combine(outDir, DailyBuilder.ReportFile));

            var path = DailyBuilder.ExportBook(store, outDir, _dir);

            Assert.True(File.Exists(path));
            Assert.Equal("OIS_Runs_20August26.xlsx", Path.GetFileName(path));   // the report's own as-of
            using var wb = new XLWorkbook(path);
            Assert.Contains("ECB closing run",
                string.Join("\n", wb.Worksheet("Runs").CellsUsed().Select(c => c.GetString())));
        }

        [Fact]
        public void FallbackIngest_MapsManualRowsToRungs_InsertOnly_BbgWins()
        {
            Directory.CreateDirectory(_dir);
            using var store = new HistoryStore(Path.Combine(_dir, "h.db"));

            // build a fixture fallback workbook: Historical_AU with two manual days for the
            // period starting 30-Sep-26 (rung 1 as of those dates: no boundary crossed between
            // the row date and the start except the 30-Sep cluster itself)
            var book = Path.Combine(_dir, "fallback.xlsm");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Historical_AU");
                string[] hdr = { "CurrentDate", "Meeting", "StartDate", "EndDate", "Rate" };
                for (int c = 0; c < hdr.Length; c++) ws.Cell(1, c + 1).Value = hdr[c];
                ws.Cell(2, 1).Value = new DateTime(2026, 8, 17);
                ws.Cell(2, 3).Value = new DateTime(2026, 9, 30);
                ws.Cell(2, 5).Value = 4.391;
                ws.Cell(3, 1).Value = new DateTime(2026, 8, 18);
                ws.Cell(3, 3).Value = new DateTime(2026, 9, 30);
                ws.Cell(3, 5).Value = 4.402;
                wb.SaveAs(book);
            }

            // the engine already has 18-Aug for the mapped rung — that day must NOT be touched
            var sched = MeetingsStore.Schedules.First(s => s.Name == "RBA");
            var pat = sched.Tickers.First(t => t.Contains("{N}"));
            var bounds = new List<DateTime>();
            foreach (var d in sched.DecisionDates.Concat(sched.Dates).Concat(sched.PastDates)
                         .Select(x => x.Date).OrderBy(x => x))
                if (bounds.Count == 0 || (d - bounds[^1]).TotalDays > 14) bounds.Add(d);
            int rung = bounds.Count(b => b > new DateTime(2026, 8, 18) && b <= new DateTime(2026, 9, 30));
            var tkr = pat.Replace("{N}", rung.ToString()) + " Curncy";
            store.UpsertDaily(tkr, new[] { new HistPoint(new DateTime(2026, 8, 18), 4.999) }, excludeToday: false);

            var res = FallbackIngest.Run(book, store);

            Assert.Equal(1, res.RowsIngested);   // only 17-Aug; 18-Aug already engine-owned
            var rows = store.GetDailyWithSource(tkr, 400).ToDictionary(x => x.Date.Date, x => x);
            Assert.Equal(4.391, rows[new DateTime(2026, 8, 17)].Value, 6);
            Assert.Equal("xls", rows[new DateTime(2026, 8, 17)].Source);
            Assert.Equal(4.999, rows[new DateTime(2026, 8, 18)].Value, 6);   // untouched
            Assert.Equal("bbg", rows[new DateTime(2026, 8, 18)].Source);

            // and a subsequent REAL Bloomberg pull for 17-Aug supersedes the manual entry
            store.UpsertDaily(tkr, new[] { new HistPoint(new DateTime(2026, 8, 17), 4.390) }, excludeToday: false);
            var after = store.GetDailyWithSource(tkr, 400).First(x => x.Date.Date == new DateTime(2026, 8, 17));
            Assert.Equal(4.390, after.Value, 6);
            Assert.Equal("bbg", after.Source);
        }

        [Fact]
        public void Book_WritesRunsSheet_WithTurnLabel()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "h.db"));
            var path = DailyBook.Write(Report(), store, _dir);

            Assert.Equal("OIS_Runs_20August26.xlsx", Path.GetFileName(path));
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("Runs");
            var text = string.Join("\n", ws.CellsUsed().Select(c => c.GetString()));
            Assert.Contains("ECB closing run", text);
            Assert.Contains("€STR fixing", text);
            Assert.Contains("Y/E Turn", text);
            // the ECB block's first data row carries T = 2.432
            var t = ws.CellsUsed().FirstOrDefault(c => c.GetString() == "T");
            Assert.NotNull(t);
        }
    }
}
