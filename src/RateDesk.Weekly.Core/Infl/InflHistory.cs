using ClosedXML.Excel;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Infl
{
    /// <summary>THE UNIFIED INFLATION-FIXINGS HISTORY (desk 2026-08-25) — one store of daily
    /// CPI/RPI/HICP fixing marks keyed by fixing identity (family + reference month), merged
    /// from two sources under the desk's rule: where the external pricer's history is GOOD,
    /// keep it; where it is bad, Bloomberg fills.
    ///
    /// "Good" is provable, not assumed — every external row must pass BASE-PRINT VALIDATION:
    /// its Base column must equal the published index of its fixing month minus 12 months.
    /// The Aug-2025 comparison analysis found the pricer's export sometimes writes its curve
    /// tail with month labels shifted 1-2 slots; a row whose Base contradicts its label but
    /// uniquely matches a different month's print is RE-KEYED to the month its own Base proves.
    /// Placeholder zeros, within-save duplicate copies, internally inconsistent rows
    /// (yoy vs Mid/Base-1) and unresolvable rows are rejected — Bloomberg fills those days.
    ///
    /// Bloomberg closes are mapped to fixing identity through each ticker's own RECORDED
    /// MATURITY (maturity minus the family lag = the reference month — the ticker's own field,
    /// no release calendar, no inference). Days with no recorded maturity are skipped, not
    /// guessed. Values are the market's native quote: CPI = forecast index level,
    /// RPI/HICP = YoY in bp.</summary>
    public static class InflHistory
    {
        public sealed record Fam(string Key, string Root, string Tab, string IndexTicker, bool IsIndexUnit);

        public static readonly Fam[] Families =
        {
            new("CPI",  "USSWIF", "CPI_History",  "CPURNSA Index", true),
            new("RPI",  "BPSWIF", "RPI_History",  "UKRPI Index",   false),
            new("HICP", "EUSWIF", "HICP_History", "CPTFEMU Index", false),
        };

        /// <summary>CPTFEMU was restated to a 2026 basis; the external sheet stored old-basis
        /// values until Feb-2026. Base matching accepts either basis (YoY itself is
        /// basis-invariant, which is why the stored HICP unit is YoY bp).</summary>
        public const double HicpRebase = 1.281085;

        private static readonly Dictionary<string, double> BaseTol = new()
            { ["CPI"] = 0.02, ["RPI"] = 0.06, ["HICP"] = 0.02 };

        public sealed record IngestResult(int Ingested, int Rekeyed, int DupeCopies,
            int Placeholders, int Inconsistent, int Unresolved, int NoPrintCheck);

        // ------------------------------------------------------------------ xlsm ingest ----
        /// <summary><paramref name="onlyMissingOrChanged"/>: skip rows the store already holds
        /// at the same value — used when re-reading the app's OWN save-down workbooks, so rows
        /// the app itself wrote keep their bbg provenance and only genuine manual edits (which
        /// change a number) are promoted to validated 'xls' rows.</summary>
        public static IngestResult Ingest(string workbookPath, HistoryStore store,
            Action<string>? log = null, bool onlyMissingOrChanged = false, DateTime? minDate = null)
        {
            // never open the desk's live workbook directly — read a temp copy (FallbackIngest rule)
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-infl-" + Guid.NewGuid().ToString("N") + ".xlsm");
            File.Copy(workbookPath, tmp, overwrite: true);
            try { return IngestCopy(tmp, store, log, onlyMissingOrChanged, minDate); }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static IngestResult IngestCopy(string path, HistoryStore store, Action<string>? log,
            bool onlyMissingOrChanged = false, DateTime? minDate = null)
        {
            int ingested = 0, rekeyed = 0, dupes = 0, placeholders = 0, inconsistent = 0,
                unresolved = 0, noCheck = 0, adopted = 0;
            using var wb = new XLWorkbook(path);
            foreach (var fam in Families)
            {
                if (!wb.TryGetWorksheet(fam.Tab, out var ws)) { log?.Invoke($"infl: no tab {fam.Tab}"); continue; }
                var prints = Prints(store, fam.IndexTicker);
                double tol = BaseTol[fam.Key];

                var byFix = new Dictionary<string, List<HistPoint>>();
                DateTime? curObs = null;
                (int M, int Y)? chain = null;
                var seenSigs = new HashSet<(double, double)>();

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var obs = AsDate(row.Cell(1));
                    if (obs is null) { chain = null; continue; }
                    if (minDate is { } md && obs.Value < md.Date) { chain = null; continue; }
                    if (curObs != obs) { curObs = obs; chain = null; seenSigs.Clear(); }

                    var (m, yExplicit) = MonthOf(row.Cell(2));
                    if (m is null) { chain = null; continue; }
                    double? baseV = AsNum(row.Cell(3)), mid = AsNum(row.Cell(4)), yoy = AsNum(row.Cell(5));

                    int y;
                    if (yExplicit is { } ye) y = ye;
                    else if (chain is { } c && NextMonth(c).M == m) y = NextMonth(c).Y;
                    else y = PinYear(m.Value, obs.Value);
                    chain = (m.Value, y);

                    // ---- validation gates (bad rows never enter the unified history) ----
                    double? val = fam.IsIndexUnit ? mid : yoy * 100.0;
                    if (val is null || (fam.IsIndexUnit ? val < 100 : Math.Abs(val.Value) < 0.5))
                        { placeholders++; continue; }
                    if (baseV is { } b0 && mid is { } m0 && yoy is { } y0
                        && Math.Abs(m0 / b0 - 1 - y0 / 100.0) > 0.005) { inconsistent++; continue; }

                    int km = m.Value, ky = y;
                    if (baseV is { } b)
                    {
                        var own = MatchesPrint(prints, b, m.Value, y - 1, tol, fam);
                        if (own == false)
                        {
                            var cand = prints.Keys
                                .Where(k => MatchesPrint(prints, b, k.M, k.Y, tol, fam) == true)
                                .Select(k => (M: k.M, Y: k.Y + 1))
                                .Where(k => MonthsFrom(obs.Value, k.M, k.Y) is >= -1 and <= 13)
                                .Distinct().ToList();
                            if (cand.Count == 1) { (km, ky) = cand[0]; rekeyed++; }
                            else { unresolved++; continue; }
                        }
                        else if (own is null)
                        {
                            noCheck++;   // print not in store — kept on label
                            // BBG HOLE ADOPTION (desk 2026-08-25, the shutdown-skipped Oct-25
                            // CPI): when the base month is old enough that its print MUST have
                            // existed and Bloomberg simply has none, adopt the sheet's own base
                            // as the print (source 'xls', insert-only — a real Bloomberg print
                            // for that month supersedes it the day one appears). A base month
                            // still inside the publication lag is a FORECAST base — never adopted.
                            var baseMonthEnd = new DateTime(ky - 1, km, DateTime.DaysInMonth(ky - 1, km));
                            if (baseMonthEnd < obs.Value.AddDays(-45))
                            {
                                store.UpsertDaily(fam.IndexTicker,
                                    new[] { new HistPoint(baseMonthEnd, b) },
                                    excludeToday: false, source: "xls");
                                prints[(km, ky - 1)] = b;   // later rows validate against it
                                adopted++;
                            }
                        }

                        var sig = (Math.Round(b, 6), Math.Round(val.Value, 6));
                        if (!seenSigs.Add(sig)) { dupes++; continue; }
                    }

                    var fix = $"{ky:0000}-{km:00}";
                    if (!byFix.TryGetValue(fix, out var l)) byFix[fix] = l = new List<HistPoint>();
                    l.Add(new HistPoint(obs.Value, val.Value));
                }

                Dictionary<(string, DateTime), double>? existing = null;
                if (onlyMissingOrChanged)
                    existing = store.GetFixingHistory(fam.Key)
                        .ToDictionary(x => (x.Fix, x.Date), x => x.Value);
                foreach (var (fix, pts) in byFix)
                {
                    var keep = existing == null ? pts
                        : pts.Where(p => !existing.TryGetValue((fix, p.Date.Date), out var v)
                                         || Math.Abs(v - p.Value) > 1e-9).ToList();
                    if (keep.Count > 0) ingested += store.UpsertFixings(fam.Key, fix, keep, "xls");
                }
                log?.Invoke($"infl: {fam.Key} sheet ingest — {byFix.Values.Sum(v => v.Count)} validated rows" +
                            (onlyMissingOrChanged ? " (unchanged rows skipped)" : ""));
            }
            var res = new IngestResult(ingested, rekeyed, dupes, placeholders, inconsistent, unresolved, noCheck);
            log?.Invoke($"infl: ingest done — {res.Ingested} rows written ({res.Rekeyed} re-keyed by base-print, " +
                        $"{res.DupeCopies} duplicate copies, {res.Placeholders} placeholders, " +
                        $"{res.Inconsistent} inconsistent, {res.Unresolved} unresolvable dropped; " +
                        $"{res.NoPrintCheck} kept on label with no print to check against" +
                        (adopted > 0 ? $"; {adopted} sheet base(s) adopted for Bloomberg print holes" : "") + ")");
            return res;
        }

        // --------------------------------------------------- maturity-documented bbg fill ----
        /// <summary>Map recent Bloomberg closes of every fixing ticker to their fixing identity
        /// via the maturity RECORDED for that day (ticker's own field; unrecorded days are
        /// skipped, never guessed) and upsert as 'bbg' — fills days the sheet lacks and keeps
        /// the unified history current from live runs alone.</summary>
        public static int Maintain(HistoryStore store, Action<string>? log = null, int lookbackDays = 45)
        {
            int wrote = 0;
            foreach (var fam in Families)
            {
                var byFix = new Dictionary<string, List<HistPoint>>();
                for (int m = 1; m <= 12; m++)
                {
                    var tk = $"{fam.Root}{m} Curncy";
                    foreach (var p in store.GetDaily(tk, lookbackDays))
                    {
                        if (store.MaturityAsOf(tk, p.Date) is not { } mat) continue;
                        var refMonth = RefMonth(mat, m);
                        if (refMonth is null) continue;
                        var fix = $"{refMonth.Value.Year:0000}-{refMonth.Value.Month:00}";
                        if (!byFix.TryGetValue(fix, out var l)) byFix[fix] = l = new List<HistPoint>();
                        l.Add(p);
                    }
                }
                foreach (var (fix, pts) in byFix)
                    wrote += store.UpsertFixings(fam.Key, fix, pts, "bbg");
            }
            log?.Invoke($"infl: bbg maintain wrote {wrote} rows (maturity-documented days only)");
            return wrote;
        }

        /// <summary>One-off seed of validated historical Bloomberg marks from the Aug-2025
        /// comparison analysis (print-anchored mapping; rows within ±3bd of a ticker re-point
        /// already excluded upstream). CSV: family,ticker,date,px,fix_month,fix_year,near_roll.</summary>
        public static int SeedBackfill(string csvPath, HistoryStore store, Action<string>? log = null)
        {
            var byKey = new Dictionary<(string, string), List<HistPoint>>();
            foreach (var line in File.ReadLines(csvPath).Skip(1))
            {
                var c = line.Split(',');
                if (c.Length < 7 || c[6].Trim() == "1") continue;
                var fam = c[0].Trim();
                if (!Families.Any(f => f.Key == fam)) continue;
                var fix = $"{int.Parse(c[5]):0000}-{int.Parse(c[4]):00}";
                var key = (fam, fix);
                if (!byKey.TryGetValue(key, out var l)) byKey[key] = l = new List<HistPoint>();
                l.Add(new HistPoint(
                    DateTime.ParseExact(c[2].Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(c[3], System.Globalization.CultureInfo.InvariantCulture)));
            }
            int wrote = 0;
            foreach (var ((fam, fix), pts) in byKey) wrote += store.UpsertFixings(fam, fix, pts, "bbg");
            log?.Invoke($"infl: backfill seed wrote {wrote} bbg rows from {csvPath}");
            return wrote;
        }

        // ---------------------------------------------------------------- today's marks ----
        public sealed record Mark(DateTime RefMonth, double Value);   // native unit

        /// <summary>Live fixing marks captured during a run (per family), for the save-down
        /// workbook's "today" blocks. Set by the daily build while its snapshot is in hand;
        /// falls back to the last documented closes when no live snapshot exists.</summary>
        public static Dictionary<string, List<Mark>>? LastLiveMarks { get; set; }

        /// <summary>Next scheduled print per family (Bloomberg ECO_RELEASE_DT on the fixing
        /// index), captured during a run — the "Next Print:" header line. Null entries are
        /// OMITTED downstream, never guessed.</summary>
        public static Dictionary<string, DateTime>? LastNextPrints { get; set; }

        public static Dictionary<string, List<Mark>> CollectLiveMarks(
            RateDesk.Core.Market.RatesSnapshot snap, HistoryStore store)
        {
            var all = new Dictionary<string, List<Mark>>();
            foreach (var fam in Families)
            {
                var l = new List<Mark>();
                for (int m = 1; m <= 12; m++)
                {
                    var tk = $"{fam.Root}{m} Curncy";
                    var q = snap.Get(tk);
                    var mat = q?.Maturity ?? store.MaturityLatest(tk);
                    if (q?.Mid is not { } px || mat is not { } mt) continue;
                    if (RefMonth(mt, m) is { } rm) l.Add(new Mark(rm, px));
                }
                all[fam.Key] = l.OrderBy(x => x.RefMonth).ToList();
            }
            return all;
        }

        /// <summary>The last documented close of every still-quoted fixing — the offline
        /// fallback for "today's" marks (staleness visible via the workbook's own as-of).</summary>
        public static Dictionary<string, List<Mark>> LatestMarks(HistoryStore store)
        {
            var all = new Dictionary<string, List<Mark>>();
            foreach (var fam in Families)
            {
                var hist = store.GetFixingHistory(fam.Key);
                if (hist.Count == 0) { all[fam.Key] = new(); continue; }
                var lastDay = hist.Max(x => x.Date);
                all[fam.Key] = hist.Where(x => x.Date == lastDay)
                    .Select(x => new Mark(
                        DateTime.ParseExact(x.Fix + "-01", "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture), x.Value))
                    .OrderBy(x => x.RefMonth).ToList();
            }
            return all;
        }

        // ------------------------------------------------------------ display derivation ----
        /// <summary>One rendered fixing row — the shape of the incumbent's Table blocks and the
        /// desk's Bloomberg screen: Month | Base | Mid | YoY% | MoM% | Δ index 1d/1w/1m. Derived
        /// once here and consumed by the email section, the lean runs xlsx and the save-down
        /// book, so all three always publish the same numbers.</summary>
        public sealed record DisplayRow(DateTime RefMonth, double? BaseV, double? Mid,
            double? Yoy, double? Mom, double? D1, double? W1, double? M1);

        public static Dictionary<(int M, int Y), double> PrintsOf(HistoryStore store, Fam fam)
        {
            var d = new Dictionary<(int, int), double>();
            foreach (var p in store.GetDaily(fam.IndexTicker, 2600))
                d[(p.Date.Month, p.Date.Year)] = p.Value;   // stamped at the reference month
            return d;
        }

        /// <summary>Derive the full display block for one family from marks (native unit) +
        /// published prints + the unified history (for the 1d/1w/1m index changes). MoM chains
        /// mid-over-previous-mid, anchoring the front row on the last published print — the
        /// incumbent Table's own convention.</summary>
        public static List<DisplayRow> BuildDisplayRows(HistoryStore store, Fam fam,
            IEnumerable<Mark> famMarks, DateTime asOf)
        {
            var prints = PrintsOf(store, fam);
            var hist = store.GetFixingHistory(fam.Key)
                .GroupBy(x => x.Fix).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Date).ToList());
            var rows = new List<DisplayRow>();
            double? prevMid = null;
            foreach (var mk in famMarks.OrderBy(x => x.RefMonth))
            {
                double? baseV = prints.TryGetValue((mk.RefMonth.Month, mk.RefMonth.Year - 1), out var b) ? b : null;
                double? mid, yoy;
                if (fam.IsIndexUnit) { mid = mk.Value; yoy = baseV is { } b2 ? (mk.Value / b2 - 1) * 100.0 : null; }
                else { yoy = mk.Value / 100.0; mid = baseV is { } b3 ? b3 * (1 + mk.Value / 10000.0) : null; }
                double? anchor = prevMid ?? (prints.TryGetValue(
                    (mk.RefMonth.AddMonths(-1).Month, mk.RefMonth.AddMonths(-1).Year), out var pa) ? pa : null);
                double? mom = mid is { } m0 && anchor is { } a0 ? (m0 / a0 - 1) * 100.0 : null;
                double? d1 = null, w1 = null, m1 = null;
                if (mid is { } midNow && hist.TryGetValue($"{mk.RefMonth:yyyy-MM}", out var series))
                {
                    double? MidAt(DateTime then)
                    {
                        for (int i = series.Count - 1; i >= 0; i--)
                            if (series[i].Date <= then)
                                return fam.IsIndexUnit
                                    ? series[i].Value
                                    : baseV is { } b4 ? b4 * (1 + series[i].Value / 10000.0) : null;
                        return null;
                    }
                    d1 = midNow - MidAt(PrevBd(asOf.Date));
                    w1 = midNow - MidAt(asOf.Date.AddDays(-7));
                    m1 = midNow - MidAt(Series.WeeklyCurves.MonthAgo(asOf.Date));
                }
                rows.Add(new DisplayRow(mk.RefMonth, baseV, mid, yoy, mom, d1, w1, m1));
                prevMid = mid;
            }
            return rows;
        }

        private static DateTime PrevBd(DateTime d)
        {
            var p = d.AddDays(-1);
            while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
            return p;
        }

        // ------------------------------------------------------------------- helpers ----
        /// <summary>Reference month from a maturity: walk the lag back until the month matches
        /// the ticker's own calendar-month index (USD/EUR are +3m, GBP +2m — but derived, never
        /// hard-coded; CpiFixings' rule).</summary>
        public static DateTime? RefMonth(DateTime maturity, int monthIndex)
        {
            for (int lag = 1; lag <= 6; lag++)
            {
                var r = maturity.AddMonths(-lag);
                if (r.Month == monthIndex) return new DateTime(r.Year, r.Month, 1);
            }
            return null;
        }

        private static Dictionary<(int M, int Y), double> Prints(HistoryStore store, string idxTicker)
        {
            var d = new Dictionary<(int, int), double>();
            foreach (var p in store.GetDaily(idxTicker, 2600))
                d[(p.Date.Month, p.Date.Year)] = p.Value;   // stamped at the reference month
            return d;
        }

        private static bool? MatchesPrint(Dictionary<(int M, int Y), double> prints, double baseV,
            int m, int y, double tol, Fam fam)
        {
            if (!prints.TryGetValue((m, y), out var p)) return null;
            if (Math.Abs(baseV - p) < tol) return true;
            if (fam.Key == "HICP" && Math.Abs(baseV - p * HicpRebase) < tol * HicpRebase) return true;
            return false;
        }

        private static int MonthsFrom(DateTime obs, int m, int y) => (y * 12 + m) - (obs.Year * 12 + obs.Month);

        private static (int M, int Y) NextMonth((int M, int Y) c) =>
            c.M < 12 ? (c.M + 1, c.Y) : (1, c.Y + 1);

        /// <summary>The front unfixed month is never more than ~2 months from the save date, so
        /// the candidate years (12 months apart) are unambiguous.</summary>
        private static int PinYear(int m, DateTime obs) =>
            new[] { obs.Year - 1, obs.Year, obs.Year + 1 }
                .OrderBy(y => Math.Abs((new DateTime(y, m, 15) - obs).TotalDays)).First();

        private static readonly string[] MonthNames =
            { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        private static (int?, int?) MonthOf(IXLCell cell)
        {
            if (cell.DataType == XLDataType.DateTime)
            {
                var d = cell.GetDateTime();
                return (d.Month, d.Year);
            }
            var s = cell.GetString().Trim();
            if (s.Length >= 3)
            {
                var i = Array.IndexOf(MonthNames, s[..3].ToLowerInvariant());
                if (i >= 0) return (i + 1, null);
            }
            return (null, null);
        }

        private static DateTime? AsDate(IXLCell cell) =>
            cell.DataType == XLDataType.DateTime ? cell.GetDateTime().Date
            : DateTime.TryParse(cell.GetString(), out var d) ? d.Date : null;

        private static double? AsNum(IXLCell cell)
        {
            if (cell.DataType == XLDataType.Number) return cell.GetDouble();
            return double.TryParse(cell.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
    }
}
