using System.Text.Json;
using RateDesk.Bloomberg;
using RateDesk.Core;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core.Daily
{
    /// <summary>The daily OIS run, end to end (desk 2026-08-20): live snapshot → meetings-only
    /// report (front table + 9 CB runs, futures-guarded) → three deliverables in out\:
    ///   1. daily_blast.txt — the Bloomberg-chat paste (DailyBlast; COPY BLAST serves it),
    ///   2. OIS_Runs_{date}.xlsx — today's runs + roll-corrected history (DailyBook), also
    ///      copied to publish.json "dailyDir" (the Y: drive) when configured,
    ///   3. daily_email.html/.txt — the daily email fragment (the weekly's own CB front table
    ///      + meeting cards rendering, no forward grid), with the workbook as the attachment.
    /// The incumbent Central Bank OIS MAIN.xlsm stays untouched as the manual break-glass
    /// fallback — this builder never writes into a live macro workbook.</summary>
    public static class DailyBuilder
    {
        public const string BlastFile = "daily_blast.txt";
        public const string BlastHtmlFile = "daily_blast.html";
        public const string ReportFile = "daily_report.json";
        public const string FragmentFile = "daily_email.html";
        public const string PlainTextFile = "daily_email.txt";
        public const string PreviewFile = "daily_email_preview.html";

        public sealed record Output(string BlastPath, string BookPath, string FragmentPath, string? DailyDirCopy);

        /// <summary>publish.json {"dailyDir": "Y:\\..."} — where the workbook is ALSO copied.
        /// Null (absent/blank) = no copy, honestly logged; never guessed.</summary>
        public static string? LoadDailyDir(string appDataDir) => LoadString(appDataDir, "dailyDir");

        /// <summary>publish.json {"fallbackBook": "...xlsm"} — the manual-override workbook whose
        /// Historical_* tabs are ingested for outage days (FallbackIngest). Null = no ingest.</summary>
        public static string? LoadFallbackBook(string appDataDir) => LoadString(appDataDir, "fallbackBook");

        /// <summary>publish.json {"historyDays": 61} — the save-down books' history_ window in
        /// BUSINESS days. Default 61 = the incumbent's own depth; the key was previously read
        /// and dropped (audit 2026-08-26) — it now actually reaches the books.</summary>
        public static int LoadHistoryDays(string appDataDir)
        {
            const int dflt = 61;
            var path = Path.Combine(appDataDir, "publish.json");
            if (!File.Exists(path)) return dflt;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty("historyDays", out var v)
                       && v.ValueKind == JsonValueKind.Number && v.GetInt32() is > 0 and <= 2000
                    ? v.GetInt32() : dflt;
            }
            catch { return dflt; }
        }

        /// <summary>Mirror every local OIS_Runs workbook the shared drive is missing (or holds
        /// an older copy of) — not just today's. A remote drive that was down for a week catches
        /// up in one pass the moment it is back; until then everything lives locally in out\ and
        /// the store, and NOTHING is lost (desk 2026-08-21). Returns true when the drive was
        /// reachable and today's workbook is mirrored.</summary>
        public static bool SyncDailyDir(string outDir, string appDataDir, Action<string>? log = null)
        {
            var dailyDir = LoadDailyDir(appDataDir);
            if (dailyDir == null)
            {
                log?.Invoke("daily: no dailyDir in publish.json — shared-drive mirror off");
                return false;
            }
            var local = Directory.Exists(outDir)
                ? Directory.GetFiles(outDir, "DRAX OIS Runs *.xlsx")
                    .Concat(Directory.GetFiles(outDir, "DRAX Fixing Runs *.xlsx")).ToArray()
                : Array.Empty<string>();
            try
            {
                Directory.CreateDirectory(dailyDir);
                int copied = 0;
                foreach (var f in local)
                {
                    var target = Path.Combine(dailyDir, Path.GetFileName(f));
                    if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(f))
                        continue;
                    File.Copy(f, target, overwrite: true);
                    copied++;
                }
                log?.Invoke(copied > 0
                    ? $"daily: mirrored {copied} workbook(s) to {dailyDir}" +
                      (copied > 1 ? " (caught up days the drive was unreachable)" : "")
                    : "daily: shared drive already up to date");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"! daily: shared drive unreachable ({ex.Message}) — {local.Length} local " +
                            "workbook(s) held in out\\; they mirror automatically when it returns " +
                            "(or via EXPORT XLS). The store and local files lose nothing.");
                return false;
            }
        }

        /// <summary>Rebuild the OIS workbook OFFLINE — from the last run's frozen report and the
        /// store alone: no Bloomberg, no shared drive required (desk 2026-08-21, the unified-
        /// information-store failsafe). Manual fallback days are ingested first so an export made
        /// during an outage still carries them. Returns the local path; also mirrors to the
        /// shared drive when reachable.</summary>
        public static string ExportBook(HistoryStore store, string outDir, string appDataDir,
            Action<string>? log = null)
        {
            var rep = ReportStore.Load(Path.Combine(outDir, ReportFile))
                ?? throw new InvalidOperationException(
                    "no stored daily report yet — run DAILY RUN once (with a terminal) first");
            if (LoadFallbackBook(appDataDir) is { } fb)
                FallbackIngest.Run(fb, store, log);
            log?.Invoke($"export: rebuilding workbooks from stored data as of " +
                        $"{rep.AsOf:dd-MMM-yy HH:mm} — no Bloomberg required");
            // the RUN's frozen marks, never a stale-close downgrade of the emailed file
            Infl.InflHistory.LoadPersistedMarks(outDir);
            var marks = Infl.InflHistory.LastLiveMarks;
            var path = DailyBook.Write(rep, outDir, log);
            Infl.InflRunsXlsx.Write(store, outDir, rep.AsOf, marks, Infl.InflHistory.LastNextPrints, log);
            try
            {
                SaveDown.StoreBooks.WriteOis(rep, store, outDir, log, LoadHistoryDays(appDataDir));
                SaveDown.StoreBooks.WriteInfl(store, outDir, rep.AsOf, marks, log);
                if (SaveDown.SaveDownConfig.Load(appDataDir) is { } sd)
                {
                    SaveDown.SaveDownConfig.Sync(outDir, "OIS_Runs_*.xlsm",
                        Path.Combine(sd.Root, SaveDown.SaveDownConfig.OisFolder), log);
                    SaveDown.SaveDownConfig.Sync(outDir, "Inflation_Runs_*.xlsm",
                        Path.Combine(sd.Root, SaveDown.SaveDownConfig.InflFolder), log);
                }
            }
            catch (Exception ex) { log?.Invoke("! export: save-down books failed: " + ex.Message); }
            SyncDailyDir(outDir, appDataDir, log);
            return path;
        }

        /// <summary>Public access to a publish.json string (e.g. "inflBook" — the external
        /// inflation workbook the unified fixings history validates and ingests).</summary>
        public static string? LoadPublishString(string appDataDir, string key) => LoadString(appDataDir, key);

        /// <summary>Ingest manual rows from EVERY recent save-down workbook in a folder (audit
        /// 2026-08-26: newest-only lost the middle days of a multi-day outage — the desk stores
        /// into a dated book each day, and only the last one was ever read). Bounded to files
        /// dated within the trailing 45 days; each file's ingest is restricted to rows on/after
        /// its own date, so re-reading old books is idempotent and cheap.</summary>
        private static void IngestNewestSaved(string dir, string pattern,
            Action<string, DateTime?> ingest, Action<string>? log)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                var dated = new List<(string Path, DateTime Day)>();
                foreach (var f in Directory.GetFiles(dir, pattern))
                {
                    // OIS_Runs_25August26.xlsm / Inflation_Runs_25August26.xlsm → 25August26
                    var stem = Path.GetFileNameWithoutExtension(f);
                    var tag = stem[(stem.LastIndexOf('_') + 1)..];
                    if (DateTime.TryParseExact(tag, "dMMMMyy",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var fd))
                    {
                        if (fd >= DateTime.Today.AddDays(-45)) dated.Add((f, fd));
                    }
                    else log?.Invoke($"! daily: cannot date {Path.GetFileName(f)} — ingest skipped");
                }
                foreach (var (p, fd) in dated.OrderBy(x => x.Day))
                    ingest(p, fd);
            }
            catch (Exception ex) { log?.Invoke($"! daily: saved-book ingest ({dir}): {ex.Message}"); }
        }

        private static string? LoadString(string appDataDir, string key)
        {
            var path = Path.Combine(appDataDir, "publish.json");
            if (!File.Exists(path)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty(key, out var v)
                       && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString() : null;
            }
            catch { return null; }
        }

        /// <summary>Snapshot live and build the meetings-only report (same universe and guard
        /// pass as the weekly email — one definition of the boards, two cadences). When a store
        /// is passed, the meeting-ticker closes the run already fetched are UPSERTED into it, so
        /// the workbook's history sheets stay current on daily cadence alone — a desk that skips
        /// WEEKLY RUN for weeks still gets full history sheets, at zero extra Bloomberg cost
        /// (the stitcher prefetched these series anyway; desk question 2026-08-20).</summary>
        public static WeeklyReport Build(HistoryStore? store = null, Action<string>? log = null,
            string? appDataDir = null)
        {
            var configs = RateDesk.Core.Config.ConfigStore.LoadDefault();
            var snap = new RatesSnapshot();
            var svc = new PricingService(configs, snap);
            // saved per-run contributor overrides (source-selection trial, desk 2026-08-26) —
            // BEFORE ticker collection so the chosen contributor's spellings get snapshotted
            SourceStore.Apply(svc, appDataDir, log);
            using var refData = new RefDataClient();
            // STORE-FIRST HISTORY (desk 2026-08-25): lookbacks read the maintained store and
            // Bloomberg is only touched to gap-fill stale tickers — same marks every run,
            // minimal API load. The live snapshot below still runs in full (today's marks
            // cannot come from history).
            var sbh = store != null ? new StoreBackedHistory(store, refData, log) : null;
            svc.History = (IHistoryProvider?)sbh ?? refData;
            var all = EmailBuilder.AllTickers(configs, svc);
            // inflation fixing swaps ride along (desk 2026-08-25): their maturities identify
            // which reference month each ticker means today — the unified fixings history's key
            foreach (var fam in Infl.InflHistory.Families)
                for (int n = 1; n <= 12; n++) all.Add($"{fam.Root}{n} Curncy");
            log?.Invoke($"daily: snapshotting {all.Count} tickers...");
            refData.Snapshot(all, snap);
            // CLOSE DISCIPLINE (desk 2026-08-25): 15:30-16:15 London = live mids save as the
            // close; from 16:15 the published marks are pinned to the 16:15 snap; earlier runs
            // are flagged PRE-CLOSE. Applied to the tickers the daily products publish.
            var snapSet = svc.MeetingTickers().Concat(PricingService.WeeklyExtraTickers)
                .Concat(Infl.InflHistory.Families.SelectMany(f =>
                    Enumerable.Range(1, 12).Select(n => $"{f.Root}{n} Curncy"))).ToList();
            var (_, snapNote) = SnapDiscipline.Apply(refData, snap, snapSet, log);
            if (sbh == null)
                try { refData.Prefetch(all, 220); } catch { /* singles fallback inside Core */ }
            var rep = svc.BuildWeekly(meetingsOnly: true);
            if (snapNote != null) rep.Notes.Add(snapNote);
            if (sbh != null) log?.Invoke("daily " + sbh.Stats);
            // active source + compounded fixing onto every run (trial, desk 2026-08-26)
            CompoundedFixing.Stamp(rep, svc, configs, log);
            rep.Notes.AddRange(FuturesGuard.Check(svc));
            // cross-sectional outlier flag (desk 2026-08-25, the BOJ +4.9-in-a-strip-of-+11
            // question): one row far off its run's median gets a CHECK note for a manual look
            // before distribution — flagged, never suppressed
            rep.Notes.AddRange(OutlierGuard.Check(rep));
            foreach (var n in rep.Notes) log?.Invoke("  daily note: " + n);

            if (store != null)
            {
                int wrote = 0;
                foreach (var sched in MeetingsStore.Schedules.Where(s => string.IsNullOrEmpty(s.Kind)))
                {
                    var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                    if (pat == null) continue;
                    // the run's ACTIVE source (override-aware) — history must follow the mids
                    var activeSrc = svc.MeetingSrc(sched);
                    var srcSfx = activeSrc.Length == 0 ? "" : " " + activeSrc;
                    for (int n = 1; n <= 13; n++)
                    {
                        // BOTH spellings stay current on daily cadence: the contributor series
                        // (the stitcher/history sheets' first read — desk sources 2026-08-25)
                        // and the composite fallback. Store-first: fresh tickers cost nothing.
                        try
                        {
                            if (sbh!.GetDaily(pat.Replace("{N}", n.ToString()) + srcSfx + " Curncy", 70).Count > 0) wrote++;
                            if (srcSfx.Length > 0)
                                sbh!.GetDaily(pat.Replace("{N}", n.ToString()) + " Curncy", 70);
                        }
                        catch { /* a dead far rung is not an error */ }
                    }
                }
                log?.Invoke($"daily: upserted {wrote} meeting series into the store (history sheets self-sufficient)");

                // unified inflation-fixings upkeep on daily cadence alone (same self-sufficiency
                // rule as the meeting closes): record today's maturities, top up the raw closes,
                // fold both into the fixing-identity history
                int fx = 0;
                foreach (var fam in Infl.InflHistory.Families)
                    for (int n = 1; n <= 12; n++)
                    {
                        var tk = $"{fam.Root}{n} Curncy";
                        try
                        {
                            if (snap.Get(tk)?.Maturity is { } mat)
                                store.SetMaturity(tk, DateTime.Today, mat);
                            if (sbh!.GetDaily(tk, 45).Count > 0) fx++;
                        }
                        catch { /* an unquoted fixing month is not an error */ }
                    }
                log?.Invoke($"daily: topped up {fx} inflation fixing series");
                try { Infl.InflHistory.Maintain(store, log); }
                catch (Exception ex) { log?.Invoke("  ! infl maintain: " + ex.Message); }
                // capture the live fixing marks while the snapshot is in hand — the email
                // section, lean xlsx and save-down book all publish these
                try { Infl.InflHistory.LastLiveMarks = Infl.InflHistory.CollectLiveMarks(snap, store); }
                catch { /* absent quotes just mean fewer rows */ }
                // and the next scheduled prints (ECO_RELEASE_DT) — the "Next Print:" lines
                try
                {
                    var rel = refData.GetNextReleaseDates(
                        Infl.InflHistory.Families.Select(f => f.IndexTicker));
                    Infl.InflHistory.LastNextPrints = Infl.InflHistory.Families
                        .Where(f => rel.ContainsKey(f.IndexTicker))
                        .ToDictionary(f => f.Key, f => rel[f.IndexTicker]);
                }
                catch { /* omitted, never guessed */ }
            }
            return rep;
        }

        /// <summary>Write all three deliverables (blast, workbook, email trio) and copy the
        /// workbook to the configured daily dir. The fragment on disk IS what CREATE DAILY
        /// EMAIL sends — same persistence discipline as the weekly.</summary>
        public static Output Render(WeeklyReport rep, HistoryStore store, string outDir,
            string appDataDir, Action<string>? log = null)
        {
            Directory.CreateDirectory(outDir);

            // FAILSAFE ROUND-TRIP (desk 2026-08-20): pull any outage days the desk stored
            // manually in the fallback workbook into the store BEFORE the history sheets render,
            // so history is continuous across an app/API outage and manual rows appear marked.
            if (LoadFallbackBook(appDataDir) is { } fb)
                FallbackIngest.Run(fb, store, log);
            else
                log?.Invoke("daily: no fallbackBook in publish.json — manual-override ingest off");

            ReportStore.Save(rep, Path.Combine(outDir, ReportFile));
            // freeze the run's live inflation marks + next prints next to the report — offline
            // rebuilds re-serve THESE, never a stale-close downgrade under the same filename
            Infl.InflHistory.PersistMarks(outDir, rep.AsOf);

            var blastPath = Path.Combine(outDir, BlastFile);
            File.WriteAllText(blastPath, DailyBlast.Render(rep));
            File.WriteAllText(Path.Combine(outDir, BlastHtmlFile), DailyBlast.Html(rep));
            log?.Invoke($"daily: wrote {BlastFile} (+ table flavour for the chat paste)");

            var bookPath = DailyBook.Write(rep, outDir, log);
            log?.Invoke($"daily: wrote {Path.GetFileName(bookPath)} (runs only, " +
                        $"{new FileInfo(bookPath).Length / 1024.0:F0} KB)");
            // the second attachment: the lean inflation runs workbook, same discipline
            Infl.InflRunsXlsx.Write(store, outDir, rep.AsOf,
                Infl.InflHistory.LastLiveMarks, Infl.InflHistory.LastNextPrints, log);

            var dailyCopy = SyncDailyDir(outDir, appDataDir, log)
                ? Path.Combine(LoadDailyDir(appDataDir)!, Path.GetFileName(bookPath)) : null;

            // MACRO-ENABLED SAVE-DOWN BOOKS (desk 2026-08-25): the daily .xlsm files carrying
            // the incumbent store machinery, written locally then mirrored (with catch-up) into
            // the configured "OIS Runs" / "Inflation Runs" folders. Before writing, manual rows
            // the desk stored into PREVIOUS saved books are re-ingested — the failsafe loop.
            try
            {
                var sdCfg = SaveDown.SaveDownConfig.Load(appDataDir);
                if (sdCfg != null)
                {
                    // only rows the DESK stored (dated on/after the file's own date) — the
                    // app-written history rows are roll-corrected walk-back values and must
                    // never re-enter raw ticker history
                    IngestNewestSaved(Path.Combine(sdCfg.Root, SaveDown.SaveDownConfig.OisFolder),
                        "OIS_Runs_*.xlsm",
                        (p, fd) => FallbackIngest.Run(p, store, log, minDate: fd), log);
                    IngestNewestSaved(Path.Combine(sdCfg.Root, SaveDown.SaveDownConfig.InflFolder),
                        "Inflation_Runs_*.xlsm",
                        (p, fd) => Infl.InflHistory.Ingest(p, store, log,
                            onlyMissingOrChanged: true, minDate: fd), log);
                }
                SaveDown.StoreBooks.WriteOis(rep, store, outDir, log, LoadHistoryDays(appDataDir));
                SaveDown.StoreBooks.WriteInfl(store, outDir, rep.AsOf, Infl.InflHistory.LastLiveMarks, log);
                if (sdCfg != null)
                {
                    SaveDown.SaveDownConfig.Sync(outDir, "OIS_Runs_*.xlsm",
                        Path.Combine(sdCfg.Root, SaveDown.SaveDownConfig.OisFolder), log);
                    SaveDown.SaveDownConfig.Sync(outDir, "Inflation_Runs_*.xlsm",
                        Path.Combine(sdCfg.Root, SaveDown.SaveDownConfig.InflFolder), log);
                }
                else log?.Invoke("daily: no save-down destination configured yet — books held in out\\");
            }
            catch (Exception ex) { log?.Invoke("! daily: save-down books failed: " + ex.Message); }

            // the inflation section fragment — frozen at run time, appended at click time
            // under the tickboxes (and into the default fragment below)
            string inflHtml = "", inflText = "";
            try
            {
                inflHtml = Infl.InflEmail.WriteFragments(store, Infl.InflHistory.LastLiveMarks,
                    Infl.InflHistory.LastNextPrints, rep.AsOf, outDir, daily: true);
                inflText = File.ReadAllText(Path.Combine(outDir, Infl.InflEmail.DailyTextFile));
            }
            catch (Exception ex) { log?.Invoke("! daily: inflation email section failed: " + ex.Message); }

            var frag = Path.Combine(outDir, FragmentFile);
            File.WriteAllText(frag, WeeklyEmail.Html(rep) + inflHtml);
            File.WriteAllText(Path.Combine(outDir, PlainTextFile), WeeklyEmail.PlainText(rep) + inflText);
            File.WriteAllText(Path.Combine(outDir, PreviewFile),
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><title>Daily OIS preview</title></head>" +
                "<body style=\"margin:14px;background:#ffffff;\">" + File.ReadAllText(frag) + "</body></html>");
            log?.Invoke($"daily: wrote {FragmentFile} / {PlainTextFile} / {PreviewFile}");

            return new Output(blastPath, bookPath, frag, dailyCopy);
        }
    }
}
