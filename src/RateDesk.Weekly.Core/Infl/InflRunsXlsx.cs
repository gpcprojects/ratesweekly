using ClosedXML.Excel;

namespace RateDesk.Weekly.Core.Infl
{
    /// <summary>The LEAN inflation runs workbook — the daily email's attachment (desk
    /// 2026-08-25: attachments carry no history; history lives in the macro-enabled C+C
    /// save-down books). One "Runs" sheet in the incumbent CreateInflationRun shape: a block
    /// per family (US CPI / UK RPI / EU HICP Ex-Tobacco), Month | Base | Mid | YoY % | MoM %
    /// | Index Change Daily/Weekly/Monthly, the furthest fixing dropped. The same sheet writer
    /// fills the save-down book's Runs page — one rendering, two containers.</summary>
    public static class InflRunsXlsx
    {
        /// <summary>Attachment name per desk 2026-08-25: "DRAX Fixing Runs 25Aug26.xlsx".</summary>
        public static string FileName(DateTime asOf) =>
            $"DRAX Fixing Runs {asOf.ToString("dMMMyy", System.Globalization.CultureInfo.InvariantCulture)}.xlsx";

        /// <summary>The sheet's own title row — shared with the email facsimile.</summary>
        public static string Title(DateTime asOf) =>
            $"DRAX Fixing Runs {asOf.ToString("dMMMyy", System.Globalization.CultureInfo.InvariantCulture)}";

        public static string Write(HistoryStore store, string outDir, DateTime asOf,
            Dictionary<string, List<InflHistory.Mark>>? marks,
            Dictionary<string, DateTime>? nextPrints, Action<string>? log = null)
        {
            marks ??= InflHistory.LatestMarks(store);
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Runs");
            WriteRunsSheet(ws, store, marks, nextPrints, asOf);
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, FileName(asOf));
            wb.SaveAs(path);
            log?.Invoke($"daily: wrote {Path.GetFileName(path)} (runs only, no history)");
            return path;
        }

        public static void WriteRunsSheet(IXLWorksheet ws, HistoryStore store,
            Dictionary<string, List<InflHistory.Mark>> marks,
            Dictionary<string, DateTime>? nextPrints, DateTime asOf)
        {
            int r = 1;
            ws.Cell(r, 1).Value = Title(asOf);
            ws.Cell(r, 1).Style.Font.SetBold();
            r += 2;

            foreach (var fam in InflHistory.Families)
            {
                var famMarks = marks.TryGetValue(fam.Key, out var m) ? m : new List<InflHistory.Mark>();
                var rows = InflHistory.BuildDisplayRows(store, fam, famMarks, asOf);
                var shown = rows.Take(Math.Max(0, rows.Count - 1)).ToList();   // drop the furthest
                if (shown.Count == 0) continue;

                ws.Cell(r, 1).Value = fam.Key switch
                {
                    "CPI" => "US CPI Fixing Run", "RPI" => "UK RPI Fixing Run",
                    _ => "EU HICP Ex-Tobacco Fixing Run",
                };
                ws.Cell(r, 1).Style.Font.SetBold();
                if (nextPrints != null && nextPrints.TryGetValue(fam.Key, out var np))
                {
                    ws.Cell(r, 4).Value = "Next Print:";
                    ws.Cell(r, 5).Value = np;
                    ws.Cell(r, 5).Style.DateFormat.Format = "dd-mmm-yy";
                }
                r++;
                ws.Cell(r, 1).Value = fam.IndexTicker.Replace(" Index", "");
                ws.Cell(r, 6).Value = "Index Change";
                ws.Cell(r, 6).Style.Font.SetBold();
                r++;
                int hdrRow = r;
                string[] hdr = { "Month", "Base Index", "Mid Index", "YoY %", "MoM %", "Daily", "Weekly", "Monthly" };
                for (int c = 0; c < hdr.Length; c++)
                {
                    ws.Cell(r, c + 1).Value = hdr[c];
                    ws.Cell(r, c + 1).Style.Font.SetBold();
                    // DRAX blue band (desk 2026-08-26, was grey)
                    ws.Cell(r, c + 1).Style.Fill.SetBackgroundColor(
                        XLColor.FromHtml(RateDesk.Weekly.Core.Daily.RunsTable.BrandBlue));
                }
                r++;
                foreach (var row in shown)
                {
                    ws.Cell(r, 1).Value = row.RefMonth;
                    ws.Cell(r, 1).Style.DateFormat.Format = "mmm-yy";
                    Set(ws.Cell(r, 2), row.BaseV, "0.00");
                    Set(ws.Cell(r, 3), row.Mid, "0.00");
                    Set(ws.Cell(r, 4), row.Yoy, "0.00");
                    Set(ws.Cell(r, 5), row.Mom, "0.00");
                    Set(ws.Cell(r, 6), row.D1, "+0.00;-0.00;0.00");
                    Set(ws.Cell(r, 7), row.W1, "+0.00;-0.00;0.00");
                    Set(ws.Cell(r, 8), row.M1, "+0.00;-0.00;0.00");
                    r++;
                }
                // GRID LINES on the attachment (desk 2026-08-26) — the email carries none
                var grid = ws.Range(hdrRow, 1, r - 1, hdr.Length);
                grid.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                grid.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                var gc = XLColor.FromHtml(RateDesk.Weekly.Core.Daily.RunsTable.GridLine);
                grid.Style.Border.OutsideBorderColor = gc;
                grid.Style.Border.InsideBorderColor = gc;
                r++;   // blank separator between families
            }
            ws.Columns(1, 8).Width = 11;
        }

        private static void Set(IXLCell cell, double? v, string fmt)
        {
            if (v is not { } x) return;
            cell.Value = x;
            cell.Style.NumberFormat.Format = fmt;
        }
    }
}
