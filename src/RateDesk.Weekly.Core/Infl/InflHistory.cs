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

                Dictionary<(string, DateTime), (double V, string Src)>? existing = null;
                if (onlyMissingOrChanged)
                    existing = store.GetFixingHistory(fam.Key)
                        .ToDictionary(x => (x.Fix, x.Date), x => (x.Value, x.Source));
                foreach (var (fix, pts) in byFix)
                {
                    var keep = existing == null ? pts
                        : pts.Where(p => !existing.TryGetValue((fix, p.Date.Date), out var v)
                                         || Math.Abs(v.V - p.Value) > 1e-9).ToList();
                    // a saved-book row DISPLACING a documented Bloomberg close is honoured (the
                    // override capability is the point of the books) but never silent (audit
                    // 2026-08-26: an Excel recalc could otherwise shadow real closes forever)
                    if (existing != null)
                        foreach (var p in keep)
                            if (existing.TryGetValue((fix, p.Date.Date), out var old)
                                && old.Src == "bbg" && Math.Abs(old.V - p.Value) > 1e-9)
                                log?.Invoke($"! CHECK: {fam.Key} {fix} {p.Date:dd-MMM-yy} — saved book " +
                                            $"overrides a Bloomberg close {old.V:0.####} → {p.Value:0.####} " +
                                            "(override honoured; verify it was deliberate)");
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
        /// <summary><paramref name="bars"/>: when given, every day's value is the LONDON SNAP from
        /// intraday bars (16:15, or 16:30 up to the cutover) with the daily close as the fallback —
        /// the OIS boards' own convention, now applied here too (desk 2026-08-26, the "CoD looks
        /// rough" question). WHY it matters: the fixing strip's months are posted by contributors
        /// at DIFFERENT times — probed today the twelve RPI months last updated between 16:39 and
        /// 17:15 — so a close-stamped anchor differenced against a snap-stamped mark mixes up to
        /// 40 minutes of tape PER MONTH, and adjacent fixings dislocate for no market reason. Both
        /// sides of every change must be the same construct at the same time of day.</summary>
        public static int Maintain(HistoryStore store, Action<string>? log = null, int lookbackDays = 45,
            IHistoryProvider? bars = null)
        {
            int wrote = 0, snapped = 0;
            foreach (var fam in Families)
            {
                var byFix = new Dictionary<string, List<HistPoint>>();

                // TWO CANDIDATE MARKS PER TENOR PER DAY, AND THE STRIP PICKS (2026-08-31).
                //
                // A monthly inflation fixing is quoted on its own and trades thinly, so any ONE
                // observation of it can be junk while the rest of the curve is fine. Both marks
                // have now been caught doing it, a working day apart:
                //   · Mar-27 (BPSWIF3) 27-Aug — the CLOSE was 415.000 while the tenor traded
                //     429-435 all day. A bad tick printed twice and the second landed on the last
                //     bar, so it became the close. The 16:15 snap was 435.500.
                //   · Nov-26 (BPSWIF11) 28-Aug — the SNAP was 434.250 while the close was
                //     439.375. Nothing was wrong with either: the last trade before 16:15 really
                //     was 434.250, the tenor jumped at 16:15 and held 439.375 for five hours.
                //     Thin instrument, two honest marks, 5bp apart.
                // Preferring one source wholesale just moves the error between tenors, which is
                // exactly what happened when snaps were switched on: Mar-27 came right and
                // Nov-26 went wrong.
                //
                // So keep both and let the STRIP arbitrate, which is the same discriminator the
                // OIS side uses and the one measured to work here: a real move carries the whole
                // curve together (the 25-Aug Ofgem cap reset moved all twelve months, worst
                // single-tenor disagreement 4.8bp), while a bad mark moves one month alone.
                // For each day take the median day-over-day change across the strip, then give
                // each tenor whichever of its two candidates lands closer to it. Nothing is
                // invented and nothing is discarded — both numbers are real prints of that
                // tenor on that day, and the strip only decides which one to believe.
                var close = new Dictionary<int, Dictionary<DateTime, double>>();
                var snap = new Dictionary<int, Dictionary<DateTime, double>>();
                for (int m = 1; m <= 12; m++)
                {
                    var tk = $"{fam.Root}{m} Curncy";
                    close[m] = store.GetDaily(tk, lookbackDays)
                        .GroupBy(p => p.Date.Date).ToDictionary(g => g.Key, g => g.Last().Value);
                    snap[m] = new Dictionary<DateTime, double>();
                    if (bars == null) continue;
                    try
                    {
                        // same snap discipline (and same cutover) as the meeting boards
                        foreach (var sp in bars.GetLondonSnaps(tk, lookbackDays, new TimeSpan(16, 30, 0)))
                            if (sp.Date.Date < RateDesk.Core.PricingService.SnapTimeCutover)
                                snap[m][sp.Date.Date] = sp.Value;
                        foreach (var sp in bars.GetLondonSnaps(tk, lookbackDays, new TimeSpan(16, 15, 0)))
                            if (sp.Date.Date >= RateDesk.Core.PricingService.SnapTimeCutover)
                                snap[m][sp.Date.Date] = sp.Value;
                    }
                    catch { /* no bars for this rung — closes still serve */ }
                }

                var chosen = new Dictionary<int, Dictionary<DateTime, double>>();
                for (int m = 1; m <= 12; m++) chosen[m] = new Dictionary<DateTime, double>();
                var prev = new Dictionary<int, double>();       // last CHOSEN mark (published-series continuity)
                var prevDay = new Dictionary<int, DateTime>();  // ...and the day it belongs to
                var prevClose = new Dictionary<int, double>();  // the PREVIOUS day's closes, and only those
                DateTime lastDay = default;
                foreach (var day in close.Values.SelectMany(d => d.Keys)
                             .Concat(snap.Values.SelectMany(d => d.Keys)).Distinct().OrderBy(d => d))
                {
                    // the strip's own move that day: today's close against YESTERDAY'S CLOSE,
                    // never against yesterday's pick. The old code differenced against prev
                    // picks, so any day the snaps won displaced the next day's median by the
                    // close−snap gap — the reference was a lagged function of the choice, the
                    // one property its own comment ruled out (audit 2026-08-31, scenario 152).
                    // Only consecutive-day pairs contribute: a tenor that skipped a day would
                    // otherwise feed a multi-day change into a one-day median.
                    var strip = new List<double>();
                    for (int m = 1; m <= 12; m++)
                        if (close[m].TryGetValue(day, out var cv) && prevClose.TryGetValue(m, out var pv))
                            strip.Add(cv - pv);
                    strip.Sort();
                    double? med = strip.Count == 0 ? null
                        : strip.Count % 2 == 1 ? strip[strip.Count / 2]
                        : (strip[strip.Count / 2 - 1] + strip[strip.Count / 2]) / 2.0;

                    for (int m = 1; m <= 12; m++)
                    {
                        bool hasC = close[m].TryGetValue(day, out var c0);
                        bool hasS = snap[m].TryGetValue(day, out var s0);
                        if (!hasC && !hasS) continue;
                        double pick;
                        bool contiguous = prev.ContainsKey(m)
                                          && prevDay.TryGetValue(m, out var pd) && pd == lastDay;
                        if (!hasC) { pick = s0; snapped++; }
                        // no reference median, or this tenor's own last mark is not from the
                        // preceding day: the close is the documented default. (The old med=0.0
                        // fallback quietly preferred whichever candidate moved LESS, on exactly
                        // the thin days the arbitration exists for.)
                        else if (!hasS || med is not { } md || !contiguous) pick = c0;
                        else
                        {
                            var p0 = prev[m];
                            if (Math.Abs(s0 - p0 - md) < Math.Abs(c0 - p0 - md)) { pick = s0; snapped++; }
                            else pick = c0;
                        }
                        chosen[m][day] = pick;
                        prev[m] = pick;
                        prevDay[m] = day;
                    }

                    // roll the close-reference forward — today's closes only, so tomorrow's
                    // median is built from genuine one-day moves or not at all
                    prevClose.Clear();
                    for (int m = 1; m <= 12; m++)
                        if (close[m].TryGetValue(day, out var cv2)) prevClose[m] = cv2;
                    lastDay = day;
                }

                for (int m = 1; m <= 12; m++)
                {
                    var tk = $"{fam.Root}{m} Curncy";
                    foreach (var (day, value) in chosen[m])
                    {
                        // a day is attributable only when the records BRACKETING it agree — a
                        // re-point inside a record gap (a run skipped over a print day) means
                        // nothing documents which YEAR this close belonged to, and keying it on
                        // the stale at-or-before record filed post-roll prices onto the month
                        // that had just fixed (audit 2026-08-31, scenarios 151/154). The record
                        // day itself is always attributable: it was stamped alongside the mark.
                        var (matB, dateB, matA) = store.MaturityBrackets(tk, day);
                        if (matB is not { } mat) continue;
                        if (day != dateB && matA is { } ma && ma != mat) continue;
                        var refMonth = RefMonth(mat, m);
                        if (refMonth is null) continue;
                        var fix = $"{refMonth.Value.Year:0000}-{refMonth.Value.Month:00}";
                        if (!byFix.TryGetValue(fix, out var l)) byFix[fix] = l = new List<HistPoint>();
                        l.Add(new HistPoint(day, value));
                    }
                }
                foreach (var (fix, pts) in byFix)
                    wrote += store.UpsertFixings(fam.Key, fix, pts, "bbg");
            }
            log?.Invoke($"infl: bbg maintain wrote {wrote} rows (maturity-documented days only" +
                        (bars != null ? $"; {snapped} day(s) taken from London snaps, not closes)" : ")"));
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

        private sealed class MarksShape
        {
            public DateTime AsOf { get; set; }
            public Dictionary<string, List<Mark>> Marks { get; set; } = new();
            public Dictionary<string, DateTime> NextPrints { get; set; } = new();
        }

        public const string MarksFile = "infl_marks.json";

        /// <summary>Persist the run's live marks + next prints (audit 2026-08-26): the offline
        /// paths (EXPORT XLS, CLI savedown) previously fell back to LatestMarks — LAST STORED
        /// CLOSES — and rewrote the very files the desk had just emailed with different numbers
        /// under the same name. PER-CADENCE files (prefix "daily_"/"weekly_", fresh-eyes review
        /// 2026-08-26): the two cadences no longer clobber each other's frozen marks.</summary>
        public static void PersistMarks(string outDir, DateTime asOf, string prefix = "")
        {
            try
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(System.IO.Path.Combine(outDir, prefix + MarksFile),
                    System.Text.Json.JsonSerializer.Serialize(new MarksShape
                    {
                        AsOf = asOf,
                        Marks = LastLiveMarks ?? new(),
                        NextPrints = LastNextPrints ?? new(),
                    }));
            }
            catch { /* persistence is best-effort; the live statics still serve this session */ }
        }

        /// <summary>Reload persisted marks into the statics when this session has none —
        /// the offline rebuild's first stop before any LatestMarks fallback. When
        /// <paramref name="expectAsOf"/> is given, marks persisted on a DIFFERENT day are
        /// refused (fresh-eyes review 2026-08-26).</summary>
        public static void LoadPersistedMarks(string outDir, DateTime? expectAsOf = null,
            string prefix = "")
        {
            if (LastLiveMarks != null) return;
            try
            {
                var p = System.IO.Path.Combine(outDir, prefix + MarksFile);
                if (!File.Exists(p)) return;
                var s = System.Text.Json.JsonSerializer.Deserialize<MarksShape>(File.ReadAllText(p));
                if (s == null) return;
                if (expectAsOf is { } ea && s.AsOf.Date != ea.Date) return;
                LastLiveMarks = s.Marks;
                LastNextPrints ??= s.NextPrints.Count > 0 ? s.NextPrints : null;
            }
            catch { /* fall through to LatestMarks */ }
        }

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
                // per-FIXING latest within 5 days of the family's newest save (audit
                // 2026-08-26): a partial maintain — one fixing filled a day later than the
                // rest — used to shrink the family to a single mark, and the drop-the-furthest
                // display rule then emptied it entirely
                var lastDay = hist.Max(x => x.Date);
                all[fam.Key] = hist.GroupBy(x => x.Fix)
                    .Select(g => g.OrderBy(x => x.Date).Last())
                    .Where(x => (lastDay - x.Date).TotalDays <= 5)
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
            // a REAL Bloomberg print always beats an adopted sheet base for the same month
            // (audit 2026-08-26: adoption stamps month-END, so a later genuine print stamped
            // earlier in the month used to lose the last-write-wins scan permanently)
            var d = new Dictionary<(int, int), double>();
            var src = new Dictionary<(int, int), string>();
            foreach (var p in store.GetDailyWithSource(fam.IndexTicker, 2600))
            {
                var k = (p.Date.Month, p.Date.Year);
                if (d.ContainsKey(k) && src[k] == "bbg" && p.Source != "bbg") continue;
                d[k] = p.Value;
                src[k] = p.Source;
            }
            return d;
        }

        /// <summary>ONE definition of Base/Mid/YoY/MoM per fixing row — consumed by
        /// BuildDisplayRows AND the save-down book's History pages (audit 2026-08-26: the
        /// latter carried a hand-cloned copy of this arithmetic).</summary>
        public static (double? BaseV, double? Mid, double? Yoy, double? Mom) DeriveRow(
            Fam fam, Dictionary<(int M, int Y), double> prints, DateTime fixMonth, double value,
            double? prevMid)
        {
            double? baseV = prints.TryGetValue((fixMonth.Month, fixMonth.Year - 1), out var b) ? b : null;
            double? mid, yoy;
            if (fam.IsIndexUnit) { mid = value; yoy = baseV is { } b2 ? (value / b2 - 1) * 100.0 : null; }
            else { yoy = value / 100.0; mid = baseV is { } b3 ? b3 * (1 + value / 10000.0) : null; }
            double? anchor = prevMid ?? (prints.TryGetValue(
                (fixMonth.AddMonths(-1).Month, fixMonth.AddMonths(-1).Year), out var pa) ? pa : null);
            double? mom = mid is { } m0 && anchor is { } a0 ? (m0 / a0 - 1) * 100.0 : null;
            return (baseV, mid, yoy, mom);
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
                var (baseV, mid, yoy, mom) = DeriveRow(fam, prints, mk.RefMonth, mk.Value, prevMid);
                double? d1 = null, w1 = null, m1 = null;
                if (mid is { } midNow && hist.TryGetValue($"{mk.RefMonth:yyyy-MM}", out var series))
                {
                    // THE INCUMBENT SHEET'S OWN ANCHORS (read from its Table helpers,
                    // 2026-08-25 after the app's monthly diverged badly): previous business
                    // day / −7 calendar days / −28 CALENDAR DAYS (not same-day-last-month —
                    // that is the OIS sheet's convention, not this one), each matched to the
                    // EXACT saved date and blank when that date has no save. 7 and 28
                    // preserve the weekday, so exact matching is safe by construction.
                    double? MidOn(DateTime day)
                    {
                        foreach (var p in series)
                            if (p.Date.Date == day.Date)
                                return fam.IsIndexUnit
                                    ? p.Value
                                    : baseV is { } b4 ? b4 * (1 + p.Value / 10000.0) : null;
                        return null;
                    }
                    d1 = midNow - MidOn(PrevBd(asOf.Date));
                    w1 = midNow - MidOn(asOf.Date.AddDays(-7));
                    m1 = midNow - MidOn(asOf.Date.AddDays(-28));
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

        /// <summary>12bp, in the family's own unit via the row's base index — the cut is the same
        /// in every family whether it quotes YoY bp or an index level.</summary>
        public const double LoneMoverBp = 12.0;

        /// <summary>Prefix for the inflation mark-quality notes: informational grammar, beside
        /// STALE — surfaced in the log and the non-blocking popup, never the CHECK gate.</summary>
        public const string InfoPrefix = "FIXING";

        /// <summary>A TENOR THAT MOVED ALONE, SAID OUT LOUD ON EVERY RUN (desk 2026-08-31).
        ///
        /// This test was specified on 2026-08-28 and left on paper, and both times a bad mark
        /// reached the desk it was the desk that spotted it, not the app — Mar-27 published
        /// +0.99 index points beside neighbours at +0.15, and Nov-26 +0.21 beside a strip that
        /// moved 0.00. Maintain now arbitrates between the close and the snap, so most of these
        /// resolve before they are published; this is the backstop that says when arbitration
        /// did NOT help, because "nobody flagged it" must never again be the control.
        ///
        /// A monthly fixing is quoted on its own and trades thinly, so its own history cannot
        /// judge it — these tenors swing 10-18bp day to day and a Hampel filter over three
        /// months flags two points and misses the ones that matter. The STRIP can judge it. A
        /// real repricing carries the whole curve (the 25-Aug Ofgem cap reset moved all twelve
        /// months, worst single-tenor disagreement 4.8bp); a bad mark moves one month alone
        /// (18-105bp). Three months of closes put nothing between 5 and 18, so 12 sits in an
        /// empty gap rather than on a judgement call.
        ///
        /// Neighbours, not the strip median: the change profile has a real term structure and
        /// measuring against the middle of the curve would condemn its ends. Four nearest months
        /// either side, median taken, so one bad tenor cannot drag the next one over the line.
        ///
        /// Δ1d only — that is the horizon the 12bp gap was measured on. A week's or a month's
        /// residuals are naturally wider and would need their own calibration; guessing a number
        /// for them would be the same mistake in a new place.
        ///
        /// NON-BLOCKING and never suppressed: the number still publishes, the desk is just told
        /// which one to look at.</summary>
        public static List<string> CoherenceNotes(HistoryStore store, DateTime asOf,
            Dictionary<string, List<Mark>>? marks = null)
        {
            var notes = new List<string>();
            marks ??= LatestMarks(store);
            foreach (var fam in Families)
            {
                var rows = BuildDisplayRows(store, fam,
                    marks.TryGetValue(fam.Key, out var m) ? m : new List<Mark>(), asOf);
                var v = rows.Select(r => r.D1).ToList();
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].D1 is not { } x || rows[i].BaseV is not { } bse) continue;
                    var near = new List<double>();
                    foreach (var j in new[] { i - 2, i - 1, i + 1, i + 2 })
                        if (j >= 0 && j < rows.Count && v[j] is { } nv) near.Add(nv);
                    if (near.Count < 2) continue;          // too few neighbours to hold a vote
                    near.Sort();
                    double med = near.Count % 2 == 1
                        ? near[near.Count / 2]
                        : (near[near.Count / 2 - 1] + near[near.Count / 2]) / 2.0;
                    double tol = LoneMoverBp / 10000.0 * bse;
                    if (Math.Abs(x - med) <= tol) continue;
                    // FIXING:, not CHECK: — this note is NON-BLOCKING by its own spec (above),
                    // but the CHECK prefix routed it into the blocking modal, so one bad RPI
                    // tenor cancelled the entire daily OIS product (audit 2026-08-31, sc. 146).
                    // It surfaces in the log and the informational popup, never the gate.
                    notes.Add($"{InfoPrefix}: {fam.Key} {rows[i].RefMonth:MMM-yy} " +
                              $"Δ1d {x:+0.00;-0.00;0.00} against {med:+0.00;-0.00;0.00} either side " +
                              $"of it — that tenor moved ALONE, which is a bad mark far more often " +
                              $"than it is a market. Check it before this goes out.");
                }
            }
            return notes;
        }

        /// <summary>UNQUOTED FIXINGS (desk 2026-08-26, probed): the front months of a fixing strip
        /// are thinly quoted — BPSWIF8/9 sat unchanged for days while the rest of the RPI curve
        /// moved 2-6bp, and BPSWIF8 skipped a day entirely. Their change columns then print 0.00,
        /// which READS as "the market did not move" when the truth is "nobody quoted it". Flagged
        /// the same way as a stale OIS feed: named, non-blocking, never suppressed — the numbers
        /// still publish, the desk just knows which ones are asleep.</summary>
        public static List<string> StaleNotes(HistoryStore store)
        {
            var notes = new List<string>();
            foreach (var fam in Families)
            {
                var hist = store.GetFixingHistory(fam.Key);
                if (hist.Count == 0) continue;
                var days = hist.Select(x => x.Date).Distinct().OrderByDescending(d => d).Take(4).ToList();
                if (days.Count < 3) continue;
                var byFix = hist.GroupBy(x => x.Fix)
                    .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Date, x => x.Value));
                var live = byFix.Where(kv => kv.Value.ContainsKey(days[0])).ToList();
                if (live.Count < 4) continue;
                // did the family move at all on the latest save? (else it is a quiet day, not staleness)
                int moved = live.Count(kv => kv.Value.TryGetValue(days[1], out var prev)
                                             && Math.Abs(kv.Value[days[0]] - prev) > 1e-9);
                if (moved < live.Count / 2) continue;
                var stale = new List<string>();
                foreach (var (fix, series) in live.OrderBy(kv => kv.Key))
                {
                    // unchanged across the last three saves = asleep while its neighbours traded
                    bool flat = days.Take(3).All(d => series.ContainsKey(d))
                                && Math.Abs(series[days[0]] - series[days[1]]) < 1e-9
                                && Math.Abs(series[days[1]] - series[days[2]]) < 1e-9;
                    if (flat) stale.Add(DateTime.ParseExact(fix + "-01", "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture)
                        .ToString("MMM-yy", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (stale.Count > 0)
                    notes.Add($"STALE: {fam.Key} {string.Join(", ", stale)} unquoted — unchanged " +
                              $"across the last 3 saves while {moved}/{live.Count} of the strip moved; " +
                              "their change columns read 0.00 because nobody quoted them");
            }
            return notes;
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
