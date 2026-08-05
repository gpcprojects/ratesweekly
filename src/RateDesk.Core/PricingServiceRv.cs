using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Trades;

namespace RateDesk.Core
{
    /// <summary>One row of the RV screen: a curve structure and how dislocated it is vs its own history.</summary>
    public sealed class RvRow
    {
        public string Structure { get; init; } = "";   // "2s10s" / "2s5s10s" — pasteable into the query bar
        public string Kind { get; init; } = "";        // spread | fly | xccy | box
        public double? NowBp { get; init; }
        /// <summary>Now − 1y mean: the gap the mean-reversion trade is trying to capture.</summary>
        public double? DeltaMeanBp { get; init; }
        public double? Mean1yBp { get; init; }
        public double? Z1y { get; init; }
        public double? Z6m { get; init; }
        public double? Z3m { get; init; }
        public double? Chg1mBp { get; init; }
        public double? Chg3mBp { get; init; }
        public double? Pctl1y { get; init; }
        /// <summary>Change on day in units of daily vol — "a 2σ steepening" is desk language.</summary>
        public double? CoDSig { get; init; }
        /// <summary>3m roll+carry of the structure on an unchanged curve (bp; + = paid to hold LONG).</summary>
        public double? Roll3mBp { get; set; }
        /// <summary>AR(1) mean-reversion half-life, business days (null / >126 → no usable reversion).</summary>
        public double? HlDays { get; init; }
        /// <summary>Expected 3m P&L of the RECEIVED structure: (now − μ)·(1 − φ^63) + roll3m — both
        /// terms receiver-framed (a level above its mean falls back → receiver gains; roll is the
        /// receiver's carry). Positive → receive, negative → pay. TREND rows carry roll only.</summary>
        public double? EPnl3m { get; init; }
        /// <summary>E[PnL]3m ÷ 3m sigma (1y vol × √(63/252)) — expected bp per unit of noise.</summary>
        public double? EShp { get; init; }
        /// <summary>No in-sample mean reversion (φ ≥ 1 or half-life > 126d): the z is a trend, not value.</summary>
        public bool Trend { get; init; }
        /// <summary>XCCY only: which side the dislocation says to trade.</summary>
        public string Dir { get; init; } = "";
        /// <summary>Level beta: slope of the structure on the 10y (spreads) — how directional it is.</summary>
        public double? Beta { get; set; }
        /// <summary>Residual z after removing level (and slope, for flies): rich/cheap CONDITIONAL on
        /// where the curve is — a big |z| with a small |resid z| is just directionality, not value.</summary>
        public double? ResidZ { get; set; }
        /// <summary>Primary rank: |E[Shp]| (TREND rows demoted), tie-broken by |z|.</summary>
        public double Score => (Trend ? 0.0 : Math.Abs(EShp ?? 0) * 10.0)
                               + Math.Abs(Z1y ?? 0) + 0.25 * Math.Abs(Z6m ?? 0);
    }

    public sealed partial class PricingService
    {
        private static readonly int[] RvTenors = { 2, 3, 5, 7, 10, 15, 20, 30 };

        /// <summary>Expected-value block shared by SCAN/XCCY/MAP: half-life-aware 3m convergence +
        /// carry, per the AR(1) fit in the stats. Returns (hl, ePnl, eShp, trend, codSig).</summary>
        private static (double? hl, double? ePnl, double? eShp, bool trend, double? codSig)
            ExpectedValue(SeriesStats s, double? nowBp, double? roll3mBp, double? codBp)
        {
            double? hl = s.HalfLifeDays;
            bool trend = !(s.Ar1Phi is double phi0 && phi0 > 0 && phi0 < 1) || (hl ?? 999) > 126;
            double? ePnl = null, eShp = null;
            if (s.Mean1y is double mu)
            {
                double now = nowBp ?? s.Last;
                // receiver-framed like roll3m: a level ABOVE its mean converging down pays the receiver
                double conv = trend ? 0.0 : (now - mu) * (1 - Math.Pow(s.Ar1Phi!.Value, 63));
                if (roll3mBp is double roll) ePnl = conv + roll;
                else if (!trend) ePnl = conv;
                double sigma3m = (s.RealizedVol1yBp ?? 0) * 0.5; // 1y ann vol × √(63/252)
                if (ePnl.HasValue && sigma3m > 0.5) eShp = ePnl / sigma3m;
            }
            double? codSig = null;
            if (codBp.HasValue && s.RealizedVol1yBp is double v1 && v1 > 1)
                codSig = codBp.Value / (v1 / 15.87); // √252
            return (hl, ePnl, eShp, trend, codSig);
        }

        /// <summary>Scan every standard spread and fly on a currency's headline curve, ranked by
        /// expected risk-adjusted 3m P&L (half-life-aware convergence + carry), |z| as tiebreak.
        /// Histories come from the (cached) BDH pillar series with a fixed ~2y stats window.</summary>
        public List<RvRow> RvScan(string ccy, int maxRows = 60)
        {
            if (History == null) return new List<RvRow>();
            var cfg = Configs.Get(ccy);
            var src = SourceFor(ccy);
            var product = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null
                ? ProductKind.IRS : ProductKind.OIS;
            if (product == ProductKind.OIS && cfg.Ois == null) product = ProductKind.IRS;

            // fixed 2y stats window so the RV screen doesn't move with the chart's lookback setting
            var cutoff = DateTime.Today.AddDays(-750);
            IReadOnlyList<HistPoint> H(string ticker)
            {
                var full = HistoryFilter.Despike(History.GetDaily(ticker, 1825));
                return full.Count == 0 ? full : full.Where(p => p.Date >= cutoff).ToList();
            }

            var wanted = RvTenors.Select(t => ResolvePillarTicker(cfg, product, TenorUtil.Parse($"{t}Y"), src))
                .Where(t => t != null).Select(t => t!).ToList();
            try { History.Prefetch(wanted, 1825); } catch { /* per-ticker fallback */ }

            var legs = new List<(int tenor, IReadOnlyList<HistPoint> hist, double? liveMid, double? prevClose)>();
            foreach (var t in RvTenors)
            {
                var tkr = ResolvePillarTicker(cfg, product, TenorUtil.Parse($"{t}Y"), src);
                if (tkr == null) continue;
                var h = H(tkr);
                if (h.Count < 250) continue; // need a year of data for the z to mean anything
                var q = Snapshot.Get(tkr);
                legs.Add((t, h, q?.Mid, q?.PrevClose));
            }
            if (legs.Count < 3) return new List<RvRow>();

            // factors for conditional rich/cheap: 10y = level, 10y−2y = slope (Litterman-Scheinkman lite)
            var lvlLeg = legs.FirstOrDefault(l => l.tenor == 10).hist ?? legs[^1].hist;
            var slpNear = legs.FirstOrDefault(l => l.tenor == 2).hist;

            // 3m roll+carry from the live pillar grid: structure now minus structure with every leg
            // slid 3m down today's curve (linear interp between quoted tenors)
            var livePts = legs.Where(l => l.liveMid.HasValue)
                .Select(l => ((double)l.tenor, l.liveMid!.Value)).OrderBy(p => p.Item1).ToList();
            double? LiveAt(double t)
            {
                if (livePts.Count < 2) return null;
                if (t <= livePts[0].Item1) return livePts[0].Item2;
                if (t >= livePts[^1].Item1) return livePts[^1].Item2;
                for (int i = 1; i < livePts.Count; i++)
                    if (t <= livePts[i].Item1)
                    {
                        var (x0, y0) = livePts[i - 1];
                        var (x1, y1) = livePts[i];
                        return y0 + (y1 - y0) * (t - x0) / (x1 - x0);
                    }
                return null;
            }
            double? Roll3m(int[] tenors, double[] w)
            {
                double sum = 0;
                for (int i = 0; i < tenors.Length; i++)
                {
                    var now = LiveAt(tenors[i]);
                    var aged = LiveAt(Math.Max(tenors[i] - 0.25, livePts.Count > 0 ? livePts[0].Item1 : 0.25));
                    if (now == null || aged == null) return null;
                    sum += w[i] * (now.Value - aged.Value);
                }
                return sum * 100.0;
            }

            var rows = new List<RvRow>();
            RvRow? Build(string label, string kind, List<IReadOnlyList<HistPoint>> series, double[] w,
                double? now, double? cod, int[] tenors)
            {
                var combined = HistoryFilter.Despike(CombineSeries(series, w, scaleToBp: true),
                    window: 7, k: 4, madFloorPct: 0.5, passes: 3);
                if (combined.Count < 250) return null;
                var s = SeriesStats.Compute(combined, liveLast: now, changeScale: 1.0);
                var roll = Roll3m(tenors, w);
                var (hl, ePnl, eShp, trend, codSig) = ExpectedValue(s, now, roll, cod);
                var row = new RvRow
                {
                    Structure = label, Kind = kind,
                    NowBp = now ?? combined[^1].Value,
                    DeltaMeanBp = s.Mean1y is double m ? (now ?? combined[^1].Value) - m : null,
                    Mean1yBp = s.Mean1y, Z1y = s.ZScore1y, Z6m = s.ZScore6m, Z3m = s.ZScore3m,
                    Chg1mBp = s.Chg1m, Chg3mBp = s.Chg3m, Pctl1y = s.Percentile1y,
                    CoDSig = codSig, Roll3mBp = roll,
                    HlDays = hl, EPnl3m = ePnl, EShp = eShp, Trend = trend,
                };
                // conditional rich/cheap: residual z after regressing the structure on curve factors
                try
                {
                    if (kind == "fly" && slpNear != null)
                    {
                        // three-way DATE alignment (tail-slicing two pairwise aligns quietly pairs
                        // different dates when any series has a gap)
                        var slope = CombineSeries(new List<IReadOnlyList<HistPoint>> { slpNear, lvlLeg },
                            new[] { -1.0, 1.0 }, scaleToBp: true);
                        var lvlMap = lvlLeg.ToDictionary(p => p.Date, p => p.Value * 100.0);
                        var slpMap = slope.ToDictionary(p => p.Date, p => p.Value);
                        var ys = new List<double>();
                        var ls = new List<double>();
                        var ss = new List<double>();
                        foreach (var pt in combined)
                            if (lvlMap.TryGetValue(pt.Date, out var lv) && slpMap.TryGetValue(pt.Date, out var sv))
                            {
                                ys.Add(pt.Value);
                                ls.Add(lv);
                                ss.Add(sv);
                            }
                        if (Regression.Two(ys.ToArray(), ls.ToArray(), ss.ToArray()) is { } t2)
                        {
                            row.Beta = t2.b1;
                            row.ResidZ = t2.residZ;
                        }
                    }
                    else
                    {
                        var (y, lvl) = Regression.AlignByDate(combined, lvlLeg);
                        for (int i = 0; i < lvl.Length; i++) lvl[i] *= 100.0; // % -> bp
                        if (Regression.Simple(y, lvl) is { } t1)
                        {
                            row.Beta = t1.beta;
                            row.ResidZ = t1.residZ;
                        }
                    }
                }
                catch { /* factor columns are best-effort */ }
                return row;
            }

            double? Cod2(in (int tenor, IReadOnlyList<HistPoint> hist, double? liveMid, double? prevClose) l) =>
                l.liveMid.HasValue && l.prevClose.HasValue ? (l.liveMid - l.prevClose) * 100.0 : null;

            for (int i = 0; i < legs.Count; i++)
                for (int j = i + 1; j < legs.Count; j++)
                {
                    var (a, b) = (legs[i], legs[j]);
                    double? now = a.liveMid.HasValue && b.liveMid.HasValue ? (b.liveMid - a.liveMid) * 100.0 : null;
                    double? cod = Cod2(a) is { } ca && Cod2(b) is { } cb ? cb - ca : null;
                    var r = Build($"{a.tenor}s{b.tenor}s", "spread",
                        new List<IReadOnlyList<HistPoint>> { a.hist, b.hist }, new[] { -1.0, 1.0 }, now, cod,
                        new[] { a.tenor, b.tenor });
                    if (r != null) rows.Add(r);
                }

            for (int i = 0; i < legs.Count; i++)
                for (int j = i + 1; j < legs.Count; j++)
                    for (int k = j + 1; k < legs.Count; k++)
                    {
                        var (a, b, c) = (legs[i], legs[j], legs[k]);
                        double? now = a.liveMid.HasValue && b.liveMid.HasValue && c.liveMid.HasValue
                            ? (2 * b.liveMid - a.liveMid - c.liveMid) * 100.0 : null;
                        double? cod = Cod2(a) is { } ca && Cod2(b) is { } cb && Cod2(c) is { } cc
                            ? 2 * cb - ca - cc : null;
                        var r = Build($"{a.tenor}s{b.tenor}s{c.tenor}s", "fly",
                            new List<IReadOnlyList<HistPoint>> { a.hist, b.hist, c.hist },
                            new[] { -1.0, 2.0, -1.0 }, now, cod, new[] { a.tenor, b.tenor, c.tenor });
                        if (r != null) rows.Add(r);
                    }

            return rows.OrderByDescending(r => r.Score).Take(maxRows).ToList();
        }

        private static readonly int[] XccyTenors = { 2, 5, 10, 30 };

        /// <summary>Cross-market RV: rate differentials (ccy1 − ccy2, bp) at each standard tenor plus
        /// 2s10s boxes, across every pair of cleared currencies. Z-scores are SIGNED (z > 0 = ccy1
        /// rich vs ccy2) and the Dir column reads the trade; legs are dv01-neutral by construction.</summary>
        public List<RvRow> XccyScan(IEnumerable<string> ccys, int maxRows = 80)
        {
            if (History == null) return new List<RvRow>();
            var cutoff = DateTime.Today.AddDays(-750);

            // per ccy: tenor -> (history, live mid, prev close). One batched BDH warms everything.
            var allTickers = new List<string>();
            foreach (var ccy in ccys)
            {
                if (!Configs.TryGet(ccy, out var c0) || (c0.Ois == null && c0.Irs == null)) continue;
                var s0 = SourceFor(ccy);
                var p0 = c0.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && c0.Irs != null
                    ? ProductKind.IRS : c0.Ois != null ? ProductKind.OIS : ProductKind.IRS;
                foreach (var t in XccyTenors)
                    if (ResolvePillarTicker(c0, p0, TenorUtil.Parse($"{t}Y"), s0) is { } tk)
                        allTickers.Add(tk);
            }
            try { History.Prefetch(allTickers, 1825); } catch { /* per-ticker fallback */ }

            var byCcy = new List<(string ccy, Dictionary<int, (IReadOnlyList<HistPoint> hist, double? live, double? prev)> legs)>();
            foreach (var ccy in ccys)
            {
                if (!Configs.TryGet(ccy, out var cfg) || (cfg.Ois == null && cfg.Irs == null)) continue;
                var src = SourceFor(ccy);
                var product = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null
                    ? ProductKind.IRS : ProductKind.OIS;
                if (product == ProductKind.OIS && cfg.Ois == null) product = ProductKind.IRS;
                var legs = new Dictionary<int, (IReadOnlyList<HistPoint>, double?, double?)>();
                foreach (var t in XccyTenors)
                {
                    var tkr = ResolvePillarTicker(cfg, product, TenorUtil.Parse($"{t}Y"), src);
                    if (tkr == null) continue;
                    var full = HistoryFilter.Despike(History.GetDaily(tkr, 1825));
                    var h = full.Count == 0 ? full : full.Where(p => p.Date >= cutoff).ToList();
                    if (h.Count < 250) continue;
                    var q = Snapshot.Get(tkr);
                    legs[t] = (h, q?.Mid, q?.PrevClose);
                }
                if (legs.Count >= 2) byCcy.Add((ccy.ToUpperInvariant(), legs));
            }
            if (byCcy.Count < 2) return new List<RvRow>();

            // per-ccy 3m roll at a tenor from the live grid (linear interp between quoted tenors)
            double? RollAt(Dictionary<int, (IReadOnlyList<HistPoint> hist, double? live, double? prev)> legs, double t)
            {
                var pts = legs.Where(kv => kv.Value.live.HasValue)
                    .Select(kv => ((double)kv.Key, kv.Value.live!.Value)).OrderBy(p => p.Item1).ToList();
                if (pts.Count < 2) return null;
                double At(double x)
                {
                    if (x <= pts[0].Item1) return pts[0].Item2;
                    if (x >= pts[^1].Item1) return pts[^1].Item2;
                    for (int i = 1; i < pts.Count; i++)
                        if (x <= pts[i].Item1)
                        {
                            var (x0, y0) = pts[i - 1];
                            var (x1, y1) = pts[i];
                            return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
                        }
                    return pts[^1].Item2;
                }
                return (At(t) - At(Math.Max(t - 0.25, pts[0].Item1))) * 100.0;
            }

            var rows = new List<RvRow>();
            // SIGNED convention (no folding): the differential is always ccy1 − ccy2 in bp;
            // z > 0 = ccy1 historically rich vs ccy2, and Dir spells the trade out
            RvRow? Build(string ca, string cb, string suffix, string kind,
                List<IReadOnlyList<HistPoint>> series, double[] w, double? now, double? cod, double? roll3m,
                IReadOnlyList<HistPoint>? lvlFactor)
            {
                var combined = HistoryFilter.Despike(CombineSeries(series, w, scaleToBp: true),
                    window: 7, k: 4, madFloorPct: 0.5, passes: 3);
                if (combined.Count < 250) return null;
                var s = SeriesStats.Compute(combined, liveLast: now, changeScale: 1.0);
                var (hl, ePnl, eShp, trend, codSig) = ExpectedValue(s, now, roll3m, cod);
                var row = new RvRow
                {
                    Structure = $"{ca}-{cb} {suffix}", Kind = kind,
                    NowBp = now ?? combined[^1].Value,
                    DeltaMeanBp = s.Mean1y is double m ? (now ?? combined[^1].Value) - m : null,
                    Mean1yBp = s.Mean1y, Z1y = s.ZScore1y, Z6m = s.ZScore6m, Z3m = s.ZScore3m,
                    Chg1mBp = s.Chg1m, Chg3mBp = s.Chg3m, Pctl1y = s.Percentile1y,
                    CoDSig = codSig, Roll3mBp = roll3m,
                    HlDays = hl, EPnl3m = ePnl, EShp = eShp, Trend = trend,
                    Dir = (s.ZScore1y ?? 0) >= 0 ? $"pay {cb} / rec {ca}" : $"pay {ca} / rec {cb}",
                };
                // directionality vs ccy1's 10y level
                if (lvlFactor != null)
                {
                    try
                    {
                        var (y, lvl) = Regression.AlignByDate(combined, lvlFactor);
                        for (int i = 0; i < lvl.Length; i++) lvl[i] *= 100.0;
                        if (Regression.Simple(y, lvl) is { } t1)
                        {
                            row.Beta = t1.beta;
                            row.ResidZ = t1.residZ;
                        }
                    }
                    catch { /* factor columns are best-effort */ }
                }
                return row;
            }

            for (int i = 0; i < byCcy.Count; i++)
                for (int j = i + 1; j < byCcy.Count; j++)
                {
                    var (ca, la) = byCcy[i];
                    var (cb, lb) = byCcy[j];
                    var lvlA = la.TryGetValue(10, out var l10a) ? l10a.hist : null;
                    double? CodOf((IReadOnlyList<HistPoint> hist, double? live, double? prev) l) =>
                        l.live.HasValue && l.prev.HasValue ? (l.live - l.prev) * 100.0 : null;
                    foreach (var t in XccyTenors)
                    {
                        if (!la.TryGetValue(t, out var a) || !lb.TryGetValue(t, out var b)) continue;
                        double? now = a.live.HasValue && b.live.HasValue ? (a.live - b.live) * 100.0 : null;
                        double? cod = CodOf(a) is { } cda && CodOf(b) is { } cdb ? cda - cdb : null;
                        double? roll = RollAt(la, t) is { } r1 && RollAt(lb, t) is { } r2 ? r1 - r2 : null;
                        var r = Build(ca, cb, $"{t}y", "xccy",
                            new List<IReadOnlyList<HistPoint>> { a.hist, b.hist }, new[] { 1.0, -1.0 },
                            now, cod, roll, lvlA);
                        if (r != null) rows.Add(r);
                    }
                    // 2s10s box: curve-shape differential
                    if (la.TryGetValue(2, out var a2) && la.TryGetValue(10, out var a10)
                        && lb.TryGetValue(2, out var b2) && lb.TryGetValue(10, out var b10))
                    {
                        double? now = a2.live.HasValue && a10.live.HasValue && b2.live.HasValue && b10.live.HasValue
                            ? ((a10.live - a2.live) - (b10.live - b2.live)) * 100.0 : null;
                        double? cod = CodOf(a2) is { } c2a && CodOf(a10) is { } c10a
                                   && CodOf(b2) is { } c2b && CodOf(b10) is { } c10b
                            ? (c10a - c2a) - (c10b - c2b) : null;
                        double? roll = RollAt(la, 10) is { } ra10 && RollAt(la, 2) is { } ra2
                                    && RollAt(lb, 10) is { } rb10 && RollAt(lb, 2) is { } rb2
                            ? (ra10 - ra2) - (rb10 - rb2) : null;
                        var r = Build(ca, cb, "2s10s box", "box",
                            new List<IReadOnlyList<HistPoint>> { a2.hist, a10.hist, b2.hist, b10.hist },
                            new[] { -1.0, 1.0, 1.0, -1.0 }, now, cod, roll, lvlA);
                        if (r != null) rows.Add(r);
                    }
                }

            return rows.OrderByDescending(r => r.Score).Take(maxRows).ToList();
        }
    }
}
