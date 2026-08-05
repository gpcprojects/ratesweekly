using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using QLNet;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.Trades;

namespace RateDesk.Core
{
    // ---------- data shapes for the Monitor and Meetings boards ----------

    public sealed class MonitorCell
    {
        public string Label { get; init; } = "";
        public double? MidPct { get; set; }
        public double? CoDBp { get; set; }
    }

    public sealed class MonitorColumn
    {
        public string Ccy { get; init; } = "";
        public List<MonitorCell> Tenors { get; } = new();
        public List<MonitorCell> Spreads { get; } = new();
        /// <summary>Annuity-less par forwards from the tenor mids (1y1y, 2y2y, 5y5y, 10y10y).</summary>
        public List<MonitorCell> Fwds { get; } = new();
    }

    public sealed class MeetingRow
    {
        public DateTime Date { get; init; }
        public double MidPct { get; init; }
        public double? PricedBp { get; init; }
        public double? StepBp { get; init; }
        public double? CoDBp { get; init; }
        /// <summary>Where the mid came from: the meeting-dated OIS ticker, or "curve" when implied.</summary>
        public string MidSource { get; init; } = "";
    }

    public sealed class MeetingRunResult
    {
        public string Name { get; init; } = "";
        public string Ccy { get; init; } = "";
        public string Header { get; init; } = "";
        public string RefName { get; init; } = "";
        public double? RefPct { get; set; }
        /// <summary>The ref rate was replaced by a manual override (post-decision, fixing not yet printed).</summary>
        public bool RefOverridden { get; set; }
        /// <summary>Next decision date + announcement time on the London clock.</summary>
        public DateTime? NextDecision { get; set; }
        public string DecisionTimeLondon { get; set; } = "";
        public List<MeetingRow> Rows { get; } = new();
        public string? Warning { get; set; }
        /// <summary>"tickers" (market meeting-dated OIS) / "curve" (our OIS fwd between ticker dates) / "schedule" (json dates).</summary>
        public string DatesSource { get; set; } = "";
    }

    public sealed class MeetingScheduleDef
    {
        public string Name { get; set; } = "";
        public string Ccy { get; set; } = "";
        public string Header { get; set; } = "";
        /// <summary>Meeting-dated OIS ticker patterns, {N} = meeting number (0 = run-down whose
        /// maturity is the next meeting). Tried in order per N (BOJ switches root at 10).</summary>
        public List<string> Tickers { get; set; } = new();
        /// <summary>STIR futures pattern ({MY} = month code + year digit, e.g. SSY{MY} Comdty) used
        /// for mids when the meeting OIS has no quote — SNB periods map onto the quarterly SARON strip.</summary>
        public string? FuturesPattern { get; set; }
        public string? RefTicker { get; set; }
        /// <summary>Ladder name whose strip is the POLICY curve for this central bank, when that is a
        /// different index from the currency's default OIS curve. USD is the case: tenor swaps and forwards
        /// are SOFR, but everything meeting-dated is Fed Funds — the board's own USSOFED{N} tickers and its
        /// FEDL01 reference are already EFFR, so a meeting trade must price on the FedFunds strip and not on
        /// SOFR. Null (the norm) means the currency's OIS curve IS the policy curve.</summary>
        public string? PolicyLadder { get; set; }
        /// <summary>Decision announcement time on the London clock, e.g. "19:00".</summary>
        public string DecisionTimeLondon { get; set; } = "";
        /// <summary>"" = meeting run; "fra" = curve-implied 3M forwards at quarterly IMM dates.</summary>
        public string Kind { get; set; } = "";
        /// <summary>Default pricing contributor for the meeting tickers ("" = composite). The
        /// composite drops thin meeting OIS (BOC run-down) that BMOD/NABZ carry live.</summary>
        public string Source { get; set; } = "";
        public List<DateTime> Dates { get; set; } = new();
        /// <summary>Trailing-year decision dates — only used to stitch ticker HISTORY across rolls.</summary>
        public List<DateTime> PastDates { get; set; } = new();
    }

    /// <summary>Central-bank decision dates: config\meetings.json next to the exe overrides the embedded list.
    /// Past dates are skipped at read time, so runs roll automatically after each meeting.</summary>
    public static class MeetingsStore
    {
        private sealed class FileShape { public List<MeetingScheduleDef> Runs { get; set; } = new(); }

        private static readonly Lazy<List<MeetingScheduleDef>> _schedules = new(Load);

        public static IReadOnlyList<MeetingScheduleDef> Schedules => _schedules.Value;
        public static string Origin { get; private set; } = "embedded";

        private static List<MeetingScheduleDef> Load()
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            string? json = null;
            var over = System.IO.Path.Combine(AppContext.BaseDirectory, "config", "meetings.json");
            if (File.Exists(over)) { json = File.ReadAllText(over); Origin = over; }
            if (json == null)
            {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream("RateDesk.Core.config.meetings.json");
                if (s != null) { using var r = new StreamReader(s); json = r.ReadToEnd(); }
            }
            if (json == null) return new List<MeetingScheduleDef>();
            var shape = JsonSerializer.Deserialize<FileShape>(json, opts);
            var runs = shape?.Runs ?? new List<MeetingScheduleDef>();
            // a date that has settled is a PAST date now: migrate it so the history stitcher and
            // the roll-day CoD correction stay current without anyone hand-editing pastDates after
            // every decision (BOJ's Jul-31 roll was missed exactly that way). 6-day dedup matches
            // the stitcher's clustering of ticker-maturity vs config dates for the same meeting.
            foreach (var s in runs)
                foreach (var d in s.Dates.Where(d => d.Date <= DateTime.Today))
                    if (!s.PastDates.Any(p => Math.Abs((p - d).TotalDays) <= 6))
                        s.PastDates.Add(d);
            return runs;
        }
    }

    public sealed partial class PricingService
    {
        /// <summary>Manual ref-rate overrides per run name (post-decision, before the fixing prints).
        /// Concurrent: written from the UI thread, read from the meetings worker.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, double> MeetingRefOverrides { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-run pricing-source overrides (run name → contributor mnemonic, "" = composite).</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> MeetingSourceOverrides { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        public string MeetingSrc(MeetingScheduleDef sched) =>
            MeetingSourceOverrides.TryGetValue(sched.Name, out var o) ? o : sched.Source ?? "";

        /// <summary>Full meeting-ticker security for pattern index n, on the run's active source.</summary>
        public string MeetingTick(MeetingScheduleDef sched, string pat, int n)
        {
            var src = MeetingSrc(sched);
            return pat.Replace("{N}", n.ToString()) + (src.Length > 0 ? " " + src : "") + " Curncy";
        }

        // prev-close curves rebuild once per day per (ccy, source) — PX_CLOSE_1D is static intraday
        private readonly Dictionary<(string ccy, string src), (DateTime day, CurveSet curves)> _prevCurveCache = new();

        private CurveSet? GetPrevCloseCurvesUnlocked(CurrencyConfig cfg, string src)
        {
            var key = (cfg.Ccy.ToUpperInvariant(), src.ToUpperInvariant());
            if (_prevCurveCache.TryGetValue(key, out var hit) && hit.day == DateTime.Today) return hit.curves;
            try
            {
                // only day-cache a CLEAN build — if any pillar lacked PX_CLOSE_1D (early morning,
                // partial snapshot) the live-mid substitute must not be frozen in for the whole day
                bool clean = true;
                var curves = CurveBuilder.Build(cfg, src, Snapshot, AdjustedToday(cfg),
                    (full, r) =>
                    {
                        if (Snapshot.Get(full)?.PrevClose is double pc) return pc / 100.0;
                        clean = false;
                        return r;
                    },
                    ExternalDiscountFor(cfg));
                if (clean) _prevCurveCache[key] = (DateTime.Today, curves);
                return curves;
            }
            catch { return null; }
        }

        // ---------- rates monitor ----------

        /// <summary>Mids + change-on-day for one currency's headline curve (default product's quotes,
        /// dated-ladder fallback for analytics-only ccys like BRL), plus curve spreads and par
        /// forwards. Tenor matching is nearest-within-tolerance so 28-day-period markets (MXN 26P
        /// ≈ 2Y) populate their columns. Values come straight from the snapshot.</summary>
        /// <summary>chgDays: 1 = change vs prior close (PX_CLOSE_1D); 7/31/93 = change vs the
        /// close N calendar days back from (raw) BDH history.</summary>
        public MonitorColumn MonitorFor(string ccy, string[] tenors, (string a, string b)[] spreads,
            int chgDays = 1)
        {
            var cfg = Configs.Get(ccy);
            var src = SourceFor(ccy);

            double? ChgBp(string ticker, double midPct)
            {
                if (History == null) return null;
                var h = History.GetDaily(ticker, 220);
                var target = DateTime.Today.AddDays(-chgDays);
                for (int i = h.Count - 1; i >= 0; i--)
                    if (h[i].Date <= target)
                        return (midPct - h[i].Value) * 100.0;
                return null;
            }

            // pillar list: default product, else the first quoted ladder (BRL DI).
            // Dual-band markets quote TWO families at one tenor (AUD 4Y-9Y q/q AND s/s, ~26bp apart),
            // so every pillar carries its band and whether that band is the tenor-rule (screen) one:
            // tenor ROWS read screen-convention pillars only; par-FORWARD rows read both endpoints
            // from the window tenor's own family, never straddling the basis.
            List<(double months, string full, bool natural, string? band)>? pillars = null;
            bool boardIrs = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null;
            bool multiBand = boardIrs && cfg.Irs!.Legs.Count > 1;
            var curve = boardIrs ? cfg.Irs!.Curve : cfg.Ois?.Curve ?? cfg.Irs?.Curve;
            if (curve != null)
                pillars = curve.Where(p => p.Enabled && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase) && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                    .Select(p =>
                    {
                        string? band = multiBand ? RateDesk.Core.Pricing.SwapBuilder.PillarBand(cfg.Irs!, p) : null;
                        bool natural = band == null || band.Equals(
                            RateDesk.Core.Pricing.SwapBuilder.SelectIrsLeg(cfg.Irs!, TenorUtil.Parse(p.Tenor), null).FloatTenor,
                            StringComparison.OrdinalIgnoreCase);
                        return (TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)), ConfigStore.ResolveTicker(p.Ticker, src), natural, band);
                    })
                    .ToList();
            else if (cfg.Ladders.Count > 0)
                pillars = cfg.Ladders[0].Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                    .Select(p => (TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)), ConfigStore.ResolveTicker(p.Ticker, ""), true, (string?)null))
                    .ToList();

            (double mid, double? cod)? QuoteAt(double wantMonths, string? band)
            {
                var near = pillars?
                    .Where(p => band == null ? p.natural : band.Equals(p.band, StringComparison.OrdinalIgnoreCase))
                    .Where(p => Math.Abs(p.months - wantMonths) <= Math.Max(1.5, wantMonths * 0.035))
                    .OrderBy(p => Math.Abs(p.months - wantMonths)).ToList();
                if (near is not { Count: > 0 } || Snapshot.Get(near[0].full) is not { } q || !q.Mid.HasValue)
                    return null;
                return (q.Mid.Value, chgDays <= 1 ? q.CoDBp : ChgBp(near[0].full, q.Mid.Value));
            }

            var col = new MonitorColumn { Ccy = ccy.ToUpperInvariant() };
            var byLabel = new Dictionary<string, MonitorCell>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tenors)
            {
                var cell = new MonitorCell { Label = t };
                if (QuoteAt(TenorUtil.ApproxMonths(TenorUtil.Parse(t)), null) is { } q)
                {
                    cell.MidPct = q.mid;
                    cell.CoDBp = q.cod;
                }
                col.Tenors.Add(cell);
                byLabel[t] = cell;
            }
            foreach (var (a, b) in spreads)
            {
                var cell = new MonitorCell { Label = $"{a.TrimEnd('Y', 'y')}s{b.TrimEnd('Y', 'y')}s" };
                if (byLabel.TryGetValue(a, out var ca) && byLabel.TryGetValue(b, out var cb)
                    && ca.MidPct.HasValue && cb.MidPct.HasValue)
                {
                    cell.MidPct = (cb.MidPct - ca.MidPct) * 100.0; // spread quoted in bp
                    if (ca.CoDBp.HasValue && cb.CoDBp.HasValue) cell.CoDBp = cb.CoDBp - ca.CoDBp;
                }
                col.Spreads.Add(cell);
            }
            // par forwards: f(A,B) = (B·rB − A·rA)/(B−A), CoD combined with the same weights.
            // Both endpoints come from the WINDOW tenor's own quote family (AUD 2y2y = 2Q & 4Q, not
            // the screen rows' 2Q & s/s 4Y — that straddle books the 3s6s basis into the forward).
            foreach (var (label, ta, tb, win) in new[]
                     { ("1y1y", "1Y", "2Y", "1Y"), ("2y2y", "2Y", "4Y", "2Y"), ("5y5y", "5Y", "10Y", "5Y"), ("10y10y", "10Y", "20Y", "10Y") })
            {
                var cell = new MonitorCell { Label = label };
                string? fwdBand = multiBand
                    ? RateDesk.Core.Pricing.SwapBuilder.SelectIrsLeg(cfg.Irs!, TenorUtil.Parse(win), null).FloatTenor
                    : null;
                double a = TenorUtil.ApproxMonths(TenorUtil.Parse(ta)) / 12.0;
                double b = TenorUtil.ApproxMonths(TenorUtil.Parse(tb)) / 12.0;
                if (QuoteAt(a * 12.0, fwdBand) is { } qa && QuoteAt(b * 12.0, fwdBand) is { } qb)
                {
                    cell.MidPct = (b * qb.mid - a * qa.mid) / (b - a);
                    if (qa.cod.HasValue && qb.cod.HasValue)
                        cell.CoDBp = (b * qb.cod.Value - a * qa.cod.Value) / (b - a);
                }
                col.Fwds.Add(cell);
            }
            return col;
        }

        /// <summary>"Things to flag": beta-conditional anomalies across the monitored currencies.
        /// Rule 1 — curve moves scored against the ccy's own 6m beta to its 10y move
        /// ("NZD 2s10s out-flattening 2.1σ vs its usual steepening-in-selloff beta").
        /// Rule 2 — relative performance vs the G3 average when there IS a common move
        /// ("GBP 2y underperforming the G3 selloff, +2.9σ vs +1.1σ avg").
        /// Only ≥1.5σ speaks; a quiet day says so explicitly.</summary>
        public List<string> MonitorFlags(IEnumerable<string> ccys, int maxFlags = 5)
        {
            var found = new List<(double score, string txt)>();
            if (History == null) return new List<string> { "no history provider" };

            // per ccy per tenor: aligned level history + today's move (bp) + daily vol (bp).
            // G3 legs always load — even under an EM/DM filter rule 2 needs a common move to be
            // relative TO — but only ccys in the passed universe are FLAGGED.
            var universe = new HashSet<string>(ccys, StringComparer.OrdinalIgnoreCase);
            var data = new Dictionary<string, Dictionary<int, (IReadOnlyList<HistPoint> hist, double today, double vol)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var ccy in universe.Union(new[] { "USD", "EUR", "JPY" }, StringComparer.OrdinalIgnoreCase))
            {
                if (!Configs.TryGet(ccy, out var cfg) || (cfg.Ois == null && cfg.Irs == null)) continue;
                var src = SourceFor(ccy);
                var product = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null
                    ? ProductKind.IRS : cfg.Ois != null ? ProductKind.OIS : ProductKind.IRS;
                var per = new Dictionary<int, (IReadOnlyList<HistPoint>, double, double)>();
                foreach (var t in new[] { 2, 10, 30 })
                {
                    var tkr = ResolvePillarTicker(cfg, product, TenorUtil.Parse($"{t}Y"), src);
                    if (tkr == null) continue;
                    var q = Snapshot.Get(tkr);
                    if (q?.Mid is not double mid || q.PrevClose is not double pc) continue;
                    // despiked: one bad print in the window distorts the beta and the vol
                    var h = Analytics.HistoryFilter.Despike(History.GetDaily(tkr, 220));
                    if (h.Count < 100) continue;
                    double vol = 0;
                    int n = Math.Min(126, h.Count - 1);
                    var d = new double[n];
                    for (int i = 0; i < n; i++)
                        d[i] = (h[h.Count - n + i].Value - h[h.Count - n + i - 1].Value) * 100.0;
                    double m = d.Average();
                    vol = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / Math.Max(1, n - 1));
                    if (vol < 0.3) continue; // stale marks — a conditional z would be meaningless
                    double today = (mid - pc) * 100.0;
                    // an EXACTLY unchanged mark on a market that normally moves is almost surely
                    // an unticked quote (pre-open, snapshot seeded from the close), not genuine
                    // outperformance — without this every illiquid ccy "outperforms" each G3 move
                    if (today == 0.0) continue;
                    per[t] = (h, today, vol);
                }
                if (per.Count > 0) data[ccy.ToUpperInvariant()] = per;
            }

            // rule 1: curve move conditional on the level move (per ccy)
            foreach (var (ccy, per) in data)
            {
                if (!universe.Contains(ccy)) continue; // G3 loaded only as the rule-2 baseline
                foreach (var (name, tA, tB) in new[] { ("2s10s", 2, 10), ("10s30s", 10, 30) })
                {
                    if (!per.TryGetValue(tA, out var a) || !per.TryGetValue(tB, out var b)) continue;
                    // regime word from the REGRESSOR leg (the long leg of this pair), so the
                    // narrative matches what the beta is actually conditioned on
                    string word = b.today >= b.vol ? "selloff" : b.today <= -b.vol ? "rally" : "move";
                    var (_, dxA, dxB) = Analytics.Correlation.AlignedChanges(a.hist, b.hist, false, false);
                    int n = Math.Min(126, dxA.Length);
                    if (n < 60) continue;
                    // OLS: Δspread on the long leg's Δ (the level driver for that curve segment)
                    var y = new double[n];
                    var x = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        y[i] = (dxB[dxB.Length - n + i] - dxA[dxA.Length - n + i]) * 100.0;
                        x[i] = dxB[dxB.Length - n + i] * 100.0; // long leg ≈ the level driver
                    }
                    double mx = x.Average(), my = y.Average();
                    double sxx = 0, sxy = 0;
                    for (int i = 0; i < n; i++) { sxx += (x[i] - mx) * (x[i] - mx); sxy += (x[i] - mx) * (y[i] - my); }
                    if (sxx < 1e-9) continue;
                    double beta = sxy / sxx, alpha = my - beta * mx;
                    double ss = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double e = y[i] - (alpha + beta * x[i]);
                        ss += e * e;
                    }
                    double sd = Math.Sqrt(ss / Math.Max(1, n - 2));
                    if (sd < 0.2) continue;
                    double sprToday = b.today - a.today;
                    double lvlToday = b.today;
                    double resid = sprToday - (alpha + beta * lvlToday);
                    double z = resid / sd;
                    if (Math.Abs(z) < 1.5) continue;
                    string dir = resid > 0 ? "out-steepening" : "out-flattening";
                    found.Add((Math.Abs(z),
                        $"{ccy} {name} {dir} {Math.Abs(z):0.0}σ vs its 6m beta in this {word} " +
                        $"({sprToday:+0.0;-0.0}bp curve on a {lvlToday:+0.0;-0.0}bp long-leg move)"));
                }
            }

            // rule 2: relative performance vs the G3 average when the move is real
            foreach (var tenor in new[] { 2, 10 })
            {
                var g3 = new[] { "USD", "EUR", "JPY" }
                    .Where(c => data.TryGetValue(c, out var p) && p.ContainsKey(tenor))
                    .Select(c => data[c][tenor].today / data[c][tenor].vol).ToList();
                if (g3.Count < 2) continue;
                double avg = g3.Average();
                if (Math.Abs(avg) < 1.0) continue; // no common move — nothing to be relative TO
                string word = avg > 0 ? "selloff" : "rally";
                foreach (var (ccy, per) in data)
                {
                    if (ccy is "USD" or "EUR" or "JPY") continue;
                    if (!per.TryGetValue(tenor, out var v)) continue;
                    double sig = v.today / v.vol;
                    double rel = sig - avg;
                    if (Math.Abs(rel) < 1.2) continue;
                    string perf = rel > 0 ? "underperforming" : "outperforming";
                    found.Add((Math.Abs(rel),
                        $"{ccy} {tenor}y {perf} the G3 {word} ({sig:+0.0;-0.0}σ vs {avg:+0.0;-0.0}σ avg)"));
                }
            }

            if (found.Count == 0)
                return new List<string> { "nothing unusual — moves are in line with 6m betas" };
            return found.OrderByDescending(f => f.score).Take(maxFlags).Select(f => f.txt).ToList();
        }

        // ---------- central-bank meeting runs ----------

        public List<MeetingRunResult> MeetingRuns(int maxRows = 10)
        {
            var outp = new List<MeetingRunResult>();
            foreach (var sched in MeetingsStore.Schedules)
            {
                MeetingRunResult res;
                try { res = MeetingRun(sched, maxRows); }
                catch (Exception ex)
                {
                    res = new MeetingRunResult { Name = sched.Name, Ccy = sched.Ccy, Header = sched.Header, Warning = ex.Message };
                }
                outp.Add(res);
            }
            return outp;
        }

        /// <summary>All meeting-dated OIS tickers for every run (N = 0..maxN), for snapshot/subscribe.
        /// Unknown candidates are harmless — the snapshot just marks them missing.</summary>
        public IEnumerable<string> MeetingTickers(int maxN = 12)
        {
            foreach (var sched in MeetingsStore.Schedules)
            {
                foreach (var pat in sched.Tickers)
                {
                    // explicit securities (the FRA-run IMM strips) carry no {N} — once is enough
                    if (!pat.Contains("{N}")) { yield return MeetingTick(sched, pat, 0); continue; }
                    for (int n = 0; n <= maxN; n++)
                        yield return MeetingTick(sched, pat, n);
                }
                if (!string.IsNullOrEmpty(sched.RefTicker)) yield return sched.RefTicker;
                if (!string.IsNullOrEmpty(sched.FuturesPattern))
                {
                    var q = new DateTime(DateTime.Today.Year, ((DateTime.Today.Month - 1) / 3) * 3 + 3, 1);
                    for (int i = 0; i < 14; i++)
                    {
                        yield return sched.FuturesPattern.Replace("{MY}", FutMy(q));
                        q = q.AddMonths(3);
                    }
                }
            }
        }

        private static string FutMy(DateTime quarterMonth) =>
            "FGHJKMNQUVXZ"[quarterMonth.Month - 1] + (quarterMonth.Year % 10).ToString();

        /// <summary>One CB run. Primary: Bloomberg meeting-dated OIS tickers — dates from MATURITY
        /// (ticker N matures at meeting N+1; the N=0 run-down matures at the FIRST meeting), mids from
        /// their live quotes, CoD vs their prior close. Fallbacks: mid implied off our bootstrapped OIS
        /// curve between those dates; hardcoded schedule dates only when tickers are absent entirely.
        /// Priced = mid − current fixing (bp); Step = per-meeting increment.</summary>
        /// <summary>Resolved meeting dates and the quotes they came from.</summary>
        public sealed class MeetingDatesResult
        {
            public Market.QuoteData?[] Quotes { get; init; } = Array.Empty<Market.QuoteData?>();
            public Dictionary<int, DateTime> Dates { get; init; } = new();
            /// <summary>True when ticker MATURITY fields carried the run (rather than meetings.json).</summary>
            public bool FromTickers { get; init; }
        }

        /// <summary>Future meeting DATES only — no curve, no mids, no live data required beyond what
        /// the snapshot already holds.
        ///
        /// <para>Extracted verbatim from <see cref="MeetingRun"/> because that method only populates
        /// its Rows AFTER computing a mid per meeting: with no live ticker and a failed OIS curve it
        /// breaks on the first row and returns empty even though the dates are perfectly well known
        /// from config/meetings.json. That is fine for the board (which needs the mid) but wrong for
        /// resolving a meeting ANCHOR — an IMM anchor needs zero live data, so a meeting anchor
        /// shouldn't either. Also makes the date logic unit-testable on its own.</para></summary>
        public MeetingDatesResult ResolveMeetingDates(MeetingScheduleDef sched, int maxRows = 10)
        {
            // meeting-dated OIS ticker per N (first pattern that has data in the snapshot)
            Market.QuoteData? Resolve(int n)
            {
                foreach (var pat in sched.Tickers)
                {
                    var q = Snapshot.Get(MeetingTick(sched, pat, n));
                    if (q != null && (q.Mid.HasValue || q.Maturity.HasValue)) return q;
                }
                return null;
            }

            // Meeting date N (1-based) = maturity of ticker N-1; the run-down (0) matures at meeting 1.
            // ALIAS GUARD: Bloomberg aliases past-the-end numbers back to #1 (USSOFED10 -> USSOFED1),
            // so maturities must strictly increase — the family ends at the first violation.
            var quotes = new Market.QuoteData?[maxRows + 2];
            var meetDates = new Dictionary<int, DateTime>();
            var lastMat = DateTime.MinValue;
            for (int n = 0; n <= maxRows + 1; n++)
            {
                var q = Resolve(n);
                quotes[n] = q;
                if (q?.Maturity is DateTime m)
                {
                    if (m > lastMat) { meetDates[n + 1] = m; lastMat = m; }
                    else { quotes[n] = null; break; }
                }
            }
            bool tickerDates = meetDates.Count >= 2;

            // fill gaps from the schedule: some families price beyond where MATURITY is populated
            // (EESF4A+, JYSOMPM4+), and without any tickers the schedule carries the whole run
            var schedDates = sched.Dates.Where(d => d.Date > DateTime.Today).OrderBy(d => d).ToList();
            var prevDate = DateTime.Today;
            bool havePrev = false;
            for (int n = 1; n <= maxRows + 1; n++)
            {
                if (meetDates.TryGetValue(n, out var known)) { prevDate = known; havePrev = true; continue; }
                // the 7-day guard de-duplicates against the PREVIOUS resolved meeting (ticker maturities
                // and config dates describe the same meeting a day or two apart). With no previous
                // meeting it must not apply, or an imminent one is silently dropped: on 28-Jul-26 the
                // 29-Jul-26 FOMC was skipped and the run started at SEP-26, which also made
                // "usd jul fomc 5y" anchor on JUL-27.
                var fill = schedDates.FirstOrDefault(d => havePrev ? d > prevDate.AddDays(7) : d > DateTime.Today);
                if (fill == default) break;
                meetDates[n] = fill;
                prevDate = fill;
                havePrev = true;
            }
            return new MeetingDatesResult { Quotes = quotes, Dates = meetDates, FromTickers = tickerDates };
        }

        /// <summary>Date of the meeting a month (and optional year) names, for anchoring a swap.
        /// Needs no mid, so it works when the board itself would come back empty.</summary>
        public DateTime MeetingDateFor(string runName, int month, int? year, out string label)
        {
            label = "";
            var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                s.Name.Equals(runName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"unknown central-bank run '{runName}'.");
            var dates = ResolveMeetingDates(sched).Dates.Values.OrderBy(d => d).ToList();
            var hit = dates.FirstOrDefault(d => d.Month == month && (year == null || d.Year == year));
            if (hit == default)
            {
                string mn = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat
                    .GetAbbreviatedMonthName(month);
                throw new InvalidOperationException(
                    $"{sched.Name}: no {mn}{(year != null ? $"-{year % 100:00}" : "")} meeting in the next "
                    + $"{dates.Count}{(dates.Count > 0 ? $" ({dates[0]:MMM-yy}..{dates[^1]:MMM-yy})" : "")}.");
            }
            label = $"{sched.Name} {hit:MMM-yy}".ToUpperInvariant();
            return hit;
        }

        public MeetingRunResult MeetingRun(MeetingScheduleDef sched, int maxRows = 10)
        {
            if (sched.Kind.Equals("fra", StringComparison.OrdinalIgnoreCase)) return FraRun(sched, maxRows);
            lock (_gate)
            {
                var cfg = Configs.Get(sched.Ccy);
                var src = SourceFor(sched.Ccy);
                // OIS curve is only needed for curve-implied fallback mids — the board must still
                // run ticker-only when the curve won't build (e.g. an OIS-family quote outage)
                CurveSet? curves = null;
                bool curveBuildFailed = false;
                if (cfg.Ois != null)
                    try { curves = GetCurvesUnlocked(cfg, src); }
                    catch { curveBuildFailed = true; /* ticker-only run */ }
                var ts = curves?.Ois;

                var res = new MeetingRunResult
                {
                    Name = sched.Name, Ccy = sched.Ccy.ToUpperInvariant(), Header = sched.Header,
                    RefName = sched.RefTicker ?? cfg.Ois?.OnFixingTicker ?? "",
                    DecisionTimeLondon = sched.DecisionTimeLondon,
                };
                if (!string.IsNullOrEmpty(res.RefName) && Snapshot.TryGetMid(res.RefName, out var fix))
                    res.RefPct = fix;
                // manual ref override: after a decision the fixing lags a day — Priced re-bases off this
                if (MeetingRefOverrides.TryGetValue(sched.Name, out var ovr))
                {
                    res.RefPct = ovr;
                    res.RefOverridden = true;
                }

                var resolved = ResolveMeetingDates(sched, maxRows);
                var quotes = resolved.Quotes;
                var meetDates = resolved.Dates;
                bool tickerDates = resolved.FromTickers;

                if (meetDates.Count == 0)
                {
                    res.Warning = "no meeting tickers and schedule exhausted — update config\\meetings.json";
                    return res;
                }
                if (meetDates.TryGetValue(1, out var next)) res.NextDecision = next;

                Calendar? cal = null;
                DayCounter? dcc = null;
                if (curves != null && cfg.Ois != null)
                {
                    Settings.setEvaluationDate(curves.AsOf);
                    cal = curves.Cal;
                    dcc = SwapBuilder.MakeOvernightIndex(cfg, cfg.Ois, cal, new Handle<YieldTermStructure>()).dayCounter();
                }
                Date Q(DateTime d) => cal!.adjust(new Date(d.Day, (Month)d.Month, d.Year), BusinessDayConvention.Following);
                double Fwd(YieldTermStructure c, DateTime a, DateTime b) =>
                    c.forwardRate(Q(a), Q(b), dcc!, Compounding.Simple, Frequency.Annual).value() * 100.0;
                CurveSet? prev = null;
                bool prevTried = false;
                YieldTermStructure? PrevTs()
                {
                    if (!prevTried) { prev = GetPrevCloseCurvesUnlocked(cfg, src); prevTried = true; }
                    return prev?.Ois;
                }

                // a decision settled since the previous close ⇒ every numbered ticker re-pointed:
                // N's own PrevClose belongs to the meeting N used to be, and yesterday's N is
                // today's N+1 — difference against THAT close instead
                bool rolled = RolledSincePrevClose(sched.Dates.Concat(sched.PastDates), cal);

                // Thin meeting OIS families misprint with a straight face: SKSF4A published a live
                // two-sided 1.387 between 1.848/2.086 neighbours (2026-08-03) — an impossible
                // inter-meeting rate. Interior ticker rows are judged against their QUOTED
                // NEIGHBOURS, not the curve (a year-turn pillar legitimately drags curve-implied
                // rates near December, which false-flagged good prints): rejected when >25bp from
                // the neighbour midpoint while the neighbours agree within 25bp of each other,
                // replaced by that midpoint and labelled. Edge rows are never judged — the front
                // meeting is the one that gaps for real.
                var tickMid = new double?[quotes.Length];
                for (int k = 0; k < quotes.Length; k++) tickMid[k] = quotes[k]?.Mid;
                (double v, bool rej) GuardedMid(int k)
                {
                    double m0 = tickMid[k]!.Value;
                    if (k - 1 >= 1 && tickMid[k - 1] is double a && k + 1 < tickMid.Length && tickMid[k + 1] is double b
                        && Math.Abs(a - b) * 100.0 < 25.0)
                    {
                        double mExp = (a + b) / 2.0;
                        if (Math.Abs(m0 - mExp) * 100.0 > 25.0) return (mExp, true);
                    }
                    return (m0, false);
                }

                double? prevPriced = null;
                for (int n = 1; n <= maxRows; n++)
                {
                    if (!meetDates.TryGetValue(n, out var d0)) break;
                    var q = quotes[n];
                    double mid;
                    string midSrc;
                    double? cod = null;
                    if (q?.Mid is double qm)
                    {
                        var (gm, rej) = GuardedMid(n);
                        mid = gm;
                        midSrc = rej ? $"interp (ticker {SignedBp((qm - gm) * 100.0)}bp off — rejected)" : "ticker";
                        cod = rej ? null
                            : rolled
                                ? (n + 1 < quotes.Length && quotes[n + 1]?.PrevClose is double pc
                                    ? (qm - pc) * 100.0 : null)
                                : q.CoDBp;
                    }
                    else if (!string.IsNullOrEmpty(sched.FuturesPattern) && d0.Month % 3 == 0
                             && Snapshot.Get(sched.FuturesPattern.Replace("{MY}", FutMy(new DateTime(d0.Year, d0.Month, 1))))
                                 is { Mid: double fpx } fq)
                    {
                        // STIR future covering the post-meeting quarter (SNB ≈ the SARON strip): rate = 100 − price
                        mid = 100.0 - fpx;
                        midSrc = "future";
                        if (fq.PrevClose is double fprev) cod = (fprev - fpx) * 100.0;
                    }
                    else
                    {
                        // curve-implied between this meeting and the next (last resort when nothing quotes)
                        if (ts == null) break; // no OIS curve (none configured, or build failed) — stop at the last quoted ticker
                        var d1 = meetDates.TryGetValue(n + 1, out var nx) ? nx : d0.AddDays(42);
                        try { mid = Fwd(ts, d0, d1); }
                        catch { break; }
                        midSrc = "curve";
                        if (PrevTs() is { } tp)
                        {
                            try { cod = (mid - Fwd(tp, d0, d1)) * 100.0; } catch { /* gap */ }
                        }
                    }
                    double? priced = res.RefPct.HasValue ? (mid - res.RefPct.Value) * 100.0 : null;
                    res.Rows.Add(new MeetingRow
                    {
                        Date = d0, MidPct = mid, PricedBp = priced,
                        StepBp = priced.HasValue && prevPriced.HasValue ? priced - prevPriced : null,
                        CoDBp = cod, MidSource = midSrc,
                    });
                    prevPriced = priced;
                }

                res.DatesSource = tickerDates ? "tickers" : "schedule";
                if (!tickerDates && res.Rows.Count > 0)
                    res.Warning = "dates from config\\meetings.json";
                if (res.Rows.Count == 0)
                    res.Warning = "no future meetings resolved — check config\\meetings.json";
                // silent truncation is worse than a warning: without the curve fallback the run
                // stops at the last quoted ticker, which reads like a complete run
                if (curveBuildFailed && res.Warning == null)
                    res.Warning = "curve build failed — run truncated to quoted tickers";
                return res;
            }
        }

        /// <summary>Signed bp display that never renders "-+0.0" (.NET section-format quirk on tiny negatives).</summary>
        public static string SignedBp(double? v) =>
            v.HasValue ? (v.Value >= 0 ? "+" : "-") + Math.Abs(v.Value).ToString("0.0") : "";

        /// <summary>True when a numbered/generic family re-pointed since the previous close — i.e. a
        /// roll boundary (meeting settlement / IMM expiry) fell AFTER the previous business day.
        /// On that day ticker N's own PX_CLOSE_1D belongs to the instrument N pointed at YESTERDAY,
        /// so a naive CoD differences two different meetings: the first session after the Jul-31 BOJ,
        /// JYSOMPM1 (now SEP) printed 1.104 vs a 0.980 close that was the JUL period — +12.4bp of
        /// phantom CoD on every row. The families roll intraday ON the boundary date (JYSOMPM1's
        /// maturity had already moved by the Monday open), so: rolled iff lastBoundary &gt; prev
        /// business day, boundary ≤ today. The day after, PrevClose is post-roll and the naive CoD
        /// is right again — FOMC (rolled the previous Wednesday) must NOT trigger this on Monday.</summary>
        private static bool RolledSincePrevClose(IEnumerable<DateTime> boundaries, Calendar? cal)
        {
            DateTime last = DateTime.MinValue;
            foreach (var b in boundaries)
                if (b.Date <= DateTime.Today && b.Date > last) last = b.Date;
            if (last == DateTime.MinValue) return false;
            DateTime prevBd;
            if (cal != null)
            {
                var q = cal.advance(new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year),
                    -1, TimeUnit.Days);
                prevBd = new DateTime(q.year(), q.month(), q.Day);
            }
            else
            {
                prevBd = DateTime.Today.AddDays(-1);
                while (prevBd.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) prevBd = prevBd.AddDays(-1);
            }
            return last > prevBd;
        }

        /// <summary>IMM FRA run: 3M forwards at quarterly IMM dates, implied off the bootstrapped
        /// curve's 3M projection (SEK STIBOR / NOK NIBOR). Ref = the 3M fixing; CoD vs prev closes.</summary>
        private MeetingRunResult FraRun(MeetingScheduleDef sched, int maxRows)
        {
            lock (_gate)
            {
                var cfg = Configs.Get(sched.Ccy);
                var src = SourceFor(sched.Ccy);
                var curves = GetCurvesUnlocked(cfg, src);
                var res = new MeetingRunResult
                {
                    Name = sched.Name, Ccy = sched.Ccy.ToUpperInvariant(), Header = sched.Header,
                    RefName = sched.RefTicker ?? "",
                };
                if (!string.IsNullOrEmpty(res.RefName) && Snapshot.TryGetMid(res.RefName, out var fix))
                    res.RefPct = fix;
                if (MeetingRefOverrides.TryGetValue(sched.Name, out var ovr))
                {
                    res.RefPct = ovr;
                    res.RefOverridden = true;
                }

                Settings.setEvaluationDate(curves.AsOf);
                var cal = curves.Cal;
                var dcc = new Actual360();
                // 3M band where its quotes cover the date, else the full-term curve
                YieldTermStructure ProjAt(double years)
                {
                    try
                    {
                        var (h, _) = curves.ProjectionFor("3M", years);
                        if (!h.empty()) return h.currentLink();
                    }
                    catch { /* fall through */ }
                    return curves.Irs ?? curves.Ois
                        ?? throw new InvalidOperationException($"{sched.Ccy}: no curve for FRA run");
                }

                // quarterly IMM dates (3rd Wednesday of Mar/Jun/Sep/Dec)
                static DateTime Imm(int y, int m)
                {
                    var d = new DateTime(y, m, 15);
                    while (d.DayOfWeek != DayOfWeek.Wednesday) d = d.AddDays(1);
                    return d;
                }
                var imms = new List<DateTime>();
                var q = DateTime.Today;
                for (int i = 0; imms.Count < maxRows && i < 40; i++)
                {
                    int mm = ((q.Month - 1) / 3) * 3 + 3;
                    var candidate = Imm(q.Year, mm);
                    if (candidate > DateTime.Today && !imms.Contains(candidate)) imms.Add(candidate);
                    q = new DateTime(q.Year, mm, 1).AddMonths(3);
                }

                Date Q(DateTime d) => cal.adjust(new Date(d.Day, (Month)d.Month, d.Year), BusinessDayConvention.Following);
                double FwdAt(CurveSet set, DateTime a)
                {
                    double years = (a.AddMonths(3) - DateTime.Today).TotalDays / 365.25;
                    YieldTermStructure c;
                    if (ReferenceEquals(set, curves)) c = ProjAt(years);
                    else
                    {
                        try
                        {
                            var (h, _) = set.ProjectionFor("3M", years);
                            c = !h.empty() ? h.currentLink() : set.Irs ?? set.Ois!;
                        }
                        catch { c = set.Irs ?? set.Ois!; }
                    }
                    return c.forwardRate(Q(a), Q(a.AddMonths(3)), dcc, Compounding.Simple, Frequency.Annual).value() * 100.0;
                }

                var prev = GetPrevCloseCurvesUnlocked(cfg, src);

                // REAL IMM FRA quotes when the sched lists the contracts (NKF30001../SKF30001..),
                // matched to each row BY MATURITY — the numbers are rolling generics, and the rule
                // stands: use a ticker only when its own MATURITY equals the period being assigned.
                // Without this NOK's rows were curve-implied off a curve with no 3M band, i.e. 3M
                // rows read from the 6M NIBOR curve, ~a 3s6s too high against the NIBOR3M ref.
                var strip = sched.Tickers
                    .Select(t => Snapshot.Get(MeetingTick(sched, t, 0)))
                    .Where(q2 => q2?.Maturity != null)
                    .OrderBy(q2 => q2!.Maturity!.Value).ToList();
                // contracts expire intraday on their IMM start — same roll discipline as meetings
                var pastImms = new List<DateTime>();
                for (var pq0 = DateTime.Today.AddMonths(-4); pq0 <= DateTime.Today; pq0 = pq0.AddDays(1))
                    if (pq0.Month % 3 == 0 && pq0 == Imm(pq0.Year, pq0.Month)) pastImms.Add(pq0);
                bool rolled = strip.Count > 0 && RolledSincePrevClose(pastImms, cal);

                res.NextDecision = null;
                double? prevPriced = null;
                foreach (var d0 in imms)
                {
                    double mid;
                    string midSrc;
                    double? cod = null;
                    var end = d0.AddMonths(3);
                    int si = strip.FindIndex(q2 => Math.Abs((q2!.Maturity!.Value - end).TotalDays) <= 12);
                    if (si >= 0 && strip[si]!.Mid is double sm)
                    {
                        mid = sm;
                        midSrc = "ticker";
                        // on the roll day, yesterday's holder of this maturity was the NEXT generic
                        cod = rolled
                            ? (si + 1 < strip.Count && strip[si + 1]!.PrevClose is double pc
                                ? (sm - pc) * 100.0 : null)
                            : strip[si]!.CoDBp;
                    }
                    else if (strip.Count > 0)
                    {
                        // the quoted strip has run out — curve rows here would silently change basis
                        // (NOK has no 3M band to imply from), so say so and stop
                        res.Warning = $"run ends at the quoted {sched.Ccy} 3M IMM strip ({res.Rows.Count} contracts)";
                        break;
                    }
                    else
                    {
                        // no contracts configured: curve-implied as before
                        try { mid = FwdAt(curves, d0); }
                        catch { break; }
                        midSrc = "curve";
                        if (prev != null)
                        {
                            try { cod = (mid - FwdAt(prev, d0)) * 100.0; } catch { /* gap */ }
                        }
                    }
                    double? priced = res.RefPct.HasValue ? (mid - res.RefPct.Value) * 100.0 : null;
                    res.Rows.Add(new MeetingRow
                    {
                        Date = d0, MidPct = mid, PricedBp = priced,
                        StepBp = priced.HasValue && prevPriced.HasValue ? priced - prevPriced : null,
                        CoDBp = cod, MidSource = midSrc,
                    });
                    prevPriced = priced;
                }
                res.DatesSource = "imm";
                return res;
            }
        }

        /// <summary>Meeting-dated structure ("jul fomc" / "jul sep boe" / "jul sep dec ecb"):
        /// legs are meeting-period rates from the run; 2/3 legs quote the spread/fly in bp.</summary>
        internal Analytics.InstrumentResult AnalyzeMeeting(Query.ParsedQuery pq)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sched = MeetingsStore.Schedules.First(s => s.Name == pq.MeetingRun);
            var run = MeetingRun(sched, 12);
            if (run.Rows.Count == 0)
                throw new InvalidOperationException($"{sched.Name}: no meeting data ({run.Warning}).");

            var chosen = new List<MeetingRow>();
            foreach (var (month, year) in pq.MeetingMonths!)
            {
                var row = run.Rows.FirstOrDefault(r => r.Date.Month == month && (year == null || r.Date.Year == year));
                if (row == null)
                    throw new InvalidOperationException(
                        $"{sched.Name}: no {System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month)}" +
                        $"{(year != null ? $"-{year % 100:00}" : "")} meeting in the next {run.Rows.Count} ({run.Rows[0].Date:MMM-yy}..{run.Rows[^1].Date:MMM-yy}).");
                if (chosen.Any(c => c.Date == row.Date))
                    throw new InvalidOperationException($"{sched.Name}: duplicate meeting {row.Date:dd-MMM-yy}.");
                chosen.Add(row);
            }
            chosen.Sort((a, b) => a.Date.CompareTo(b.Date));

            int n = chosen.Count;
            double[] w = n switch { 1 => new[] { 1.0 }, 2 => new[] { -1.0, 1.0 }, _ => new[] { -1.0, 2.0, -1.0 } };
            double level = n == 1 ? chosen[0].MidPct
                : chosen.Select((r, i) => w[i] * r.MidPct).Sum() * 100.0;

            var res = new Analytics.InstrumentResult
            {
                Query = pq.Raw,
                Label = $"{sched.Name} {string.Join(" / ", chosen.Select(r => r.Date.ToString("MMM-yy").ToUpperInvariant()))}"
                        + (n == 2 ? " meeting spread" : n == 3 ? " meeting fly" : " meeting"),
                Ccy = run.Ccy, Kind = n == 1 ? "Meeting" : n == 2 ? "Meeting spread" : "Meeting fly",
                Unit = n == 1 ? "%" : "bp",
                Source = "meeting OIS" + (MeetingSrc(sched).Length > 0 ? $" ({MeetingSrc(sched)})" : ""),
                // headline stays clean/screenshottable — only the ref, no run-header echo
                ConventionSummary = run.RefPct.HasValue
                    ? $"ref {run.RefPct.Value:0.000} ({(run.RefOverridden ? "manual" : run.RefName)})"
                    : "",
                Mid = level, ParRatePct = level,
            };
            // meeting-period OIS risk: P&L per 1bp = notional × yearFrac(meeting → next meeting,
            // index dcc ~ ACT/360) × 1e-4 — the same dates shown as Effective/Maturity on the legs.
            // The period's payoff settles at the period END, so P&L per bp is discounted to the
            // payment date off the OIS curve; undiscounted DV01 overstates blotter NPV by ~DF.
            double legNotional = pq.Notional > 0 ? pq.Notional : 10_000_000;
            string? meetingFxNote = null;
            double Df(DateTime periodEnd)
            {
                lock (_gate)
                {
                    var cfg = Configs.Get(sched.Ccy);
                    if (cfg.Ois != null)
                        try
                        {
                            var set = GetCurvesUnlocked(cfg, SourceFor(sched.Ccy));
                            Settings.setEvaluationDate(set.AsOf);
                            if (set.Ois is { } ois)
                                return ois.discount(new Date(periodEnd.Day, (Month)periodEnd.Month, periodEnd.Year));
                        }
                        catch { /* simple-rate fallback below */ }
                }
                // no OIS curve (none configured, or build failed): 1/(1 + ref·t), t ACT/360 to pay date
                double t = Math.Max(0.0, (periodEnd - DateTime.Today).TotalDays) / 360.0;
                return run.RefPct is double rp ? 1.0 / (1.0 + rp / 100.0 * t) : 1.0;
            }
            // path F: size off the period's OWN density rather than a flat notional, so an unsized
            // meeting query carries the same desk risk as an unsized swap and "dv01 50k" works here
            // too. Density per 1mm = accrual × DF, i.e. exactly the dv01 formula below at 1mm.
            double DensityFor(MeetingRow rowAt)
            {
                var nxt = run.Rows.FirstOrDefault(r => r.Date > rowAt.Date);
                return nxt != null
                    ? 1_000_000.0 * (nxt.Date - rowAt.Date).TotalDays / 360.0 * 1e-4 * Df(nxt.Date)
                    : 0.0;
            }
            if (pq.LegNotionals is { Count: > 0 } exactNot)
            {
                legNotional = exactNot[0];   // blotter's exact channel — never resized or rounded
            }
            else if ((pq.LegDv01s is { Count: > 0 } || pq.Dv01Target.HasValue)
                     && chosen.Select(DensityFor).FirstOrDefault(d => d > 0) is > 0 and var dens)
            {
                double want = pq.LegDv01s is { Count: > 0 } pl ? pl[0] : pq.Dv01Target!.Value;
                try
                {
                    double fx = FxRiskFactor(pq.Dv01Ccy, run.Ccy);
                    if (!pq.Dv01Ccy.Equals(run.Ccy, StringComparison.OrdinalIgnoreCase))
                        meetingFxNote = $"dv01 input in {pq.Dv01Ccy} × {fx:0.####} → {run.Ccy}/bp";
                    legNotional = Risk.RiskSizer.Resolve(dens, explicitDv01: want * fx).Notional;
                }
                catch (Exception ex)
                {
                    meetingFxNote = $"dv01 target not applied — {ex.Message}; showing the flat notional.";
                }
            }

            for (int i = 0; i < n; i++)
            {
                var next = run.Rows.FirstOrDefault(r => r.Date > chosen[i].Date);
                double dv01 = next != null
                    ? legNotional * (next.Date - chosen[i].Date).TotalDays / 360.0 * 1e-4 * Df(next.Date) : 0.0;
                res.Legs.Add(new Analytics.LegResult
                {
                    Label = chosen[i].Date.ToString("dd-MMM-yy"),
                    Weight = w[i],
                    Effective = new Date(chosen[i].Date.Day, (Month)chosen[i].Date.Month, chosen[i].Date.Year),
                    Maturity = next != null
                        ? new Date(next.Date.Day, (Month)next.Date.Month, next.Date.Year) : new Date(),
                    RatePct = chosen[i].MidPct,
                    Notional = legNotional,
                    Dv01 = dv01,
                    DensityPerMm = dv01 > 0 ? dv01 / (legNotional / 1_000_000.0) : 0.0,
                    HistoryNote = chosen[i].MidSource,
                });
            }
            // structure dv01 basis so NET DV01/tiles and +watch sizing see risk — per unit weight,
            // meeting spreads/flies trade ±1 per leg; legs with an unknown period carry no risk
            var riskLegs = res.Legs.Where(l => l.Dv01 > 0).ToList();
            if (riskLegs.Count > 0) res.StructDv01 = riskLegs.Average(l => l.Dv01);
            if (n == 1 && res.Legs[0].Dv01 > 0) res.Dv01 = res.Legs[0].Dv01;
            if (riskLegs.Count > 0) res.Notes.Add("meeting P&L discounted to period end");
            if (meetingFxNote != null) res.Notes.Add(meetingFxNote);
            if (n == 1 && run.RefPct.HasValue)
                res.Notes.Add($"priced {SignedBp((level - run.RefPct.Value) * 100.0)} bp vs {run.RefName}" +
                              (run.RefOverridden ? " (ref OVERRIDDEN)" : ""));

            // history: stitch the generic ticker series so each history date uses the ticker index
            // that POINTED AT this meeting on that date — indices shift down after every decision
            if (!pq.SkipHistory)
            try
            {
                var stitched = MeetingSeriesBuilder(sched, run.Rows.Select(r => r.Date));
                var legSeries = chosen.Select(c => stitched(c.Date)).ToList();
                if (legSeries.All(s2 => s2.Count >= 10))
                {
                    var combined = HistoryFilter.Despike(
                        CombineSeries(legSeries, w, scaleToBp: n > 1), window: 7, k: 4,
                        madFloorPct: n > 1 ? 0.5 : 0.005, passes: 2);
                    if (combined.Count >= 10)
                    {
                        // stats on the full stitched window; the chart shows the lookback slice
                        res.History = SliceLookback(combined);
                        ApplyMidOverride(pq, res);
                        res.Stats = Analytics.SeriesStats.Compute(combined, liveLast: res.Mid ?? level,
                            changeScale: n > 1 ? 1.0 : 100.0);
                        // exact Δ 1d from the run rows (live mid vs prev close) — the stitched
                        // series' last point can predate today, which skews a history-based 1d
                        if (chosen.All(c => c.CoDBp.HasValue))
                            res.Stats.Chg1d = chosen.Select((c, i) => w[i] * c.CoDBp!.Value).Sum() + OvrShiftBp(res);
                        res.Notes.Add("history stitched across ticker rolls at decision dates (past dates from config).");
                    }
                }
            }
            catch { /* history is best-effort for meeting structures */ }
            ApplyMidOverride(pq, res); // no-op when already applied ahead of the stats pass
            if (res.History.Count == 0)
                res.Notes.Add("no stitched history available for this structure.");
            res.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        /// <summary>Meeting-CONSTANT history for any meeting in a run: stitches the generic ticker
        /// series so each history date reads the ticker index that POINTED AT that meeting on that
        /// date — indices shift down after every decision, and a naive single-ticker BDH would splice
        /// two different meetings at each roll. One batched prefetch per builder; call the returned
        /// func per meeting. Used by the pricer's meeting charts AND the weekly report's 1w/1m
        /// changes, so both stay roll-safe by construction.</summary>
        internal Func<DateTime, IReadOnlyList<HistPoint>> MeetingSeriesBuilder(
            MeetingScheduleDef sched, IEnumerable<DateTime> runDates)
        {
            // warm every ticker index the stitching can touch in one batched BDH round-trip
            try
            {
                History?.Prefetch(sched.Tickers.Where(p => p.Contains("{N}")).SelectMany(p =>
                    Enumerable.Range(1, 13).Select(i => p.Replace("{N}", i.ToString()) + " Curncy")), 1825);
            }
            catch { /* per-ticker fallback */ }
            // cluster within 6 days: ticker maturities and config dates describe the SAME meeting
            // with day-level differences — double-counting would shift every stitch index by one
            var allMeet = new List<DateTime>();
            foreach (var d in sched.PastDates.Concat(runDates)
                         .Concat(sched.Dates).Distinct().OrderBy(x => x))
                if (allMeet.Count == 0 || (d - allMeet[^1]).TotalDays > 6)
                    allMeet.Add(d);
            return meeting =>
            {
                var upTo = allMeet.Where(m => m <= meeting).ToList();
                var pts = new List<HistPoint>();
                for (int i = upTo.Count - 2; i >= 0; i--)
                {
                    int idx = upTo.Count - 1 - i; // in [upTo[i], upTo[i+1]) this meeting is the idx-th next
                    if (idx > 13) break;
                    IReadOnlyList<HistPoint>? h = null;
                    foreach (var pat in sched.Tickers)
                    {
                        if (!pat.Contains("{N}")) continue; // explicit FRA-strip securities don't renumber this way
                        var cand = Hist(pat.Replace("{N}", idx.ToString()) + " Curncy", full: true);
                        if (cand.Count > 0) { h = cand; break; }
                    }
                    if (h == null) continue;
                    var lo = upTo[i];
                    var hi = upTo[i + 1];
                    pts.InsertRange(0, h.Where(p => p.Date >= lo && p.Date < hi));
                }
                return pts;
            };
        }

        /// <summary>Chat-paste text for one run, monospace-aligned.</summary>
        public static string MeetingRunText(MeetingRunResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(r.Header);
            if (r.RefPct.HasValue) sb.Append($"   ref {r.RefPct.Value:0.000}");
            sb.AppendLine();
            sb.AppendLine($"{"Meeting",-11} {"Mid",7} {"Priced",8} {"Step",7} {"CoD",6}");
            foreach (var row in r.Rows)
                sb.AppendLine($"{row.Date:dd-MMM-yy}   {row.MidPct,7:0.000} {SignedBp(row.PricedBp),8} {SignedBp(row.StepBp),7} {SignedBp(row.CoDBp),6}");
            return sb.ToString();
        }
    }
}
