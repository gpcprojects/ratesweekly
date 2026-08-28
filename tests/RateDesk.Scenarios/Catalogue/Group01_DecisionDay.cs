using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>DECISION DAY, the two shapes the ten runs come in.
///
/// SAME-DAY START (FOMC, MPC): the swap period the decision governs begins on the decision date
/// itself, so the announced-but-not-yet-effective re-base (which is gated on today &lt; period
/// start) cannot apply.
///
/// LAGGED START (ECB, BOJ, SNB and the Riksbank at 6 days; RBA, RBNZ, BOC, Norges at 1): the
/// decision is announced days before the period it governs begins. Between the two, the o/n
/// fixing still prints the OLD rate, so Priced re-bases onto the just-decided period's own OIS.
///
/// Both are run with the feed NOT yet re-pointed - the state a run minutes after the statement
/// is actually in, and the reason the time-gated roll exists at all.</summary>
public static class Group01_DecisionDay
{
    // ---------------------------------------------------------------- FOMC (same-day start)

    private static readonly DateTime F_P2 = Cal.D(-84), F_P1 = Cal.D(-42), F_D0 = Cal.D(0);
    private static readonly DateTime F_D1 = Cal.D(42), F_D2 = Cal.D(84), F_D3 = Cal.D(126), F_D4 = Cal.D(168);
    private static readonly DateTime[] F_Bounds = { F_P2, F_P1, F_D0, F_D1, F_D2, F_D3, F_D4 };

    private const double F_Fix = 3.900;                    // EFFR - still the PRE-cut rate today
    // the cut was ~fully priced, so the forward meeting contracts barely move on the day
    private const double F_Pre0 = 3.660, F_Post0 = 3.650;  // the period the decision governs
    private const double F_Pre1 = 3.560, F_Post1 = 3.550;
    private const double F_Pre2 = 3.500, F_Post2 = 3.490;
    private const double F_Pre3 = 3.460, F_Post3 = 3.450;
    private const double F_Pre4 = 3.430, F_Post4 = 3.420;

    private static BankSpec Fomc()
    {
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { F_P2, F_P1, F_D0, F_D1, F_D2, F_D3, F_D4 });
        b.DecisionDates.AddRange(new[] { F_D0, F_D1, F_D2, F_D3, F_D4 });
        b.Fix(F_Fix).FixHist(Cal.D(-70), Cal.D(-1), F_Fix);

        // THE FEED HAS NOT RE-POINTED: rung 1 still quotes the period that starts today
        b.Quote(0, mid: F_Fix, prevClose: F_Fix, eff: F_P1, mat: F_D0);
        b.Quote(1, mid: F_Post0, prevClose: F_Pre0, eff: F_D0, mat: F_D1);
        b.Quote(2, mid: F_Post1, prevClose: F_Pre1, eff: F_D1, mat: F_D2);
        b.Quote(3, mid: F_Post2, prevClose: F_Pre2, eff: F_D2, mat: F_D3);
        b.Quote(4, mid: F_Post3, prevClose: F_Pre3, eff: F_D3, mat: F_D4);

        b.Contract(F_P1, F_Bounds, Cal.D(-70), Cal.D(-1), F_Fix);
        b.ContractStep(F_D0, F_Bounds, Cal.D(-70), Cal.D(0), F_D0, F_Pre0, F_Post0);
        b.ContractStep(F_D1, F_Bounds, Cal.D(-70), Cal.D(0), F_D0, F_Pre1, F_Post1);
        b.ContractStep(F_D2, F_Bounds, Cal.D(-70), Cal.D(0), F_D0, F_Pre2, F_Post2);
        b.ContractStep(F_D3, F_Bounds, Cal.D(-70), Cal.D(0), F_D0, F_Pre3, F_Post3);
        b.ContractStep(F_D4, F_Bounds, Cal.D(-70), Cal.D(0), F_D0, F_Pre4, F_Post4);
        return b;
    }

    // ---------------------------------------------------------------- ECB (6-day lagged start)

    private static readonly DateTime E_S2 = Cal.D(-92), E_S1 = Cal.D(-50);
    private static readonly DateTime E_Dec0 = Cal.D(0), E_St0 = Cal.D(6);
    private static readonly DateTime E_Dec1 = Cal.D(49), E_St1 = Cal.D(55);
    private static readonly DateTime E_Dec2 = Cal.D(98), E_St2 = Cal.D(104);
    private static readonly DateTime E_Dec3 = Cal.D(147), E_St3 = Cal.D(153);
    private static readonly DateTime E_St4 = Cal.D(202);
    // the dates the family RENUMBERS on: the announcements (past ones derived as start - 6)
    private static readonly DateTime[] E_Bounds =
        { Cal.D(-98), Cal.D(-56), E_Dec0, E_Dec1, E_Dec2, E_Dec3 };

    private const double E_Fix = 2.000;                    // ESTRON - still the PRE-hike rate
    private const double E_Pre0 = 2.240, E_Post0 = 2.250;  // the just-decided period
    private const double E_Pre1 = 2.290, E_Post1 = 2.300;
    private const double E_Pre2 = 2.320, E_Post2 = 2.330;
    private const double E_Pre3 = 2.340, E_Post3 = 2.350;

    private static BankSpec Ecb()
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { E_S2, E_S1, E_St0, E_St1, E_St2, E_St3 });
        b.DecisionDates.AddRange(new[] { E_Dec0, E_Dec1, E_Dec2, E_Dec3 });
        b.Fix(E_Fix).FixHist(Cal.D(-70), Cal.D(-1), E_Fix);

        // feed not re-pointed: rung 1 still quotes the period the decision governs
        b.Quote(0, mid: E_Fix, prevClose: E_Fix, eff: E_S1, mat: E_St0);
        b.Quote(1, mid: E_Post0, prevClose: E_Pre0, eff: E_St0, mat: E_St1);
        b.Quote(2, mid: E_Post1, prevClose: E_Pre1, eff: E_St1, mat: E_St2);
        b.Quote(3, mid: E_Post2, prevClose: E_Pre2, eff: E_St2, mat: E_St3);
        b.Quote(4, mid: E_Post3, prevClose: E_Pre3, eff: E_St3, mat: E_St4);

        b.Contract(E_S1, E_Bounds, Cal.D(-70), Cal.D(-1), E_Fix);
        b.ContractStep(E_St0, E_Bounds, Cal.D(-70), Cal.D(0), E_Dec0, E_Pre0, E_Post0);
        b.ContractStep(E_St1, E_Bounds, Cal.D(-70), Cal.D(0), E_Dec0, E_Pre1, E_Post1);
        b.ContractStep(E_St2, E_Bounds, Cal.D(-70), Cal.D(0), E_Dec0, E_Pre2, E_Post2);
        b.ContractStep(E_St3, E_Bounds, Cal.D(-70), Cal.D(0), E_Dec0, E_Pre3, E_Post3);
        return b;
    }

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 2
        var fed = new ScenarioSpec
        {
            Id = 2,
            Name = "FOMC cuts 25bp TODAY, feed not re-pointed (same-day start)",
            Question = "After the statement, does the decided period leave the board, do the " +
                       "remaining rows keep their own dates and prices, and what is Priced " +
                       "measured against while EFFR still prints the old rate?",
        };
        fed.Banks.Add(Fomc());
        fed.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            // FIXED 2026-08-27: the re-base now fires for a same-day-start family too. EFFR
            // still prints the pre-cut 3.900, but the base is the just-decided period's own OIS
            // (3.650), so Priced measures against the rate the Fed actually set:
            //   (3.550 - 3.650) x 100 = -10.0 ; (3.490 - 3.650) = -16.0 ; (3.450 - 3.650) = -20.0
            Fixing = F_Post0,
            Rebased = true,
            Front = new FrontExpect(F_D1, F_D1, F_Post1, F_Post0, -10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(F_D1, F_D2, F_Post1, -10.0, null, -1.0, -1.0, -1.0),
                new(F_D2, F_D3, F_Post2, -16.0, -6.0, -1.0, -1.0, -1.0),
                new(F_D3, F_D4, F_Post3, -20.0, -4.0, -1.0, -1.0, -1.0),
            },
        });
        fed.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("FOMC")!;
            // the just-decided period must be GONE from every surface, feed or no feed
            if (run.Rows.Any(r => r.Date == F_D0))
                msgs.Add("the period the FOMC just decided is still on the board after the statement");
            var blk = Render.Blast(s.BlastText).GetValueOrDefault("FOMC");
            if (blk != null && blk.Rows.Any(r => r[0] == F_D0.ToString("dd-MMM-yy")))
                msgs.Add("the blast still carries the just-decided period");
            return msgs;
        });
        yield return fed;

        // ---------------------------------------------------------------- 3
        var ecb = new ScenarioSpec
        {
            Id = 3,
            Name = "ECB hikes 25bp TODAY, period starts in 6 days (lagged start)",
            Question = "Does the decided period roll off, and does Priced re-base onto that " +
                       "period's own OIS instead of the stale ESTR fixing?",
        };
        ecb.Banks.Add(Ecb());
        ecb.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = E_Post0,          // re-based onto the just-decided period's own OIS
            Rebased = true,
            Front = new FrontExpect(E_Dec1, E_St1, E_Post1, E_Post0, +5.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(E_St1, E_St2, E_Post1, +5.0, null, +1.0, +1.0, +1.0),
                new(E_St2, E_St3, E_Post2, +8.0, +3.0, +1.0, +1.0, +1.0),
                new(E_St3, E_St4, E_Post3, +10.0, +2.0, +1.0, +1.0, +1.0),
            },
        });
        ecb.Custom.Add(s =>
        {
            var msgs = new List<string>();
            if (s.Run("ECB")!.Rows.Any(r => r.Date == E_St0))
                msgs.Add("the period the ECB just decided is still on the board after the statement");
            // the re-base must be VISIBLE - a silently changed base is worse than none
            if (!s.SheetHtml.Contains("†")) msgs.Add("the re-based fixing carries no dagger in the email");
            if (!s.BlastText.Contains("rebased")) msgs.Add("the blast does not say the fixing is rebased");
            return msgs;
        });
        yield return ecb;
    }
}
