using ClosedXML.Excel;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Infl;

namespace RateDesk.Tests
{
    /// <summary>The unified inflation-fixings history (desk 2026-08-25): merge rule, the
    /// base-print validation gate (incl. the pricer's label-shift export bug), maturity-
    /// documented Bloomberg mapping, and the export workbook.</summary>
    public class InflTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-infl-" + Guid.NewGuid().ToString("N"));
        public InflTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void MergeRule_ValidatedSheetWins_BloombergFillsAndNeverOverwrites()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "m.db"));
            var d1 = new DateTime(2026, 8, 10);
            var d2 = new DateTime(2026, 8, 11);

            // bbg lands first on d1; xls later replaces it (existing GOOD sheet data wins)
            store.UpsertFixings("CPI", "2026-08", new[] { new HistPoint(d1, 337.00) }, "bbg", excludeToday: false);
            store.UpsertFixings("CPI", "2026-08", new[] { new HistPoint(d1, 337.02) }, "xls", excludeToday: false);
            // xls lands first on d2; a later bbg pull must NOT overwrite it
            store.UpsertFixings("CPI", "2026-08", new[] { new HistPoint(d2, 337.10) }, "xls", excludeToday: false);
            store.UpsertFixings("CPI", "2026-08", new[] { new HistPoint(d2, 999.0) }, "bbg", excludeToday: false);

            var h = store.GetFixingHistory("CPI").ToDictionary(x => x.Date);
            Assert.Equal(337.02, h[d1].Value, 6); Assert.Equal("xls", h[d1].Source);
            Assert.Equal(337.10, h[d2].Value, 6); Assert.Equal("xls", h[d2].Source);
        }

        [Fact]
        public void Ingest_ValidatesAgainstPrints_RekeysLabelShift_DropsCopiesPlaceholdersInconsistent()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "i.db"));
            // published CPURNSA prints, stamped at the reference month (as the store keeps them)
            var prints = new (int m, int y, double v)[]
            {
                (5, 2025, 321.465), (6, 2025, 322.561), (7, 2025, 323.048), (8, 2025, 323.976),
                (9, 2025, 324.8), (10, 2025, 325.604), (11, 2025, 324.122), (12, 2025, 324.054),
                (1, 2026, 325.252), (2, 2026, 326.785), (3, 2026, 327.6), (4, 2026, 328.1),
            };
            store.UpsertDaily("CPURNSA Index",
                prints.Select(p => new HistPoint(new DateTime(p.y, p.m, DateTime.DaysInMonth(p.y, p.m)), p.v)),
                excludeToday: false);

            // rebuild the REAL corrupt save shape seen on 18-May-26: eight good rows, the
            // Jan/Feb slots exact copies of Nov/Dec, the Mar/Apr slots carrying Jan-27/Feb-27
            // fixings (their Base = the Jan-26/Feb-26 prints), plus a placeholder and an
            // internally inconsistent row
            var book = Path.Combine(_dir, "infl.xlsm");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("CPI_History");
                string[] hdr = { "Date", "Month", "Base", "Mid", "%yoy", "%mom" };
                for (int c = 0; c < hdr.Length; c++) ws.Cell(1, c + 1).Value = hdr[c];
                var rows = new (string mon, double baseV, double mid)[]
                {
                    ("May", 321.465, 335.48), ("Jun", 322.561, 336.69), ("Jul", 323.048, 336.77),
                    ("Aug", 323.976, 337.02), ("Sep", 324.8, 337.45), ("Oct", 325.604, 337.26),
                    ("Nov", 324.122, 336.72), ("Dec", 324.054, 336.68),
                    ("Jan", 324.122, 336.72),   // copy of Nov — label shifted
                    ("Feb", 324.054, 336.68),   // copy of Dec
                    ("Mar", 325.252, 337.88),   // actually the Jan-27 fixing (Base = Jan-26 print)
                    ("Apr", 326.785, 339.12),   // actually Feb-27
                    ("May", 321.465, 0.0),      // placeholder save
                    ("Jun", 322.561, 336.69),   // inconsistent: yoy wildly off Mid/Base-1
                };
                int r = 2;
                foreach (var (mon, baseV, mid) in rows)
                {
                    ws.Cell(r, 1).Value = new DateTime(2026, 5, 18);
                    ws.Cell(r, 2).Value = mon;
                    ws.Cell(r, 3).Value = baseV;
                    ws.Cell(r, 4).Value = mid;
                    ws.Cell(r, 5).Value = r == 15 ? 9.99 : (mid / baseV - 1) * 100.0;
                    r++;
                }
                wb.SaveAs(book);
            }

            var res = InflHistory.Ingest(book, store);

            Assert.Equal(1, res.Placeholders);
            Assert.Equal(1, res.Inconsistent);
            Assert.Equal(2, res.DupeCopies);       // the Jan/Feb copies of Nov/Dec
            Assert.Equal(4, res.Rekeyed);          // Jan, Feb (then dropped), Mar, Apr
            var hist = store.GetFixingHistory("CPI");
            Assert.Equal(10, hist.Count);          // 8 good on label + Mar->Jan-27 + Apr->Feb-27
            var byFix = hist.ToDictionary(x => x.Fix, x => x.Value);
            Assert.Equal(335.48, byFix["2026-05"], 6);
            Assert.Equal(337.88, byFix["2027-01"], 6);   // re-keyed to what its Base proves
            Assert.Equal(339.12, byFix["2027-02"], 6);
            Assert.False(byFix.ContainsKey("2027-03"));  // the shifted label never enters
            Assert.All(hist, x => Assert.Equal("xls", x.Source));
        }

        [Fact]
        public void Maintain_MapsClosesViaRecordedMaturity_SkipsUndocumentedDays()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "b.db"));
            var tk = "USSWIF8 Curncy";
            // maturity first recorded 10-Aug: 01-Nov-26 => the Aug-2026 fixing (+3m lag)
            store.SetMaturity(tk, new DateTime(2026, 8, 10), new DateTime(2026, 11, 1));
            store.UpsertDaily(tk, new[]
            {
                new HistPoint(new DateTime(2026, 8, 7), 336.5),    // BEFORE any maturity record
                new HistPoint(new DateTime(2026, 8, 10), 337.0),
                new HistPoint(new DateTime(2026, 8, 11), 337.5),
            }, excludeToday: false);
            // a validated sheet mark already sits on the 11th — bbg must not overwrite it
            store.UpsertFixings("CPI", "2026-08", new[] { new HistPoint(new DateTime(2026, 8, 11), 337.51) },
                "xls", excludeToday: false);

            InflHistory.Maintain(store, lookbackDays: 30);

            var h = store.GetFixingHistory("CPI").Where(x => x.Fix == "2026-08")
                .ToDictionary(x => x.Date.Day);
            Assert.False(h.ContainsKey(7));                    // undocumented day skipped, not guessed
            Assert.Equal(337.0, h[10].Value, 6); Assert.Equal("bbg", h[10].Source);
            Assert.Equal(337.51, h[11].Value, 6); Assert.Equal("xls", h[11].Source);
        }

        [Fact]
        public void RefMonth_DerivesLagFromTheMaturityItself()
        {
            Assert.Equal(new DateTime(2026, 8, 1), InflHistory.RefMonth(new DateTime(2026, 11, 1), 8));  // USD +3
            Assert.Equal(new DateTime(2026, 8, 1), InflHistory.RefMonth(new DateTime(2026, 10, 15), 8)); // GBP +2
            Assert.Equal(new DateTime(2027, 1, 1), InflHistory.RefMonth(new DateTime(2027, 4, 15), 1));  // EUR +3
            Assert.Null(InflHistory.RefMonth(new DateTime(2026, 11, 1), 3));   // no lag 1-6 fits => skip
        }

        [Fact]
        public void Ingest_AdoptsSheetBase_ForBloombergPrintHoles_ButNeverForForecastBases()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "gap.db"));
            // Bloomberg's CPURNSA has Sep-25 and Nov-25 but NO Oct-25 (the shutdown skip)
            store.UpsertDaily("CPURNSA Index", new[]
            {
                new HistPoint(new DateTime(2025, 9, 30), 324.8),
                new HistPoint(new DateTime(2025, 11, 30), 324.122),
            }, excludeToday: false);

            var book = Path.Combine(_dir, "gap.xlsm");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("CPI_History");
                string[] hdr = { "Date", "Month", "Base", "Mid", "%yoy", "%mom" };
                for (int c = 0; c < hdr.Length; c++) ws.Cell(1, c + 1).Value = hdr[c];
                void Row(int r, string mon, double baseV, double mid)
                {
                    ws.Cell(r, 1).Value = new DateTime(2026, 8, 20);
                    ws.Cell(r, 2).Value = mon;
                    ws.Cell(r, 3).Value = baseV;
                    ws.Cell(r, 4).Value = mid;
                    ws.Cell(r, 5).Value = (mid / baseV - 1) * 100.0;
                }
                Row(2, "Sep", 324.8, 336.01);     // validates against the real Sep-25 print
                Row(3, "Oct", 325.604, 335.81);   // base month PASSED, print missing -> ADOPT
                Row(4, "Nov", 324.122, 335.29);   // validates against Nov-25
                // a Jul-27 fixing whose base month (Jul-26) is still inside the publication
                // lag at the 20-Aug-26 obs = a FORECAST base — must never be adopted
                ws.Cell(5, 1).Value = new DateTime(2026, 8, 20);
                ws.Cell(5, 2).Value = new DateTime(2027, 7, 1);
                ws.Cell(5, 3).Value = 999.99;
                ws.Cell(5, 4).Value = 1020.0;
                ws.Cell(5, 5).Value = (1020.0 / 999.99 - 1) * 100.0;
                wb.SaveAs(book);
            }

            InflHistory.Ingest(book, store);

            var prints = store.GetDailyWithSource("CPURNSA Index", 4000)
                .ToDictionary(x => (x.Date.Month, x.Date.Year));
            Assert.Equal(325.604, prints[(10, 2025)].Value, 6);      // the hole, filled
            Assert.Equal("xls", prints[(10, 2025)].Source);          // marked as the sheet's
            Assert.Equal("bbg", prints[(9, 2025)].Source);           // real prints untouched
            Assert.False(prints.ContainsKey((7, 2026)));             // forecast base NOT adopted
            // and a later real Bloomberg print for Oct-25 supersedes the adopted value
            store.UpsertDaily("CPURNSA Index",
                new[] { new HistPoint(new DateTime(2025, 10, 31), 325.7) }, excludeToday: false);
            Assert.Equal(325.7, store.GetDailyWithSource("CPURNSA Index", 4000)
                .First(x => x.Date == new DateTime(2025, 10, 31)).Value, 6);
        }

        [Fact]
        public void DisplayRows_DeriveBaseYoyMomAndIndexChanges_FromMarksPrintsAndHistory()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "dr.db"));
            // prints: Aug-25 (the YoY base) and Jul-26 (the last published index — the front
            // row's MoM anchor, exactly the incumbent Table's convention)
            store.UpsertDaily("CPURNSA Index", new[]
            {
                new HistPoint(new DateTime(2025, 8, 31), 323.976),
                new HistPoint(new DateTime(2026, 7, 31), 323.048),
            }, excludeToday: false);
            // unified history: yesterday's close for the Aug-26 fixing
            store.UpsertFixings("CPI", "2026-08",
                new[] { new HistPoint(new DateTime(2026, 8, 19), 334.98) }, "bbg", excludeToday: false);
            var fam = InflHistory.Families.First(f => f.Key == "CPI");
            var marks = new[]
            {
                new InflHistory.Mark(new DateTime(2026, 8, 1), 335.02),
                new InflHistory.Mark(new DateTime(2026, 9, 1), 336.13),
            };

            var rows = InflHistory.BuildDisplayRows(store, fam, marks, new DateTime(2026, 8, 20));

            Assert.Equal(2, rows.Count);
            var aug = rows[0];
            Assert.Equal(323.976, aug.BaseV!.Value, 6);
            Assert.Equal(335.02, aug.Mid!.Value, 6);
            Assert.Equal((335.02 / 323.976 - 1) * 100, aug.Yoy!.Value, 6);
            Assert.Equal((335.02 / 323.048 - 1) * 100, aug.Mom!.Value, 6);   // front row vs Jul print
            Assert.Equal(335.02 - 334.98, aug.D1!.Value, 6);                 // vs yesterday's close
            // second row's MoM chains off the FIRST row's mid, not a print
            Assert.Equal((336.13 / 335.02 - 1) * 100, rows[1].Mom!.Value, 6);
        }

        [Fact]
        public void InflEmailAndLeanXlsx_DropTheFurthestFixing_AndCarryNextPrint()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "ie.db"));
            store.UpsertDaily("CPURNSA Index",
                new[] { new HistPoint(new DateTime(2025, 8, 31), 323.976) }, excludeToday: false);
            var marks = new Dictionary<string, List<InflHistory.Mark>>
            {
                ["CPI"] = new()
                {
                    new InflHistory.Mark(new DateTime(2026, 8, 1), 335.02),
                    new InflHistory.Mark(new DateTime(2026, 9, 1), 336.13),   // furthest — dropped
                },
            };
            var nextPrints = new Dictionary<string, DateTime> { ["CPI"] = new(2026, 9, 11) };

            var html = InflEmail.WriteFragments(store, marks, nextPrints,
                new DateTime(2026, 8, 20), _dir, daily: true);
            Assert.Contains("Inflation Fixing Runs", html);
            // Word breaks at spaces even under nowrap, so all multi-word cell text is &nbsp;-joined
            Assert.Contains("Next&nbsp;Print:&nbsp;11-Sep-26", html);
            Assert.Contains("Aug&nbsp;26", html);
            Assert.DoesNotContain("Sep&nbsp;26", html);     // furthest fixing dropped
            Assert.Contains("<td nowrap width=", html);     // widths live ON the cells (Word rule)
            Assert.True(File.Exists(Path.Combine(_dir, InflEmail.DailyHtmlFile)));
            Assert.True(File.Exists(Path.Combine(_dir, InflEmail.DailyTextFile)));

            var path = InflRunsXlsx.Write(store, _dir, new DateTime(2026, 8, 20), marks, nextPrints);
            Assert.Equal("DRAX Fixing Runs 20Aug26.xlsx", Path.GetFileName(path));
            using var wb = new ClosedXML.Excel.XLWorkbook(path);
            var text = string.Join("\n", wb.Worksheet("Runs").CellsUsed().Select(c => c.GetString()));
            Assert.Contains("US CPI Fixing Run", text);
            Assert.Contains("Next Print:", text);
            Assert.Single(wb.Worksheets);                    // LEAN: no history sheets
        }

        [Fact]
        public void Book_WritesPerFamilySheets_WithSameFixingChangesAndSource()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "x.db"));
            store.UpsertFixings("RPI", "2026-12", new[]
            {
                new HistPoint(new DateTime(2026, 3, 2), 355.0),
                new HistPoint(new DateTime(2026, 3, 3), 358.0),
            }, "xls", excludeToday: false);
            store.UpsertFixings("RPI", "2026-12", new[]
                { new HistPoint(new DateTime(2026, 3, 4), 360.5) }, "bbg", excludeToday: false);

            var path = InflBook.Write(store, _dir);

            Assert.Equal("Inflation_Fixings_History.xlsx", Path.GetFileName(path));
            using var wb = new XLWorkbook(path);
            Assert.True(wb.TryGetWorksheet("Hist_RPI", out var ws));
            Assert.True(wb.TryGetWorksheet("Hist_CPI", out _));
            Assert.True(wb.TryGetWorksheet("Hist_HICP", out _));
            // row 4 = 04-Mar bbg row: value 360.5, delta 1d = +2.5 vs 03-Mar, source plain bbg
            Assert.Equal(360.5, ws!.Cell(4, 3).GetDouble(), 6);
            Assert.Equal(2.5, ws.Cell(4, 4).GetDouble(), 6);
            Assert.Equal("bbg", ws.Cell(4, 7).GetString());
            Assert.False(ws.Cell(4, 7).Style.Font.Bold);
            Assert.True(ws.Cell(3, 7).Style.Font.Bold);   // xls provenance bold, OIS convention
        }
    }
}
