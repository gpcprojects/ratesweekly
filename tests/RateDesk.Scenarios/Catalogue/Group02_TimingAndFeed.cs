using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>DECISION-DAY TIMING AND FEED STATE - the six states a run can actually be in on the
/// day a bank moves, and what each one must publish.
///
/// The board's front row is decided by TWO independent things that only agree on a good day:
///   · the CALENDAR CLOCK  - meetings.json's decisionTimeLondon vs DecisionClock.LondonNow()
///     (PricingServiceBoards.cs, the gateShift loop) - which rolls the decided period off the
///     board whether or not Bloomberg has caught up;
///   · the FEED - each rung's own SW_EFF_DT / MATURITY, which re-points at the announcement but
///     NON-uniformly through the day.
/// Every scenario here pins one combination of the two and derives, by hand, what the desk must
/// read afterwards:
///
///     id  clock                     feed              must happen
///     --  ------------------------  ----------------  -------------------------------------
///      4  before the announcement   old numbering     nothing: no roll, no re-base
///      5  after                     ALREADY re-pointed gate self-disarms, CoD correction fires
///      6  no decisionTimeLondon     old numbering     honest degradation - no roll, no re-base
///      7  after (RBA, 1-day lag)    old numbering     roll AND re-base
///      8  after (BOJ, 6-day lag)    old numbering     roll AND re-base, non-contiguous family
///      9  after, calendar behind    old numbering     gate shifts TWICE; exhausted run warns
///
/// EVERY market below is built the same way: neighbouring meeting contracts sit 15bp apart, so a
/// rung read one place off produces a 15bp error nobody could mistake for rounding, and each
/// contract's own move on the day is small (1-5bp). Right and wrong can never coincide.</summary>
public static class Group02_TimingAndFeed
{
    // =================================================================== shared geometry
    //
    // ECB SHAPE (6-day lag): announcement, then the maintenance period starts 6 days later.
    // Past starts stay in "dates"; the loader migrates them to pastDates and MeetingCalendar
    // derives their announcements as start-6, which is why the boundary list below starts at
    // D(-98) and D(-56) and not at the starts themselves.
    private static readonly DateTime E_S2 = Cal.D(-92), E_S1 = Cal.D(-50);
    private static readonly DateTime E_Dec0 = Cal.D(0), E_St0 = Cal.D(6);
    private static readonly DateTime E_Dec1 = Cal.D(49), E_St1 = Cal.D(55);
    private static readonly DateTime E_Dec2 = Cal.D(98), E_St2 = Cal.D(104);
    private static readonly DateTime E_Dec3 = Cal.D(147), E_St3 = Cal.D(153);
    private static readonly DateTime E_Dec4 = Cal.D(196), E_St4 = Cal.D(202);
    private static readonly DateTime E_St5 = Cal.D(251);
    /// <summary>The dates the ECB family RENUMBERS on = its announcements (past ones derived as
    /// start-6). 14-day clustering keeps the announcement and drops the start six days later.</summary>
    private static readonly DateTime[] E_Bounds =
        { Cal.D(-98), Cal.D(-56), E_Dec0, E_Dec1, E_Dec2, E_Dec3, E_Dec4 };

    // FOMC / MPC SHAPE (same-day start): the period the decision governs begins on the decision
    // date itself, so the announced-but-not-yet-effective re-base can never apply.
    private static readonly DateTime F_P2 = Cal.D(-84), F_P1 = Cal.D(-42);
    private static readonly DateTime F_D0 = Cal.D(0), F_D1 = Cal.D(42), F_D2 = Cal.D(84);
    private static readonly DateTime F_D3 = Cal.D(126), F_D4 = Cal.D(168), F_D5 = Cal.D(210);
    private static readonly DateTime F_D6 = Cal.D(252);
    private static readonly DateTime[] F_Bounds = { F_P2, F_P1, F_D0, F_D1, F_D2, F_D3, F_D4, F_D5 };

    // ============================================================================ 4
    // ------------------------------------------------------- ECB, decision later TODAY

    private const double S4_E_Fix = 2.000;                 // ESTRON - the hike has not landed
    // the four quoted meeting periods, 15bp apart, quiet all month
    private const double S4_E0 = 2.240, S4_E1 = 2.390, S4_E2 = 2.540, S4_E3 = 2.690;

    private static BankSpec S4_Ecb()
    {
        // decisionTimeLondon 23:50 London: the statement is still hours away
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimeNotYetPassed };
        b.Dates.AddRange(new[] { E_S2, E_S1, E_St0, E_St1, E_St2, E_St3 });
        b.DecisionDates.AddRange(new[] { E_Dec0, E_Dec1, E_Dec2, E_Dec3 });
        b.Fix(S4_E_Fix).FixHist(Cal.D(-70), Cal.D(-1), S4_E_Fix);

        // feed in its ordinary pre-decision state: rung 1 quotes the period the ECB is about to
        // decide, and every rung's own SW_EFF_DT agrees with the previous rung's maturity
        b.Quote(0, mid: S4_E_Fix, prevClose: S4_E_Fix, eff: E_S1, mat: E_St0);
        b.Quote(1, mid: S4_E0, prevClose: S4_E0, eff: E_St0, mat: E_St1);
        b.Quote(2, mid: S4_E1, prevClose: S4_E1, eff: E_St1, mat: E_St2);
        b.Quote(3, mid: S4_E2, prevClose: S4_E2, eff: E_St2, mat: E_St3);
        b.Quote(4, mid: S4_E3, prevClose: S4_E3, eff: E_St3, mat: E_St4);

        // a QUIET tape: every contract's rate is constant, its ticker NUMBER is not
        b.Contract(E_S1, E_Bounds, Cal.D(-70), Cal.D(-1), S4_E_Fix);
        b.Contract(E_St0, E_Bounds, Cal.D(-70), Cal.D(-1), S4_E0);
        b.Contract(E_St1, E_Bounds, Cal.D(-70), Cal.D(-1), S4_E1);
        b.Contract(E_St2, E_Bounds, Cal.D(-70), Cal.D(-1), S4_E2);
        b.Contract(E_St3, E_Bounds, Cal.D(-70), Cal.D(-1), S4_E3);
        return b;
    }

    // ------------------------------------------------------- MPC, decision later TODAY

    private const double S4_M_Fix = 3.900;                 // SONIA - unchanged, the cut is pending
    private const double S4_M0 = 3.680, S4_M1 = 3.530, S4_M2 = 3.380, S4_M3 = 3.230, S4_M4 = 3.080;

    private static BankSpec S4_Mpc()
    {
        var b = new BankSpec { Bank = "MPC", DecisionTimeLondon = Cal.TimeNotYetPassed };
        b.Dates.AddRange(new[] { F_P2, F_P1, F_D0, F_D1, F_D2, F_D3, F_D4 });
        b.DecisionDates.AddRange(new[] { F_D0, F_D1, F_D2, F_D3, F_D4 });
        b.Fix(S4_M_Fix).FixHist(Cal.D(-70), Cal.D(-1), S4_M_Fix);

        b.Quote(0, mid: S4_M_Fix, prevClose: S4_M_Fix, eff: F_P1, mat: F_D0);
        b.Quote(1, mid: S4_M0, prevClose: S4_M0, eff: F_D0, mat: F_D1);
        b.Quote(2, mid: S4_M1, prevClose: S4_M1, eff: F_D1, mat: F_D2);
        b.Quote(3, mid: S4_M2, prevClose: S4_M2, eff: F_D2, mat: F_D3);
        b.Quote(4, mid: S4_M3, prevClose: S4_M3, eff: F_D3, mat: F_D4);

        b.Contract(F_P1, F_Bounds, Cal.D(-70), Cal.D(-1), S4_M_Fix);
        b.Contract(F_D0, F_Bounds, Cal.D(-70), Cal.D(-1), S4_M0);
        b.Contract(F_D1, F_Bounds, Cal.D(-70), Cal.D(-1), S4_M1);
        b.Contract(F_D2, F_Bounds, Cal.D(-70), Cal.D(-1), S4_M2);
        b.Contract(F_D3, F_Bounds, Cal.D(-70), Cal.D(-1), S4_M3);
        b.Contract(F_D4, F_Bounds, Cal.D(-70), Cal.D(-1), S4_M4);
        return b;
    }

    // ============================================================================ 5
    // ------------------------------------------- FOMC cut TODAY, feed ALREADY re-pointed

    private const double S5_F_Fix = 3.900;                 // EFFR still prints the pre-cut rate
    // contract levels, PRE close / POST the statement. The cut was nearly fully priced, so each
    // CONTRACT moved only -5bp; consecutive contracts are 15bp apart.
    private const double S5_F0p = 3.700, S5_F0 = 3.650;    // the just-decided period D0..D1
    private const double S5_F1p = 3.550, S5_F1 = 3.500;
    private const double S5_F2p = 3.400, S5_F2 = 3.350;
    private const double S5_F3p = 3.250, S5_F3 = 3.200;
    private const double S5_F4p = 3.100, S5_F4 = 3.050;

    private static BankSpec S5_Fomc()
    {
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { F_P2, F_P1, F_D0, F_D1, F_D2, F_D3, F_D4, F_D5 });
        b.DecisionDates.AddRange(new[] { F_D0, F_D1, F_D2, F_D3, F_D4, F_D5 });
        b.Fix(S5_F_Fix).FixHist(Cal.D(-70), Cal.D(-1), S5_F_Fix);

        // THE FEED HAS RE-POINTED: rung 0 is now the just-decided period, rung 1 the NEXT one.
        // PrevClose on rung n is therefore yesterday's close of the contract now at rung n-1.
        b.Quote(0, mid: S5_F0, prevClose: S5_F_Fix, eff: F_D0, mat: F_D1);
        b.Quote(1, mid: S5_F1, prevClose: S5_F0p, eff: F_D1, mat: F_D2);
        b.Quote(2, mid: S5_F2, prevClose: S5_F1p, eff: F_D2, mat: F_D3);
        b.Quote(3, mid: S5_F3, prevClose: S5_F2p, eff: F_D3, mat: F_D4);
        b.Quote(4, mid: S5_F4, prevClose: S5_F3p, eff: F_D4, mat: F_D5);
        // rung 5 prices nothing (the family ends), but it carries dates and yesterday's close -
        // which is exactly what the roll-day CoD correction needs for the LAST published row
        b.Quote(5, mid: null, prevClose: S5_F4p, eff: F_D5, mat: F_D6);

        b.Contract(F_P1, F_Bounds, Cal.D(-70), Cal.D(-1), S5_F_Fix);
        b.Contract(F_D0, F_Bounds, Cal.D(-70), Cal.D(-1), S5_F0p);
        b.Contract(F_D1, F_Bounds, Cal.D(-70), Cal.D(-1), S5_F1p);
        b.Contract(F_D2, F_Bounds, Cal.D(-70), Cal.D(-1), S5_F2p);
        b.Contract(F_D3, F_Bounds, Cal.D(-70), Cal.D(-1), S5_F3p);
        b.Contract(F_D4, F_Bounds, Cal.D(-70), Cal.D(-1), S5_F4p);
        return b;
    }

    // ------------------------------------------- ECB hike TODAY, feed ALREADY re-pointed

    private const double S5_E_Fix = 2.000;                 // ESTRON - pre-hike for six more days
    private const double S5_E0p = 2.230, S5_E0 = 2.250;    // the just-decided period St0..St1
    private const double S5_E1p = 2.380, S5_E1 = 2.400;
    private const double S5_E2p = 2.530, S5_E2 = 2.550;
    private const double S5_E3p = 2.680, S5_E3 = 2.700;
    private const double S5_E4p = 2.830, S5_E4 = 2.850;

    private static BankSpec S5_Ecb()
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { E_S2, E_S1, E_St0, E_St1, E_St2, E_St3, E_St4 });
        b.DecisionDates.AddRange(new[] { E_Dec0, E_Dec1, E_Dec2, E_Dec3, E_Dec4 });
        b.Fix(S5_E_Fix).FixHist(Cal.D(-70), Cal.D(-1), S5_E_Fix);

        // re-pointed: rung 0 = the just-decided (not yet effective) period, rung 1 = the next
        b.Quote(0, mid: S5_E0, prevClose: S5_E_Fix, eff: E_St0, mat: E_St1);
        b.Quote(1, mid: S5_E1, prevClose: S5_E0p, eff: E_St1, mat: E_St2);
        b.Quote(2, mid: S5_E2, prevClose: S5_E1p, eff: E_St2, mat: E_St3);
        b.Quote(3, mid: S5_E3, prevClose: S5_E2p, eff: E_St3, mat: E_St4);
        b.Quote(4, mid: S5_E4, prevClose: S5_E3p, eff: E_St4, mat: E_St5);
        b.Quote(5, mid: null, prevClose: S5_E4p, eff: E_St5, mat: Cal.D(300));

        b.Contract(E_S1, E_Bounds, Cal.D(-70), Cal.D(-1), S5_E_Fix);
        b.Contract(E_St0, E_Bounds, Cal.D(-70), Cal.D(-1), S5_E0p);
        b.Contract(E_St1, E_Bounds, Cal.D(-70), Cal.D(-1), S5_E1p);
        b.Contract(E_St2, E_Bounds, Cal.D(-70), Cal.D(-1), S5_E2p);
        b.Contract(E_St3, E_Bounds, Cal.D(-70), Cal.D(-1), S5_E3p);
        b.Contract(E_St4, E_Bounds, Cal.D(-70), Cal.D(-1), S5_E4p);
        return b;
    }

    // ============================================================================ 6
    // ------------------------------------ ECB hike TODAY, NO decisionTimeLondon on file

    private const double S6_Fix = 2.000;                   // ESTRON, pre-hike
    private const double S6_E0p = 2.240, S6_E0 = 2.250;    // the period the ECB just decided
    private const double S6_E1p = 2.390, S6_E1 = 2.400;
    private const double S6_E2p = 2.540, S6_E2 = 2.550;
    private const double S6_E3p = 2.690, S6_E3 = 2.700;

    private static BankSpec S6_Ecb()
    {
        // "" - the config carries the decision date but not the announcement time
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = "" };
        b.Dates.AddRange(new[] { E_S2, E_S1, E_St0, E_St1, E_St2, E_St3 });
        b.DecisionDates.AddRange(new[] { E_Dec0, E_Dec1, E_Dec2, E_Dec3 });
        b.Fix(S6_Fix).FixHist(Cal.D(-70), Cal.D(-1), S6_Fix);

        // the feed has not re-pointed - the state the app cannot distinguish from "no statement
        // yet", which is precisely why the clock gate exists
        b.Quote(0, mid: S6_Fix, prevClose: S6_Fix, eff: E_S1, mat: E_St0);
        b.Quote(1, mid: S6_E0, prevClose: S6_E0p, eff: E_St0, mat: E_St1);
        b.Quote(2, mid: S6_E1, prevClose: S6_E1p, eff: E_St1, mat: E_St2);
        b.Quote(3, mid: S6_E2, prevClose: S6_E2p, eff: E_St2, mat: E_St3);
        b.Quote(4, mid: S6_E3, prevClose: S6_E3p, eff: E_St3, mat: E_St4);

        b.Contract(E_S1, E_Bounds, Cal.D(-70), Cal.D(-1), S6_Fix);
        b.Contract(E_St0, E_Bounds, Cal.D(-70), Cal.D(-1), S6_E0p);
        b.Contract(E_St1, E_Bounds, Cal.D(-70), Cal.D(-1), S6_E1p);
        b.Contract(E_St2, E_Bounds, Cal.D(-70), Cal.D(-1), S6_E2p);
        b.Contract(E_St3, E_Bounds, Cal.D(-70), Cal.D(-1), S6_E3p);
        return b;
    }

    // ============================================================================ 7
    // ------------------------------------------------- RBA (1-day lag), after the statement

    private static readonly DateTime R_S2 = Cal.D(-84), R_S1 = Cal.D(-42);
    private static readonly DateTime R_Dec0 = Cal.D(0), R_St0 = Cal.D(1);
    private static readonly DateTime R_Dec1 = Cal.D(42), R_St1 = Cal.D(43);
    private static readonly DateTime R_Dec2 = Cal.D(84), R_St2 = Cal.D(85);
    private static readonly DateTime R_Dec3 = Cal.D(126), R_St3 = Cal.D(127);
    private static readonly DateTime R_Dec4 = Cal.D(168), R_St4 = Cal.D(169);
    private static readonly DateTime R_St5 = Cal.D(211);
    // announcements; past ones derived as start-1, and the 14-day cluster keeps the
    // announcement rather than the period start one day later
    private static readonly DateTime[] R_Bounds =
        { Cal.D(-85), Cal.D(-43), R_Dec0, R_Dec1, R_Dec2, R_Dec3, R_Dec4 };

    private const double S7_Fix = 3.850;                   // RBACOR - today still the old cash rate
    private const double S7_C0p = 3.620, S7_C0 = 3.600;    // the just-decided period, starts tomorrow
    private const double S7_C1p = 3.470, S7_C1 = 3.450;
    private const double S7_C2p = 3.320, S7_C2 = 3.300;
    private const double S7_C3p = 3.170, S7_C3 = 3.150;
    private const double S7_C4p = 3.020, S7_C4 = 3.000;

    private static BankSpec S7_Rba()
    {
        // the shipped contributor (NABZ) is kept, and every quote/close is seeded on BOTH
        // spellings - the production state, not the split-source case
        var b = new BankSpec { Bank = "RBA", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { R_S2, R_S1, R_St0, R_St1, R_St2, R_St3, R_St4 });
        b.DecisionDates.AddRange(new[] { R_Dec0, R_Dec1, R_Dec2, R_Dec3, R_Dec4 });
        b.Fix(S7_Fix).FixHist(Cal.D(-70), Cal.D(-1), S7_Fix);

        b.Quote(0, mid: S7_Fix, prevClose: S7_Fix, eff: R_S1, mat: R_St0);
        b.Quote(1, mid: S7_C0, prevClose: S7_C0p, eff: R_St0, mat: R_St1);
        b.Quote(2, mid: S7_C1, prevClose: S7_C1p, eff: R_St1, mat: R_St2);
        b.Quote(3, mid: S7_C2, prevClose: S7_C2p, eff: R_St2, mat: R_St3);
        b.Quote(4, mid: S7_C3, prevClose: S7_C3p, eff: R_St3, mat: R_St4);
        b.Quote(5, mid: S7_C4, prevClose: S7_C4p, eff: R_St4, mat: R_St5);

        b.Contract(R_S1, R_Bounds, Cal.D(-70), Cal.D(-1), S7_Fix);
        b.Contract(R_St0, R_Bounds, Cal.D(-70), Cal.D(-1), S7_C0p);
        b.Contract(R_St1, R_Bounds, Cal.D(-70), Cal.D(-1), S7_C1p);
        b.Contract(R_St2, R_Bounds, Cal.D(-70), Cal.D(-1), S7_C2p);
        b.Contract(R_St3, R_Bounds, Cal.D(-70), Cal.D(-1), S7_C3p);
        b.Contract(R_St4, R_Bounds, Cal.D(-70), Cal.D(-1), S7_C4p);
        return b;
    }

    // ============================================================================ 8
    // -------------------------------- BOJ (6-day lag, NON-CONTIGUOUS family), after the statement

    private static readonly DateTime J_S2 = Cal.D(-92), J_S1 = Cal.D(-50);
    private static readonly DateTime J_Dec0 = Cal.D(0), J_St0 = Cal.D(6);
    private static readonly DateTime J_Dec1 = Cal.D(49), J_St1 = Cal.D(55);
    private static readonly DateTime J_Dec2 = Cal.D(98), J_St2 = Cal.D(104);
    private static readonly DateTime J_Dec3 = Cal.D(147), J_St3 = Cal.D(153);
    private static readonly DateTime J_Dec4 = Cal.D(196), J_St4 = Cal.D(202);
    private static readonly DateTime J_Dec5 = Cal.D(245), J_St5 = Cal.D(251);
    private static readonly DateTime[] J_Bounds =
        { Cal.D(-98), Cal.D(-56), J_Dec0, J_Dec1, J_Dec2, J_Dec3, J_Dec4, J_Dec5 };

    private const double S8_Fix = 0.480;                   // MUTKCALM - pre-hike TONA
    private const double S8_C0p = 0.720, S8_C0 = 0.730;    // the just-decided period, starts in 6d
    private const double S8_C1p = 0.870, S8_C1 = 0.880;
    private const double S8_C2p = 1.020, S8_C2 = 1.030;
    private const double S8_C3p = 1.170, S8_C3 = 1.180;
    private const double S8_C4p = 1.320, S8_C4 = 1.330;

    private static BankSpec S8_Boj()
    {
        var b = new BankSpec { Bank = "BOJ", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { J_S2, J_S1, J_St0, J_St1, J_St2, J_St3, J_St4, J_St5 });
        b.DecisionDates.AddRange(new[] { J_Dec0, J_Dec1, J_Dec2, J_Dec3, J_Dec4, J_Dec5 });
        b.Fix(S8_Fix).FixHist(Cal.D(-70), Cal.D(-1), S8_Fix);

        // THE BOJ FAMILY IS NOT CONTIGUOUS (the live 30-Oct/02-Nov case in ResolveMeetingDates):
        // rung N MATURES on the next DECISION date and its own SW_EFF_DT is the settlement date
        // six days after the previous one. So maturity(N) != effective(N+1), and the run's dates
        // must come from each rung's own SW_EFF_DT or every row names a decision, not a period.
        b.Quote(0, mid: S8_Fix, prevClose: S8_Fix, eff: J_S1, mat: J_Dec0);
        b.Quote(1, mid: S8_C0, prevClose: S8_C0p, eff: J_St0, mat: J_Dec1);
        b.Quote(2, mid: S8_C1, prevClose: S8_C1p, eff: J_St1, mat: J_Dec2);
        b.Quote(3, mid: S8_C2, prevClose: S8_C2p, eff: J_St2, mat: J_Dec3);
        b.Quote(4, mid: S8_C3, prevClose: S8_C3p, eff: J_St3, mat: J_Dec4);
        b.Quote(5, mid: S8_C4, prevClose: S8_C4p, eff: J_St4, mat: J_Dec5);

        b.Contract(J_S1, J_Bounds, Cal.D(-70), Cal.D(-1), S8_Fix);
        b.Contract(J_St0, J_Bounds, Cal.D(-70), Cal.D(-1), S8_C0p);
        b.Contract(J_St1, J_Bounds, Cal.D(-70), Cal.D(-1), S8_C1p);
        b.Contract(J_St2, J_Bounds, Cal.D(-70), Cal.D(-1), S8_C2p);
        b.Contract(J_St3, J_Bounds, Cal.D(-70), Cal.D(-1), S8_C3p);
        b.Contract(J_St4, J_Bounds, Cal.D(-70), Cal.D(-1), S8_C4p);
        return b;
    }

    // ============================================================================ 9
    // ------------------------------------ ECB: TWO announced decisions, feed never re-pointed

    private static readonly DateTime N_S0 = Cal.D(-59);              // the period before last
    private static readonly DateTime N_DecA = Cal.D(-17), N_StA = Cal.D(-11);  // decided, RUNNING
    private static readonly DateTime N_DecB = Cal.D(0), N_StB = Cal.D(6);      // decided TODAY
    private static readonly DateTime N_Dec1 = Cal.D(49), N_St1 = Cal.D(55);
    private static readonly DateTime N_Dec2 = Cal.D(98), N_St2 = Cal.D(104);
    private static readonly DateTime N_Dec3 = Cal.D(147), N_St3 = Cal.D(153);
    private static readonly DateTime N_Dec4 = Cal.D(196), N_St4 = Cal.D(202);
    private static readonly DateTime N_St5 = Cal.D(251);
    private static readonly DateTime[] N_Bounds =
        { Cal.D(-65), N_DecA, N_DecB, N_Dec1, N_Dec2, N_Dec3, N_Dec4 };

    private const double S9_Fix = 2.150;    // ESTRON - already carries the FIRST hike
    private const double S9_Cur = 2.150;    // the period running since D(-11)
    private const double S9_B0p = 2.390, S9_B0 = 2.400;   // decided TODAY, effective in 6 days
    private const double S9_B1p = 2.540, S9_B1 = 2.550;
    private const double S9_B2p = 2.690, S9_B2 = 2.700;
    private const double S9_B3p = 2.840, S9_B3 = 2.850;
    private const double S9_B4p = 2.990, S9_B4 = 3.000;

    private static BankSpec S9_Ecb()
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { N_S0, N_StA, N_StB, N_St1, N_St2, N_St3, N_St4 });
        // BOTH the fortnight-old decision and today's are still on file: the config lists future
        // decisions and a decision STAYS in the list after it happens. 17 days is the closest two
        // meetings of one bank can sit under the 14-day cluster invariant.
        b.DecisionDates.AddRange(new[] { N_DecA, N_DecB, N_Dec1, N_Dec2, N_Dec3, N_Dec4 });
        b.Fix(S9_Fix).FixHist(Cal.D(-70), Cal.D(-1), S9_Fix);

        // THE FEED NEVER RE-POINTED at the D(-17) announcement either: rung 1 still quotes the
        // period that STARTED 11 days ago, rung 2 the one decided this morning.
        b.Quote(0, mid: S9_Cur, prevClose: S9_Cur, eff: N_S0, mat: N_StA);
        b.Quote(1, mid: S9_Cur, prevClose: S9_Cur, eff: N_StA, mat: N_StB);
        b.Quote(2, mid: S9_B0, prevClose: S9_B0p, eff: N_StB, mat: N_St1);
        b.Quote(3, mid: S9_B1, prevClose: S9_B1p, eff: N_St1, mat: N_St2);
        b.Quote(4, mid: S9_B2, prevClose: S9_B2p, eff: N_St2, mat: N_St3);
        b.Quote(5, mid: S9_B3, prevClose: S9_B3p, eff: N_St3, mat: N_St4);
        b.Quote(6, mid: S9_B4, prevClose: S9_B4p, eff: N_St4, mat: N_St5);

        b.Contract(N_S0, N_Bounds, Cal.D(-70), Cal.D(-1), 2.000);
        b.Contract(N_StA, N_Bounds, Cal.D(-70), Cal.D(-1), S9_Cur);
        b.Contract(N_StB, N_Bounds, Cal.D(-70), Cal.D(-1), S9_B0p);
        b.Contract(N_St1, N_Bounds, Cal.D(-70), Cal.D(-1), S9_B1p);
        b.Contract(N_St2, N_Bounds, Cal.D(-70), Cal.D(-1), S9_B2p);
        b.Contract(N_St3, N_Bounds, Cal.D(-70), Cal.D(-1), S9_B3p);
        b.Contract(N_St4, N_Bounds, Cal.D(-70), Cal.D(-1), S9_B4p);
        return b;
    }

    // ------------------------------------------------ NORGES: the calendar has run out entirely

    private static BankSpec S9_Norges()
    {
        var b = new BankSpec { Bank = "NORGES", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { Cal.D(-84), Cal.D(-42), Cal.D(1) });
        b.DecisionDates.Add(Cal.D(0));                 // today's, and nothing after it
        b.Fix(3.900);
        // one quoted rung only: the run-down maturing at tomorrow's period start. Once today's
        // decision is announced there is no undecided meeting left anywhere in the run.
        b.Quote(0, mid: 3.900, prevClose: 3.900, eff: Cal.D(-42), mat: Cal.D(1));
        return b;
    }

    // ================================================================================ specs

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 4
        var pre = new ScenarioSpec
        {
            Id = 4,
            Name = "Decision TODAY but BEFORE the announcement time",
            Question = "Hours before the statement, does the board still show the period the bank " +
                       "is about to decide, with today's decision date on the front line, no roll " +
                       "and no re-base?",
        };
        pre.Banks.Add(S4_Ecb());
        pre.Banks.Add(S4_Mpc());
        pre.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            // NOTHING has happened: DecisionClock.Announced(today, "23:50", now) is false, so the
            // gate never shifts and the announced-but-not-yet-effective re-base never arms.
            // Priced is the honest market-vs-fixing number it always is before a decision.
            Fixing = S4_E_Fix,
            Rebased = false,
            //                     decision  start   mid     fixing     priced
            // (2.240 - 2.000) x 100 = +24.0
            Front = new FrontExpect(E_Dec0, E_St0, S4_E0, S4_E_Fix, +24.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                //   start   end     mid     priced  step    d1    w1    m1
                // (2.240-2.000)x100 = +24.0 ; quiet contract => every change 0.0
                new(E_St0, E_St1, S4_E0, +24.0, null, 0.0, 0.0, 0.0),
                // (2.390-2.000)x100 = +39.0 ; step +39.0 - +24.0 = +15.0
                new(E_St1, E_St2, S4_E1, +39.0, +15.0, 0.0, 0.0, 0.0),
                // (2.540-2.000)x100 = +54.0 ; step +15.0
                new(E_St2, E_St3, S4_E2, +54.0, +15.0, 0.0, 0.0, 0.0),
                // (2.690-2.000)x100 = +69.0 ; step +15.0
                new(E_St3, E_St4, S4_E3, +69.0, +15.0, 0.0, 0.0, 0.0),
            },
        });
        pre.Expect.Add(new BankExpect
        {
            Bank = "MPC",
            // same-day start: the period the MPC will decide at noon BEGINS today, so it is the
            // front row and its own start date is today
            Fixing = S4_M_Fix,
            Rebased = false,
            // (3.680 - 3.900) x 100 = -22.0
            Front = new FrontExpect(F_D0, F_D0, S4_M0, S4_M_Fix, -22.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(F_D0, F_D1, S4_M0, -22.0, null, 0.0, 0.0, 0.0),
                // (3.530-3.900)x100 = -37.0 ; step -37.0 - -22.0 = -15.0
                new(F_D1, F_D2, S4_M1, -37.0, -15.0, 0.0, 0.0, 0.0),
                // (3.380-3.900)x100 = -52.0 ; step -15.0
                new(F_D2, F_D3, S4_M2, -52.0, -15.0, 0.0, 0.0, 0.0),
                // (3.230-3.900)x100 = -67.0 ; step -15.0
                new(F_D3, F_D4, S4_M3, -67.0, -15.0, 0.0, 0.0, 0.0),
            },
        });
        pre.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // NOTHING may pretend the statement has landed
            foreach (var bank in new[] { "ECB", "MPC" })
                if (s.Run(bank)!.RefRebased)
                    msgs.Add($"{bank}: the fixing is flagged re-based before the announcement time");
            if (s.SheetHtml.Contains("†"))
                msgs.Add("the sheet email carries a re-based dagger before any announcement");
            if (s.BlastText.Contains("rebased"))
                msgs.Add("the blast says the fixing is rebased before any announcement");
            // and the periods under decision must STILL be on the board
            if (!s.Run("ECB")!.Rows.Any(r => r.Date == E_St0))
                msgs.Add("the period the ECB has not yet decided has already left the board");
            if (!s.Run("MPC")!.Rows.Any(r => r.Date == F_D0))
                msgs.Add("the period the MPC has not yet decided has already left the board");
            return msgs;
        });
        pre.NotesNotContain.Add("CHECK");
        pre.NotesNotContain.Add("STALE");
        pre.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        yield return pre;

        // ---------------------------------------------------------------- 5
        var repointed = new ScenarioSpec
        {
            Id = 5,
            Name = "Decision TODAY, statement out, feed ALREADY re-pointed",
            Question = "When Bloomberg has already renumbered, does the time gate stand down " +
                       "instead of rolling a second time, and does change-on-day difference each " +
                       "mid against the close of the SAME contract rather than its own stale one?",
        };
        repointed.Banks.Add(S5_Fomc());
        repointed.Banks.Add(S5_Ecb());
        repointed.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            // same-day start => no re-base is possible; EFFR prints the pre-cut rate all day
            Fixing = 3.650,
            Rebased = true,
            // gate: meetDates[1] is D1, whose own decision is D1 and is NOT announced => shift 0
            // (3.500 - 3.900) x 100 = -40.0
            Front = new FrontExpect(F_D1, F_D1, S5_F1, 3.650, -15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //   start   end     mid     priced  step     d1     w1     m1
                // Priced (3.500-3.900)x100 = -40.0 ; each CONTRACT moved -5bp on the day, and the
                // stitched series is contract-constant, so d1 = w1 = m1 = -5.0 on every row
                new(F_D1, F_D2, S5_F1, -15.0, null, -5.0, -5.0, -5.0),
                // (3.350-3.900)x100 = -55.0 ; step -55.0 - -40.0 = -15.0
                new(F_D2, F_D3, S5_F2, -30.0, -15.0, -5.0, -5.0, -5.0),
                // (3.200-3.900)x100 = -70.0 ; step -15.0
                new(F_D3, F_D4, S5_F3, -45.0, -15.0, -5.0, -5.0, -5.0),
                // (3.050-3.900)x100 = -85.0 ; step -15.0
                new(F_D4, F_D5, S5_F4, -60.0, -15.0, -5.0, -5.0, -5.0),
            },
        });
        repointed.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            // lagged start: the decided period is not effective for six days, so Priced re-bases
            // onto that period's OWN OIS - which the re-pointed feed now serves as rung 0
            Fixing = S5_E0,
            Rebased = true,
            // (2.400 - 2.250) x 100 = +15.0
            Front = new FrontExpect(E_Dec1, E_St1, S5_E1, 2.250, +15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                // Priced (2.400-2.250)x100 = +15.0 ; every contract +2bp on the day
                new(E_St1, E_St2, S5_E1, +15.0, null, +2.0, +2.0, +2.0),
                // (2.550-2.250)x100 = +30.0 ; step +15.0
                new(E_St2, E_St3, S5_E2, +30.0, +15.0, +2.0, +2.0, +2.0),
                // (2.700-2.250)x100 = +45.0 ; step +15.0
                new(E_St3, E_St4, S5_E3, +45.0, +15.0, +2.0, +2.0, +2.0),
                // (2.850-2.250)x100 = +60.0 ; step +15.0
                new(E_St4, E_St5, S5_E4, +60.0, +15.0, +2.0, +2.0, +2.0),
            },
        });
        repointed.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // THE POINT OF THIS SCENARIO. Today's ticker N carries YESTERDAY's close of the
            // contract that sat at N-1, so change-on-day must difference mid(N) against
            // PrevClose(N+1). Naive (mid(N) - PrevClose(N)) books the 15bp inter-contract gap on
            // top of the real move: FOMC would print -20.0 instead of -5.0, ECB +17.0 instead of
            // +2.0 - a wrong SIGN on the ECB row.
            var fomc = s.Runs["FOMC"];
            for (int i = 0; i < fomc.Rows.Count; i++)
                if (fomc.Rows[i].CoDBp is not { } c || Math.Abs(c - -5.0) > 0.05)
                    msgs.Add($"FOMC {fomc.Rows[i].Date:dd-MMM-yy}: change-on-day is " +
                             $"{(fomc.Rows[i].CoDBp?.ToString("+0.0;-0.0;0.0") ?? "blank")}, expected -5.0 " +
                             "(the roll-day correction did not fire; naive would give -20.0)");
            var ecb = s.Runs["ECB"];
            for (int i = 0; i < ecb.Rows.Count; i++)
                if (ecb.Rows[i].CoDBp is not { } c || Math.Abs(c - +2.0) > 0.05)
                    msgs.Add($"ECB {ecb.Rows[i].Date:dd-MMM-yy}: change-on-day is " +
                             $"{(ecb.Rows[i].CoDBp?.ToString("+0.0;-0.0;0.0") ?? "blank")}, expected +2.0 " +
                             "(the roll-day correction did not fire; naive would give +17.0)");
            // no double roll: the period AFTER the front must still be on the board
            if (!s.Run("FOMC")!.Rows.Any(r => r.Date == F_D1))
                msgs.Add("FOMC: the gate rolled a second time - the next undecided period is gone");
            if (!s.Run("ECB")!.Rows.Any(r => r.Date == E_St1))
                msgs.Add("ECB: the gate rolled a second time - the next undecided period is gone");
            // ...and the just-decided periods must not be
            if (s.Run("FOMC")!.Rows.Any(r => r.Date == F_D0))
                msgs.Add("FOMC: the just-decided period is still on the board");
            if (s.Run("ECB")!.Rows.Any(r => r.Date == E_St0))
                msgs.Add("ECB: the just-decided period is still on the board");
            return msgs;
        });
        repointed.NotesNotContain.Add("CHECK");
        repointed.NotesNotContain.Add("STALE");
        yield return repointed;

        // ---------------------------------------------------------------- 6
        var noTime = new ScenarioSpec
        {
            Id = 6,
            Name = "Decision TODAY, NO decisionTimeLondon on file",
            Question = "With the announcement time missing the intraday state is unknowable - " +
                       "does the run degrade honestly (no roll, no re-base), and does it TELL " +
                       "anyone that the decision day is running ungated?",
        };
        noTime.Banks.Add(S6_Ecb());
        noTime.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            // DecisionClock.Announced returns false all day when TimeSpan.TryParse("") fails, so
            // both the gate and the re-base stay disarmed and the deciding period keeps the front
            // until tomorrow. DESIGN.md section 12: "No decisionTimeLondon on file = honest
            // degradation to the old next-morning roll".
            Fixing = S6_Fix,
            Rebased = false,
            // (2.250 - 2.000) x 100 = +25.0 - the whole hike the ECB has ALREADY delivered,
            // printed as if it were still to come. That is the cost of the missing time.
            Front = new FrontExpect(E_Dec0, E_St0, S6_E0, S6_Fix, +25.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                //   start   end     mid     priced  step    d1    w1    m1
                // (2.250-2.000)x100 = +25.0 ; contract +1bp on the day
                new(E_St0, E_St1, S6_E0, +25.0, null, +1.0, +1.0, +1.0),
                // (2.400-2.000)x100 = +40.0 ; step +15.0
                new(E_St1, E_St2, S6_E1, +40.0, +15.0, +1.0, +1.0, +1.0),
                // (2.550-2.000)x100 = +55.0 ; step +15.0
                new(E_St2, E_St3, S6_E2, +55.0, +15.0, +1.0, +1.0, +1.0),
                // (2.700-2.000)x100 = +70.0 ; step +15.0
                new(E_St3, E_St4, S6_E3, +70.0, +15.0, +1.0, +1.0, +1.0),
            },
        });
        noTime.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // nothing may PRETEND the announcement happened
            if (s.Run("ECB")!.RefRebased)
                msgs.Add("the fixing is flagged re-based although the announcement time is unknown");
            if (s.BlastText.Contains("rebased"))
                msgs.Add("the blast claims a rebased fixing with no announcement time on file");
            if (!s.Run("ECB")!.Rows.Any(r => r.Date == E_St0))
                msgs.Add("the deciding period left the board although the run cannot know the " +
                         "statement is out");
            // ...but the desk must be TOLD. DESIGN.md section 12 promises "CalendarHealth warns"
            // for a run with decisions and no decisionTimeLondon; CalendarHealth.Check runs only
            // in UpdateEngine (the weekly data refresh), never in the daily build, so the front
            // line above ships a delivered hike as "+25.0 priced in" with no flag anywhere.
            if (!s.Notes.Any(n => n.Contains("decisionTimeLondon", StringComparison.OrdinalIgnoreCase)
                                  || n.Contains("announcement time", StringComparison.OrdinalIgnoreCase)))
                msgs.Add("DECISION DAY IS RUNNING UNGATED AND NOTHING SAYS SO: the calendar puts a " +
                         "decision on today's date, the run has no decisionTimeLondon, the front " +
                         "line therefore prints +25.0 'priced' for a hike already delivered - and " +
                         "not one note on any generated surface mentions it. Notes were: " +
                         (s.Notes.Count == 0 ? "(none)" : string.Join(" || ", s.Notes)));
            return msgs;
        });
        yield return noTime;

        // ---------------------------------------------------------------- 7
        var rba = new ScenarioSpec
        {
            Id = 7,
            Name = "RBA cuts 25bp TODAY, period starts TOMORROW (1-day lag)",
            Question = "On the shortest lag there is, do BOTH mechanisms fire in the same run - " +
                       "the decided period off the board, and Priced re-based onto its own OIS " +
                       "instead of a cash rate that is still yesterday's?",
        };
        rba.Banks.Add(S7_Rba());
        rba.Expect.Add(new BankExpect
        {
            Bank = "RBA",
            // the gate shifts by one (the D+1 period's decision is announced), which also puts the
            // just-decided period's own OIS at quotes[0] - the rung the re-base reads
            Fixing = S7_C0,
            Rebased = true,
            // (3.450 - 3.600) x 100 = -15.0
            Front = new FrontExpect(R_Dec1, R_St1, S7_C1, S7_C0, -15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //   start   end     mid     priced  step     d1     w1     m1
                // Priced (3.450-3.600)x100 = -15.0 ; each contract -2bp on the day
                new(R_St1, R_St2, S7_C1, -15.0, null, -2.0, -2.0, -2.0),
                // (3.300-3.600)x100 = -30.0 ; step -15.0
                new(R_St2, R_St3, S7_C2, -30.0, -15.0, -2.0, -2.0, -2.0),
                // (3.150-3.600)x100 = -45.0 ; step -15.0
                new(R_St3, R_St4, S7_C3, -45.0, -15.0, -2.0, -2.0, -2.0),
                // (3.000-3.600)x100 = -60.0 ; step -15.0
                new(R_St4, R_St5, S7_C4, -60.0, -15.0, -2.0, -2.0, -2.0),
            },
        });
        rba.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("RBA")!;
            if (run.Rows.Any(r => r.Date == R_St0))
                msgs.Add("the period the RBA just decided is still on the board after the statement");
            // the re-base must be VISIBLE - a swap mid printed silently under "RBA cash" would be
            // read as the policy rate itself
            if (!s.SheetHtml.Contains("†")) msgs.Add("the re-based fixing carries no dagger in the email");
            if (!s.BlastText.Contains("rebased")) msgs.Add("the blast does not say the fixing is rebased");
            // and it must NOT be the stale cash rate: Priced off 3.850 would read -40.0, not -15.0
            if (run.RefPct is { } rp && Math.Abs(rp - S7_Fix) < 1e-9)
                msgs.Add("Priced is still measured against the pre-cut RBACOR fixing");
            return msgs;
        });
        rba.NotesNotContain.Add("CHECK");
        rba.NotesNotContain.Add("STALE");
        yield return rba;

        // ---------------------------------------------------------------- 8
        var boj = new ScenarioSpec
        {
            Id = 8,
            Name = "BOJ hikes 25bp TODAY, period starts in 6 days (non-contiguous family)",
            Question = "On the one family whose rungs do NOT mature where the next one starts, " +
                       "do the rows still name PERIODS rather than decisions once the roll and " +
                       "the re-base have both fired?",
        };
        boj.Banks.Add(S8_Boj());
        boj.Expect.Add(new BankExpect
        {
            Bank = "BOJ",
            Fixing = S8_C0,
            Rebased = true,
            // (0.880 - 0.730) x 100 = +15.0
            Front = new FrontExpect(J_Dec1, J_St1, S8_C1, S8_C0, +15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //   start   end     mid     priced  step     d1     w1     m1
                // every START is the rung's own SW_EFF_DT, every END the NEXT rung's - never the
                // maturity, which on this family names the decision six days earlier
                // Priced (0.880-0.730)x100 = +15.0 ; each contract +1bp on the day
                new(J_St1, J_St2, S8_C1, +15.0, null, +1.0, +1.0, +1.0),
                // (1.030-0.730)x100 = +30.0 ; step +15.0
                new(J_St2, J_St3, S8_C2, +30.0, +15.0, +1.0, +1.0, +1.0),
                // (1.180-0.730)x100 = +45.0 ; step +15.0
                new(J_St3, J_St4, S8_C3, +45.0, +15.0, +1.0, +1.0, +1.0),
                // (1.330-0.730)x100 = +60.0 ; step +15.0 ; the last END comes from the config
                // grid (the lagged-family fill) because rung 6 quotes nothing
                new(J_St4, J_St5, S8_C4, +60.0, +15.0, +1.0, +1.0, +1.0),
            },
        });
        boj.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("BOJ")!;
            if (run.Rows.Any(r => r.Date == J_St0))
                msgs.Add("the period the BOJ just decided is still on the board after the statement");
            // a row labelled with a DECISION date is the fault class ResolveMeetingDates' SW_EFF_DT
            // preference exists to prevent - it would mis-date every row by six days
            foreach (var dec in new[] { J_Dec0, J_Dec1, J_Dec2, J_Dec3, J_Dec4, J_Dec5 })
                if (run.Rows.Any(r => r.Date == dec))
                    msgs.Add($"a published row starts on the DECISION date {dec:dd-MMM-yy}, not on " +
                             "the period the rate applies over");
            if (!s.SheetHtml.Contains("†")) msgs.Add("the re-based fixing carries no dagger in the email");
            if (run.RefPct is { } rp && Math.Abs(rp - S8_Fix) < 1e-9)
                msgs.Add("Priced is still measured against the pre-hike TONA fixing");
            return msgs;
        });
        boj.NotesNotContain.Add("CHECK");
        boj.NotesNotContain.Add("STALE");
        yield return boj;

        // ---------------------------------------------------------------- 9
        var behind = new ScenarioSpec
        {
            Id = 9,
            Name = "Two announced decisions on the board at once, and a run out of meetings",
            Question = "When a stuck feed leaves BOTH a running period and a just-decided one on " +
                       "the front, does the gate shift twice rather than once - and when every " +
                       "resolved meeting is decided, does the run say so instead of publishing?",
        };
        behind.Banks.Add(S9_Ecb());
        behind.Banks.Add(S9_Norges());
        behind.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            // gate: meetDates[1] = D(-11) pairs with the D(-17) decision (announced) => shift 1;
            // meetDates[2] = D(6) pairs with today's (announced) => shift 2; meetDates[3] = D(55)
            // pairs with D(49), which is not => stop. quotes[0] is then the period decided today.
            Fixing = S9_B0,
            Rebased = true,
            // (2.550 - 2.400) x 100 = +15.0
            Front = new FrontExpect(N_Dec1, N_St1, S9_B1, S9_B0, +15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //   start   end     mid     priced  step     d1     w1     m1
                // Priced (2.550-2.400)x100 = +15.0 ; each contract +1bp on the day.
                // NOTE the m1 anchor sits at D(-31), which is BEFORE the D(-17) announcement, so
                // it is read one rung further out - the stitcher's whole reason for existing.
                new(N_St1, N_St2, S9_B1, +15.0, null, +1.0, +1.0, +1.0),
                // (2.700-2.400)x100 = +30.0 ; step +15.0
                new(N_St2, N_St3, S9_B2, +30.0, +15.0, +1.0, +1.0, +1.0),
                // (2.850-2.400)x100 = +45.0 ; step +15.0
                new(N_St3, N_St4, S9_B3, +45.0, +15.0, +1.0, +1.0, +1.0),
                // (3.000-2.400)x100 = +60.0 ; step +15.0
                new(N_St4, N_St5, S9_B4, +60.0, +15.0, +1.0, +1.0, +1.0),
            },
        });
        behind.Expect.Add(new BankExpect
        {
            Bank = "NORGES",
            // every resolved meeting is already decided => the run publishes nothing at all
            NoRun = true,
        });
        behind.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("ECB")!;
            // a SINGLE shift would leave the just-decided D(6) period on the front at 2.400 with
            // Priced measured off the running period - 15bp of pure roll error on every row
            if (run.Rows.Any(r => r.Date == N_StB))
                msgs.Add("the gate shifted only once: the period decided this morning is still the front");
            if (run.Rows.Any(r => r.Date == N_StA))
                msgs.Add("a period that STARTED eleven days ago is still on the board");
            // the exhausted run must be absent from every surface AND flagged
            if (s.Front("NORGES") != null)
                msgs.Add("NORGES has no publishable meeting but still has a front-table line");
            if (Render.Blast(s.BlastText).ContainsKey("NORGES"))
                msgs.Add("NORGES has no publishable meeting but still has a blast block");
            return msgs;
        });
        // the documented warning for an exhausted calendar, plus the completeness gate that stops
        // a nine-bank table mailing as eight without anyone noticing
        behind.NotesContain.Add("every resolved meeting is already decided");
        behind.NotesContain.Add("NORGES produced NO rows");
        yield return behind;
    }
}
