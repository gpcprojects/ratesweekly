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

            // THE VANDIT AND BLAST PAGES, rebuilt every run in the ATTACHMENT'S layout
            foreach (var page in new[] { "Vandit", "Blast" })
                if (wb.Worksheets.TryGetWorksheet(page, out var pws))
                    WriteLivePage(pws, rep, log);

            wb.Save();
            log?.Invoke($"save-down: wrote {System.IO.Path.GetFileName(path)} " +
                        "(incumbent entry pages + app history, macro-enabled)");
            return path;
        }

        /// <summary>THE VANDIT AND BLAST PAGES (rebuilt 2026-08-28, desk: "rewrite the blast and
        /// vandit pages to be EXACTLY LIKE the spreadsheet that gets attached to the email").
        ///
        /// WHAT WAS WRONG. Both pages read the incumbent's Current sheet, and the columns they
        /// read for the change-on-day - Current!L:N and AB:AD - are not formulas. They are
        /// LITERALS the incumbent's macro stamps, and the template carries whatever they held the
        /// day it was captured (19-Aug-26, per Current!D23). So every saved book paired TODAY's
        /// live Bloomberg prices with 19-Aug's change columns, and the pages showed it three
        /// different ways: #VALUE! where the literal was the text "NA", a silent 0.000 where the
        /// formula wrapped it in IFERROR(...,0) (RBA, RBNZ - the worst of the three, because a
        /// stale zero reads as "the market did not move"), and quietly stale where it happened to
        /// still be a number (NOWA, BOJ). T-1 was then derived FROM that stale change, so it was
        /// wrong wherever the change was.
        ///
        /// WHAT THIS DOES INSTEAD. Writes both pages fresh each run, in RunsTable's own column
        /// order, entirely from formulas - nothing on these pages is a stored number, so the desk
        /// can open the file with the app down, hit refresh, and get that day's run:
        ///   · StartDate / Maturity / Mid come straight off the ticker (SW_EFF_DT, MATURITY,
        ///     LAST_PRICE), so a row always labels the contract it is actually showing;
        ///   · Priced and Step derive from Mid and the run's own fixing cell;
        ///   · the three CHANGE columns look up the Historical_ sheet on the contract's own
        ///     START DATE, never on the ticker number. That is what makes them roll-proof:
        ///     EESF1A is a different contract after a meeting passes, but the 16-Sep-26 contract
        ///     is the 16-Sep-26 contract on every date it was quoted. A PX_YEST_CLOSE on a fixed
        ///     rung would have been one line shorter and wrong on exactly the days that matter.
        ///
        /// The anchor dates are found with MAXIFS ("the latest observation on or before the
        /// target"), in helper columns J:L so the formulas stay readable - the same 1d / -7d /
        /// EDATE(-1) convention the boards use. A contract with no history on the anchor date
        /// publishes BLANK, never a zero.</summary>
        private static void WriteLivePage(IXLWorksheet ws, WeeklyReport rep, Action<string>? log)
        {
            var configs = RateDesk.Core.Config.ConfigStore.LoadDefault();
            ws.Clear();
            int r = 1;
            ws.Cell(r, 1).Value = RunsTable.Title(rep.AsOf);
            ws.Cell(r, 1).Style.Font.SetBold();
            r += 2;

            foreach (var b in RunsTable.Build(rep))
            {
                var tag = OisTags.FirstOrDefault(t =>
                    t.Run.Equals(b.Bank, StringComparison.OrdinalIgnoreCase));
                var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                    string.IsNullOrEmpty(s.Kind) && s.Name.Equals(b.Bank, StringComparison.OrdinalIgnoreCase));
                var pat = sched?.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (tag.Sheet == null || sched == null || pat == null) continue;
                var src = string.IsNullOrEmpty(sched.Source) ? "" : " " + sched.Source;
                string Tick(int n) => $"{pat.Replace("{N}", n.ToString())}{src} Curncy";
                string H = tag.Sheet;

                var refTicker = sched.RefTicker
                    ?? configs.Enabled.FirstOrDefault(c =>
                        c.Ccy.Equals(sched.Ccy, StringComparison.OrdinalIgnoreCase))?.Ois?.OnFixingTicker;

                ws.Cell(r, 1).Value = $"{b.Bank} closing run";
                ws.Cell(r, 1).Style.Font.SetBold();
                r++;
                int fixRow = r;
                ws.Cell(r, 1).Value = $"{b.FixingLabel} fixing";
                if (!string.IsNullOrEmpty(refTicker))
                    ws.Cell(r, 2).FormulaA1 = $"_xll.BDP(\"{refTicker}\",\"LAST_PRICE\")";
                ws.Cell(r, 2).Style.NumberFormat.Format = RunsTable.RateFmt;
                r++;

                int hdrRow = r;
                for (int c = 0; c < RunsTable.Headers.Length; c++)
                {
                    ws.Cell(r, c + 1).Value = RunsTable.Headers[c];
                    ws.Cell(r, c + 1).Style.Font.SetBold();
                    ws.Cell(r, c + 1).Style.Fill.SetBackgroundColor(XLColor.FromHtml(RunsTable.BrandBlue));
                }
                r++;

                int firstRow = r;
                for (int i = 0; i < b.Rows.Count; i++)
                {
                    string t = Tick(i + 1);        // row i is the i-th rung, as Current does
                    // LIVE FIRST, THE RUN'S OWN DATE AS THE FALLBACK. The deep rungs of the
                    // thinner families publish no date fields at all - SKSF4A upwards carry a
                    // price and nothing else - so a bare BDP would leave the last Riksbank rows
                    // blank, and blank in column A also kills the change lookups, which key on
                    // it. A date is safe to carry as a literal in a way a PRICE never is: it is
                    // the contract's identity, not its mark. Bloomberg still wins whenever it
                    // has one.
                    string D(DateTime d) => $"DATE({d.Year},{d.Month},{d.Day})";
                    ws.Cell(r, 1).FormulaA1 =
                        $"IFERROR(_xll.BDP(\"{t}\",\"SW_EFF_DT\"),{D(b.Rows[i].Start)})";
                    ws.Cell(r, 1).Style.DateFormat.Format = "dd-mmm-yy";
                    ws.Cell(r, 2).FormulaA1 = b.Rows[i].End is { } endD
                        ? $"IFERROR(_xll.BDP(\"{t}\",\"MATURITY\"),{D(endD)})"
                        : $"IFERROR(_xll.BDP(\"{t}\",\"MATURITY\"),\"\")";
                    ws.Cell(r, 2).Style.DateFormat.Format = "dd-mmm-yy";

                    // a masked row is a LABEL on every surface (Y/E Turn, n/a) - the attachment
                    // prints the label in the Mid cell, so this page does too
                    if (b.Rows[i].Masked)
                    {
                        ws.Cell(r, 3).Value = b.Rows[i].MaskLabel;
                        ws.Cell(r, 3).Style.Font.SetItalic();
                        r++;
                        continue;
                    }

                    ws.Cell(r, 3).FormulaA1 = $"_xll.BDP(\"{t}\",\"LAST_PRICE\")";
                    ws.Cell(r, 3).Style.NumberFormat.Format = RunsTable.RateFmt;
                    ws.Cell(r, 4).FormulaA1 = $"IF(N(C{r})=0,\"\",(C{r}-$B${fixRow})*100)";
                    if (r > firstRow)
                        ws.Cell(r, 5).FormulaA1 =
                            $"IF(OR(N(C{r})=0,N(C{r - 1})=0),\"\",(C{r}-C{r - 1})*100)";

                    // THE CHANGE COLUMNS: today's live mid minus the anchor the run resolved,
                    // written in as a plain number. One subtraction per cell.
                    //
                    // TWO EARLIER ATTEMPTS ARE WHY THIS IS SO PLAIN (2026-08-28). The first put
                    // MAXIFS in helper columns: MAXIFS post-dates the file format, so a workbook
                    // must spell it `_xlfn.MAXIFS` and ClosedXML writes the bare name - every cell
                    // read #NAME?. The second used LOOKUP(2,1/(...)) against the Historical_ sheet,
                    // keyed on column A's start date - which is a BDP(...,"SW_EFF_DT") that comes
                    // back as TEXT, so it never equalled the numeric date serials in the history
                    // and every cell came back blank. Both failures were the formula depending on
                    // something it did not need to depend on.
                    //
                    // The anchor is a settled close. The app has already resolved it, roll-corrected
                    // and snap-timed, to produce this row's own Δ - so anchor = Mid - Δ/100, and the
                    // cell only has to subtract. It still tracks the mid as the market moves, which
                    // is the whole point of a live page; it just no longer re-derives a number the
                    // run already knew. A row whose Δ the run withheld gets no formula at all, so
                    // the sheet stays blank exactly where the app is blank.
                    var chg = new[] { b.Rows[i].D1Bp, b.Rows[i].W1Bp, b.Rows[i].M1Bp };
                    for (int k = 0; k < 3; k++)
                    {
                        ws.Cell(r, 6 + k).Style.NumberFormat.Format = RunsTable.BpFmt;
                        if (chg[k] is not { } dbp) continue;
                        var anchor = b.Rows[i].Mid - dbp / 100.0;
                        ws.Cell(r, 6 + k).FormulaA1 =
                            $"IFERROR((C{r}-{anchor.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)})*100,\"\")";
                    }
                    ws.Cell(r, 4).Style.NumberFormat.Format = RunsTable.BpFmt;
                    ws.Cell(r, 5).Style.NumberFormat.Format = RunsTable.BpFmt;
                    r++;
                }
                if (r - 1 > hdrRow)
                    ws.Range(hdrRow + 1, 1, r - 1, RunsTable.Headers.Length)
                        .Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin)
                        .Border.SetInsideBorder(XLBorderStyleValues.Thin);
                r++;
            }

            ws.Columns(1, RunsTable.Headers.Length).AdjustToContents();
            log?.Invoke($"save-down: rebuilt the {ws.Name} page (live BDP formulas, " +
                        "changes looked up on start date)");
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
