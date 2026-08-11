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
        Console.WriteLine($"RATESWEEKLY — history update  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
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
        var covered = store.TickersCoveringDate(DateTime.Today.AddDays(-WeeklyCurves.MonthDays));
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
        int n = 0;
        foreach (var cfg in configs.Enabled)
        {
            if (only != null && !cfg.Ccy.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (cfg.Ois == null && cfg.Irs == null && cfg.Ladders.Count == 0) continue;
            try
            {
                var html = RateDesk.Weekly.Core.Render.CurrencyPage.Build(
                    cfg, svc.SourceFor(cfg.Ccy), store, asOf);
                var path = Path.Combine(outDir, cfg.Ccy.ToLowerInvariant() + ".html");
                File.WriteAllText(path, html);
                Console.WriteLine($"  {cfg.Ccy}  {new FileInfo(path).Length / 1024.0,6:F0} KB  {path}");
                n++;
            }
            catch (Exception ex) { Console.Error.WriteLine($"  ! {cfg.Ccy}: {ex.Message}"); }
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
            var rep = EmailBuilder.Build(Console.WriteLine);
            var o = EmailBuilder.Render(rep, outDir, EmailBuilder.LoadSiteBase(appData), Console.WriteLine);
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

    default:
        Console.WriteLine("RATESWEEKLY CLI — usage: update [--db <path>] | status [--db <path>] | render [ccy] [--out <dir>] | email [--out <dir>]");
        return cmd == "help" ? 0 : 1;
}
