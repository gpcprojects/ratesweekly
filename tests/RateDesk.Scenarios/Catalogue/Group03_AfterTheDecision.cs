using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE DAYS AFTER A DECISION - the window nobody watches, where the roll damage from a
/// hike or a cut actually shows up in the published numbers.
///
/// Decision day itself (Group01) is the loud case: the desk is watching, the front rolls, the
/// re-base fires. The quiet danger is the next ten runs. Three separate mechanisms have to keep
/// agreeing after the statement:
///
///   1. THE RE-BASE keeps running until the decided period's OIS actually starts (ECB/BOJ: six
///      more days), but the contract it re-bases onto has LEFT THE BOARD - so it is read out of
///      history, at its last PRE-decision close;
///   2. THE STITCHER has to keep answering "which generic carried this contract on that day"
///      across the announcement, and must source NOTHING from the mixed-state days between an
///      announcement and its period start (the ECB +24.3bp Delta-1m that motivated the rule);
///   3. THE ROLL CORRECTION must fire on the announcement day and NEVER the day after - by then
///      PrevClose is post-roll and correcting on top would double-shift.
///
/// Every scenario below is dated relative to today and every expected number is derived by hand
/// from the synthetic market in the comment above it. Neighbouring contracts are kept 5-15bp
/// apart throughout so a mis-rung read is never within tolerance of the right answer.</summary>
public static class Group03_AfterTheDecision
{
    // ================================================================== 10
    // ECB DELIVERED 50bp YESTERDAY WHEN 25bp WAS PRICED - the period it decided starts in 5 days
    //
    //   Dec0=D(-1) ... today=D(0) ... St0=D(+5) ................ Dec1=D(+48) ... St1=D(+54)
    //   announced      MIXED STATE     the decided period        next decision
    //
    // The feed re-pointed at the announcement (EESF jumped between the 24-Jul and 27-Jul closes
    // around the 23-Jul-26 ECB - DESIGN.md s12), so today rung 1 is ALREADY St1 and the decided
    // period St0 is quoted by nobody. ESTR still prints the OLD 2.000 until St0, so Priced has to
    // re-base onto St0's OWN OIS - "the market print carries the new rate, surprises included"
    // (DESIGN.md s12).
    //
    // THE SURPRISE IS THE POINT. St0 traded 2.250 before the statement (a 25bp hike priced) and
    // 2.480 on the announcement-day 16:15 LONDON SNAP - which the app's own stitcher documents as
    // uniformly OLD-numbered, i.e. an unambiguous mark of exactly this contract on exactly this
    // day. Only the pre-statement close ignores the 23bp the ECB actually delivered.

    private static readonly DateTime E10_PastDec2 = Cal.D(-99), E10_S2 = Cal.D(-93);
    private static readonly DateTime E10_PastDec1 = Cal.D(-50), E10_S1 = Cal.D(-44);
    private static readonly DateTime E10_Dec0 = Cal.D(-1), E10_St0 = Cal.D(5);
    private static readonly DateTime E10_Dec1 = Cal.D(48), E10_St1 = Cal.D(54);
    private static readonly DateTime E10_Dec2 = Cal.D(97), E10_St2 = Cal.D(103);
    private static readonly DateTime E10_Dec3 = Cal.D(146), E10_St3 = Cal.D(152);
    private static readonly DateTime E10_St4 = Cal.D(201);

    // the dates the family RENUMBERS on = the announcements. Past ones are what the loader
    // derives (start - 6); the 14-day cluster drops every period start behind its announcement.
    private static readonly DateTime[] E10_B =
        { E10_PastDec2, E10_PastDec1, E10_Dec0, E10_Dec1, E10_Dec2, E10_Dec3 };

    private const double E10_Fix = 2.000;    // ESTRON - still the PRE-hike print, the hike bites at St0
    private const double E10_St0Lvl = 2.250; // the decided period, flat: the hike was fully priced
    private const double E10_St1Pre = 2.300, E10_St1Post = 2.380, E10_St1Live = 2.410;
    private const double E10_St2Pre = 2.360, E10_St2Post = 2.440, E10_St2Live = 2.460;
    private const double E10_St3Pre = 2.420, E10_St3Post = 2.490, E10_St3Live = 2.500;

    private static BankSpec Ecb10()
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { E10_S2, E10_S1, E10_St0, E10_St1, E10_St2, E10_St3 });
        b.DecisionDates.AddRange(new[] { E10_Dec0, E10_Dec1, E10_Dec2, E10_Dec3 });
        b.Fix(E10_Fix).FixHist(Cal.D(-70), Cal.D(-1), E10_Fix);

        // THE FEED HAS RE-POINTED (the announcement was yesterday): rung 0 runs down to St1 and
        // rung 1 is already the period AFTER the decided one. quotes[0].Effective is in the past,
        // so the re-base cannot use a live mid and must read St0 out of history.
        b.Quote(0, mid: 2.200, prevClose: 2.190, eff: E10_S1, mat: E10_St1);
        b.Quote(1, mid: E10_St1Live, prevClose: E10_St1Post, eff: E10_St1, mat: E10_St2);
        b.Quote(2, mid: E10_St2Live, prevClose: E10_St2Post, eff: E10_St2, mat: E10_St3);
        b.Quote(3, mid: E10_St3Live, prevClose: E10_St3Post, eff: E10_St3, mat: E10_St4);

        // history, contract by contract, to YESTERDAY (the morning run has no snap for today yet)
        b.Contract(E10_S1, E10_B, Cal.D(-70), Cal.D(-1), E10_Fix);
        b.Contract(E10_St0, E10_B, Cal.D(-70), Cal.D(-1), E10_St0Lvl);
        b.ContractStep(E10_St1, E10_B, Cal.D(-70), Cal.D(-1), E10_Dec0, E10_St1Pre, E10_St1Post);
        b.ContractStep(E10_St2, E10_B, Cal.D(-70), Cal.D(-1), E10_Dec0, E10_St2Pre, E10_St2Post);
        b.ContractStep(E10_St3, E10_B, Cal.D(-70), Cal.D(-1), E10_Dec0, E10_St3Pre, E10_St3Post);
        return b;
    }

    // ================================================================== 11
    // FOMC CUT 25bp YESTERDAY - same-day start, so the period is already running
    //
    //   P1=D(-42) ...... D0=D(-1) ...... today ...... D1=D(+41) ...... D2 ...... D3
    //                    decision AND
    //                    period start
    //
    // No re-base is possible (today is not < the period start). EFFR already prints the new rate.
    // Yesterday was the renumber day, so today ticker N's own PX_CLOSE_1D belongs to the SAME
    // contract N points at now: the naive change-on-day is right again and the roll correction
    // must NOT fire (RollCorrectionDue keys on the announcement date == today).

    private static readonly DateTime F11_P2 = Cal.D(-84), F11_P1 = Cal.D(-42), F11_D0 = Cal.D(-1);
    private static readonly DateTime F11_D1 = Cal.D(41), F11_D2 = Cal.D(83);
    private static readonly DateTime F11_D3 = Cal.D(125), F11_D4 = Cal.D(167);
    private static readonly DateTime[] F11_B = { F11_P2, F11_P1, F11_D0, F11_D1, F11_D2, F11_D3, F11_D4 };

    private const double F11_Fix = 3.650;    // EFFR, already the POST-cut print (period started yesterday)
    private const double F11_D0Pre = 3.660, F11_D0Post = 3.650;
    private const double F11_D1Pre = 3.560, F11_D1Post = 3.550, F11_D1Live = 3.520;
    private const double F11_D2Pre = 3.490, F11_D2Post = 3.480, F11_D2Live = 3.460;
    private const double F11_D3Pre = 3.430, F11_D3Post = 3.420, F11_D3Live = 3.410;
    private const double F11_D4Pre = 3.400, F11_D4Post = 3.395;

    private static BankSpec Fomc11()
    {
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { F11_P2, F11_P1, F11_D0, F11_D1, F11_D2, F11_D3, F11_D4 });
        b.DecisionDates.AddRange(new[] { F11_D0, F11_D1, F11_D2, F11_D3, F11_D4 });
        b.Fix(F11_Fix).FixHistStep(Cal.D(-70), Cal.D(-1), F11_D0, 3.900, F11_Fix);

        // the feed re-pointed yesterday: rung 0 is the just-started period, rung 1 the next one.
        // Every PrevClose is that SAME contract's close yesterday - post-roll, as the design says.
        b.Quote(0, mid: F11_D0Post, prevClose: F11_D0Post, eff: F11_D0, mat: F11_D1);
        b.Quote(1, mid: F11_D1Live, prevClose: F11_D1Post, eff: F11_D1, mat: F11_D2);
        b.Quote(2, mid: F11_D2Live, prevClose: F11_D2Post, eff: F11_D2, mat: F11_D3);
        b.Quote(3, mid: F11_D3Live, prevClose: F11_D3Post, eff: F11_D3, mat: F11_D4);

        b.Contract(F11_P1, F11_B, Cal.D(-70), Cal.D(-1), 3.900);
        b.ContractStep(F11_D0, F11_B, Cal.D(-70), Cal.D(-1), F11_D0, F11_D0Pre, F11_D0Post);
        b.ContractStep(F11_D1, F11_B, Cal.D(-70), Cal.D(-1), F11_D0, F11_D1Pre, F11_D1Post);
        b.ContractStep(F11_D2, F11_B, Cal.D(-70), Cal.D(-1), F11_D0, F11_D2Pre, F11_D2Post);
        b.ContractStep(F11_D3, F11_B, Cal.D(-70), Cal.D(-1), F11_D0, F11_D3Pre, F11_D3Post);
        b.ContractStep(F11_D4, F11_B, Cal.D(-70), Cal.D(-1), F11_D0, F11_D4Pre, F11_D4Post);
        return b;
    }

    // ================================================================== 12
    // NORGES CUT 25bp 7 DAYS AGO - the period started 6 days ago and NOWA prints the new rate
    //
    //   Dec0=D(-7) . St0=D(-6) ................ today ................ Dec1=D(+41) . St1=D(+42)
    //   announced    period start (NOWA steps)
    //
    // The re-base MUST be off (today is past the period start), the published fixing is the
    // live o/n print, and Priced is measured against it. Delta 1w targets D(-7) - the
    // announcement day - so its anchor has to be read under the OLD numbering (rung 2 for the
    // front contract), not the new one, or it books the inter-contract gap.

    private static readonly DateTime N12_PastDec2 = Cal.D(-105), N12_S2 = Cal.D(-104);
    private static readonly DateTime N12_PastDec1 = Cal.D(-56), N12_S1 = Cal.D(-55);
    private static readonly DateTime N12_Dec0 = Cal.D(-7), N12_St0 = Cal.D(-6);
    private static readonly DateTime N12_Dec1 = Cal.D(41), N12_St1 = Cal.D(42);
    private static readonly DateTime N12_Dec2 = Cal.D(90), N12_St2 = Cal.D(91);
    private static readonly DateTime N12_Dec3 = Cal.D(139), N12_St3 = Cal.D(140);
    private static readonly DateTime N12_St4 = Cal.D(189);
    private static readonly DateTime[] N12_B =
        { N12_PastDec2, N12_PastDec1, N12_Dec0, N12_Dec1, N12_Dec2, N12_Dec3 };

    private const double N12_FixOld = 4.250, N12_Fix = 4.000;   // NOWA, stepped at the period start
    private const double N12_St0Pre = 4.070, N12_St0Post = 4.010;
    private const double N12_St1Pre = 4.010, N12_St1Post = 3.930, N12_St1Yday = 3.900, N12_St1Live = 3.880;
    private const double N12_St2Pre = 3.930, N12_St2Post = 3.855, N12_St2Yday = 3.815, N12_St2Live = 3.800;
    private const double N12_St3Pre = 3.870, N12_St3Post = 3.800, N12_St3Yday = 3.760, N12_St3Live = 3.750;

    private static BankSpec Norges12()
    {
        var b = new BankSpec { Bank = "NORGES", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { N12_S2, N12_S1, N12_St0, N12_St1, N12_St2, N12_St3 });
        b.DecisionDates.AddRange(new[] { N12_Dec0, N12_Dec1, N12_Dec2, N12_Dec3 });
        b.Fix(N12_Fix).FixHistStep(Cal.D(-70), Cal.D(-1), N12_St0, N12_FixOld, N12_Fix);

        b.Quote(0, mid: N12_Fix, prevClose: N12_Fix, eff: N12_St0, mat: N12_St1);
        b.Quote(1, mid: N12_St1Live, prevClose: N12_St1Yday, eff: N12_St1, mat: N12_St2);
        b.Quote(2, mid: N12_St2Live, prevClose: N12_St2Yday, eff: N12_St2, mat: N12_St3);
        b.Quote(3, mid: N12_St3Live, prevClose: N12_St3Yday, eff: N12_St3, mat: N12_St4);

        b.Contract(N12_S1, N12_B, Cal.D(-70), Cal.D(-1), N12_FixOld);
        b.ContractStep(N12_St0, N12_B, Cal.D(-70), Cal.D(-1), N12_Dec0, N12_St0Pre, N12_St0Post);
        // each forward contract: pre-announcement level, the announcement-day reprice, then a
        // separate mark for yesterday so Delta 1d and Delta 1w cannot coincide by accident
        b.ContractStep(N12_St1, N12_B, Cal.D(-70), Cal.D(-2), N12_Dec0, N12_St1Pre, N12_St1Post);
        b.Contract(N12_St1, N12_B, Cal.D(-1), Cal.D(-1), N12_St1Yday);
        b.ContractStep(N12_St2, N12_B, Cal.D(-70), Cal.D(-2), N12_Dec0, N12_St2Pre, N12_St2Post);
        b.Contract(N12_St2, N12_B, Cal.D(-1), Cal.D(-1), N12_St2Yday);
        b.ContractStep(N12_St3, N12_B, Cal.D(-70), Cal.D(-2), N12_Dec0, N12_St3Pre, N12_St3Post);
        b.Contract(N12_St3, N12_B, Cal.D(-1), Cal.D(-1), N12_St3Yday);
        return b;
    }

    // ================================================================== 13
    // BOJ HIKED 25bp 36 DAYS AGO - the Delta 1m anchor lands INSIDE the old mixed-state window
    //
    //   Dec0=D(-36) . [mixed D(-35)..D(-31)] . St0=D(-30) ......... today ......... Dec1=D(+13)
    //                        ^ 1m target D(-31) lands here
    //
    // 1m = same day last month = D(-31), which is a mixed-state day (announcement -> period
    // start), so no close or snap from it may source an anchor. The walk-back must step past
    // the whole window to the announcement-day SNAP at D(-36) - 5 days, inside ChangeToBp's
    // 10-day cap - and read it under the OLD numbering.

    private static readonly DateTime J13_PastDec2 = Cal.D(-134), J13_S2 = Cal.D(-128);
    private static readonly DateTime J13_PastDec1 = Cal.D(-85), J13_S1 = Cal.D(-79);
    private static readonly DateTime J13_Dec0 = Cal.D(-36), J13_St0 = Cal.D(-30);
    private static readonly DateTime J13_Dec1 = Cal.D(13), J13_St1 = Cal.D(19);
    private static readonly DateTime J13_Dec2 = Cal.D(62), J13_St2 = Cal.D(68);
    private static readonly DateTime J13_Dec3 = Cal.D(111), J13_St3 = Cal.D(117);
    private static readonly DateTime J13_St4 = Cal.D(166);
    private static readonly DateTime[] J13_B =
        { J13_PastDec2, J13_PastDec1, J13_Dec0, J13_Dec1, J13_Dec2, J13_Dec3 };

    private const double J13_FixOld = 0.500, J13_Fix = 0.750;   // MUTKCALM, stepped at the period start
    private const double J13_St1Live = 0.930, J13_St2Live = 1.010, J13_St3Live = 1.060;

    private static BankSpec Boj13()
    {
        var b = new BankSpec { Bank = "BOJ", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { J13_S2, J13_S1, J13_St0, J13_St1, J13_St2, J13_St3 });
        b.DecisionDates.AddRange(new[] { J13_Dec0, J13_Dec1, J13_Dec2, J13_Dec3 });
        b.Fix(J13_Fix).FixHistStep(Cal.D(-70), Cal.D(-1), J13_St0, J13_FixOld, J13_Fix);

        b.Quote(0, mid: J13_Fix, prevClose: J13_Fix, eff: J13_St0, mat: J13_St1);
        b.Quote(1, mid: J13_St1Live, prevClose: 0.925, eff: J13_St1, mat: J13_St2);
        b.Quote(2, mid: J13_St2Live, prevClose: 1.000, eff: J13_St2, mat: J13_St3);
        b.Quote(3, mid: J13_St3Live, prevClose: 1.055, eff: J13_St3, mat: J13_St4);

        // the settled period, only alive up to its own announcement
        b.ContractStep(J13_St0, J13_B, Cal.D(-70), J13_Dec0, J13_Dec0, 0.700, 0.740);
        // the three quoted contracts, in three legs: pre-announcement / announcement day, then
        // the MIXED-STATE days (deliberately off the trend - nothing may source them), then
        // since the period start with a separate mark for yesterday
        b.ContractStep(J13_St1, J13_B, Cal.D(-70), J13_Dec0, J13_Dec0, 0.860, 0.900);
        b.Contract(J13_St1, J13_B, Cal.D(-35), Cal.D(-31), 0.870);
        b.ContractStep(J13_St1, J13_B, Cal.D(-30), Cal.D(-1), Cal.D(-1), 0.915, 0.925);
        b.ContractStep(J13_St2, J13_B, Cal.D(-70), J13_Dec0, J13_Dec0, 0.940, 0.980);
        b.Contract(J13_St2, J13_B, Cal.D(-35), Cal.D(-31), 0.960);
        b.ContractStep(J13_St2, J13_B, Cal.D(-30), Cal.D(-1), Cal.D(-1), 0.995, 1.000);
        b.ContractStep(J13_St3, J13_B, Cal.D(-70), J13_Dec0, J13_Dec0, 1.000, 1.030);
        b.Contract(J13_St3, J13_B, Cal.D(-35), Cal.D(-31), 1.020);
        b.ContractStep(J13_St3, J13_B, Cal.D(-30), Cal.D(-1), Cal.D(-1), 1.050, 1.055);
        return b;
    }

    // ================================================================== 14
    // ECB HIKED 25bp 3 DAYS AGO - the run sits INSIDE the mixed-state window
    //
    //   Dec0=D(-3) ... [mixed D(-2) D(-1) TODAY D(+1) D(+2)] ... St0=D(+3) ...... Dec1=D(+46)
    //
    // Every day since the announcement is per-rung ambiguous, so the mixed days here are seeded
    // with values 5-11bp off the truth - a half-renumbered feed, exactly what the rule exists
    // for. Nothing published may read them: Delta 1d has to step back past yesterday AND the day
    // before to the announcement-day snap. The re-base is still on (today < the period start)
    // and must read the decided period's last PRE-decision close.

    private static readonly DateTime E14_PastDec2 = Cal.D(-101), E14_S2 = Cal.D(-95);
    private static readonly DateTime E14_PastDec1 = Cal.D(-52), E14_S1 = Cal.D(-46);
    private static readonly DateTime E14_Dec0 = Cal.D(-3), E14_St0 = Cal.D(3);
    private static readonly DateTime E14_Dec1 = Cal.D(46), E14_St1 = Cal.D(52);
    private static readonly DateTime E14_Dec2 = Cal.D(95), E14_St2 = Cal.D(101);
    private static readonly DateTime E14_Dec3 = Cal.D(144), E14_St3 = Cal.D(150);
    private static readonly DateTime E14_St4 = Cal.D(199);
    private static readonly DateTime[] E14_B =
        { E14_PastDec2, E14_PastDec1, E14_Dec0, E14_Dec1, E14_Dec2, E14_Dec3 };

    private const double E14_Fix = 2.500;     // ESTRON - still the PRE-hike print
    private const double E14_St0Lvl = 2.740;  // the decided period, fully priced and flat
    private const double E14_St1Pre = 2.720, E14_St1Post = 2.785, E14_St1Live = 2.790;
    private const double E14_St2Pre = 2.780, E14_St2Post = 2.845, E14_St2Live = 2.850;
    private const double E14_St3Pre = 2.825, E14_St3Post = 2.888, E14_St3Live = 2.890;
    // the half-renumbered prints on the mixed days: 8.5, 8.5 and 6.8bp below the truth
    private const double E14_Poison1 = 2.700, E14_Poison2 = 2.760, E14_Poison3 = 2.820;

    private static BankSpec Ecb14()
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { E14_S2, E14_S1, E14_St0, E14_St1, E14_St2, E14_St3 });
        b.DecisionDates.AddRange(new[] { E14_Dec0, E14_Dec1, E14_Dec2, E14_Dec3 });
        b.Fix(E14_Fix).FixHist(Cal.D(-70), Cal.D(-1), E14_Fix);

        // the feed re-pointed at the announcement; every PrevClose is therefore the MIXED-STATE
        // print from yesterday - poisoned by construction, which is why the published Delta 1d
        // must come off the stitched series and not off the raw change-on-day
        b.Quote(0, mid: 2.700, prevClose: 2.700, eff: E14_S1, mat: E14_St1);
        b.Quote(1, mid: E14_St1Live, prevClose: E14_Poison1, eff: E14_St1, mat: E14_St2);
        b.Quote(2, mid: E14_St2Live, prevClose: E14_Poison2, eff: E14_St2, mat: E14_St3);
        b.Quote(3, mid: E14_St3Live, prevClose: E14_Poison3, eff: E14_St3, mat: E14_St4);

        // clean history up to and including the announcement day
        b.Contract(E14_S1, E14_B, Cal.D(-70), E14_Dec0, E14_Fix);
        b.Contract(E14_St0, E14_B, Cal.D(-70), E14_Dec0, E14_St0Lvl);
        b.ContractStep(E14_St1, E14_B, Cal.D(-70), E14_Dec0, E14_Dec0, E14_St1Pre, E14_St1Post);
        b.ContractStep(E14_St2, E14_B, Cal.D(-70), E14_Dec0, E14_Dec0, E14_St2Pre, E14_St2Post);
        b.ContractStep(E14_St3, E14_B, Cal.D(-70), E14_Dec0, E14_Dec0, E14_St3Pre, E14_St3Post);
        // ...then the mixed days, seeded RAW on the rungs a half-renumbered feed would use
        b.Level(1, Cal.D(-2), Cal.D(0), E14_Poison1);
        b.Level(2, Cal.D(-2), Cal.D(0), E14_Poison2);
        b.Level(3, Cal.D(-2), Cal.D(0), E14_Poison3);
        return b;
    }

    // ================================================================== 15
    // TWO FOMC DECISIONS INSIDE THE 1m WINDOW (a scheduled cut, then an inter-meeting cut)
    //
    //   1m target D(-31) . Deca=D(-27) ......... Decb=D(-10) ......... today ......... D1=D(+35)
    //
    // The Delta 1m anchor sits before BOTH announcements, so the contract now on rung 1 sat on
    // rung 3 that day. Getting the count wrong by one books a whole meeting's step; getting it
    // wrong by two books two.

    private static readonly DateTime F15_P2 = Cal.D(-111), F15_P1 = Cal.D(-69);
    private static readonly DateTime F15_Deca = Cal.D(-27), F15_Decb = Cal.D(-10);
    private static readonly DateTime F15_D1 = Cal.D(35), F15_D2 = Cal.D(77);
    private static readonly DateTime F15_D3 = Cal.D(119), F15_D4 = Cal.D(161);
    private static readonly DateTime[] F15_B =
        { F15_P2, F15_P1, F15_Deca, F15_Decb, F15_D1, F15_D2, F15_D3, F15_D4 };

    private const double F15_Fix = 3.650;   // EFFR after two 25bp cuts (4.150 -> 3.900 -> 3.650)
    private const double F15_D1Live = 3.480, F15_D2Live = 3.380, F15_D3Live = 3.320;

    private static BankSpec Fomc15()
    {
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { F15_P2, F15_P1, F15_Deca, F15_Decb, F15_D1, F15_D2, F15_D3, F15_D4 });
        b.DecisionDates.AddRange(new[] { F15_Deca, F15_Decb, F15_D1, F15_D2, F15_D3, F15_D4 });
        b.Fix(F15_Fix);
        b.FixHistStep(Cal.D(-70), Cal.D(-11), F15_Deca, 4.150, 3.900);
        b.FixHist(Cal.D(-10), Cal.D(-1), F15_Fix);

        b.Quote(0, mid: F15_Fix, prevClose: F15_Fix, eff: F15_Decb, mat: F15_D1);
        b.Quote(1, mid: F15_D1Live, prevClose: 3.470, eff: F15_D1, mat: F15_D2);
        b.Quote(2, mid: F15_D2Live, prevClose: 3.375, eff: F15_D2, mat: F15_D3);
        b.Quote(3, mid: F15_D3Live, prevClose: 3.315, eff: F15_D3, mat: F15_D4);

        // the two settled periods, each alive only until its own announcement renumbers it away
        b.ContractStep(F15_Deca, F15_B, Cal.D(-60), F15_Deca, F15_Deca, 3.910, 3.900);
        b.ContractStep(F15_Decb, F15_B, Cal.D(-60), F15_Deca, F15_Deca, 3.790, 3.700);
        b.ContractStep(F15_Decb, F15_B, Cal.D(-26), F15_Decb, F15_Decb, 3.680, 3.650);
        // the three published contracts, one leg per numbering regime
        b.ContractStep(F15_D1, F15_B, Cal.D(-60), F15_Deca, F15_Deca, 3.700, 3.620);
        b.ContractStep(F15_D1, F15_B, Cal.D(-26), F15_Decb, F15_Decb, 3.590, 3.520);
        b.ContractStep(F15_D1, F15_B, Cal.D(-9), Cal.D(-1), Cal.D(-1), 3.500, 3.470);
        b.ContractStep(F15_D2, F15_B, Cal.D(-60), F15_Deca, F15_Deca, 3.620, 3.545);
        b.ContractStep(F15_D2, F15_B, Cal.D(-26), F15_Decb, F15_Decb, 3.510, 3.440);
        b.ContractStep(F15_D2, F15_B, Cal.D(-9), Cal.D(-1), Cal.D(-1), 3.400, 3.375);
        b.ContractStep(F15_D3, F15_B, Cal.D(-60), F15_Deca, F15_Deca, 3.560, 3.490);
        b.ContractStep(F15_D3, F15_B, Cal.D(-26), F15_Decb, F15_Decb, 3.455, 3.390);
        b.ContractStep(F15_D3, F15_B, Cal.D(-9), Cal.D(-1), Cal.D(-1), 3.350, 3.315);
        return b;
    }

    // ================================================================================ scenarios

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 10
        var s10 = new ScenarioSpec
        {
            Id = 10,
            Name = "ECB hiked YESTERDAY, the decided period starts in 5 days (mixed state)",
            Question = "A day after the statement and still five days before the new rate bites: " +
                       "is Priced still re-based onto the decided period's own OIS now that the " +
                       "contract has left the board, and does Delta 1d anchor on the announcement " +
                       "day under the OLD numbering instead of a half-renumbered rung?",
        };
        s10.Banks.Add(Ecb10());
        s10.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            // RE-BASE: today (D0) < the decided period's start (D+5) and the lag is 6d <= 10, so
            // the base is the decided period's own OIS. Its contract is no longer quoted (rung 0
            // now runs to St1 and its Effective is in the past), so the fallback reads rung 1's
            // last close BEFORE the announcement = St0 at 2.250.
            Fixing = E10_St0Lvl, Rebased = true,
            // front row = St1: (2.410-2.250)*100 = +16.0bp; the decision that owns it is Dec1
            Front = new FrontExpect(E10_Dec1, E10_St1, E10_St1Live, E10_St0Lvl, +16.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                // St1  Priced (2.410-2.250)*100 = +16.0   Step -
                //      d1 (2.410-2.380)*100 = +3.0   [anchor = the D(-1) SNAP on rung 2]
                //      w1 (2.410-2.300)*100 = +11.0  [D(-7), rung 2, pre-announcement]
                //      m1 (2.410-2.300)*100 = +11.0  [D(-31), rung 2, pre-announcement]
                new(E10_St1, E10_St2, E10_St1Live, +16.0, null, +3.0, +11.0, +11.0),
                // St2  Priced (2.460-2.250)*100 = +21.0   Step 21.0-16.0 = +5.0
                //      d1 (2.460-2.440)*100 = +2.0   w1/m1 (2.460-2.360)*100 = +10.0
                new(E10_St2, E10_St3, E10_St2Live, +21.0, +5.0, +2.0, +10.0, +10.0),
                // St3  Priced (2.500-2.250)*100 = +25.0   Step 25.0-21.0 = +4.0
                //      d1 (2.500-2.490)*100 = +1.0   w1/m1 (2.500-2.420)*100 = +8.0
                new(E10_St3, E10_St4, E10_St3Live, +25.0, +4.0, +1.0, +8.0, +8.0),
            },
        });
        s10.NotesNotContain.Add("CHECK");
        s10.NotesNotContain.Add("STALE");
        s10.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        s10.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("ECB")!;
            if (run.Rows.Any(r => r.Date == E10_St0))
                msgs.Add("the period the ECB decided yesterday is back on the board");
            // the re-base must stay VISIBLE for the whole announcement-to-start week
            if (!s.SheetHtml.Contains("†")) msgs.Add("the re-based fixing carries no dagger in the email");
            if (!s.BlastText.Contains("rebased")) msgs.Add("the blast does not say the fixing is rebased");
            // the mis-rung read this scenario exists to catch: Delta 1d off rung 1 on the
            // announcement day would read the DECIDED period (2.250), i.e. +16.0bp, not +3.0
            var r0 = run.Rows[0];
            if (r0.D1Bp is { } d1 && Math.Abs(d1 - 16.0) < 0.5)
                msgs.Add($"ECB {E10_St1:dd-MMM-yy} Delta 1d is {d1:+0.0;-0.0} - that is the " +
                         "inter-contract gap to the decided period, not the contract's own move");
            return msgs;
        });
        yield return s10;

        // ---------------------------------------------------------------- 11
        var s11 = new ScenarioSpec
        {
            Id = 11,
            Name = "FOMC cut YESTERDAY (same-day start) - the naive change-on-day is right again",
            Question = "The day after a same-day-start decision, is the roll correction OFF, does " +
                       "Delta 1d show each contract's own move rather than the gap to its " +
                       "neighbour, and is Priced measured against the un-re-based EFFR print?",
        };
        s11.Banks.Add(Fomc11());
        s11.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            // NO RE-BASE: the decided period started yesterday, so today is not < its start.
            // EFFR already prints the post-cut 3.650 and Priced is measured straight against it.
            Fixing = F11_Fix, Rebased = true,
            Front = new FrontExpect(F11_D1, F11_D1, F11_D1Live, F11_Fix, -13.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                // D1  Priced (3.520-3.650)*100 = -13.0  Step -
                //     d1 (3.520-3.550)*100 = -3.0  [D(-1) snap, rung 2 = OLD numbering]
                //     w1 (3.520-3.560)*100 = -4.0  m1 same anchor level = -4.0
                new(F11_D1, F11_D2, F11_D1Live, -13.0, null, -3.0, -4.0, -4.0),
                // D2  Priced (3.460-3.650)*100 = -19.0  Step -19.0-(-13.0) = -6.0
                //     d1 (3.460-3.480)*100 = -2.0   w1/m1 (3.460-3.490)*100 = -3.0
                new(F11_D2, F11_D3, F11_D2Live, -19.0, -6.0, -2.0, -3.0, -3.0),
                // D3  Priced (3.410-3.650)*100 = -24.0  Step -24.0-(-19.0) = -5.0
                //     d1 (3.410-3.420)*100 = -1.0   w1/m1 (3.410-3.430)*100 = -2.0
                new(F11_D3, F11_D4, F11_D3Live, -24.0, -5.0, -1.0, -2.0, -2.0),
            },
        });
        s11.NotesNotContain.Add("CHECK");
        s11.NotesNotContain.Add("STALE");
        s11.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Runs["FOMC"];
            // the raw change-on-day must be the NAIVE one today: mid(N) - PrevClose(N), because
            // the family renumbered yesterday. Applying the roll correction on top would give
            // mid(N) - PrevClose(N+1) = the inter-contract gap.
            var want = new[] { -3.0, -2.0, -1.0 };
            var gap = new[] { +4.0, +4.0, double.NaN };   // (3.520-3.480)*100, (3.460-3.420)*100
            for (int i = 0; i < Math.Min(want.Length, run.Rows.Count); i++)
            {
                if (run.Rows[i].CoDBp is not { } cod)
                { msgs.Add($"FOMC row {i + 1}: change-on-day is blank, expected {want[i]:+0.0;-0.0}bp"); continue; }
                if (Math.Abs(cod - want[i]) > 0.05)
                    msgs.Add($"FOMC row {i + 1}: change-on-day {cod:+0.0;-0.0}bp != the contract's own " +
                             $"move {want[i]:+0.0;-0.0}bp" +
                             (double.IsFinite(gap[i]) && Math.Abs(cod - gap[i]) < 0.05
                                 ? " - that is the gap to the NEXT contract, the roll correction fired a day late"
                                 : ""));
            }
            if (run.Rows.Any(r => r.Date == F11_D0))
                msgs.Add("the period the FOMC decided yesterday is back on the board");
            // FIXED 2026-08-27: EFFR publishes a day in arrears, so the morning after a cut the
            // printed fixing is still the pre-cut rate and the base must be the decided period's
            // own OIS. A same-day-start family gets the same treatment as every other.
            if (s.Front("FOMC")?.RefRebased != true)
                msgs.Add("the morning after a Fed cut, Priced is measured against a fixing that " +
                         "still prints the pre-cut rate - the re-base did not fire");
            return msgs;
        });
        yield return s11;

        // ---------------------------------------------------------------- 12
        var s12 = new ScenarioSpec
        {
            Id = 12,
            Name = "NORGES cut 7 days ago - period running, NOWA prints the new rate",
            Question = "Once the decided period has started, is the re-base off, is Priced " +
                       "measured against the new o/n print, and does Delta 1w - which straddles " +
                       "the announcement - compare the same contract on both sides?",
        };
        s12.Banks.Add(Norges12());
        s12.Expect.Add(new BankExpect
        {
            Bank = "NORGES",
            // RE-BASE OFF: the decided period started at D(-6), so today is not < its start.
            Fixing = N12_Fix, Rebased = false,
            Front = new FrontExpect(N12_Dec1, N12_St1, N12_St1Live, N12_Fix, -12.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // St1  Priced (3.880-4.000)*100 = -12.0   Step -
                //      d1 (3.880-3.900)*100 = -2.0   [D(-1), rung 1]
                //      w1 (3.880-3.930)*100 = -5.0   [D(-7) SNAP on rung 2 - the OLD numbering;
                //                                     rung 1 that day held the DECIDED period at
                //                                     4.010, which would print -13.0]
                //      m1 (3.880-4.010)*100 = -13.0  [D(-31), rung 2, pre-announcement]
                new(N12_St1, N12_St2, N12_St1Live, -12.0, null, -2.0, -5.0, -13.0),
                // St2  Priced (3.800-4.000)*100 = -20.0   Step -20.0-(-12.0) = -8.0
                //      d1 (3.800-3.815)*100 = -1.5   w1 (3.800-3.855)*100 = -5.5
                //      m1 (3.800-3.930)*100 = -13.0
                new(N12_St2, N12_St3, N12_St2Live, -20.0, -8.0, -1.5, -5.5, -13.0),
                // St3  Priced (3.750-4.000)*100 = -25.0   Step -25.0-(-20.0) = -5.0
                //      d1 (3.750-3.760)*100 = -1.0   w1 (3.750-3.800)*100 = -5.0
                //      m1 (3.750-3.870)*100 = -12.0
                new(N12_St3, N12_St4, N12_St3Live, -25.0, -5.0, -1.0, -5.0, -12.0),
            },
        });
        s12.NotesNotContain.Add("CHECK");
        s12.NotesNotContain.Add("STALE");
        s12.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("NORGES")!;
            if (run.RefPct is { } fx && Math.Abs(fx - N12_FixOld) < 1e-9)
                msgs.Add("the published fixing is still the PRE-decision o/n rate");
            // the mis-rung Delta 1w this scenario exists to catch
            if (run.Rows[0].W1Bp is { } w1 && Math.Abs(w1 + 13.0) < 0.5)
                msgs.Add($"NORGES {N12_St1:dd-MMM-yy} Delta 1w is {w1:+0.0;-0.0} - that is the gap to " +
                         "the decided period, i.e. the announcement-day anchor was read under the " +
                         "NEW numbering");
            return msgs;
        });
        yield return s12;

        // ---------------------------------------------------------------- 13
        var s13 = new ScenarioSpec
        {
            Id = 13,
            Name = "BOJ hiked 36 days ago - the 1m anchor lands inside the old mixed-state window",
            Question = "When same-day-last-month falls between an announcement and its period " +
                       "start, does the walk-back skip the whole ambiguous window, land on the " +
                       "announcement-day snap under the old numbering, and survive ChangeToBp's " +
                       "10-day cap rather than blanking the cell?",
        };
        s13.Banks.Add(Boj13());
        s13.Expect.Add(new BankExpect
        {
            Bank = "BOJ",
            // RE-BASE OFF: the decided period started 30 days ago.
            Fixing = J13_Fix, Rebased = false,
            Front = new FrontExpect(J13_Dec1, J13_St1, J13_St1Live, J13_Fix, +18.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // St1  Priced (0.930-0.750)*100 = +18.0   Step -
                //      d1 (0.930-0.925)*100 = +0.5   [D(-1), rung 1]
                //      w1 (0.930-0.915)*100 = +1.5   [D(-7), rung 1]
                //      m1 target D(-31) is MIXED -> walk back over D(-32)..D(-35) (mixed/weekend)
                //         to the D(-36) announcement snap on rung 2 = 0.900, 5 days < the 10-day
                //         cap:  (0.930-0.900)*100 = +3.0
                //         [reading the mixed day itself would give (0.930-0.870)*100 = +6.0;
                //          reading rung 1 on D(-36) would give (0.930-0.740)*100 = +19.0]
                new(J13_St1, J13_St2, J13_St1Live, +18.0, null, +0.5, +1.5, +6.0),
                // St2  Priced (1.010-0.750)*100 = +26.0   Step 26.0-18.0 = +8.0
                //      d1 (1.010-1.000)*100 = +1.0   w1 (1.010-0.995)*100 = +1.5
                //      m1 (1.010-0.980)*100 = +3.0
                new(J13_St2, J13_St3, J13_St2Live, +26.0, +8.0, +1.0, +1.5, +5.0),
                // St3  Priced (1.060-0.750)*100 = +31.0   Step 31.0-26.0 = +5.0
                //      d1 (1.060-1.055)*100 = +0.5   w1 (1.060-1.050)*100 = +1.0
                //      m1 (1.060-1.030)*100 = +3.0
                new(J13_St3, J13_St4, J13_St3Live, +31.0, +5.0, +0.5, +1.0, +4.0),
            },
        });
        s13.NotesNotContain.Add("CHECK");
        s13.NotesNotContain.Add("STALE");
        s13.Custom.Add(s =>
        {
            var msgs = new List<string>();
            foreach (var row in s.Run("BOJ")!.Rows)
                if (row.M1Bp is null)
                    msgs.Add($"BOJ {row.Date:dd-MMM-yy}: Delta 1m is blank - the anchor is 5 days " +
                             "behind the target, well inside ChangeToBp's 10-day cap");
            return msgs;
        });
        yield return s13;

        // ---------------------------------------------------------------- 14
        var s14 = new ScenarioSpec
        {
            Id = 14,
            Name = "ECB hiked 3 days ago - the run sits INSIDE the mixed-state window",
            Question = "With every day since the announcement per-rung ambiguous and seeded with " +
                       "visibly wrong prints, does any published change read them - and does the " +
                       "re-base still hold three days in?",
        };
        s14.Banks.Add(Ecb14());
        s14.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            // RE-BASE ON: today (D0) < the decided period's start (D+3), lag 6d <= 10. The
            // fallback reads rung 1's last close strictly BEFORE the announcement - the mixed
            // days are all after it - so the base is St0 at 2.740.
            Fixing = E14_St0Lvl, Rebased = true,
            Front = new FrontExpect(E14_Dec1, E14_St1, E14_St1Live, E14_St0Lvl, +5.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                // St1  Priced (2.790-2.740)*100 = +5.0   Step -
                //      d1 target D(-1) is MIXED, as is D(-2); the walk-back lands on the D(-3)
                //         announcement snap (rung 2, old numbering) = 2.785, 2 days < the 5-day
                //         cap:  (2.790-2.785)*100 = +0.5
                //         [the poisoned rung-1 print would give (2.790-2.700)*100 = +9.0]
                //      w1 (2.790-2.720)*100 = +7.0   m1 (2.790-2.720)*100 = +7.0
                new(E14_St1, E14_St2, E14_St1Live, +5.0, null, +0.5, +7.0, +7.0),
                // St2  Priced (2.850-2.740)*100 = +11.0   Step 11.0-5.0 = +6.0
                //      d1 (2.850-2.845)*100 = +0.5   [poison would give +9.0]
                //      w1/m1 (2.850-2.780)*100 = +7.0
                new(E14_St2, E14_St3, E14_St2Live, +11.0, +6.0, +0.5, +7.0, +7.0),
                // St3  Priced (2.890-2.740)*100 = +15.0   Step 15.0-11.0 = +4.0
                //      d1 (2.890-2.888)*100 = +0.2   [poison would give +7.0]
                //      w1/m1 (2.890-2.825)*100 = +6.5
                new(E14_St3, E14_St4, E14_St3Live, +15.0, +4.0, +0.2, +6.5, +6.5),
            },
        });
        s14.NotesNotContain.Add("CHECK");
        s14.NotesNotContain.Add("STALE");
        s14.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("ECB")!;
            // no published change anywhere may be a mixed-state read
            var poison = new[] { E14_Poison1, E14_Poison2, E14_Poison3 };
            var live = new[] { E14_St1Live, E14_St2Live, E14_St3Live };
            for (int i = 0; i < Math.Min(3, run.Rows.Count); i++)
            {
                double bad = (live[i] - poison[i]) * 100.0;
                foreach (var (label, v) in new (string, double?)[]
                         { ("1d", run.Rows[i].D1Bp), ("1w", run.Rows[i].W1Bp), ("1m", run.Rows[i].M1Bp) })
                    if (v is { } x && Math.Abs(x - bad) < 0.05)
                        msgs.Add($"ECB {run.Rows[i].Date:dd-MMM-yy} Delta {label} is {x:+0.0;-0.0}bp - " +
                                 "that is a mixed-state print, which may source nothing");
            }
            if (run.Rows.Any(r => r.Date == E14_St0))
                msgs.Add("the period the ECB decided three days ago is back on the board");
            if (!s.SheetHtml.Contains("†")) msgs.Add("the re-based fixing carries no dagger in the email");
            return msgs;
        });
        yield return s14;

        // ---------------------------------------------------------------- 15
        var s15 = new ScenarioSpec
        {
            Id = 15,
            Name = "TWO FOMC decisions inside the 1m window - the anchor crosses two renumbers",
            Question = "With a scheduled cut 27 days ago and an inter-meeting cut 10 days ago, " +
                       "does the 1m lookback still compare the SAME contract, two renumber " +
                       "boundaries back, rather than booking one or two whole meeting steps?",
        };
        s15.Banks.Add(Fomc15());
        s15.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = F15_Fix, Rebased = false,
            Front = new FrontExpect(F15_D1, F15_D1, F15_D1Live, F15_Fix, -17.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // D1  Priced (3.480-3.650)*100 = -17.0   Step -
                //     d1 (3.480-3.470)*100 = +1.0   [D(-1), rung 1]
                //     w1 (3.480-3.500)*100 = -2.0   [D(-7), rung 1]
                //     m1 target D(-31): two boundaries back, so RUNG 3 = 3.700
                //        (3.480-3.700)*100 = -22.0
                //        [rung 1 that day held the D(-27) period at 3.910 -> -43.0;
                //         rung 2 held the D(-10) period at 3.790 -> -31.0]
                new(F15_D1, F15_D2, F15_D1Live, -17.0, null, +1.0, -2.0, -22.0),
                // D2  Priced (3.380-3.650)*100 = -27.0   Step -27.0-(-17.0) = -10.0
                //     d1 (3.380-3.375)*100 = +0.5   w1 (3.380-3.400)*100 = -2.0
                //     m1 rung 4 = 3.620:  (3.380-3.620)*100 = -24.0
                new(F15_D2, F15_D3, F15_D2Live, -27.0, -10.0, +0.5, -2.0, -24.0),
                // D3  Priced (3.320-3.650)*100 = -33.0   Step -33.0-(-27.0) = -6.0
                //     d1 (3.320-3.315)*100 = +0.5   w1 (3.320-3.350)*100 = -3.0
                //     m1 rung 5 = 3.560:  (3.320-3.560)*100 = -24.0
                new(F15_D3, F15_D4, F15_D3Live, -33.0, -6.0, +0.5, -3.0, -24.0),
            },
        });
        s15.NotesNotContain.Add("CHECK");
        s15.NotesNotContain.Add("STALE");
        s15.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var r0 = s.Run("FOMC")!.Rows[0];
            if (r0.M1Bp is { } m1)
            {
                if (Math.Abs(m1 + 43.0) < 0.5)
                    msgs.Add($"FOMC {r0.Date:dd-MMM-yy} Delta 1m is {m1:+0.0;-0.0} - a single-ticker read, " +
                             "two renumber boundaries un-corrected");
                else if (Math.Abs(m1 + 31.0) < 0.5)
                    msgs.Add($"FOMC {r0.Date:dd-MMM-yy} Delta 1m is {m1:+0.0;-0.0} - one renumber boundary " +
                             "was corrected, the other was not");
            }
            return msgs;
        });
        yield return s15;
    }
}
