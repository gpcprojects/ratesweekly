using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Dates;
using RateDesk.Core.QL;
using RateDesk.Core.Trades;

namespace RateDesk.Core.Pricing
{
    public sealed class CashflowRow
    {
        public string Leg { get; init; } = "";
        public Date PayDate { get; init; } = new Date();
        public Date AccrualStart { get; init; } = new Date();
        public Date AccrualEnd { get; init; } = new Date();
        public double RatePct { get; init; }
        public double Amount { get; init; }
        public double Df { get; init; }
        public double Pv { get; init; }
    }

    public sealed class LadderPoint
    {
        public string Curve { get; init; } = "";
        public string Label { get; init; } = "";
        public string Ticker { get; init; } = "";
        public double MarketRatePct { get; init; }
        public double Dv01 { get; init; }
    }

    public sealed class PriceResult
    {
        public TradeSpec Spec { get; init; } = new();
        public CurrencyConfig Cfg { get; init; } = new();
        public Date AsOf { get; init; } = new Date();
        public Date Effective { get; init; } = new Date();
        public Date Maturity { get; init; } = new Date();
        public string ProductUsed { get; init; } = "";
        public string ConventionSummary { get; init; } = "";
        public string Source { get; init; } = "";

        public double ParRatePct { get; init; }
        public double? TradedRatePct { get; init; }
        public double Npv { get; init; }
        /// <summary>PV of 1bp on the fixed leg (annuity risk), absolute.</summary>
        public double Annuity01 { get; init; }
        public double LadderTotalDv01 { get; set; }
        public List<LadderPoint> Ladder { get; } = new();
        /// <summary>Forward-space ladder (segment bumps): where on the FORWARD curve the risk sits.</summary>
        public List<LadderPoint> FwdLadder { get; } = new();
        public double FwdLadderTotalDv01 { get; set; }
        public List<CashflowRow> Cashflows { get; } = new();
        /// <summary>Carry+roll to horizon in bp of rate (positive = trade rolls in receiver's favour).</summary>
        public List<KeyValuePair<string, double>> CarryRollBp { get; } = new();
        public TimeSpan Elapsed { get; set; }
        public List<string> Warnings { get; } = new();
    }

    public static class Pricer
    {
        /// <summary>Resolve product for a spec (Default -> currency default; guards for missing configs).</summary>
        public static ProductKind ResolveProduct(TradeSpec spec, CurrencyConfig cfg)
        {
            var p = spec.Product;
            if (p == ProductKind.Default)
                p = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) ? ProductKind.IRS : ProductKind.OIS;
            if (p == ProductKind.OIS && cfg.Ois == null) p = ProductKind.IRS;
            if ((p == ProductKind.IRS || p == ProductKind.FRA) && cfg.Irs == null) p = ProductKind.OIS;
            return p;
        }

        public static PriceResult Price(TradeSpec spec, CurveSet curves)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cfg = curves.Cfg;
            var cal = curves.Cal;
            var asOf = curves.AsOf;
            var product = ResolveProduct(spec, cfg);
            if (product == ProductKind.OIS && curves.Ois == null)
                throw new InvalidOperationException(
                    $"{cfg.Ccy}: no live OIS quotes on {curves.Source} — OIS curve not built.");

            var (swap, effective, maturity, convSummary) = BuildTrade(spec, product, curves, spec.FixedRate ?? 0.0);
            // includeSettlementDateFlows:false and an explicit npv/settlement date so a SEASONED swap's
            // already-PAID coupons are dropped instead of discounted to a negative time
            swap.setPricingEngine(new DiscountingSwapEngine(
                curves.DiscountHandleFor(product), false, curves.AsOf, curves.AsOf));

            double parRate = FairRate(swap);

            // if no traded rate, re-build at par so NPV=0 and cashflows show the par coupon
            double tradedRate = spec.FixedRate ?? parRate;
            if (spec.FixedRate == null)
            {
                (swap, _, _, _) = BuildTrade(spec, product, curves, parRate);
                swap.setPricingEngine(new DiscountingSwapEngine(
                    curves.DiscountHandleFor(product), false, curves.AsOf, curves.AsOf));
            }

            double npv = swap.NPV();
            double annuity01 = Math.Abs(Nz(swap.legBPS(0)));

            var result = new PriceResult
            {
                Spec = spec,
                Cfg = cfg,
                AsOf = asOf,
                Effective = effective,
                Maturity = maturity,
                ProductUsed = product.ToString(),
                ConventionSummary = convSummary,
                Source = curves.Source,
                ParRatePct = parRate * 100.0,
                TradedRatePct = spec.FixedRate.HasValue ? spec.FixedRate.Value * 100.0 : null,
                Npv = npv,
                Annuity01 = annuity01,
            };
            result.Warnings.AddRange(curves.Warnings);

            ExtractCashflows(swap, curves, product, result.Cashflows);
            ComputeCarryRoll(spec, product, curves, parRate, maturity, result.CarryRollBp);

            sw.Stop();
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // ---------- trade construction ----------

        public static (Swap swap, Date effective, Date maturity, string summary) BuildTrade(
            TradeSpec spec, ProductKind product, CurveSet curves, double fixedRate)
        {
            var cfg = curves.Cfg;
            var cal = curves.Cal;
            var asOf = curves.AsOf;

            if (product == ProductKind.FRA)
            {
                if (cfg.Irs == null) throw new InvalidOperationException($"{cfg.Ccy}: no IBOR conventions for FRA");

                // IMM-dated contract ("sek u26 fra"): the period starts on the IMM date and runs one
                // index tenor, so there are no AxB months to read. Built as a real single-period swap
                // rather than a bare forwardRate() off the curve, so NPV/DV01/cashflows all work.
                if (spec.StartKind == StartKind.Imm && spec.ImmDate != null
                    && (spec.FraStartMonths == null || spec.FraEndMonths == null))
                {
                    // the index period comes from the STRIP config, not the swap leg: DKK quotes a 3M
                    // IMM strip while its only IRS leg is 6M CIBOR
                    var stripTenor = Curves.CurveBuilder.ImmFraIndexTenor(cfg);
                    var idxTenor = spec.FloatTenorOverride
                        ?? (stripTenor != null ? TenorUtil.Parse(stripTenor) : new Period(3, TimeUnit.Months));
                    var legI = SwapBuilder.SelectIrsLeg(cfg.Irs, idxTenor, idxTenor);
                    double reqYears = (spec.ImmDate.serialNumber() - asOf.serialNumber()) / 365.0
                                      + TenorUtil.ApproxMonths(idxTenor) / 12.0;
                    var (projI, _) = curves.ProjectionFor(legI.FloatTenor, reqYears);
                    var idxI = SwapBuilder.MakeIborIndex(cfg, legI, cal, projI, idxTenor);
                    var fraI = SwapBuilder.BuildFraAt(cfg.Irs, legI, cal, spec.ImmDate, idxTenor,
                        fixedRate, spec.Notional, spec.PayFixed, idxI);
                    return (fraI, fraI.startDate(), fraI.maturityDate(),
                        $"FRA {spec.ImmCode} ({spec.ImmDate}) on {legI.FloatIndex}{legI.FloatTenor}, {legI.FloatDcc}");
                }

                int s = spec.FraStartMonths ?? throw new InvalidOperationException("FRA needs AxB months");
                int e = spec.FraEndMonths ?? throw new InvalidOperationException("FRA needs AxB months");
                var tenorP = new Period(e - s, TimeUnit.Months);
                var legF = SwapBuilder.SelectIrsLeg(cfg.Irs, tenorP, spec.FloatTenorOverride ?? tenorP);
                var (projF, _) = curves.ProjectionFor(legF.FloatTenor, (e + 1) / 12.0);
                var idxF = SwapBuilder.MakeIborIndex(cfg, legF, cal, projF, TenorUtil.Parse(legF.FloatTenor));
                var fra = SwapBuilder.BuildFra(cfg, cfg.Irs, legF, cal, asOf, s, e,
                    fixedRate, spec.Notional, spec.PayFixed, idxF);
                return (fra, fra.startDate(), fra.maturityDate(),
                    $"FRA {s}x{e} on {legF.FloatIndex}{legF.FloatTenor}, {legF.FloatDcc}");
            }

            var effective = SwapBuilder.EffectiveDate(spec, cfg, cal, asOf);
            var tenor = spec.Tenor ?? throw new InvalidOperationException("No tenor");
            var maturityUnadj = SwapBuilder.MaturityDate(effective, tenor);

            if (product == ProductKind.OIS)
            {
                var ois = cfg.Ois ?? throw new InvalidOperationException($"{cfg.Ccy}: no OIS conventions");
                var projection = curves.OisProjectionHandle;
                // seasoned trade: ask for fixings back to the effective date so the elapsed accrual prices
                var index = SwapBuilder.MakeOvernightIndex(cfg, ois, cal, projection,
                    effective < asOf ? effective : null);
                var swap = SwapBuilder.BuildOis(cfg, ois, cal, effective, maturityUnadj, tenor,
                    fixedRate, spec.Notional, spec.PayFixed, index);
                string summary = $"OIS vs {ois.IndexName}: fixed {ois.FixedFreq} {ois.FixedDcc}, pay lag {ois.PayLag}d, spot T+{cfg.SpotLag}";
                return (swap, swap.startDate(), swap.maturityDate(), summary);
            }
            else
            {
                var irs = cfg.Irs ?? throw new InvalidOperationException($"{cfg.Ccy}: no IRS conventions");
                var leg = SwapBuilder.SelectIrsLeg(irs, tenor, spec.FloatTenorOverride);
                double requiredYears = (maturityUnadj - curves.AsOf) / 365.25;
                var (proj, usedBand) = curves.ProjectionFor(leg.FloatTenor, requiredYears);
                string note = "";

                // Beyond a short band's quoted ladder there is no market in that index: unless the user
                // FORCED the index (qq/ss), switch the leg to the full-term convention (FWCM-consistent).
                var defaultLeg = irs.Legs[^1];
                if (!usedBand && spec.FloatTenorOverride == null
                    && !leg.FloatTenor.Equals(defaultLeg.FloatTenor, StringComparison.OrdinalIgnoreCase))
                {
                    note = $" · {leg.FloatIndex}{leg.FloatTenor} unquoted at this horizon (ladder ends " +
                           $"{(curves.BandMaxYears.TryGetValue(leg.FloatTenor, out var my) ? my.ToString("0.#") : "?")}y) — " +
                           $"priced {defaultLeg.FixedFreq}/{defaultLeg.FloatTenor} per the full-term market";
                    leg = defaultLeg;
                    (proj, _) = curves.ProjectionFor(leg.FloatTenor, requiredYears);
                }
                else if (!usedBand && curves.IrsByBand.Count > 1)
                {
                    note = $" · projected off full-term curve ({leg.FloatTenor} ladder ends " +
                           $"{(curves.BandMaxYears.TryGetValue(leg.FloatTenor, out var my2) ? my2.ToString("0.#") : "?")}y)";
                }

                var index = SwapBuilder.MakeIborIndex(cfg, leg, cal, proj);
                var swap = SwapBuilder.BuildIrs(cfg, irs, leg, cal, effective, maturityUnadj, tenor,
                    fixedRate, spec.Notional, spec.PayFixed, index);
                string summary = $"IRS: fixed {leg.FixedFreq} {leg.FixedDcc} vs {leg.FloatIndex}{leg.FloatTenor} " +
                                 $"({leg.FloatFreq} {leg.FloatDcc}), spot T+{cfg.SpotLag}" + note;
                return (swap, swap.startDate(), swap.maturityDate(), summary);
            }
        }

        private static double Nz(double? x) => x ?? double.NaN;

        public static double FairRate(Swap swap) => swap switch
        {
            VanillaSwap v => Nz(v.fairRate()),
            OvernightIndexedSwap o => Nz(o.fairRate()),
            _ => throw new InvalidOperationException("Unknown swap type"),
        };

        // ---------- outputs ----------

        private static void ExtractCashflows(Swap swap, CurveSet curves, ProductKind product, List<CashflowRow> rows)
        {
            var disc = curves.DiscountHandleFor(product).currentLink();
            for (int legIx = 0; legIx < 2; legIx++)
            {
                var legName = LegName(swap, legIx);
                foreach (var cf in swap.leg(legIx))
                {
                    if (cf is Coupon c)
                    {
                        // A SEASONED swap has coupons that have already PAID. Discounting one asks the curve
                        // for a negative time and throws, which is what refused any trade old enough to have
                        // made a payment. They are shown with df=0/pv=0: the row is history, not a mark.
                        bool paid = c.date() <= curves.AsOf;
                        double df = paid ? 0.0 : disc.discount(c.date());
                        rows.Add(new CashflowRow
                        {
                            Leg = legName,
                            PayDate = c.date(),
                            AccrualStart = c.accrualStartDate(),
                            AccrualEnd = c.accrualEndDate(),
                            RatePct = SafeRate(c) * 100.0,
                            Amount = c.amount(),
                            Df = df,
                            Pv = c.amount() * df,
                        });
                    }
                }
            }
            rows.Sort((a, b) => a.PayDate != b.PayDate
                ? a.PayDate.CompareTo(b.PayDate)
                : string.CompareOrdinal(a.Leg, b.Leg));
        }

        private static double SafeRate(Coupon c)
        {
            try { return Nz(c.rate()); }
            catch { return double.NaN; }
        }

        private static string LegName(Swap swap, int ix) => swap switch
        {
            VanillaSwap => ix == 0 ? "Fixed" : "Float",
            OvernightIndexedSwap => ix == 0 ? "Fixed" : "OIS Float",
            _ => $"Leg{ix}",
        };

        /// <summary>Public carry+roll: par now minus forward-start par to the same maturity, per horizon (bp).</summary>
        public static List<KeyValuePair<string, double>> CarryRoll(TradeSpec spec, ProductKind product,
            CurveSet curves, double parNow, Date maturity)
        {
            var list = new List<KeyValuePair<string, double>>();
            ComputeCarryRoll(spec, product, curves, parNow, maturity, list);
            return list;
        }

        /// <summary>Static-curve carry+roll: at horizon h the swap's remaining life (E, M) prices like
        /// today's (E-h, M-h) — slide the WHOLE structure back, clamping the start at spot.
        /// Positive = the position rolls in the receiver's favour.</summary>
        private static void ComputeCarryRoll(TradeSpec spec, ProductKind product, CurveSet curves,
            double parNow, Date maturity, List<KeyValuePair<string, double>> outList)
        {
            if (product == ProductKind.FRA) return;
            var cal = curves.Cal;
            var spot = SwapBuilder.SpotDate(curves.Cfg, cal, curves.AsOf);
            var effective = SwapBuilder.EffectiveDate(spec, curves.Cfg, cal, curves.AsOf);

            // 9M is here so the profile has a point in every quarter: WHEN a year of roll lands is the
            // question ("all of it by 3m" vs "only in the 9m-1y leg"), and 6M->1Y alone cannot show it.
            // The roll-destination history overlays read their shape straight off these values.
            foreach (var (label, months) in new[] { ("1M", 1), ("3M", 3), ("6M", 6), ("9M", 9), ("1Y", 12) })
            {
                try
                {
                    var h = new Period(months, TimeUnit.Months);
                    var newEnd = maturity - h;
                    var newStart = effective - h;
                    if (newStart < spot) newStart = spot;
                    if (newEnd <= newStart) break;

                    var rolled = new TradeSpec
                    {
                        Ccy = spec.Ccy,
                        Product = spec.Product,
                        StartKind = StartKind.Date,
                        ExplicitStart = newStart,
                        Tenor = new Period(newEnd - newStart, TimeUnit.Days),
                        Notional = spec.Notional,
                        PayFixed = spec.PayFixed,
                        FloatTenorOverride = spec.FloatTenorOverride,
                    };
                    var (rolledSwap, _, _, _) = BuildTrade(rolled, product, curves, 0.0);
                    rolledSwap.setPricingEngine(new DiscountingSwapEngine(curves.DiscountHandleFor(product)));
                    double parRolled = FairRate(rolledSwap);
                    outList.Add(new KeyValuePair<string, double>(label, (parNow - parRolled) * 1e4));
                }
                catch
                {
                    // horizon beyond trade life etc. -- skip
                }
            }
        }
    }
}
