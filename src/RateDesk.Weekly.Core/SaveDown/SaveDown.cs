using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RateDesk.Weekly.Core.SaveDown
{
    /// <summary>WHERE the daily runs are saved down (desk 2026-08-25). On app open the system
    /// looks for a network drive called "salix" and locates the Coverage &amp; Counterparties
    /// folder on it; found → "C+C folder located successfully" on the status line, nothing to
    /// click. Not found → a dialog offers "Locate C+C" (folder picker) or "Save Locally"
    /// (the user's Documents). Either way the app creates — and afterwards checks for — two
    /// folders, "OIS Runs" and "Inflation Runs", and each day's run files land there under
    /// dated names, so no previous day is ever overwritten.</summary>
    public static class SaveDownConfig
    {
        public const string OisFolder = "OIS Run History";
        public const string InflFolder = "Inflation Fixing Run History";
        public const string FileName = "savedown.json";

        public sealed record Config(string Mode, string Root);   // mode: "cc" | "local"

        public static Config? Load(string appDataDir)
        {
            var p = Path.Combine(appDataDir, FileName);
            if (!File.Exists(p)) return null;
            try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(p)); }
            catch { return null; }
        }

        public static void Save(string appDataDir, Config cfg)
        {
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(Path.Combine(appDataDir, FileName),
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static string LocalRoot() =>
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        /// <summary>Scan network drives whose volume label or UNC share path mentions "salix"
        /// and locate the Coverage &amp; Counterparties folder (either spelling) at the root or
        /// one level down. Returns the folder the run folders should live in: C+C's own
        /// "OIS and Inflation Runs" subfolder when it exists (the incumbent home), else C+C.</summary>
        public static string? DetectSalix(Action<string>? log = null)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Network || !d.IsReady) continue;
                    bool salix = d.VolumeLabel.Contains("salix", StringComparison.OrdinalIgnoreCase)
                                 || (UncTarget(d.Name.TrimEnd('\\')) is { } unc
                                     && unc.Contains("salix", StringComparison.OrdinalIgnoreCase));
                    if (!salix) continue;
                    if (FindCc(d.RootDirectory.FullName) is { } cc)
                    {
                        log?.Invoke($"save-down: C+C located on {d.Name} → {cc}");
                        return cc;
                    }
                }
                catch { /* an unready mapped drive throws on label access — skip it */ }
            }
            return null;
        }

        public static string? FindCc(string root)
        {
            static bool IsCc(string name) =>
                name.Equals("Coverage & Counterparties", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Coverage and Counterparties", StringComparison.OrdinalIgnoreCase);
            try
            {
                foreach (var level1 in Directory.EnumerateDirectories(root))
                {
                    if (IsCc(Path.GetFileName(level1))) return PreferRunsHome(level1);
                    try
                    {
                        foreach (var level2 in Directory.EnumerateDirectories(level1))
                            if (IsCc(Path.GetFileName(level2))) return PreferRunsHome(level2);
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string PreferRunsHome(string cc)
        {
            var home = Path.Combine(cc, "OIS and Inflation Runs");
            return Directory.Exists(home) ? home : cc;
        }

        /// <summary>Create (and afterwards just check for) the two run folders.</summary>
        public static (string Ois, string Infl) EnsureFolders(string parent)
        {
            var ois = Path.Combine(parent, OisFolder);
            var infl = Path.Combine(parent, InflFolder);
            Directory.CreateDirectory(ois);
            Directory.CreateDirectory(infl);
            return (ois, infl);
        }

        /// <summary>Mirror every locally held run file the destination folder is missing (or
        /// holds an older copy of) — the v0.9.1 catch-up rule, per folder and pattern. Soft
        /// false when the destination is unreachable; nothing is lost locally.</summary>
        public static bool Sync(string localDir, string pattern, string destDir, Action<string>? log = null)
        {
            var local = Directory.Exists(localDir)
                ? Directory.GetFiles(localDir, pattern) : Array.Empty<string>();
            try
            {
                Directory.CreateDirectory(destDir);
                int copied = 0;
                foreach (var f in local)
                {
                    var target = Path.Combine(destDir, Path.GetFileName(f));
                    if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(f))
                        continue;
                    File.Copy(f, target, overwrite: true);
                    copied++;
                }
                if (copied > 0) log?.Invoke($"save-down: mirrored {copied} file(s) to {destDir}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"! save-down: {destDir} unreachable ({ex.Message}) — {local.Length} " +
                            "file(s) held locally; they mirror when it returns");
                return false;
            }
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        private static string? UncTarget(string driveLetter)
        {
            var sb = new StringBuilder(512);
            int len = sb.Capacity;
            return WNetGetConnection(driveLetter, sb, ref len) == 0 ? sb.ToString() : null;
        }
    }
}
