using ClosedXML.Excel;
using RateDesk.Core;
using RateDesk.Weekly.Core.Daily;
using RateDesk.Weekly.Core.Infl;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.SaveDown
{
    /// <summary>The MACRO-ENABLED save-down workbooks (desk 2026-08-25) — the daily files that
    /// land in the "OIS Runs" / "Inflation Runs" folders. Unlike the plain email attachments,
    /// these carry the desk's own store machinery (clean-room replica of Central Bank OIS MAIN
    /// and MOST RECENT Inflation Fixing Runs, learned from their VBA): if the app is down, the
    /// desk types today's marks into the Current/Copy sheets, presses Store, and the appended
    /// history rows are re-ingested (and validated) by the app on its next run. Built by
    /// copying an embedded .xlsm template (the VBA project travels with the file — ClosedXML
    /// preserves it) and filling the tables.</summary>
    public static class StoreBooks
    {
        private const string OisTemplate = "OIS_Store_Template.xlsm";
        private const string InflTemplate = "Inflation_Store_Template.xlsm";

        public static string OisFileName(DateTime asOf) =>
            Daily.DailyBook.FileName(asOf).Replace(".xlsx", ".xlsm");
        public static string InflFileName(DateTime asOf) =>
            "Inflation_Runs_" + Daily.DailyBook.FileName(asOf)["OIS_Runs_".Length..].Replace(".xlsx", ".xlsm");

        // ------------------------------------------------------------------ OIS ----
        public static string WriteOis(WeeklyReport rep, HistoryStore store, string outDir,
            Action<string>? log = null, int historyDays = 61)
        {
            var path = System.IO.Path.Combine(outDir, OisFileName(rep.AsOf));
            ExtractTemplate(OisTemplate, path);
            using var wb = new XLWorkbook(path);

            DailyBook.WriteRunsSheet(wb.Worksheet("Runs"), rep);

            var cur = wb.Worksheet("Current");
            foreach (var sched in MeetingsStore.Schedules.Where(s => string.IsNullOrEmpty(s.Kind)))
            {
                if (!DailyBlast.Blocks.Any(b => b.Run.Equals(sched.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var run = rep.Runs.FirstOrDefault(x =>
                    x.Title.Split('·')[0].Trim().Equals(sched.Name, StringComparison.OrdinalIgnoreCase));
                var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (run == null || pat == null || run.Rows.Count == 0) continue;
                var tag = sched.Name.ToLowerInvariant();

                // today's rows into current_xx (cols: CurrentDate, Meeting, Start, End, Rate,
                // StepDelta, Priced In(Total), Percent, 1d, 1w, 1m — the macro reads 1/3/4/5)
                var curRows = new List<object?[]>();
                for (int i = 0; i < run.Rows.Count; i++)
                {
                    var m = run.Rows[i];
                    var end = m.EndDate ?? (i + 1 < run.Rows.Count ? run.Rows[i + 1].Date : (DateTime?)null);
                    curRows.Add(new object?[]
                    {
                        rep.AsOf.Date, m.Date.ToString("MMM"), m.Date, end, m.MidPct,
                        m.StepBp, m.PricedBp, m.PricedBp / 25.0 * 100.0, m.D1Bp, m.W1Bp, m.M1Bp,
                    });
                }
                FillTable(cur, "current_" + tag, curRows);

                // trailing history into history_xx on Historical_XX — same walk as Hist_ sheets
                var histRows = DailyBook.BankHistoryRows(store, sched, run, pat, rep.AsOf, historyDays)
                    .Select(h => new object?[]
                    {
                        h.Day, h.Start.ToString("MMM"), h.Start, h.End, h.Rate,
                        null, null, null, h.D1, h.W1, h.M1,
                    }).ToList();
                FillTable(wb.Worksheet("Historical_" + sched.Name), "history_" + tag, histRows);
            }

            wb.Save();
            log?.Invoke($"save-down: wrote {System.IO.Path.GetFileName(path)} (macro-enabled)");
            return path;
        }

        // ------------------------------------------------------------- Inflation ----
        /// <summary>marks: per family the fixing marks to publish as "today" (native unit,
        /// live snapshot when the run just took one, last documented closes otherwise).</summary>
        public static string WriteInfl(HistoryStore store, string outDir, DateTime asOf,
            Dictionary<string, List<InflHistory.Mark>>? marks = null, Action<string>? log = null)
        {
            marks ??= InflHistory.LatestMarks(store);
            var path = System.IO.Path.Combine(outDir, InflFileName(asOf));
            ExtractTemplate(InflTemplate, path);
            using var wb = new XLWorkbook(path);
            // the Runs display page — same writer as the lean email attachment
            InflRunsXlsx.WriteRunsSheet(wb.Worksheet("Runs"), store, marks,
                InflHistory.LastNextPrints, asOf);
            var copy = wb.Worksheet("Copy");

            int copyBase = 1;   // block anchors at rows 1 / 18 / 35 (template layout)
            foreach (var fam in InflHistory.Families)
            {
                var famMarks = marks.TryGetValue(fam.Key, out var mm)
                    ? mm : new List<InflHistory.Mark>();
                var prints = InflHistory.PrintsOf(store, fam);   // the History sheet below needs them too
                // Month | Base | Mid | YoY% | MoM% | Daily | Weekly | Monthly — the one shared
                // derivation (email section, lean xlsx and this book all publish the same rows)
                var rows = InflHistory.BuildDisplayRows(store, fam, famMarks, asOf)
                    .Select(r => new object?[] { r.RefMonth, r.BaseV, r.Mid, r.Yoy, r.Mom, r.D1, r.W1, r.M1 })
                    .ToList();

                // Copy sheet block (B..G from its anchor): Date | Month | Base | Mid | YoY | MoM
                int cr = copyBase + 3;   // data starts 3 rows under the block title (row 4/21/38)
                foreach (var row in rows)
                {
                    copy.Cell(cr, 2).Value = asOf.Date;
                    copy.Cell(cr, 2).Style.DateFormat.Format = "dd-mmm-yy";
                    copy.Cell(cr, 3).Value = ((DateTime)row[0]!).ToString("MMM");
                    for (int c = 1; c < 5; c++)
                        if (row[c] is double v) copy.Cell(cr, c + 3).Value = v;
                    cr++;
                }
                copyBase += 17;

                // History sheet: the full unified record, newest observation first
                var hsheet = wb.Worksheet(fam.Key + "_History");
                var flat = store.GetFixingHistory(fam.Key)
                    .OrderByDescending(x => x.Date).ThenBy(x => x.Fix).ToList();
                int hr = 2;
                foreach (var x in flat)
                {
                    var fixMonth = DateTime.ParseExact(x.Fix + "-01", "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture);
                    double? baseV = prints.TryGetValue((fixMonth.Month, fixMonth.Year - 1), out var b) ? b : null;
                    double? mid, yoy;
                    if (fam.IsIndexUnit) { mid = x.Value; yoy = baseV is { } b2 ? (x.Value / b2 - 1) * 100.0 : null; }
                    else { yoy = x.Value / 100.0; mid = baseV is { } b3 ? b3 * (1 + x.Value / 10000.0) : null; }
                    hsheet.Cell(hr, 1).Value = x.Date; hsheet.Cell(hr, 1).Style.DateFormat.Format = "dd-mmm-yy";
                    hsheet.Cell(hr, 2).Value = fixMonth.ToString("MMM");
                    if (baseV is { } bb) hsheet.Cell(hr, 3).Value = bb;
                    if (mid is { } mv) hsheet.Cell(hr, 4).Value = mv;
                    if (yoy is { } yv) hsheet.Cell(hr, 5).Value = yv;
                    hr++;
                }
                log?.Invoke($"save-down: {fam.Key} history {hr - 2} rows in workbook");
            }

            wb.Save();
            log?.Invoke($"save-down: wrote {System.IO.Path.GetFileName(path)} (macro-enabled)");
            return path;
        }

        // ------------------------------------------------------------------ shared ----
        private static void ExtractTemplate(string name, string destPath)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath)!);
            var asm = typeof(StoreBooks).Assembly;
            var res = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"embedded template {name} missing");
            using var s = asm.GetManifestResourceStream(res)!;
            using var f = File.Create(destPath);
            s.CopyTo(f);
        }

        /// <summary>Write rows under a template table's header and resize the table over them,
        /// so the VBA (which addresses the ListObject) sees exactly these rows.</summary>
        private static void FillTable(IXLWorksheet ws, string tableName, List<object?[]> rows)
        {
            var tbl = ws.Table(tableName);
            int hdrRow = tbl.RangeAddress.FirstAddress.RowNumber;
            int col0 = tbl.RangeAddress.FirstAddress.ColumnNumber;
            int cols = tbl.RangeAddress.LastAddress.ColumnNumber - col0 + 1;
            // clear the template's single placeholder data row before writing
            ws.Range(hdrRow + 1, col0, hdrRow + Math.Max(1, rows.Count), col0 + cols - 1).Clear(XLClearOptions.Contents);
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < Math.Min(cols, rows[r].Length); c++)
                {
                    var cell = ws.Cell(hdrRow + 1 + r, col0 + c);
                    switch (rows[r][c])
                    {
                        case null: break;
                        case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "dd-mmm-yy"; break;
                        case double d: cell.Value = d; break;
                        case string s: cell.Value = s; break;
                    }
                }
            tbl.Resize(ws.Range(hdrRow, col0, hdrRow + Math.Max(1, rows.Count), col0 + cols - 1));
        }

        private static DateTime PrevBd(DateTime d)
        {
            var p = d.AddDays(-1);
            while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
            return p;
        }
    }
}
