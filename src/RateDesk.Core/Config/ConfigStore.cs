using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace RateDesk.Core.Config
{
    public sealed class ConfigStore
    {
        private readonly Dictionary<string, CurrencyConfig> _byCcy = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where the active configs came from ("embedded" or a directory path).</summary>
        public string Origin { get; private set; } = "embedded";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private void AddJson(string json, string name)
        {
            var cfg = JsonSerializer.Deserialize<CurrencyConfig>(json, JsonOpts)
                      ?? throw new InvalidOperationException($"Bad config: {name}");
            if (string.IsNullOrWhiteSpace(cfg.Ccy))
                throw new InvalidOperationException($"Config missing ccy: {name}");
            _byCcy[cfg.Ccy] = cfg;
        }

        public static ConfigStore LoadFromDirectory(string dir)
        {
            var store = new ConfigStore { Origin = dir };
            foreach (var f in Directory.EnumerateFiles(dir, "*.json").OrderBy(x => x))
                store.AddJson(File.ReadAllText(f), f);
            return store;
        }

        /// <summary>Configs baked into the assembly — the exe works with zero external files.</summary>
        public static ConfigStore LoadEmbedded()
        {
            var store = new ConfigStore { Origin = "embedded" };
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames()
                         .Where(n => n.Contains("config.currencies.", StringComparison.OrdinalIgnoreCase)
                                     && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n))
            {
                using var s = asm.GetManifestResourceStream(res)!;
                using var r = new StreamReader(s);
                store.AddJson(r.ReadToEnd(), res);
            }
            if (store._byCcy.Count == 0)
                throw new InvalidOperationException("No embedded currency configs found in RateDesk.Core.");
            return store;
        }

        /// <summary>Standalone-friendly loader: a config\currencies folder near the exe (or the dev tree)
        /// overrides; otherwise the embedded configs are used — no external files required.</summary>
        public static ConfigStore LoadDefault()
        {
            var exeDir = AppContext.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         Path.Combine(exeDir, "config", "currencies"),
                         Path.Combine(exeDir, "..", "..", "..", "..", "..", "config", "currencies"),
                     })
            {
                try
                {
                    if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.json").Any())
                        return LoadFromDirectory(Path.GetFullPath(candidate));
                }
                catch { /* fall through to embedded */ }
            }
            return LoadEmbedded();
        }

        public IReadOnlyCollection<CurrencyConfig> All => _byCcy.Values;
        public IEnumerable<CurrencyConfig> Enabled => _byCcy.Values.Where(c => c.Enabled).OrderBy(c => c.Ccy);
        public bool TryGet(string ccy, out CurrencyConfig cfg) => _byCcy.TryGetValue(ccy, out cfg!);
        public CurrencyConfig Get(string ccy) =>
            _byCcy.TryGetValue(ccy, out var c) ? c : throw new KeyNotFoundException($"No config for currency '{ccy}'");

        /// <summary>Resolve a pillar ticker to the full Bloomberg security with pricing source.</summary>
        public static string ResolveTicker(string baseTicker, string source)
        {
            if (baseTicker.EndsWith(" Index", StringComparison.OrdinalIgnoreCase) ||
                baseTicker.EndsWith(" Curncy", StringComparison.OrdinalIgnoreCase) ||
                baseTicker.EndsWith(" Comdty", StringComparison.OrdinalIgnoreCase))
                return baseTicker;
            return string.IsNullOrEmpty(source) ? $"{baseTicker} Curncy" : $"{baseTicker} {source} Curncy";
        }
    }
}
