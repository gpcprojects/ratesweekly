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
