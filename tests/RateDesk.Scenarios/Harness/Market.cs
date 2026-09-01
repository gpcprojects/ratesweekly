using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;

namespace RateDesk.Scenarios.Harness;

/// <summary>The synthetic market. Two things stand in for Bloomberg and nothing else does:
///   1. a <see cref="RatesSnapshot"/> filled with the scenario's live quotes, and
///   2. a real on-disk <see cref="HistoryStore"/> (SQLite, temp file) filled with the
///      scenario's closes - the SAME store class the app maintains, so the workbook history
///      walk and the dashboards read production code all the way down.
/// London snaps are served by <see cref="ScenarioHistory"/>, because in production they come
/// from Bloomberg intraday bars and the store has no equivalent.</summary>
public static class Market
{
    public static string TickerFor(PricingService svc, MeetingScheduleDef sched, int n, Spell sp)
    {
        var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"))
                  ?? throw new InvalidOperationException($"{sched.Name}: no {{N}} ticker pattern");
        return sp == Spell.Composite
            ? pat.Replace("{N}", n.ToString()) + " Curncy"
            : svc.MeetingTick(sched, pat, n);
    }

    private static IEnumerable<string> Spellings(PricingService svc, MeetingScheduleDef sched, int n, Spell sp)
    {
        var active = TickerFor(svc, sched, n, Spell.Active);
        var composite = TickerFor(svc, sched, n, Spell.Composite);
        return sp switch
        {
            Spell.Active => new[] { active },
            Spell.Composite => new[] { composite },
            _ => active.Equals(composite, StringComparison.OrdinalIgnoreCase)
                ? new[] { active } : new[] { active, composite },
        };
    }

    public static string FixingTicker(MeetingScheduleDef sched, ConfigStore configs) =>
        sched.RefTicker
        ?? (configs.TryGet(sched.Ccy, out var cfg) ? cfg.Ois?.OnFixingTicker : null)
        ?? "";

    /// <summary>Fill the snapshot, the store and the snap tables from the scenario.</summary>
    public static ScenarioHistory Seed(ScenarioSpec spec, PricingService svc, RatesSnapshot snap,
        HistoryStore store, ConfigStore configs, List<string> setupProblems, string artifactDir = "")
    {
        var snaps = new Dictionary<string, SortedDictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);

        void AddSnap(string ticker, DateTime d, double v)
        {
            if (!snaps.TryGetValue(ticker, out var s)) snaps[ticker] = s = new SortedDictionary<DateTime, double>();
            s[d.Date] = v;
        }

        foreach (var bank in spec.Banks)
        {
            var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                            s.Name.Equals(bank.Bank, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            $"scenario {spec.Id}: '{bank.Bank}' is not in the written config");

            // ---- live quotes ----
            foreach (var q in bank.Rungs)
                foreach (var tk in Spellings(svc, sched, q.N, q.Spelling))
                {
                    if (q.Mid is { } m) snap.Update(tk, m, m, m);
                    if (q.PrevClose is { } pc) snap.SetPrevClose(tk, pc);
                    if (q.Mat is { } mat)
                    {
                        snap.SetMaturity(tk, mat);
                        store.SetMaturity(tk, DateTime.Today, mat, q.Eff);
                    }
                    if (q.Eff is { } eff) snap.SetEffective(tk, eff);
                    var age = q.AgeMinutes ?? spec.DefaultAgeMinutes;
                    if (age is { } a) snap.SetAgeMinutes(tk, a);
                }

            // ---- stored closes ----
            foreach (var g in bank.Closes.GroupBy(c => (c.N, c.Spelling)))
                foreach (var tk in Spellings(svc, sched, g.Key.N, g.Key.Spelling))
                    store.UpsertDaily(tk, g.Select(p => new HistPoint(p.Date, p.Value)).ToList());

            // ---- Bloomberg's per-day record of what each rung pointed at ----
            foreach (var r in bank.RecordMaturities ? bank.Records : Enumerable.Empty<(int N, DateTime Day, DateTime Eff, DateTime Mat)>())
                foreach (var tk in Spellings(svc, sched, r.N, Spell.Both))
                    store.SetMaturity(tk, r.Day, r.Mat, r.Eff);

            // ---- London snaps (Bloomberg intraday bars in production) ----
            foreach (var p in bank.Snaps)
                foreach (var tk in Spellings(svc, sched, p.N, p.Spelling))
                    AddSnap(tk, p.Date, p.Value);

            // ---- the o/n fixing ----
            var fixTk = FixingTicker(sched, configs);
            if (fixTk.Length == 0)
                setupProblems.Add($"SETUP: {bank.Bank} has no fixing ticker (schedule refTicker and " +
                                  $"{sched.Ccy} onFixingTicker are both empty)");
            else
            {
                if (bank.Fixing is { } f)
                {
                    snap.Update(fixTk, f, f, f);
                    if (spec.DefaultAgeMinutes is { } fa) snap.SetAgeMinutes(fixTk, fa);
                }
                if (bank.FixingPrevClose is { } fp) snap.SetPrevClose(fixTk, fp);
                if (bank.FixingHistory.Count > 0)
                    store.UpsertDaily(fixTk, bank.FixingHistory.Select(p => new HistPoint(p.Date, p.Value)).ToList());
            }

            // ---- raw closes for explicitly-named securities (policy targets, chiefly) ----
            foreach (var g in bank.RawCloses.GroupBy(x => x.Ticker))
                store.UpsertDaily(g.Key, g.Select(p => new HistPoint(p.Date, p.Value)).ToList());

            // ---- anything else the scenario names explicitly ----
            foreach (var e in bank.Extras)
            {
                if (e.Mid is { } m) snap.Update(e.Ticker, m, m, m);
                if (e.PrevClose is { } pc) snap.SetPrevClose(e.Ticker, pc);
                if (e.Mat is { } mt) snap.SetMaturity(e.Ticker, mt);
                if (e.Eff is { } ef) snap.SetEffective(e.Ticker, ef);
                var age = e.AgeMinutes ?? spec.DefaultAgeMinutes;
                if (age is { } a) snap.SetAgeMinutes(e.Ticker, a);
            }
        }

        Hygiene.Check(spec, setupProblems);
        if (artifactDir.Length > 0) Dump(spec, svc, snap, snaps, artifactDir);
        return new ScenarioHistory(store, snaps);
    }

    /// <summary>The synthetic market, written out in full. A finding is only worth acting on if
    /// the fake world it came from can be read by a human.</summary>
    private static void Dump(ScenarioSpec spec, PricingService svc, RatesSnapshot snap,
        Dictionary<string, SortedDictionary<DateTime, double>> snaps, string dir)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.AppendLine($"scenario {spec.Id}: {spec.Name}");
            sb.AppendLine($"today = {DateTime.Today:dd-MMM-yy ddd}   London now = {Cal.LondonNow():dd-MMM-yy HH:mm}");
            foreach (var bank in spec.Banks)
            {
                var sched = MeetingsStore.Schedules.First(s =>
                    s.Name.Equals(bank.Bank, StringComparison.OrdinalIgnoreCase));
                sb.AppendLine();
                sb.AppendLine($"=== {bank.Bank} ({sched.Ccy}) source='{svc.MeetingSrc(sched)}' " +
                              $"decisionTime={sched.DecisionTimeLondon} " +
                              $"rollsAtPeriodStart={sched.RollsAtPeriodStart} turn={sched.MarkTurnPeriods}");
                sb.AppendLine("  starts    : " + string.Join(", ", sched.Dates.Select(d => d.ToString("dd-MMM-yy", inv))));
                sb.AppendLine("  past      : " + string.Join(", ", sched.PastDates.Select(d => d.ToString("dd-MMM-yy", inv))));
                sb.AppendLine("  decisions : " + string.Join(", ", sched.DecisionDates.Select(d => d.ToString("dd-MMM-yy", inv))));
                var map = new MeetingRungMap(sched);
                sb.AppendLine("  app boundaries: " + string.Join(", ", map.Boundaries.Select(d => d.ToString("dd-MMM-yy", inv))));
                sb.AppendLine($"  fixing {FixingTicker(sched, ConfigStore.LoadEmbedded())} = " +
                              (bank.Fixing?.ToString("0.000", inv) ?? "(unquoted)"));
                foreach (var q in bank.Rungs.OrderBy(q => q.N))
                    sb.AppendLine($"  quote  n={q.N,-2} spell={q.Spelling,-9} mid=" +
                                  $"{q.Mid?.ToString("0.000", inv) ?? "-",-7} prev=" +
                                  $"{q.PrevClose?.ToString("0.000", inv) ?? "-",-7} eff=" +
                                  $"{q.Eff?.ToString("dd-MMM-yy", inv) ?? "-",-10} mat=" +
                                  $"{q.Mat?.ToString("dd-MMM-yy", inv) ?? "-",-10} age={q.AgeMinutes?.ToString() ?? "-"}");
                foreach (var g in bank.Closes.GroupBy(c => c.N).OrderBy(g => g.Key))
                    sb.AppendLine($"  closes n={g.Key}: " + Compress(g.OrderBy(p => p.Date)));
                foreach (var g in bank.Snaps.GroupBy(c => c.N).OrderBy(g => g.Key))
                    sb.AppendLine($"  snaps  n={g.Key}: " + Compress(g.OrderBy(p => p.Date)));
            }
            File.WriteAllText(Path.Combine(dir, "market.txt"), sb.ToString());
        }
        catch { /* the dump is a convenience */ }
    }

    /// <summary>Runs of equal values compressed to "from..to = value".</summary>
    private static string Compress(IEnumerable<HistPointSpec> pts)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = pts.ToList();
        var parts = new List<string>();
        int i = 0;
        while (i < list.Count)
        {
            int j = i;
            while (j + 1 < list.Count && Math.Abs(list[j + 1].Value - list[i].Value) < 1e-12) j++;
            parts.Add($"{list[i].Date.ToString("dd-MMM", inv)}..{list[j].Date.ToString("dd-MMM", inv)}=" +
                      list[i].Value.ToString("0.000", inv));
            i = j + 1;
        }
        return string.Join("  ", parts);
    }
}

/// <summary>The history provider the service reads: stored closes for GetDaily (exactly what
/// StoreBackedHistory serves in production), scenario-supplied London snaps for GetLondonSnaps
/// (which in production come from Bloomberg intraday bars, so the store cannot serve them).</summary>
public sealed class ScenarioHistory : IHistoryProvider
{
    private readonly HistoryStore _store;
    private readonly Dictionary<string, SortedDictionary<DateTime, double>> _snaps;

    public ScenarioHistory(HistoryStore store,
        Dictionary<string, SortedDictionary<DateTime, double>> snaps)
    { _store = store; _snaps = snaps; }

    public int DailyCalls { get; private set; }
    public int SnapCalls { get; private set; }

    public IReadOnlyList<HistPoint> GetDaily(string ticker, int lookbackDays)
    { DailyCalls++; return _store.GetDaily(ticker, lookbackDays); }

    public void Prefetch(IEnumerable<string> tickers, int lookbackDays) { }

    /// <summary>What the store recorded this rung pointing at that day — the same delegation
    /// StoreBackedHistory does in production. Without it every consumer falls back to calendar
    /// inference and the harness silently tests a store that has never been maintained.</summary>
    public DateTime? EffectiveOn(string ticker, DateTime day) => _store.EffectiveOn(ticker, day);

    public IReadOnlyList<HistPoint> GetLondonSnaps(string ticker, int lookbackDays, TimeSpan londonTimeOfDay)
    {
        SnapCalls++;
        if (!_snaps.TryGetValue(ticker, out var s)) return Array.Empty<HistPoint>();
        var cutoff = DateTime.Today.AddDays(-Math.Max(1, lookbackDays)).Date;
        return s.Where(kv => kv.Key >= cutoff).Select(kv => new HistPoint(kv.Key, kv.Value)).ToList();
    }
}

/// <summary>Guards against scenarios whose SYNTHETIC DATA would be mangled before the code under
/// test ever sees it - the harness must not manufacture findings.
///
/// The one real hazard is the Hampel despike filter every history read passes through
/// (HistoryFilter.Despike, window 5, k 6, MAD floor 0.5bp): an ISOLATED one-day move larger than
/// about 4.4bp whose neighbours agree with each other is replaced by the local median. A
/// persistent level shift (what a rate decision actually looks like) is never touched, because
/// the local MAD grows with it. So: level shifts are fine, one-day blips are not.</summary>
public static class Hygiene
{
    public const double SpikeDangerBp = 4.4;

    public static void Check(ScenarioSpec spec, List<string> problems)
    {
        foreach (var bank in spec.Banks)
        {
            foreach (var g in bank.Closes.GroupBy(c => (c.N, c.Spelling)))
                Scan($"{bank.Bank} closes rung {g.Key.N}", g.OrderBy(p => p.Date).ToList(), problems);
            foreach (var g in bank.Snaps.GroupBy(c => (c.N, c.Spelling)))
                Scan($"{bank.Bank} snaps rung {g.Key.N}", g.OrderBy(p => p.Date).ToList(), problems);
            Collisions($"{bank.Bank} closes", bank.Closes, problems);
            Collisions($"{bank.Bank} snaps", bank.Snaps, problems);
        }
    }

    /// <summary>Two CONTRACTS landing on the same rung on the same day. Always an authoring
    /// error - usually a boundary missing from the list passed to Contract(), which collapses
    /// two contracts onto one ticker and silently overwrites one series with the other. It
    /// produces a phantom change that looks exactly like a product bug, so it is caught here.</summary>
    private static void Collisions(string what, List<HistPointSpec> pts, List<string> problems)
    {
        foreach (var g in pts.GroupBy(p => (p.N, p.Date, p.Spelling)))
        {
            var vals = g.Select(p => p.Value).Distinct().ToList();
            if (vals.Count > 1)
                problems.Add($"SETUP: {what} rung {g.Key.N} on {g.Key.Date:dd-MMM-yy} is written " +
                             $"twice with different values ({string.Join(" / ", vals.Select(v => v.ToString("0.000")))}) " +
                             "- two contracts collapsed onto one rung. A renumber boundary is " +
                             "missing from the list passed to Contract()/ContractStep().");
        }
    }

    private static void Scan(string what, List<HistPointSpec> pts, List<string> problems)
    {
        // the filter needs 2*window+3 = 13 points before it does anything at all
        if (pts.Count < 13) return;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double prev = pts[i - 1].Value, cur = pts[i].Value, next = pts[i + 1].Value;
            double bump = Math.Min(Math.Abs(cur - prev), Math.Abs(cur - next)) * 100.0;
            double neighbourGap = Math.Abs(next - prev) * 100.0;
            if (bump > SpikeDangerBp && neighbourGap < bump)
                problems.Add($"SETUP: {what} has an isolated {bump:0.0}bp one-day move on " +
                             $"{pts[i].Date:dd-MMM-yy} - the despike filter may rewrite it before the " +
                             "code under test sees it. Use a persistent level step instead.");
        }
    }
}
