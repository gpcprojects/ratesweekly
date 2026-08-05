using System.Diagnostics;
using RateDesk.Bloomberg;
using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>One UPDATE click: snapshot live mids, then bring the history store current.
    ///
    /// Buckets are chosen from the store's DEPTH WATERMARK, not from "does this ticker have any
    /// row" — that distinction is the whole deepening plan. A ticker whose recorded depth is
    /// shallower than the depth we now want is re-seeded at the deeper window; everything else
    /// takes a short maintenance overlap. Raising SeedDays therefore really does deepen the store
    /// in place, spread over as many runs as MaxSeedPerRun implies.
    ///
    /// Bloomberg BDH is metered and the allowance is shared with the rest of the desk, so the two
    /// guards below are cost controls, not paranoia: seeding is capped per run, and per-ticker
    /// fallback fetches (~20s each on a throttled terminal) stop after a budget.</summary>
    public sealed class UpdateEngine
    {
        /// <summary>Seed depth. 45d by desk decision 2026-08-05 ("1 month for now"); deepen later
        /// by raising these — already-seeded tickers WILL re-fetch at the new depth, MaxSeedPerRun
        /// tickers at a time, so the deepening spreads across nights instead of one huge burst.
        /// Corr charts need ~2.5y and |z| ranking ~1y (DESIGN.md §0a).</summary>
        public const int SeedDays = 45;
        public const int CorrSeedDays = 45;

        /// <summary>Trailing overlap re-fetched for an already-deep ticker, so restated prints and
        /// a skipped week self-heal. Widened automatically when a ticker is staler than this.</summary>
        public const int MaintainDays = 45;

        /// <summary>Tickers seeded per run, per bucket — the "gradual / overnight" knob.</summary>
        public const int MaxSeedPerRun = 250;

        /// <summary>Per-ticker BDH fallbacks allowed in one run before we stop and defer the rest.
        /// Prefetch swallows its own batch failures, so a degraded terminal surfaces only as cache
        /// misses — each one a separate metered request. Stopping loses nothing: the next run
        /// re-fetches the same window.</summary>
        public const int MaxSingleFetchFailures = 40;

        /// <summary>Extra days added when widening a window to reach a stale ticker's last close,
        /// so the refetch overlaps rather than abuts the stored data.</summary>
        private const int GapSlackDays = 5;

        public sealed record Result(
            int Tickers, int Seeded, int Deferred, long RowsWritten, int NoHistory,
            int NoPrice, int Unknown, TimeSpan Elapsed, List<string> Warnings)
        {
            /// <summary>False when the run finished but did not fetch everything it set out to.</summary>
            public bool Complete => Deferred == 0 && Warnings.Count == 0;
        }

        public static Result Run(HistoryStore store, RatesSnapshot snap, Action<string>? log = null)
        {
            void Log(string s) => log?.Invoke(s);
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();

            var configs = ConfigStore.LoadDefault();
            var svc = new PricingService(configs, snap);
            var universe = TickerUniverse.Build(configs, svc);
            var corrAnchors = TickerUniverse.CorrAnchors();
            Log($"universe: {universe.Count} tickers across {configs.Enabled.Count()} currencies");

            using var refdata = new RefDataClient();

            Log("snapshotting live mids...");
            var status = refdata.Snapshot(universe, snap);
            int noPrice = status.Count(s => !s.HasPrice && s.Exists);
            int unknown = status.Count(s => !s.Exists);
            if (noPrice > 0) Log($"  {noPrice} ticker(s) with no live price (normal for run-down fronts)");
            if (unknown > 0)
                Log($"  {unknown} unknown security(ies) — speculative pattern tails, expected");

            // Partition by DEPTH, and widen a maintain window that would not reach a stale
            // ticker's last stored close (otherwise a permanent hole opens that no later run fills).
            // A security Bloomberg does not know has no history by definition — asking for it every
            // run is pure waste, and these never earn a depth watermark so they would retry forever.
            // Most are the speculative tails of the meeting-ticker patterns (families that stop
            // short), which is expected and not an error.
            var absent = new HashSet<string>(
                status.Where(s => !s.Exists).Select(s => s.Ticker), StringComparer.OrdinalIgnoreCase);

            var seed = new List<string>();
            var seedDeep = new List<string>();
            var maintain = new Dictionary<int, List<string>>();
            var today = DateTime.Today;
            foreach (var t in universe)
            {
                if (absent.Contains(t)) continue;
                bool anchor = corrAnchors.Contains(t);
                int need = anchor ? CorrSeedDays : SeedDays;
                if (store.SeededDepth(t) < need) { (anchor ? seedDeep : seed).Add(t); continue; }

                int win = MaintainDays;
                if (store.LastDate(t) is { } last)
                {
                    int stale = (int)(today - last).TotalDays + GapSlackDays;
                    if (stale > win) win = Math.Min(stale, Math.Max(need, stale));
                }
                if (!maintain.TryGetValue(win, out var b)) maintain[win] = b = new List<string>();
                b.Add(t);
            }

            int deferred = 0;
            List<string> Cap(List<string> bucket, string name)
            {
                if (bucket.Count <= MaxSeedPerRun) return bucket;
                deferred += bucket.Count - MaxSeedPerRun;
                Log($"  {bucket.Count - MaxSeedPerRun} {name} ticker(s) deferred to a later run (cap {MaxSeedPerRun})");
                return bucket.Take(MaxSeedPerRun).ToList();
            }
            var seedRun = Cap(seed, "seed");
            var seedDeepRun = Cap(seedDeep, "deep-seed");

            Log($"history: {seedRun.Count + seedDeepRun.Count} to seed, " +
                $"{maintain.Values.Sum(v => v.Count)} to maintain" +
                (maintain.Count > 1 ? $" (in {maintain.Count} window groups; some tickers were stale)" : ""));

            long rows = 0;
            int noHistory = 0, failures = 0;
            void Fetch(List<string> tickers, int days) =>
                FetchAndStore(refdata, store, tickers, days, Log, ref rows, ref noHistory, ref failures);

            Fetch(seedDeepRun, CorrSeedDays);
            Fetch(seedRun, SeedDays);
            foreach (var (win, bucket) in maintain.OrderBy(kv => kv.Key)) Fetch(bucket, win);

            if (failures >= MaxSingleFetchFailures)
                warnings.Add($"stopped after {failures} per-ticker fetch failures — terminal may be throttled; " +
                             "the next run re-fetches what was skipped");
            if (noHistory > 0)
                warnings.Add($"{noHistory} ticker(s) returned no history this run");
            if (deferred > 0)
                warnings.Add($"{deferred} ticker(s) deferred by the per-run seed cap — run UPDATE again to continue");

            string state = warnings.Count == 0 ? "ok" : "partial";
            store.RecordRun("update",
                $"state={state} tickers={universe.Count} seeded={seedRun.Count + seedDeepRun.Count} " +
                $"deferred={deferred} rows={rows} noHistory={noHistory} failures={failures} " +
                $"noPrice={noPrice} unknown={unknown}");
            sw.Stop();
            Log($"store: {store.TickerCount()} tickers / {store.RowCount()} rows total ({sw.Elapsed.TotalSeconds:F0}s, {state})");
            return new Result(universe.Count, seedRun.Count + seedDeepRun.Count, deferred,
                rows, noHistory, noPrice, unknown, sw.Elapsed, warnings);
        }

        private static void FetchAndStore(
            RefDataClient refdata, HistoryStore store, List<string> tickers, int days,
            Action<string> log, ref long rows, ref int noHistory, ref int failures)
        {
            if (tickers.Count == 0) return;
            log($"BDH {tickers.Count} ticker(s) x {days}d...");
            try { refdata.Prefetch(tickers, days); }
            catch (Exception ex) { log($"  ! batched BDH failed ({ex.Message}) — falling back to singles"); }

            long wrote = 0;
            int empty = 0, skipped = 0;
            foreach (var t in tickers)
            {
                if (failures >= MaxSingleFetchFailures) { skipped++; continue; }
                IReadOnlyList<HistPoint> h;
                try { h = refdata.GetDaily(t, days); }
                catch { h = Array.Empty<HistPoint>(); failures++; }
                if (h.Count == 0) { empty++; continue; }
                wrote += store.UpsertDaily(t, h);
                // Watermark only on a real fetch, so a failed or empty ticker retries next run.
                store.SetSeededDepth(t, days);
            }
            rows += wrote;
            noHistory += empty;
            if (empty > 0) log($"  {empty} ticker(s) returned no history");
            if (skipped > 0) log($"  {skipped} ticker(s) skipped — fetch-failure budget spent");
            log($"  wrote {wrote} row(s)");
        }
    }
}
