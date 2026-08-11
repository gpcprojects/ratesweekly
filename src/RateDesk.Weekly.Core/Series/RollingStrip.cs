using RateDesk.Core;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>Levels, not changes: WeekLevel/MonthLevel are what THIS contract was quoted at
    /// then, already roll-corrected, so the renderer differences them like any other ladder.</summary>
    public sealed record StripRow(
        string Label, DateTime Contract, double? Mid, double? WeekLevel, double? MonthLevel, string Ticker);

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
            int maxIndexProbe = 13)
        {
            var res = new StripTable { Title = title, AsOf = asOf };

            // de-duplicated, ascending roll boundaries
            var bounds = new List<DateTime>();
            foreach (var d in boundaries.Select(b => b.Date).OrderBy(b => b))
                if (bounds.Count == 0 || (d - bounds[^1]).TotalDays > ClusterDays) bounds.Add(d);

            var mids = new double?[contracts.Count];
            for (int i = 0; i < contracts.Count; i++) mids[i] = store.ValueAsOf(ticker(i + 1), asOf);

            int guarded = 0;
            for (int i = 0; i < contracts.Count; i++)
            {
                var (label, contract) = contracts[i];
                string tkNow = ticker(i + 1);
                if (Guard(mids, i) is not { } mid) continue;
                if (mids[i] is { } raw && Math.Abs(raw - mid) > 1e-9) { guarded++; label += "*"; }

                double? w = RolledValue(store, ticker, bounds, contract, asOf.AddDays(-WeeklyCurves.WeekDays), maxIndexProbe);
                double? m = RolledValue(store, ticker, bounds, contract, asOf.AddDays(-WeeklyCurves.MonthDays), maxIndexProbe);

                res.Rows.Add(new StripRow(label, contract, mid, w, m, tkNow));
            }

            // Trailing rows with no quote are the family running out, not a market: drop them
            // rather than publishing empty lines.
            while (res.Rows.Count > 0 && res.Rows[^1].Mid is null) res.Rows.RemoveAt(res.Rows.Count - 1);
            if (guarded > 0)
                res.Notes.Add($"{guarded} interior row(s) replaced by the neighbour midpoint — the quoted " +
                              "print was implausible against both neighbours");

            int rolls1m = bounds.Count(b => b > asOf.AddDays(-WeeklyCurves.MonthDays) && b <= asOf);
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
        private static double? RolledValue(
            HistoryStore store, Func<int, string> ticker, List<DateTime> bounds,
            DateTime contract, DateTime then, int maxIndex)
        {
            if (bounds.Any(b => b == then.Date)) then = then.Date.AddDays(-1);

            // Boundaries strictly after `then` and at or before the contract date are exactly the
            // rolls that have happened between then and now for this contract.
            int crossed = bounds.Count(b => b > then.Date && b <= contract.Date);
            int idxThen = crossed;                       // 1-based: the contract was `crossed`-th next
            if (idxThen < 1) idxThen = 1;
            if (idxThen > maxIndex) return null;

            // ...and if the walk-back itself resolves to a decision-day close (a weekend lookback
            // over a Friday decision, say), recompute from the day before that boundary — the
            // index shifts too: before the roll, this contract lived under the NEXT number up.
            var read = store.ValueAsOf(ticker(idxThen), then);
            if (read is null) return null;
            if (LastCloseDate(store, ticker(idxThen), then) is { } d && bounds.Contains(d.Date))
                return RolledValue(store, ticker, bounds, contract, d.Date.AddDays(-1), maxIndex);
            return read;
        }

        /// <summary>The DATE of the close ValueAsOf would read at or before <paramref name="then"/>.</summary>
        private static DateTime? LastCloseDate(HistoryStore store, string ticker, DateTime then)
        {
            foreach (var p in store.GetDaily(ticker, 400).Reverse())
                if (p.Date.Date <= then.Date) return p.Date.Date;
            return null;
        }

        /// <summary>Central-bank runs from config: contracts are the future decision dates, roll
        /// boundaries are every decision date including the settled ones.</summary>
        public static StripTable ForMeetings(
            MeetingScheduleDef sched, HistoryStore store, DateTime asOf, int maxRows = 8)
        {
            var future = sched.Dates.Where(d => d.Date > asOf.Date).OrderBy(d => d).Take(maxRows).ToList();
            var contracts = future.Select(d => (d.ToString("dd-MMM-yy"), d)).ToList();
            // Roll boundaries SNAP TO DECISION DATES where the calendar has them: the numbered
            // tickers re-point at the decision, not at the swap-period start — for the BOJ those
            // differ by up to six days (settlement lag; RBA showed the 1-day version live
            // 2026-08-11), and a lookback landing between them would shift by one index. The
            // period dates still contribute for banks with no decision calendar; the 14-day
            // cluster keeps the EARLIEST of each pair, which is the decision.
            var bounds = sched.DecisionDates.Concat(sched.Dates).Concat(sched.PastDates);
            var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));

            if (pat == null || contracts.Count == 0)
                return new StripTable { Title = $"{sched.Name} · {sched.Ccy}", AsOf = asOf };

            // The composite (source-less) spelling is what the store keys these under for the
            // stitcher's benefit; see TickerUniverse.
            var t = RollingStrip.Build($"{sched.Name} · {sched.Ccy}", store, asOf, contracts, bounds,
                n => pat.Replace("{N}", n.ToString()) + " Curncy");
            return t;
        }
    }
}
