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

        /// <summary>Par curve from <paramref name="minYears"/> out, at asOf / -1w / -1m.</summary>
        public static CurveTriple ParCurve(
            CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf, double minYears = 1.0)
        {
            var pillars = Pillars(cfg, src)
                .Where(p => p.Natural && p.Months >= minYears * 12.0 - 0.5)
                .OrderBy(p => p.Months)
                .ToList();

            var res = new CurveTriple { Title = $"{cfg.Ccy} par curve", AsOf = asOf };
            foreach (var p in pillars)
            {
                double years = p.Months / 12.0;
                Add(res.Today, store, p.Ticker, asOf, years, p.Label);
                Add(res.Week, store, p.Ticker, asOf.AddDays(-WeekDays), years, p.Label);
                Add(res.Month, store, p.Ticker, asOf.AddDays(-MonthDays), years, p.Label);
            }
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

            static double? Interp(List<CurvePoint> pts, double years)
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
}
