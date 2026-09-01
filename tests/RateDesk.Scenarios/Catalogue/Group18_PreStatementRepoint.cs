using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE FOURTH QUADRANT of the announcement-day 2x2 (audit 2026-08-31, catalogue 120).
///
/// Announcement day has four states: {announced, not announced} x {feed re-pointed, not
/// re-pointed}. Scenarios 2/3 cover announced+not-re-pointed, 4 covers not-announced+
/// not-re-pointed, 5 covers announced+re-pointed. Nobody covered the fourth: Bloomberg
/// renumbers the family EARLY, hours before the statement. There the announced-gate is
/// rightly silent (nothing is announced), but every rung's PX_CLOSE_1D already belongs to
/// the NEXT contract along — so the naive change-on-day differences two different meetings
/// and a flat tape prints a phantom step down the whole strip.
///
/// The fix under test is the roll correction's EVIDENCE ARM: when the store's record of the
/// previous business day disagrees with a rung's live SW_EFF_DT, the family provably
/// renumbered since that close, and the correction (mid(N) − PrevClose(N+1)) is due with or
/// without an announcement. Records are the evidence — this scenario deliberately seeds NO
/// price history, so the Δ1d column rides the corrected CoD path and nothing else.</summary>
public static class Group18_PreStatementRepoint
{
    // FOMC same-day-start shape, decision TODAY, statement NOT yet out.
    private static readonly DateTime P1 = Cal.D(-42);                    // previous period start
    private static readonly DateTime D0 = Cal.D(0);                      // decision + start today
    private static readonly DateTime D1 = Cal.D(42), D2 = Cal.D(84), D3 = Cal.D(126), D4 = Cal.D(168);

    private const double Fix = 4.330;    // EFFR — the pre-decision rate, still in force
    private const double M0 = 4.180;     // the period being decided today (now on rung 0)
    private const double M1 = 4.050;     // [D1, D2)
    private const double M2 = 3.930;     // [D2, D3)
    private const double M3 = 3.850;     // [D3, D4)

    public static IEnumerable<ScenarioSpec> All()
    {
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimeNotYetPassed };
        b.Dates.AddRange(new[] { P1, D0, D1, D2, D3, D4 });
        b.DecisionDates.AddRange(new[] { D0, D1, D2, D3, D4 });
        b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

        // THE FEED HAS ALREADY RE-POINTED, pre-statement: the just-decided period sits on the
        // run-down rung 0 and every published rung is one contract further out than yesterday.
        // Each PrevClose is what THAT RUNG closed at yesterday — i.e. the next-nearer contract,
        // because yesterday the rung pointed one meeting closer in. The market itself is FLAT.
        b.Quote(0, mid: M0, prevClose: Fix, eff: D0, mat: D1);
        b.Quote(1, mid: M1, prevClose: M0, eff: D1, mat: D2);
        b.Quote(2, mid: M2, prevClose: M1, eff: D2, mat: D3);
        b.Quote(3, mid: M3, prevClose: M2, eff: D3, mat: D4);

        // NO closes, NO snaps: the stitched anchors must come up empty so Δ1d can only come
        // from the CoD path under test. What the store DOES hold is Bloomberg's own per-day
        // rung documentation through yesterday — the OLD numbering (rung 1 was the period
        // starting today, and so on down the strip).
        foreach (var day in Cal.BusinessDays(Cal.D(-40), Cal.D(-1)))
        {
            b.Records.Add((1, day, D0, D1));
            b.Records.Add((2, day, D1, D2));
            b.Records.Add((3, day, D2, D3));
            b.Records.Add((4, day, D3, D4));
        }

        var s = new ScenarioSpec
        {
            Id = 65,
            Name = "Feed re-points BEFORE the statement - flat tape, corrected CoD",
            Question = "Bloomberg renumbers the family hours before the decision is announced. " +
                       "Nothing is announced, so the gate stands down - but every PrevClose now " +
                       "belongs to the next contract along. Does a flat tape still print 0.0?",
        };
        s.Banks.Add(b);

        // Hand arithmetic. RefPct = the printed EFFR 4.330 (nothing announced, no re-base).
        //   row D1: Mid 4.050, Priced (4.050-4.330)*100 = -28.0, Step blank (front),
        //           d1 = mid(1) - PrevClose(2) = 4.050 - 4.050 = 0.0 (naive would say
        //           4.050 - 4.180 = -13.0 - the phantom step this scenario exists to catch);
        //   row D2: Mid 3.930, Priced -40.0, Step -12.0, d1 = 3.930 - 3.930 = 0.0;
        //   row D3: Mid 3.850, Priced -48.0, Step -8.0, d1 blank (no rung 4 quote to correct
        //           against - blank beats the naive 3.850 - 3.930 = -8.0, which is phantom);
        //           End = D4 from the rung's OWN maturity field (documented, so it publishes -
        //           first drafted blank, corrected per rule 2: setup error, not a defect).
        // w1/m1 blank everywhere: no stored prices exist, and nothing may invent an anchor.
        s.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = Fix,
            Rebased = false,
            Front = new FrontExpect(D1, D1, M1, Fix, -28.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(D1, D2, M1, -28.0, null, 0.0, null, null),
                new(D2, D3, M2, -40.0, -12.0, 0.0, null, null),
                new(D3, D4, M3, -48.0, -8.0, null, null, null),
            },
        });
        s.NotesNotContain.Add("FUTURES GUARD");
        yield return s;
    }
}
