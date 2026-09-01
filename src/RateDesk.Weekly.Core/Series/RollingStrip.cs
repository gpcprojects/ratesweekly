using RateDesk.Core;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>Levels, not changes: WeekLevel/MonthLevel are what THIS contract was quoted at
    /// then, already roll-corrected, so the renderer differences them like any other ladder.
    /// Turn = the period spans a year-end on a marked run (SEK/SWESTR): renderers print
    /// "Y/E Turn" instead of the numbers and movers/charts skip the row.</summary>
    public sealed record StripRow(
        string Label, DateTime Contract, double? Mid, double? WeekLevel, double? MonthLevel,
        string Ticker, bool Turn = false);

    public sealed class StripTable
    {
        public required string Title { get; init; }
        public required DateTime AsOf { get; init; }
        public List<StripRow> Rows { get; init; } = new();
        public List<string> Notes { get; init; } = new();
        public bool HasData => Rows.Any(r => r.Mid.HasValue);
    }

    /// <summary>Levels and roll-corrected 1w/1m changes for a POSITIONAL strip — central-bank
    /// meeting-dated OIS and quarterly IMM FRA runs alike.
    ///
    /// The trap these families set: the numbered tickers are ROLLING GENERICS. On the day after a
    /// boundary passes, ticker N re-points at the next contract, so N's own close from before the
    /// roll belongs to a DIFFERENT contract. Differencing them naively books the whole inter-contract
    /// step as a market move — in Dodgeball this printed +12bp of phantom change on every BOJ row the
    /// morning after a decision. Over a 1-month lookback a quarterly IMM strip rolls about a third of
    /// the time, so this is the common case, not an edge case.
    ///
    /// The fix, lifted from Dodgeball's MeetingSeriesBuilder: walk the KNOWN boundary dates
    /// (decision dates from config, IMM dates for FRA strips) and, for any past date, read the
    /// ticker index that pointed at THIS contract back then — index + (boundaries crossed since).
    /// Config's settled decision dates are what make this possible, which is why the meetings loader
    /// migrates them into pastDates automatically.</summary>
    public static class RollingStrip
    {
        /// <summary>Cluster width for treating two configured dates as the same decision.
        /// 14 days, not 6 — dodgeball's stitcher learned this live (2026-08-06): a config date
        /// drifted more than the cluster width from ticker truth counts as a SECOND boundary and
        /// shifts every row's index by one. No two real meetings of one bank sit within 14 days
        /// (a tested config invariant), so the wide cluster is safe by construction.</summary>
        private const int ClusterDays = 14;

        /// <summary>Rows for a numbered family.
        /// <paramref name="ticker"/> maps a 1-based index to a security;
        /// <paramref name="boundaries"/> is every roll date, past and future, in any order.</summary>
        public static StripTable Build(
            string title, HistoryStore store, DateTime asOf,
            IReadOnlyList<(string Label, DateTime Contract)> contracts,
            IEnumerable<DateTime> boundaries,
            Func<int, string> ticker,
            int maxIndexProbe = 13,
            MeetingRungMap? map = null)
        {
            var res = new StripTable { Title = title, AsOf = asOf };

            // de-duplicated, ascending roll boundaries
            var bounds = new List<DateTime>();
            foreach (var d in boundaries.Select(b => b.Date).OrderBy(b => b))
                if (bounds.Count == 0 || (d - bounds[^1]).TotalDays > ClusterDays) bounds.Add(d);

            // The rung a contract lives under AT asOf is the number of boundaries between asOf and
            // the contract — the same rule RolledValue applies to lookbacks. Positionally (i + 1)
            // is only equivalent while the list starts at the very next contract; once an announced
            // decision drops the front (ForMeetings), position and rung diverge by one.
            // ZERO boundaries between asOf and the contract = the contract's own period already
            // started: there is no rung, and the row publishes nothing (the old Max(1,…) coercion
            // published the FRONT rung's rate there — fresh-eyes review 2026-08-26).
            int? RungAt(DateTime contract)
            {
                int idx = bounds.Count(b => b > asOf.Date && b <= contract.Date);
                return idx < 1 ? null : idx;
            }
            // THE LEVEL GOES THROUGH RolledValue (fix 2026-08-27, scenarios 53/59). RungAt
            // counts boundaries STRICTLY AFTER asOf, so a boundary falling ON asOf is not
            // counted — while the close being read comes from asOf or earlier, i.e. from the
            // numbering in force BEFORE that boundary. The two disagreed by exactly one rung
            // whenever the render's as-of was a renumber day, which is every weekly run made the
            // day after a bank announced: every published level was the neighbouring contract's,
            // and the 1w/1m levels beside it (which DO step back) were right, so the rendered
            // change was a phantom of one full inter-meeting step, wrong-signed.
            var mids = new double?[contracts.Count];
            for (int i = 0; i < contracts.Count; i++)
                mids[i] = RolledValue(store, ticker, bounds, contracts[i].Contract, asOf,
                                      maxIndexProbe, map);

            int guarded = 0;
            for (int i = 0; i < contracts.Count; i++)
            {
                var (label, contract) = contracts[i];
                if (RungAt(contract) is not { } rNow) continue;
                string tkNow = ticker(rNow);
                if (Guard(mids, i) is not { } mid) continue;
                if (mids[i] is { } raw && Math.Abs(raw - mid) > 1e-9) { guarded++; label += "*"; }

                double? w = RolledValue(store, ticker, bounds, contract, asOf.AddDays(-WeeklyCurves.WeekDays), maxIndexProbe, map);
                double? m = RolledValue(store, ticker, bounds, contract, WeeklyCurves.MonthAgo(asOf), maxIndexProbe, map);

                res.Rows.Add(new StripRow(label, contract, mid, w, m, tkNow));
            }

            // Trailing rows with no quote are the family running out, not a market: drop them
            // rather than publishing empty lines.
            while (res.Rows.Count > 0 && res.Rows[^1].Mid is null) res.Rows.RemoveAt(res.Rows.Count - 1);
            if (guarded > 0)
                res.Notes.Add($"{guarded} interior row(s) replaced by the neighbour midpoint — the quoted " +
                              "print was implausible against both neighbours");

            int rolls1m = bounds.Count(b => b > WeeklyCurves.MonthAgo(asOf) && b <= asOf);
            if (rolls1m > 0)
                res.Notes.Add($"{rolls1m} roll(s) inside the 1m window — changes are measured against the " +
                              "ticker that pointed at the same contract then, not against this ticker's own close");
            return res;
        }

        /// <summary>Thin strips misprint with a straight face: a Riksbank meeting OIS published a
        /// live TWO-SIDED 1.387 between 1.848 and 2.086 neighbours — an impossible inter-meeting
        /// rate, and one this app would otherwise publish to the desk and to external readers.
        ///
        /// An INTERIOR row is judged against its QUOTED NEIGHBOURS, never against a curve (a
        /// year-turn pillar legitimately drags curve-implied rates near December, which false-flags
        /// good prints). Rejected when it sits &gt;25bp from the neighbour midpoint while those
        /// neighbours agree with each other within 25bp; the midpoint replaces it and the row is
        /// labelled. Edge rows are never judged — the front contract is the one that gaps for real.</summary>
        private static double? Guard(double?[] mids, int i)
        {
            if (mids[i] is not { } m0) return null;
            if (i - 1 >= 0 && i + 1 < mids.Length && mids[i - 1] is { } a && mids[i + 1] is { } b
                && Math.Abs(a - b) * 100.0 < 25.0)
            {
                double expected = (a + b) / 2.0;
                if (Math.Abs(m0 - expected) * 100.0 > 25.0) return expected;
            }
            return m0;
        }

        /// <summary>The value THIS contract had on <paramref name="then"/>, found by shifting the
        /// index forward by the number of boundaries that have passed since. Null when the store
        /// has nothing for the resolved ticker.
        ///
        /// A lookback landing ON a decision day steps back to the day before: the numbered
        /// families re-point NON-uniformly during the decision day (probed in dodgeball at 16:30
        /// London: #1 had rolled, #2-#4 had not), so that day's close is unattributable to either
        /// contract and must never source a change.</summary>
        public static double? RolledValue(
            HistoryStore store, Func<int, string> ticker, List<DateTime> bounds,
            DateTime contract, DateTime then, int maxIndex, MeetingRungMap? map = null)
        {
            // boundary days and mixed-state days (announcement→start) never source a value
            then = then.Date;
            while (bounds.Any(b => b == then) || (map?.IsMixedState(then) ?? false))
                then = then.AddDays(-1);

            // Boundaries strictly after `then` and at or before the contract date are exactly the
            // rolls that have happened between then and now for this contract. Zero = the
            // contract's period had already started then: no rung, no value (never rung 1).
            int idxThen = bounds.Count(b => b > then.Date && b <= contract.Date);
            if (idxThen < 1 || idxThen > maxIndex) return null;

            // ...and if the walk-back itself resolves to a decision-day or mixed-state close (a
            // weekend lookback over a Friday decision, say), recompute from the day before —
            // the index shifts too: before the roll, this contract lived under the NEXT number up.
            var read = store.ValueAsOf(ticker(idxThen), then);
            if (read is null) return null;
            if (LastCloseDate(store, ticker(idxThen), then) is { } d
                && (bounds.Contains(d.Date) || (map?.IsMixedState(d.Date) ?? false)))
                return RolledValue(store, ticker, bounds, contract, d.Date.AddDays(-1), maxIndex, map);
            return read;
        }

        /// <summary>The DATE of the close ValueAsOf would read at or before <paramref name="then"/>.</summary>
        private static DateTime? LastCloseDate(HistoryStore store, string ticker, DateTime then)
        {
            foreach (var p in store.GetDaily(ticker, 400).Reverse())
                if (p.Date.Date <= then.Date) return p.Date.Date;
            return null;
        }

        /// <summary>The ACTIVE-source-aware rung ticker: the contributor spelling when the store
        /// holds data for that rung, the composite otherwise — the same per-rung fallback the
        /// boards and history books use, so every surface reads the SAME series (desk 2026-08-26:
        /// EVERYTHING follows the SOURCES selection).</summary>
        public static Func<int, string> SourceAwareTicker(HistoryStore store, string pat, string src)
        {
            string Plain(int n) => pat.Replace("{N}", n.ToString()) + " Curncy";
            if (string.IsNullOrEmpty(src)) return Plain;
            var hasSrc = new Dictionary<int, bool>();
            return n =>
            {
                var q = pat.Replace("{N}", n.ToString()) + " " + src + " Curncy";
                if (!hasSrc.TryGetValue(n, out var h))
                    hasSrc[n] = h = store.GetDaily(q, 40).Count > 0;
                return h ? q : Plain(n);
            };
        }

        /// <summary>Central-bank runs from config: contracts are the future decision dates, roll
        /// boundaries come from the shared MeetingRungMap (fresh-eyes review 2026-08-26 — this
        /// surface previously built its own list without the SKSF start rule or the settled-
        /// announcement snap, so the SEK strip carried the bug the boards had already fixed).
        /// <paramref name="source"/>: the run's ACTIVE contributor ("" = composite; null =
        /// config default) — dashboards must price off the same feed as the email.</summary>
        public static StripTable ForMeetings(
            MeetingScheduleDef sched, HistoryStore store, DateTime asOf, int maxRows = 8,
            DateTime? nowLondon = null, string? source = null)
        {
            // TIME-GATED FRONT ROLL (desk 2026-08-20), the boards' own rule: once a period's
            // decision is ANNOUNCED (decision date + decisionTimeLondon on the London clock) the
            // period is decided, not priced, and leaves the strip — even before it starts, and
            // even when the generics haven't re-pointed yet. Periods without a decision on the
            // calendar keep the old behaviour (they leave when they start).
            var nowLdn = nowLondon ?? RateDesk.Core.Dates.DecisionClock.LondonNow();
            var future = sched.Dates.Where(d => d.Date > asOf.Date)
                .Where(d => !(RateDesk.Core.Dates.DecisionClock.DecisionFor(sched.DecisionDates, d) is { } fd
                              && RateDesk.Core.Dates.DecisionClock.Announced(fd, sched.DecisionTimeLondon, nowLdn)))
                .OrderBy(d => d).Take(maxRows).ToList();
            var contracts = future.Select(d => (d.ToString("dd-MMM-yy"), d)).ToList();
            // ONE boundary derivation for every consumer — MeetingRungMap: announcements
            // (recorded + stable-lag-derived) for decision-renumbering families, starts for
            // SKSF, 14-day cluster keeping the earliest. ARMED with the store's own maturity
            // records (audit 2026-08-31, finding 2): the boards' SKSF fixes — evidence beats
            // the boundary count — never reached this public surface, so the dashboards could
            // publish a different rung than the email on the very days the records settle.
            // Records key the COMPOSITE spelling (the recording side is source-agnostic).
            var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
            var map = new MeetingRungMap(sched, null, pat == null ? null
                : (n, d) => store.EffectiveOn(pat.Replace("{N}", n.ToString()) + " Curncy", d));

            if (pat == null || contracts.Count == 0)
                return new StripTable { Title = $"{sched.Name} · {sched.Ccy}", AsOf = asOf };

            var tick = SourceAwareTicker(store, pat, source ?? sched.Source ?? "");

            // HARD-DATA RULE (desk 2026-08-20): a strip row is published only while the rung that
            // carries it has a Bloomberg-DOCUMENTED contract (a recorded MATURITY in the store).
            // Config dates still order the roll boundaries internally, but they never label a
            // published row past where Bloomberg's own fields stop. Maturity records key the
            // COMPOSITE spelling (the recording side is source-agnostic).
            {
                string Rung(int n) => pat.Replace("{N}", n.ToString()) + " Curncy";
                // the family's current recording day, from the front rung; a family with NO
                // maturity records at all (fixtures, families the engine has never snapped)
                // cannot be discriminated and keeps the legacy behaviour
                if (store.MaturityRecordDay(Rung(1)) is { } famDay)
                {
                    int keep = 0;
                    foreach (var (_, c) in contracts)
                    {
                        if (map.RungFor(c, asOf) is not { } rung) break;
                        if (store.MaturityRecordDay(Rung(rung)) != famDay) break;
                        keep++;
                    }
                    if (keep < contracts.Count) contracts = contracts.Take(keep).ToList();
                }
            }

            var t = RollingStrip.Build($"{sched.Name} · {sched.Ccy}", store, asOf, contracts,
                map.Boundaries, tick, map: map);
            // Y/E TURN (desk 2026-08-20): on marked runs, a period spanning a year-end renders
            // as a label — its average carries the turn dislocation (SWESTR's is extreme)
            if (sched.MarkTurnPeriods)
            {
                var all = sched.Dates.Select(d => d.Date).OrderBy(d => d).ToList();
                for (int i = 0; i < t.Rows.Count; i++)
                {
                    var end = all.FirstOrDefault(d => d > t.Rows[i].Contract);
                    // unresolved end: only a DECEMBER start provably spans the year-end — the
                    // same rule as the boards (the old 42-day guess could disagree with the
                    // email on which row is a turn; fresh-eyes review 2026-08-26)
                    bool turn = end == default
                        ? t.Rows[i].Contract.Month == 12
                        : t.Rows[i].Contract.Year != end.Year;
                    if (turn) t.Rows[i] = t.Rows[i] with { Turn = true };
                }
            }
            return t;
        }
    }
}
