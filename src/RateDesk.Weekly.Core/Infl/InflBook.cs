using ClosedXML.Excel;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Infl
{
    /// <summary>The unified inflation-fixings history workbook — the store's fixings table
    /// rendered per family, full depth, long format so it filters like the OIS Hist_ sheets:
    /// Date | Fixing | Value | Δ1d | Δ1w | Δ1m | Source. Values are the market's native quote
    /// (CPI = forecast index level; RPI/HICP = YoY bp); changes are computed against the SAME
    /// fixing identity, so a ticker's annual re-point can never masquerade as a move. Source
    /// column: 'xls' = validated external-pricer mark, 'bbg' = Bloomberg close (bold when not
    /// bbg, same convention as the OIS history sheets). REGENERATED on every export — the
    /// store stays the single writer.</summary>
    public static class InflBook
    {
        public const string FileName = "Inflation_Fixings_History.xlsx";

        public static string Write(HistoryStore store, string outDir, Action<string>? log = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            using var wb = new XLWorkbook();
            foreach (var fam in InflHistory.Families)
            {
                var hist = store.GetFixingHistory(fam.Key);
                var hs = wb.Worksheets.Add("Hist_" + fam.Key);
                string unit = fam.IsIndexUnit ? "index" : "y/y bp";
                string[] hh = { "Date", "Fixing", $"Value ({unit})", "Δ 1d", "Δ 1w", "Δ 1m", "Source" };
                for (int c = 0; c < hh.Length; c++)
                {
                    hs.Cell(1, c + 1).Value = hh[c];
                    hs.Cell(1, c + 1).Style.Font.SetBold();
                }
                if (hist.Count == 0) { log?.Invoke($"infl book: {fam.Key} empty"); continue; }

                // one pass per fixing for the lookbacks — all from memory
                var byFix = hist.GroupBy(x => x.Fix)
                    .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Date).ToList());
                double? At(string fix, DateTime then)
                {
                    var l = byFix[fix];
                    for (int i = l.Count - 1; i >= 0; i--)
                        if (l[i].Date <= then) return l[i].Value;
                    return null;
                }

                string dFmt = fam.IsIndexUnit ? "+0.00;-0.00;0.00" : "+0.0;-0.0;0.0";
                int r = 2;
                foreach (var row in hist.OrderBy(x => x.Date).ThenBy(x => x.Fix))
                {
                    hs.Cell(r, 1).Value = row.Date;
                    hs.Cell(r, 1).Style.DateFormat.Format = "dd-mmm-yy";
                    hs.Cell(r, 2).Value = DateTime.ParseExact(row.Fix + "-01", "yyyy-MM-dd", inv);
                    hs.Cell(r, 2).Style.DateFormat.Format = "mmm-yy";
                    hs.Cell(r, 3).Value = row.Value;
                    hs.Cell(r, 3).Style.NumberFormat.Format = "0.000";
                    var d1 = At(row.Fix, PrevBd(row.Date));
                    var w1 = At(row.Fix, row.Date.AddDays(-7));
                    var m1 = At(row.Fix, WeeklyCurves.MonthAgo(row.Date));
                    if (d1 is { } a) Set(hs.Cell(r, 4), row.Value - a, dFmt);
                    if (w1 is { } b) Set(hs.Cell(r, 5), row.Value - b, dFmt);
                    if (m1 is { } c2) Set(hs.Cell(r, 6), row.Value - c2, dFmt);
                    hs.Cell(r, 7).Value = row.Source;
                    if (row.Source != "bbg") hs.Cell(r, 7).Style.Font.SetBold();
                    r++;
                }
                hs.Columns(1, 2).Width = 11;
                hs.Columns(3, 6).Width = 10;
                hs.Column(7).Width = 8;
                log?.Invoke($"infl book: {fam.Key} {r - 2} rows " +
                            $"({byFix.Count} fixings, {hist.Min(x => x.Date):dd-MMM-yy}..{hist.Max(x => x.Date):dd-MMM-yy})");
            }
            Directory.CreateDirectory(outDir);
            var path = System.IO.Path.Combine(outDir, FileName);
            wb.SaveAs(path);
            return path;
        }

        private static void Set(IXLCell cell, double v, string fmt)
        {
            cell.Value = v;
            cell.Style.NumberFormat.Format = fmt;
        }

        private static DateTime PrevBd(DateTime d)
        {
            var p = d.AddDays(-1);
            while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
            return p;
        }
    }
}
