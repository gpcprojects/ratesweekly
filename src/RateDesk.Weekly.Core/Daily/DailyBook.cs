using ClosedXML.Excel;
using RateDesk.Core;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Daily
{
    /// <summary>The daily OIS workbook — the app-owned successor to the incumbent sheet's
    /// "Generate file" xlsx (desk 2026-08-20): a "Runs" sheet with today's per-bank blocks in
    /// the improved format, plus one history sheet per bank with the ROLL-CORRECTED daily rate
    /// of each current meeting over the trailing window (the incumbent kept 61 days and matched
    /// by period identity, printing NA across rolls; ours reads the rung that pointed at the
    /// same contract on each day). Written to out\ with the incumbent's own file-name pattern
    /// (OIS_Runs_{d}{MMMM}{yy}.xlsx) so the Y:-drive consumers notice nothing, attached to the
    /// daily email, and optionally copied to publish.json's "dailyDir".</summary>
    public static class DailyBook
    {
        public const int HistoryDays = 60;

        public static string FileName(DateTime asOf) =>
            System.Globalization.CultureInfo.InvariantCulture is var inv
                ? $"OIS_Runs_{asOf.Day}{asOf.ToString("MMMM", inv)}{asOf.ToString("yy", inv)}.xlsx"
                : "";

        /// <summary>Build and save the workbook; returns the written path.</summary>
        public static string Write(WeeklyReport rep, HistoryStore store, string outDir, Action<string>? log = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            using var wb = new XLWorkbook();

            // ---- Runs sheet: today's blocks, improved format ----
            var ws = wb.Worksheets.Add("Runs");
            int r = 1;
            ws.Cell(r, 1).Value = $"London EOD OIS Runs — {rep.AsOf.ToString("dd-MMM-yy HH:mm", inv)} " +
                                  "(T = live mid; changes in bp vs 16:30-London snaps, roll-corrected)";
            ws.Cell(r, 1).Style.Font.SetBold();
            r += 2;

            foreach (var (runName, _, fixing) in DailyBlast.Blocks)
            {
                var run = rep.Runs.FirstOrDefault(x =>
                    x.Title.Split('·')[0].Trim().Equals(runName, StringComparison.OrdinalIgnoreCase));
                if (run == null || run.Rows.Count == 0) continue;

                ws.Cell(r, 1).Value = $"{runName} closing run";
                ws.Cell(r, 1).Style.Font.SetBold();
                r++;
                ws.Cell(r, 1).Value = $"{fixing} fixing";
                if (run.RefPct is { } rp) ws.Cell(r, 2).Value = rp;
                ws.Cell(r, 2).Style.NumberFormat.Format = "0.000";
                r++;
                string[] hdr = { "Start_date", "Maturity", "T", "Δ 1d (bp)", "Δ 1w (bp)", "Step (bp)", "Priced (bp)" };
                for (int c = 0; c < hdr.Length; c++)
                {
                    ws.Cell(r, c + 1).Value = hdr[c];
                    ws.Cell(r, c + 1).Style.Font.SetBold();
                    ws.Cell(r, c + 1).Style.Fill.SetBackgroundColor(XLColor.FromArgb(217, 217, 217));
                }
                r++;
                for (int i = 0; i < run.Rows.Count; i++)
                {
                    var m = run.Rows[i];
                    ws.Cell(r, 1).Value = m.Date; ws.Cell(r, 1).Style.DateFormat.Format = "dd-mmm-yy";
                    if (i + 1 < run.Rows.Count)
                    {
                        ws.Cell(r, 2).Value = run.Rows[i + 1].Date;
                        ws.Cell(r, 2).Style.DateFormat.Format = "dd-mmm-yy";
                    }
                    if (m.TurnPeriod)
                    {
                        ws.Cell(r, 3).Value = "Y/E Turn";
                        ws.Cell(r, 3).Style.Font.SetItalic();
                    }
                    else
                    {
                        ws.Cell(r, 3).Value = m.MidPct; ws.Cell(r, 3).Style.NumberFormat.Format = "0.000";
                        SetBp(ws.Cell(r, 4), m.D1Bp);
                        SetBp(ws.Cell(r, 5), m.W1Bp);
                        SetBp(ws.Cell(r, 6), m.StepBp);
                        SetBp(ws.Cell(r, 7), m.PricedBp);
                    }
                    r++;
                }
                r++;   // blank separator
            }
            ws.Columns(1, 2).Width = 12;
            ws.Columns(3, 7).Width = 10;

            // ---- per-bank history sheets: roll-corrected daily rate per current meeting ----
            foreach (var sched in MeetingsStore.Schedules.Where(s => string.IsNullOrEmpty(s.Kind)))
            {
                if (!DailyBlast.Blocks.Any(b => b.Run.Equals(sched.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var run = rep.Runs.FirstOrDefault(x =>
                    x.Title.Split('·')[0].Trim().Equals(sched.Name, StringComparison.OrdinalIgnoreCase));
                var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (run == null || pat == null || run.Rows.Count == 0) continue;

                var hs = wb.Worksheets.Add("Hist_" + sched.Name);
                string[] hh = { "Date", "StartDate", "Rate", "Δ 1d (bp)" };
                // one column-group per meeting would sprawl; long format instead — filterable
                for (int c = 0; c < hh.Length; c++)
                {
                    hs.Cell(1, c + 1).Value = hh[c];
                    hs.Cell(1, c + 1).Style.Font.SetBold();
                }
                var bounds = sched.DecisionDates.Concat(sched.Dates).Concat(sched.PastDates)
                    .Select(d => d.Date).OrderBy(d => d).Distinct().ToList();
                var clustered = new List<DateTime>();
                foreach (var d in bounds)
                    if (clustered.Count == 0 || (d - clustered[^1]).TotalDays > 14) clustered.Add(d);

                int hr = 2;
                var days = Enumerable.Range(0, HistoryDays)
                    .Select(i => rep.AsOf.Date.AddDays(-HistoryDays + i))
                    .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
                foreach (var day in days)
                {
                    foreach (var m in run.Rows)
                    {
                        double? v = RollingStrip.RolledValue(store,
                            n => pat.Replace("{N}", n.ToString()) + " Curncy",
                            clustered, m.Date, day, 13);
                        if (v is null) continue;
                        double? prev = RollingStrip.RolledValue(store,
                            n => pat.Replace("{N}", n.ToString()) + " Curncy",
                            clustered, m.Date, PrevBd(day), 13);
                        hs.Cell(hr, 1).Value = day; hs.Cell(hr, 1).Style.DateFormat.Format = "dd-mmm-yy";
                        hs.Cell(hr, 2).Value = m.Date; hs.Cell(hr, 2).Style.DateFormat.Format = "dd-mmm-yy";
                        hs.Cell(hr, 3).Value = v.Value; hs.Cell(hr, 3).Style.NumberFormat.Format = "0.000";
                        if (prev is { } pv) SetBp(hs.Cell(hr, 4), (v.Value - pv) * 100.0);
                        hr++;
                    }
                }
                hs.Columns(1, 2).Width = 12;
                hs.Columns(3, 4).Width = 10;
                log?.Invoke($"daily book: {sched.Name} history {hr - 2} rows");
            }

            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, FileName(rep.AsOf));
            wb.SaveAs(path);
            return path;
        }

        private static void SetBp(IXLCell cell, double? v)
        {
            if (v is not { } x) return;
            cell.Value = x;
            cell.Style.NumberFormat.Format = "+0.0;-0.0;0.0";
        }

        private static DateTime PrevBd(DateTime d)
        {
            var p = d.AddDays(-1);
            while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
            return p;
        }
    }
}
