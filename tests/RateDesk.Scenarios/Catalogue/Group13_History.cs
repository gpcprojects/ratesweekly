using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE HISTORY TABLES across a decision.
///
/// The macro-enabled save-down workbooks are filled from `DailyBook.BankHistoryRows`: for every
/// business day in the window and every published meeting period, the rate that period was quoted
/// at on that day, walked back through the renumbering. It is the desk's own record of what a rate
/// meant when, and it is regenerated from scratch on every run - so a roll fault there rewrites
/// history rather than mis-stating one day.
///
/// The rule it must follow: a boundary day (the announcement) and every day between the
/// announcement and the period start are UNATTRIBUTABLE - the family is renumbering through them -
/// so a row dated in that window carries the last clean value before the announcement, never a
/// half-renumbered read. This scenario checks the whole table against an independently computed
/// answer, day by day and period by period.</summary>
public static class Group13_History
{
    public static IEnumerable<ScenarioSpec> All()
    {
        // ECB shape: announced 9 days ago, the period it decided started 3 days ago
        DateTime a0 = Cal.D(-58), s0 = Cal.D(-52);
        DateTime aPast = Cal.D(-9), sPast = Cal.D(-3);
        DateTime d1 = Cal.D(40), m1 = Cal.D(46);
        DateTime d2 = Cal.D(89), m2 = Cal.D(95);
        DateTime d3 = Cal.D(138), m3 = Cal.D(144);
        DateTime d4 = Cal.D(187), m4 = Cal.D(193);
        var bounds = new[] { a0, aPast, d1, d2, d3, d4 };

        const double fix = 2.250;
        // a 25bp hike, fully priced: every contract steps 1bp on the announcement
        const double curPre = 2.240, curPost = 2.250;
        const double m1Pre = 2.290, m1Post = 2.300;
        const double m2Pre = 2.320, m2Post = 2.330;
        const double m3Pre = 2.340, m3Post = 2.350;
        const double m4Pre = 2.360, m4Post = 2.370;

        var spec = new ScenarioSpec
        {
            Id = 57,
            Name = "Save-down history tables across a decision",
            Question = "Does every history row carry the value that period was actually quoted " +
                       "at on that day, including through the announcement-to-start window?",
            CheckHistoryRows = true,
            HistoryDays = 25,
        };

        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { s0, sPast, m1, m2, m3, m4 });
        b.DecisionDates.AddRange(new[] { aPast, d1, d2, d3, d4 });
        b.Fix(fix).FixHist(Cal.D(-70), Cal.D(-1), fix);

        // the feed re-pointed at the announcement, nine days ago
        b.Quote(0, mid: curPost, prevClose: curPost, eff: sPast, mat: m1);
        b.Quote(1, mid: m1Post, prevClose: m1Post, eff: m1, mat: m2);
        b.Quote(2, mid: m2Post, prevClose: m2Post, eff: m2, mat: m3);
        b.Quote(3, mid: m3Post, prevClose: m3Post, eff: m3, mat: m4);

        b.ContractStep(sPast, bounds, Cal.D(-70), Cal.D(-1), aPast, curPre, curPost);
        b.ContractStep(m1, bounds, Cal.D(-70), Cal.D(-1), aPast, m1Pre, m1Post);
        b.ContractStep(m2, bounds, Cal.D(-70), Cal.D(-1), aPast, m2Pre, m2Post);
        b.ContractStep(m3, bounds, Cal.D(-70), Cal.D(-1), aPast, m3Pre, m3Post);
        b.ContractStep(m4, bounds, Cal.D(-70), Cal.D(-1), aPast, m4Pre, m4Post);
        spec.Banks.Add(b);

        // the board itself: Priced = (mid - 2.250) * 100
        spec.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = fix,
            Rebased = false,          // the period it decided has already started
            Rows = new List<RowExpect>
            {
                new(m1, m2, m1Post, +5.0, null, 0.0, 0.0, Any.Num),
                new(m2, m3, m2Post, +8.0, +3.0, 0.0, 0.0, Any.Num),
                new(m3, m4, m3Post, +10.0, +2.0, 0.0, 0.0, Any.Num),
            },
        });

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            if (!s.HistoryRows.TryGetValue("ECB", out var rows) || rows.Count == 0)
            { msgs.Add("no history rows were produced at all"); return msgs; }

            // INDEPENDENT ANSWER, computed here from the synthetic market alone.
            // A day is unattributable when it IS the announcement or falls between the
            // announcement and the period start; the honest value there is the last clean
            // pre-announcement one.
            var mixed = new HashSet<DateTime>();
            for (var d = aPast.AddDays(1); d < sPast; d = d.AddDays(1)) mixed.Add(d.Date);
            for (var d = a0.AddDays(1); d < s0; d = d.AddDays(1)) mixed.Add(d.Date);

            double Truth(DateTime period, DateTime day)
            {
                var d = day.Date;
                while (d == aPast.Date || d == a0.Date || mixed.Contains(d)) d = d.AddDays(-1);
                bool post = d >= aPast.Date;
                if (period == m1) return post ? m1Post : m1Pre;
                if (period == m2) return post ? m2Post : m2Pre;
                if (period == m3) return post ? m3Post : m3Pre;
                return double.NaN;
            }

            int checkedRows = 0;
            foreach (var r in rows)
            {
                double want = Truth(r.Start, r.Day);
                if (double.IsNaN(want)) continue;
                checkedRows++;
                if (Math.Abs(r.Rate - want) > 1e-9)
                    msgs.Add($"history {r.Day:dd-MMM-yy} / period {r.Start:dd-MMM-yy}: " +
                             $"{r.Rate:0.000} != {want:0.000}" +
                             (mixed.Contains(r.Day.Date) || r.Day.Date == aPast.Date
                                 ? " (this day sits in the announcement-to-start window, where the " +
                                   "family is renumbering and only the last clean value is honest)"
                                 : ""));
                // a boundary day cannot support a change-on-day: both sides resolve to the same
                // pre-announcement close, and publishing 0.0 there would read as "unchanged"
                if (r.Day.Date == aPast.Date && r.D1 is { } d1v)
                    msgs.Add($"history {r.Day:dd-MMM-yy} / period {r.Start:dd-MMM-yy}: publishes a " +
                             $"1d change of {d1v:+0.0;-0.0;0.0}bp on the announcement day, which is " +
                             "unanchorable");
            }
            if (checkedRows < 20)
                msgs.Add($"only {checkedRows} history row(s) could be checked - the window is too " +
                         "thin for this scenario to mean anything");
            return msgs;
        });
        yield return spec;
    }
}
