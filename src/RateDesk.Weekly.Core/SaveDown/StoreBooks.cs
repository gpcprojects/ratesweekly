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

        // the HISTORY books keep the incumbent underscore pattern — IngestNewestSaved parses
        // the date from the {d}{MMMM}{yy} tail, and these names never reach the email
        public static string OisFileName(DateTime asOf) =>
            $"OIS_Runs_{asOf.ToString("dMMMMyy", System.Globalization.CultureInfo.InvariantCulture)}.xlsm";
        public static string InflFileName(DateTime asOf) =>
            $"Inflation_Runs_{asOf.ToString("dMMMMyy", System.Globalization.CultureInfo.InvariantCulture)}.xlsm";

        // ------------------------------------------------------------------ OIS ----
        /// <summary>App name → the incumbent workbook's table tag and Historical_ sheet.</summary>
        private static readonly (string Run, string Tag, string Sheet)[] OisTags =
        {
            ("RBA", "au", "Historical_AU"), ("RBNZ", "nz", "Historical_NZ"),
            ("ECB", "eu", "Historical_EU"), ("MPC", "uk", "Historical_UK"),
            ("FOMC", "us", "Historical_US"), ("BOC", "cd", "Historical_CD"),
            ("NORGES", "nok", "Historical_NOK"), ("BOJ", "jpy", "Historical_JPY"),
            ("RIKSBANK", "sek", "Historical_SEK"),
        };

        public static string WriteOis(WeeklyReport rep, HistoryStore store, string outDir,
            Action<string>? log = null, int historyDays = 61)
        {
            var path = System.IO.Path.Combine(outDir, OisFileName(rep.AsOf));
            ExtractTemplate(OisTemplate, path);
            using var wb = new XLWorkbook(path);

            var runsWs = wb.Worksheet("Runs");
            runsWs.Clear();
            DailyBook.WriteRunsSheet(runsWs, rep);

            // The Current entry pages are the INCUMBENT'S OWN, formulas and all (BDP/BDH
            // auto-fill with a terminal, manually overridable without one) — the app never
            // writes them. It fills the Historical_ tables, ALL eleven columns: StepDelta and
            // Priced In(Total) against the day's own ref-rate fixing, Percent = priced/25.
            var configs = RateDesk.Core.Config.ConfigStore.LoadDefault();
            foreach (var (runName, tag, sheetName) in OisTags)
            {
                var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                    string.IsNullOrEmpty(s.Kind) && s.Name.Equals(runName, StringComparison.OrdinalIgnoreCase));
                var run = DailyBlast.Find(rep, runName);
                var pat = sched?.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (sched == null || run == null || pat == null || run.Rows.Count == 0) continue;

                // same ref resolution as the boards: the schedule's own refTicker, else the
                // currency's OIS overnight fixing (ECB→ESTR etc.)
                var refTicker = sched.RefTicker
                    ?? configs.Enabled.FirstOrDefault(c =>
                        c.Ccy.Equals(sched.Ccy, StringComparison.OrdinalIgnoreCase))?.Ois?.OnFixingTicker;
                var refHist = string.IsNullOrEmpty(refTicker)
                    ? new List<RateDesk.Core.Market.HistPoint>()
                    : store.GetDaily(refTicker!, historyDays + 40).ToList();
                double? RefAt(DateTime day)
                {
                    for (int i = refHist.Count - 1; i >= 0; i--)
                        if (refHist[i].Date.Date <= day.Date) return refHist[i].Value;
                    return null;
                }

                var histRows = new List<object?[]>();
                DateTime? curDay = null;
                double? prevRate = null, refVal = null;
                foreach (var h in DailyBook.BankHistoryRows(store, sched, run, pat, rep.AsOf, historyDays))
                {
                    if (curDay != h.Day) { curDay = h.Day; prevRate = null; refVal = RefAt(h.Day); }
                    double? step = prevRate is { } pr ? (h.Rate - pr) * 100.0
                        : refVal is { } rv0 ? (h.Rate - rv0) * 100.0 : null;
                    double? priced = refVal is { } rv ? (h.Rate - rv) * 100.0 : null;
                    histRows.Add(new object?[]
                    {
                        // ENGLISH month label always — the app's own re-ingest and the incumbent
                        // VBA both match English names (audit 2026-08-26: a non-English machine
                        // wrote "Mai" and silently dropped every row on re-ingest)
                        h.Day, h.Start.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture),
                        h.Start, h.End, h.Rate,
                        step, priced, priced / 25.0, h.D1, h.W1, h.M1,
                    });
                    prevRate = h.Rate;
                }
                FillTable(wb.Worksheet(sheetName), "history_" + tag, histRows);
            }

            wb.Save();
            log?.Invoke($"save-down: wrote {System.IO.Path.GetFileName(path)} " +
                        "(incumbent entry pages + app history, macro-enabled)");
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
            var runsWs = wb.Worksheet("Runs");
            runsWs.Clear();
            InflRunsXlsx.WriteRunsSheet(runsWs, store, marks, InflHistory.LastNextPrints, asOf);

            // The Copy and Table entry pages are the INCUMBENT'S OWN — formula-fed from the
            // USSWIF/BPSWIF/EUSWIF tickers, auto-filling with a terminal, manually overridable
            // without one. The app only rewrites the History pages: the FULL unified record,
            // newest observation first, every incumbent column populated including %mom.
            foreach (var fam in InflHistory.Families)
            {
                var prints = InflHistory.PrintsOf(store, fam);
                var hsheet = wb.Worksheet(fam.Key + "_History");
                var oldLast = hsheet.LastRowUsed()?.RowNumber() ?? 1;
                var oldWide = hsheet.LastColumnUsed()?.ColumnNumber() ?? 6;
                if (oldLast > 1)   // full used width — template leftovers beyond col 6 must not survive
                    hsheet.Range(2, 1, oldLast, Math.Max(6, oldWide)).Clear(XLClearOptions.Contents);

                // group by observation date so %mom chains mid-over-previous-mid within each
                // day's block, the front row anchored on the last published print — exactly
                // what the incumbent's Copy formulas compute
                var byDay = store.GetFixingHistory(fam.Key)
                    .GroupBy(x => x.Date)
                    .OrderByDescending(g => g.Key);
                int hr = 2;
                foreach (var day in byDay)
                {
                    double? prevMid = null;
                    foreach (var x in day.OrderBy(v => v.Fix))
                    {
                        var fixMonth = DateTime.ParseExact(x.Fix + "-01", "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture);
                        // ONE derivation, shared with BuildDisplayRows (audit 2026-08-26)
                        var (baseV, mid, yoy, mom) = InflHistory.DeriveRow(fam, prints, fixMonth, x.Value, prevMid);
                        hsheet.Cell(hr, 1).Value = x.Date;
                        hsheet.Cell(hr, 1).Style.DateFormat.Format = "dd-mmm-yy";
                        // English month label — re-ingest matches English names (audit 2026-08-26)
                        hsheet.Cell(hr, 2).Value = fixMonth.ToString("MMM",
                            System.Globalization.CultureInfo.InvariantCulture);
                        SetNum(hsheet.Cell(hr, 3), baseV, "0.000");
                        SetNum(hsheet.Cell(hr, 4), mid, "0.000");
                        SetNum(hsheet.Cell(hr, 5), yoy, "0.000");
                        SetNum(hsheet.Cell(hr, 6), mom, "0.000");
                        prevMid = mid;
                        hr++;
                    }
                }
                log?.Invoke($"save-down: {fam.Key} history {hr - 2} rows in workbook");
            }

            wb.Save();
            log?.Invoke($"save-down: wrote {System.IO.Path.GetFileName(path)} " +
                        "(incumbent entry pages + app history, macro-enabled)");
            return path;
        }

        private static void SetNum(IXLCell cell, double? v, string fmt)
        {
            if (v is not { } x) return;
            cell.Value = x;
            cell.Style.NumberFormat.Format = fmt;
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
            // clear EVERYTHING the table previously held (the incumbent-derived templates ship
            // with hundreds of rows of their own history) — the app's fill is the whole truth
            int oldLast = tbl.RangeAddress.LastAddress.RowNumber;
            ws.Range(hdrRow + 1, col0, Math.Max(oldLast, hdrRow + Math.Max(1, rows.Count)),
                col0 + cols - 1).Clear(XLClearOptions.Contents);
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

    }
}
