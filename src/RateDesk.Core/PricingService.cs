using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.Risk;
using RateDesk.Core.Trades;

namespace RateDesk.Core
{
    /// <summary>
    /// Facade over configs + quotes + curves + pricing. All QLNet work is serialized behind
    /// one lock (QLNet evaluation-date state is ambient/global).
    /// </summary>
    public sealed partial class PricingService
    {
        internal readonly object _gate = new();
        private readonly Dictionary<(string ccy, string src), (long version, DateTime builtUtc, CurveSet curves)> _curveCache = new();

        public ConfigStore Configs { get; }
        public RatesSnapshot Snapshot { get; }
        // written from the UI thread, read from analysis worker threads
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> ActiveSource { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        public PricingService(ConfigStore configs, RatesSnapshot snapshot)
        {
            Configs = configs;
            Snapshot = snapshot;
        }

        public Date Today
        {
            get
            {
                var now = DateTime.Today;
                return new Date(now.Day, (Month)now.Month, now.Year);
            }
        }

        public string SourceFor(string ccy)
        {
            if (ActiveSource.TryGetValue(ccy, out var s)) return s;
            return Configs.Get(ccy).DefaultSource;
        }

        /// <summary>All full tickers needed for a currency at a given source (curve + fixings).</summary>
        public static IEnumerable<string> TickersFor(CurrencyConfig cfg, string source)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (cfg.Ois != null)
            {
                foreach (var p in cfg.Ois.Curve.Where(p => p.Enabled))
                    set.Add(ConfigStore.ResolveTicker(p.Ticker, source));
                if (!string.IsNullOrEmpty(cfg.Ois.OnFixingTicker)) set.Add(cfg.Ois.OnFixingTicker);
            }
            if (cfg.Irs != null)
            {
                foreach (var p in cfg.Irs.Curve.Where(p => p.Enabled))
                    set.Add(ConfigStore.ResolveTicker(p.Ticker, source));
                foreach (var leg in cfg.Irs.Legs)
                    if (!string.IsNullOrEmpty(leg.FixingTicker)) set.Add(leg.FixingTicker);
                if (cfg.Irs.FrontFromOis != null)
                {
                    foreach (var bt in cfg.Irs.FrontFromOis.BasisTickers) set.Add(bt);
                    foreach (var ft in cfg.Irs.FrontFromOis.FraTickers) set.Add(ft);
                }
            }
            return set;
        }

        public CurveSet GetCurves(string ccy, string? sourceOverride = null)
        {
            lock (_gate)
            {
                var cfg = Configs.Get(ccy);
                var src = sourceOverride ?? SourceFor(ccy);
                return GetCurvesUnlocked(cfg, src);
            }
        }

        private Date AdjustedToday(CurrencyConfig cfg)
        {
            var cal = QL.QlMaps.MakeCalendar(cfg.Calendar);
            return cal.adjust(Today, BusinessDayConvention.Following);
        }

        public PriceResult Price(TradeSpec spec, bool withLadder = true)
        {
            lock (_gate)
            {
                var cfg = Configs.Get(spec.Ccy);
                var src = spec.Source ?? SourceFor(spec.Ccy);
                var curves = GetCurvesUnlocked(cfg, src);
                var result = Pricer.Price(spec, curves);
                if (withLadder)
                {
                    Risk.Ladder.Compute(spec, cfg, src, Snapshot, curves.AsOf, result, ExternalDiscountFor(cfg),
                        DiscountCcyFor(cfg));
                    try { Risk.Ladder.ComputeForward(spec, cfg, src, Snapshot, curves.AsOf, result, ExternalDiscountFor(cfg)); }
                    catch (Exception ex) { result.Warnings.Add("fwd ladder: " + ex.Message); }
                }
                return result;
            }
        }

        /// <summary>Parse a ticket command, SIZE it, then price it. This is the real entry point for
        /// the CLI's "price" command and for anything else pricing from a command string.
        ///
        /// <para>The ticket grammar had no dv01 concept at all: every trade priced at the flat 10mm
        /// default, so the CLI reported a fifth of the risk the analytics bar showed for the same
        /// trade. Sizing now goes through the one rule: a typed notional is dealt EXACTLY, else the
        /// dv01 target (or the desk default) is converted through the trade's own density and rounded
        /// to a dealable lot.</para>
        ///
        /// <para>Both the density probe and the real price happen inside one _gate critical section so
        /// they see the same curve build and evaluation date.</para></summary>
        public PriceResult PriceCommand(string command, bool withLadder = true)
        {
            var spec = CommandParser.Parse(command, Configs);
            lock (_gate)
            {
                string? note = ResolveSpecSize(spec);
                var result = Price(spec, withLadder);
                if (note != null) result.Warnings.Add(note);
                return result;
            }
        }

        /// <summary>Set <see cref="TradeSpec.Notional"/> from the spec's sizing intent. Caller holds
        /// _gate. A typed notional wins and is never rounded; otherwise the dv01 target (defaulting to
        /// the desk's) is divided by the trade's own 1mm density and rounded to a dealable lot.</summary>
        /// <returns>A warning to surface when sizing could not be applied, else null.</returns>
        internal string? ResolveSpecSize(TradeSpec spec)
        {
            if (spec.ExplicitNotional.HasValue) { spec.Notional = spec.ExplicitNotional.Value; return null; }

            var cfg = Configs.Get(spec.Ccy);
            var src = spec.Source ?? SourceFor(spec.Ccy);
            var curves = GetCurvesUnlocked(cfg, src);
            QLNet.Settings.setEvaluationDate(curves.AsOf);

            double dv01 = spec.Dv01Target ?? Risk.RiskSizer.DefaultDv01Usd;
            double fx;
            try { fx = FxRiskFactor(spec.Dv01Ccy, cfg.Ccy); }
            catch (Exception ex)
            {
                // leave the legacy notional rather than fail the price — but SAY so, or the trade
                // silently prices at a fifth of the intended risk
                return $"dv01 target not applied — {ex.Message}; priced on the flat {spec.Notional:N0} instead.";
            }
            var probe = CloneForProbe(spec);
            double density = Pricer.Price(probe, curves).Annuity01;   // dv01 per 1mm, trade ccy
            spec.Notional = Risk.RiskSizer.Resolve(density, explicitDv01: dv01 * fx).Notional;
            return null;
        }

        /// <summary>Copy of a spec at 1mm, for the density probe — must not mutate the caller's.</summary>
        private static TradeSpec CloneForProbe(TradeSpec s) => new()
        {
            Ccy = s.Ccy, Product = s.Product, StartKind = s.StartKind,
            ImmDate = s.ImmDate, ImmCode = s.ImmCode, ForwardStart = s.ForwardStart,
            ExplicitStart = s.ExplicitStart, Tenor = s.Tenor, ExplicitEnd = s.ExplicitEnd,
            FraStartMonths = s.FraStartMonths, FraEndMonths = s.FraEndMonths,
            Notional = 1_000_000, PayFixed = s.PayFixed, FixedRate = s.FixedRate,
            FloatTenorOverride = s.FloatTenorOverride, Source = s.Source,
        };

        private CurveSet GetCurvesUnlocked(CurrencyConfig cfg, string src)
        {
            var key = (cfg.Ccy.ToUpperInvariant(), src.ToUpperInvariant());
            long v = Snapshot.Version;
            // ~700 live subscriptions bump Version constantly — without the 1s dampener every 600ms
            // tick would re-bootstrap the full QLNet curve set from scratch
            if (_curveCache.TryGetValue(key, out var hit)
                && (hit.version == v || (DateTime.UtcNow - hit.builtUtc).TotalMilliseconds < 1000))
            {
                // QLNet evaluation date is process-global: another currency's build may have moved it,
                // and pricing off a cache-hit with the wrong date silently shifts every NPV/DV01
                Settings.setEvaluationDate(hit.curves.AsOf);
                return hit.curves;
            }
            var asOf = AdjustedToday(cfg);
            var curves = CurveBuilder.Build(cfg, src, Snapshot, asOf, null, ExternalDiscountFor(cfg));
            _curveCache[key] = (v, DateTime.UtcNow, curves);
            return curves;
        }

        /// <summary>USD SOFR discount handle for USD-settled ND curves ("discounting": "USD-OIS").</summary>
        internal QLNet.Handle<QLNet.YieldTermStructure>? ExternalDiscountFor(CurrencyConfig cfg)
        {
            if (!cfg.Discounting.Equals("USD-OIS", StringComparison.OrdinalIgnoreCase)
                || cfg.Ccy.Equals("USD", StringComparison.OrdinalIgnoreCase))
                return null;
            var usd = GetCurvesUnlocked(Configs.Get("USD"), SourceFor("USD"));
            return usd.DiscountHandle;
        }

        /// <summary>The (config, source) of the external discount ccy — lets the par ladder bump the
        /// USD OIS quotes a CLP/COP trade discounts on. Null when the ccy discounts itself.</summary>
        internal (CurrencyConfig cfg, string source)? DiscountCcyFor(CurrencyConfig cfg) =>
            cfg.Discounting.Equals("USD-OIS", StringComparison.OrdinalIgnoreCase)
            && !cfg.Ccy.Equals("USD", StringComparison.OrdinalIgnoreCase)
                ? (Configs.Get("USD"), SourceFor("USD"))
                : null;

        /// <summary>Curve tickers incl. the USD discount curve when the ccy is USD-OIS discounted.</summary>
        public IEnumerable<string> TickersWithDiscount(CurrencyConfig cfg, string source)
        {
            var set = new HashSet<string>(TickersFor(cfg, source), StringComparer.OrdinalIgnoreCase);
            if (cfg.Discounting.Equals("USD-OIS", StringComparison.OrdinalIgnoreCase)
                && !cfg.Ccy.Equals("USD", StringComparison.OrdinalIgnoreCase))
                foreach (var t in TickersFor(Configs.Get("USD"), SourceFor("USD"))) set.Add(t);
            return set;
        }

        /// <summary>Sampled zero, 1m-forward and annual (Ny1y) forward curves for charting.</summary>
        public (List<(double years, double zeroPct)> zeros, List<(double years, double fwdPct)> fwds,
                List<(double years, double fwd1yPct)> annual)
            SampleCurve(string ccy, string curveName /* OIS|IRS */, double maxYears = 30)
        {
            lock (_gate)
            {
                var curves = GetCurvesUnlocked(Configs.Get(ccy), SourceFor(ccy));
                var ts = curveName == "IRS" ? (curves.Irs ?? curves.Ois) : (curves.Ois ?? curves.Irs);
                if (ts == null) throw new InvalidOperationException($"{ccy}: no curve");
                // never chart past the SAMPLED curve's last node — extrapolation is not information
                double maxQuoted = (ts.maxDate() - curves.AsOf) / 365.25;
                maxYears = Math.Min(maxYears, Math.Max(1.0, maxQuoted));
                var dc = new Actual365Fixed();
                var zeros = new List<(double, double)>();
                var fwds = new List<(double, double)>();
                var annual = new List<(double, double)>();
                var asOf = curves.AsOf;
                for (double t = 1.0 / 12; t <= maxYears - 1.0 / 12 + 1e-9; t += 1.0 / 12)
                {
                    var d = asOf + new Period((int)Math.Round(t * 12), TimeUnit.Months);
                    double z = ts.zeroRate(d, dc, Compounding.Continuous, Frequency.Annual).value();
                    var d2 = d + new Period(1, TimeUnit.Months);
                    double f = ts.forwardRate(d, d2, dc, Compounding.Simple, Frequency.Annual).value();
                    zeros.Add((t, z * 100));
                    fwds.Add((t, f * 100));
                }
                // annual forward strip: 1y1y, 2y1y ... (maxYears-1)y1y
                for (int a = 1; a <= (int)maxYears - 1; a++)
                {
                    var d = asOf + new Period(a, TimeUnit.Years);
                    var d2 = d + new Period(1, TimeUnit.Years);
                    double f = ts.forwardRate(d, d2, dc, Compounding.Simple, Frequency.Annual).value();
                    annual.Add((a, f * 100));
                }
                return (zeros, fwds, annual);
            }
        }
    }
}
