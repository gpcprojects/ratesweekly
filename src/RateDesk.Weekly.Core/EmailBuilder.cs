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
    /// The MOVERS strip (DESIGN §4's top strip) is read from movers.json in the out directory —
    /// written by the same render pass that draws index.html — and is skipped when the file is
    /// missing or stale, so the email can never tease movers it cannot show.</summary>
    public static class EmailBuilder
    {
        public sealed record Output(string FragmentPath, string PlainTextPath, string PreviewPath);

        public const string FragmentFile = "email.html";        // the exact clipboard fragment
        public const string PlainTextFile = "email.txt";
        public const string PreviewFile = "email_preview.html"; // browser-openable wrapper

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
        /// engine's is already disposed by the time this runs, and the CLI has none.</summary>
        public static WeeklyReport Build(Action<string>? log = null)
        {
            var configs = ConfigStore.LoadDefault();
            var snap = new RatesSnapshot();
            var svc = new PricingService(configs, snap);
            using var refData = new RefDataClient();
            svc.History = refData;
            var all = AllTickers(configs, svc);
            log?.Invoke($"email: snapshotting {all.Count} tickers...");
            refData.Snapshot(all, snap);
            try { refData.Prefetch(all, 220); } catch { /* singles fallback inside Core */ }
            var rep = svc.BuildWeekly();
            foreach (var n in rep.Notes) log?.Invoke("  email note: " + n);
            return rep;
        }

        /// <summary>Write the fragment/plaintext/preview trio. The fragment on disk IS what the
        /// clipboard carries — COPY EMAIL must never rebuild or restyle.</summary>
        public static Output Render(WeeklyReport rep, string outDir, string? siteBase, Action<string>? log = null)
        {
            Directory.CreateDirectory(outDir);
            Func<string, string?>? href = siteBase == null
                ? null
                : ccy => $"{siteBase}/{ccy.ToLowerInvariant()}.html";

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string stamp = rep.AsOf.ToString("dd-MMM-yy HH:mm", inv);
            string footerHtml =
                $"<div style=\"{WeeklyEmail.EmFont}font-size:10px;color:{WeeklyEmail.EmMut};margin:2px 0 0 2px;\">" +
                (siteBase != null
                    ? $"dashboards updated {stamp} · <a href=\"{siteBase}/\" style=\"color:{WeeklyEmail.EmMut};\">{siteBase}/</a> · "
                    : $"dashboards updated {stamp} · ") +
                "source: Bloomberg</div>";
            string footerText = siteBase != null
                ? $"dashboards updated {stamp} · {siteBase}/ · source: Bloomberg"
                : $"dashboards updated {stamp} · source: Bloomberg";

            // movers strip: title links to the hub when the site base is known, else plain bold —
            // the teaser is still worth reading before the dashboards are hosted anywhere
            string? moversHtml = null, moversText = null;
            try
            {
                var mj = Path.Combine(outDir, "movers.json");
                if (File.Exists(mj))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(mj));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("asOf", out var a)
                        && DateTime.TryParseExact(a.GetString(), "yyyy-MM-dd", inv,
                            System.Globalization.DateTimeStyles.None, out var mvAsOf)
                        && (rep.AsOf.Date - mvAsOf.Date).TotalDays is >= 0 and <= 7
                        && root.TryGetProperty("headline", out var hl)
                        && hl.GetString() is { Length: > 0 } head)
                    {
                        string title = siteBase != null
                            ? $"<a href=\"{siteBase}/index.html\" style=\"color:{WeeklyEmail.EmAccent};\">► MOVERS SUMMARY</a>"
                            : $"<span style=\"color:{WeeklyEmail.EmAccent};\">► MOVERS SUMMARY</span>";
                        moversHtml =
                            $"<div style=\"{WeeklyEmail.EmFont}font-size:12px;padding:7px 10px;margin:0 0 12px 0;" +
                            $"background:{WeeklyEmail.EmHead};border:1px solid {WeeklyEmail.EmLine};\">" +
                            $"<b>{title}</b> &nbsp;{System.Net.WebUtility.HtmlEncode(head)}</div>";
                        moversText = "MOVERS SUMMARY" + (siteBase != null ? $" ({siteBase}/index.html)" : "")
                            + ": " + head;
                    }
                }
            }
            catch { /* the teaser is best-effort; the email must never fail on it */ }

            var frag = Path.Combine(outDir, FragmentFile);
            var txt = Path.Combine(outDir, PlainTextFile);
            var prev = Path.Combine(outDir, PreviewFile);
            File.WriteAllText(frag, WeeklyEmail.Html(rep, href, footerHtml, moversHtml));
            File.WriteAllText(txt, WeeklyEmail.PlainText(rep, footerText, moversText));
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
