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

            string? dailyCopy = null;
            var dailyDir = LoadDailyDir(appDataDir);
            if (dailyDir != null)
            {
                try
                {
                    Directory.CreateDirectory(dailyDir);
                    dailyCopy = Path.Combine(dailyDir, Path.GetFileName(bookPath));
                    File.Copy(bookPath, dailyCopy, overwrite: true);
                    log?.Invoke($"daily: copied workbook to {dailyDir}");
                }
                catch (Exception ex)
                {
                    dailyCopy = null;
                    log?.Invoke($"! daily: workbook copy to dailyDir failed — {ex.Message}");
                }
            }
            else
                log?.Invoke("daily: no dailyDir in publish.json — workbook not copied to the shared drive");

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
