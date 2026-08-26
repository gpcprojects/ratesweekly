using System.Reflection;
using System.Text.Json;
using RateDesk.Bloomberg;
using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>The desk email, end to end: live snapshot → Core's BuildWeekly → WeeklyEmail
    /// renderings written to the out directory, with each currency header hyperlinked to its
    /// dashboard (DESIGN.md §4). The report/rendering definition lives in RateDesk.Core
    /// (BuildWeekly + WeeklyEmail, consolidated here from dodgeball 2026-08-11) — this class only
    /// drives it and persists the result, so COPY EMAIL can serve the clipboard after a restart
    /// without touching Bloomberg again.
    ///
    /// Needs a running, logged-in terminal: mids are a fresh snapshot and the 1w/1m meeting
    /// columns ride 16:30-London intraday snaps, neither of which the history store carries.
    /// The movers TEASER STRIP was removed on desk instruction 2026-08-11 — the movers live in
    /// the attached dashboards file instead (single-file edition); the WeeklyEmail header hook
    /// stays for whoever wants a strip back.</summary>
    public static class EmailBuilder
    {
        public sealed record Output(string FragmentPath, string PlainTextPath, string PreviewPath);

        public const string FragmentFile = "email.html";        // the exact clipboard fragment
        public const string PlainTextFile = "email.txt";
        public const string PreviewFile = "email_preview.html"; // browser-openable wrapper
        /// <summary>The frozen report data, so the outgoing email can be COMPOSED at click time
        /// under the current tickboxes (EmailSettings) without touching Bloomberg.</summary>
        public const string ReportFile = "weekly_report.json";

        /// <summary>Site root for dashboard links, e.g. "https://…/". From
        /// %APPDATA%\RatesWeekly\publish.json {"siteBase": "…"} — the same file DESIGN.md §9
        /// earmarks for publish credentials. Null (file or key absent) = links omitted; the email
        /// must never carry a guessed URL.</summary>
        public static string? LoadSiteBase(string appDataDir)
        {
            var path = Path.Combine(appDataDir, "publish.json");
            if (!File.Exists(path)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty("siteBase", out var v) && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString()!.TrimEnd('/')
                    : null;
            }
            catch { return null; } // a malformed file means "not configured", not a crash
        }

        /// <summary>Same universe the standalone dodgeball weekly app snapshotted: every enabled
        /// currency's curve (+ the weekly's preferred contributor where it differs, so the
        /// preference can be VALIDATED), ladder pillars, meeting runs, and the direct
        /// cell-override quotes.</summary>
        public static List<string> AllTickers(ConfigStore configs, PricingService svc)
        {
            var all = new List<string>();
            foreach (var cfg in configs.Enabled)
            {
                if (cfg.Ois != null || cfg.Irs != null)
                {
                    all.AddRange(svc.TickersWithDiscount(cfg, svc.SourceFor(cfg.Ccy)));
                    var pref = svc.WeeklySource(cfg.Ccy);
                    if (!pref.Equals(svc.SourceFor(cfg.Ccy), StringComparison.OrdinalIgnoreCase))
                        all.AddRange(svc.TickersWithDiscount(cfg, pref));
                }
                foreach (var lad in cfg.Ladders)
                    all.AddRange(lad.Pillars.Where(p => p.Enabled).Select(p => ConfigStore.ResolveTicker(p.Ticker, "")));
            }
            all.AddRange(svc.MeetingTickers());
            all.AddRange(PricingService.WeeklyExtraTickers);
            return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Snapshot live and build the report. Owns its Bloomberg session — the UPDATE
        /// engine's is already disposed by the time this runs, and the CLI has none. Pass the
        /// store to serve lookbacks STORE-FIRST (desk 2026-08-25): Bloomberg is then touched
        /// only for the live snapshot, the 16:30 intraday snaps, and per-ticker gap-fills —
        /// same marks every run, minimal API load.</summary>
        public static WeeklyReport Build(Action<string>? log = null, HistoryStore? store = null,
            string? appDataDir = null)
        {
            var configs = ConfigStore.LoadDefault();
            var snap = new RatesSnapshot();
            var svc = new PricingService(configs, snap);
            // saved per-run contributor overrides (source-selection trial, desk 2026-08-26) —
            // BEFORE ticker collection so the chosen contributor's spellings get snapshotted
            SourceStore.Apply(svc, appDataDir, log);
            using var refData = new RefDataClient();
            var sbh = store != null ? new StoreBackedHistory(store, refData, log) : null;
            svc.History = (RateDesk.Core.Market.IHistoryProvider?)sbh ?? refData;
            var all = AllTickers(configs, svc);
            // inflation fixing swaps ride along (desk 2026-08-25): the weekly email carries the
            // same Inflation Fixing Runs section as the daily
            foreach (var fam in Infl.InflHistory.Families)
                for (int n = 1; n <= 12; n++) all.Add($"{fam.Root}{n} Curncy");
            log?.Invoke($"email: snapshotting {all.Count} tickers...");
            refData.Snapshot(all, snap);
            if (store != null)
            {
                try { Infl.InflHistory.LastLiveMarks = Infl.InflHistory.CollectLiveMarks(snap, store); }
                catch { /* absent quotes just mean fewer rows */ }
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
            // same close discipline as the daily (desk 2026-08-25): meeting-board marks pin to
            // the 16:15 snap when pressed after it; pre-15:30 runs carry a PRE-CLOSE flag. The
            // forward grid stays live-at-press — it is not a close product. The INFLATION
            // fixings pin too (audit 2026-08-26: the weekly's cards were live-at-press while
            // the daily's were pinned — same cards, two different marks on the same day).
            var (_, snapNote) = SnapDiscipline.Apply(refData, snap,
                svc.MeetingTickers().Concat(PricingService.WeeklyExtraTickers)
                    .Concat(Infl.InflHistory.Families.SelectMany(f =>
                        Enumerable.Range(1, 12).Select(n => $"{f.Root}{n} Curncy"))), log);
            if (store != null)
            {
                // the daily's own upkeep, mirrored (audit 2026-08-26): a desk running only
                // WEEKLY still records maturities, tops up the fixing closes and maintains the
                // unified history — otherwise its Δ columns never advance
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
                log?.Invoke($"email: topped up {fx} inflation fixing series");
                try { Infl.InflHistory.Maintain(store, log); }
                catch (Exception ex) { log?.Invoke("  ! infl maintain: " + ex.Message); }
                // re-collect the marks AFTER the snap pin so the cards publish the close
                try { Infl.InflHistory.LastLiveMarks = Infl.InflHistory.CollectLiveMarks(snap, store); }
                catch { /* absent quotes just mean fewer rows */ }
            }
            if (sbh == null)
                try { refData.Prefetch(all, 220); } catch { /* singles fallback inside Core */ }
            var rep = svc.BuildWeekly();
            if (snapNote != null) rep.Notes.Add(snapNote);
            if (sbh != null) log?.Invoke("email " + sbh.Stats);
            // active source + compounded fixing onto every run (trial, desk 2026-08-26)
            CompoundedFixing.Stamp(rep, svc, configs, log);
            // exchange-settled futures cross-check (FuturesGuard) — a TRIGGERED line here is the
            // flag that the meeting rows disagree with instruments that share nothing with the
            // OIS machinery. Notes only: the investor-facing email body never carries diagnostics.
            rep.Notes.AddRange(FuturesGuard.Check(svc));
            // cross-sectional outlier flag (desk 2026-08-25): a row far off its run's median
            // gets a CHECK note for a manual look before distribution — flagged, never hidden
            rep.Notes.AddRange(OutlierGuard.Check(rep));
            foreach (var n in rep.Notes) log?.Invoke("  email note: " + n);
            return rep;
        }

        /// <summary>Write the fragment/plaintext/preview trio. The fragment on disk IS what the
        /// clipboard carries — COPY EMAIL must never rebuild or restyle. Pass the store to also
        /// render the Inflation Fixing Runs section below the forward grid (desk 2026-08-25).</summary>
        public static Output Render(WeeklyReport rep, string outDir, string? siteBase,
            Action<string>? log = null, HistoryStore? store = null)
        {
            Directory.CreateDirectory(outDir);
            Func<string, string?>? href = siteBase == null
                ? null
                : ccy => $"{siteBase}/{ccy.ToLowerInvariant()}.html";

            // NO footer ("dashboards updated … · source: Bloomberg") — removed permanently on
            // desk instruction 2026-08-11. Do not re-add it; the WeeklyEmail hook stays unused.

            ReportStore.Save(rep, Path.Combine(outDir, ReportFile));
            if (store != null) Infl.InflHistory.PersistMarks(outDir, rep.AsOf, prefix: "weekly_");

            string inflHtml = "", inflText = "";
            if (store != null)
                try
                {
                    inflHtml = Infl.InflEmail.WriteFragments(store, Infl.InflHistory.LastLiveMarks,
                        Infl.InflHistory.LastNextPrints, rep.AsOf, outDir, daily: false);
                    inflText = File.ReadAllText(Path.Combine(outDir, Infl.InflEmail.WeeklyTextFile));
                }
                catch (Exception ex) { log?.Invoke("! email: inflation section failed: " + ex.Message); }

            var frag = Path.Combine(outDir, FragmentFile);
            var txt = Path.Combine(outDir, PlainTextFile);
            var prev = Path.Combine(outDir, PreviewFile);
            File.WriteAllText(frag, WeeklyEmail.Html(rep, href) + inflHtml);
            File.WriteAllText(txt, WeeklyEmail.PlainText(rep) + inflText);
            // full-document wrapper only for the PREVIEW; the clipboard fragment stays bare
            File.WriteAllText(prev,
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><title>RatesWeekly email preview</title></head>" +
                "<body style=\"margin:14px;background:#ffffff;\">" + File.ReadAllText(frag) + "</body></html>");
            log?.Invoke($"email: wrote {FragmentFile} / {PlainTextFile} / {PreviewFile}" +
                        (siteBase == null ? " (no siteBase in publish.json — links omitted)" : ""));
            return new Output(frag, txt, prev);
        }
    }
}
