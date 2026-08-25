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
        /// <summary>Default trailing window for the per-bank history sheets — overridable via
        /// publish.json "historyDays". The workbook is REGENERATED from the store every run
        /// (never appended), so the store stays the single source of truth and manual fallback
        /// days ingested from the incumbent sheet appear here automatically, marked by Source.</summary>
        public const int HistoryDays = 250;

        public static string FileName(DateTime asOf) =>
            System.Globalization.CultureInfo.InvariantCulture is var inv
                ? $"OIS_Runs_{asOf.Day}{asOf.ToString("MMMM", inv)}{asOf.ToString("yy", inv)}.xlsx"
                : "";

        /// <summary>Build and save the workbook; returns the written path. LEAN since the desk's
        /// 2026-08-25 integration call: the email attachment and shared-drive copies carry the
        /// Runs sheet only — history lives in the macro-enabled C+C save-down books (and the
        /// full-depth Inflation_Fixings_History export).</summary>
        public static string Write(WeeklyReport rep, string outDir, Action<string>? log = null)
        {
            using var wb = new XLWorkbook();
            WriteRunsSheet(wb.Worksheets.Add("Runs"), rep);
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, FileName(rep.AsOf));
            wb.SaveAs(path);
            return path;
        }

        /// <summary>Today's per-bank run blocks — shared by the plain workbook and the
        /// macro-enabled save-down workbook (one rendering, two containers).</summary>
        public static void WriteRunsSheet(IXLWorksheet ws, WeeklyReport rep)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int r = 1;
            ws.Cell(r, 1).Value = $"DRAX OIS Runs {rep.AsOf.ToString("dMMMyy", inv)}";
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
                string[] hdr = { "StartDate", "Maturity", "Mid", "Step (bp)", "Priced (bp)", "Δ 1d (bp)", "Δ 1w (bp)", "Δ 1m (bp)" };
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
                    var end0 = m.EndDate ?? (i + 1 < run.Rows.Count ? run.Rows[i + 1].Date : (DateTime?)null);
                    if (end0 is { } e0)
                    {
                        ws.Cell(r, 2).Value = e0;
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
                        SetBp(ws.Cell(r, 4), m.StepBp);
                        SetBp(ws.Cell(r, 5), m.PricedBp);
                        SetBp(ws.Cell(r, 6), m.D1Bp);
                        SetBp(ws.Cell(r, 7), m.W1Bp);
                        SetBp(ws.Cell(r, 8), m.M1Bp);
                    }
                    r++;
                }
                r++;   // blank separator
            }
            ws.Columns(1, 2).Width = 12;
            ws.Columns(3, 8).Width = 10;
        }

        /// <summary>The per-bank Hist_ sheets (roll-corrected daily rate per current meeting).</summary>
        public static void WriteHistorySheets(XLWorkbook wb, WeeklyReport rep, HistoryStore store,
            int historyDays, Action<string>? log)
        {
            foreach (var sched in MeetingsStore.Schedules.Where(s => string.IsNullOrEmpty(s.Kind)))
            {
                if (!DailyBlast.Blocks.Any(b => b.Run.Equals(sched.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var run = rep.Runs.FirstOrDefault(x =>
                    x.Title.Split('·')[0].Trim().Equals(sched.Name, StringComparison.OrdinalIgnoreCase));
                var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (run == null || pat == null || run.Rows.Count == 0) continue;

                var hs = wb.Worksheets.Add("Hist_" + sched.Name);
                string[] hh = { "Date", "StartDate", "EndDate", "Rate", "Δ 1d (bp)", "Δ 1w (bp)", "Δ 1m (bp)", "Source" };
                // one column-group per meeting would sprawl; long format instead — filterable
                for (int c = 0; c < hh.Length; c++)
                {
                    hs.Cell(1, c + 1).Value = hh[c];
                    hs.Cell(1, c + 1).Style.Font.SetBold();
                }
                int hr = 2;
                foreach (var row in BankHistoryRows(store, sched, run, pat, rep.AsOf, historyDays))
                {
                    hs.Cell(hr, 1).Value = row.Day; hs.Cell(hr, 1).Style.DateFormat.Format = "dd-mmm-yy";
                    hs.Cell(hr, 2).Value = row.Start; hs.Cell(hr, 2).Style.DateFormat.Format = "dd-mmm-yy";
                    if (row.End is { } he)
                    {
                        hs.Cell(hr, 3).Value = he;
                        hs.Cell(hr, 3).Style.DateFormat.Format = "dd-mmm-yy";
                    }
                    hs.Cell(hr, 4).Value = row.Rate; hs.Cell(hr, 4).Style.NumberFormat.Format = "0.000";
                    if (row.D1 is { } d1) SetBp(hs.Cell(hr, 5), d1);
                    if (row.W1 is { } w1) SetBp(hs.Cell(hr, 6), w1);
                    if (row.M1 is { } m1) SetBp(hs.Cell(hr, 7), m1);
                    hs.Cell(hr, 8).Value = row.Source;
                    if (row.Source != "bbg") hs.Cell(hr, 8).Style.Font.SetBold();
                    hr++;
                }
                hs.Columns(1, 3).Width = 12;
                hs.Columns(4, 7).Width = 10;
                hs.Column(8).Width = 8;
                log?.Invoke($"daily book: {sched.Name} history {hr - 2} rows ({historyDays}d window)");
            }
        }

        public sealed record HistRow(DateTime Day, DateTime Start, DateTime? End, double Rate,
            string Source, double? D1, double? W1, double? M1);

        /// <summary>The roll-corrected per-meeting daily history for one bank — the machinery
        /// behind the Hist_ sheets, exposed so the macro-enabled save-down workbook fills its
        /// history_ tables from the SAME walk (one source of truth for what a rate meant when).</summary>
        public static List<HistRow> BankHistoryRows(HistoryStore store, MeetingScheduleDef sched,
            WeeklyRun run, string pat, DateTime asOf, int historyDays)
        {
            var bounds = sched.DecisionDates.Concat(sched.Dates).Concat(sched.PastDates)
                .Select(d => d.Date).OrderBy(d => d).Distinct().ToList();
            var clustered = new List<DateTime>();
            foreach (var d in bounds)
                if (clustered.Count == 0 || (d - clustered[^1]).TotalDays > 14) clustered.Add(d);

            // one store read per rung, then everything from memory — full-depth sheets
            // would take minutes with a store round-trip per (day x meeting x horizon)
            var rungData = new Dictionary<int, List<(DateTime Date, double Value, string Source)>>();
            List<(DateTime Date, double Value, string Source)> RungHist(int n)
            {
                if (!rungData.TryGetValue(n, out var l))
                    rungData[n] = l = store.GetDailyWithSource(
                        pat.Replace("{N}", n.ToString()) + " Curncy", historyDays + 60);
                return l;
            }
            var boundSet = clustered.ToHashSet();
            (double Value, string Source)? ValueAt(DateTime contract, DateTime then, int depth = 0)
            {
                if (depth > 6) return null;
                if (boundSet.Contains(then.Date)) then = then.Date.AddDays(-1);
                int idx = Math.Max(1, clustered.Count(x => x > then.Date && x <= contract.Date));
                if (idx > 13) return null;
                var l = RungHist(idx);
                for (int i = l.Count - 1; i >= 0; i--)
                    if (l[i].Date.Date <= then.Date)
                    {
                        // a walk-back RESOLVING to a boundary close recomputes from the day
                        // before it (mixed-state decision-day closes — the stitcher's rule)
                        if (boundSet.Contains(l[i].Date.Date))
                            return ValueAt(contract, l[i].Date.Date.AddDays(-1), depth + 1);
                        return (l[i].Value, l[i].Source);
                    }
                return null;
            }

            var outRows = new List<HistRow>();
            var days = Enumerable.Range(0, historyDays)
                .Select(i => asOf.Date.AddDays(-historyDays + i))
                .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
            foreach (var day in days)
                for (int ri = 0; ri < run.Rows.Count; ri++)
                {
                    var m = run.Rows[ri];
                    if (ValueAt(m.Date, day) is not { } cur) continue;
                    var prev = ValueAt(m.Date, PrevBd(day));
                    var week = ValueAt(m.Date, day.AddDays(-7));
                    var month = ValueAt(m.Date, WeeklyCurves.MonthAgo(day));
                    var hEnd = m.EndDate ?? (ri + 1 < run.Rows.Count ? run.Rows[ri + 1].Date : (DateTime?)null);
                    outRows.Add(new HistRow(day, m.Date, hEnd, cur.Value, cur.Source,
                        prev is { } pv ? (cur.Value - pv.Value) * 100.0 : null,
                        week is { } wv ? (cur.Value - wv.Value) * 100.0 : null,
                        month is { } mv ? (cur.Value - mv.Value) * 100.0 : null));
                }
            return outRows;
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
