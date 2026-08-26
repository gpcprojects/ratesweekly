using System.Globalization;
using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Series;

// RatesWeekly CLI — scriptable history maintenance, same code path as the app's UPDATE button.
// Exists so the store can be topped up (and later DEEPENED gradually/overnight) from a scheduled
// task without anyone sitting in front of the GUI.
//
//   RatesWeeklyCli update              bring the store current (seed/maintain per UpdateEngine)
//   RatesWeeklyCli status              store stats, no Bloomberg calls
//   RatesWeeklyCli render [ccy]        redraw dashboard pages from the store
//   RatesWeeklyCli email               build the desk email (live snapshot) into the out dir
//
// Requires a running, logged-in Bloomberg terminal on localhost:8194 for `update` and `email`.

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "RatesWeekly", "history.db");

for (int i = 1; i < args.Length - 1; i++)
    if (args[i].Equals("--db", StringComparison.OrdinalIgnoreCase)) dbPath = args[i + 1];

switch (cmd)
{
    case "update":
    {
        Console.WriteLine($"RatesWeekly — history update  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine($"store: {dbPath}");
        using var store = new HistoryStore(dbPath);
        try
        {
            var r = UpdateEngine.Run(store, new RatesSnapshot(), Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine($"DONE — {r.Tickers} tickers, {r.Seeded} seeded, {r.RowsWritten} rows written, " +
                              $"{r.NoPrice} without a live price, {r.Unknown} unknown, {r.Elapsed.TotalSeconds:F0}s");
            foreach (var w in r.Warnings) Console.WriteLine("  ! " + w);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UPDATE FAILED: " + ex.Message);
            return 1;
        }
    }

    case "status":
    {
        if (!File.Exists(dbPath)) { Console.WriteLine($"no store yet at {dbPath}"); return 0; }
        using var store = new HistoryStore(dbPath);
        var len = new FileInfo(dbPath).Length / 1024.0 / 1024.0;
        Console.WriteLine($"store:   {dbPath}  ({len:F1} MB)");
        Console.WriteLine($"tickers: {store.TickerCount():N0}");
        Console.WriteLine($"rows:    {store.RowCount():N0}");
        Console.WriteLine($"depths:  seed {UpdateEngine.SeedDays}d / corr {UpdateEngine.CorrSeedDays}d / " +
                          $"maintain {UpdateEngine.MaintainDays}d");
        // Probe the tickers the app actually stores — curve pillars carry a contributor suffix
        // ("USOSFR10 BGN Curncy"), so probing the bare name reports a false absence.
        foreach (var probe in new[]
                 { "USOSFR10 BGN Curncy", "EUSA5 BGN Curncy", "USSOFED2 Curncy",
                   "USSWIF7 Curncy", ".US1010IN G Index", "CO1 Comdty" })
        {
            var h = store.GetDaily(probe, 4000);
            Console.WriteLine(h.Count == 0
                ? $"  {probe,-22} (absent)"
                : $"  {probe,-22} {h.Count,5} closes  {h[0].Date:yyyy-MM-dd} .. {h[^1].Date:yyyy-MM-dd}  " +
                  $"last {h[^1].Value.ToString("F4", CultureInfo.InvariantCulture)}");
        }
        var covered = store.TickersCoveringDate(WeeklyCurves.MonthAgo(DateTime.Today));
        Console.WriteLine($"1m lookback resolvable for {covered:N0} of {store.TickerCount():N0} tickers");
        return 0;
    }

    case "render":
    {
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly", "out");
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outDir = args[i + 1];
        var only = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--") &&
            !a.Equals(outDir, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(a) && a.Length == 3);

        Directory.CreateDirectory(outDir);
        using var store = new HistoryStore(dbPath);
        if (store.LatestDate() is not { } asOf)
        {
            Console.Error.WriteLine("store is empty — run `update` first");
            return 1;
        }
        var configs = RateDesk.Core.Config.ConfigStore.LoadDefault();
        var svc = new PricingService(configs, new RatesSnapshot());
        // the SOURCES selection carries through to EVERY surface (desk 2026-08-26)
        var cliSrcOverrides = RateDesk.Weekly.Core.SourceStore.Load(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly"));
        string cliMeetingSrc(RateDesk.Core.MeetingScheduleDef sc) =>
            cliSrcOverrides.TryGetValue(sc.Name, out var so) ? so : sc.Source ?? "";
        int n = 0;
        foreach (var cfg in configs.Enabled)
        {
            if (only != null && !cfg.Ccy.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (cfg.Ois == null && cfg.Irs == null && cfg.Ladders.Count == 0) continue;
            try
            {
                var html = RateDesk.Weekly.Core.Render.CurrencyPage.Build(
                    cfg, svc.SourceFor(cfg.Ccy), store, asOf, cliMeetingSrc);
                var path = Path.Combine(outDir, cfg.Ccy.ToLowerInvariant() + ".html");
                File.WriteAllText(path, html);
                Console.WriteLine($"  {cfg.Ccy}  {new FileInfo(path).Length / 1024.0,6:F0} KB  {path}");
                n++;
            }
            catch (Exception ex) { Console.Error.WriteLine($"  ! {cfg.Ccy}: {ex.Message}"); }
        }
        if (only == null)
        {
            try
            {
                var mv = RateDesk.Weekly.Core.Series.MoverScan.Scan(configs, svc.SourceFor, store, asOf, cliMeetingSrc);
                var idx = Path.Combine(outDir, "index.html");
                File.WriteAllText(idx, RateDesk.Weekly.Core.Render.MoversPage.Build(mv));
                File.WriteAllText(Path.Combine(outDir, "movers.json"),
                    RateDesk.Weekly.Core.Series.MoverScan.ToJson(mv));
                Console.WriteLine($"  MOVERS {new FileInfo(idx).Length / 1024.0,5:F0} KB  {idx}  " +
                                  $"({mv.DmRanked.Count} DM / {mv.EmRanked.Count} EM ranked)");
                // the page itself carries NO blurb (desk rule) — the context lines land here
                if (mv.G3Line is { } g3) Console.WriteLine("    " + g3);
                foreach (var note in mv.Notes) Console.WriteLine("    " + note);
                n++;
                var pack = Path.Combine(outDir, RateDesk.Weekly.Core.Render.SiteFile.FileName);
                File.WriteAllText(pack,
                    RateDesk.Weekly.Core.Render.SiteFile.Build(configs, svc.SourceFor, store, asOf, mv, cliMeetingSrc));
                Console.WriteLine($"  PACK   {new FileInfo(pack).Length / 1024.0,5:F0} KB  {pack}");
            }
            catch (Exception ex) { Console.Error.WriteLine("  ! movers/pack: " + ex.Message); }
        }
        Console.WriteLine($"rendered {n} page(s) as of {asOf:yyyy-MM-dd} into {outDir}");
        return 0;
    }

    case "email":
    {
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly", "out");
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outDir = args[i + 1];
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly");
            using var store = new HistoryStore(dbPath);
            var rep = EmailBuilder.Build(Console.WriteLine, store, appData);
            var o = EmailBuilder.Render(rep, outDir, EmailBuilder.LoadSiteBase(appData), Console.WriteLine, store);
            Console.WriteLine($"as of {rep.AsOf:yyyy-MM-dd HH:mm:ss} — " +
                              $"{rep.Sections.Sum(s => s.Ccys.Count)} currencies, " +
                              $"{rep.Runs.Count} CB runs, {rep.Fronts.Count} front rows");
            Console.WriteLine($"fragment:  {o.FragmentPath}");
            Console.WriteLine($"plaintext: {o.PlainTextPath}");
            Console.WriteLine($"preview:   {o.PreviewPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EMAIL BUILD FAILED: " + ex.Message);
            return 1;
        }
    }

    case "daily":
    {
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly", "out");
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outDir = args[i + 1];
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly");
            using var store = new HistoryStore(dbPath);
            var rep = RateDesk.Weekly.Core.Daily.DailyBuilder.Build(store, Console.WriteLine, appData);
            var o = RateDesk.Weekly.Core.Daily.DailyBuilder.Render(rep, store, outDir, appData, Console.WriteLine);
            Console.WriteLine($"as of {rep.AsOf:yyyy-MM-dd HH:mm:ss} — {rep.Runs.Count} CB runs, " +
                              $"{rep.Fronts.Count} front rows");
            Console.WriteLine($"blast:     {o.BlastPath}");
            Console.WriteLine($"workbook:  {o.BookPath}" + (o.DailyDirCopy != null ? $"  (+ {o.DailyDirCopy})" : ""));
            Console.WriteLine($"email:     {o.FragmentPath}");
            Console.WriteLine();
            Console.WriteLine(File.ReadAllText(o.BlastPath));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("DAILY BUILD FAILED: " + ex.Message);
            return 1;
        }
    }

    case "savedown":
    {
        // regenerate both macro-enabled save-down books from stored data (no Bloomberg) and
        // mirror them into the configured OIS Runs / Inflation Runs folders
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly", "out");
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outDir = args[i + 1];
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly");
        var rep = RateDesk.Weekly.Core.ReportStore.Load(
                Path.Combine(outDir, RateDesk.Weekly.Core.Daily.DailyBuilder.ReportFile))
            ?? throw new InvalidOperationException("no stored daily report — run DAILY RUN once first");
        using var store = new HistoryStore(dbPath);
        // the RUN's frozen marks + next prints (audit 2026-08-26): an offline regen must never
        // downgrade the emailed files to stale closes under the same names
        RateDesk.Weekly.Core.Infl.InflHistory.LoadPersistedMarks(outDir, rep.AsOf, prefix: "daily_");
        var marks = RateDesk.Weekly.Core.Infl.InflHistory.LastLiveMarks;
        var nextPrints = RateDesk.Weekly.Core.Infl.InflHistory.LastNextPrints;
        var p1 = RateDesk.Weekly.Core.SaveDown.StoreBooks.WriteOis(rep, store, outDir, Console.WriteLine);
        var p2 = RateDesk.Weekly.Core.SaveDown.StoreBooks.WriteInfl(store, outDir, rep.AsOf, marks, Console.WriteLine);
        RateDesk.Weekly.Core.Infl.InflRunsXlsx.Write(store, outDir, rep.AsOf, marks, nextPrints, Console.WriteLine);
        RateDesk.Weekly.Core.Infl.InflEmail.WriteFragments(store, marks, nextPrints, rep.AsOf, outDir, daily: true);
        File.WriteAllText(Path.Combine(outDir, RateDesk.Weekly.Core.Daily.DailyBuilder.BlastFile),
            RateDesk.Weekly.Core.Daily.DailyBlast.Render(rep));
        File.WriteAllText(Path.Combine(outDir, RateDesk.Weekly.Core.Daily.DailyBuilder.BlastHtmlFile),
            RateDesk.Weekly.Core.Daily.DailyBlast.Html(rep));
        Console.WriteLine("daily inflation fragment, blast (text + table) and lean workbook regenerated");
        if (RateDesk.Weekly.Core.SaveDown.SaveDownConfig.Load(appData) is { } sd)
        {
            RateDesk.Weekly.Core.SaveDown.SaveDownConfig.Sync(outDir, "OIS_Runs_*.xlsm",
                Path.Combine(sd.Root, RateDesk.Weekly.Core.SaveDown.SaveDownConfig.OisFolder), Console.WriteLine);
            RateDesk.Weekly.Core.SaveDown.SaveDownConfig.Sync(outDir, "Inflation_Runs_*.xlsm",
                Path.Combine(sd.Root, RateDesk.Weekly.Core.SaveDown.SaveDownConfig.InflFolder), Console.WriteLine);
        }
        Console.WriteLine($"ois:  {p1}");
        Console.WriteLine($"infl: {p2}");
        return 0;
    }

    case "inflingest":
    {
        string? book = null, bbgcsv = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--book", StringComparison.OrdinalIgnoreCase)) book = args[i + 1];
            if (args[i].Equals("--bbgcsv", StringComparison.OrdinalIgnoreCase)) bbgcsv = args[i + 1];
        }
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly");
        book ??= RateDesk.Weekly.Core.Daily.DailyBuilder.LoadPublishString(appData, "inflBook");
        if (book == null || !File.Exists(book))
        {
            Console.Error.WriteLine("no inflation workbook — pass --book <xlsm> or set publish.json {\"inflBook\": ...}");
            return 1;
        }
        using var store = new HistoryStore(dbPath);
        // deep print history for the three fixing indices — the base-print validation gate
        // needs prints back to the workbook's first save (tiny fetch: 3 monthly series)
        try
        {
            using var refData = new RateDesk.Bloomberg.RefDataClient();
            foreach (var fam in RateDesk.Weekly.Core.Infl.InflHistory.Families)
            {
                var h = refData.GetDaily(fam.IndexTicker, 2600);
                if (h.Count > 0) store.UpsertDaily(fam.IndexTicker, h, excludeToday: true);
                Console.WriteLine($"  {fam.IndexTicker}: {h.Count} monthly prints");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! no Bloomberg session ({ex.Message}) — validating against stored prints only");
        }
        var res = RateDesk.Weekly.Core.Infl.InflHistory.Ingest(book, store, Console.WriteLine);
        if (bbgcsv != null && File.Exists(bbgcsv))
            RateDesk.Weekly.Core.Infl.InflHistory.SeedBackfill(bbgcsv, store, Console.WriteLine);
        Console.WriteLine($"fixings table now holds {store.FixingRowCount():N0} rows");
        return res.Ingested >= 0 ? 0 : 1;
    }

    case "inflexport":
    {
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly", "out");
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outDir = args[i + 1];
        using var store = new HistoryStore(dbPath);
        var path = RateDesk.Weekly.Core.Infl.InflBook.Write(store, outDir, Console.WriteLine);
        Console.WriteLine($"workbook: {path}");
        return 0;
    }

    case "export":
    {
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly", "out");
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outDir = args[i + 1];
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly");
            using var store = new HistoryStore(dbPath);
            var path = RateDesk.Weekly.Core.Daily.DailyBuilder.ExportBook(store, outDir, appData, Console.WriteLine);
            Console.WriteLine($"workbook: {path}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EXPORT FAILED: " + ex.Message);
            return 1;
        }
    }

    default:
        Console.WriteLine("RatesWeekly CLI — usage: update [--db <path>] | status [--db <path>] | render [ccy] [--out <dir>] | email [--out <dir>] | daily [--out <dir>] | export [--out <dir>] | inflingest [--book <xlsm>] [--bbgcsv <csv>] | inflexport [--out <dir>]");
        return cmd == "help" ? 0 : 1;
}
