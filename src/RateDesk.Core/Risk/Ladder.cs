using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.Trades;

namespace RateDesk.Core.Risk
{
    /// <summary>Bucketed delta: bump each curve input +1bp, re-bootstrap, reprice.
    /// Market-quote (par) risk, the way desks read a ladder.</summary>
    public static class Ladder
    {
        public static void Compute(TradeSpec spec, CurrencyConfig cfg, string source,
            RatesSnapshot snap, Date asOf, PriceResult result,
            QLNet.Handle<YieldTermStructure>? externalDiscount = null,
            (CurrencyConfig cfg, string source)? discountCcy = null)
        {
            var baseCurves = CurveBuilder.Build(cfg, source, snap, asOf, null, externalDiscount);
            var product = Pricer.ResolveProduct(spec, cfg);
            double rate = (result.TradedRatePct ?? result.ParRatePct) / 100.0;

            double baseNpv = Reprice(spec, product, baseCurves, rate);
            const double bumpSize = 1e-4;

            bool bothFamilies = cfg.Discounting.Equals("OIS", StringComparison.OrdinalIgnoreCase);
            foreach (var pillar in baseCurves.Pillars)
            {
                // OIS trades project AND discount on the OIS curve — IRS pillars can never move them;
                // for IRS/FRA, SELF/USD-discounted: the OIS family cannot move this trade — skip zero rows
                if (product == ProductKind.OIS
                        ? pillar.CurveName != "OIS"
                        : !bothFamilies && pillar.CurveName.StartsWith("OIS", StringComparison.OrdinalIgnoreCase))
                    continue;
                var bumped = CurveBuilder.Build(cfg, source, snap, asOf,
                    (ticker, r) => ticker.Equals(pillar.Ticker, StringComparison.OrdinalIgnoreCase) ? r + bumpSize : r,
                    externalDiscount);
                double npv = Reprice(spec, product, bumped, rate);
                result.Ladder.Add(new LadderPoint
                {
                    Curve = pillar.CurveName,
                    Label = pillar.Label,
                    Ticker = pillar.Ticker,
                    MarketRatePct = pillar.MarketRatePct,
                    Dv01 = npv - baseNpv,
                });
            }

            // USD-OIS-discounted markets (CLP/COP): the discount curve is a real risk the desk hedges,
            // and the FORWARD ladder already shifts it — the par ladder must show the same exposure in
            // QUOTE space. Applies to EVERY product here (a CLP OIS discounts on USD too). Keep only
            // rows that actually move the trade (the tail of a 21-pillar strip is noise rows).
            if (discountCcy is { } dc && externalDiscount != null)
            {
                double floor = Math.Max(0.02 * Math.Abs(result.Ladder.Sum(p => p.Dv01)) / Math.Max(1, result.Ladder.Count), 0.01);
                var usdBase = CurveBuilder.Build(dc.cfg, dc.source, snap, asOf, null, null);
                foreach (var pillar in usdBase.Pillars.Where(p => p.CurveName == "OIS"))
                {
                    var usdBumped = CurveBuilder.Build(dc.cfg, dc.source, snap, asOf,
                        (ticker, r) => ticker.Equals(pillar.Ticker, StringComparison.OrdinalIgnoreCase) ? r + bumpSize : r,
                        null);
                    var bumpedSet = CurveBuilder.Build(cfg, source, snap, asOf, null, usdBumped.DiscountHandle);
                    double npv = Reprice(spec, product, bumpedSet, rate);
                    if (Math.Abs(npv - baseNpv) < floor) continue;
                    result.Ladder.Add(new LadderPoint
                    {
                        Curve = $"{dc.cfg.Ccy} DISC",
                        Label = pillar.Label,
                        Ticker = pillar.Ticker,
                        MarketRatePct = pillar.MarketRatePct,
                        Dv01 = npv - baseNpv,
                    });
                }
            }
            result.LadderTotalDv01 = result.Ladder.Sum(p => p.Dv01);
        }

        private static double Reprice(TradeSpec spec, ProductKind product, CurveSet curves, double fixedRate)
        {
            var (swap, _, _, _) = Pricer.BuildTrade(spec, product, curves, fixedRate);
            swap.setPricingEngine(new DiscountingSwapEngine(curves.DiscountHandleFor(product)));
            return swap.NPV();
        }

        // ---------- FORWARD-space ladder ----------

        private static readonly (double A, double B)[] FwdSegs =
        {
            (0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 7), (7, 8), (8, 9), (9, 10),
            (10, 12), (12, 15), (15, 20), (20, 25), (25, 30), (30, 40), (40, 50),
        };

        /// <summary>FORWARD-space ladder: +1bp on the forwards inside each segment [a,b]
        /// (zero-rate shift (min(t,b)−min(t,a))/t applied to every curve in the set), reprice.
        /// The par ladder answers "which QUOTES move my P&L"; this answers "which part of the
        /// FORWARD curve am I long/short" — a 5y5y is par-flat inside 5y but pure 5y→10y here.</summary>
        public static void ComputeForward(TradeSpec spec, CurrencyConfig cfg, string source,
            RatesSnapshot snap, Date asOf, PriceResult result,
            QLNet.Handle<YieldTermStructure>? externalDiscount = null)
        {
            var baseCurves = CurveBuilder.Build(cfg, source, snap, asOf, null, externalDiscount);
            var product = Pricer.ResolveProduct(spec, cfg);
            double rate = (result.TradedRatePct ?? result.ParRatePct) / 100.0;
            const double bump = 1e-4;
            // base NPV on the RESAMPLED (bump=0) curves so zero-interp resampling error cancels
            // exactly against the shifted reprice — otherwise it bleeds ~2% into every bucket
            double baseNpv = Reprice(spec, product, ShiftSet(baseCurves, 0, 0, 0), rate);

            double matY = Math.Max(1.0, (result.Maturity - asOf) / 365.25);
            var proj = product == ProductKind.OIS
                ? (baseCurves.Ois ?? baseCurves.Irs) : (baseCurves.Irs ?? baseCurves.Ois);
            var dc = proj?.dayCounter() ?? new Actual365Fixed();

            foreach (var (a, b) in FwdSegs)
            {
                if (a > matY + 0.01) break;
                var shifted = ShiftSet(baseCurves, a, b, bump);
                double npv = Reprice(spec, product, shifted, rate);
                double mkt = 0;
                try
                {
                    if (proj != null)
                    {
                        var dA = a <= 0 ? proj.referenceDate() + 2 : proj.referenceDate() + (int)Math.Round(a * 365.25);
                        var dB = proj.referenceDate() + (int)Math.Round(b * 365.25);
                        if (dB <= proj.maxDate())
                            mkt = proj.forwardRate(dA, dB, dc, Compounding.Simple, Frequency.Annual).value() * 100.0;
                    }
                }
                catch { /* display-only column */ }
                result.FwdLadder.Add(new LadderPoint
                {
                    Curve = "FWD",
                    Label = a == 0 ? "0x1Y" : $"{a:0}Yx{b - a:0}Y",
                    MarketRatePct = mkt,
                    Dv01 = npv - baseNpv,
                });
            }
            result.FwdLadderTotalDv01 = result.FwdLadder.Sum(p => p.Dv01);
        }

        /// <summary>Clone the curve set with every term structure fwd-segment-bumped identically.</summary>
        private static CurveSet ShiftSet(CurveSet b, double aY, double bY, double bump)
        {
            var s = new CurveSet { Ccy = b.Ccy, Source = b.Source, AsOf = b.AsOf, Cfg = b.Cfg, Cal = b.Cal };
            var done = new Dictionary<YieldTermStructure, YieldTermStructure>();
            YieldTermStructure Sh(YieldTermStructure ts)
            {
                if (!done.TryGetValue(ts, out var r))
                {
                    r = ShiftFwdSegment(ts, aY, bY, bump);
                    done[ts] = r;
                }
                return r;
            }
            if (b.Ois != null) s.Ois = Sh(b.Ois);
            if (b.Irs != null) s.Irs = Sh(b.Irs);
            foreach (var kv in b.IrsByBand) s.IrsByBand[kv.Key] = Sh(kv.Value);
            foreach (var kv in b.BandMaxYears) s.BandMaxYears[kv.Key] = kv.Value;
            var disc = b.DiscountHandle.empty() ? null : b.DiscountHandle.currentLink();
            s.DiscountHandle = new QLNet.Handle<YieldTermStructure>(disc != null ? Sh(disc) : (s.Ois ?? s.Irs)!);
            var oisP = b.OisProjectionHandle.empty() ? null : b.OisProjectionHandle.currentLink();
            s.OisProjectionHandle = new QLNet.Handle<YieldTermStructure>(oisP != null ? Sh(oisP) : (s.Ois ?? s.Irs)!);
            var irsP = b.IrsProjectionHandle.empty() ? null : b.IrsProjectionHandle.currentLink();
            s.IrsProjectionHandle = new QLNet.Handle<YieldTermStructure>(irsP != null ? Sh(irsP) : (s.Irs ?? s.Ois)!);
            return s;
        }

        /// <summary>Sampled-zero clone with the fwd bump folded in: z(t) += bump·(min(t,b)−min(t,a))/t.</summary>
        private static YieldTermStructure ShiftFwdSegment(YieldTermStructure ts, double aY, double bY, double bump)
        {
            var dc = ts.dayCounter();
            var refD = ts.referenceDate();
            double maxY;
            try { maxY = Math.Max(2.0, Math.Min(60.0, dc.yearFraction(refD, ts.maxDate()))); }
            catch { maxY = 60.0; }

            var grid = new List<double>();
            for (double t = 1.0 / 12.0; t < 3.0; t += 1.0 / 12.0) grid.Add(t);
            for (double t = 3.0; t <= maxY + 1e-9; t += 0.25) grid.Add(t);
            if (grid.Count == 0 || grid[^1] < maxY - 1e-9) grid.Add(maxY);

            var dates = new List<Date> { refD };
            var rates = new List<double>();
            foreach (var t in grid)
            {
                var d = refD + (int)Math.Round(t * 365.25);
                if (d <= dates[^1]) continue;
                double z;
                try { z = ts.zeroRate(d, dc, Compounding.Continuous, Frequency.Annual).value(); }
                catch { continue; }
                double shift = bump * Math.Max(0.0, Math.Min(t, bY) - Math.Min(t, aY)) / t;
                dates.Add(d);
                rates.Add(z + shift);
            }
            if (rates.Count == 0) return ts;
            rates.Insert(0, rates[0]); // rate at the reference date itself
            var zc = new InterpolatedZeroCurve<Linear>(dates, rates, dc, new Linear());
            zc.enableExtrapolation();
            return zc;
        }
    }
}
