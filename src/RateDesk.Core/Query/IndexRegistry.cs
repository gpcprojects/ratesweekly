using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Config;
using RateDesk.Core.Trades;

namespace RateDesk.Core.Query
{
    public enum TargetKind { PrimaryOis, PrimaryIrs, Ladder }

    public sealed record CurveTarget(string Ccy, TargetKind Kind, string? LadderName, ProductKind Product)
    {
        public bool IsLadder => Kind == TargetKind.Ladder;
        public string Describe() => Kind switch
        {
            TargetKind.Ladder => $"{Ccy} {LadderName}",
            TargetKind.PrimaryOis => $"{Ccy} OIS",
            _ => $"{Ccy} IRS",
        };
    }

    /// <summary>Resolves an index/currency alias token ("sofr", "usd", "cpi", "estr", "ff") to a
    /// currency + curve target. Built from configs plus a curated short-name dictionary.</summary>
    public sealed class IndexRegistry
    {
        private readonly Dictionary<string, CurveTarget> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConfigStore _configs;

        public IndexRegistry(ConfigStore configs)
        {
            _configs = configs;
            foreach (var cfg in configs.All)
            {
                var primary = PrimaryTarget(cfg);
                Add(cfg.Ccy, primary);

                if (cfg.Ois != null)
                {
                    var oisT = new CurveTarget(cfg.Ccy, TargetKind.PrimaryOis, null, ProductKind.OIS);
                    AddIndex(cfg.Ois.IndexName, oisT);
                }
                if (cfg.Irs != null)
                {
                    var irsT = new CurveTarget(cfg.Ccy, TargetKind.PrimaryIrs, null, ProductKind.IRS);
                    foreach (var leg in cfg.Irs.Legs) AddIndex(leg.FloatIndex, irsT);
                }
                foreach (var lad in cfg.Ladders)
                {
                    var t = new CurveTarget(cfg.Ccy, TargetKind.Ladder, lad.Name, ProductKind.OIS);
                    Add(lad.Name, t);
                    foreach (var a in lad.Aliases) Add(a, t);
                }
            }

            // curated short names (only added if the ccy is configured)
            Curated("sofr", "USD", TargetKind.PrimaryOis);
            Curated("ff", "USD", TargetKind.Ladder, "FedFunds");
            Curated("fedfunds", "USD", TargetKind.Ladder, "FedFunds");
            Curated("cpi", "USD", TargetKind.Ladder, "CPI");
            Curated("uscpi", "USD", TargetKind.Ladder, "CPI");
            Curated("sonia", "GBP", TargetKind.PrimaryOis);
            Curated("rpi", "GBP", TargetKind.Ladder, "RPI");
            Curated("ukrpi", "GBP", TargetKind.Ladder, "RPI");
            Curated("estr", "EUR", TargetKind.PrimaryOis);
            Curated("ester", "EUR", TargetKind.PrimaryOis);
            Curated("eonia", "EUR", TargetKind.PrimaryOis);
            Curated("euribor", "EUR", TargetKind.PrimaryIrs);
            Curated("hicp", "EUR", TargetKind.Ladder, "HICP");
            Curated("euhicp", "EUR", TargetKind.Ladder, "HICP");
            Curated("tona", "JPY", TargetKind.PrimaryOis);
            Curated("saron", "CHF", TargetKind.PrimaryOis);
            Curated("corra", "CAD", TargetKind.PrimaryOis);
            Curated("aonia", "AUD", TargetKind.PrimaryOis);
            Curated("bbsw", "AUD", TargetKind.PrimaryIrs);
            Curated("bkbm", "NZD", TargetKind.PrimaryIrs);
            Curated("sora", "SGD", TargetKind.PrimaryOis);
            Curated("hibor", "HKD", TargetKind.PrimaryIrs);
            Curated("jibar", "ZAR", TargetKind.PrimaryIrs);
            Curated("zaronia", "ZAR", TargetKind.PrimaryOis);
            Curated("shir", "ILS", TargetKind.PrimaryOis);
            Curated("stibor", "SEK", TargetKind.PrimaryIrs);
            Curated("nibor", "NOK", TargetKind.PrimaryIrs);
            Curated("cibor", "DKK", TargetKind.PrimaryIrs);
            Curated("wibor", "PLN", TargetKind.PrimaryIrs);
            Curated("pribor", "CZK", TargetKind.PrimaryIrs);
            Curated("bubor", "HUF", TargetKind.PrimaryIrs);
            Curated("klibor", "MYR", TargetKind.PrimaryIrs);
            Curated("tiie", "MXN", TargetKind.PrimaryOis);
            Curated("ftiie", "MXN", TargetKind.PrimaryOis);
            Curated("mibor", "INR", TargetKind.PrimaryOis);
            Curated("camara", "CLP", TargetKind.PrimaryOis);
            Curated("icp", "CLP", TargetKind.PrimaryOis);
            Curated("ibr", "COP", TargetKind.PrimaryOis);
            Curated("fr007", "CNY", TargetKind.PrimaryOis);
            Curated("repo", "CNY", TargetKind.PrimaryOis);
            Curated("cd", "KRW", TargetKind.PrimaryIrs);
            Curated("cdi", "BRL", TargetKind.Ladder, "DI");
            Curated("di", "BRL", TargetKind.Ladder, "DI");

            Curated("nowa", "NOK", TargetKind.PrimaryOis);
            Curated("czeonia", "CZK", TargetKind.PrimaryOis);
            Curated("polonia", "PLN", TargetKind.PrimaryOis);
            Curated("myor", "MYR", TargetKind.PrimaryOis);

            // O/N index names for ccys whose Bloomberg OIS par families carry no API prices —
            // resolve to PrimaryOis so the query errors explicitly instead of "cannot parse"
            foreach (var (alias, ccy) in new[] { ("destr", "DKK"), ("cita", "DKK"), ("hufonia", "HUF"), ("honia", "HKD"), ("kofr", "KRW") })
                if (configs.TryGet(ccy, out var mc) && mc.Ois == null)
                    _map[alias] = new CurveTarget(ccy, TargetKind.PrimaryOis, null, ProductKind.OIS);
        }

        private static ProductKind PrimaryProduct(CurrencyConfig cfg) =>
            cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null
                ? ProductKind.IRS
                : cfg.Ois != null ? ProductKind.OIS : ProductKind.IRS;

        private static CurveTarget PrimaryTarget(CurrencyConfig cfg)
        {
            if (cfg.Ois == null && cfg.Irs == null && cfg.Ladders.Count > 0)
                return new CurveTarget(cfg.Ccy, TargetKind.Ladder, cfg.Ladders[0].Name, ProductKind.OIS);
            var p = PrimaryProduct(cfg);
            return new CurveTarget(cfg.Ccy, p == ProductKind.OIS ? TargetKind.PrimaryOis : TargetKind.PrimaryIrs, null, p);
        }

        private void Add(string key, CurveTarget t)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _map[key.Trim()] = t;
        }

        private void AddIndex(string idx, CurveTarget t)
        {
            if (string.IsNullOrWhiteSpace(idx)) return;
            // normalise "F-TIIE" -> "ftiie", strip spaces
            var norm = new string(idx.Where(char.IsLetterOrDigit).ToArray());
            if (!_map.ContainsKey(norm)) _map[norm] = t;
        }

        private void Curated(string alias, string ccy, TargetKind kind, string? ladder = null)
        {
            if (!_configs.TryGet(ccy, out var cfg)) return;
            if (kind == TargetKind.Ladder && !cfg.Ladders.Any(l => l.Name.Equals(ladder, StringComparison.OrdinalIgnoreCase)))
                return;
            if (kind == TargetKind.PrimaryOis && cfg.Ois == null) return;
            if (kind == TargetKind.PrimaryIrs && cfg.Irs == null) return;
            _map[alias] = new CurveTarget(ccy, kind, ladder,
                kind == TargetKind.PrimaryIrs ? ProductKind.IRS : ProductKind.OIS);
        }

        public bool TryResolve(string token, out CurveTarget target)
        {
            var key = new string(token.Where(char.IsLetterOrDigit).ToArray());
            if (_map.TryGetValue(token, out target!)) return true;
            if (_map.TryGetValue(key, out target!)) return true;
            target = null!;
            return false;
        }
    }
}
