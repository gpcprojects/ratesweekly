namespace RateDesk.Weekly.Core.SaveDown
{
    /// <summary>THE DATA STORE TRAVELS WITH THE DESK, NOT THE MACHINE (desk 2026-08-26):
    /// history.db holds rows that cannot be rebuilt — 'xls' marks ingested from the incumbent
    /// sheets, manual outage entries, and the per-day maturity records (point-in-time ticker
    /// fields Bloomberg cannot re-serve). A new machine must inherit them, not reseed 45 days
    /// of composite closes and lose the rest forever.
    ///
    /// The db itself must NOT live on a synced/shared path (SQLite WAL + cloud sync = a known
    /// corruption class, and two machines would fight over one file), so the design is:
    ///   · the WORKING db lives on the local disk (%LOCALAPPDATA%\RatesWeekly),
    ///   · after every successful run a consistent snapshot (VACUUM INTO) lands in the
    ///     save-down root's "RatesWeekly Data Store" folder — latest + one rotation,
    ///   · a machine that starts with NO local store and finds a backup offers to restore it.</summary>
    public static class StoreBackup
    {
        public const string Folder = "RatesWeekly Data Store";
        public const string LatestName = "history_backup.db";
        public const string PrevName = "history_backup_prev.db";

        /// <summary>Snapshot the live store to the configured save-down root. Loud on failure,
        /// silent-with-log when no destination is configured — a run must never fail because
        /// the share is down (the local store loses nothing; the next successful run backs up).</summary>
        public static void AfterRun(HistoryStore store, string appDataDir, Action<string>? log = null)
        {
            try
            {
                var sd = SaveDownConfig.Load(appDataDir);
                if (sd == null || !Directory.Exists(sd.Root))
                {
                    log?.Invoke("store backup: no save-down destination — snapshot skipped (local store intact)");
                    return;
                }
                var dir = Path.Combine(sd.Root, Folder);
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, LatestName + ".tmp");
                store.BackupTo(tmp);
                var latest = Path.Combine(dir, LatestName);
                var prev = Path.Combine(dir, PrevName);
                if (File.Exists(latest))
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(latest, prev);
                }
                File.Move(tmp, latest);
                log?.Invoke($"store backup: snapshot to {latest} " +
                            $"({new FileInfo(latest).Length / 1024.0 / 1024.0:F1} MB, previous rotated)");
            }
            catch (Exception ex) { log?.Invoke("! store backup FAILED: " + ex.Message); }
        }

        /// <summary>The newest restorable snapshot under a save-down root, if any.</summary>
        public static (string Path, DateTime AsOf)? FindBackup(string root)
        {
            try
            {
                var p = Path.Combine(root, Folder, LatestName);
                return File.Exists(p) ? (p, File.GetLastWriteTime(p)) : null;
            }
            catch { return null; }
        }

        /// <summary>Copy a snapshot in as the local working store — only ever onto a machine
        /// with NO existing store (an existing store is never silently replaced).</summary>
        public static bool Restore(string backupPath, string dbPath, Action<string>? log = null)
        {
            if (File.Exists(dbPath)) { log?.Invoke("store restore: local store already exists — refused"); return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            File.Copy(backupPath, dbPath);
            log?.Invoke($"store restore: {backupPath} → {dbPath} " +
                        $"({new FileInfo(dbPath).Length / 1024.0 / 1024.0:F1} MB)");
            return true;
        }

        /// <summary>INHERIT THE FIXING HISTORY WHEN THIS MACHINE'S IS THIN (desk report
        /// 2026-09-01: a second terminal's daily run published the inflation cards with every
        /// Δ1d/1w/1m blank). The unified fixings mapping is maturity-documented BY DESIGN, and
        /// maturity records only begin on a machine's own first run — so the 45 days of seeded
        /// closes can never be mapped locally, the fixings table starts one day deep, and the
        /// exact-date anchors (−1bd/−7d/−28d) find nothing for up to a month. The desk's own
        /// recorded history already rides the share snapshot; this pulls its `fixings` rows and
        /// index prints into the local store through the SAME merge rules the tables always use
        /// (UpsertFixings: xls wins / bbg fills; UpsertDaily: provenance-aware), touching the
        /// snapshot only via a temp copy. Returns a run note (or null when the store is already
        /// deep) so a machine that stays thin says WHY instead of publishing silent blanks.</summary>
        public static string? ImportInflation(HistoryStore store, string appDataDir, Action<string>? log = null)
        {
            // depth = distinct saved dates in the busiest family; 8+ days ⇒ anchors work
            int Depth()
            {
                int best = 0;
                foreach (var fam in Infl.InflHistory.Families)
                {
                    var n = store.GetFixingHistory(fam.Key).Select(x => x.Date.Date).Distinct().Count();
                    if (n > best) best = n;
                }
                return best;
            }
            int before = Depth();
            if (before >= 8) return null;

            var sd = SaveDownConfig.Load(appDataDir);
            var bk = sd != null && Directory.Exists(sd.Root) ? FindBackup(sd.Root) : null;
            if (bk is not { } b)
                return $"INFL: this store's fixing history is {before} day(s) deep — Δ1d/1w/1m stay " +
                       "blank until history accumulates (no share snapshot found to inherit from).";

            var tmp = Path.Combine(Path.GetTempPath(), "rw-inherit-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                File.Copy(b.Path, tmp, overwrite: true);
                using var snapStore = new HistoryStore(tmp);
                int rows = 0;
                foreach (var fam in Infl.InflHistory.Families)
                {
                    // fixings, source by source, through the standing merge rules — bbg first so
                    // an inherited validated-xls row still wins its cell afterwards. A cell this
                    // machine has already saved itself is kept (the local row is newer than any
                    // snapshot — inheritance fills the past, it does not rewrite it), with the
                    // one standing exception: a validated-xls snapshot row still beats a local
                    // bbg row, exactly as it would have had the sheet been ingested here.
                    var local = store.GetFixingHistory(fam.Key)
                        .GroupBy(x => (x.Fix, x.Date.Date))
                        .ToDictionary(g => g.Key, g => g.Last().Source);
                    var hist = snapStore.GetFixingHistory(fam.Key)
                        .Where(x => !local.TryGetValue((x.Fix, x.Date.Date), out var ls)
                                    || (x.Source == "xls" && ls == "bbg")).ToList();
                    foreach (var srcPass in new[] { "bbg", "xls" })
                        foreach (var g in hist.Where(x => x.Source == srcPass).GroupBy(x => x.Fix))
                            rows += store.UpsertFixings(fam.Key, g.Key,
                                g.Select(x => new RateDesk.Core.Market.HistPoint(x.Date, x.Value)), srcPass);
                    // the published index prints (Base column feeds off these), provenance
                    // kept, missing dates only — a print this machine pulled itself stands
                    var haveIx = store.GetDailyWithSource(fam.IndexTicker, 4000)
                        .Select(p => p.Date.Date).ToHashSet();
                    foreach (var g in snapStore.GetDailyWithSource(fam.IndexTicker, 4000)
                                 .Where(p => !haveIx.Contains(p.Date.Date)).GroupBy(p => p.Source))
                        store.UpsertDaily(fam.IndexTicker,
                            g.Select(p => new RateDesk.Core.Market.HistPoint(p.Date, p.Value)),
                            excludeToday: true, source: g.Key);
                    // and the SWIF maturity records — inherited days keep their documented
                    // mapping; a day this machine recorded itself is never touched
                    for (int m = 1; m <= 12; m++)
                    {
                        var tk = $"{fam.Root}{m} Curncy";
                        var haveRec = store.GetMaturityRows(tk).Select(r => r.Date).ToHashSet();
                        foreach (var (day, mat, eff) in snapStore.GetMaturityRows(tk))
                            if (!haveRec.Contains(day))
                                store.SetMaturity(tk, day, mat, eff);
                    }
                }
                int after = Depth();
                log?.Invoke($"infl inherit: {rows} fixing row(s) from the share snapshot " +
                            $"({b.AsOf:dd-MMM-yy HH:mm}) — history {before}→{after} day(s) deep");
                return after > before
                    ? $"INFL: inherited {rows} fixing row(s) from the share snapshot — history is now " +
                      $"{after} day(s) deep and the Δ columns populate from this run."
                    : $"INFL: this store's fixing history is {before} day(s) deep and the share snapshot " +
                      "adds nothing — Δ1d/1w/1m stay blank until history accumulates.";
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temp */ }
            }
        }

        /// <summary>One-time migration Roaming → Local (fresh-eyes review 2026-08-26: a WAL
        /// database in a roaming/synced profile is a corruption risk). Runs before the first
        /// store open; moves the db plus its -wal/-shm siblings when the Local copy is absent.</summary>
        public static void MigrateRoamingToLocal(string roamingDir, string dbPath, Action<string>? log = null)
        {
            try
            {
                if (File.Exists(dbPath)) return;
                var old = Path.Combine(roamingDir, "history.db");
                if (!File.Exists(old)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                File.Move(old, dbPath);
                foreach (var sfx in new[] { "-wal", "-shm" })
                    if (File.Exists(old + sfx)) File.Move(old + sfx, dbPath + sfx);
                log?.Invoke($"store: migrated history.db from Roaming to {dbPath}");
            }
            catch (Exception ex) { log?.Invoke("! store migration: " + ex.Message); }
        }
    }
}
