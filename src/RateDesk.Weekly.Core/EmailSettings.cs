using System.Text.Json;

namespace RateDesk.Weekly.Core
{
    /// <summary>Which elements the OUTGOING emails include (desk 2026-08-21) — the tickbox
    /// matrices on the app's front page. These gate COMPOSITION ONLY, at the moment CREATE/COPY
    /// EMAIL or DAILY EMAIL is clicked: the runs always pull, store and render everything
    /// regardless, so unticking a section never loses data — it just leaves it out of the email.
    /// Persisted to %APPDATA%\RatesWeekly\emailsettings.json; everything defaults ON.</summary>
    public sealed class EmailSettings
    {
        // Consolidated Weekly Email
        public bool WeeklyFrontTable { get; set; } = true;
        public bool WeeklyOisRuns { get; set; } = true;
        public bool WeeklyForwardGrid { get; set; } = true;
        public bool WeeklyInflRuns { get; set; } = true;
        public bool WeeklyDashboardsAttachment { get; set; } = true;

        // Daily Runs Email
        public bool DailyFrontTable { get; set; } = true;
        public bool DailyOisRuns { get; set; } = true;
        public bool DailyInflRuns { get; set; } = true;
        public bool DailyXlsAttachment { get; set; } = true;
        public bool DailyInflXlsAttachment { get; set; } = true;

        public const string FileName = "emailsettings.json";

        public static EmailSettings Load(string appDataDir)
        {
            try
            {
                var p = Path.Combine(appDataDir, FileName);
                if (File.Exists(p)
                    && JsonSerializer.Deserialize<EmailSettings>(File.ReadAllText(p)) is { } s)
                    return s;
            }
            catch { /* a corrupt settings file means defaults, not a crash */ }
            return new EmailSettings();
        }

        public void Save(string appDataDir)
        {
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(Path.Combine(appDataDir, FileName),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Persist/reload a built WeeklyReport so the email can be COMPOSED at click time
    /// from frozen run data under the CURRENT tickboxes — the data is what the run pulled, the
    /// selection is whatever the desk has ticked right now.</summary>
    public static class ReportStore
    {
        private static readonly JsonSerializerOptions Opts = new()
        {
            PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
        };

        public static void Save(RateDesk.Core.WeeklyReport rep, string path) =>
            File.WriteAllText(path, JsonSerializer.Serialize(rep, Opts));

        public static RateDesk.Core.WeeklyReport? Load(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<RateDesk.Core.WeeklyReport>(File.ReadAllText(path), Opts)
                    : null;
            }
            catch { return null; }
        }
    }
}
