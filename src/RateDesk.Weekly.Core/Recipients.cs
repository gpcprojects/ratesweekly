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

        /// <summary>The incumbent xlsm's BCC_1 list verbatim (London Closing Runs email).</summary>
        public static readonly string[] Defaults =
        {
            "Arthur.LeDreff@brevanhoward.com",
            "dberge6@bloomberg.net",
            "globalratesexec@jbdh.com",
            "Ajay.balaji@brcap.com",
            "tony.yu@brcap.com",
            "Rates-team@brcap.com",
            "jlanders@veritionfund.com",
            "rhines@veritionfund.com",
            "Charlie.kirby@tudor.com",
            "angus.abbot@exoduspoint.com",
            "jernej.fink@lmrpartners.com",
            "rmuharemi@schonfeld.com",
            "Roee Feingold <RoeeFe@barakcapital.com>",
            "Alan@agavecapital.com",
            "jongjin.park@barclays.com",
            "Shanta, Paul <paul.shanta@brevanhoward.com>",
            "mirco.bulega@gmail.com",
            "sukhjeet.atwal@bluecrestcapital.com",
            "james.austin@missioncrestcapital.com",
        };

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
