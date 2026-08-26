using System.Globalization;
using System.Text.Json;
using RateDesk.Core;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>One ranked instrument on the movers page.</summary>
    public sealed record Mover(
        string Ccy, string Group, string Kind, string Label, string PageFile,
        double LevelNow, string LevelText, string RangeText,
        double W1Bp, double? M1Bp,
        double Z, bool ZIsEst, double SigmaBp, double? SigmaWeeklyBp,
        double? VolRatio,
        IReadOnlyList<HistPoint> Spark);

    public sealed class MoversResult
    {
        public required DateTime AsOf { get; init; }
        /// <summary>Full ranked lists, biggest |z| first. EM here means everything non-DM
        /// (EM + LATAM + ASIA EM), the page's two-section spec.</summary>
        public required List<Mover> DmRanked { get; init; }
        public required List<Mover> EmRanked { get; init; }
        public required List<Mover> DmHeroes { get; init; }
        public required List<Mover> EmHeroes { get; init; }
        public string? G3Line { get; init; }
        public required string MethodNote { get; init; }
        public List<string> Notes { get; init; } = new();
        public required string Headline { get; init; }
    }

    /// <summary>Outsized weekly movers across the whole monitored universe, straight off the
    /// history store — the RatesWeekly descendant of dodgeball's "things to flag" scan, weekly
    /// horizon instead of daily.
    ///
    /// The question a rates PM actually asks is not "what moved most" but "what moved most
    /// RELATIVE TO WHAT IT USUALLY DOES" — 9bp of MXN is noise, 9bp of CHF is a story. So the
    /// primary metric is z = Δ1w / σ(weekly changes). The strict σ (DESIGN §5) needs ≥40 weekly
    /// observations, which the 45-day store cannot supply yet; until the deep seed it is
    /// ESTIMATED as √5 × the σ of daily changes over the stored window and every figure derived
    /// from it is marked "est". The estimate understates σ when weekly changes trend (positive
    /// autocorrelation) and overstates it when they mean-revert — honest to within the label.
    /// When the store deepens past the gate the true weekly σ takes over per instrument, no code
    /// change (the same self-lighting rule as the corr panels).
    ///
    /// Guards inherited from dodgeball's MonitorFlags, all learned live: despike BEFORE any σ
    /// (one bad print poisons both the vol and the move); a σ floor of 0.3bp/day (stale marks
    /// make any z meaningless); a freshness gate (a close >4 days old is a dead quote, and its
    /// "move" is staleness); and for meeting rows, rows whose print failed the neighbour guard
    /// are excluded outright — a guarded print must never rank as a mover.</summary>
    public static class MoverScan
    {
        /// <summary>Pull window. Sized so the weekly-σ gate lights the moment the store can feed
        /// it, not when someone remembers to widen a constant.</summary>
        private const int WindowDays = 400;

        private const int MinDailyObs = 20;
        private const int MinWeeklyObs = 40;
        private const double SigmaFloorBp = 0.3;
        private const int StaleDays = 4;
        private const int SparkDays = 70;      // ~45 business days

        public sealed record ScanStats(
            double LevelNow, double W1Bp, double? M1Bp,
            double? SigmaWeeklyBp, double SigmaEstBp, double Z, bool ZIsEst,
            double? VolRatio, IReadOnlyList<HistPoint> Spark);

        /// <summary>Stats for one candidate series (values in the series' own units,
        /// <paramref name="scaleToBp"/> converts a unit difference to bp). Null = not rankable:
        /// too thin, stale, vol-floored, or no resolvable weekly change. Pure — all the gates
        /// live here, which is what makes them testable.</summary>
        public static ScanStats? Stats(IReadOnlyList<HistPoint> raw, double scaleToBp, DateTime asOf)
        {
            if (raw.Count < 2) return null;
            var h = HistoryFilter.Despike(raw);
            if (h.Count < 2) return null;
            if ((asOf.Date - h[^1].Date.Date).TotalDays > StaleDays) return null;

            double? At(DateTime d)
            {
                for (int i = h.Count - 1; i >= 0; i--)
                    if (h[i].Date.Date <= d.Date) return h[i].Value;
                return null;
            }

            if (At(asOf) is not { } now) return null;
            if (At(asOf.AddDays(-WeeklyCurves.WeekDays)) is not { } wAgo) return null;
            double w1 = (now - wAgo) * scaleToBp;
            double? m1 = At(WeeklyCurves.MonthAgo(asOf)) is { } mAgo
                ? (now - mAgo) * scaleToBp : null;

            var diffs = new List<double>(h.Count);
            for (int i = 1; i < h.Count; i++)
                diffs.Add((h[i].Value - h[i - 1].Value) * scaleToBp);
            if (diffs.Count < MinDailyObs) return null;

            double sdDaily = Std(diffs.TakeLast(126).ToList());
            if (sdDaily < SigmaFloorBp) return null;
            double sigmaEst = sdDaily * Math.Sqrt(5.0);

            // how hard the LAST week traded vs the norm — ROOT MEAN SQUARE of daily changes, not
            // their dispersion: a steady +4bp-every-day week has σ≈0 around its own mean but is
            // exactly the week the ratio must flag. RMS reads trend and chop alike; for the
            // near-zero-mean prior window RMS ≈ σ, so the baseline is unchanged.
            double? volRatio = null;
            var lastWk = diffs.TakeLast(5).ToList();
            var prior = diffs.SkipLast(5).TakeLast(60).ToList();
            if (lastWk.Count == 5 && prior.Count >= 15 && Rms(prior) > 1e-9)
                volRatio = Rms(lastWk) / Rms(prior);

            // strict weekly σ — non-overlapping 7-calendar-day changes, gated on depth
            double? sigmaWeekly = null;
            var marks = new List<double>();
            for (int k = 0; k <= 54; k++)
            {
                if (At(asOf.AddDays(-7 * k)) is not { } v) break;
                marks.Add(v);
            }
            if (marks.Count >= MinWeeklyObs + 1)
            {
                var wd = new List<double>(marks.Count - 1);
                for (int i = 1; i < marks.Count; i++) wd.Add((marks[i - 1] - marks[i]) * scaleToBp);
                double s = Std(wd);
                if (s >= SigmaFloorBp) sigmaWeekly = s;
            }

            double sigma = sigmaWeekly ?? sigmaEst;
            var spark = h.Where(p => p.Date > asOf.AddDays(-SparkDays) && p.Date <= asOf).ToList();
            return new ScanStats(now, w1, m1, sigmaWeekly, sigmaEst, w1 / sigma,
                sigmaWeekly is null, volRatio, spark);
        }

        private static double Std(IReadOnlyList<double> xs)
        {
            if (xs.Count < 2) return 0;
            double m = xs.Average(), ss = 0;
            foreach (var x in xs) ss += (x - m) * (x - m);
            return Math.Sqrt(ss / (xs.Count - 1));
        }

        private static double Rms(IReadOnlyList<double> xs)
        {
            if (xs.Count == 0) return 0;
            double ss = 0;
            foreach (var x in xs) ss += x * x;
            return Math.Sqrt(ss / xs.Count);
        }

        /// <summary>Roll-corrected daily series for one meeting contract: for each stretch
        /// between decision boundaries the contract lives under a different ticker index, and the
        /// series is read from the ticker that pointed at THIS contract during that stretch —
        /// RollingStrip's shift, applied across a whole window instead of two lookback points.
        /// Points ON a boundary date are excluded: the numbered families re-point NON-uniformly
        /// during the decision day (the dodgeball 16:30-snap finding), so a boundary-day close is
        /// unattributable to either contract.</summary>
        public static List<HistPoint> MeetingSeries(
            HistoryStore store, IEnumerable<DateTime> boundaries, Func<int, string> ticker,
            DateTime contract, DateTime asOf, int windowDays, int maxIndexProbe = 13,
            MeetingRungMap? map = null)
        {
            // 14-day cluster, matching RollingStrip — the dodgeball stitcher's hardened width
            var cl = new List<DateTime>();
            foreach (var b in boundaries.Select(b => b.Date).OrderBy(b => b))
                if (cl.Count == 0 || (b - cl[^1]).TotalDays > 14) cl.Add(b);

            var from = asOf.AddDays(-windowDays).Date;
            var cuts = new List<DateTime> { from };
            cuts.AddRange(cl.Where(b => b > from && b <= asOf.Date));
            cuts.Add(asOf.Date.AddDays(1));

            var res = new List<HistPoint>();
            var boundarySet = new HashSet<DateTime>(cl);
            for (int s = 0; s + 1 < cuts.Count; s++)
            {
                // index constant inside a stretch: boundaries strictly after its start, up to the
                // contract, are exactly the rolls between then and now (RollingStrip.RolledValue).
                // Zero = the contract's period had already started: no rung, skip (never rung 1)
                int idx = cl.Count(b => b > cuts[s] && b <= contract.Date);
                if (idx < 1 || idx > maxIndexProbe) continue;
                foreach (var p in store.GetDaily(ticker(idx), windowDays + 10))
                {
                    // mixed-state days (announcement→start renumber window) never enter a
                    // roll-corrected series (desk 2026-08-26)
                    if (map?.IsMixedState(p.Date) ?? false) continue;
                    if (p.Date.Date < cuts[s] || p.Date.Date >= cuts[s + 1]) continue;
                    if (boundarySet.Contains(p.Date.Date)) continue;
                    res.Add(p);
                }
            }
            return res.OrderBy(p => p.Date).ToList();
        }

        // ---------------- the scan ----------------

        private static readonly int[] OutrightTenors = { 2, 5, 10, 30 };
        private static readonly (string Name, int A, int B)[] Slopes = { ("2s10s", 2, 10), ("5s30s", 5, 30) };
        private static readonly (int S, int T)[] InflationFwds = { (1, 1), (2, 2), (5, 5), (10, 10) };
        private static readonly string[] InflationTenors = { "1Y", "2Y", "5Y", "10Y", "30Y" };

        private static string? GroupOf(string ccy)
        {
            foreach (var g in Render.Page.Groups)
                for (int i = 1; i < g.Length; i++)
                    if (g[i].Equals(ccy, StringComparison.OrdinalIgnoreCase)) return g[0];
            return null;
        }

        public static MoversResult Scan(
            ConfigStore configs, Func<string, string> srcFor, HistoryStore store, DateTime asOf,
            Func<MeetingScheduleDef, string>? meetingSource = null)
        {
            var cands = new List<Mover>();
            int excluded = 0;

            string RateText(double v) => v.ToString("F3", CultureInfo.InvariantCulture) + "%";
            string BpText(double v) => v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "bp";

            void Add(IReadOnlyList<HistPoint> series, double scale, string ccy, string group,
                     string kind, string label, Func<double, string> fmt)
            {
                if (Stats(series, scale, asOf) is not { } st) { excluded++; return; }
                string range = st.Spark.Count > 0
                    ? $"{fmt(st.Spark.Min(p => p.Value))} … {fmt(st.Spark.Max(p => p.Value))}"
                    : "—";
                cands.Add(new Mover(ccy, group, kind, label, ccy.ToLowerInvariant() + ".html",
                    st.LevelNow, fmt(st.LevelNow), range, st.W1Bp, st.M1Bp,
                    st.Z, st.ZIsEst, st.SigmaWeeklyBp ?? st.SigmaEstBp, st.SigmaWeeklyBp,
                    st.VolRatio, st.Spark));
            }

            foreach (var cfg in configs.Enabled)
            {
                string ccy = cfg.Ccy.ToUpperInvariant();
                if (GroupOf(ccy) is not { } group) continue;
                if (cfg.Ois == null && cfg.Irs == null && cfg.Ladders.Count == 0) continue;
                string src = srcFor(ccy);

                var cache = new Dictionary<string, IReadOnlyList<HistPoint>>(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<HistPoint> Ser(string tk) =>
                    cache.TryGetValue(tk, out var s) ? s : cache[tk] = store.GetDaily(tk, WindowDays);

                if (cfg.Ois != null || cfg.Irs != null)
                {
                    var ladder = WeeklyCurves.NaturalPillarLadder(cfg, src);
                    var byTenor = new Dictionary<int, string>();
                    foreach (var t in OutrightTenors)
                    {
                        var hits = ladder.Where(p => Math.Abs(p.Years - t) < 0.12)
                                         .OrderBy(p => Math.Abs(p.Years - t)).ToList();
                        if (hits.Count > 0) byTenor[t] = hits[0].Ticker;
                    }

                    foreach (var (t, tk) in byTenor)
                        Add(Ser(tk), 100.0, ccy, group, "outright", $"{ccy} {t}Y", RateText);

                    foreach (var (name, a, b) in Slopes)
                    {
                        if (!byTenor.TryGetValue(a, out var ta) || !byTenor.TryGetValue(b, out var tb)) continue;
                        var mapA = Ser(ta).ToDictionary(p => p.Date.Date, p => p.Value);
                        var slope = new List<HistPoint>();
                        foreach (var p in Ser(tb))
                            if (mapA.TryGetValue(p.Date.Date, out var va))
                                slope.Add(new HistPoint(p.Date, (p.Value - va) * 100.0));
                        Add(slope, 1.0, ccy, group, "slope", $"{ccy} {name}", BpText);
                    }

                    // quoted forward families only: one instrument, exact close-to-close changes.
                    // Par-approx daily rebuilds are deliberately NOT ranked (their bias is not a move).
                    foreach (var (sy, ty, label) in ForwardLadder.Grid)
                    {
                        if (sy == 0) continue;
                        if (ForwardLadder.TickerFor(cfg, sy, ty) is not { } tk) continue;
                        Add(Ser(tk), 100.0, ccy, group, "forward", $"{ccy} {label}", RateText);
                    }
                }

                foreach (var lad in cfg.Ladders.Where(l =>
                             l.Kind.Equals("INFLATION", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var p in lad.Pillars.Where(p => p.Enabled &&
                                 InflationTenors.Contains(p.Tenor, StringComparer.OrdinalIgnoreCase)))
                        Add(Ser(ConfigStore.ResolveTicker(p.Ticker, "")), 100.0, ccy, group,
                            "inflation", $"{ccy} {lad.Name} {p.Tenor.ToUpperInvariant()}", RateText);

                    if (Inflation.FwdCode(ccy) is { } code)
                        foreach (var (s, t) in InflationFwds)
                            if (Inflation.FwdGrid.Contains((s, t)))
                                Add(Ser(Inflation.Ticker(code, s, t)), 100.0, ccy, group,
                                    "inflation", $"{ccy} {lad.Name} {s}y{t}y", RateText);
                }
            }

            // meeting-dated OIS: levels/changes are only meaningful roll-corrected, so the series
            // comes from the boundary-shifted read, and rows the neighbour guard rewrote are out
            foreach (var sched in MeetingsStore.Schedules)
            {
                if (sched.Kind.Equals("fra", StringComparison.OrdinalIgnoreCase)) continue;
                string ccy = sched.Ccy.ToUpperInvariant();
                if (GroupOf(ccy) is not { } group) continue;
                var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (pat == null) continue;

                // the run's ACTIVE source — the same feed as the email/boards (desk 2026-08-26)
                var activeSrc = meetingSource?.Invoke(sched) ?? sched.Source ?? "";
                var strip = RollingStrip.ForMeetings(sched, store, asOf, source: activeSrc);
                // ONE boundary derivation for every consumer — MeetingRungMap (this scan
                // previously ignored the SKSF start rule and settled announcements)
                var rmap = new MeetingRungMap(sched);
                var bounds = rmap.Boundaries.ToList();
                var tick = RollingStrip.SourceAwareTicker(store, pat, activeSrc);
                foreach (var row in strip.Rows)
                {
                    if (row.Label.EndsWith("*", StringComparison.Ordinal)) { excluded++; continue; }
                    // a year-end-turn period's "move" is the turn breathing, not policy repricing
                    if (row.Turn) { excluded++; continue; }
                    var series = MeetingSeries(store, bounds, tick,
                        row.Contract, asOf, SparkDays + 40, map: rmap);
                    Add(series, 100.0, ccy, group, "meeting",
                        $"{sched.Name} {row.Contract:dd-MMM-yy}", RateText);
                }
            }

            // ranking: |z| first, raw bp as the tie-break — z is the point of the page
            static List<Mover> Rank(IEnumerable<Mover> xs) =>
                xs.OrderByDescending(m => Math.Abs(m.Z)).ThenByDescending(m => Math.Abs(m.W1Bp)).ToList();

            var dm = Rank(cands.Where(c => c.Group == "DM"));
            var em = Rank(cands.Where(c => c.Group != "DM"));

            // hero diversity: a 4σ parallel selloff should not spend all three cards on one
            // currency's 2Y/5Y/10Y — cap one per (ccy, kind) and two per ccy in the CARDS only;
            // the table below stays pure ranking
            static List<Mover> Heroes(List<Mover> ranked)
            {
                var heroes = new List<Mover>();
                foreach (var m in ranked)
                {
                    if (heroes.Count == 3) break;
                    if (heroes.Any(h => h.Ccy == m.Ccy && h.Kind == m.Kind)) continue;
                    if (heroes.Count(h => h.Ccy == m.Ccy) >= 2) continue;
                    heroes.Add(m);
                }
                return heroes;
            }

            // cross-sectional context (the rule-2 seed from dodgeball's flags): the G3 average
            // weekly z at 10y — what "the market" did, for the relative read
            string? g3Line = null;
            var g3 = new[] { "USD", "EUR", "JPY" }
                .Select(c => cands.FirstOrDefault(m => m.Ccy == c && m.Kind == "outright" && m.Label.EndsWith("10Y")))
                .Where(m => m != null).Select(m => m!.Z).ToList();
            if (g3.Count >= 2)
            {
                double avg = g3.Average();
                g3Line = Math.Abs(avg) >= 1.0
                    ? $"Common move: G3 10y averaged {avg:+0.0;-0.0}σ on the week ({(avg > 0 ? "selloff" : "rally")}) — read single-name z's against it."
                    : $"No common G3 move this week (10y z avg {avg:+0.0;-0.0}σ) — what follows is idiosyncratic.";
            }

            bool anyStrict = cands.Any(c => !c.ZIsEst);
            string method =
                "Ranked by |z| of the 1-week change — the move divided by what a normal week does. " +
                (anyStrict
                    ? "σ = std of weekly changes (1y window) where the store is deep enough, marked 'est' elsewhere ("
                    : "Until the store is deepened, σ is ESTIMATED (") +
                "√5 × daily σ over the stored window, ≥20 obs); 'wk vol' compares last week's daily vol to the prior norm. " +
                "Despiked (Hampel 5/6); closes older than 4 days, σ under 0.3bp/day, and guard-rewritten meeting prints are excluded.";

            var notes = new List<string>();
            if (excluded > 0) notes.Add($"{excluded} instrument(s) excluded by the data gates");

            string Head(List<Mover> h) => h.Count == 0 ? "no qualifying movers"
                : $"{h[0].Label} {h[0].W1Bp:+0.0;-0.0}bp ({Math.Abs(h[0].Z):0.0}σ{(h[0].ZIsEst ? " est" : "")})";
            var dmH = Heroes(dm);
            var emH = Heroes(em);
            string headline = $"DM: {Head(dmH)} · EM: {Head(emH)}";

            return new MoversResult
            {
                AsOf = asOf,
                DmRanked = dm, EmRanked = em,
                DmHeroes = dmH, EmHeroes = emH,
                G3Line = g3Line, MethodNote = method, Notes = notes,
                Headline = headline,
            };
        }

        /// <summary>movers.json — the email builder's teaser source, deliberately tiny.</summary>
        public static string ToJson(MoversResult mv)
        {
            static object Brief(Mover m) => new
            {
                label = m.Label,
                w1bp = Math.Round(m.W1Bp, 1),
                z = Math.Round(m.Z, 2),
                est = m.ZIsEst,
                page = m.PageFile,
            };
            return JsonSerializer.Serialize(new
            {
                asOf = mv.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                headline = mv.Headline,
                dm = mv.DmHeroes.Select(Brief),
                em = mv.EmHeroes.Select(Brief),
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
