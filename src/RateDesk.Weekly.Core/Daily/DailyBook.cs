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
        /// <summary>Attachment name per desk 2026-08-25: "DRAX OIS Runs 25Aug26.xlsx".</summary>
        public static string FileName(DateTime asOf) =>
            $"DRAX OIS Runs {asOf.ToString("dMMMyy", System.Globalization.CultureInfo.InvariantCulture)}.xlsx";

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
                var run = DailyBlast.Find(rep, runName);
                if (run == null || run.Rows.Count == 0) continue;

                ws.Cell(r, 1).Value = $"{runName} closing run";
                ws.Cell(r, 1).Style.Font.SetBold();
                r++;
                ws.Cell(r, 1).Value = $"{fixing} fixing";
                if (run.RefPct is { } rp) ws.Cell(r, 2).Value = rp;
                ws.Cell(r, 2).Style.NumberFormat.Format = "0.000";
                // compounded fixing (trial, desk 2026-08-26) — same row, window shown
                if (run.CompoundedPct is { } cmp)
                {
                    ws.Cell(r, 3).Value = "compounded";
                    ws.Cell(r, 4).Value = cmp;
                    ws.Cell(r, 4).Style.NumberFormat.Format = "0.000";
                    if (run.CompoundedFrom is { } cf)
                        ws.Cell(r, 5).Value = "since " + cf.ToString("dd-MMM-yy", inv);
                }
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

        // (the old per-bank Hist_ sheet writer was deleted 2026-08-26 — dead since the books
        // went lean; history lives in the macro-enabled save-down books via BankHistoryRows)

        public sealed record HistRow(DateTime Day, DateTime Start, DateTime? End, double Rate,
            string Source, double? D1, double? W1, double? M1);

        /// <summary>The roll-corrected per-meeting daily history for one bank — the machinery
        /// behind the Hist_ sheets, exposed so the macro-enabled save-down workbook fills its
        /// history_ tables from the SAME walk (one source of truth for what a rate meant when).
        /// Reads the run's CONTRIBUTOR series first (sched.Source — the desk sheet's own
        /// prices), composite closes as the fallback per rung.</summary>
        public static List<HistRow> BankHistoryRows(HistoryStore store, MeetingScheduleDef sched,
            WeeklyRun run, string pat, DateTime asOf, int historyDays)
        {
            // the run's ACTIVE contributor (source-selection trial 2026-08-26), so the history
            // pages read the same source the mids were built on; config default otherwise
            var activeSrc = run.Source ?? sched.Source ?? "";
            string srcSuffix = activeSrc.Length == 0 ? "" : " " + activeSrc;
            // start-renumbering families (SKSF) keep boundaries ON the start dates — including
            // the decisions would cluster each boundary back onto the decision and mis-rung the
            // decision→start week (the same rule as the stitcher, desk 2026-08-25). The RUN'S
            // OWN dates (ticker-derived truth) are unioned in like the stitcher does (audit
            // 2026-08-26: a config grid drifting late — BOJ 2027 sat 8-11 days off — otherwise
            // drops the true boundary and mis-rungs every row of that period).
            var boundSrc = (sched.RollsAtPeriodStart
                    ? sched.Dates.Concat(sched.PastDates)
                    : sched.DecisionDates.Concat(sched.Dates).Concat(sched.PastDates))
                .Concat(run.Rows.Select(r => r.Date))
                .Concat(run.Rows.Where(r => r.EndDate is { }).Select(r => r.EndDate!.Value));
            var bounds = boundSrc.Select(d => d.Date).OrderBy(d => d).Distinct().ToList();
            var clustered = new List<DateTime>();
            foreach (var d in bounds)
                if (clustered.Count == 0 || (d - clustered[^1]).TotalDays > 14) clustered.Add(d);

            // one store read per rung, then everything from memory — full-depth sheets
            // would take minutes with a store round-trip per (day x meeting x horizon).
            // Lookback anchored on the REPORT's asOf, not the wall clock (audit 2026-08-26:
            // an offline export of an old report silently lost its early rows).
            int lookback = historyDays + 60 + Math.Max(0, (int)(DateTime.Today - asOf.Date).TotalDays);
            var rungData = new Dictionary<int, List<(DateTime Date, double Value, string Source)>>();
            List<(DateTime Date, double Value, string Source)> RungHist(int n)
            {
                if (!rungData.TryGetValue(n, out var l))
                {
                    l = store.GetDailyWithSource(
                        pat.Replace("{N}", n.ToString()) + srcSuffix + " Curncy", lookback);
                    if (l.Count == 0 && srcSuffix.Length > 0)
                        l = store.GetDailyWithSource(
                            pat.Replace("{N}", n.ToString()) + " Curncy", lookback);
                    rungData[n] = l;
                }
                return l;
            }
            var boundSet = clustered.ToHashSet();
            (DateTime Date, double Value, string Source)? ValueAt(DateTime contract, DateTime then, int depth = 0)
            {
                if (depth > 6) return null;
                if (boundSet.Contains(then.Date)) then = then.Date.AddDays(-1);
                int idx = clustered.Count(x => x > then.Date && x <= contract.Date);
                // 0 boundaries between the day and the contract means the contract's own period
                // has already started — there is no rung for it; publishing rung 1's rate there
                // was the audit's Max(1,…) coercion (2026-08-26)
                if (idx < 1 || idx > 13) return null;
                var l = RungHist(idx);
                for (int i = l.Count - 1; i >= 0; i--)
                    if (l[i].Date.Date <= then.Date)
                    {
                        // a walk-back RESOLVING to a boundary close recomputes from the day
                        // before it (mixed-state decision-day closes — the stitcher's rule)
                        if (boundSet.Contains(l[i].Date.Date))
                            return ValueAt(contract, l[i].Date.Date.AddDays(-1), depth + 1);
                        return (l[i].Date.Date, l[i].Value, l[i].Source);
                    }
                return null;
            }
            // a change anchor may walk back over a weekend/holiday but never so far that the
            // label lies — same 10-day cap as the email's ChangeToBp (audit 2026-08-26)
            double? Chg(double cur, (DateTime Date, double Value, string Source)? anchor, DateTime target)
                => anchor is { } a && (target.Date - a.Date).TotalDays <= 10
                    ? (cur - a.Value) * 100.0 : null;

            // last N BUSINESS days strictly before asOf — the store excludes today by design,
            // and the old calendar-day window shipped ~30% fewer rows than asked (audit 2026-08-26)
            var days = new List<DateTime>();
            for (var d = PrevBd(asOf.Date.AddDays(1)); days.Count < historyDays; d = PrevBd(d))
                if (d < asOf.Date) days.Add(d);
            days.Reverse();

            var outRows = new List<HistRow>();
            foreach (var day in days)
                for (int ri = 0; ri < run.Rows.Count; ri++)
                {
                    var m = run.Rows[ri];
                    // Y/E-turn periods never publish as policy history (the runs sheet labels
                    // them; these tables must not launder the turn print — audit 2026-08-26)
                    if (m.TurnPeriod) continue;
                    if (ValueAt(m.Date, day) is not { } cur) continue;
                    // a boundary day's own row is unanchorable for Δ1d (the walk-back resolves
                    // both sides to the same pre-boundary close — publishing 0.0 there read as
                    // "unchanged"; blank is the honest value)
                    bool boundaryDay = boundSet.Contains(day);
                    var prev = boundaryDay ? null : ValueAt(m.Date, PrevBd(day));
                    var week = ValueAt(m.Date, day.AddDays(-7));
                    var month = ValueAt(m.Date, WeeklyCurves.MonthAgo(day));
                    var hEnd = m.EndDate ?? (ri + 1 < run.Rows.Count ? run.Rows[ri + 1].Date : (DateTime?)null);
                    outRows.Add(new HistRow(day, m.Date, hEnd, cur.Value, cur.Source,
                        Chg(cur.Value, prev, PrevBd(day)),
                        Chg(cur.Value, week, day.AddDays(-7)),
                        Chg(cur.Value, month, WeeklyCurves.MonthAgo(day))));
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
