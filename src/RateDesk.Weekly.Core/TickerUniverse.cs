using RateDesk.Core;
using RateDesk.Core.Config;

namespace RateDesk.Weekly.Core
{
    /// <summary>Enumerates every ticker the weekly build needs — the same enumeration the Dodgeball
    /// WEEKLY loader does (curves incl. USD-OIS discount strips, enabled ladder pillars, meeting
    /// tickers), plus the correlation anchors (oil, DXY, FX crosses) that the rolling-corr charts
    /// difference against.</summary>
    public static class TickerUniverse
    {
        public static List<string> Build(ConfigStore configs, PricingService svc)
        {
            var all = new List<string>();
            foreach (var cfg in configs.Enabled)
            {
                if (cfg.Ois != null || cfg.Irs != null)
                    all.AddRange(svc.TickersWithDiscount(cfg, svc.SourceFor(cfg.Ccy)));
                foreach (var lad in cfg.Ladders)
                    all.AddRange(lad.Pillars.Where(p => p.Enabled)
                        .Select(p => ConfigStore.ResolveTicker(p.Ticker, "")));
            }
            all.AddRange(svc.MeetingTickers());
            all.AddRange(CorrStore.Load().Tickers.Select(t => t.Ticker)
                .Where(t => !string.IsNullOrWhiteSpace(t)));
            return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
