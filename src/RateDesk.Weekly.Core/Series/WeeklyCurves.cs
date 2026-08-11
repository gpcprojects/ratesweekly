using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.Pricing;

namespace RateDesk.Weekly.Core.Series
{
    public readonly record struct CurvePoint(double Years, double RatePct, string Label);

    /// <summary>A curve at three points in time — the shape every page chart takes.
    /// Older lines can be shorter than Today when a pillar's history starts late.</summary>
    public sealed class CurveTriple
    {
        public required string Title { get; init; }
        public required DateTime AsOf { get; init; }
        public List<CurvePoint> Today { get; init; } = new();
        public List<CurvePoint> Week { get; init; } = new();
        public List<CurvePoint> Month { get; init; } = new();
        public List<string> Notes { get; init; } = new();
        public bool HasData => Today.Count > 0;
    }

    /// <summary>Builds par and forward curves for a currency straight from the history store.
    ///
    /// Band-awareness is not optional: dual-band markets quote TWO families at one tenor (AUD
    /// 4Y-9Y q/q AND s/s, ~26bp apart), so a naive "nearest pillar by tenor" puts a step in the
    /// curve that is quote-family basis, not the market. This mirrors PricingServiceBoards'
    /// screen-family rule — tenor rows read the tenor-rule (natural) band only.</summary>
    public static class WeeklyCurves
    {
        /// <summary>Lookbacks are CALENDAR days, matching Dodgeball's weekly convention; the store
        /// walks back to the last close at or before the target, so weekends resolve to Friday.</summary>
        public const int WeekDays = 7;
        public const int MonthDays = 31;

        private readonly record struct Pillar(double Months, string Ticker, bool Natural, string? Band, string Label);

        /// <summary>The screen-convention pillar ladder for a currency, as config declares it.
        /// DEPO and FRA pillars are excluded: FRAs are forwards, not par levels, and "3X6" does
        /// not even parse as a tenor.</summary>
        private static List<Pillar> Pillars(CurrencyConfig cfg, string src)
        {
            var list = new List<Pillar>();
            bool boardIrs = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null;
            bool multiBand = boardIrs && cfg.Irs!.Legs.Count > 1;
            var curve = boardIrs ? cfg.Irs!.Curve : cfg.Ois?.Curve ?? cfg.Irs?.Curve;

            if (curve != null)
            {
                foreach (var p in curve)
                {
                    if (!p.Enabled) continue;
                    if (p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)) continue;
                    string? band = multiBand ? SwapBuilder.PillarBand(cfg.Irs!, p) : null;
                    var tenor = TenorUtil.Parse(p.Tenor);
                    bool natural = band == null || band.Equals(
                        SwapBuilder.SelectIrsLeg(cfg.Irs!, tenor, null).FloatTenor,
                        StringComparison.OrdinalIgnoreCase);
                    list.Add(new Pillar(TenorUtil.ApproxMonths(tenor),
                        ConfigStore.ResolveTicker(p.Ticker, src), natural, band, p.Tenor));
                }
            }
            else if (cfg.Ladders.Count > 0)
            {
                foreach (var p in cfg.Ladders[0].Pillars)
                {
                    if (!p.Enabled || p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)) continue;
                    var tenor = TenorUtil.Parse(p.Tenor);
                    list.Add(new Pillar(TenorUtil.ApproxMonths(tenor),
                        ConfigStore.ResolveTicker(p.Ticker, ""), true, null, p.Tenor));
                }
            }
            return list;
        }

        /// <summary>The natural-band pillar ladder as (years, ticker, label), for callers that need
        /// the SECURITIES rather than resolved values — the movers scan pulls daily series per
        /// pillar. Same band selection as <see cref="ParCurve"/>, so a dual-band basis can never
        /// enter a movers series either.</summary>
        public static IReadOnlyList<(double Years, string Ticker, string Label)> NaturalPillarLadder(
            CurrencyConfig cfg, string src, double minYears = 1.0)
            => Pillars(cfg, src)
                .Where(p => p.Natural && p.Months >= minYears * 12.0 - 0.5)
                .OrderBy(p => p.Months)
                .Select(p => (p.Months / 12.0, p.Ticker, p.Label))
                .ToList();

        /// <summary>The tenors the desk reads a curve on. Config carries every quoted pillar —
        /// 21 of them for USD, including 6/8/9/11-14Y added to keep forward ends landing on real
        /// quotes — but a weekly is read at a glance, and a table you have to scroll is a table
        /// nobody reads. Interpolation is never used to invent a row here: a tenor absent from the
        /// currency's own ladder simply doesn't appear.</summary>
        public static readonly double[] StandardTenorsY = { 1, 2, 3, 5, 7, 10, 12, 15, 20, 30, 50 };

        /// <summary>Par curve at asOf / -1w / -1m. <paramref name="standardOnly"/> trims to the
        /// display tenors; pass false when the caller needs every quoted pillar (the forward
        /// ladder's par-approximation fallback interpolates off the full ladder, so trimming it
        /// there would cost real accuracy for the currencies that have no quoted forwards).</summary>
        public static CurveTriple ParCurve(
            CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf,
            double minYears = 1.0, bool standardOnly = true)
        {
            var all = Pillars(cfg, src)
                .Where(p => p.Natural && p.Months >= minYears * 12.0 - 0.5);
            var pillars = (standardOnly
                    ? all.Where(p => StandardTenorsY.Any(t => Math.Abs(p.Months / 12.0 - t) < 0.12))
                         .GroupBy(p => StandardTenorsY.First(t => Math.Abs(p.Months / 12.0 - t) < 0.12))
                         .Select(g => g.OrderBy(p => Math.Abs(p.Months / 12.0 - g.Key)).First())
                    : all)
                .OrderBy(p => p.Months)
                .ToList();

            var res = new CurveTriple { Title = $"{cfg.Ccy} par curve", AsOf = asOf };
            bool renamed = false;
            foreach (var p in pillars)
            {
                double years = p.Months / 12.0;
                // Label by the standard tenor, not the raw config tenor, so every currency's rows
                // line up when read across the desk. MXN quotes in 28-DAY PERIODS (13P = 1Y), which
                // is correct for pricing but unreadable in a cross-currency weekly.
                string label = p.Label;
                if (standardOnly && StandardTenorsY.FirstOrDefault(t => Math.Abs(years - t) < 0.12) is var std && std > 0)
                {
                    var pretty = $"{std:0}Y";
                    if (!pretty.Equals(p.Label, StringComparison.OrdinalIgnoreCase)) renamed = true;
                    label = pretty;
                }
                Add(res.Today, store, p.Ticker, asOf, years, label);
                Add(res.Week, store, p.Ticker, asOf.AddDays(-WeekDays), years, label);
                Add(res.Month, store, p.Ticker, asOf.AddDays(-MonthDays), years, label);
            }
            if (renamed)
                res.Notes.Add($"{cfg.Ccy} quotes in 28-day periods (13P = 1Y); rows are labelled by year equivalent");
            if (pillars.Count > 0 && res.Today.Count < pillars.Count)
                res.Notes.Add($"{pillars.Count - res.Today.Count} of {pillars.Count} pillars had no stored close");
            return res;

            static void Add(List<CurvePoint> into, HistoryStore s, string ticker, DateTime d, double y, string lbl)
            {
                if (s.ValueAsOf(ticker, d) is { } v) into.Add(new CurvePoint(y, v, lbl));
            }
        }

        /// <summary>Annual forwards 1y1y .. 9y1y derived from the par ladder:
        /// f(a,b) = (b·r_b − a·r_a) / (b − a), the same par-approximation Dodgeball's history layer
        /// falls back to. Both endpoints come from the SAME curve read, so a dual-band basis can
        /// never be booked into a forward. Endpoints are linearly interpolated between bracketing
        /// pillars and NEVER extrapolated past the last quoted pillar.</summary>
        public static CurveTriple AnnualForwards(CurveTriple par, string ccy, int maxStart = 9)
        {
            var res = new CurveTriple { Title = $"{ccy} annual forwards", AsOf = par.AsOf };
            Fill(par.Today, res.Today);
            Fill(par.Week, res.Week);
            Fill(par.Month, res.Month);
            if (!res.HasData && par.HasData)
                res.Notes.Add("par ladder too short for 1y-forward derivation");
            return res;

            void Fill(List<CurvePoint> src, List<CurvePoint> dst)
            {
                if (src.Count < 2) return;
                for (int a = 1; a <= maxStart; a++)
                {
                    int b = a + 1;
                    if (Interp(src, a) is not { } ra || Interp(src, b) is not { } rb) continue;
                    dst.Add(new CurvePoint(a, (b * rb - a * ra) / (b - a), $"{a}y1y"));
                }
            }

            static double? Interp(List<CurvePoint> pts, double years) => InterpAt(pts, years);
        }

        /// <summary>Linear interpolation along a curve, never extrapolated past the quoted ends.</summary>
        public static double? InterpAt(List<CurvePoint> pts, double years)
        {
            if (pts.Count == 0 || years < pts[0].Years - 1e-9 || years > pts[^1].Years + 1e-9) return null;
            for (int i = 0; i < pts.Count; i++)
            {
                if (Math.Abs(pts[i].Years - years) < 1e-9) return pts[i].RatePct;
                if (pts[i].Years > years)
                {
                    var lo = pts[i - 1]; var hi = pts[i];
                    double w = (years - lo.Years) / (hi.Years - lo.Years);
                    return lo.RatePct + w * (hi.RatePct - lo.RatePct);
                }
            }
            return null;
        }
    }
}
