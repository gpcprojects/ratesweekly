using System.Text.Json;

namespace RateDesk.Weekly.Core
{
    /// <summary>The daily runs email's recipient list (desk 2026-08-25) — replaces the
    /// incumbent workbook's VBA recipient flow. Edited via the RECIPIENTS button, persisted to
    /// %APPDATA%\RatesWeekly\recipients.json, PRELOADED with the incumbent modClosingRunsEmail
    /// list. Recipients go into the draft as BCC, ALWAYS BCC, and never anywhere else — a
    /// client list must never leak to other clients through To/Cc.</summary>
    public static class Recipients
    {
        public const string FileName = "recipients.json";

        /// <summary>EMPTY BY DESIGN (2026-08-28). This array used to hold the incumbent's BCC_1
        /// list verbatim - nineteen live client addresses at Brevan Howard, Tudor, ExodusPoint,
        /// Schonfeld, Barclays, BlueCrest, Verition and others.
        ///
        /// Two things were wrong with that. The repository is PUBLIC, so the client distribution
        /// list was published with the source. And because Load() falls back here whenever
        /// recipients.json is absent, every fresh install came preloaded with the whole list -
        /// one machine set up by someone who did not know, one click of DAILY EMAIL, and the
        /// desk's client list is on an outbound draft nobody meant to send.
        ///
        /// A recipient list is desk data, not application data. It belongs in
        /// %APPDATA%\RatesWeekly\recipients.json, entered through the RECIPIENTS button, and
        /// nowhere in the build. A new machine now starts with NO recipients and says so
        /// ("0 recipient(s) in BCC") rather than quietly addressing eighteen counterparties.</summary>
        public static readonly string[] Defaults = System.Array.Empty<string>();

        public static List<string> Load(string appDataDir)
        {
            try
            {
                var p = Path.Combine(appDataDir, FileName);
                if (File.Exists(p)
                    && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(p)) is { } l)
                    return l;
            }
            catch { /* corrupt file = defaults, not a crash */ }
            return Defaults.ToList();
        }

        public static void Save(string appDataDir, IEnumerable<string> entries)
        {
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(Path.Combine(appDataDir, FileName),
                JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Split pasted text on semicolons and line breaks; display-name entries with
        /// embedded commas ("Shanta, Paul &lt;...&gt;") survive because commas never split.</summary>
        public static List<string> Parse(string text) =>
            text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        /// <summary>The BCC string for the Outlook draft.</summary>
        public static string Bcc(string appDataDir) => string.Join("; ", Load(appDataDir));
    }
}
