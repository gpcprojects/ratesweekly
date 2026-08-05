using System.Diagnostics;
using RateDesk.Bloomberg;
using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>One UPDATE click: snapshot live mids, then bring the history store current.
    /// Unseeded tickers backfill SeedDays of BDH; seeded tickers re-fetch a MaintainDays trailing
    /// overlap and upsert, so restated prints and skipped weeks self-heal. Page/email generation
    /// consumes the store afterwards — this class only moves data.</summary>
    public sealed class UpdateEngine
    {
        /// <summary>Seed depth is deliberately SHALLOW for now (desk call 2026-08-05: "1 month for
        /// now", deepen gradually/overnight later). 45 calendar days is the minimum that safely
        /// covers the 1m (today−31) close-to-close lookback over weekends/holidays. Because every
        /// update re-fetches at least MaintainDays and upserts, raising these later deepens the
        /// store in place — no migration. Corr charts need ~2.5y (63d rolling corr over a 2y span)
        /// and z-normalization needs ~1y of weekly changes: both stay dark until the deep seed.</summary>
        public const int SeedDays = 45;
        public const int CorrSeedDays = 45;
        public const int MaintainDays = 45;

        public sealed record Result(
            int Tickers, int Seeded, long RowsWritten, int NoPrice, int Unknown,
            TimeSpan Elapsed, List<string> Warnings);

        /// <summary>Runs one update against the live terminal. Blocking; call off the UI thread.</summary>
        public static Result Run(HistoryStore store, RatesSnapshot snap, Action<string>? log = null)
        {
            void Log(string s) => log?.Invoke(s);
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();

            var configs = ConfigStore.LoadDefault();
            var svc = new PricingService(configs, snap);
            var universe = TickerUniverse.Build(configs, svc);
            var corrAnchors = new HashSet<string>(
                CorrStore.Load().Tickers.Select(t => t.Ticker), StringComparer.OrdinalIgnoreCase);
            Log($"universe: {universe.Count} tickers across {configs.Enabled.Count()} currencies");

            using var refdata = new RefDataClient();

            Log("snapshotting live mids...");
            var status = refdata.Snapshot(universe, snap);
            int noPrice = status.Count(s => !s.HasPrice && s.Exists);
            int unknown = status.Count(s => !s.Exists);
            if (noPrice > 0) Log($"  {noPrice} ticker(s) with no live price (normal for run-down fronts)");
            if (unknown > 0)
            {
                warnings.Add($"{unknown} unknown security(ies) in the universe");
                foreach (var s in status.Where(x => !x.Exists).Take(10))
                    Log($"  ! unknown security: {s.Ticker}");
            }

            // Partition by required depth so each BDH batch carries one window.
            var seed = new List<string>();
            var seedDeep = new List<string>();
            var maintain = new List<string>();
            foreach (var t in universe)
            {
                if (store.LastDate(t) is null) (corrAnchors.Contains(t) ? seedDeep : seed).Add(t);
                else maintain.Add(t);
            }
            Log($"history: {seed.Count + seedDeep.Count} to seed, {maintain.Count} to maintain (+{MaintainDays}d overlap)");

            long rows = 0;
            rows += FetchAndStore(refdata, store, seedDeep, CorrSeedDays, Log);
            rows += FetchAndStore(refdata, store, seed, SeedDays, Log);
            rows += FetchAndStore(refdata, store, maintain, MaintainDays, Log);

            store.RecordRun("update",
                $"tickers={universe.Count} seeded={seed.Count + seedDeep.Count} rows={rows} noPrice={noPrice} unknown={unknown}");
            sw.Stop();
            Log($"store: {store.TickerCount()} tickers / {store.RowCount()} rows total ({sw.Elapsed.TotalSeconds:F0}s)");
            return new Result(universe.Count, seed.Count + seedDeep.Count, rows, noPrice, unknown, sw.Elapsed, warnings);
        }

        private static long FetchAndStore(
            RefDataClient refdata, HistoryStore store, List<string> tickers, int days, Action<string> log)
        {
            if (tickers.Count == 0) return 0;
            log($"BDH {tickers.Count} ticker(s) x {days}d...");
            try { refdata.Prefetch(tickers, days); }
            catch (Exception ex) { log($"  ! batched BDH failed ({ex.Message}) — falling back to singles"); }
            long rows = 0;
            int empty = 0;
            foreach (var t in tickers)
            {
                IReadOnlyList<HistPoint> h;
                try { h = refdata.GetDaily(t, days); }
                catch { h = Array.Empty<HistPoint>(); }
                if (h.Count == 0) { empty++; continue; }
                rows += store.UpsertDaily(t, h);
            }
            if (empty > 0) log($"  {empty} ticker(s) returned no history");
            log($"  wrote {rows} row(s)");
            return rows;
        }
    }
}
