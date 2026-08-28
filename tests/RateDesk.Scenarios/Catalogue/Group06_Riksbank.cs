using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE RIKSBANK — the run that breaks two rules the other eight obey.
///
/// 1. PERIOD-START RENUMBERING (meetings.json rollsAtPeriodStart, SKSF probed 2026-08-25).
///    Every other family re-points its generics at the ANNOUNCEMENT; SKSF re-points when the
///    swap period actually STARTS, six days later. Two consequences the desk reads directly:
///      · on the ANNOUNCEMENT day the time-gated roll still drops the decided period off the
///        board (DESIGN §12, the live 20-Aug-26 case), but the tickers have NOT renumbered, so
///        the roll-day change-on-day correction must stay OFF (scenario 28);
///      · on the PERIOD-START day nothing is announced, but the tickers HAVE renumbered, so the
///        correction must fire (scenario 29). Getting this backwards is what put +65.7bp of
///        phantom Δ1d on a SEK row in August 2026.
///
/// 2. THE YEAR-END TURN (markTurnPeriods). SWESTR collapses on the last business day of the
///    year, so any SKSF period straddling the year end prints far below the policy path. Those
///    rows are LABELLED "Y/E Turn" and publish no numbers, and the step chain skips them so the
///    row after carries the CUMULATIVE move across the masked meeting (scenarios 30, 31, 33).
///
/// 3. trustConfigDates — SKSF5A/6A quote a real price with no SW_EFF_DT / MATURITY, and the desk
///    has confirmed the config grid may date those rows (scenario 32).
///
/// CALENDAR NOTE. The harness forbids hard-coded dates, but a Y/E turn needs a period that
/// straddles a 31-Dec. <see cref="ToYe"/> measures today's distance to this year's 31-Dec, so
/// the turn scenarios stay anchored on <c>DateTime.Today</c> and still land a period in December
/// whenever the suite runs between roughly July and early October. Run far outside that window
/// and 30/31/33 stop being turn scenarios — market.txt makes that obvious immediately.</summary>
public static class Group06_Riksbank
{
    /// <summary>Calendar days from today to 31-Dec of the current year. Anchored on today, so no
    /// literal date appears anywhere below.</summary>
    private static int ToYe => (int)(new DateTime(Cal.Today.Year, 12, 31).Date - Cal.Today).TotalDays;

    // ================================================================= 28
    // SEK DECISION TODAY, PERIOD STARTS IN 6 DAYS.
    //
    //   PS2 ....... PS1 ....|today = DEC0|...... S0 ...... S1n ...... S2n ...... S3n ...... S4n
    //   settled     settled  announcement        the decided period starts here
    //
    // The gate rolls the decided period [S0,S1n) off the board the moment 00:05 London passes.
    // The FEED has not renumbered — SKSF renumbers at S0, six days from now — so ticker N's own
    // PX_CLOSE_1D still belongs to the SAME contract and the naive CoD is right.

    private static readonly DateTime A_PS3 = Cal.D(-126), A_PS2 = Cal.D(-84), A_PS1 = Cal.D(-42);
    private static readonly DateTime A_Dec0 = Cal.D(0), A_S0 = Cal.D(6);
    private static readonly DateTime A_Dec1 = Cal.D(42), A_S1n = Cal.D(48);
    private static readonly DateTime A_Dec2 = Cal.D(84), A_S2n = Cal.D(90);
    private static readonly DateTime A_Dec3 = Cal.D(126), A_S3n = Cal.D(132);
    private static readonly DateTime A_Dec4 = Cal.D(168), A_S4n = Cal.D(174);

    // RENUMBER BOUNDARIES for a rollsAtPeriodStart family = the PERIOD STARTS, never the
    // announcements. Past ones included: the 1m lookback crosses PS1.
    private static readonly DateTime[] A_Bounds =
        { A_PS3, A_PS2, A_PS1, A_S0, A_S1n, A_S2n, A_S3n, A_S4n };

    private const double A_Fix = 1.750;                     // SWESTR, still the PRE-hike print
    private const double A_Rd = 1.750;                      // the run-down [PS1,S0)
    private const double A_Pre0 = 1.990, A_Post0 = 2.000;   // [S0,S1n)  — the just-decided period
    private const double A_Pre1 = 2.140, A_Post1 = 2.150;   // [S1n,S2n)
    private const double A_Pre2 = 2.290, A_Post2 = 2.300;   // [S2n,S3n)
    private const double A_Pre3 = 2.440, A_Post3 = 2.450;   // [S3n,S4n)

    private static BankSpec Riks28()
    {
        var b = new BankSpec
        {
            Bank = "RIKSBANK",
            DecisionTimeLondon = Cal.TimePassed,   // the statement is out
            MarkTurnPeriods = false,               // no period here straddles a year end
        };
        b.Dates.AddRange(new[] { A_PS3, A_PS2, A_PS1, A_S0, A_S1n, A_S2n, A_S3n, A_S4n });
        b.DecisionDates.AddRange(new[] { A_Dec0, A_Dec1, A_Dec2, A_Dec3, A_Dec4 });
        b.Fix(A_Fix).FixHist(Cal.D(-70), Cal.D(-1), A_Fix);

        // THE FEED HAS NOT RENUMBERED (it will at S0): rung 1 still quotes the decided period,
        // and every rung's PrevClose is its OWN contract's yesterday close.
        b.Quote(0, mid: A_Rd, prevClose: A_Rd, eff: A_PS1, mat: A_S0);
        b.Quote(1, mid: A_Post0, prevClose: A_Pre0, eff: A_S0, mat: A_S1n);
        b.Quote(2, mid: A_Post1, prevClose: A_Pre1, eff: A_S1n, mat: A_S2n);
        b.Quote(3, mid: A_Post2, prevClose: A_Pre2, eff: A_S2n, mat: A_S3n);
        b.Quote(4, mid: A_Post3, prevClose: A_Pre3, eff: A_S3n, mat: A_S4n);

        // history, per CONTRACT: each period reprices by exactly 1bp today (the 25bp hike was
        // already in the curve), so every published change must read +1.0 and nothing else
        b.Contract(A_PS1, A_Bounds, Cal.D(-75), Cal.D(0), A_Rd);
        b.ContractStep(A_S0, A_Bounds, Cal.D(-75), Cal.D(0), A_Dec0, A_Pre0, A_Post0);
        b.ContractStep(A_S1n, A_Bounds, Cal.D(-75), Cal.D(0), A_Dec0, A_Pre1, A_Post1);
        b.ContractStep(A_S2n, A_Bounds, Cal.D(-75), Cal.D(0), A_Dec0, A_Pre2, A_Post2);
        b.ContractStep(A_S3n, A_Bounds, Cal.D(-75), Cal.D(0), A_Dec0, A_Pre3, A_Post3);
        return b;
    }

    // ================================================================= 29
    // THE SEK PERIOD-START DAY. The decision was six days ago; TODAY the period it governs
    // begins, and TODAY is the day SKSF renumbers.
    //
    //   PS2 ....... PS1 ....... DEC0 ...|today = S0|....... S1n ....... S2n ....... S3n ...... S4n
    //                          (-6d)     renumber here
    //
    // The feed HAS re-pointed: rung 1 now quotes [S1n,S2n) while rung 1's PX_CLOSE_1D is
    // yesterday's [S0,S1n). Without the correction every row books the 15bp inter-contract gap
    // as a change-on-day — the August-2026 phantom, in miniature.

    private static readonly DateTime B_PS2 = Cal.D(-84), B_PS1 = Cal.D(-42);
    private static readonly DateTime B_Dec0 = Cal.D(-6), B_S0 = Cal.D(0);
    private static readonly DateTime B_Dec1 = Cal.D(36), B_S1n = Cal.D(42);
    private static readonly DateTime B_Dec2 = Cal.D(78), B_S2n = Cal.D(84);
    private static readonly DateTime B_Dec3 = Cal.D(120), B_S3n = Cal.D(126);
    private static readonly DateTime B_Dec4 = Cal.D(162), B_S4n = Cal.D(168);
    private static readonly DateTime B_Dec5 = Cal.D(204), B_S5n = Cal.D(210);

    private static readonly DateTime[] B_Bounds =
        { B_PS2, B_PS1, B_S0, B_S1n, B_S2n, B_S3n, B_S4n, B_S5n };

    // SWESTR publishes T+1, so on the morning the new rate takes effect the ticker still prints
    // yesterday's (pre-hike) fixing. Seeded honestly — see the finding filed against this run.
    private const double B_Fix = 1.750;
    private const double B_Old = 1.750;    // the period that ENDED today, [PS1,S0)
    private const double B_Cur = 2.000;    // [S0,S1n)  — the new policy rate, effective today
    private const double B_M1 = 2.150;     // [S1n,S2n)
    private const double B_M2 = 2.300;     // [S2n,S3n)
    private const double B_M3 = 2.450;     // [S3n,S4n)
    private const double B_M4 = 2.600;     // [S4n,S5n)
    private const double B_M5 = 2.750;     // [S5n,..)  — never published, seeded as a neighbour

    private static BankSpec Riks29()
    {
        var b = new BankSpec
        {
            Bank = "RIKSBANK",
            DecisionTimeLondon = Cal.TimePassed,
            MarkTurnPeriods = false,
        };
        b.Dates.AddRange(new[] { B_PS2, B_PS1, B_S0, B_S1n, B_S2n, B_S3n, B_S4n, B_S5n });
        b.DecisionDates.AddRange(new[] { B_Dec0, B_Dec1, B_Dec2, B_Dec3, B_Dec4, B_Dec5 });
        b.Fix(B_Fix).FixHistStep(Cal.D(-70), Cal.D(-1), B_S0, B_Old, B_Cur);

        // THE FEED HAS RENUMBERED TODAY. Mids are today's contracts; PrevCloses are YESTERDAY'S
        // contracts on the same ticker number — one rung further out under the old numbering.
        b.Quote(0, mid: B_Cur, prevClose: B_Old, eff: B_S0, mat: B_S1n);
        b.Quote(1, mid: B_M1, prevClose: B_Cur, eff: B_S1n, mat: B_S2n);
        b.Quote(2, mid: B_M2, prevClose: B_M1, eff: B_S2n, mat: B_S3n);
        b.Quote(3, mid: B_M3, prevClose: B_M2, eff: B_S3n, mat: B_S4n);
        b.Quote(4, mid: B_M4, prevClose: B_M3, eff: B_S4n, mat: B_S5n);

        // A QUIET TAPE: every contract flat. The correct Δ1d is therefore 0.0 on every row and
        // the uncorrected one is +15.0 — which also clears OutlierGuard's 12bp absolute bar, so
        // a regression trips the expectation AND the CHECK note.
        //
        // History deliberately STOPS at D(-8): with no snap inside the Δ1d window the published
        // Δ1d falls back to MeetingRun's roll-corrected CoD (PricingServiceWeekly.cs:270,
        // "wm.D1Bp ??= ... row.CoDBp"), which is the path that carried the SEK phantom. Δ1w/Δ1m
        // still anchor on the stitched series.
        b.Contract(B_PS1, B_Bounds, Cal.D(-75), Cal.D(-8), B_Old);
        b.Contract(B_S0, B_Bounds, Cal.D(-75), Cal.D(-8), B_Cur);
        b.Contract(B_S1n, B_Bounds, Cal.D(-75), Cal.D(-8), B_M1);
        b.Contract(B_S2n, B_Bounds, Cal.D(-75), Cal.D(-8), B_M2);
        b.Contract(B_S3n, B_Bounds, Cal.D(-75), Cal.D(-8), B_M3);
        b.Contract(B_S4n, B_Bounds, Cal.D(-75), Cal.D(-8), B_M4);
        b.Contract(B_S5n, B_Bounds, Cal.D(-75), Cal.D(-8), B_M5);
        return b;
    }

    // ================================================================= 30
    // A Y/E TURN PERIOD ON A DECISION WEEK. The Riksbank cuts 25bp today; the period it decided
    // rolls off the board, and of the three rows left the MIDDLE one straddles the year end.
    //
    //   PS1 ...|today = DEC0|... S0 ...... SA ......|TS ... 31-Dec ... TE|...... SC
    //                            gated off          the turn period

    private static readonly DateTime C_PS2 = Cal.D(-84), C_PS1 = Cal.D(-42);
    private static readonly DateTime C_Dec0 = Cal.D(0), C_S0 = Cal.D(6);
    private static readonly DateTime C_DecA = Cal.D(ToYe - 72), C_SA = Cal.D(ToYe - 66);
    private static readonly DateTime C_DecT = Cal.D(ToYe - 30), C_TS = Cal.D(ToYe - 24);
    private static readonly DateTime C_DecB = Cal.D(ToYe + 12), C_TE = Cal.D(ToYe + 18);
    private static readonly DateTime C_DecC = Cal.D(ToYe + 54), C_SC = Cal.D(ToYe + 60);

    private static readonly DateTime[] C_Bounds = { C_PS2, C_PS1, C_S0, C_SA, C_TS, C_TE, C_SC };

    private const double C_Fix = 2.000;                     // SWESTR, still the PRE-cut print
    private const double C_Rd = 2.000;                      // run-down [PS1,S0)
    private const double C_Pre0 = 1.760, C_Post0 = 1.750;   // [S0,SA) — the just-decided period
    private const double C_PreA = 1.710, C_PostA = 1.700;   // [SA,TS)
    private const double C_PreT = 1.260, C_PostT = 1.250;   // [TS,TE) — the TURN: SWESTR drag
    private const double C_PreC = 1.660, C_PostC = 1.650;   // [TE,SC)
    private const double C_PreD = 1.640, C_PostD = 1.630;   // [SC,..) — neighbour only

    private static BankSpec Riks30()
    {
        var b = new BankSpec
        {
            Bank = "RIKSBANK",
            DecisionTimeLondon = Cal.TimePassed,
            MarkTurnPeriods = true,
        };
        b.Dates.AddRange(new[] { C_PS2, C_PS1, C_S0, C_SA, C_TS, C_TE, C_SC });
        b.DecisionDates.AddRange(new[] { C_Dec0, C_DecA, C_DecT, C_DecB, C_DecC });
        b.Fix(C_Fix).FixHist(Cal.D(-70), Cal.D(-1), C_Fix);

        b.Quote(0, mid: C_Rd, prevClose: C_Rd, eff: C_PS1, mat: C_S0);
        b.Quote(1, mid: C_Post0, prevClose: C_Pre0, eff: C_S0, mat: C_SA);
        b.Quote(2, mid: C_PostA, prevClose: C_PreA, eff: C_SA, mat: C_TS);
        b.Quote(3, mid: C_PostT, prevClose: C_PreT, eff: C_TS, mat: C_TE);
        b.Quote(4, mid: C_PostC, prevClose: C_PreC, eff: C_TE, mat: C_SC);

        b.Contract(C_PS1, C_Bounds, Cal.D(-75), Cal.D(0), C_Rd);
        b.ContractStep(C_S0, C_Bounds, Cal.D(-75), Cal.D(0), C_Dec0, C_Pre0, C_Post0);
        b.ContractStep(C_SA, C_Bounds, Cal.D(-75), Cal.D(0), C_Dec0, C_PreA, C_PostA);
        b.ContractStep(C_TS, C_Bounds, Cal.D(-75), Cal.D(0), C_Dec0, C_PreT, C_PostT);
        b.ContractStep(C_TE, C_Bounds, Cal.D(-75), Cal.D(0), C_Dec0, C_PreC, C_PostC);
        b.ContractStep(C_SC, C_Bounds, Cal.D(-75), Cal.D(0), C_Dec0, C_PreD, C_PostD);
        return b;
    }

    // ================================================================= 31 / 33
    // A four-rung strip built around the turn, on a QUIET tape (every contract flat) so the only
    // thing the numbers can be about is the chain arithmetic.
    //
    //   PS2 ..... PS1 ..|today|..... SX ..... SA ....|TS ... 31-Dec ... TE|..... SC
    //   settled   current                             the masked meeting

    private static readonly DateTime D_PS3 = Cal.D(ToYe - 234), D_PS2 = Cal.D(ToYe - 192);
    private static readonly DateTime D_DecP = Cal.D(ToYe - 156), D_PS1 = Cal.D(ToYe - 150);
    private static readonly DateTime D_DecX = Cal.D(ToYe - 114), D_SX = Cal.D(ToYe - 108);
    private static readonly DateTime D_DecA = Cal.D(ToYe - 72), D_SA = Cal.D(ToYe - 66);
    private static readonly DateTime D_DecT = Cal.D(ToYe - 30), D_TS = Cal.D(ToYe - 24);
    private static readonly DateTime D_DecB = Cal.D(ToYe + 12), D_TE = Cal.D(ToYe + 18);
    private static readonly DateTime D_DecC = Cal.D(ToYe + 54), D_SC = Cal.D(ToYe + 60);

    private static readonly DateTime[] D_Bounds =
        { D_PS3, D_PS2, D_PS1, D_SX, D_SA, D_TS, D_TE, D_SC };

    private const double D_Fix = 2.000;    // SWESTR
    private const double D_Rd = 2.000;     // [PS1,SX) — the current period / run-down
    private const double D_MX = 2.050;     // [SX,SA)
    private const double D_MA = 2.150;     // [SA,TS)
    private const double D_MT = 1.550;     // [TS,TE)  — the TURN
    private const double D_MC = 2.450;     // [TE,SC)
    private const double D_MD = 2.500;     // [SC,..)  — neighbour only
    private const double D_P2 = 1.950;     // [PS2,PS1) — settled, neighbour only

    private static BankSpec Riks31()
    {
        var b = new BankSpec { Bank = "RIKSBANK", MarkTurnPeriods = true };
        b.Dates.AddRange(new[] { D_PS3, D_PS2, D_PS1, D_SX, D_SA, D_TS, D_TE, D_SC });
        b.DecisionDates.AddRange(new[] { D_DecP, D_DecX, D_DecA, D_DecT, D_DecB, D_DecC });
        b.Fix(D_Fix).FixHist(Cal.D(-70), Cal.D(-1), D_Fix);

        // no decision today and no period start today: nothing renumbers, so every rung's
        // PrevClose is its own contract's and the whole board is unchanged on the day
        b.Quote(0, mid: D_Rd, prevClose: D_Rd, eff: D_PS1, mat: D_SX);
        b.Quote(1, mid: D_MX, prevClose: D_MX, eff: D_SX, mat: D_SA);
        b.Quote(2, mid: D_MA, prevClose: D_MA, eff: D_SA, mat: D_TS);
        b.Quote(3, mid: D_MT, prevClose: D_MT, eff: D_TS, mat: D_TE);
        b.Quote(4, mid: D_MC, prevClose: D_MC, eff: D_TE, mat: D_SC);

        b.Contract(D_PS2, D_Bounds, Cal.D(-75), Cal.D(0), D_P2);
        b.Contract(D_PS1, D_Bounds, Cal.D(-75), Cal.D(0), D_Rd);
        b.Contract(D_SX, D_Bounds, Cal.D(-75), Cal.D(0), D_MX);
        b.Contract(D_SA, D_Bounds, Cal.D(-75), Cal.D(0), D_MA);
        b.Contract(D_TS, D_Bounds, Cal.D(-75), Cal.D(0), D_MT);
        b.Contract(D_TE, D_Bounds, Cal.D(-75), Cal.D(0), D_MC);
        b.Contract(D_SC, D_Bounds, Cal.D(-75), Cal.D(0), D_MD);
        return b;
    }

    // ================================================================= 32
    // trustConfigDates: SKSF5A/6A quote a live two-sided price and publish NO date fields. The
    // desk has confirmed the config grid may date those rows — so they publish, on config dates,
    // and the run still stops dead at the last rung that carries a PRICE.

    private static readonly DateTime E_PS3 = Cal.D(-105), E_PS2 = Cal.D(-63);
    private static readonly DateTime E_DecP = Cal.D(-27), E_PS1 = Cal.D(-21);
    private static readonly DateTime E_Dec1 = Cal.D(15), E_S1 = Cal.D(21);
    private static readonly DateTime E_Dec2 = Cal.D(57), E_S2 = Cal.D(63);
    private static readonly DateTime E_Dec3 = Cal.D(99), E_S3 = Cal.D(105);
    private static readonly DateTime E_Dec4 = Cal.D(141), E_S4 = Cal.D(147);
    private static readonly DateTime E_Dec5 = Cal.D(183), E_S5 = Cal.D(189);

    private static readonly DateTime[] E_Bounds =
        { E_PS3, E_PS2, E_PS1, E_S1, E_S2, E_S3, E_S4, E_S5 };

    private const double E_Fix = 2.000;
    private const double E_Rd = 2.000;     // [PS1,S1)
    private const double E_M1 = 2.100;     // [S1,S2)
    private const double E_M2 = 2.200;     // [S2,S3)
    private const double E_M3 = 2.300;     // [S3,S4)
    private const double E_M4 = 2.400;     // [S4,S5) — PRICELESS on the feed, must not publish
    private const double E_P2 = 1.900;     // [PS2,PS1) — settled

    private static BankSpec Riks32()
    {
        var b = new BankSpec { Bank = "RIKSBANK", MarkTurnPeriods = false };
        b.Dates.AddRange(new[] { E_PS3, E_PS2, E_PS1, E_S1, E_S2, E_S3, E_S4, E_S5 });
        b.DecisionDates.AddRange(new[] { E_DecP, E_Dec1, E_Dec2, E_Dec3, E_Dec4, E_Dec5 });
        b.Fix(E_Fix).FixHist(Cal.D(-70), Cal.D(-1), E_Fix);

        // THE CARVE-OUT: a live mid on every rung, and NOT ONE date field anywhere. Rung 4 is
        // absent entirely — the meeting is in the config grid but nothing prices it.
        b.Quote(0, mid: E_Rd, prevClose: E_Rd);
        b.Quote(1, mid: E_M1, prevClose: E_M1);
        b.Quote(2, mid: E_M2, prevClose: E_M2);
        b.Quote(3, mid: E_M3, prevClose: E_M3);

        b.Contract(E_PS2, E_Bounds, Cal.D(-75), Cal.D(0), E_P2);
        b.Contract(E_PS1, E_Bounds, Cal.D(-75), Cal.D(0), E_Rd);
        b.Contract(E_S1, E_Bounds, Cal.D(-75), Cal.D(0), E_M1);
        b.Contract(E_S2, E_Bounds, Cal.D(-75), Cal.D(0), E_M2);
        b.Contract(E_S3, E_Bounds, Cal.D(-75), Cal.D(0), E_M3);
        b.Contract(E_S4, E_Bounds, Cal.D(-75), Cal.D(0), E_M4);
        return b;
    }

    // ================================================================= 33
    // THE TURN ROW AS THE FRONT ROW. Everything before the December period has settled, so the
    // very first row the desk reads is the one that straddles the year end.
    //
    // The current period runs unusually long here — "today" is months from December and the
    // front row must BE the turn — but the front-table rendering under test does not care how
    // the board got there.

    private static readonly DateTime F_PS2 = Cal.D(-84), F_DecP = Cal.D(-48), F_PS1 = Cal.D(-42);
    private static readonly DateTime F_DecT = Cal.D(ToYe - 30), F_TS = Cal.D(ToYe - 24);
    private static readonly DateTime F_DecB = Cal.D(ToYe + 12), F_TE = Cal.D(ToYe + 18);
    private static readonly DateTime F_DecC = Cal.D(ToYe + 54), F_SC = Cal.D(ToYe + 60);
    private static readonly DateTime F_DecD = Cal.D(ToYe + 96), F_SD = Cal.D(ToYe + 102);

    private static readonly DateTime[] F_Bounds = { F_PS2, F_PS1, F_TS, F_TE, F_SC, F_SD };

    private const double F_Fix = 2.000;
    private const double F_Rd = 2.000;     // [PS1,TS) — the current period
    private const double F_MT = 1.500;     // [TS,TE)  — the TURN, and the FRONT
    private const double F_MC = 2.400;     // [TE,SC)
    private const double F_MD = 2.500;     // [SC,SD)
    private const double F_ME = 2.600;     // [SD,..)  — neighbour only

    private static BankSpec Riks33()
    {
        var b = new BankSpec { Bank = "RIKSBANK", MarkTurnPeriods = true };
        b.Dates.AddRange(new[] { F_PS2, F_PS1, F_TS, F_TE, F_SC, F_SD });
        b.DecisionDates.AddRange(new[] { F_DecP, F_DecT, F_DecB, F_DecC, F_DecD });
        b.Fix(F_Fix).FixHist(Cal.D(-70), Cal.D(-1), F_Fix);

        b.Quote(0, mid: F_Rd, prevClose: F_Rd, eff: F_PS1, mat: F_TS);
        b.Quote(1, mid: F_MT, prevClose: F_MT, eff: F_TS, mat: F_TE);
        b.Quote(2, mid: F_MC, prevClose: F_MC, eff: F_TE, mat: F_SC);
        b.Quote(3, mid: F_MD, prevClose: F_MD, eff: F_SC, mat: F_SD);

        b.Contract(F_TS, F_Bounds, Cal.D(-75), Cal.D(0), F_MT);
        b.Contract(F_TE, F_Bounds, Cal.D(-75), Cal.D(0), F_MC);
        b.Contract(F_SC, F_Bounds, Cal.D(-75), Cal.D(0), F_MD);
        b.Contract(F_SD, F_Bounds, Cal.D(-75), Cal.D(0), F_ME);
        return b;
    }

    // ================================================================= helpers

    /// <summary>"Y/E Turn" must reach ALL THREE run surfaces as a label, no number may ride with
    /// it on any of them, and the REPORT the surfaces are frozen from must carry none either —
    /// the label is a suppression, not a decoration.
    ///
    /// The HTML surfaces write the label with a NON-BREAKING space ("Y/E&amp;nbsp;Turn"), so the
    /// comparisons go through the parsed tables (Render normalises), never a raw substring.</summary>
    private static IEnumerable<string> TurnRendersEverywhere(Surfaces s, DateTime turnStart)
    {
        var msgs = new List<string>();
        string d = turnStart.ToString("dd-MMM-yy", System.Globalization.CultureInfo.InvariantCulture);

        // --- the report itself: a masked row must publish NOTHING
        var wr = s.Run("RIKSBANK");
        var trow = wr?.Rows.FirstOrDefault(r => r.Date == turnStart.Date);
        if (trow == null) msgs.Add($"the report has no row for the turn period {d}");
        else
        {
            if (!trow.TurnPeriod) msgs.Add($"{d} is not flagged as a Y/E turn period");
            // The turn row KEEPS its real print and its Priced in the model, deliberately:
            // "the row keeps its real print internally (MeetingRow.TurnPeriod)" - the guards and
            // the futures blend consume them, and every renderer substitutes the label. What must
            // not exist even in the model is a STEP or a CHANGE, because the masked meeting makes
            // those unrecoverable. The rendered surfaces are checked below, cell by cell, and are
            // the thing the desk and its clients actually read.
            foreach (var (label, v) in new (string, double?)[]
                     { ("Step", trow.StepBp), ("d1", trow.D1Bp),
                       ("w1", trow.W1Bp), ("m1", trow.M1Bp) })
                if (v is { } x)
                    msgs.Add($"the Y/E turn row {d} publishes {label} {x:+0.0;-0.0;0.0} in the " +
                             "report; the masked meeting makes that number unrecoverable");
        }

        // --- the blast: "07-Dec-26  Y/E Turn" and nothing else
        var blk = Render.Blast(s.BlastText).GetValueOrDefault("RIKSBANK");
        var row = blk?.Rows.FirstOrDefault(r => r.Length > 0 && r[0] == d);
        if (row == null) msgs.Add($"the blast has no row for the turn period {d}");
        else if (row.Length < 3 || row[1] != "Y/E" || row[2] != "Turn")
            msgs.Add($"the blast turn row is not labelled: {string.Join("|", row)}");
        else if (row.Length > 3)
            msgs.Add($"the blast turn row carries {row.Length - 3} cell(s) beyond 'Y/E Turn': "
                     + string.Join("|", row));

        // --- the xlsx Runs sheet: label in the Mid column, columns 4..8 empty
        var xr = Render.Sheet(s.Xlsx).GetValueOrDefault("RIKSBANK")?.Rows
            .FirstOrDefault(r => r.Length > 0 && r[0] == d);
        if (xr == null) msgs.Add($"the xlsx has no row for the turn period {d}");
        else
        {
            if (xr.Length < 3 || xr[2] != "Y/E Turn")
                msgs.Add($"the xlsx turn row Mid cell is '{(xr.Length > 2 ? xr[2] : "")}', expected 'Y/E Turn'");
            for (int c = 3; c < xr.Length; c++)
                if (xr[c].Length > 0)
                    msgs.Add($"the xlsx turn row publishes '{xr[c]}' in column {c + 1}");
        }

        // --- the sheet-style email body: the same table, same cells
        var er = Render.Email(s.SheetHtml).GetValueOrDefault("RIKSBANK")?.Rows
            .FirstOrDefault(r => r.Length > 0 && Render.Norm(r[0]) == d);
        if (er == null) msgs.Add($"the sheet-style email has no row for the turn period {d}");
        else
        {
            if (er.Length < 3 || Render.Norm(er[2]) != "Y/E Turn")
                msgs.Add($"the email turn row Mid cell is '{(er.Length > 2 ? Render.Norm(er[2]) : "")}'," +
                         " expected 'Y/E Turn'");
            for (int c = 3; c < er.Length; c++)
                if (Render.Norm(er[c]).Length > 0)
                    msgs.Add($"the email turn row publishes '{Render.Norm(er[c])}' in column {c + 1}");
        }

        // --- the card email and the plaintext must label it too
        if (!Render.Norm(s.WeeklyHtml.Replace("&nbsp;", " ")).Contains("Y/E Turn"))
            msgs.Add("the card email does not label the turn row");
        if (!s.WeeklyText.Contains("Y/E Turn"))
            msgs.Add("the plaintext email does not label the turn row");

        return msgs;
    }

    /// <summary>The raw per-row change-on-day out of MeetingRun — the layer under the report, so
    /// the roll correction can be asserted whether or not the stitched series masks it.</summary>
    private static IEnumerable<string> CodIs(Surfaces s, params double?[] want)
    {
        var msgs = new List<string>();
        var run = s.Runs.GetValueOrDefault("RIKSBANK");
        if (run == null) { msgs.Add("no RIKSBANK MeetingRun result"); return msgs; }
        if (run.Rows.Count != want.Length)
        {
            msgs.Add($"MeetingRun published {run.Rows.Count} row(s), expected {want.Length}");
            return msgs;
        }
        for (int i = 0; i < want.Length; i++)
        {
            var got = run.Rows[i].CoDBp;
            if (want[i] is { } w)
            {
                if (got is not { } g) msgs.Add($"row {i + 1} CoD is blank, expected {w:+0.0;-0.0;0.0}");
                else if (Math.Abs(g - w) > 0.05)
                    msgs.Add($"row {i + 1} CoD {g:+0.0;-0.0;0.0} != expected {w:+0.0;-0.0;0.0}");
            }
            else if (got is { } g2) msgs.Add($"row {i + 1} CoD expected BLANK, got {g2:+0.0;-0.0;0.0}");
        }
        return msgs;
    }

    // ================================================================= scenarios

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 28
        var announce = new ScenarioSpec
        {
            Id = 28,
            Name = "RIKSBANK hikes TODAY, SKSF renumbers in 6 days (announcement day)",
            Question = "On the announcement day of a period-start-renumbering family, does the " +
                       "decided period leave the board WITHOUT the roll-day CoD correction " +
                       "firing on a feed that has not renumbered?",
        };
        announce.Banks.Add(Riks28());
        announce.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            // the announced-but-not-yet-effective re-base: SWESTR still prints 1.750, so Priced
            // measures against the just-decided period's OWN OIS, 2.000
            Fixing = A_Post0,
            Rebased = true,
            // front = the first row left after the gate drops [S0,S1n)
            //   Priced = (2.150 - 2.000) x 100 = +15.0
            Front = new FrontExpect(A_Dec1, A_S1n, A_Post1, A_Post0, +15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //   Priced = (2.150 - 2.000) x 100 = +15.0 ; first row, no Step
                //   d1/w1/m1 = (2.150 - 2.140) x 100 = +1.0
                new(A_S1n, A_S2n, A_Post1, +15.0, null, +1.0, +1.0, +1.0),
                //   Priced = (2.300 - 2.000) x 100 = +30.0 ; Step = +30.0 - +15.0 = +15.0
                //   d1/w1/m1 = (2.300 - 2.290) x 100 = +1.0
                new(A_S2n, A_S3n, A_Post2, +30.0, +15.0, +1.0, +1.0, +1.0),
                //   Priced = (2.450 - 2.000) x 100 = +45.0 ; Step = +45.0 - +30.0 = +15.0
                //   d1/w1/m1 = (2.450 - 2.440) x 100 = +1.0
                new(A_S3n, A_S4n, A_Post3, +45.0, +15.0, +1.0, +1.0, +1.0),
            },
        });
        announce.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // the decided period must be off every surface the moment the statement lands
            if (s.Run("RIKSBANK")!.Rows.Any(r => r.Date == A_S0))
                msgs.Add("the period the Riksbank just decided is still on the board after the statement");
            if (Render.Blast(s.BlastText).GetValueOrDefault("RIKSBANK") is { } blk
                && blk.Rows.Any(r => r[0] == A_S0.ToString("dd-MMM-yy")))
                msgs.Add("the blast still carries the just-decided period");
            // THE POINT: the family renumbers at S0, six days from now. Today the naive CoD is
            // right. Correcting it (mid(N) - PrevClose(N+1)) would print
            //   (2.150 - 2.290) x 100 = -14.0 / (2.300 - 2.440) x 100 = -14.0 / blank
            // instead of +1.0. PricingServiceBoards.cs:1050 RollCorrectionDue must say NO.
            msgs.AddRange(CodIs(s, +1.0, +1.0, +1.0));
            return msgs;
        });
        announce.NotesNotContain.Add("CHECK");
        announce.NotesNotContain.Add("STALE");
        yield return announce;

        // ---------------------------------------------------------------- 29
        var start = new ScenarioSpec
        {
            Id = 29,
            Name = "RIKSBANK period-start day — SKSF renumbers, decision was 6 days ago",
            Question = "On the day SKSF actually renumbers, does the change-on-day correction " +
                       "fire, or does the 15bp inter-contract gap print as a market move?",
        };
        start.Banks.Add(Riks29());
        start.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            // the re-base window CLOSED at the period start (today >= effective), so Priced is
            // back on the printed SWESTR - which still carries yesterday's, pre-hike, fixing
            Fixing = 2.000,
            Rebased = true,
            //   Priced = (2.150 - 1.750) x 100 = +40.0
            Front = new FrontExpect(B_Dec1, B_S1n, B_M1, 2.000, +15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //   Priced = (2.150 - 1.750) x 100 = +40.0 ; first row, no Step
                //   d1 = roll-corrected CoD = (2.150 - PrevClose(rung 2) 2.150) x 100 = 0.0
                //        (naive would be (2.150 - 2.000) x 100 = +15.0 - the phantom)
                //   w1/m1 = (2.150 - 2.150) x 100 = 0.0   (quiet tape)
                new(B_S1n, B_S2n, B_M1, +15.0, null, 0.0, 0.0, 0.0),
                //   Priced = (2.300 - 1.750) x 100 = +55.0 ; Step = +55.0 - +40.0 = +15.0
                //   d1 = (2.300 - 2.300) x 100 = 0.0
                new(B_S2n, B_S3n, B_M2, +30.0, +15.0, 0.0, 0.0, 0.0),
                //   Priced = (2.450 - 1.750) x 100 = +70.0 ; Step = +15.0
                //   d1 = (2.450 - 2.450) x 100 = 0.0
                new(B_S3n, B_S4n, B_M3, +45.0, +15.0, 0.0, 0.0, 0.0),
                //   Priced = (2.600 - 1.750) x 100 = +85.0 ; Step = +15.0
                //   d1: the LAST quoted rung has no rung above it to borrow a pre-roll close
                //       from, so the corrected CoD has no anchor - blank is the honest answer
                new(B_S4n, B_S5n, B_M4, +60.0, +15.0, null, 0.0, 0.0),
            },
        });
        start.Custom.Add(s =>
        {
            var msgs = new List<string>();
            //   corrected: (2.150-2.150), (2.300-2.300), (2.450-2.450) = 0.0 ; last row unanchored
            //   uncorrected would be (2.150-2.000), (2.300-2.150), (2.450-2.300) = +15.0 each
            msgs.AddRange(CodIs(s, 0.0, 0.0, 0.0, null));
            // the period that ENDED today must not be on the board
            if (s.Run("RIKSBANK")!.Rows.Any(r => r.Date == B_S0))
                msgs.Add("the period that started today is still quoted as a future meeting");
            return msgs;
        });
        // a +15bp phantom on every row clears OutlierGuard's 12bp absolute Δ1d bar
        start.NotesNotContain.Add("CHECK");
        yield return start;

        // ---------------------------------------------------------------- 30
        var turn = new ScenarioSpec
        {
            Id = 30,
            Name = "RIKSBANK cuts TODAY with a Y/E turn period on the board",
            Question = "Does the year-end-straddling period render as a label on the blast, the " +
                       "xlsx and the email, and publish no number anywhere?",
        };
        turn.Banks.Add(Riks30());
        turn.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            // re-based onto the just-decided period's own OIS (SWESTR still prints 2.000)
            Fixing = C_Post0,
            Rebased = true,
            //   Priced = (1.700 - 1.750) x 100 = -5.0
            Front = new FrontExpect(C_DecA, C_SA, C_PostA, C_Post0, -5.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //   Priced = (1.700 - 1.750) x 100 = -5.0 ; first row, no Step
                //   d1/w1/m1 = (1.700 - 1.710) x 100 = -1.0
                new(C_SA, C_TS, C_PostA, -5.0, null, -1.0, -1.0, -1.0),
                //   THE TURN: the label replaces every number, including Priced
                new(C_TS, C_TE, C_PostT, null, null, null, null, null, Turn: true),
                //   Priced = (1.650 - 1.750) x 100 = -10.0
                //   Step SKIPS the masked meeting: -10.0 - (-5.0) = -5.0
                //   d1/w1/m1 = (1.650 - 1.660) x 100 = -1.0
                new(C_TE, C_SC, C_PostC, -10.0, -5.0, -1.0, -1.0, -1.0),
            },
        });
        turn.Custom.Add(s =>
        {
            var msgs = new List<string>(TurnRendersEverywhere(s, C_TS));
            // the turn is NOT the front here - the front must be the clean row above it
            var f = s.Front("RIKSBANK");
            if (f == null) msgs.Add("no RIKSBANK front line");
            else if (f.TurnPeriod) msgs.Add("the front line is flagged as a turn; the turn is row 2");
            return msgs;
        });
        turn.NotesNotContain.Add("CHECK");
        yield return turn;

        // ---------------------------------------------------------------- 31
        var chain = new ScenarioSpec
        {
            Id = 31,
            Name = "RIKSBANK step chain skips the Y/E turn row",
            Question = "Does the row after the turn carry the CUMULATIVE move priced across the " +
                       "masked meeting plus its own, rather than a step off the turn print?",
        };
        chain.Banks.Add(Riks31());
        chain.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            Fixing = D_Fix,
            Rebased = false,
            //   Priced = (2.050 - 2.000) x 100 = +5.0
            Front = new FrontExpect(D_DecX, D_SX, D_MX, D_Fix, +5.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                //   Priced = (2.050 - 2.000) x 100 = +5.0 ; first row, no Step ; quiet tape
                new(D_SX, D_SA, D_MX, +5.0, null, 0.0, 0.0, 0.0),
                //   Priced = (2.150 - 2.000) x 100 = +15.0 ; Step = +15.0 - +5.0 = +10.0
                new(D_SA, D_TS, D_MA, +15.0, +10.0, 0.0, 0.0, 0.0),
                //   THE MASKED MEETING: mid 1.550 is the turn print, no numbers publish
                new(D_TS, D_TE, D_MT, null, null, null, null, null, Turn: true),
                //   Priced = (2.450 - 2.000) x 100 = +45.0
                //   Step differences the last CLEAN Priced (+15.0), NOT the turn row:
                //        +45.0 - +15.0 = +30.0  <- the Dec meeting and the Jan meeting together
                //   A chain that stepped off the turn print would show
                //        +45.0 - (1.550-2.000)x100 = +45.0 - (-45.0) = +90.0
                new(D_TE, D_SC, D_MC, +45.0, +30.0, 0.0, 0.0, 0.0),
            },
        });
        chain.Custom.Add(s =>
        {
            var msgs = new List<string>(TurnRendersEverywhere(s, D_TS));
            // the cumulative step must survive to the surfaces, not just the report
            var blk = Render.Blast(s.BlastText).GetValueOrDefault("RIKSBANK");
            var last = blk?.Rows.LastOrDefault();
            if (last == null) msgs.Add("no RIKSBANK rows in the blast");
            else if (last.Length < 4 || last[3] != "+30.0")
                msgs.Add($"the blast's post-turn Step is '{(last.Length > 3 ? last[3] : "")}', expected '+30.0'");
            return msgs;
        });
        chain.NotesNotContain.Add("CHECK");
        yield return chain;

        // ---------------------------------------------------------------- 32
        var cfg = new ScenarioSpec
        {
            Id = 32,
            Name = "RIKSBANK trustConfigDates — priced rungs with no date fields",
            Question = "Do rungs that price but publish no SW_EFF_DT / MATURITY publish on the " +
                       "desk-confirmed config dates, and does the run still stop where the " +
                       "prices stop?",
        };
        cfg.Banks.Add(Riks32());
        cfg.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            Fixing = E_Fix,
            Rebased = false,
            //   Priced = (2.100 - 2.000) x 100 = +10.0
            Front = new FrontExpect(E_Dec1, E_S1, E_M1, E_Fix, +10.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                //   dates are the CONFIG grid's, end dates the next config start
                //   Priced = (2.100 - 2.000) x 100 = +10.0 ; first row, no Step ; quiet tape
                new(E_S1, E_S2, E_M1, +10.0, null, 0.0, 0.0, 0.0),
                //   Priced = (2.200 - 2.000) x 100 = +20.0 ; Step = +10.0
                new(E_S2, E_S3, E_M2, +20.0, +10.0, 0.0, 0.0, 0.0),
                //   Priced = (2.300 - 2.000) x 100 = +30.0 ; Step = +10.0
                //   the END date is the config's S4 even though nothing prices that rung -
                //   a documented meeting date is a date, it is not a manufactured price
                new(E_S3, E_S4, E_M3, +30.0, +10.0, 0.0, 0.0, 0.0),
            },
        });
        cfg.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Runs.GetValueOrDefault("RIKSBANK");
            // prove the carve-out is the path taken: without trustConfigDates these rows would
            // not be tickerDated and the run would publish NOTHING
            if (run == null) msgs.Add("no RIKSBANK MeetingRun result");
            else if (run.DatesSource != "schedule")
                msgs.Add($"dates came from '{run.DatesSource}', expected 'schedule' - the scenario " +
                         "seeded no date fields, so it is not exercising trustConfigDates");
            // NOTHING may be invented past the last price: S4 is in the grid, priceless, and
            // must not appear as a row start on any surface
            if (s.Run("RIKSBANK")!.Rows.Any(r => r.Date == E_S4))
                msgs.Add("a row was published for the config meeting nothing prices");
            var blk = Render.Blast(s.BlastText).GetValueOrDefault("RIKSBANK");
            if (blk != null && blk.Rows.Any(r => r[0] == E_S4.ToString("dd-MMM-yy")))
                msgs.Add("the blast carries a row for the config meeting nothing prices");
            return msgs;
        });
        cfg.NotesNotContain.Add("CHECK");
        yield return cfg;

        // ---------------------------------------------------------------- 33
        var frontTurn = new ScenarioSpec
        {
            Id = 33,
            Name = "RIKSBANK Y/E turn period IS the front row",
            Question = "When the next meeting period straddles the year end, what does the CB " +
                       "front table print in its Mid, Priced and % of 25bp cells?",
        };
        frontTurn.Banks.Add(Riks33());
        frontTurn.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            Fixing = F_Fix,
            Rebased = false,
            // THE DESK RULE (DESIGN §12, Y/E TURN LABELLING): a turn period is not policy
            // pricing, so the front line shows the label and NO market-pricing number. Priced
            // must therefore be blank - the same suppression the row itself gets.
            // Priced is not checked at MODEL level for a turn front: the model keeps it by
            // design and the RENDERED front table blanks it, which the universal front-table
            // invariant already asserts cell by cell (Priced and '% of 25bp' both empty).
            Front = new FrontExpect(F_DecT, F_TS, F_MT, F_Fix, Any.Num, Rebased: false, Turn: true),
            Rows = new List<RowExpect>
            {
                //   THE FRONT IS THE TURN: label only
                new(F_TS, F_TE, F_MT, null, null, null, null, null, Turn: true),
                //   Priced = (2.400 - 2.000) x 100 = +40.0
                //   Step: the turn is the first row, so there is no clean Priced before this one
                //         and the chain has nothing to difference against - blank
                new(F_TE, F_SC, F_MC, +40.0, null, 0.0, 0.0, 0.0),
                //   Priced = (2.500 - 2.000) x 100 = +50.0 ; Step = +50.0 - +40.0 = +10.0
                new(F_SC, F_SD, F_MD, +50.0, +10.0, 0.0, 0.0, 0.0),
            },
        });
        frontTurn.Custom.Add(s =>
        {
            var msgs = new List<string>(TurnRendersEverywhere(s, F_TS));
            var rows = Render.EmailFront(s.SheetHtml);
            var line = rows.FirstOrDefault(r => r.Length > 0 && r[0].StartsWith("RIKSBANK"));
            if (line == null) { msgs.Add("no RIKSBANK line in the CB front table"); return msgs; }
            // Bank | Decision | Start | OIS Mid | Fixing | Priced (bp) | % 25bp
            if (line.Length < 7) { msgs.Add($"front line has {line.Length} cells, expected 7+"); return msgs; }
            if (line[3] != "Y/E Turn") msgs.Add($"front OIS Mid cell is '{line[3]}', expected 'Y/E Turn'");
            if (line[4] != "2.000") msgs.Add($"front Fixing cell is '{line[4]}', expected '2.000'");
            if (line[5].Length > 0)
                msgs.Add($"front Priced cell publishes '{line[5]}' for a turn period; it must be blank");
            if (line[6].Length > 0)
                msgs.Add($"front % of 25bp cell publishes '{line[6]}' for a turn period; it must be blank");
            return msgs;
        });
        frontTurn.NotesNotContain.Add("CHECK");
        yield return frontTurn;
    }
}
