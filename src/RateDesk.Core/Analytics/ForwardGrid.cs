using System;
using System.Collections.Generic;
using QLNet;
using RateDesk.Core.Curves;
using RateDesk.Core.Dates;
using RateDesk.Core.Pricing;
using RateDesk.Core.Trades;

namespace RateDesk.Core.Analytics
{
    public sealed class FwdCell
    {
        public string Start { get; init; } = "";   // "0Y","1Y",...
        public string Tenor { get; init; } = "";    // "5Y"
        public double RatePct { get; init; }
        public bool Ok { get; init; }
    }

    public sealed class ForwardGridResult
    {
        public string Ccy { get; init; } = "";
        public string Product { get; init; } = "";
        public string[] Starts { get; init; } = Array.Empty<string>();
        public string[] Tenors { get; init; } = Array.Empty<string>();
        public List<FwdCell> Cells { get; } = new();
        public double BuildMs { get; set; }
    }

    /// <summary>Strips forward-starting par-swap rates from a bootstrapped curve — the live
    /// forward-points matrix a rates desk watches. Pure curve evaluation, no extra Bloomberg calls.</summary>
    public static class ForwardGrid
    {
        public static readonly string[] DefaultStarts =
            { "0Y", "3M", "6M", "1Y", "2Y", "3Y", "4Y", "5Y", "7Y", "10Y", "15Y", "20Y" };
        public static readonly string[] DefaultTenors =
            { "1Y", "2Y", "3Y", "5Y", "7Y", "10Y", "15Y", "20Y", "30Y" };

        public static ForwardGridResult Build(CurveSet curves, ProductKind product,
            string[]? starts = null, string[]? tenors = null, double maxYears = 50)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            starts ??= DefaultStarts;
            tenors ??= DefaultTenors;
            var res = new ForwardGridResult
            {
                Ccy = curves.Ccy, Product = product.ToString(),
                Starts = starts, Tenors = tenors,
            };

            foreach (var st in starts)
            {
                var startP = st == "0Y" ? null : TenorUtil.Parse(st);
                double startYears = st == "0Y" ? 0 : TenorUtil.ApproxMonths(startP!) / 12.0;
                foreach (var tn in tenors)
                {
                    double tenorYears = TenorUtil.ApproxMonths(TenorUtil.Parse(tn)) / 12.0;
                    bool ok = false;
                    double rate = double.NaN;
                    if (startYears + tenorYears <= maxYears + 1e-6)
                    {
                        try
                        {
                            var spec = new TradeSpec
                            {
                                Ccy = curves.Ccy,
                                Product = product,
                                StartKind = startP == null ? StartKind.Spot : StartKind.Forward,
                                ForwardStart = startP,
                                Tenor = TenorUtil.Parse(tn),
                                Notional = 1_000_000,
                            };
                            var (swap, _, _, _) = Pricer.BuildTrade(spec, product, curves, 0.0);
                            swap.setPricingEngine(new DiscountingSwapEngine(curves.DiscountHandleFor(product)));
                            rate = Pricer.FairRate(swap) * 100.0;
                            ok = !double.IsNaN(rate) && Math.Abs(rate) < 100;
                        }
                        catch { ok = false; }
                    }
                    res.Cells.Add(new FwdCell { Start = st, Tenor = tn, RatePct = rate, Ok = ok });
                }
            }
            sw.Stop();
            res.BuildMs = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        /// <summary>Single forward par rate (%) for start/tenor periods.
        /// idxOverride forces a float-index tenor (e.g. FWCM's uniform-convention grid).</summary>
        public static double ForwardRate(CurveSet curves, ProductKind product, Period? start, Period tenor,
            Period? idxOverride = null)
        {
            var spec = new TradeSpec
            {
                Ccy = curves.Ccy,
                Product = product,
                StartKind = start == null ? StartKind.Spot : StartKind.Forward,
                ForwardStart = start,
                Tenor = tenor,
                Notional = 1_000_000,
                FloatTenorOverride = idxOverride,
            };
            var (swap, _, _, _) = Pricer.BuildTrade(spec, product, curves, 0.0);
            swap.setPricingEngine(new DiscountingSwapEngine(curves.DiscountHandleFor(product)));
            return Pricer.FairRate(swap) * 100.0;
        }
    }
}
