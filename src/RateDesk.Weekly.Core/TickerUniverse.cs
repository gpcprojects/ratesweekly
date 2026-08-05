using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core
{
    /// <summary>Everything the weekly build needs a daily history for.
    ///
    /// Mirrors the enumeration Dodgeball's WEEKLY loader does (curves incl. USD-OIS discount
    /// strips, enabled ladder pillars, meeting tickers) and adds what THIS app needs on top:
    /// ladder fixings, quoted inflation forwards, and the correlation anchors.
    ///
    /// One trap worth naming: Dodgeball reads history straight from BDH, which serves any spelling
    /// of a security. Here history comes from a local store keyed by EXACT ticker string, so a
    /// consumer asking for a different spelling than the one stored gets silence rather than a
    /// fallback. The source-qualified meeting runs are where that bites — see below.</summary>
    public static class TickerUniverse
    {
        /// <summary>The meeting stitcher walks ticker indices 1..13, one past MeetingTickers' own
        /// enumeration, so the stored set has to reach that far too.</summary>
        private const int MeetingMaxN = 13;

        /// <summary>Inflation forward grid: the annual strip (1y1y..9y1y, matching the nominal
        /// forward charts) plus the equal-tenor diagonals including the 5y5y benchmark. Whole
        /// single digits only — the pattern substitutes bare integers, so 10 would render
        /// ambiguously (FWISUS105 reads as both 10y5y and 1y05y).
        /// ⚠ grid choice pending desk sign-off (DESIGN.md §10 backlog).</summary>
        private static readonly (int A, int B)[] InflationForwards =
            Enumerable.Range(1, 9).Select(a => (a, 1))
                .Concat(new[] { (2, 2), (3, 3), (4, 4), (5, 5), (7, 7), (9, 9) })
                .Distinct().ToArray();

        public static List<string> Build(ConfigStore configs, PricingService svc)
        {
            var all = new List<string>();

            foreach (var cfg in configs.Enabled)
            {
                if (cfg.Ois != null || cfg.Irs != null)
                    all.AddRange(svc.TickersWithDiscount(cfg, svc.SourceFor(cfg.Ccy)));

                // The forward LADDER (1y1y … 30y20y) as QUOTED securities. This app has no curve
                // engine, so deriving these would mean bootstrapping per date per currency; the
                // quoted point also gives an exact close-to-close 1w/1m change on the same
                // instrument. Probed live 2026-08-05: every requested point resolves by NAME with
                // two-sided prices in both families. Currencies with no forward id (CLP/HKD/THB/BRL
                // — Bloomberg's curves are wrong for them, deliberately) simply contribute nothing.
                all.AddRange(ForwardLadder.Tickers(cfg));

                foreach (var lad in cfg.Ladders)
                {
                    all.AddRange(lad.Pillars.Where(p => p.Enabled)
                        .Select(p => ConfigStore.ResolveTicker(p.Ticker, "")));

                    // The ladder's own fixing — CPURNSA / UKRPI / CPTFEMU / BZDIOVRA. Config values
                    // are already full securities, so no ResolveTicker (matching Core's own query
                    // assembly). Without these the inflation pages have no fixings history at all.
                    if (!string.IsNullOrWhiteSpace(lad.FixingTicker)) all.Add(lad.FixingTicker);

                    // Quoted inflation forwards (FWISUS{A}{B} etc). Unlike nominal forwards, these
                    // cannot be derived here — an inflation forward off the ZC ladder needs a
                    // bootstrapped curve this app does not build, so the quote is the only source.
                    if (!string.IsNullOrWhiteSpace(lad.FwdTickerPattern))
                        foreach (var (a, b) in InflationForwards)
                            all.Add(lad.FwdTickerPattern.Replace("{A}", a.ToString())
                                                        .Replace("{B}", b.ToString()));
                }
            }

            // Monthly CPI fixing swaps (USSWIF/BPSWIF/EUSWIF 1..12) — the market's forecast of each
            // upcoming print. Calendar-month indexed and roll once a year, which is why the store
            // keeps their maturities.
            foreach (var f in CpiFixings.Families)
                if (configs.Enabled.Any(c => c.Ccy.Equals(f.Ccy, StringComparison.OrdinalIgnoreCase)))
                    all.AddRange(CpiFixings.Tickers(f));

            all.AddRange(svc.MeetingTickers());

            // MeetingTickers qualifies by the schedule's contributor (BOC=BMOD, RBA/RBNZ=NABZ), but
            // the 1w/1m stitcher asks for the COMPOSITE spelling with no contributor. Against live
            // BDH both resolve; against an exact-match local store only the stored one does — so
            // those three runs' meeting changes would be permanently blank. Store both spellings.
            foreach (var sched in MeetingsStore.Schedules)
            {
                if (string.IsNullOrEmpty(sched.Source)) continue;
                foreach (var pat in sched.Tickers)
                {
                    if (!pat.Contains("{N}")) continue;
                    for (int n = 0; n <= MeetingMaxN; n++)
                        all.Add(pat.Replace("{N}", n.ToString()) + " Curncy");
                }
            }

            all.AddRange(CorrAnchors());

            return all.Where(t => !string.IsNullOrWhiteSpace(t))
                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Oil, the dollar, FX crosses and the rest of the correlation anchor set. These
        /// need a deeper history than the rates pillars (a 63-day rolling correlation shown over
        /// ~2 years needs ~2.5 years of raw series), so the engine seeds them as their own
        /// bucket — it is the one place a different depth is wanted per ticker.</summary>
        public static HashSet<string> CorrAnchors() =>
            new(CorrStore.Load().Tickers.Select(t => t.Ticker).Where(t => !string.IsNullOrWhiteSpace(t)),
                StringComparer.OrdinalIgnoreCase);
    }
}
