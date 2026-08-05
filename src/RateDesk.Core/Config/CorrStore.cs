using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RateDesk.Core.Config
{
    public sealed class CorrTickerDef
    {
        public string Label { get; set; } = "";
        public string Ticker { get; set; } = "";
        /// <summary>fx | cmdty | eqty — selects log-return differencing and scan exclusion rules.</summary>
        public string Class { get; set; } = "fx";
    }

    public sealed class CorrPairDef
    {
        /// <summary>A leg is a ticker LABEL from tickers[], a swap query ("nok 2y", "usd 2s10s",
        /// "eur 5y5y"), or a rates combo "eur 2y - usd 2y" (cross-market differential).</summary>
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string Why { get; set; } = "";
    }

    public sealed class CorrConfig
    {
        /// <summary>Curated pairs + auto-scan top-up stop at roughly this many rows.</summary>
        public int TargetTotal { get; set; } = 100;
        public List<CorrTickerDef> Tickers { get; set; } = new();
        public List<CorrPairDef> Pairs { get; set; } = new();
        /// <summary>Snapshot-matrix columns (A = label, B = spec; B empty → A is both).
        /// Empty list → built-in defaults (DXY, BCOM, Brent, CDX IG 5y).</summary>
        public List<CorrPairDef> Snapshot { get; set; } = new();
    }

    /// <summary>Loads config\correlations.json beside the exe (or the dev tree) with the embedded
    /// copy as fallback — same standalone-friendly pattern as the currency configs.</summary>
    public static class CorrStore
    {
        private static readonly JsonSerializerOptions Opts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static CorrConfig Load()
        {
            var exeDir = AppContext.BaseDirectory;
            foreach (var dir in new[]
                     {
                         Path.Combine(exeDir, "config"),
                         Path.Combine(exeDir, "..", "..", "..", "..", "..", "config"),
                     })
            {
                var p = Path.Combine(dir, "correlations.json");
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<CorrConfig>(File.ReadAllText(p), Opts) ?? new CorrConfig();
            }
            using var s = typeof(CorrStore).Assembly
                .GetManifestResourceStream("RateDesk.Core.config.correlations.json");
            if (s == null) return new CorrConfig();
            using var r = new StreamReader(s);
            return JsonSerializer.Deserialize<CorrConfig>(r.ReadToEnd(), Opts) ?? new CorrConfig();
        }
    }
}
