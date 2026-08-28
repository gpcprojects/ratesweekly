using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE CHANGE COLUMNS - Δ1d / Δ1w / Δ1m, the three cells the desk reads to answer
/// "what moved today". Everything else on the run is a level; these are the only DIFFERENCES,
/// and a difference is where a roll fault hides: the level stays right while the change books
/// the gap between two different contracts.
///
/// EVERY scenario in this group seeds its history RAW, rung by rung (Level / LevelStep / Close /
/// Snap), never through Contract()/ContractStep(). The rung each value lands on is worked out by
/// hand in the comments from the boundary list, so the scenario shares no derivation with the
/// app's own MeetingRungMap - if both were wrong in the same way, these scenarios would still
/// catch it.
///
/// THE THREE ANCHOR RULES BEING EXERCISED (PricingServiceWeekly.BuildWeekly:256-273):
///   Δ1d = ChangeToBp(series, mid, today-1,         maxStale 5)  ... else the row's own CoD
///   Δ1w = ChangeToBp(series, mid, today-7,         maxStale 7)  ... no fallback, ever
///   Δ1m = ChangeToBp(series, mid, MonthAgo(today), maxStale 10) ... no fallback, ever
/// over a series stitched meeting-CONSTANT by MeetingSeriesBuilder, which is where the roll
/// shift, the boundary-day close exclusion and the 16:15-London snap rule live.</summary>
public static class Group05_ChangeColumns
{
    // ================================================================== 22
    // THE PHANTOM-STEP TRAP
    //
    // Geometry (FOMC shape: lag 0, the decision date IS the period start, periods contiguous,
    // the family renumbers AT the announcement):
    //
    //   P2 .......... P1 .......... B0 ..|today|.. D1 .......... D2 .......... D3 .......... D4
    //   settled       settled     decided 3 bd ago (INSIDE the 1w and the 1m window)
    //
    // The market is DEAD FLAT: every contract's rate is what it was forty days ago. Only the
    // TICKER NUMBER moved - it stepped down one at B0. Neighbouring contracts are 20bp apart, so
    // a surface that differences a RUNG against its own past mark books 20bp of pure phantom.
    // Every change cell must print 0.0.
    private static readonly DateTime Q22_P2 = Cal.D(-87), Q22_P1 = Cal.D(-45);
    private static readonly DateTime Q22_B0 = Cal.Bd(-3);   // a real business day, 3-5 cal days back
    private static readonly DateTime Q22_D1 = Cal.D(39), Q22_D2 = Cal.D(81);
    private static readonly DateTime Q22_D3 = Cal.D(123), Q22_D4 = Cal.D(165);

    // the o/n fixing and the four quoted contracts - 20bp apart on purpose
    private const double Q22_Fix = 3.900;
    private const double Q22_C1 = 3.700, Q22_C2 = 3.500, Q22_C3 = 3.300, Q22_C4 = 3.100;

    private static BankSpec Q22_Fomc()
    {
        var b = new BankSpec { Bank = "FOMC" };
        b.Dates.AddRange(new[] { Q22_P2, Q22_P1, Q22_B0, Q22_D1, Q22_D2, Q22_D3, Q22_D4 });
        b.DecisionDates.AddRange(new[] { Q22_B0, Q22_D1, Q22_D2, Q22_D3, Q22_D4 });
        b.Fix(Q22_Fix).FixHist(Cal.D(-70), Cal.D(-1), Q22_Fix);

        // LIVE, feed fully re-pointed (the decision was three business days ago)
        b.Quote(0, mid: Q22_Fix, prevClose: Q22_Fix, eff: Q22_B0, mat: Q22_D1);
        b.Quote(1, mid: Q22_C1, prevClose: Q22_C1, eff: Q22_D1, mat: Q22_D2);
        b.Quote(2, mid: Q22_C2, prevClose: Q22_C2, eff: Q22_D2, mat: Q22_D3);
        b.Quote(3, mid: Q22_C3, prevClose: Q22_C3, eff: Q22_D3, mat: Q22_D4);

        // RAW rung seeding. Boundaries = { P2, P1, B0, D1, D2, D3, D4 }; the rung carrying a
        // contract on day d is #{ b : b > d' and b <= contract }, with d' = d, or d-1 when d IS
        // a boundary. Over the seeded window D(-40)..D(-1) that gives, by hand:
        //
        //   day <= B0 (pre-roll)        day > B0 (post-roll)
        //   rung 1 = contract B0        rung 1 = contract D1
        //   rung 2 = contract D1        rung 2 = contract D2
        //   rung 3 = contract D2        rung 3 = contract D3
        //   rung 4 = contract D3        rung 4 = contract D4
        //
        // so a FLAT contract is a STEPPING rung. LevelStep switches on the first post-roll day.
        var roll = Q22_B0.AddDays(1);
        b.LevelStep(1, Cal.D(-40), Cal.D(-1), roll, Q22_Fix, Q22_C1);
        b.LevelStep(2, Cal.D(-40), Cal.D(-1), roll, Q22_C1, Q22_C2);
        b.LevelStep(3, Cal.D(-40), Cal.D(-1), roll, Q22_C2, Q22_C3);
        b.LevelStep(4, Cal.D(-40), Cal.D(-1), roll, Q22_C3, Q22_C4);
        return b;
    }

    // ================================================================== 23
    // THE RENUMBER DAY, FEED RE-POINTED - the change-on-day correction
    //
    //   P2 .......... P1 ..........|today = MPC decision|.......... D1 .......... D2 ....... D3
    //
    // The MPC cuts today and the generics have ALREADY re-pointed. Ticker N's own PX_CLOSE_1D
    // therefore belongs to the contract N pointed at YESTERDAY, which is today's N+1. The
    // correction (PricingServiceBoards.cs:972-976, gated by RollCorrectionDue:1050) differences
    // mid(N) against PrevClose(N+1). A naive read is a whole inter-contract gap out.
    private static readonly DateTime Q23_P2 = Cal.D(-84), Q23_P1 = Cal.D(-42), Q23_D0 = Cal.D(0);
    private static readonly DateTime Q23_D1 = Cal.D(42), Q23_D2 = Cal.D(84);
    private static readonly DateTime Q23_D3 = Cal.D(126), Q23_D4 = Cal.D(168), Q23_D5 = Cal.D(210);

    private const double Q23_Fix = 3.900;                    // SONIA still prints the OLD rate
    // yesterday's closes, under OLD numbering: ticker k closed on the contract starting dates[k].
    // NOTHING was priced in - the strip sits at the unchanged policy rate, 15bp apart down the run.
    private const double Q23_YD0 = 3.900, Q23_YD1 = 3.750, Q23_YD2 = 3.600, Q23_YD3 = 3.450;
    // today's mids, under NEW numbering: ticker k quotes the contract one further out. The cut
    // was a SURPRISE, so every contract is 22bp lower than the SAME contract's close yesterday.
    private const double Q23_TD0 = 3.680, Q23_TD1 = 3.530, Q23_TD2 = 3.380, Q23_TD3 = 3.230;

    private static BankSpec Q23_Mpc()
    {
        var b = new BankSpec { Bank = "MPC", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { Q23_P2, Q23_P1, Q23_D0, Q23_D1, Q23_D2, Q23_D3, Q23_D4 });
        b.DecisionDates.AddRange(new[] { Q23_D0, Q23_D1, Q23_D2, Q23_D3, Q23_D4 });
        b.Fix(Q23_Fix).FixHist(Cal.D(-70), Cal.D(-1), Q23_Fix);

        // THE FEED HAS RE-POINTED: quote k now covers the period starting one meeting later,
        // while PrevClose on the same ticker is still yesterday's OLD contract.
        b.Quote(0, mid: Q23_TD0, prevClose: Q23_Fix, eff: Q23_D0, mat: Q23_D1);
        b.Quote(1, mid: Q23_TD1, prevClose: Q23_YD0, eff: Q23_D1, mat: Q23_D2);
        b.Quote(2, mid: Q23_TD2, prevClose: Q23_YD1, eff: Q23_D2, mat: Q23_D3);
        b.Quote(3, mid: Q23_TD3, prevClose: Q23_YD2, eff: Q23_D3, mat: Q23_D4);
        // ticker 4 lost its price when the family renumbered past the end of what GPSF quotes,
        // but PX_CLOSE_1D survives - and it is exactly the close row 3's correction reads. It
        // carries a maturity so Resolve() returns it at all (a PrevClose-only security is
        // dropped, PricingServiceBoards.cs:630); with no Mid it still publishes no row.
        b.Quote(4, mid: null, prevClose: Q23_YD3, mat: Q23_D5);

        // RAW rung seeding, all of it BEFORE the roll (history stops yesterday), so the old
        // numbering holds throughout: rung k = the contract starting dates[k].
        b.Level(1, Cal.D(-40), Cal.D(-1), Q23_YD0);
        b.Level(2, Cal.D(-40), Cal.D(-1), Q23_YD1);
        b.Level(3, Cal.D(-40), Cal.D(-1), Q23_YD2);
        b.Level(4, Cal.D(-40), Cal.D(-1), Q23_YD3);
        return b;
    }

    // ================================================================== 24
    // THE STALENESS CAP - a hole in the tape, and a "1w" that refuses to lie
    //
    // BOC shape (lag 1: announcement A, period starts A+1; the family renumbers at A).
    // History exists over D(-40)..D(-19) and NOWHERE after: the store was deepened that far and
    // the last three weeks of BDH never landed.
    //
    //   Δ1d  target D(-1),      nearest mark ~D(-21) -> 18-20d > 5  -> no series value
    //   Δ1w  target D(-7),      nearest mark ~D(-21) -> 12-14d > 7  -> BLANK, no fallback exists
    //   Δ1m  target D(-28..-31), a mark on or beside it ->  0-2d <= 10 -> publishes
    private static readonly DateTime Q24_A2 = Cal.D(-85), Q24_S2 = Cal.D(-84);
    private static readonly DateTime Q24_A1 = Cal.D(-43), Q24_S1 = Cal.D(-42);
    private static readonly DateTime Q24_A0 = Cal.D(20), Q24_S0 = Cal.D(21);
    private static readonly DateTime Q24_An = Cal.D(62), Q24_Sn = Cal.D(63);
    private static readonly DateTime Q24_Am = Cal.D(104), Q24_Sm = Cal.D(105);
    private static readonly DateTime Q24_Al = Cal.D(146), Q24_Sl = Cal.D(147);

    private const double Q24_Fix = 3.900;
    private const double Q24_M1 = 3.750, Q24_M2 = 3.600, Q24_M3 = 3.450;   // live mids
    private const double Q24_H1 = 3.700, Q24_H2 = 3.550, Q24_H3 = 3.400;   // the stranded marks
    private const double Q24_P1 = 3.745, Q24_P2 = 3.595, Q24_P3 = 3.445;   // PX_CLOSE_1D

    private static BankSpec Q24_Boc()
    {
        var b = new BankSpec { Bank = "BOC" };
        b.Dates.AddRange(new[] { Q24_S2, Q24_S1, Q24_S0, Q24_Sn, Q24_Sm, Q24_Sl });
        b.DecisionDates.AddRange(new[] { Q24_A2, Q24_A1, Q24_A0, Q24_An, Q24_Am, Q24_Al });
        b.Fix(Q24_Fix).FixHist(Cal.D(-70), Cal.D(-1), Q24_Fix);

        b.Quote(0, mid: Q24_Fix, prevClose: Q24_Fix, eff: Q24_S1, mat: Q24_S0);
        b.Quote(1, mid: Q24_M1, prevClose: Q24_P1, eff: Q24_S0, mat: Q24_Sn);
        b.Quote(2, mid: Q24_M2, prevClose: Q24_P2, eff: Q24_Sn, mat: Q24_Sm);
        b.Quote(3, mid: Q24_M3, prevClose: Q24_P3, eff: Q24_Sm, mat: Q24_Sl);

        // Boundaries after the 14-day cluster (EARLIEST of each pair, so the ANNOUNCEMENT beats
        // its own period start): { A2, A1, A0, An, Am, Al }. The whole seeded window sits inside
        // (A1, A0), so ONE numbering covers it: rung 1 = S0, rung 2 = Sn, rung 3 = Sm.
        b.Level(1, Cal.D(-40), Cal.D(-19), Q24_H1);
        b.Level(2, Cal.D(-40), Cal.D(-19), Q24_H2);
        b.Level(3, Cal.D(-40), Cal.D(-19), Q24_H3);
        return b;
    }

    // ================================================================== 25
    // BOUNDARY-DAY SOURCES - which mark anchors a lookback on a renumber day
    //
    // Two banks, ONE identical close tape, ONE difference: only one of them published a 16:15
    // London snap on the boundary day B.
    //
    //   FOMC rung 2 on B: close 3.900, snap 3.750  -> the SNAP anchors (uniformly old-numbered)
    //   MPC  rung 2 on B: close 3.900, no snap     -> B is DROPPED, the anchor steps to B-1bd
    //
    // The close is the same 3.900 for both, so if a boundary-day close ever anchored, both would
    // print -10.0. They must not, and they must not agree with each other either.
    private static readonly DateTime Q25_P2 = Cal.D(-88), Q25_P1 = Cal.D(-46);
    private static readonly DateTime Q25_B = Cal.Bd(-2), Q25_Bm1 = Cal.Bd(-3);
    private static readonly DateTime Q25_D1 = Cal.D(38), Q25_D2 = Cal.D(80);
    private static readonly DateTime Q25_D3 = Cal.D(122), Q25_D4 = Cal.D(164);

    private const double Q25_Fix = 3.900;
    private const double Q25_M1 = 3.800, Q25_M2 = 3.600, Q25_M3 = 3.400;   // live mids
    private const double Q25_Old = 3.700;                                  // rung 2 before B
    private const double Q25_BClose = 3.900, Q25_BSnap = 3.750;            // rung 2 ON B

    private static BankSpec Q25_Bank(string name, bool snapOnBoundary)
    {
        var b = new BankSpec { Bank = name };
        b.Dates.AddRange(new[] { Q25_P2, Q25_P1, Q25_B, Q25_D1, Q25_D2, Q25_D3, Q25_D4 });
        b.DecisionDates.AddRange(new[] { Q25_B, Q25_D1, Q25_D2, Q25_D3, Q25_D4 });
        b.Fix(Q25_Fix).FixHist(Cal.D(-70), Cal.D(-1), Q25_Fix);

        b.Quote(0, mid: Q25_Fix, prevClose: Q25_Fix, eff: Q25_B, mat: Q25_D1);
        b.Quote(1, mid: Q25_M1, prevClose: 3.790, eff: Q25_D1, mat: Q25_D2);
        b.Quote(2, mid: Q25_M2, prevClose: 3.590, eff: Q25_D2, mat: Q25_D3);
        b.Quote(3, mid: Q25_M3, prevClose: 3.390, eff: Q25_D3, mat: Q25_D4);

        // Pre-roll numbering across the whole seeded window (P1, B] - B itself reads under the
        // numbering of the day before, which is what makes the boundary rule testable at all:
        //   rung 1 = the current run-down, rung 2 = contract D1, 3 = D2, 4 = D3.
        b.Level(1, Cal.D(-40), Q25_B, 3.900);
        b.Level(2, Cal.D(-40), Q25_Bm1, Q25_Old);
        b.Close(2, Q25_B, Q25_B, Q25_BClose);                       // unattributable by rule
        if (snapOnBoundary) b.Snap(2, Q25_B, Q25_B, Q25_BSnap);     // uniformly old-numbered
        b.Level(3, Cal.D(-40), Q25_B, 3.500);
        b.Level(4, Cal.D(-40), Q25_B, 3.300);
        return b;
    }

    // ================================================================== 26
    // WEEKEND WALK-BACK - a Friday decision read after the weekend
    //
    // The decision lands on the most recent FRIDAY before today and the tape ends there; the run
    // is today. On a Monday run that is the literal desk case - Δ1d's target is SUNDAY and the
    // anchor has to walk back over the weekend to Friday's 16:15 snap. The geometry is placed
    // off Cal.D/Cal.PrevBd so it builds whatever today is; when the walk-back would exceed the
    // 5-day cap (today IS a Friday, so the previous Friday is 7 days back) the CORRECT answer is
    // a blank Δ1d, and the expectation below says so rather than skipping the scenario.
    private static readonly DateTime Q26_F = LastFridayBeforeToday();
    private static readonly DateTime Q26_Fm1 = Cal.PrevBd(Q26_F);
    private static readonly DateTime Q26_P2 = Q26_F.AddDays(-84), Q26_P1 = Q26_F.AddDays(-42);
    private static readonly DateTime Q26_D1 = Q26_F.AddDays(42), Q26_D2 = Q26_F.AddDays(84);
    private static readonly DateTime Q26_D3 = Q26_F.AddDays(126), Q26_D4 = Q26_F.AddDays(168);
    /// <summary>Calendar days the Δ1d anchor must walk: from its target (today-1) back to the
    /// Friday mark. ChangeToBp blanks the cell when this exceeds 5.</summary>
    private static readonly double Q26_WalkDays = (Cal.D(-1).Date - Q26_F.Date).TotalDays;

    private const double Q26_Fix = 3.750;                                       // post-cut EFFR
    private const double Q26_Pre1 = 3.700, Q26_Pre2 = 3.500, Q26_Pre3 = 3.300;  // before Friday
    private const double Q26_Fri1 = 3.550, Q26_Fri2 = 3.350, Q26_Fri3 = 3.150;  // Friday's snap
    private const double Q26_Now1 = 3.530, Q26_Now2 = 3.330, Q26_Now3 = 3.130;  // today's mids

    private static DateTime LastFridayBeforeToday()
    {
        var d = Cal.D(-1).Date;
        while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(-1);
        return d;
    }

    private static BankSpec Q26_Fomc()
    {
        var b = new BankSpec { Bank = "FOMC" };
        b.Dates.AddRange(new[] { Q26_P2, Q26_P1, Q26_F, Q26_D1, Q26_D2, Q26_D3, Q26_D4 });
        b.DecisionDates.AddRange(new[] { Q26_F, Q26_D1, Q26_D2, Q26_D3, Q26_D4 });
        b.Fix(Q26_Fix).FixHist(Cal.D(-70), Cal.D(-1), Q26_Fix);

        // the feed re-pointed over the weekend; PX_CLOSE_1D is post-roll by then, so it is
        // Friday's close of the SAME contract (RollCorrectionDue only fires ON the announcement
        // date, so no correction is applied today - and none is needed)
        b.Quote(0, mid: Q26_Fix, prevClose: Q26_Fix, eff: Q26_F, mat: Q26_D1);
        b.Quote(1, mid: Q26_Now1, prevClose: Q26_Fri1, eff: Q26_D1, mat: Q26_D2);
        b.Quote(2, mid: Q26_Now2, prevClose: Q26_Fri2, eff: Q26_D2, mat: Q26_D3);
        b.Quote(3, mid: Q26_Now3, prevClose: Q26_Fri3, eff: Q26_D3, mat: Q26_D4);

        // Pre-roll numbering all the way to F inclusive: rung 1 = the run-down, 2 = D1, 3 = D2,
        // 4 = D3. The strip repriced 15bp lower on the decision itself and that mark IS Friday's
        // snap, so it survives the boundary-day close exclusion.
        b.Level(1, Cal.D(-40), Q26_Fm1, 3.900);
        b.Level(1, Q26_F, Q26_F, Q26_Fix);
        b.Level(2, Cal.D(-40), Q26_Fm1, Q26_Pre1);
        b.Level(2, Q26_F, Q26_F, Q26_Fri1);
        b.Level(3, Cal.D(-40), Q26_Fm1, Q26_Pre2);
        b.Level(3, Q26_F, Q26_F, Q26_Fri2);
        b.Level(4, Cal.D(-40), Q26_Fm1, Q26_Pre3);
        b.Level(4, Q26_F, Q26_F, Q26_Fri3);
        return b;
    }

    // ================================================================== 27
    // NO PRE-ROLL HISTORY on a rung that has only just become quotable
    //
    // The FOMC cuts today, the feed has re-pointed, and the far row's contract has NEVER been
    // fetched into the store - BDH returns nothing for the generic that carried it. The stitched
    // series is therefore empty for that row alone; the documented fallback (BuildWeekly:272) is
    // the row's own ROLL-AWARE change-on-day, and only because the mid is a real ticker print.
    // 1w and 1m have no fallback at all and must stay blank.
    private static readonly DateTime Q27_P2 = Cal.D(-84), Q27_P1 = Cal.D(-42), Q27_D0 = Cal.D(0);
    private static readonly DateTime Q27_D1 = Cal.D(42), Q27_D2 = Cal.D(84);
    private static readonly DateTime Q27_D3 = Cal.D(126), Q27_D4 = Cal.D(168), Q27_D5 = Cal.D(210);

    private const double Q27_Fix = 3.900;
    private const double Q27_YD0 = 3.700, Q27_YD1 = 3.550, Q27_YD2 = 3.400, Q27_YD3 = 3.250;
    private const double Q27_TD0 = 3.690, Q27_TD1 = 3.530, Q27_TD2 = 3.370, Q27_TD3 = 3.210;

    private static BankSpec Q27_Fomc()
    {
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { Q27_P2, Q27_P1, Q27_D0, Q27_D1, Q27_D2, Q27_D3, Q27_D4 });
        b.DecisionDates.AddRange(new[] { Q27_D0, Q27_D1, Q27_D2, Q27_D3, Q27_D4 });
        b.Fix(Q27_Fix).FixHist(Cal.D(-70), Cal.D(-1), Q27_Fix);

        b.Quote(0, mid: Q27_TD0, prevClose: Q27_Fix, eff: Q27_D0, mat: Q27_D1);
        b.Quote(1, mid: Q27_TD1, prevClose: Q27_YD0, eff: Q27_D1, mat: Q27_D2);
        b.Quote(2, mid: Q27_TD2, prevClose: Q27_YD1, eff: Q27_D2, mat: Q27_D3);
        b.Quote(3, mid: Q27_TD3, prevClose: Q27_YD2, eff: Q27_D3, mat: Q27_D4);
        b.Quote(4, mid: null, prevClose: Q27_YD3, mat: Q27_D5);

        // Pre-roll numbering (history stops yesterday): rung k = the contract starting dates[k].
        // RUNG 4 IS DELIBERATELY UNSEEDED - the contract that today's row 3 quotes has no stored
        // history at any horizon.
        b.Level(1, Cal.D(-40), Cal.D(-1), Q27_YD0);
        b.Level(2, Cal.D(-40), Cal.D(-1), Q27_YD1);
        b.Level(3, Cal.D(-40), Cal.D(-1), Q27_YD2);
        return b;
    }

    // ================================================================== helpers

    private const string Dash = "—";

    /// <summary>One cell of the chat blast. Columns: 0 StartDate 1 Mid 2 Priced 3 Step 4 d1
    /// 5 w1 6 m1.</summary>
    private static IEnumerable<string> BlastCell(Surfaces s, string bank, int row, int col,
        string want, string what)
    {
        var blk = Render.Blast(s.BlastText).GetValueOrDefault(bank);
        if (blk == null) { yield return $"{bank}: no block in the blast"; yield break; }
        if (blk.Rows.Count <= row) { yield return $"{bank}: the blast has {blk.Rows.Count} row(s)"; yield break; }
        var cells = blk.Rows[row];
        string got = col < cells.Length ? Render.Norm(cells[col]) : "(missing)";
        if (got != want)
            yield return $"{bank} blast row {row + 1} {what}: '{got}', expected '{want}'";
    }

    /// <summary>The same cell in the workbook AND the sheet-style email. Columns: 0 StartDate
    /// 1 Maturity 2 Mid 3 Priced 4 Step 5 d1 6 w1 7 m1.</summary>
    private static IEnumerable<string> GridCell(Surfaces s, string bank, int row, int col,
        string want, string what)
    {
        foreach (var (name, blocks) in new[]
                 { ("workbook", Render.Sheet(s.Xlsx)), ("email", Render.Email(s.SheetHtml)) })
        {
            var blk = blocks.GetValueOrDefault(bank);
            if (blk == null) { yield return $"{bank}: no block in the {name}"; continue; }
            if (blk.Rows.Count <= row) { yield return $"{bank}: the {name} has {blk.Rows.Count} row(s)"; continue; }
            var cells = blk.Rows[row];
            string got = col < cells.Length ? Render.Norm(cells[col]) : "(missing)";
            if (got != want)
                yield return $"{bank} {name} row {row + 1} {what}: '{got}', expected '{want}'";
        }
    }

    private static string Bp(double? v) => v is { } x ? x.ToString("+0.0;-0.0;0.0") : "blank";

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 22
        var phantom = new ScenarioSpec
        {
            Id = 22,
            Name = "Phantom-step trap: flat market across a renumber inside 1w and 1m",
            Question = "The decision was three business days ago and not one contract has " +
                       "repriced since. Do Δ1d, Δ1w and Δ1m all print 0.0, or does the 20bp gap " +
                       "between neighbouring contracts leak into the change columns?",
        };
        phantom.Banks.Add(Q22_Fomc());
        phantom.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = Q22_Fix,        // the decision is delivered and EFFR already prints it
            Rebased = false,         // lag 0: today is past the period start, no re-base window
            // Priced = (mid - 3.900) * 100
            //   D1: (3.700-3.900)*100 = -20.0
            //   D2: (3.500-3.900)*100 = -40.0   Step = -40.0 - (-20.0) = -20.0
            //   D3: (3.300-3.900)*100 = -60.0   Step = -60.0 - (-40.0) = -20.0
            Front = new FrontExpect(Q22_D1, Q22_D1, Q22_C1, Q22_Fix, -20.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // Δ1d anchors on a POST-roll day (rung 1 = contract D1 = 3.700)  -> 0.0
                // Δ1w / Δ1m anchor PRE-roll  (rung 2 = contract D1 = 3.700)      -> 0.0
                // a naive same-rung read would print (3.700 - 3.900)*100 = -20.0
                new(Q22_D1, Q22_D2, Q22_C1, -20.0, null,  0.0, 0.0, 0.0),
                new(Q22_D2, Q22_D3, Q22_C2, -40.0, -20.0, 0.0, 0.0, 0.0),
                new(Q22_D3, Q22_D4, Q22_C3, -60.0, -20.0, 0.0, 0.0, 0.0),
            },
        });
        phantom.NotesNotContain.Add("CHECK");
        phantom.NotesNotContain.Add("STALE");
        phantom.Custom.Add(s =>
        {
            var msgs = new List<string>();
            for (int r = 0; r < 3; r++)
                foreach (var c in new[] { 4, 5, 6 })
                    msgs.AddRange(BlastCell(s, "FOMC", r, c, "0.0",
                        c == 4 ? "d1" : c == 5 ? "w1" : "m1"));
            // the specific wrong answer this scenario exists to catch
            var run = s.Run("FOMC");
            if (run != null)
                foreach (var m in run.Rows)
                    foreach (var (lbl, v) in new[] { ("Δ1d", m.D1Bp), ("Δ1w", m.W1Bp), ("Δ1m", m.M1Bp) })
                        if (v is { } x && Math.Abs(Math.Abs(x) - 20.0) < 0.5)
                            msgs.Add($"FOMC {m.Date:dd-MMM-yy}: {lbl} = {Bp(x)} is the 20bp " +
                                     "inter-contract gap, booked as a market move");
            return msgs;
        });
        yield return phantom;

        // ---------------------------------------------------------------- 23
        var renumber = new ScenarioSpec
        {
            Id = 23,
            Name = "Renumber day, feed re-pointed: mid(N) vs PrevClose(N+1)",
            Question = "The MPC has cut by surprise and the generics have already re-pointed. " +
                       "Is the change-on-day differenced against the SAME contract's close, or " +
                       "against the close of the contract that ticker number used to be?",
        };
        renumber.Banks.Add(Q23_Mpc());
        renumber.Expect.Add(new BankExpect
        {
            Bank = "MPC",
            Fixing = 3.680,
            Rebased = true,      // lag 0 - the decided period starts today, no re-base possible
            // Priced = (mid - 3.900) * 100
            //   D1: (3.530-3.900)*100 = -37.0
            //   D2: (3.380-3.900)*100 = -52.0   Step = -15.0
            //   D3: (3.230-3.900)*100 = -67.0   Step = -15.0
            Front = new FrontExpect(Q23_D1, Q23_D1, Q23_TD1, 3.680, -15.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                // the stitched series for D1 is rung 2 (pre-roll) = 3.750 flat, last point
                // yesterday:  Δ1d = (3.530 - 3.750)*100 = -22.0, and Δ1w = Δ1m = -22.0 too.
                // The CORRECTED CoD reads the same: mid(1) - PrevClose(2) = 3.530 - 3.750 = -22.0
                // The NAIVE CoD would be            mid(1) - PrevClose(1) = 3.530 - 3.900 = -37.0
                // - a 15bp phantom on every row, the width of one meeting step.
                new(Q23_D1, Q23_D2, Q23_TD1, -15.0, null, -22.0, -22.0, -22.0),
                new(Q23_D2, Q23_D3, Q23_TD2, -30.0, -15.0, -22.0, -22.0, -22.0),
                new(Q23_D3, Q23_D4, Q23_TD3, -45.0, -15.0, -22.0, -22.0, -22.0),
            },
        });
        // DELIBERATE, AND WORTH THE DESK'S ATTENTION: OutlierGuard's absolute bar is 12bp on
        // Δ1d (OutlierGuard.AbsD1Bp), and it fires "even on a uniform strip". A 25bp surprise
        // moves every row ~22bp, so a perfectly correct decision-day run earns a
        // CHECK-before-distribution flag on EVERY row - on the one day the desk wants the blast
        // out fastest. Asserted here so the behaviour is pinned, not discovered live.
        renumber.NotesContain.Add("CHECK");
        renumber.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Runs.GetValueOrDefault("MPC");
            if (run == null) { msgs.Add("MPC: no raw run result"); return msgs; }
            //                       corrected                          naive
            //  row 1   3.530 - 3.750 = -22.0          3.530 - 3.900 = -37.0
            //  row 2   3.380 - 3.600 = -22.0          3.380 - 3.750 = -37.0
            //  row 3   3.230 - 3.450 = -22.0          3.230 - 3.600 = -37.0
            var want = new[] { -22.0, -22.0, -22.0 };
            var naive = new[] { -37.0, -37.0, -37.0 };
            for (int i = 0; i < want.Length && i < run.Rows.Count; i++)
            {
                var cod = run.Rows[i].CoDBp;
                if (cod is not { } c)
                    msgs.Add($"MPC row {i + 1}: CoD is blank on a renumber day - the correction " +
                             "found no PrevClose(N+1) to read");
                else if (Math.Abs(c - naive[i]) < 0.05)
                    msgs.Add($"MPC row {i + 1}: CoD {Bp(c)} is the NAIVE mid(N)-PrevClose(N) read - " +
                             $"the roll correction did not fire (expected {Bp(want[i])})");
                else if (Math.Abs(c - want[i]) > 0.05)
                    msgs.Add($"MPC row {i + 1}: CoD {Bp(c)} != expected {Bp(want[i])}");
            }
            // the two independent routes to "what moved today" - the stitched series and the
            // roll-corrected CoD - must agree, or one of them is mis-rung
            var pub = s.Run("MPC");
            if (pub != null)
                for (int i = 0; i < pub.Rows.Count && i < run.Rows.Count; i++)
                    if (pub.Rows[i].D1Bp is { } d && run.Rows[i].CoDBp is { } c
                        && Math.Abs(d - c) > 0.05)
                        msgs.Add($"MPC row {i + 1}: published Δ1d {Bp(d)} disagrees with the " +
                                 $"roll-corrected CoD {Bp(c)}");
            // the guard must flag the Δ1d column only - Δ1w (30bp bar) and Δ1m (50bp bar) are
            // not breached by a 22bp move, and the cross-sectional test needs four populated rows
            int d1Checks = s.Notes.Count(n => n.StartsWith("CHECK") && n.Contains("Δ1d"));
            int otherChecks = s.Notes.Count(n => n.StartsWith("CHECK")
                                                 && (n.Contains("Δ1w") || n.Contains("Δ1m")));
            if (d1Checks != 3)
                msgs.Add($"expected one Δ1d CHECK per published row (3), got {d1Checks}: " +
                         string.Join(" || ", s.Notes));
            if (otherChecks != 0)
                msgs.Add($"a 22bp move breached the Δ1w/Δ1m sanity bars, which sit at 30/50bp: " +
                         string.Join(" || ", s.Notes));
            return msgs;
        });
        yield return renumber;

        // ---------------------------------------------------------------- 24
        var stale = new ScenarioSpec
        {
            Id = 24,
            Name = "Staleness cap: a three-week hole must blank the 1w, not stretch it",
            Question = "The tape stops nineteen days ago. Does the 1w column go blank, or does " +
                       "it publish a three-week move under a one-week label?",
        };
        stale.Banks.Add(Q24_Boc());
        stale.Expect.Add(new BankExpect
        {
            Bank = "BOC",
            Fixing = Q24_Fix,
            Rebased = false,
            // Priced = (mid - 3.900) * 100 -> -15.0 / -30.0 / -45.0, Steps -15.0 / -15.0
            Front = new FrontExpect(Q24_A0, Q24_S0, Q24_M1, Q24_Fix, -15.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // Δ1d: no series point within 5d of today-1, so the documented CoD fallback
                //      fires: (3.750 - 3.745)*100 = +0.5 (and +0.5 on rows 2 and 3 alike)
                // Δ1w: the nearest mark is 12-14 days before the target, cap 7 -> BLANK
                // Δ1m: a mark sits on (or within 2 days of) the target -> (3.750-3.700)*100 = +5.0
                new(Q24_S0, Q24_Sn, Q24_M1, -15.0, null,  +0.5, null, +5.0),
                new(Q24_Sn, Q24_Sm, Q24_M2, -30.0, -15.0, +0.5, null, +5.0),
                new(Q24_Sm, Q24_Sl, Q24_M3, -45.0, -15.0, +0.5, null, +5.0),
            },
        });
        stale.Custom.Add(s =>
        {
            var msgs = new List<string>();
            for (int r = 0; r < 3; r++)
            {
                // a blank must READ as blank: an em-dash in the chat blast, an EMPTY cell in the
                // workbook and the email. A stale number under a "1w" label is a lie; so is a
                // zero, and so is a gap the eye reads as alignment.
                msgs.AddRange(BlastCell(s, "BOC", r, 5, Dash, "w1 (must be blank)"));
                msgs.AddRange(BlastCell(s, "BOC", r, 6, "+5.0", "m1"));
                msgs.AddRange(GridCell(s, "BOC", r, 6, "", "w1 (must be an empty cell)"));
                msgs.AddRange(GridCell(s, "BOC", r, 7, "+5.0", "m1"));
            }
            return msgs;
        });
        yield return stale;

        // ---------------------------------------------------------------- 25
        var boundary = new ScenarioSpec
        {
            Id = 25,
            Name = "Boundary-day sources: the 16:15 snap anchors, the close never does",
            Question = "Two families, one identical close tape, one difference: only one snapped " +
                       "the boundary day. Does the published Δ1d follow the snap, and does the " +
                       "family without one step back a day rather than use the close?",
        };
        boundary.Banks.Add(Q25_Bank("FOMC", snapOnBoundary: true));
        boundary.Banks.Add(Q25_Bank("MPC", snapOnBoundary: false));
        // Priced (both banks, fixing 3.900): -10.0 / -30.0 / -50.0, Steps -20.0 / -20.0
        boundary.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = Q25_Fix,
            Rebased = false,
            Front = new FrontExpect(Q25_D1, Q25_D1, Q25_M1, Q25_Fix, -10.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // row 1's series ends at B with the SNAP 3.750 (snapped boundary days are kept):
                //   Δ1d = (3.800 - 3.750)*100 = +5.0
                //   Δ1w / Δ1m anchor before B at 3.700 -> (3.800 - 3.700)*100 = +10.0
                //   had the boundary CLOSE 3.900 anchored: (3.800 - 3.900)*100 = -10.0
                new(Q25_D1, Q25_D2, Q25_M1, -10.0, null,  +5.0,  +10.0, +10.0),
                new(Q25_D2, Q25_D3, Q25_M2, -30.0, -20.0, +10.0, +10.0, +10.0),
                new(Q25_D3, Q25_D4, Q25_M3, -50.0, -20.0, +10.0, +10.0, +10.0),
            },
        });
        boundary.Expect.Add(new BankExpect
        {
            Bank = "MPC",
            Fixing = Q25_Fix,
            Rebased = false,
            Front = new FrontExpect(Q25_D1, Q25_D1, Q25_M1, Q25_Fix, -10.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // row 1: no snap on B, so the boundary day is dropped whole and the anchor is
                // B-1bd at 3.700 -> (3.800 - 3.700)*100 = +10.0. NOT the 3.900 close (-10.0).
                new(Q25_D1, Q25_D2, Q25_M1, -10.0, null,  +10.0, +10.0, +10.0),
                new(Q25_D2, Q25_D3, Q25_M2, -30.0, -20.0, +10.0, +10.0, +10.0),
                new(Q25_D3, Q25_D4, Q25_M3, -50.0, -20.0, +10.0, +10.0, +10.0),
            },
        });
        boundary.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var f = s.Run("FOMC"); var m = s.Run("MPC");
            if (f == null || m == null) { msgs.Add("a run is missing from the report"); return msgs; }
            foreach (var (bank, run) in new[] { ("FOMC", f), ("MPC", m) })
                if (run.Rows.Count > 0 && run.Rows[0].D1Bp is { } d && Math.Abs(d + 10.0) < 0.05)
                    msgs.Add($"{bank} row 1: Δ1d {Bp(d)} is the boundary-day CLOSE read - a " +
                             "decision-day close is unattributable and must never anchor");
            if (f.Rows.Count > 0 && m.Rows.Count > 0
                && f.Rows[0].D1Bp is { } fd && m.Rows[0].D1Bp is { } md
                && Math.Abs(fd - md) < 0.05)
                msgs.Add("the snapped and un-snapped families published the SAME Δ1d - the " +
                         "boundary-day snap is not being distinguished from the close");
            return msgs;
        });
        yield return boundary;

        // ---------------------------------------------------------------- 26
        var weekend = new ScenarioSpec
        {
            Id = 26,
            Name = "Weekend walk-back: Friday decision, run after the weekend",
            Question = "The last mark of any kind is Friday's 16:15 snap. Does Δ1d walk back " +
                       "over the weekend to it and print the move since, leaving the delivered " +
                       "cut where it belongs - in the 1w and the 1m?",
        };
        weekend.Banks.Add(Q26_Fomc());
        weekend.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = Q26_Fix,     // the period started Friday, so EFFR prints the new rate
            Rebased = false,
            // Priced = (mid - 3.750) * 100
            //   D1: (3.530-3.750)*100 = -22.0
            //   D2: (3.330-3.750)*100 = -42.0   Step = -20.0
            //   D3: (3.130-3.750)*100 = -62.0   Step = -20.0
            Front = new FrontExpect(Q26_D1, Q26_D1, Q26_Now1, Q26_Fix, -22.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                // Δ1d: target today-1, nearest mark is Friday's snap; walk = Q26_WalkDays.
                //      <= 5  -> (3.530 - 3.550)*100 = -2.0
                //      >  5  -> the cap blanks it, which is the honest answer
                // Δ1w: target today-7, always strictly before that Friday, anchors 3.700
                //      -> (3.530 - 3.700)*100 = -17.0   (Δ1m identical, same flat pre-decision tape)
                new(Q26_D1, Q26_D2, Q26_Now1, -22.0, null,  Q26_WalkDays <= 5 ? -2.0 : null, -17.0, -17.0),
                new(Q26_D2, Q26_D3, Q26_Now2, -42.0, -20.0, Q26_WalkDays <= 5 ? -2.0 : null, -17.0, -17.0),
                new(Q26_D3, Q26_D4, Q26_Now3, -62.0, -20.0, Q26_WalkDays <= 5 ? -2.0 : null, -17.0, -17.0),
            },
        });
        weekend.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("FOMC");
            if (run == null) { msgs.Add("FOMC: no run"); return msgs; }
            foreach (var r in run.Rows)
            {
                // the walk-back must never blur the horizons together: a Δ1d equal to the Δ1w
                // means the anchor slipped past Friday into the pre-decision tape
                if (r.D1Bp is { } d && r.W1Bp is { } w && Math.Abs(d - w) < 0.05)
                    msgs.Add($"FOMC {r.Date:dd-MMM-yy}: Δ1d and Δ1w are both {Bp(d)} - the 1d " +
                             "anchor walked past Friday's mark into the pre-decision tape");
                // ...and it must not swallow the delivered decision
                if (r.D1Bp is { } d2 && Math.Abs(d2) > 12.0)
                    msgs.Add($"FOMC {r.Date:dd-MMM-yy}: Δ1d {Bp(d2)} carries the delivered " +
                             "decision - the 1d anchor is on the wrong side of the announcement");
            }
            return msgs;
        });
        yield return weekend;

        // ---------------------------------------------------------------- 27
        var fresh = new ScenarioSpec
        {
            Id = 27,
            Name = "No pre-roll history: 1d falls back to CoD, 1w and 1m stay blank",
            Question = "One row's contract has no stored history at all. Does its Δ1d fall back " +
                       "to the row's own roll-aware change-on-day, and do 1w and 1m stay blank " +
                       "rather than being manufactured from whatever is nearest?",
        };
        fresh.Banks.Add(Q27_Fomc());
        fresh.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = 3.690,
            Rebased = true,
            // Priced = (mid - 3.900) * 100 -> -37.0 / -53.0 / -69.0, Steps -16.0 / -16.0
            Front = new FrontExpect(Q27_D1, Q27_D1, Q27_TD1, 3.690, -16.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                // rows 1-2 have their rungs seeded: series 3.550 and 3.400, flat
                //   (3.530 - 3.550)*100 = -2.0   and   (3.370 - 3.400)*100 = -3.0
                new(Q27_D1, Q27_D2, Q27_TD1, -16.0, null, -2.0, -2.0, -2.0),
                new(Q27_D2, Q27_D3, Q27_TD2, -32.0, -16.0, -3.0, -3.0, -3.0),
                // row 3's contract sat on rung 4, which the store has never held. The stitched
                // series is EMPTY, so all three anchors fail. Δ1d falls back to the roll-aware
                // CoD: mid(3) - PrevClose(4) = (3.210 - 3.250)*100 = -4.0
                //   (the naive mid(3) - PrevClose(3) would be (3.210 - 3.400)*100 = -19.0)
                // Δ1w and Δ1m have no fallback and must be BLANK.
                new(Q27_D3, Q27_D4, Q27_TD3, -48.0, -16.0, -4.0, null, null),
            },
        });
        fresh.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var pub = s.Run("FOMC");
            var run = s.Runs.GetValueOrDefault("FOMC");
            if (pub == null || run == null) { msgs.Add("FOMC: no run"); return msgs; }
            if (pub.Rows.Count < 3) { msgs.Add($"FOMC: only {pub.Rows.Count} row(s) published"); return msgs; }
            var r3 = pub.Rows[2];
            if (r3.MidSource != "ticker")
                msgs.Add($"FOMC row 3: the CoD fallback is licensed only for a real print, and " +
                         $"the mid source is '{r3.MidSource}'");
            if (run.Rows.Count >= 3 && run.Rows[2].CoDBp is { } cod)
            {
                if (Math.Abs(cod + 19.0) < 0.05)
                    msgs.Add("FOMC row 3: the fallback used the NAIVE mid(N)-PrevClose(N) CoD " +
                             "(-19.0) - the roll correction did not fire on the renumber day");
                if (r3.D1Bp is { } d && Math.Abs(d - cod) > 0.05)
                    msgs.Add($"FOMC row 3: Δ1d {Bp(d)} is not the row's own CoD {Bp(cod)}");
            }
            if (r3.W1Bp.HasValue || r3.M1Bp.HasValue)
                msgs.Add($"FOMC row 3: 1w/1m were manufactured ({Bp(r3.W1Bp)} / {Bp(r3.M1Bp)}) " +
                         "although the contract has no stored history at any horizon");
            msgs.AddRange(BlastCell(s, "FOMC", 2, 5, Dash, "w1 (must be blank)"));
            msgs.AddRange(BlastCell(s, "FOMC", 2, 6, Dash, "m1 (must be blank)"));
            msgs.AddRange(GridCell(s, "FOMC", 2, 6, "", "w1 (must be an empty cell)"));
            msgs.AddRange(GridCell(s, "FOMC", 2, 7, "", "m1 (must be an empty cell)"));
            return msgs;
        });
        yield return fresh;
    }
}
