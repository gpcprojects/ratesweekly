using System.Globalization;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;

// RatesWeekly CLI — scriptable history maintenance, same code path as the app's UPDATE button.
// Exists so the store can be topped up (and later DEEPENED gradually/overnight) from a scheduled
// task without anyone sitting in front of the GUI.
//
//   RatesWeeklyCli update              bring the store current (seed/maintain per UpdateEngine)
//   RatesWeeklyCli status              store stats, no Bloomberg calls
//
// Requires a running, logged-in Bloomberg terminal on localhost:8194 for `update`.

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
        foreach (var probe in new[] { "USOSFR10 Curncy", "EUSA5 Curncy", "USSOFED2 Curncy", "CO1 Comdty" })
        {
            var h = store.GetDaily(probe, 4000);
            Console.WriteLine(h.Count == 0
                ? $"  {probe,-18} (absent)"
                : $"  {probe,-18} {h.Count,5} closes  {h[0].Date:yyyy-MM-dd} .. {h[^1].Date:yyyy-MM-dd}  " +
                  $"last {h[^1].Value.ToString("F4", CultureInfo.InvariantCulture)}");
        }
        return 0;
    }

    default:
        Console.WriteLine("RATESWEEKLY CLI — usage: update [--db <path>] | status [--db <path>]");
        return cmd == "help" ? 0 : 1;
}
