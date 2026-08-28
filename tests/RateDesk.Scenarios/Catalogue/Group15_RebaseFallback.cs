using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>WHAT THE RE-BASE FALLS BACK TO.
///
/// Between a decision and the start of the period it decided, Priced re-bases onto that period's
/// own OIS. There are two ways to get it:
///
///   1. the LIVE mid of `quotes[0]` when that contract is the decided period - which it is on the
///      statement day, because the announced-gate shift puts it there;
///   2. otherwise, that contract's last CLOSE strictly BEFORE the decision day
///      (`PricingServiceBoards.cs:866-877`).
///
/// Path 2 is a PRE-decision price. It cannot contain anything the decision surprised the market
/// with - and the surface still marks the fixing with a dagger and the words "(rebased)", which
/// tell the reader the base is the decided period's own OIS.
///
/// This scenario asks what the desk reads on the morning after a decision that was NOT fully
/// priced, once the feed has re-pointed and path 1 is gone.</summary>
public static class Group15_RebaseFallback
{
    public static IEnumerable<ScenarioSpec> All()
    {
        DateTime D = Cal.Bd(-1);                    // the statement, yesterday
        DateTime S = D.AddDays(6);                  // the period it decided starts in 5 days
        DateTime Sp = D.AddDays(-43);               // the period now running
        DateTime Ap = Sp.AddDays(-6);               // its announcement
        DateTime D1 = D.AddDays(49), M1 = D1.AddDays(6);
        DateTime D2 = D.AddDays(98), M2 = D2.AddDays(6);
        DateTime D3 = D.AddDays(147), M3 = D3.AddDays(6);
        DateTime D4 = D.AddDays(196), M4 = D4.AddDays(6);
        var bounds = new[] { Ap, D, D1, D2, D3, D4 };

        const double fix = 2.230;        // ESTRON, still the pre-hike rate (the period has not started)
        // the market had priced almost nothing: the decided period was worth 2.250 at yesterday's
        // close, and the 25bp hike lifts it to 2.480 - a 23bp surprise
        const double decPre = 2.250, decPost = 2.480;
        const double m1Pre = 2.300, m1Post = 2.530;
        const double m2Pre = 2.330, m2Post = 2.560;
        const double m3Pre = 2.350, m3Post = 2.580;
        const double m4Pre = 2.360, m4Post = 2.590;

        var spec = new ScenarioSpec
        {
            Id = 61,
            Name = "Morning after a SURPRISE hike, feed re-pointed - what is the re-based fixing?",
            Question = "The live decided-period quote is gone because the family renumbered. Does " +
                       "the re-base still find the delivered rate, or the market's guess from " +
                       "before the statement?",
        };

        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { Sp, S, M1, M2, M3, M4 });
        b.DecisionDates.AddRange(new[] { D, D1, D2, D3, D4 });
        b.Fix(fix).FixHist(Cal.D(-70), Cal.PrevBd(D), fix);

        // THE FEED HAS RE-POINTED: the decided period is no longer a numbered rung, so rung 0 is
        // the run-down out to the next undecided meeting and rung 1 is the period after the
        // decided one. This is the state a run finds the morning after a statement.
        b.Quote(0, mid: 2.400, prevClose: 2.300, eff: Sp, mat: M1);
        b.Quote(1, mid: m1Post, prevClose: m1Pre, eff: M1, mat: M2);
        b.Quote(2, mid: m2Post, prevClose: m2Pre, eff: M2, mat: M3);
        b.Quote(3, mid: m3Post, prevClose: m3Pre, eff: M3, mat: M4);

        // history through yesterday's close, contract by contract, stepping on the statement
        var clean = Cal.PrevBd(D);
        b.Contract(Sp, bounds, Cal.D(-70), clean, fix);
        b.ContractStep(S, bounds, Cal.D(-70), D, D, decPre, decPost);
        b.ContractStep(M1, bounds, Cal.D(-70), D, D, m1Pre, m1Post);
        b.ContractStep(M2, bounds, Cal.D(-70), D, D, m2Pre, m2Post);
        b.ContractStep(M3, bounds, Cal.D(-70), D, D, m3Pre, m3Post);
        b.ContractStep(M4, bounds, Cal.D(-70), D, D, m4Pre, m4Post);
        spec.Banks.Add(b);

        // WHAT THE DESK SHOULD READ.
        // The base is the decided period's own OIS, which after a 25bp hike is 2.480. So
        //   M1: (2.530 - 2.480) x 100 = +5.0
        //   M2: (2.560 - 2.480) x 100 = +8.0
        //   M3: (2.580 - 2.480) x 100 = +10.0
        // If the base is instead the PRE-statement close of 2.250, every row reads 23bp higher:
        //   +28.0 / +31.0 / +33.0.
        spec.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = decPost,
            Rebased = true,
            Rows = new List<RowExpect>
            {
                new(M1, M2, m1Post, +5.0, null, Any.Num, Any.Num, Any.Num),
                new(M2, M3, m2Post, +8.0, +3.0, Any.Num, Any.Num, Any.Num),
                new(M3, M4, m3Post, +10.0, +2.0, Any.Num, Any.Num, Any.Num),
            },
        });

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("ECB")!;
            if (run.RefPct is { } rp && Math.Abs(rp - decPre) < 1e-9)
                msgs.Add($"the re-based fixing is {rp:0.000} - the decided period's price at the " +
                         $"close BEFORE the statement, not the {decPost:0.000} it is worth now. " +
                         $"The whole Priced column is {(decPost - decPre) * 100.0:0}bp high, which " +
                         "is exactly the part of the move the market had not priced. The cell " +
                         "still carries the dagger and the blast still says '(rebased)', so the " +
                         "page asserts the base IS the decided period's own OIS.");
            // and the mechanical consequence the desk sees overnight
            if (s.Front("ECB") is { PricedBp: { } p } && Math.Abs(p - 5.0) > 0.05)
                msgs.Add($"the front line reads {p:+0.0;-0.0;0.0}bp priced into the next meeting; " +
                         "against the rate the ECB actually set it is +5.0bp");
            return msgs;
        });
        yield return spec;
    }
}
