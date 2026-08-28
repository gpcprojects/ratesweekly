using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE WORKED EXAMPLES. Scenario 1 is the quiet control every decision scenario is
/// measured against; scenario 99 is the positive control that proves the harness can go red.
///
/// GEOMETRY USED THROUGHOUT (FOMC shape - the decision day IS the period start, periods are
/// contiguous, the family renumbers at the announcement):
///
///     P2 ......... P1 ..............|today|.......... D1 ......... D2 ......... D3 ......... D4
///     settled      settled                            next         then        then        then
///
/// The market is QUIET: every contract's rate is constant over the whole window. Its TICKER
/// NUMBER is not - it steps down at each boundary - so a surface that differences a rung against
/// its own past close books the inter-contract gap as a move. Every change column here must
/// therefore print 0.0, and any non-zero is a roll fault.</summary>
public static class Group00_Baseline
{
    // the calendar
    private static readonly DateTime P2 = Cal.D(-84), P1 = Cal.D(-42);
    private static readonly DateTime D1 = Cal.D(21), D2 = Cal.D(63), D3 = Cal.D(105), D4 = Cal.D(147);
    private static readonly DateTime[] Bounds = { P2, P1, D1, D2, D3, D4 };

    // the market: the current period, then the three quoted meeting periods
    private const double Fixing = 3.900;   // FEDL01, the o/n rate the current period pays
    private const double MidD1 = 3.750, MidD2 = 3.700, MidD3 = 3.600, MidD4 = 3.500;

    /// <summary>The bank both scenarios share. Seeded contract by contract, so the ticker each
    /// value lands on is derived from the boundary list rather than assumed.</summary>
    private static BankSpec Fomc()
    {
        var b = new BankSpec { Bank = "FOMC" };
        // settled starts stay in "dates" exactly as the shipped config keeps them - the loader
        // migrates them into pastDates itself, and AnnouncementDates derives past announcements
        // from them. Authoring them any other way would test a config shape the desk never runs.
        b.Dates.AddRange(new[] { P2, P1, D1, D2, D3, D4 });
        b.DecisionDates.AddRange(new[] { D1, D2, D3, D4 });

        b.Fix(Fixing).FixHist(Cal.D(-70), Cal.D(-1), Fixing);

        // live quotes: rung 0 is the run-down (matures at the next meeting), 1..3 the meeting
        // periods, 4 unquoted so the family ends where Bloomberg's documentation ends
        b.Quote(0, mid: Fixing, prevClose: Fixing, eff: P1, mat: D1);
        b.Quote(1, mid: MidD1, prevClose: MidD1, eff: D1, mat: D2);
        b.Quote(2, mid: MidD2, prevClose: MidD2, eff: D2, mat: D3);
        b.Quote(3, mid: MidD3, prevClose: MidD3, eff: D3, mat: D4);

        // history, per CONTRACT (a quiet tape: each contract's rate never moves)
        b.Contract(P1, Bounds, Cal.D(-70), Cal.D(-1), Fixing);
        b.Contract(D1, Bounds, Cal.D(-70), Cal.D(-1), MidD1);
        b.Contract(D2, Bounds, Cal.D(-70), Cal.D(-1), MidD2);
        b.Contract(D3, Bounds, Cal.D(-70), Cal.D(-1), MidD3);
        b.Contract(D4, Bounds, Cal.D(-70), Cal.D(-1), MidD4);
        return b;
    }

    private static List<RowExpect> QuietRows() => new()
    {
        new RowExpect(D1, D2, MidD1, -15.0, null, 0.0, 0.0, 0.0),
        new RowExpect(D2, D3, MidD2, -20.0, -5.0, 0.0, 0.0, 0.0),
        new RowExpect(D3, D4, MidD3, -30.0, -10.0, 0.0, 0.0, 0.0),
    };

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 1
        var quiet = new ScenarioSpec
        {
            Id = 1,
            Name = "Quiet week, no decision in the window",
            Question = "With no decision anywhere near, does the run publish the right three rows, " +
                       "flat changes, and a front line pointing at the next meeting?",
        };
        quiet.Banks.Add(Fomc());
        quiet.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = Fixing,
            Rebased = false,
            Front = new FrontExpect(D1, D1, MidD1, Fixing, -15.0, Rebased: false),
            Rows = QuietRows(),
        });
        quiet.NotesNotContain.Add("CHECK");
        quiet.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        quiet.NotesNotContain.Add("STALE");
        yield return quiet;

        // ---------------------------------------------------------------- 99 (positive control)
        var control = new ScenarioSpec
        {
            Id = 99,
            Name = "POSITIVE CONTROL - the harness must be able to fail",
            Question = "Fed a deliberately wrong expectation, does the harness report it? " +
                       "A suite that cannot go red proves nothing.",
            MustFail = true,
        };
        control.Banks.Add(Fomc());
        control.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            // every one of these is wrong on purpose
            Fixing = 1.111,
            Rebased = true,
            Front = new FrontExpect(D2, D2, 9.999, 1.111, +99.9, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(D1, D2, 9.999, +99.9, +99.9, +99.9, +99.9, +99.9),
                new(D2, D3, MidD2, -20.0, -5.0, 0.0, 0.0, 0.0),
                new(D3, D4, MidD3, -30.0, -10.0, 0.0, 0.0, 0.0),
            },
        });
        control.NotesContain.Add("a note that will never exist");
        yield return control;
    }
}
