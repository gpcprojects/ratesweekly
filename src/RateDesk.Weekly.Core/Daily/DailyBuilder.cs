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

        /// <summary>publish.json {"historyDays": 250} — the workbook history sheets' window.</summary>
        public static int LoadHistoryDays(string appDataDir)
        {
            var path = Path.Combine(appDataDir, "publish.json");
            if (!File.Exists(path)) return DailyBook.HistoryDays;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty("historyDays", out var v)
                       && v.ValueKind == JsonValueKind.Number && v.GetInt32() is > 0 and <= 2000
                    ? v.GetInt32() : DailyBook.HistoryDays;
            }
            catch { return DailyBook.HistoryDays; }
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
                ? Directory.GetFiles(outDir, "OIS_Runs_*.xlsx") : Array.Empty<string>();
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
            log?.Invoke($"export: rebuilding workbook from stored data as of " +
                        $"{rep.AsOf:dd-MMM-yy HH:mm} — no Bloomberg required");
            var path = DailyBook.Write(rep, store, outDir, log, LoadHistoryDays(appDataDir));
            SyncDailyDir(outDir, appDataDir, log);
            return path;
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
        public static WeeklyReport Build(HistoryStore? store = null, Action<string>? log = null)
        {
            var configs = RateDesk.Core.Config.ConfigStore.LoadDefault();
            var snap = new RatesSnapshot();
            var svc = new PricingService(configs, snap);
            using var refData = new RefDataClient();
            svc.History = refData;
            var all = EmailBuilder.AllTickers(configs, svc);
            log?.Invoke($"daily: snapshotting {all.Count} tickers...");
            refData.Snapshot(all, snap);
            try { refData.Prefetch(all, 220); } catch { /* singles fallback inside Core */ }
            var rep = svc.BuildWeekly(meetingsOnly: true);
            rep.Notes.AddRange(FuturesGuard.Check(svc));
            foreach (var n in rep.Notes) log?.Invoke("  daily note: " + n);

            if (store != null)
            {
                int wrote = 0;
                foreach (var sched in MeetingsStore.Schedules.Where(s => string.IsNullOrEmpty(s.Kind)))
                {
                    var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                    if (pat == null) continue;
                    for (int n = 1; n <= 13; n++)
                    {
                        // the COMPOSITE spelling — the store key the stitcher and the workbook's
                        // history sheets read (TickerUniverse's both-spellings lesson)
                        var tkr = pat.Replace("{N}", n.ToString()) + " Curncy";
                        try
                        {
                            var h = refData.GetDaily(tkr, 70);
                            if (h.Count == 0) continue;
                            store.UpsertDaily(tkr, h, excludeToday: true);
                            wrote++;
                        }
                        catch { /* a dead far rung is not an error */ }
                    }
                }
                log?.Invoke($"daily: upserted {wrote} meeting series into the store (history sheets self-sufficient)");
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

            var blastPath = Path.Combine(outDir, BlastFile);
            File.WriteAllText(blastPath, DailyBlast.Render(rep));
            log?.Invoke($"daily: wrote {BlastFile}");

            var bookPath = DailyBook.Write(rep, store, outDir, log, LoadHistoryDays(appDataDir));
            log?.Invoke($"daily: wrote {Path.GetFileName(bookPath)} " +
                        $"({new FileInfo(bookPath).Length / 1024.0:F0} KB)");

            var dailyCopy = SyncDailyDir(outDir, appDataDir, log)
                ? Path.Combine(LoadDailyDir(appDataDir)!, Path.GetFileName(bookPath)) : null;

            var frag = Path.Combine(outDir, FragmentFile);
            File.WriteAllText(frag, WeeklyEmail.Html(rep));
            File.WriteAllText(Path.Combine(outDir, PlainTextFile), WeeklyEmail.PlainText(rep));
            File.WriteAllText(Path.Combine(outDir, PreviewFile),
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><title>Daily OIS preview</title></head>" +
                "<body style=\"margin:14px;background:#ffffff;\">" + File.ReadAllText(frag) + "</body></html>");
            log?.Invoke($"daily: wrote {FragmentFile} / {PlainTextFile} / {PreviewFile}");

            return new Output(blastPath, bookPath, frag, dailyCopy);
        }
    }
}
