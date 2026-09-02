namespace RateDesk.Weekly.Core.SaveDown
{
    /// <summary>THE DESK'S HISTORY SHIPS INSIDE THE APP (desk 2026-09-02: "the app is
    /// STANDALONE — incorporate the history INTO the app, that way it can't be missed. no copy
    /// paste of external file"). assets\history_seed.db — a VACUUM'd copy of the desk store,
    /// refreshed at every release — is an embedded resource, exactly like the configs and the
    /// save-down templates:
    ///
    ///   · a machine with NO store is BORN from the seed (EnsureStore, before anything opens
    ///     the db) — closes to 2019, the ingested inflation sheet, every rung record;
    ///   · a machine with a SHALLOW store (its own recent seed only — the second terminal,
    ///     2026-09-01/02) inherits everything the seed holds that it lacks, insert-only,
    ///     provenance kept (InheritInto);
    ///   · a DEEP store answers one local query and skips.
    ///
    /// The seed is as fresh as the release; the gap between the seed's last close and today is
    /// filled by StoreBackedHistory's ordinary gap-fill on the first run. The share-snapshot
    /// carrier (StoreBackup) remains as a top-up, but nothing depends on it any more — updating
    /// the exe IS the whole distribution.</summary>
    public static class EmbeddedSeed
    {
        public const string DbName = "history_seed.db";
        public const string TxtName = "history_seed.txt";

        /// <summary>The seed's own manifest ("asOf=... daily=..."), one line for the startup
        /// log — a stale seed must be visible, not discovered. Null when not embedded.</summary>
        public static string? Manifest()
        {
            try
            {
                var asm = typeof(EmbeddedSeed).Assembly;
                var res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(TxtName, StringComparison.OrdinalIgnoreCase));
                if (res == null) return null;
                using var s = asm.GetManifestResourceStream(res)!;
                using var r = new StreamReader(s);
                var kv = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Split('=', 2)).Where(a => a.Length == 2)
                    .ToDictionary(a => a[0].Trim(), a => a[1].Trim());
                return $"embedded desk history: as of {kv.GetValueOrDefault("asOf", "?")} " +
                       $"({kv.GetValueOrDefault("daily", "?")} closes from " +
                       $"{kv.GetValueOrDefault("dailyFrom", "?")}, " +
                       $"{kv.GetValueOrDefault("fixings", "?")} fixings)";
            }
            catch { return null; }
        }

        private static string ExtractDb()
        {
            var asm = typeof(EmbeddedSeed).Assembly;
            var res = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(DbName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("embedded history seed missing from this build");
            var tmp = Path.Combine(Path.GetTempPath(), "rw-seed-" + Guid.NewGuid().ToString("N") + ".db");
            using var s = asm.GetManifestResourceStream(res)!;
            using var f = File.Create(tmp);
            s.CopyTo(f);
            return tmp;
        }

        /// <summary>A machine with no store is born from the seed. Runs BEFORE anything opens
        /// the db; an existing store — any store — is never touched here. True when seeded.</summary>
        public static bool EnsureStore(string dbPath, Action<string>? log = null)
        {
            if (File.Exists(dbPath)) return false;
            var tmp = ExtractDb();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                File.Move(tmp, dbPath);
                log?.Invoke($"store: born from the {Manifest() ?? "embedded seed"} — " +
                            "recent days gap-fill from Bloomberg on the first run");
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>A shallow store (closes reaching back under ~120 days) inherits everything
        /// the embedded seed holds that it lacks — closes, rung records, fixings and index
        /// prints — insert-only, local rows never replaced. Returns the run note, or null when
        /// the store is already deep (one local query, no extraction).</summary>
        public static string? InheritInto(HistoryStore store, Action<string>? log = null)
        {
            var localFloor = store.EarliestDaily();
            if (localFloor is { } lf && lf <= DateTime.Today.AddDays(-120)) return null;

            var tmp = ExtractDb();
            try
            {
                using var snap = new HistoryStore(tmp);
                var snapFloor = snap.EarliestDaily();
                if (snapFloor is not { } sf
                    || (localFloor is { } lf2 && sf > lf2.AddDays(-60))) return null;

                var (closes, recs) = StoreBackup.InheritRowsFrom(store, snap);
                int fix = 0;
                foreach (var fam in Infl.InflHistory.Families)
                {
                    var local = store.GetFixingHistory(fam.Key)
                        .GroupBy(x => (x.Fix, x.Date.Date))
                        .ToDictionary(g => g.Key, g => g.Last().Source);
                    var hist = snap.GetFixingHistory(fam.Key)
                        .Where(x => !local.TryGetValue((x.Fix, x.Date.Date), out var ls)
                                    || (x.Source == "xls" && ls == "bbg")).ToList();
                    foreach (var srcPass in new[] { "bbg", "xls" })
                        foreach (var g in hist.Where(x => x.Source == srcPass).GroupBy(x => x.Fix))
                            fix += store.UpsertFixings(fam.Key, g.Key,
                                g.Select(x => new RateDesk.Core.Market.HistPoint(x.Date, x.Value)), srcPass);
                }
                log?.Invoke($"inherit: {closes:N0} close(s), {recs:N0} rung record(s), {fix:N0} " +
                            $"fixing row(s) from the embedded seed — this store now reaches " +
                            $"{store.EarliestDaily():dd-MMM-yy}");
                return $"INFL: this machine inherited the desk history from the app itself " +
                       $"({closes:N0} closes, {fix:N0} fixing rows) — lookbacks now reach " +
                       $"{store.EarliestDaily():dd-MMM-yy} and the Δ columns populate from this run.";
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
    }
}
