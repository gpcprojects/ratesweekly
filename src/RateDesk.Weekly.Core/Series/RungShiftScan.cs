using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>WHICH CONTRACT A ROLLING TICKER MEANT ON A PAST DAY, READ OFF ITS OWN PRICES.
    ///
    /// The meeting-dated tickers are rolling generics: EESF1A is "the ECB's next meeting period",
    /// and what that IS changes as meetings pass. Every change column the desk publishes depends
    /// on knowing which contract each number belonged to on each past day.
    ///
    /// There are three ways to know, and they are used strictly in this order:
    ///
    ///   1. THE TICKER'S OWN FIELD. The store stamps each rung's SW_EFF_DT every run
    ///      (HistoryStore.EffectiveOn). Exact, and unarguable - but SW_EFF_DT is a reference
    ///      field, so it can only ever be written down as it is seen. Recording began
    ///      2026-08-26; there is nothing before that and no way to fetch it.
    ///   2. THIS SCAN. Prices ARE backfilled - the first run on a machine seeds 45 days per
    ///      ticker - so the renumbering is visible in the price history itself, and visible for
    ///      the whole seeded window rather than only from the day recording began.
    ///   3. THE CALENDAR. Count the announcements between a past day and a contract. Correct
    ///      whenever the calendar was stable, which is nearly always; wrong when a meeting
    ///      entered the calendar after the fact (an unscheduled decision), because it then
    ///      numbers past days as though the market had always known about it.
    ///
    /// HOW THE SCAN WORKS. A renumber is not a price move, it is the whole strip stepping along
    /// itself. If the family rolled overnight, today's rung 1 holds what yesterday's rung 2 held,
    /// today's 2 holds yesterday's 3, and so on - every rung at once. So for a candidate day the
    /// scan simply asks which of a few hypotheses fits the numbers:
    ///
    ///     shift +1  a meeting passed: today's rung N == yesterday's rung N+1
    ///     shift  0  nothing renumbered
    ///     shift -1  a meeting was INSERTED at the front (an unscheduled decision): today's
    ///               rung N == yesterday's rung N-1
    ///
    /// The insertion case is why this exists at all. It is the one the calendar cannot get right,
    /// and it has its own unmistakable signature - the strip shifting the OTHER way.
    ///
    /// WHY IT IS SAFE. The scan reports Confirmed only when one hypothesis fits closely AND the
    /// runners-up are clearly worse. It needs several rungs agreeing at once, and it only counts
    /// rungs where the strip is sloped enough for the hypotheses to differ. Where the strip is
    /// FLAT the scan abstains - and a flat strip is precisely the case where the answer does not
    /// matter, because every hypothesis yields the same number. Detection is impossible only
    /// where it is unnecessary.
    ///
    /// WHAT IT NEVER DOES. It never invents a price, never adjusts one, and never overrules a
    /// recorded SW_EFF_DT. When it cannot tell, it says Unknown and the caller falls back.</summary>
    public static class RungShiftScan
    {
        /// <summary>The hypotheses tested, in the order they are reported on ties. A shift of -1
        /// (an inserted meeting) is deliberately last: it is the rarest event, so it must clear
        /// the same bar as the others and win outright, never by default.</summary>
        private static readonly int[] Hypotheses = { 0, 1, 2, -1 };

        /// <summary>A rung pair only helps decide when the gap between the two contracts is
        /// bigger than this: below it, every hypothesis predicts near enough the same number and
        /// the pair tells us nothing. 3bp - comfortably above quote noise, well below a policy
        /// step.</summary>
        public const double MinSlopeBp = 3.0;

        /// <summary>How closely the winning hypothesis must fit, per rung, in bp. A renumber is
        /// exact - the same contract's own close - so the residual should be a rounding artefact,
        /// not a market move.</summary>
        public const double FitToleranceBp = 1.5;

        /// <summary>How far clear of the runner-up the winner must be, in bp per rung. Without
        /// this a strip whose gaps happen to be small would "confirm" on noise.</summary>
        public const double MinMarginBp = 4.0;

        /// <summary>How many discriminating rungs must agree. Three simultaneous, independent
        /// confirmations; a market move that reproduces the pattern at three points at once, each
        /// by exactly its own neighbour gap, is not a thing that happens.</summary>
        public const int MinSupport = 3;

        public enum Verdict
        {
            /// <summary>The prices could not decide. The caller must fall back, never guess.</summary>
            Unknown,
            /// <summary>One hypothesis fits closely and the others clearly do not.</summary>
            Confirmed,
        }

        /// <summary>What happened to the numbering between the previous business day and this one.
        /// <paramref name="Shift"/> is how far along the strip each rung moved: +1 a meeting
        /// passed, 0 nothing, -1 a meeting was inserted at the front.</summary>
        public sealed record DayShift(
            DateTime Day, int Shift, Verdict Verdict, int Support, double MarginBp, string Why);

        /// <summary>Scan every business day in (from, to] for a renumbering.
        /// <paramref name="ticker"/> maps a 1-based rung to its full security.</summary>
        /// <param name="liveMid">Today's live mid per rung, when the caller has a snapshot in
        /// hand. The store never books today's print as a close, so without this the scan is
        /// blind to a renumbering that happened TODAY - which is exactly when an unscheduled
        /// decision shows up. These are real prints, just not yet settled ones.</param>
        public static List<DayShift> Scan(HistoryStore store, Func<int, string> ticker,
            DateTime from, DateTime to, int maxRung = 13, Func<int, double?>? liveMid = null)
        {
            // one store read per rung, then everything from memory
            int lookback = (int)(DateTime.Today - from.Date).TotalDays + 15;
            var byRung = new Dictionary<int, Dictionary<DateTime, double>>();
            for (int n = 1; n <= maxRung; n++)
            {
                var pts = store.GetDaily(ticker(n), Math.Max(20, lookback));
                var day = pts.GroupBy(p => p.Date.Date).ToDictionary(g => g.Key, g => g.Last().Value);
                if (liveMid?.Invoke(n) is { } live) day[DateTime.Today] = live;
                if (day.Count == 0) break;                 // the family ends here
                byRung[n] = day;
            }

            var days = new List<DateTime>();
            foreach (var d in AllDays(from, to)) if (IsBusinessDay(d)) days.Add(d);

            var res = new List<DayShift>();
            for (int i = 1; i < days.Count; i++)
                res.Add(Judge(byRung, days[i - 1], days[i], maxRung));
            return res;
        }

        /// <summary>The whole test, for one pair of consecutive days.</summary>
        private static DayShift Judge(Dictionary<int, Dictionary<DateTime, double>> byRung,
            DateTime prev, DateTime day, int maxRung)
        {
            double? At(int n, DateTime d) =>
                byRung.TryGetValue(n, out var m) && m.TryGetValue(d.Date, out var v) ? v : null;

            // The strip must be sloped somewhere, or nothing can be told apart. Measure the slope
            // on the PREVIOUS day, which is the day the hypotheses read from.
            var scored = new List<(int Shift, double MeanErrBp, int Support)>();
            foreach (var s in Hypotheses)
            {
                double sum = 0; int n0 = 0;
                for (int n = 1; n <= maxRung; n++)
                {
                    var today = At(n, day);
                    var thenSame = At(n, prev);            // what "no renumber" predicts
                    var thenShift = At(n + s, prev);       // what THIS hypothesis predicts
                    if (today is not { } t || thenShift is not { } h || thenSame is not { } q) continue;
                    // only count a rung where this hypothesis actually says something different
                    // from "nothing happened" - otherwise it is free agreement, not evidence
                    if (s != 0 && Math.Abs(h - q) * 100.0 < MinSlopeBp) continue;
                    sum += Math.Abs(t - h) * 100.0;
                    n0++;
                }
                if (n0 > 0) scored.Add((s, sum / n0, n0));
            }

            // the "nothing happened" hypothesis is scored over every rung that quotes both days,
            // so that it can win on a quiet day even where no pair is discriminating
            {
                double sum = 0; int n0 = 0;
                for (int n = 1; n <= maxRung; n++)
                    if (At(n, day) is { } t && At(n, prev) is { } q) { sum += Math.Abs(t - q) * 100.0; n0++; }
                scored.RemoveAll(x => x.Shift == 0);
                if (n0 > 0) scored.Add((0, sum / n0, n0));
            }

            if (scored.Count == 0)
                return new DayShift(day, 0, Verdict.Unknown, 0, 0,
                    "no prices on both days - nothing to compare");

            var ranked = scored.OrderBy(x => x.MeanErrBp).ToList();
            var best = ranked[0];
            double margin = ranked.Count > 1 ? ranked[1].MeanErrBp - best.MeanErrBp : double.MaxValue;

            if (best.Support < MinSupport && best.Shift != 0)
                return new DayShift(day, 0, Verdict.Unknown, best.Support, margin,
                    $"only {best.Support} rung(s) could tell the difference - need {MinSupport}");
            if (best.MeanErrBp > FitToleranceBp)
                return new DayShift(day, 0, Verdict.Unknown, best.Support, margin,
                    $"the closest match is still {best.MeanErrBp:0.0}bp out per rung - the prices " +
                    "moved too much to say what the numbering did");
            if (margin < MinMarginBp)
                return new DayShift(day, 0, Verdict.Unknown, best.Support, margin,
                    $"two explanations fit almost equally well ({margin:0.0}bp apart) - the strip " +
                    "is too flat here to tell them apart");

            string why = best.Shift switch
            {
                0 => "the tickers still point at the same contracts",
                1 => "a meeting passed and every rung stepped up one",
                -1 => "an extra meeting appeared at the front and every rung stepped down one",
                _ => $"{best.Shift} meetings passed at once",
            };
            return new DayShift(day, best.Shift, Verdict.Confirmed, best.Support, margin,
                $"{why} ({best.Support} rungs agree, {best.MeanErrBp:0.0}bp fit, " +
                $"next-best {margin:0.0}bp worse)");
        }

        /// <summary>How many rungs a contract has moved along the strip between
        /// <paramref name="from"/> and the end of the scan - i.e. add this to a contract's rung
        /// TODAY to get the rung it sat on then. Null when any day in between could not be
        /// judged: a chain is only as good as its weakest link, and half a chain is a guess.</summary>
        public static int? ShiftSince(IReadOnlyList<DayShift> scan, DateTime from)
        {
            int total = 0;
            foreach (var s in scan)
            {
                if (s.Day.Date <= from.Date) continue;
                if (s.Verdict != Verdict.Confirmed) return null;
                total += s.Shift;
            }
            return total;
        }

        /// <summary>The delegate PricingService consumes. One line at each call site, so the
        /// email, the daily run and the CLI cannot end up reading the strip differently.</summary>
        public static Func<RateDesk.Core.MeetingScheduleDef, Func<int, string>, DateTime,
            Func<int, double?>, IReadOnlyList<(DateTime Day, int Shift, bool Confirmed)>>
            Bind(HistoryStore store) =>
            (sched, ticker, from, live) => Scan(store, ticker, from, DateTime.Today, 13, live)
                .Select(x => (x.Day, x.Shift, x.Verdict == Verdict.Confirmed))
                .ToList();

        private static IEnumerable<DateTime> AllDays(DateTime from, DateTime to)
        {
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) yield return d;
        }

        private static bool IsBusinessDay(DateTime d) =>
            d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
