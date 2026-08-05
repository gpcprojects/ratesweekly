using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>The desk's forward ladder — spot 1y plus the quoted forward grid out to 30y20y.
    ///
    /// These are QUOTED Bloomberg securities (FWCM `{id}FS {start}{tenor} BLC Curncy`, or the
    /// year-pair families EUSA/NDFS/SKFS/SAFS/KWFS), not curve-derived points. That is deliberate:
    /// RatesWeekly holds a history store and a renderer, not a bootstrapper, so a derived forward
    /// would need the whole pricing stack re-run per date. The quoted point also gives an exact
    /// close-to-close 1w/1m change on one instrument rather than a difference of two rebuilds.
    ///
    /// The BLC qualifier is mandatory — without it these resolve by name with no price and no
    /// history, which is what once made a sweep wrongly conclude a family was dead.</summary>
    public static class ForwardLadder
    {
        /// <summary>The ladder the desk asked for. The leading entry is the SPOT 1y (no forward
        /// start); the rest are start × tenor. Verified live against the terminal 2026-08-05.</summary>
        public static readonly (int StartY, int TenorY, string Label)[] Grid =
        {
            (0,  1,  "1y"),
            (1,  1,  "1y1y"),
            (2,  1,  "2y1y"),
            (3,  1,  "3y1y"),
            (4,  1,  "4y1y"),
            (5,  2,  "5y2y"),
            (7,  3,  "7y3y"),
            (10, 2,  "10y2y"),
            (12, 3,  "12y3y"),
            (15, 5,  "15y5y"),
            (20, 10, "20y10y"),
            (30, 20, "30y20y"),
        };

        /// <summary>Forward id + style for the currency's screen product (IRS where that is the
        /// default and it carries an id, else OIS). Empty id ⇒ this currency quotes no forwards.</summary>
        public static (string Id, FwdTickerStyle Style) IdFor(CurrencyConfig cfg)
        {
            bool preferIrs = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase)
                             && cfg.Irs != null && cfg.Irs.FwdCurveId.Length > 0;
            if (preferIrs)
                return (cfg.Irs!.FwdCurveId, ForwardTicker.Parse(cfg.Irs.FwdTickerStyle));
            if (cfg.Ois != null && cfg.Ois.FwdCurveId.Length > 0)
                return (cfg.Ois.FwdCurveId, ForwardTicker.Parse(cfg.Ois.FwdTickerStyle));
            if (cfg.Irs != null && cfg.Irs.FwdCurveId.Length > 0)
                return (cfg.Irs.FwdCurveId, ForwardTicker.Parse(cfg.Irs.FwdTickerStyle));
            return ("", FwdTickerStyle.Fwcm);
        }

        /// <summary>Security for one grid point, or null when the family cannot express it.
        /// The spot leg (start 0) has no forward security — it comes off the par ladder.</summary>
        public static string? TickerFor(CurrencyConfig cfg, int startY, int tenorY)
        {
            if (startY <= 0) return null;
            var (id, style) = IdFor(cfg);
            if (id.Length == 0) return null;
            return ForwardTicker.Exact(id, style,
                new QLNet.Period(startY, QLNet.TimeUnit.Years),
                new QLNet.Period(tenorY, QLNet.TimeUnit.Years));
        }

        public static IEnumerable<string> Tickers(CurrencyConfig cfg)
        {
            foreach (var (s, t, _) in Grid)
                if (TickerFor(cfg, s, t) is { } tk)
                    yield return tk;
        }

        /// <summary>The ladder at asOf / -1w / -1m. X is the ladder INDEX (evenly spaced), not a
        /// year — these points are not equally spaced in time and plotting them against years would
        /// bunch the front end into illegibility. Labels carry the real meaning.</summary>
        public static LadderTriple Build(
            CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf, CurveTriple par)
        {
            var res = new LadderTriple { Title = $"{cfg.Ccy} forward ladder", AsOf = asOf };
            var (id, _) = IdFor(cfg);
            bool derived = false;

            for (int i = 0; i < Grid.Length; i++)
            {
                var (sy, ty, label) = Grid[i];
                res.Labels.Add(label);

                if (sy == 0)
                {
                    // spot leg: read the par ladder rather than inventing a security
                    AddFrom(par.Today, res.Today, i, ty);
                    AddFrom(par.Week, res.Week, i, ty);
                    AddFrom(par.Month, res.Month, i, ty);
                    continue;
                }

                if (TickerFor(cfg, sy, ty) is { } tk)
                {
                    Add(res.Today, store, tk, asOf, i);
                    Add(res.Week, store, tk, asOf.AddDays(-WeeklyCurves.WeekDays), i);
                    Add(res.Month, store, tk, asOf.AddDays(-WeeklyCurves.MonthDays), i);
                }
                else if (par.HasData)
                {
                    // no quoted family (CLP/HKD/THB/BRL): fall back to the par-approximation and SAY SO
                    derived = true;
                    AddApprox(par.Today, res.Today, i, sy, ty);
                    AddApprox(par.Week, res.Week, i, sy, ty);
                    AddApprox(par.Month, res.Month, i, sy, ty);
                }
            }

            res.Notes.Add(id.Length == 0
                ? "no quoted forward family for this currency — points derived from the par ladder"
                : $"quoted forwards ({id})" + (derived ? "; some points derived from the par ladder" : ""));
            return res;

            static void Add(List<CurvePoint> into, HistoryStore s, string tk, DateTime d, int i)
            {
                if (s.ValueAsOf(tk, d) is { } v) into.Add(new CurvePoint(i, v, ""));
            }
            static void AddFrom(List<CurvePoint> src, List<CurvePoint> into, int i, int years)
            {
                var p = src.FirstOrDefault(q => Math.Abs(q.Years - years) < 0.1);
                if (p.Label is not null) into.Add(new CurvePoint(i, p.RatePct, ""));
            }
            static void AddApprox(List<CurvePoint> src, List<CurvePoint> into, int i, int sy, int ty)
            {
                if (WeeklyCurves.InterpAt(src, sy) is not { } ra ||
                    WeeklyCurves.InterpAt(src, sy + ty) is not { } rb) return;
                double a = sy, b = sy + ty;
                into.Add(new CurvePoint(i, (b * rb - a * ra) / (b - a), ""));
            }
        }
    }

    /// <summary>A ladder whose x-axis is an index into <see cref="Labels"/> rather than a year.</summary>
    public sealed class LadderTriple
    {
        public required string Title { get; init; }
        public required DateTime AsOf { get; init; }
        public List<string> Labels { get; init; } = new();
        public List<CurvePoint> Today { get; init; } = new();
        public List<CurvePoint> Week { get; init; } = new();
        public List<CurvePoint> Month { get; init; } = new();
        public List<string> Notes { get; init; } = new();
        public bool HasData => Today.Count > 0;
    }
}
