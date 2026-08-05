using RateDesk.Weekly.Core.Render;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>The monthly CPI FIXING swaps — the market's forecast of each upcoming index print.
    ///
    /// Everything here was established by probing the terminal (2026-08-05), not assumed:
    ///
    /// INDEXING IS BY CALENDAR MONTH, NOT POSITION. USSWIF1 is "CPI FIX JAN", USSWIF12 is DEC.
    /// So the family is a rolling 12-month window and any given ticker keeps its calendar month
    /// forever — only the YEAR rolls, once a year, on the day that month publishes.
    ///
    /// THE LAG IS IN THE MATURITY. Each security matures a fixed number of months after the month
    /// it fixes: USD and EUR +3, GBP +2 (USSWIF7 "JUL" matures 2026-10-01; BPSWIF7 "JUL" matures
    /// 2026-09-15). That is Bloomberg stating the market's lag convention as data, which is why
    /// this class derives the reference month from the maturity rather than hard-coding a lag —
    /// and it independently confirms the 2-month RPI swap convention.
    ///
    /// WHICH FIXING IS ACTIVE FALLS OUT OF THE MATURITIES. Sorting the twelve by maturity puts the
    /// next unfixed month first: once July publishes, USSWIF7 re-points to the following July and
    /// its maturity jumps a year, so August naturally becomes the front. No release calendar to
    /// maintain, and it cannot drift out of date.
    ///
    /// UNITS DIFFER BY MARKET. USD quotes the INDEX LEVEL (USSWIF7 334.10 continues CPURNSA's
    /// 333.95). GBP and EUR quote the YEAR-ON-YEAR RATE in basis points (BPSWIF7 328.5 = 3.285%
    /// against a UKRPI index of 416.5; EUSWIF7 287.5 = 2.875% against a CPTFEMU index of 102.98).
    /// Reading a rate as an index — or the reverse — would put a nonsense number on the page, so
    /// <see cref="Unit"/> is explicit and <see cref="SanityNote"/> re-checks it against the
    /// published index every build.</summary>
    public static class CpiFixings
    {
        public enum FixUnit { IndexLevel, YoYBp }

        public sealed record Family(string Ccy, string Root, string Name, FixUnit Unit, string FixingTicker);

        /// <summary>Probed live 2026-08-05: plain/BGN carry the quotes, BLC is empty for all three.</summary>
        public static readonly Family[] Families =
        {
            new("USD", "USSWIF", "CPI",   FixUnit.IndexLevel, "CPURNSA Index"),
            new("GBP", "BPSWIF", "RPI",   FixUnit.YoYBp,      "UKRPI Index"),
            new("EUR", "EUSWIF", "HICPxT", FixUnit.YoYBp,     "CPTFEMU Index"),
        };

        public static Family? For(string ccy) =>
            Families.FirstOrDefault(f => f.Ccy.Equals(ccy, StringComparison.OrdinalIgnoreCase));

        public static IEnumerable<string> Tickers(Family f)
        {
            for (int m = 1; m <= 12; m++) yield return $"{f.Root}{m} Curncy";
        }

        public sealed record Ladder(List<LadderPoint> Rows, List<string> Notes, string ValueLabel, int Dp);

        /// <summary>The twelve fixings ordered from the next one to fix, with 1w/1m changes.</summary>
        public static Ladder Build(Family f, HistoryStore store, DateTime asOf)
        {
            var notes = new List<string>();

            // Order by maturity: the nearest is the next month still to print.
            var slots = new List<(string Ticker, DateTime Mat, int Month)>();
            for (int m = 1; m <= 12; m++)
            {
                var tk = $"{f.Root}{m} Curncy";
                // "What does this ticker mean now" — deliberately not as-of the close date, since
                // maturities are observed live and the page renders as of the last settled close.
                if (store.MaturityLatest(tk) is { } mat) slots.Add((tk, mat, m));
            }
            if (slots.Count == 0)
                return new Ladder(new(), new List<string> { "no maturities recorded yet — run an update" },
                                  f.Unit == FixUnit.IndexLevel ? "index" : "y/y %", 2);

            slots.Sort((a, b) => a.Mat.CompareTo(b.Mat));
            int lagMonths = LagFrom(slots[0]);

            var rows = new List<LadderPoint>();
            int rolled = 0;
            foreach (var s in slots)
            {
                var refMonth = s.Mat.AddMonths(-lagMonths);
                double? now = store.ValueAsOf(s.Ticker, asOf);
                double? wk = LookBack(store, s, asOf.AddDays(-WeeklyCurves.WeekDays), ref rolled);
                double? mo = LookBack(store, s, asOf.AddDays(-WeeklyCurves.MonthDays), ref rolled);
                if (now is null) continue;

                double scale = f.Unit == FixUnit.YoYBp ? 0.01 : 1.0;   // bp → %
                rows.Add(new LadderPoint(refMonth.ToString("MMM yy"),
                    now * scale, wk * scale, mo * scale));
            }

            notes.Add($"fixings ordered from the next to print · lag {lagMonths}m (from each security's own maturity)");
            notes.Add(f.Unit == FixUnit.IndexLevel
                ? "quoted as the index level"
                : "quoted as the year-on-year rate");
            if (rolled > 0)
                notes.Add($"{rolled} lookback(s) suppressed — the ticker rolled to the next year inside the window, " +
                          "so there is no like-for-like comparison");

            return new Ladder(rows, notes,
                f.Unit == FixUnit.IndexLevel ? "index" : "y/y %",
                f.Unit == FixUnit.IndexLevel ? 2 : 3);
        }

        /// <summary>Whole months between a fixing's reference month and its maturity. Taken from
        /// the front contract, whose reference month is unambiguous.</summary>
        private static int LagFrom((string Ticker, DateTime Mat, int Month) s)
        {
            // s.Month is the calendar month it fixes; walk back until the months line up.
            for (int lag = 1; lag <= 6; lag++)
                if (s.Mat.AddMonths(-lag).Month == s.Month) return lag;
            return 3;
        }

        /// <summary>Value on a past date, but only when the ticker still meant the SAME contract
        /// then. A CPI fixing ticker jumps a full year the day its month publishes, and the month
        /// it used to quote is then fixed and quoted by nobody — so the honest answer after a roll
        /// is no change at all, not a year's worth of drift dressed up as a weekly move.</summary>
        private static double? LookBack(
            HistoryStore store, (string Ticker, DateTime Mat, int Month) s, DateTime then, ref int rolled)
        {
            var thenMat = store.MaturityAsOf(s.Ticker, then);
            if (thenMat is null) return store.ValueAsOf(s.Ticker, then);   // no record: best effort
            if (thenMat.Value != s.Mat) { rolled++; return null; }
            return store.ValueAsOf(s.Ticker, then);
        }

        /// <summary>Cross-check the declared unit against the published index, so a wrong call
        /// shows up as a note rather than a plausible-looking wrong number.</summary>
        public static string? SanityNote(Family f, HistoryStore store, DateTime asOf, List<LadderPoint> rows)
        {
            if (rows.Count == 0 || rows[0].Now is not { } first) return null;
            if (store.ValueAsOf(f.FixingTicker, asOf) is not { } published) return null;

            bool looksLikeIndex = Math.Abs(first - published) / Math.Max(1e-9, Math.Abs(published)) < 0.15;
            bool declaredIndex = f.Unit == FixUnit.IndexLevel;
            if (looksLikeIndex == declaredIndex) return null;
            return $"⚠ unit check: the front fixing ({first:0.###}) does not sit where a " +
                   $"{(declaredIndex ? "index level" : "rate")} should against the published " +
                   $"{f.FixingTicker} ({published:0.###}) — verify the quoting convention";
        }
    }
}
