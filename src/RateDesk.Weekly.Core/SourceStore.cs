using System.Text.Json;
using RateDesk.Core;

namespace RateDesk.Weekly.Core
{
    /// <summary>PRICING-SOURCE SELECTION (trial, desk 2026-08-26 — dodgeball's per-run
    /// contributor picker, persisted): %APPDATA%\RatesWeekly\sources.json holds the desk's
    /// per-run contributor overrides (run name → mnemonic, "" = composite). An ABSENT entry
    /// means the config default (RBA/RBNZ NABZ, BOC BMOD, all else composite). Applied to
    /// PricingService.MeetingSourceOverrides before every build, so mids, change anchors,
    /// history sheets and save-down books all follow the SAME active source (v0.10.4 made the
    /// anchors source-coherent; WeeklyRun.Source carries the choice into the frozen report).</summary>
    public static class SourceStore
    {
        public const string FileName = "sources.json";

        private sealed class Shape { public Dictionary<string, string> Overrides { get; set; } = new(); }

        public static Dictionary<string, string> Load(string appDataDir)
        {
            var path = Path.Combine(appDataDir, FileName);
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var s = JsonSerializer.Deserialize<Shape>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return new Dictionary<string, string>(s?.Overrides ?? new(), StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }

        public static void Save(string appDataDir, Dictionary<string, string> overrides)
        {
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(Path.Combine(appDataDir, FileName),
                JsonSerializer.Serialize(new Shape { Overrides = overrides },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Push the saved overrides into the service (before ticker collection, so the
        /// overridden contributor's tickers are snapshotted). Logs every active override — a
        /// silently rerouted feed is exactly what the desk must never discover by surprise.</summary>
        public static void Apply(PricingService svc, string? appDataDir, Action<string>? log = null)
        {
            if (appDataDir == null) return;
            foreach (var (run, src) in Load(appDataDir))
            {
                var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                    s.Name.Equals(run, StringComparison.OrdinalIgnoreCase));
                if (sched == null) continue;
                var dflt = sched.Source ?? "";
                if (src.Equals(dflt, StringComparison.OrdinalIgnoreCase)) continue;   // = default, noise
                svc.MeetingSourceOverrides[sched.Name] = src;
                log?.Invoke($"sources: {sched.Name} priced from " +
                            $"{(src.Length == 0 ? "composite" : src)} (override; default " +
                            $"{(dflt.Length == 0 ? "composite" : dflt)})");
            }
        }
    }
}
