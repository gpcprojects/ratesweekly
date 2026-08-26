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
