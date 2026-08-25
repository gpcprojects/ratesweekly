using ClosedXML.Excel;
using RateDesk.Core;

namespace RateDesk.Weekly.Core.Daily
{
    /// <summary>The failsafe half of the integrated system (desk 2026-08-20): when the app or the
    /// Bloomberg API is down, the desk stores the day in the FALLBACK WORKBOOK exactly as they
    /// always have (the incumbent Central Bank OIS MAIN.xlsm's per-bank UPDATE &amp; STORE), and the
    /// next DAILY RUN ingests any dates the store is missing from its Historical_* tabs — so
    /// history is continuous across an outage and every change column computes normally.
    ///
    /// Mapping: a manual row is (date, period start, rate); the rung that carried that period on
    /// that date is the count of clustered roll boundaries in (date, start] — the same arithmetic
    /// as the stitcher — and the rate lands in the store under that rung's COMPOSITE ticker with
    /// source='xls'. INSERT-ONLY: a manual entry never overwrites an engine ('bbg') row, and the
    /// next real Bloomberg pull for the same date supersedes it. The workbook is opened READ-ONLY
    /// from a temp copy (the desk may have it open in Excel).</summary>
    public static class FallbackIngest
    {
        /// <summary>Sheet name → meetings.json run name: the incumbent's country codes AND the
        /// app save-down workbooks' direct bank names (desk 2026-08-25) — one reader, both
        /// fallback sources.</summary>
        public static readonly (string Sheet, string Run)[] SheetMap =
        {
            ("Historical_AU", "RBA"), ("Historical_NZ", "RBNZ"), ("Historical_EU", "ECB"),
            ("Historical_UK", "MPC"), ("Historical_US", "FOMC"), ("Historical_CD", "BOC"),
            ("Historical_NOK", "NORGES"), ("Historical_JPY", "BOJ"), ("Historical_SEK", "RIKSBANK"),
            ("Historical_RBA", "RBA"), ("Historical_RBNZ", "RBNZ"), ("Historical_ECB", "ECB"),
            ("Historical_MPC", "MPC"), ("Historical_FOMC", "FOMC"), ("Historical_BOC", "BOC"),
            ("Historical_NORGES", "NORGES"), ("Historical_BOJ", "BOJ"), ("Historical_RIKSBANK", "RIKSBANK"),
        };

        public sealed record Result(int RowsIngested, List<DateTime> Dates, List<string> Notes);

        /// <summary><paramref name="minDate"/>: ingest only rows dated ON/AFTER it. Used when
        /// re-reading the app's OWN save-down workbooks: their app-written history rows are
        /// roll-corrected walk-back values (older than the file), while rows the DESK stored
        /// via the macro are stamped the day they pressed Store (the file date or later) —
        /// only those are genuine manual marks that belong in raw ticker history.</summary>
        public static Result Run(string workbookPath, HistoryStore store, Action<string>? log = null,
            DateTime? minDate = null)
        {
            var notes = new List<string>();
            var dates = new SortedSet<DateTime>();
            int wrote = 0;
            if (!File.Exists(workbookPath))
            {
                notes.Add($"fallback ingest: workbook not found at {workbookPath} — skipped");
                return new Result(0, new List<DateTime>(), notes);
            }

            // read a temp copy — the live workbook is usually open in Excel
            var tmp = Path.Combine(Path.GetTempPath(), "rw-fallback-" + Guid.NewGuid().ToString("N") + ".xlsm");
            File.Copy(workbookPath, tmp, overwrite: true);
            try
            {
                using var wb = new XLWorkbook(tmp);
                foreach (var (sheetName, runName) in SheetMap)
                {
                    if (!wb.TryGetWorksheet(sheetName, out var ws)) continue;
                    var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                        s.Name.Equals(runName, StringComparison.OrdinalIgnoreCase));
                    var pat = sched?.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                    if (sched == null || pat == null) continue;

                    var bounds = new List<DateTime>();
                    foreach (var d in sched.DecisionDates.Concat(sched.Dates).Concat(sched.PastDates)
                                 .Select(x => x.Date).OrderBy(x => x))
                        if (bounds.Count == 0 || (d - bounds[^1]).TotalDays > 14) bounds.Add(d);

                    // engine coverage per rung — across BOTH spellings (contributor + composite):
                    // only ingest dates the engine has NOTHING for on either
                    var srcSuffix = string.IsNullOrEmpty(sched.Source) ? "" : " " + sched.Source;
                    var have = new Dictionary<int, HashSet<DateTime>>();
                    HashSet<DateTime> Have(int rung)
                    {
                        if (!have.TryGetValue(rung, out var set))
                        {
                            set = store.GetDaily(pat.Replace("{N}", rung.ToString()) + srcSuffix + " Curncy", 400)
                                .Select(x => x.Date.Date).ToHashSet();
                            if (srcSuffix.Length > 0)
                                set.UnionWith(store.GetDaily(pat.Replace("{N}", rung.ToString()) + " Curncy", 400)
                                    .Select(x => x.Date.Date));
                            have[rung] = set;
                        }
                        return set;
                    }

                    int sheetRows = 0;
                    foreach (var row in ws.RowsUsed().Skip(1))
                    {
                        var d = AsDate(row.Cell(1));           // CurrentDate
                        var start = AsDate(row.Cell(3));       // StartDate
                        var rate = row.Cell(5).TryGetValue(out double rv) ? rv : (double?)null;
                        if (d is null || start is null || rate is null) continue;
                        if (rate is <= 0 or > 25) continue;    // sanity — a percent-scale policy rate
                        if (d.Value >= DateTime.Today) continue;
                        if (minDate is { } md && d.Value < md.Date) continue;
                        // ingest window: outage gaps are recent by nature, and the engine-coverage
                        // check below only looks 400 days back — a legacy row older than that
                        // would ingest unchecked (and its rung mapping is unreliable that far out)
                        if (d.Value < DateTime.Today.AddDays(-370)) continue;

                        int rung = bounds.Count(b => b > d.Value && b <= start.Value);
                        if (rung < 1 || rung > 13) continue;
                        if (Have(rung).Contains(d.Value)) continue;   // engine data exists — never touch

                        // manual rows land under the run's ACTIVE contributor spelling — the
                        // series the stitcher and history sheets read first (2026-08-25)
                        var tkr = pat.Replace("{N}", rung.ToString()) + srcSuffix + " Curncy";
                        wrote += store.UpsertDaily(tkr,
                            new[] { new RateDesk.Core.Market.HistPoint(d.Value, rate.Value) },
                            excludeToday: true, source: "xls");
                        Have(rung).Add(d.Value);
                        dates.Add(d.Value);
                        sheetRows++;
                    }
                    if (sheetRows > 0)
                        notes.Add($"fallback ingest: {runName} +{sheetRows} manual row(s) from {sheetName}");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"fallback ingest: FAILED reading workbook — {ex.Message}");
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* temp */ }
            }

            if (wrote > 0)
                notes.Add($"fallback ingest: {wrote} row(s) across {dates.Count} outage day(s) " +
                          $"({string.Join(", ", dates.Select(d => d.ToString("dd-MMM")))}) — marked source=xls");
            foreach (var n in notes) log?.Invoke(n);
            return new Result(wrote, dates.ToList(), notes);
        }

        private static DateTime? AsDate(IXLCell c)
        {
            if (c.DataType == XLDataType.DateTime) return c.GetDateTime().Date;
            var s = c.GetString().Trim();
            if (string.IsNullOrEmpty(s)) return null;
            // the incumbent mixes real dates with dd/MM/yyyy and M/d/yyyy text
            string[] fmts = { "dd/MM/yyyy", "d/M/yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" };
            foreach (var f in fmts)
                if (DateTime.TryParseExact(s, f, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d))
                    return d.Date;
            return null;
        }
    }
}
