using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Query;
using RateDesk.Core.Trades;

namespace RateDesk.Core
{
    /// <summary>One monitored relationship on the CORR screen. All ρ are Pearson on DAILY CHANGES.</summary>
    public sealed class CorrRow
    {
        public string Pair { get; init; } = "";
        public string Why { get; init; } = "";
        public bool Curated { get; init; }
        /// <summary>Pearson ρ of daily changes: ~3m / ~6m / ~2y windows.</summary>
        public double? RhoNow { get; init; }
        public double? Rho6m { get; init; }
        public double? RhoLr { get; init; }
        /// <summary>Fisher-z significance of (ρ3m vs ρ2y), autocorrelation-corrected: a 3m ρ on ~63
        /// noisy observations has SE > 0.15, so raw gaps cry wolf — T asks if the move is REAL.
        /// T·sign(ρ2y) ≤ −2.5 is a genuine break, not window noise.</summary>
        public double? T { get; init; }
        /// <summary>Business days since the break condition first held (walked back down the
        /// rolling-ρ series) — separates this week's story from March's.</summary>
        public double? AgeDays { get; init; }
        public bool Fresh => (Broken || Flipped || Weakening) && (AgeDays ?? 99) <= 5;
        /// <summary>Hedge ratio dy/dx on daily changes (unit: y-units per x-unit), 2y and 6m
        /// windows; flagged unstable when they disagree beyond 2 standard errors.</summary>
        public double? HedgeBeta2y { get; init; }
        public double? HedgeBeta6m { get; init; }
        public bool BetaUnstable { get; init; }
        /// <summary>Level-regression residual z: how far the pair sits TODAY from its 2y fitted
        /// line — the "how big is the dislocation" number a breakdown turns into a trade. Only
        /// usable when the residual itself mean-reverts (half-life ≤ 90d).</summary>
        public double? ResidZ { get; init; }
        public double? ResidHlDays { get; init; }
        public bool ResidUsable { get; init; }
        /// <summary>Lead-lag: days by which the FIRST leg leads the second (negative = it lags),
        /// reported only when the lagged ρ clearly beats the contemporaneous one.</summary>
        public int LeadLagDays { get; init; }
        public double? RhoLagged { get; init; }
        public int N { get; init; }
        /// <summary>Rolling 3m ρ, stepped weekly — the evolution series for the chart.</summary>
        public IReadOnlyList<HistPoint> Rolling { get; init; } = Array.Empty<HistPoint>();
        /// <summary>True when the pair was computed on WEEKLY changes (stale/lagged daily marks).</summary>
        public bool WeeklyBasis { get; init; }
        /// <summary>Human label of the rolling-ρ window actually used ("3m", "12w", …).</summary>
        public string RollWinLabel { get; init; } = "3m";

        private double TSigned => (T ?? 0) * Math.Sign(RhoLr ?? 1);
        public bool Flipped => RhoNow is double n && RhoLr is double l && n * l < 0
                               && Math.Abs(n) >= 0.30 && Math.Abs(l) >= 0.35 && Math.Abs(T ?? 0) >= 2.5;
        public bool Broken => !Flipped && Math.Abs(RhoLr ?? 0) >= 0.35 && TSigned <= -2.5;
        public bool Weakening => !Broken && !Flipped && Math.Abs(RhoLr ?? 0) >= 0.35 && TSigned <= -1.5;
        /// <summary>Fresh significant breaks first, then stale ones, then the strongest intact links.</summary>
        public double SortKey => (Broken || Flipped ? 1000 : Weakening ? 500 : 0)
                                 + (Fresh ? 300 : 0)
                                 + Math.Min(50, Math.Abs(TSigned)) * 5
                                 + Math.Abs(RhoLr ?? 0);
    }

    /// <summary>One bubble on the RV map: a structure's dislocation (x), carry (y) and
    /// correlation to the user's anchor structure. Structures may live on ANY currency.</summary>
    public sealed class RvMapRow
    {
        /// <summary>Display label, ALWAYS ccy-qualified ("EUR 2s5s10s").</summary>
        public string Label { get; init; } = "";
        /// <summary>"Flies" (3 legs) or "Curves" (everything else).</summary>
        public string Category { get; init; } = "";
        /// <summary>Full pasteable query including its currency.</summary>
        public string Query { get; init; } = "";
        /// <summary>Z at the app lookback (kept for the CLI); the GUI picks from the trio below.</summary>
        public double Z { get; init; }
        public double? Z3m { get; init; }
        public double? Z6m { get; init; }
        public double? Z1y { get; init; }
        public double VolAdjCarry { get; init; }
        /// <summary>SIGNED ρ of daily changes vs the anchor: + moves with it, − hedges it.</summary>
        public double CorrToAnchor { get; init; }
        public double? NowBp { get; init; }
        /// <summary>Expected 3m P&L ÷ 3m σ (half-life-aware convergence + carry).</summary>
        public double? EShp { get; init; }
        /// <summary>No in-sample mean reversion — rendered hollow (the z is a trend, not value).</summary>
        public bool Trend { get; init; }
        public double? Roll3mBp { get; init; }
    }

    public sealed partial class PricingService
    {
        private const int CorrWindowNow = 63;   // ~3m of daily changes
        private const int CorrWindow6m = 126;
        private const int CorrWindowLr = 504;   // ~2y
        private const int CorrFetchDays = 3650; // 10y — the CORR chart's longest lookback

        /// <summary>Despiked FULL history (10y superset for the CORR chart's lookbacks), ignoring
        /// the UI lookback slice — correlation windows must not shrink with the chart setting.</summary>
        private IReadOnlyList<HistPoint> HistFull(string ticker)
        {
            if (History == null) return Array.Empty<HistPoint>();
            if (!_despiked.TryGetValue(ticker, out var c) || c.day != DateTime.Today || c.days < CorrFetchDays)
            {
                var clean = HistoryFilter.Despike(History.GetDaily(ticker, CorrFetchDays));
                c = (DateTime.Today, CorrFetchDays, clean);
                if (clean.Count > 0) _despiked[ticker] = c;
            }
            return c.data;
        }

        // ---------- series resolution ----------

        private sealed class CorrSeries
        {
            public string Label = "";
            public IReadOnlyList<HistPoint> Levels = Array.Empty<HistPoint>();
            public bool Log;                 // fx/cmdty/eqty: difference in log space
            public string Class = "rates";   // rates | fx | cmdty | eqty
            public string? Ccy;
        }

        /// <summary>Pillar history interpolated at an arbitrary tenor (linear in rate between the
        /// bracketing quoted pillars, clamped at the ends). Series in %. `band` restricts to one
        /// quote family in dual-band markets (AUD quotes q/q AND s/s at 4Y-9Y, ~26bp apart —
        /// interpolating across families books the tenor basis into the series).</summary>
        private IReadOnlyList<HistPoint> PillarSeriesAt(CurrencyConfig cfg, ProductKind product, string src, double years,
            string? band = null)
        {
            var pillars = (product == ProductKind.OIS ? cfg.Ois?.Curve : cfg.Irs?.Curve)?
                .Where(p => p.Enabled && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase) && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)
                            && (band == null || product != ProductKind.IRS || cfg.Irs == null
                                || Pricing.SwapBuilder.PillarBand(cfg.Irs, p).Equals(band, StringComparison.OrdinalIgnoreCase)))
                .Select(p => (y: TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0,
                              t: ConfigStore.ResolveTicker(p.Ticker, src)))
                .Where(p => p.y > 0.4)
                .OrderBy(p => p.y).ToList();
            if (pillars == null || pillars.Count == 0)
                return band != null ? PillarSeriesAt(cfg, product, src, years) : Array.Empty<HistPoint>();

            foreach (var p in pillars)
                if (Math.Abs(p.y - years) < 0.05)
                    return HistFull(p.t);
            (double y, string t)? lo = null, hi = null;
            foreach (var p in pillars)
            {
                if (p.y < years) lo = p;
                else { hi = p; break; }
            }
            if (lo == null) return hi != null ? HistFull(hi.Value.t) : Array.Empty<HistPoint>();
            if (hi == null) return HistFull(lo.Value.t);
            double f = (years - lo.Value.y) / (hi.Value.y - lo.Value.y);
            return CombineSeries(new List<IReadOnlyList<HistPoint>> { HistFull(lo.Value.t), HistFull(hi.Value.t) },
                new[] { 1 - f, f }, scaleToBp: false);
        }

        /// <summary>Rates structure history in BP from pillar histories alone: spot legs are
        /// pillar-interpolated, forward/IMM/dated legs use the annuity-less par identity
        /// f(a,b) ≈ (b·par_b − a·par_a)/(b−a). No curve builds — pure cached BDH, so a
        /// 200-series scan costs a handful of batched requests.</summary>
        private IReadOnlyList<HistPoint> StructureSeriesBp(ParsedQuery pq)
        {
            var cfg = Configs.Get(pq.Target.Ccy);
            var src = SourceFor(pq.Target.Ccy);
            var product = ResolveProductForTarget(pq.Target, cfg);
            var w = pq.Legs.Count switch
            {
                1 => new[] { 1.0 },
                2 => new[] { -1.0, 1.0 },
                _ => new[] { -1.0, 2.0, -1.0 },
            };
            var today = DateTime.Today;
            var qToday = new Date(today.Day, (Month)today.Month, today.Year);
            var legSeries = new List<IReadOnlyList<HistPoint>>();
            for (int li = 0; li < pq.Legs.Count; li++)
            {
                var leg = pq.Legs[li];
                double tn = leg.Tenor != null ? TenorUtil.ApproxMonths(leg.Tenor) / 12.0 : 0;
                if (tn <= 0) return Array.Empty<HistPoint>();
                double a = leg.StartKind switch
                {
                    StartKind.Forward => TenorUtil.ApproxMonths(leg.ForwardStart!) / 12.0,
                    StartKind.Imm => Math.Max(0.0, (leg.ImmDate! - qToday) / 365.25),
                    StartKind.Date => Math.Max(0.0, (leg.ExplicitStart! - qToday) / 365.25),
                    _ => 0.0,
                };
                // one quote family per leg (dual-band markets), matching what the pricer prices
                var band = HistBandFor(cfg, product, pq, li);
                var s = a < 1e-9
                    ? PillarSeriesAt(cfg, product, src, tn, band)
                    : CombineSeries(new List<IReadOnlyList<HistPoint>>
                        {
                            PillarSeriesAt(cfg, product, src, a, band),
                            PillarSeriesAt(cfg, product, src, a + tn, band),
                        },
                        new[] { -a / tn, (a + tn) / tn }, scaleToBp: false);
                if (s.Count < 100) return Array.Empty<HistPoint>();
                legSeries.Add(s);
            }
            var combined = legSeries.Count == 1
                ? legSeries[0].Select(p => new HistPoint(p.Date, p.Value * 100.0)).ToList()
                : CombineSeries(legSeries, w, scaleToBp: true);
            return HistoryFilter.Despike(combined, window: 7, k: 4, madFloorPct: 0.5, passes: 2);
        }

        /// <summary>Inflation/rate LADDER history in bp (ZC breakeven or FF pillars): spot tenors
        /// interpolated between quoted pillars, forwards via the annuity-less identity — this is
        /// what lets "5y us cpi" or "5y5y us cpi" sit on the CORR board as a leg.</summary>
        private IReadOnlyList<HistPoint> LadderSeriesBp(ParsedQuery pq)
        {
            var cfg = Configs.Get(pq.Target.Ccy);
            var lad = cfg.Ladders.FirstOrDefault(l =>
                l.Name.Equals(pq.Target.LadderName, StringComparison.OrdinalIgnoreCase));
            if (lad == null || pq.Main?.Tenor == null) return Array.Empty<HistPoint>();
            var pillars = lad.Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                .Select(p => (y: TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0,
                              t: ConfigStore.ResolveTicker(p.Ticker, "")))
                .OrderBy(p => p.y).ToList();
            if (pillars.Count == 0) return Array.Empty<HistPoint>();

            IReadOnlyList<HistPoint> At(double years)
            {
                foreach (var p in pillars)
                    if (Math.Abs(p.y - years) < 0.05)
                        return HistFull(p.t);
                (double y, string t)? lo = null, hi = null;
                foreach (var p in pillars)
                {
                    if (p.y < years) lo = p;
                    else { hi = p; break; }
                }
                if (lo == null) return hi != null ? HistFull(hi.Value.t) : Array.Empty<HistPoint>();
                if (hi == null) return HistFull(lo.Value.t);
                double f = (years - lo.Value.y) / (hi.Value.y - lo.Value.y);
                return CombineSeries(new List<IReadOnlyList<HistPoint>> { HistFull(lo.Value.t), HistFull(hi.Value.t) },
                    new[] { 1 - f, f }, scaleToBp: false);
            }

            double tn = TenorUtil.ApproxMonths(pq.Main.Tenor) / 12.0;
            double a = pq.Main.StartKind == StartKind.Forward && pq.Main.ForwardStart != null
                ? TenorUtil.ApproxMonths(pq.Main.ForwardStart) / 12.0 : 0.0;
            var s = a < 1e-9
                ? At(tn)
                : CombineSeries(new List<IReadOnlyList<HistPoint>> { At(a), At(a + tn) },
                    new[] { -a / tn, (a + tn) / tn }, scaleToBp: false);
            if (s.Count < 100) return Array.Empty<HistPoint>();
            return HistoryFilter.Despike(
                s.Select(p2 => new HistPoint(p2.Date, p2.Value * 100.0)).ToList(),
                window: 7, k: 4, madFloorPct: 0.5, passes: 2);
        }

        /// <summary>Resolve a pair leg: ticker label → log-return series; "A - B" → rates combo;
        /// anything else → swap-structure query. Null when unresolvable or history is too short.</summary>
        private CorrSeries? ResolveCorrSeries(string spec, Dictionary<string, CorrTickerDef> tickers,
            Dictionary<string, CorrSeries?> cache)
        {
            var key = spec.Trim();
            if (cache.TryGetValue(key, out var hit)) return hit;
            CorrSeries? Make()
            {
                if (tickers.TryGetValue(key, out var td))
                {
                    var lv = HistFull(td.Ticker);
                    if (lv.Count < 300) return null;
                    var cls = td.Class.ToLowerInvariant();
                    // log-returns for price-like series; plain bp changes for spread-like series
                    // (xccy basis and credit spreads quote in bp and can be negative)
                    bool logT = cls is "fx" or "cmdty" or "eqty" or "vol";
                    return new CorrSeries { Label = td.Label, Levels = lv, Log = logT, Class = cls };
                }
                // RAW Bloomberg ticker ("dairy futures vs the front end"): any yellow-key suffix
                // passes straight through — but never when the spec is an "A - B" combo whose
                // second leg happens to end in a yellow key. Log-returns only when the level is
                // WELL CLEAR of zero (a near-zero positive series explodes in log space and the
                // basis must not flip when a stray print crosses zero); "lin:" forces linear.
                bool forceLin = key.StartsWith("lin:", StringComparison.OrdinalIgnoreCase);
                var raw = forceLin ? key[4..].Trim() : key;
                if (!key.Contains(" - ", StringComparison.Ordinal)
                    && (raw.EndsWith(" Curncy", StringComparison.OrdinalIgnoreCase)
                        || raw.EndsWith(" Comdty", StringComparison.OrdinalIgnoreCase)
                        || raw.EndsWith(" Index", StringComparison.OrdinalIgnoreCase)
                        || raw.EndsWith(" Equity", StringComparison.OrdinalIgnoreCase)
                        || raw.EndsWith(" Corp", StringComparison.OrdinalIgnoreCase)
                        || raw.EndsWith(" Govt", StringComparison.OrdinalIgnoreCase)))
                {
                    var lv = HistFull(raw);
                    if (lv.Count < 300) return null;
                    double minLv = lv.Min(p => p.Value);
                    var absSorted = lv.Select(p => Math.Abs(p.Value)).OrderBy(v => v).ToList();
                    double medAbs = absSorted[absSorted.Count / 2];
                    bool logSafe = minLv > 0 && medAbs > 1e-9 && minLv >= 0.05 * medAbs;
                    return new CorrSeries
                    {
                        Label = raw.ToUpperInvariant(), Levels = lv,
                        Log = !forceLin && logSafe, Class = "adhoc",
                    };
                }
                if (key.Contains(" - ", StringComparison.Ordinal))
                {
                    var parts = key.Split(" - ", 2, StringSplitOptions.TrimEntries);
                    var sa = ResolveCorrSeries(parts[0], tickers, cache);
                    var sb = ResolveCorrSeries(parts[1], tickers, cache);
                    if (sa == null || sb == null || sa.Log || sb.Log) return null;
                    var comb = CombineSeries(new List<IReadOnlyList<HistPoint>> { sa.Levels, sb.Levels },
                        new[] { 1.0, -1.0 }, scaleToBp: false);
                    if (comb.Count < 300) return null;
                    return new CorrSeries
                    {
                        Label = $"{sa.Label} − {sb.Label}", Levels = comb,
                        Log = false, Class = "rates", Ccy = $"{sa.Ccy}|{sb.Ccy}",
                    };
                }
                try
                {
                    var pq = ParseQuery(key);
                    if (pq.MeetingRun != null || pq.Legs.Count == 0) return null;
                    var s = pq.Target.IsLadder ? LadderSeriesBp(pq) : StructureSeriesBp(pq);
                    if (s.Count < 300) return null;
                    return new CorrSeries
                    {
                        Label = key.ToUpperInvariant(), Levels = s, Log = false,
                        Class = pq.Target.IsLadder ? "infl" : "rates", Ccy = pq.Target.Ccy,
                    };
                }
                catch { return null; }
            }
            var made = Make();
            cache[key] = made;
            return made;
        }

        // ---------- CORR screen ----------

        /// <summary>Build the ~100-pair correlation board: curated macro pairs from
        /// config/correlations.json, topped up by an automatic scan for the strongest remaining
        /// cross-market links. Rows come back breakdowns-first.</summary>
        public List<CorrRow> CorrScan(Action<string>? progress = null,
            int nowWin = 63, int lrWin = 504, double minAbsRho = 0.45, int? targetTotal = null)
        {
            if (History == null) return new List<CorrRow>();
            var cc = CorrStore.Load();
            var tickers = cc.Tickers
                .GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var cache = new Dictionary<string, CorrSeries?>(StringComparer.OrdinalIgnoreCase);

            // one batched BDH warm covering everything the scan can touch
            progress?.Invoke("warming histories (batched)...");
            var warm = new List<string>(cc.Tickers.Select(t => t.Ticker));
            foreach (var c0 in Configs.Enabled.Where(c => c.Ois != null || c.Irs != null))
            {
                var p0 = c0.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && c0.Irs != null
                    ? ProductKind.IRS : c0.Ois != null ? ProductKind.OIS : ProductKind.IRS;
                var s0 = SourceFor(c0.Ccy);
                foreach (var t in new[] { 2, 5, 10, 30 })
                    if (ResolvePillarTicker(c0, p0, TenorUtil.Parse($"{t}Y"), s0) is { } tk)
                        warm.Add(tk);
            }
            try { History.Prefetch(warm.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), CorrFetchDays); }
            catch { /* per-ticker fallback */ }

            var rows = new List<CorrRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Dedup(string a, string b) =>
                string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

            void AddPair(string a, string b, string why, bool curated)
            {
                if (!seen.Add(Dedup(a.Trim(), b.Trim()))) return;
                var sa = ResolveCorrSeries(a, tickers, cache);
                var sb = ResolveCorrSeries(b, tickers, cache);
                if (sa == null || sb == null) return;
                var row = BuildCorrRow(sa, sb, why, curated, nowWin, lrWin);
                if (row != null) rows.Add(row);
            }

            progress?.Invoke("curated macro pairs...");
            foreach (var p in cc.Pairs) AddPair(p.A, p.B, p.Why, curated: true);

            // ---- auto-scan: strongest remaining cross-market links tops the list up ----
            progress?.Invoke("auto-scanning for strong cross-market links...");
            var universe = new List<string>();
            foreach (var c in Configs.Enabled.Where(c => c.Ois != null || c.Irs != null))
            {
                var lc = c.Ccy.ToLowerInvariant();
                universe.Add($"{lc} 2y");
                universe.Add($"{lc} 10y");
                universe.Add($"{lc} 2s10s");
            }
            universe.AddRange(cc.Tickers.Select(t => t.Label));

            var resolved = universe
                .Select(u => (key: u, s: ResolveCorrSeries(u, tickers, cache)))
                .Where(x => x.s != null).ToList();
            var cand = new List<(double score, string a, string b)>();
            for (int i = 0; i < resolved.Count; i++)
                for (int j = i + 1; j < resolved.Count; j++)
                {
                    var (ka, sa) = resolved[i];
                    var (kb, sb) = resolved[j];
                    // exclusions: same-ccy rates pairs are mechanically correlated, and same-class
                    // non-rates pairs (fx-fx, cmdty-cmdty) aren't the macro links this screen hunts
                    if (sa!.Class is "rates" or "infl" && sa.Class == sb!.Class
                        && string.Equals(sa.Ccy, sb.Ccy, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sa.Class != "rates" && sa.Class == sb!.Class) continue;
                    if (seen.Contains(Dedup(ka, kb))) continue;
                    var (_, dx, dy) = Correlation.AlignedChanges(sa.Levels, sb!.Levels, sa.Log, sb.Log);
                    if (Correlation.Pearson(dx, dy, lrWin) is not double l || Math.Abs(l) < minAbsRho) continue;
                    cand.Add((Math.Abs(l), ka, kb));
                }
            int target = targetTotal ?? cc.TargetTotal;
            foreach (var c in cand.OrderByDescending(c => c.score))
            {
                if (rows.Count >= target) break;
                AddPair(c.a, c.b, $"auto-scan: |ρ| = {c.score:0.00}", curated: false);
            }

            progress?.Invoke($"{rows.Count} pairs computed");
            return rows.OrderByDescending(r => r.SortKey).ToList();
        }

        /// <summary>Fraction of exactly-zero changes over the last ~126 obs — the stale-mark
        /// symptom that switches a pair to the weekly basis (shared by board and snapshot).</summary>
        private static double CorrStaleFrac(double[] d)
        {
            int n = Math.Min(126, d.Length);
            if (n == 0) return 0;
            int zc = 0;
            for (int i = d.Length - n; i < d.Length; i++)
                if (Math.Abs(d[i]) < 1e-9) zc++;
            return (double)zc / n;
        }

        /// <summary>Weekly Δ basis: 5-day change blocks anchored at the END of the series.</summary>
        private static (double[] dx, double[] dy, DateTime[] dates) CorrWeeklyBlocks(
            DateTime[] dates, double[] dx, double[] dy)
        {
            int nw = dx.Length / 5;
            var wdx = new double[nw];
            var wdy = new double[nw];
            var wdates = new DateTime[nw];
            for (int i = 0; i < nw; i++)
            {
                int end = dx.Length - (nw - 1 - i) * 5;
                for (int k = end - 5; k < end; k++) { wdx[i] += dx[k]; wdy[i] += dy[k]; }
                wdates[i] = dates[end - 1];
            }
            return (wdx, wdy, wdates);
        }

        /// <summary>Full pair statistics: basis choice (daily vs weekly on stale/lagged marks),
        /// Fisher T, break age, hedge betas, level-residual diagnostics. Shared by the main scan
        /// and the per-currency deep dive.</summary>
        private CorrRow? BuildCorrRow(CorrSeries sa, CorrSeries sb, string why, bool curated,
            int nowWinP, int lrWinP)
        {
                var (dates, dx, dy) = Correlation.AlignedChanges(sa.Levels, sb.Levels, sa.Log, sb.Log);
                if (dx.Length < nowWinP + 20) return null;

                // ILLIQUID MARKS (e.g. DKK swaps): repeats (stale) OR day-lagged catch-up both
                // crush a daily-change ρ while the relationship truly holds — DKK 2s10s prints
                // daily yet re-couples to EUR within the week (weekly ρ 0.95 vs daily 0.42).
                // Either symptom switches the whole pair to a WEEKLY basis.
                int nw = dx.Length / 5;
                double[]? wdx = null, wdy = null;
                DateTime[]? wdates = null;
                if (nw >= 30) (wdx, wdy, wdates) = CorrWeeklyBlocks(dates, dx, dy);
                bool weekly = Math.Max(CorrStaleFrac(dx), CorrStaleFrac(dy)) > 0.30;
                string weeklyWhy = "daily marks stale";
                if (!weekly && wdx != null)
                {
                    var dLr = Correlation.Pearson(dx, dy, lrWinP);
                    var wLrRho = Correlation.Pearson(wdx, wdy!, Math.Max(30, lrWinP / 5), minN: 30);
                    if (dLr is double dl && wLrRho is double wl
                        && Math.Abs(wl) - Math.Abs(dl) > 0.15 && Math.Abs(wl) >= 0.5)
                    {
                        weekly = true;
                        weeklyWhy = "daily marks lag";
                    }
                }
                int wNow = nowWinP, w6m = CorrWindow6m, wLr = lrWinP;
                int rollWin = Math.Max(21, nowWinP), rollStep = 5, minObs = 20;
                if (weekly)
                {
                    if (wdx == null) return null;
                    dx = wdx; dy = wdy!; dates = wdates!;
                    wNow = Math.Max(10, nowWinP / 5); w6m = 25; wLr = Math.Max(30, lrWinP / 5);
                    rollWin = Math.Max(6, rollWin / 5); rollStep = 1; minObs = 10;
                    why = (why.Length > 0 ? why + " · " : "") + $"weekly Δ ({weeklyWhy})";
                }
                string rollLbl = weekly ? $"{rollWin}w"
                    : rollWin switch { 21 => "1m", 63 => "3m", 126 => "6m", _ => $"{rollWin}d" };

                var lr = Correlation.Pearson(dx, dy, wLr, minObs);
                var now = Correlation.Pearson(dx, dy, wNow, minObs);
                if (lr == null || now == null) return null;

                // LEAD-LAG: contemporaneous ρ misses relationships where one market reacts days
                // later (oil today → NOK front end tomorrow). Scan ±5 days on the daily basis and
                // report only when the lagged link clearly beats the contemporaneous one.
                int lagDays = 0;
                double? rhoLag = null;
                if (!weekly)
                {
                    double best = Math.Abs(lr.Value);
                    for (int L = -5; L <= 5; L++)
                    {
                        if (L == 0) continue;
                        if (Correlation.PearsonLagged(dx, dy, L, wLr, minObs) is double rl2
                            && Math.Abs(rl2) > best + 1e-9)
                        {
                            best = Math.Abs(rl2);
                            lagDays = L;
                            rhoLag = rl2;
                        }
                    }
                    if (rhoLag == null || Math.Abs(rhoLag.Value) - Math.Abs(lr.Value) < 0.10)
                    {
                        lagDays = 0;
                        rhoLag = null;
                    }
                }

                // Fisher-z break significance with autocorrelation-corrected sample sizes
                double r1 = Math.Max(Correlation.Autocorr1(dx, wLr),
                                     Correlation.Autocorr1(dy, wLr));
                int nLr = Math.Min(wLr, dx.Length);
                var t = Correlation.FisherT(now, lr, Math.Min(wNow, dx.Length), nLr, r1);
                var rolling = Correlation.Rolling(dates, dx, dy, rollWin, rollStep);

                // age: walk the rolling series backwards while it stays on the broken side
                double? age = null;
                if (lr is double l0 && rolling.Count > 0)
                {
                    // mirrors CorrRow.Flipped/Broken: a BROKEN row's age must walk back at the
                    // BREAK bar (−2.5), or a fresh true break inherits weeks of mere weakening
                    bool flip0 = now.Value * l0 < 0 && Math.Abs(now.Value) >= 0.30
                                 && Math.Abs(l0) >= 0.35 && Math.Abs(t ?? 0) >= 2.5;
                    bool brk0 = !flip0 && Math.Abs(l0) >= 0.35 && (t ?? 0) * Math.Sign(l0) <= -2.5;
                    double walkBar = brk0 ? -2.5 : -1.5;
                    DateTime? firstBad = null;
                    for (int i = rolling.Count - 1; i >= 0; i--)
                    {
                        // nNow = the rolling window's OWN obs count (≠ wNow on the weekly basis)
                        var tr = Correlation.FisherT(rolling[i].Value, l0, rollWin, nLr, r1);
                        bool bad = tr is double tv
                                   && (tv * Math.Sign(l0) <= walkBar
                                       || (rolling[i].Value * l0 < 0 && Math.Abs(tv) >= 2.5));
                        if (!bad) break;
                        firstBad = rolling[i].Date;
                    }
                    if (firstBad != null)
                        age = Math.Max(0, (DateTime.Today - firstBad.Value).TotalDays * 5.0 / 7.0);
                }

                // hedge ratio dy/dx on daily changes, 2y vs 6m, with an OLS-SE instability flag
                (double beta, double se)? Ols(int lastN)
                {
                    int total = Math.Min(dx.Length, dy.Length);
                    int n = Math.Min(lastN, total);
                    if (n < 20) return null;
                    int off = total - n;
                    double mx = 0, my = 0;
                    for (int i = off; i < total; i++) { mx += dx[i]; my += dy[i]; }
                    mx /= n; my /= n;
                    double sxx = 0, sxy = 0, syy = 0;
                    for (int i = off; i < total; i++)
                    {
                        double ddx = dx[i] - mx, ddy = dy[i] - my;
                        sxx += ddx * ddx; sxy += ddx * ddy; syy += ddy * ddy;
                    }
                    if (sxx < 1e-12) return null;
                    double beta = sxy / sxx;
                    double ssRes = syy - beta * sxy;
                    double se = Math.Sqrt(Math.Max(ssRes, 0) / Math.Max(1, n - 2) / sxx);
                    return (beta, se);
                }
                var b2y = Ols(wLr);
                var b6m = Ols(w6m);
                bool unstable = b2y != null && b6m != null
                                && Math.Abs(b6m.Value.beta - b2y.Value.beta) > 2 * b6m.Value.se;

                // level regression over 2y: residual z (today's dislocation from the fitted line)
                // and the residual's own half-life — no reversion, no trade
                double? residZ = null, residHl = null;
                bool residUsable = false;
                try
                {
                    IReadOnlyList<HistPoint> TL(CorrSeries s0) => s0.Log
                        ? s0.Levels.Where(p => p.Value > 0)
                            .Select(p => new HistPoint(p.Date, Math.Log(p.Value) * 100.0)).ToList()
                        : s0.Levels;
                    var (xa, yb) = Regression.AlignByDate(TL(sa), TL(sb));
                    int nl = Math.Min(505, xa.Length);
                    if (nl >= 120)
                    {
                        int off = xa.Length - nl;
                        double mx = 0, my = 0;
                        for (int i = off; i < xa.Length; i++) { mx += xa[i]; my += yb[i]; }
                        mx /= nl; my /= nl;
                        double sxx = 0, sxy = 0;
                        for (int i = off; i < xa.Length; i++)
                        {
                            sxx += (xa[i] - mx) * (xa[i] - mx);
                            sxy += (xa[i] - mx) * (yb[i] - my);
                        }
                        if (sxx > 1e-12)
                        {
                            double beta = sxy / sxx, alpha = my - beta * mx;
                            var resid = new double[nl];
                            double ss = 0;
                            for (int i = 0; i < nl; i++)
                            {
                                resid[i] = yb[off + i] - (alpha + beta * xa[off + i]);
                                ss += resid[i] * resid[i];
                            }
                            double sd = Math.Sqrt(ss / Math.Max(1, nl - 2));
                            if (sd > 1e-9)
                            {
                                residZ = resid[^1] / sd;
                                if (Correlation.Ar1Phi(resid) is double phi && phi > 0 && phi < 1)
                                {
                                    residHl = -Math.Log(2.0) / Math.Log(phi);
                                    residUsable = residHl <= 90;
                                }
                            }
                        }
                    }
                }
                catch { /* level diagnostics are best-effort */ }

                return new CorrRow
                {
                    Pair = $"{sa.Label}  ×  {sb.Label}",
                    Why = why, Curated = curated,
                    RhoNow = now, Rho6m = Correlation.Pearson(dx, dy, w6m, minObs), RhoLr = lr,
                    T = t, AgeDays = age,
                    HedgeBeta2y = b2y?.beta, HedgeBeta6m = b6m?.beta, BetaUnstable = unstable,
                    ResidZ = residZ, ResidHlDays = residHl, ResidUsable = residUsable,
                    LeadLagDays = lagDays, RhoLagged = rhoLag,
                    N = dx.Length,
                    Rolling = rolling,
                    WeeklyBasis = weekly, RollWinLabel = rollLbl,
                };
        }

        /// <summary>DEEP DIVE one currency: its rates structures and FX crosses against EVERY
        /// other series in the universe, with a lower admission bar than the main scan — when a
        /// currency looks interesting this digs deeper rather than just filtering. Rows tagged.</summary>
        public List<CorrRow> CorrDeepScan(string ccy, Action<string>? progress = null,
            int nowWin = 63, int lrWin = 504, double minAbsRho = 0.30, int maxRows = 60)
        {
            if (History == null) return new List<CorrRow>();
            ccy = ccy.ToUpperInvariant();
            var cc = CorrStore.Load();
            var tickers = cc.Tickers.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var cache = new Dictionary<string, CorrSeries?>(StringComparer.OrdinalIgnoreCase);

            var lc = ccy.ToLowerInvariant();
            var focus = new List<string>
            {
                $"{lc} 2y", $"{lc} 5y", $"{lc} 10y", $"{lc} 30y",
                $"{lc} 1y1y", $"{lc} 5y5y", $"{lc} 2s10s", $"{lc} 5s30s", $"{lc} 10s30s",
            };
            // the ccy's FX crosses and xccy basis count as focus too (NOK → EURNOK, USDNOK)
            focus.AddRange(cc.Tickers
                .Where(t => (t.Class.Equals("fx", StringComparison.OrdinalIgnoreCase)
                             || t.Class.Equals("basis", StringComparison.OrdinalIgnoreCase))
                            && t.Label.Contains(ccy, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Label));
            // inflation ladder legs where the ccy has one (cpi/rpi/hicp — non-resolving names skip)
            foreach (var ln in new[] { "cpi", "rpi", "hicp" })
                focus.Add($"5y {lc} {ln}");

            var others = new List<string>(cc.Tickers.Select(t => t.Label));
            foreach (var c in Configs.Enabled.Where(c => c.Ois != null || c.Irs != null))
            {
                if (c.Ccy.Equals(ccy, StringComparison.OrdinalIgnoreCase)) continue;
                var olc = c.Ccy.ToLowerInvariant();
                others.Add($"{olc} 2y");
                others.Add($"{olc} 10y");
                others.Add($"{olc} 2s10s");
            }

            progress?.Invoke($"deep dive {ccy}: resolving series...");
            var fr = focus.Select(f => (key: f, s: ResolveCorrSeries(f, tickers, cache)))
                .Where(x => x.s != null).ToList();
            var others2 = others.Select(o => (key: o, s: ResolveCorrSeries(o, tickers, cache)))
                .Where(x => x.s != null).ToList();

            var cand = new List<(double score, CorrSeries a, CorrSeries b, string ka, string kb)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Consider(string ka, CorrSeries sa, string kb, CorrSeries sb)
            {
                if (sa.Class is "rates" or "infl" && sa.Class == sb.Class
                    && string.Equals(sa.Ccy, sb.Ccy, StringComparison.OrdinalIgnoreCase)) return;
                var dd = string.CompareOrdinal(ka.ToLowerInvariant(), kb.ToLowerInvariant()) <= 0
                    ? $"{ka}|{kb}" : $"{kb}|{ka}";
                if (!seen.Add(dd)) return;
                var (_, dx, dy) = Correlation.AlignedChanges(sa.Levels, sb.Levels, sa.Log, sb.Log);
                if (Correlation.Pearson(dx, dy, lrWin) is not double l || Math.Abs(l) < minAbsRho) return;
                cand.Add((Math.Abs(l), sa, sb, ka, kb));
            }
            foreach (var (ka, sa) in fr)
            {
                foreach (var (kb, sb) in others2) Consider(ka, sa!, kb, sb!);
                foreach (var (kb, sb) in fr)
                    if (!ReferenceEquals(sa, sb)) Consider(ka, sa!, kb, sb!);
            }

            progress?.Invoke($"deep dive {ccy}: {cand.Count} candidates ≥ |ρ| {minAbsRho:0.00}...");
            var rows = new List<CorrRow>();
            foreach (var c in cand.OrderByDescending(c => c.score))
            {
                if (rows.Count >= maxRows) break;
                var row = BuildCorrRow(c.a, c.b, $"deep dive {ccy}: |ρ| = {c.score:0.00}",
                    curated: false, nowWin, lrWin);
                if (row != null) rows.Add(row);
            }
            progress?.Invoke($"deep dive {ccy}: {rows.Count} rows");
            return rows.OrderByDescending(r => r.SortKey).ToList();
        }

        /// <summary>Ad-hoc pair: any two legs (query / known label / raw Bloomberg ticker),
        /// full statistics — the "how are front-end yields reacting to dairy futures" button.</summary>
        public CorrRow? CorrAdhoc(string a, string b, int nowWin = 63, int lrWin = 504)
        {
            if (History == null) return null;
            var cc = CorrStore.Load();
            var tickers = cc.Tickers.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var cache = new Dictionary<string, CorrSeries?>(StringComparer.OrdinalIgnoreCase);
            var sa = ResolveCorrSeries(a, tickers, cache);
            var sb = ResolveCorrSeries(b, tickers, cache);
            if (sa == null || sb == null) return null;
            return BuildCorrRow(sa, sb, "ad-hoc pair", curated: false, nowWin, lrWin);
        }

        /// <summary>One cell of the per-currency correlation snapshot matrix.</summary>
        public sealed class CorrSnapCell
        {
            public string RowLabel { get; init; } = "";
            public string ColLabel { get; init; } = "";
            public string LegA { get; init; } = "";
            public string LegB { get; init; } = "";
            public double? RhoNow { get; init; }
            public double? Rho6m { get; init; }
            /// <summary>Recent (1m) correlation sign contradicts the 6m sign, significantly.</summary>
            public bool SignFlip { get; init; }
            /// <summary>ρ computed on WEEKLY changes (stale daily marks — the board's fallback).</summary>
            public bool Weekly { get; init; }
        }

        /// <summary>Per-currency snapshot: front end / belly / long end / curves vs a fixed set of
        /// macro columns (FX, dollar, commodities, oil, credit, US and AUD duration by default;
        /// override via correlations.json "snapshot"). The ⚠ rule: 1m ρ sign against the 6m sign,
        /// gated by a Fisher test so a ±0.05 wobble around zero doesn't flag daily.</summary>
        public List<CorrSnapCell> CorrSnapshot(string ccy, int nowWin = 63)
        {
            var cells = new List<CorrSnapCell>();
            if (History == null) return cells;
            ccy = ccy.ToUpperInvariant();
            var cc = CorrStore.Load();
            var tickers = cc.Tickers.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var cache = new Dictionary<string, CorrSeries?>(StringComparer.OrdinalIgnoreCase);
            var lc = ccy.ToLowerInvariant();

            // long-end rows clamp to the currency's ACTUAL longest quoted pillar — otherwise a
            // 15y-max curve would silently show the 15y relabeled as "30y"
            double maxYSnap = 0;
            if (Configs.TryGet(ccy, out var cfgSnap))
            {
                var prSnap = cfgSnap.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfgSnap.Irs != null
                    ? ProductKind.IRS : cfgSnap.Ois != null ? ProductKind.OIS : ProductKind.IRS;
                maxYSnap = ((prSnap == ProductKind.OIS ? cfgSnap.Ois?.Curve : cfgSnap.Irs?.Curve)
                            ?? Enumerable.Empty<PillarDef>())
                    .Where(p => p.Enabled && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase) && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                    .Select(p => TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0)
                    .DefaultIfEmpty(0).Max();
            }
            int longT = maxYSnap >= 29.5 ? 30 : maxYSnap >= 19.5 ? 20 : maxYSnap >= 14.5 ? 15 : 0;
            var rowsList = new List<(string Label, string Spec)>
            {
                ("2y (front)", $"{lc} 2y"),
                ("5y (belly)", $"{lc} 5y"),
                ("10y", $"{lc} 10y"),
            };
            if (longT > 0) rowsList.Add(($"{longT}y (long)", $"{lc} {longT}y"));
            rowsList.Add(("2s10s", $"{lc} 2s10s"));
            if (longT > 0) rowsList.Add(($"10s{longT}s", $"{lc} 10s{longT}s"));
            var rows = rowsList.ToArray();
            var cols = new List<(string Label, string Spec)>();
            // its own FX cross first (NZD → NZDUSD; falls back to any cross containing the ccy)
            var fx = cc.Tickers.FirstOrDefault(t => t.Class.Equals("fx", StringComparison.OrdinalIgnoreCase)
                                                    && t.Label.Contains(ccy, StringComparison.OrdinalIgnoreCase)
                                                    && t.Label.Contains("USD", StringComparison.OrdinalIgnoreCase))
                     ?? cc.Tickers.FirstOrDefault(t => t.Class.Equals("fx", StringComparison.OrdinalIgnoreCase)
                                                       && t.Label.Contains(ccy, StringComparison.OrdinalIgnoreCase));
            if (fx != null) cols.Add((fx.Label, fx.Label));
            if (cc.Snapshot.Count > 0)
                cols.AddRange(cc.Snapshot.Select(s => (s.A, s.B.Length > 0 ? s.B : s.A)));
            else
                foreach (var d in new[] { "DXY", "BCOM", "Brent", "CDX IG 5y" })
                    cols.Add((d, d));
            // duration anchors last (skip when it IS the snapshot ccy)
            if (!ccy.Equals("USD", StringComparison.OrdinalIgnoreCase)) cols.Add(("US 10y", "usd 10y"));
            if (!ccy.Equals("AUD", StringComparison.OrdinalIgnoreCase)) cols.Add(("AU 10y", "aud 10y"));
            // dedupe by label: a configured column duplicating the auto FX pick must not produce
            // duplicate (row,col) cells (the grid keys on them)
            cols = cols.GroupBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();

            foreach (var (rowLabel, rowSpec) in rows)
            {
                var sa = ResolveCorrSeries(rowSpec, tickers, cache);
                if (sa == null) continue;
                foreach (var (colLabel, colSpec) in cols)
                {
                    var sb = ResolveCorrSeries(colSpec, tickers, cache);
                    if (sb == null) continue;
                    var (dts, dx, dy) = Correlation.AlignedChanges(sa.Levels, sb.Levels, sa.Log, sb.Log);
                    // stale daily marks crush the ρ — same weekly fallback as the main board
                    bool weekly = dx.Length / 5 >= 30
                                  && Math.Max(CorrStaleFrac(dx), CorrStaleFrac(dy)) > 0.30;
                    int wNow = nowWin, w1m = 21, w6m = 126, minN1 = 12, minN = 20;
                    if (weekly)
                    {
                        (dx, dy, _) = CorrWeeklyBlocks(dts, dx, dy);
                        wNow = Math.Max(10, nowWin / 5); w1m = Math.Max(10, w1m / 5);
                        w6m = 25; minN1 = 10; minN = 10;
                    }
                    var now = Correlation.Pearson(dx, dy, wNow, minN);
                    var m1 = Correlation.Pearson(dx, dy, w1m, minN1);
                    var m6 = Correlation.Pearson(dx, dy, w6m, minN);
                    bool flip = false;
                    if (m1 is double r1 && m6 is double r6
                        && r1 * r6 < 0 && Math.Abs(r6) >= 0.25 && Math.Abs(r1) >= 0.15)
                    {
                        double lag1 = Math.Max(Correlation.Autocorr1(dx, w6m), Correlation.Autocorr1(dy, w6m));
                        flip = Correlation.FisherT(r1, r6, w1m, w6m, lag1) is double tf && Math.Abs(tf) >= 1.5;
                    }
                    cells.Add(new CorrSnapCell
                    {
                        RowLabel = rowLabel, ColLabel = colLabel,
                        LegA = rowSpec, LegB = colSpec,
                        RhoNow = now, Rho6m = m6, SignFlip = flip, Weekly = weekly,
                    });
                }
            }
            return cells;
        }

        /// <summary>One-pair diagnostics: levels-ρ vs daily-Δ vs weekly-Δ correlations plus stale
        /// fractions — for judging whether a flagged break is real or a data/horizon artifact.</summary>
        public string CorrPairDiag(string a, string b)
        {
            var cc = CorrStore.Load();
            var tickers = cc.Tickers.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var cache = new Dictionary<string, CorrSeries?>(StringComparer.OrdinalIgnoreCase);
            var sa = ResolveCorrSeries(a, tickers, cache);
            var sb = ResolveCorrSeries(b, tickers, cache);
            if (sa == null || sb == null) return "series unavailable (check the leg specs)";
            var (dates, dx, dy) = Correlation.AlignedChanges(sa.Levels, sb.Levels, sa.Log, sb.Log);
            if (dx.Length < 60) return "not enough overlapping history";

            double Stale(double[] d)
            {
                int n = Math.Min(126, d.Length);
                int zc = 0;
                for (int i = d.Length - n; i < d.Length; i++)
                    if (Math.Abs(d[i]) < 1e-9) zc++;
                return (double)zc / n;
            }
            // levels correlation over the last 2y of aligned observations
            IReadOnlyList<HistPoint> TL(CorrSeries s0) => s0.Log
                ? s0.Levels.Where(p => p.Value > 0)
                    .Select(p => new HistPoint(p.Date, Math.Log(p.Value) * 100.0)).ToList()
                : s0.Levels;
            var (xa, yb) = Regression.AlignByDate(TL(sa), TL(sb));
            int nl = Math.Min(505, xa.Length);
            var lvlRho = Correlation.Pearson(xa[^nl..], yb[^nl..]);

            int nw = dx.Length / 5;
            var wdx = new double[nw];
            var wdy = new double[nw];
            for (int i = 0; i < nw; i++)
            {
                int end = dx.Length - (nw - 1 - i) * 5;
                for (int k = end - 5; k < end; k++) { wdx[i] += dx[k]; wdy[i] += dy[k]; }
            }
            string F(double? v) => v?.ToString("+0.00;-0.00") ?? "  n/a";
            return $"{sa.Label}  x  {sb.Label}   ({dx.Length} aligned daily changes)\n" +
                   $"  stale marks (126d): A {Stale(dx):P0}   B {Stale(dy):P0}\n" +
                   $"  LEVELS  rho 2y : {F(lvlRho)}   <- what a chart overlay shows\n" +
                   $"  daily-D rho    : 3m {F(Correlation.Pearson(dx, dy, 63))}   6m {F(Correlation.Pearson(dx, dy, 126))}   2y {F(Correlation.Pearson(dx, dy, 504))}\n" +
                   $"  weekly-D rho   : 3m {F(Correlation.Pearson(wdx, wdy, 13, 10))}   6m {F(Correlation.Pearson(wdx, wdy, 25, 10))}   2y {F(Correlation.Pearson(wdx, wdy, 101, 10))}";
        }

        // ---------- RV bubble map ----------

        /// <summary>Default bubble-map structures — the App seeds its editable list from these.</summary>
        public static readonly (string Label, string Cat, string Q)[] RvMapDefaults =
        {
            ("2s5s10s",         "Spot flies",          "2s5s10s"),
            ("5s10s30s",        "Spot flies",          "5s10s30s"),
            ("2s10s30s",        "Spot flies",          "2s10s30s"),
            ("5s7s10s",         "Spot flies",          "5s7s10s"),
            ("10s20s30s",       "Spot flies",          "10s20s30s"),
            ("1y fwd 2s5s10s",  "Forward flies",       "1y2y 1y5y 1y10y"),
            ("1y fwd 5s10s30s", "Forward flies",       "1y5y 1y10y 1y30y"),
            ("2y fwd 5s10s30s", "Forward flies",       "2y5y 2y10y 2y30y"),
            ("1y fwd 2s10s",    "Curve spreads (fwd)", "1y2y 1y10y"),
            ("1y fwd 5s10s",    "Curve spreads (fwd)", "1y5y 1y10y"),
            ("1y fwd 10s30s",   "Curve spreads (fwd)", "1y10y 1y30y"),
            ("2y fwd 10s30s",   "Curve spreads (fwd)", "2y10y 2y30y"),
            ("2y fwd 5s30s",    "Curve spreads (fwd)", "2y5y 2y30y"),
            ("1y1y",            "Forward rates",       "1y1y"),
            ("2y1y",            "Forward rates",       "2y1y"),
            ("3y1y",            "Forward rates",       "3y1y"),
            ("5y1y",            "Forward rates",       "5y1y"),
            ("5y5y",            "Forward rates",       "5y5y"),
            ("10y10y",          "Forward rates",       "10y10y"),
        };

        /// <summary>Bubble-map rows: standard structures on one curve, each with z-score (x),
        /// vol-adjusted 3m carry+roll (y) and correlation of daily changes to the ANCHOR
        /// structure over ~1y (bubble size). The anchor can live on any currency.</summary>
        public List<RvMapRow> RvMap(string ccy, string anchorQuery, int lookbackDays,
            IEnumerable<(string Label, string Cat, string Q)>? defs = null)
        {
            if (History == null) return new List<RvMapRow>();

            // live pillar grids are built per CURRENCY on demand — a USD map can carry
            // "eur 2s10s" or "chf 1y1y" bubbles priced off their own quotes
            var grids = new Dictionary<string, List<(double y, double v)>>(StringComparer.OrdinalIgnoreCase);
            List<(double y, double v)> GridFor(string c)
            {
                if (grids.TryGetValue(c, out var g)) return g;
                var pts = new List<(double y, double v)>();
                if (Configs.TryGet(c, out var cf))
                {
                    var sr = SourceFor(c);
                    var pr = cf.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cf.Irs != null
                        ? ProductKind.IRS : cf.Ois != null ? ProductKind.OIS : ProductKind.IRS;
                    var curve = (pr == ProductKind.OIS ? cf.Ois?.Curve : cf.Irs?.Curve)?
                        .Where(p => p.Enabled && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase) && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)
                                    // screen convention only: dual-band markets (AUD) quote two families
                                    // at one tenor and a mixed grid gets duplicate years ~26bp apart
                                    && (pr != ProductKind.IRS || cf.Irs == null || cf.Irs.Legs.Count < 2
                                        || Pricing.SwapBuilder.PillarBand(cf.Irs, p).Equals(
                                            Pricing.SwapBuilder.SelectIrsLeg(cf.Irs, TenorUtil.Parse(p.Tenor), null).FloatTenor,
                                            StringComparison.OrdinalIgnoreCase)))
                        ?? Enumerable.Empty<PillarDef>();
                    foreach (var p in curve)
                    {
                        double y = TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0;
                        if (y < 0.4) continue;
                        if (Snapshot.TryGetMid(ConfigStore.ResolveTicker(p.Ticker, sr), out var m))
                            pts.Add((y, m));
                    }
                    pts = pts.OrderBy(p => p.y).ToList();
                }
                grids[c] = pts;
                return pts;
            }
            double? LiveAt(List<(double y, double v)> pts, double t)
            {
                if (pts.Count < 2) return null;
                if (t <= pts[0].y) return pts[0].v;
                if (t >= pts[^1].y) return pts[^1].v;
                for (int i = 1; i < pts.Count; i++)
                    if (t <= pts[i].y)
                    {
                        var (x0, y0) = pts[i - 1];
                        var (x1, y1) = pts[i];
                        return y0 + (y1 - y0) * (t - x0) / (x1 - x0);
                    }
                return null;
            }
            double? LegVal(List<(double y, double v)> pts, double a, double tn)
            {
                if (a < 1e-9) return LiveAt(pts, tn);
                var la = LiveAt(pts, a);
                var lb = LiveAt(pts, a + tn);
                return la.HasValue && lb.HasValue ? ((a + tn) * lb.Value - a * la.Value) / tn : null;
            }

            // anchor accepts the SAME forms as the CORR legs: swap query ("eur 10s30s"), known
            // label ("Brent"), or raw Bloomberg ticker — not just swaps. Unresolved → NaN corr,
            // surfaced honestly rather than a silent constant.
            IReadOnlyList<HistPoint> anchor = Array.Empty<HistPoint>();
            bool anchorLog = false;
            try
            {
                var ccA = CorrStore.Load();
                var tickA = ccA.Tickers.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var cacheA = new Dictionary<string, CorrSeries?>(StringComparer.OrdinalIgnoreCase);
                var sa = ResolveCorrSeries(anchorQuery, tickA, cacheA);
                if (sa != null)
                {
                    anchor = sa.Levels;
                    anchorLog = sa.Log;
                }
            }
            catch { /* anchor optional */ }

            var rows = new List<RvMapRow>();
            foreach (var (label, _, q) in defs ?? RvMapDefaults)
            {
                try
                {
                    // a def carrying its OWN currency ("eur 2s10s") is used as-is; a generic def
                    // ("2s5s10s") is priced under the selected map currency
                    ParsedQuery pq;
                    string query;
                    try { pq = ParseQuery(q); query = q; }
                    catch { query = $"{ccy.ToLowerInvariant()} {q}"; pq = ParseQuery(query); }
                    var rowCcy = pq.Target.Ccy.ToUpperInvariant();
                    string disp = label.TrimStart();
                    if (!disp.StartsWith(rowCcy, StringComparison.OrdinalIgnoreCase))
                        disp = $"{rowCcy} {disp}";

                    var series = StructureSeriesBp(pq);
                    if (series.Count < 250) continue;

                    var w = pq.Legs.Count switch
                    {
                        1 => new[] { 1.0 },
                        2 => new[] { -1.0, 1.0 },
                        _ => new[] { -1.0, 2.0, -1.0 },
                    };
                    // live value + 3m-aged value from the quoted grid (RvScan's carry convention)
                    var grid = GridFor(pq.Target.Ccy);
                    double? now = 0, aged = 0;
                    for (int i = 0; i < pq.Legs.Count && now.HasValue && aged.HasValue; i++)
                    {
                        double tn = TenorUtil.ApproxMonths(pq.Legs[i].Tenor!) / 12.0;
                        double a = pq.Legs[i].StartKind == StartKind.Forward
                            ? TenorUtil.ApproxMonths(pq.Legs[i].ForwardStart!) / 12.0 : 0.0;
                        var vNow = LegVal(grid, a, tn);
                        var vAged = a >= 0.25 ? LegVal(grid, a - 0.25, tn) : LegVal(grid, 0, Math.Max(tn + a - 0.25, 0.5));
                        now = vNow.HasValue ? now + w[i] * vNow.Value : null;
                        aged = vAged.HasValue ? aged + w[i] * vAged.Value : null;
                    }
                    double? nowBp = now * 100.0;
                    double? roll3m = now.HasValue && aged.HasValue ? (now.Value - aged.Value) * 100.0 : null;

                    var s = SeriesStats.Compute(series, liveLast: nowBp, changeScale: 1.0);
                    double? z = lookbackDays >= 320 ? s.ZScore1y : lookbackDays >= 150 ? s.ZScore6m : s.ZScore3m;
                    z ??= s.ZScore1y ?? s.ZScore6m ?? s.ZScore3m;
                    if (z == null || roll3m == null) continue;

                    double vol = Math.Max(s.RealizedVol1yBp ?? 0, 5.0); // bp/yr floor keeps ratios sane
                    double volAdjCarry = roll3m.Value * 4.0 / vol;      // annualized carry per unit vol
                    var (_, ePnl, eShp, trend, _) = ExpectedValue(s, nowBp, roll3m, null);

                    double corr = double.NaN; // NaN = anchor didn't resolve (shown as n/a)
                    if (anchor.Count > 250)
                    {
                        var (_, dx, dy) = Correlation.AlignedChanges(series, anchor, false, anchorLog);
                        if (Correlation.Pearson(dx, dy, 252) is double rho) corr = rho;
                    }

                    rows.Add(new RvMapRow
                    {
                        Label = disp,
                        Category = pq.Legs.Count == 3 ? "Flies" : "Curves",
                        Query = query,
                        Z = z.Value, Z3m = s.ZScore3m, Z6m = s.ZScore6m, Z1y = s.ZScore1y,
                        VolAdjCarry = volAdjCarry, CorrToAnchor = corr, NowBp = nowBp,
                        EShp = eShp, Trend = trend, Roll3mBp = roll3m,
                    });
                }
                catch { /* one bad structure must not kill the map */ }
            }
            return rows;
        }
    }
}
