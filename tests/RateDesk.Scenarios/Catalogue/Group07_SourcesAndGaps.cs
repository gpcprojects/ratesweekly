using System.Globalization;
using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>CONTRIBUTOR SOURCES AND DATA GAPS, on a decision day.
///
/// Three of the ten runs price off a DEALER PAGE rather than the composite (RBA/RBNZ = NABZ,
/// BOC = BMOD), and a dealer page is a different security from the composite: "ADSF1A NABZ
/// Curncy" carries the PRICE, "ADSF1A Curncy" carries SW_EFF_DT/MATURITY. Every number the desk
/// reads on those three runs is therefore a MERGE of two securities, and the merge has to survive
/// the day the bank moves - the one day the two spellings disagree most.
///
/// These six scenarios walk the merge and the four ways the data can be missing on that day:
///   34  the merge itself      - dates from the composite, price from the source page
///   35  the re-base fallback  - no live mid on the decided rung, so a CLOSE has to carry it
///   36  a rung goes unquoted  - the run truncates, and the last row's maturity has to be honest
///   37  a rung goes quiet     - the &gt;1h STALE watch, and whether it names the FRONT
///   38  the misprint guard    - it must rewrite an interior misprint and NEVER the front
///   39  the fixing is out     - Priced/%25bp blank rather than invented, mids still published
///
/// GEOMETRY. Three calendars are shared, all anchored on today:
///
///   "A"  42-day cadence, 1-day settlement lag (RBA/RBNZ/BOC shape)
///        S2 ....... S1 ...|today=Dec0|.St0. Dec1 . St1 .. Dec2 . St2 .. Dec3 . St3 ... (St4)
///   "B"  49-day cadence, 1-day lag - same shape, wider, so two banks in one scenario never
///        share a date and market.txt stays readable
///   "M"  42-day cadence, SAME-DAY start (MPC): the decided period begins on the decision date,
///        which is what makes the announced-but-not-yet-effective re-base impossible there.
///
/// In all of them the decision is TODAY and the statement is OUT (decisionTimeLondon = 00:05),
/// the feed has NOT re-pointed, and the time gate does the roll: the decided period leaves the
/// board and quotes[0] becomes that period's own OIS - the rung the Priced re-base reads.
///
/// The BOUNDARY LISTS passed to Contract()/ContractStep() are the dates the family RENUMBERS on,
/// i.e. the ANNOUNCEMENTS (none of these banks is the Riksbank). Past announcements are derived
/// by the product as start - lag, so A_Bounds/B_Bounds carry D(-85)/D(-43) and D(-99)/D(-50)
/// explicitly.</summary>
public static class Group07_SourcesAndGaps
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string D(DateTime d) => d.ToString("dd-MMM-yy", Inv);

    // ---------------------------------------------------------------- calendars

    // "A": 42-day cadence, 1-day lag
    private static readonly DateTime A_S2 = Cal.D(-84), A_S1 = Cal.D(-42);
    private static readonly DateTime A_Dec0 = Cal.D(0), A_St0 = Cal.D(1);
    private static readonly DateTime A_Dec1 = Cal.D(42), A_St1 = Cal.D(43);
    private static readonly DateTime A_Dec2 = Cal.D(84), A_St2 = Cal.D(85);
    private static readonly DateTime A_Dec3 = Cal.D(126), A_St3 = Cal.D(127);
    private static readonly DateTime A_St4 = Cal.D(169);
    // announcements: the two past ones are derived by the product as start-1
    private static readonly DateTime[] A_Bounds =
        { Cal.D(-85), Cal.D(-43), A_Dec0, A_Dec1, A_Dec2, A_Dec3 };

    // "B": 49-day cadence, 1-day lag
    private static readonly DateTime B_S2 = Cal.D(-98), B_S1 = Cal.D(-49);
    private static readonly DateTime B_Dec0 = Cal.D(0), B_St0 = Cal.D(1);
    private static readonly DateTime B_Dec1 = Cal.D(49), B_St1 = Cal.D(50);
    private static readonly DateTime B_Dec2 = Cal.D(98), B_St2 = Cal.D(99);
    private static readonly DateTime B_Dec3 = Cal.D(147), B_St3 = Cal.D(148);
    private static readonly DateTime B_St4 = Cal.D(197);
    private static readonly DateTime[] B_Bounds =
        { Cal.D(-99), Cal.D(-50), B_Dec0, B_Dec1, B_Dec2, B_Dec3 };

    // "M": 42-day cadence, SAME-DAY start (MPC)
    private static readonly DateTime M_P2 = Cal.D(-84), M_P1 = Cal.D(-42);
    private static readonly DateTime M_D0 = Cal.D(0), M_D1 = Cal.D(42), M_D2 = Cal.D(84);
    private static readonly DateTime M_D3 = Cal.D(126), M_D4 = Cal.D(168);
    private static readonly DateTime[] M_Bounds = { M_P2, M_P1, M_D0, M_D1, M_D2, M_D3 };

    private static readonly DateTime? NoEnd = null;   // "this cell must be blank"

    /// <summary>A bank on calendar A, decision today, statement already out.</summary>
    private static BankSpec BankA(string bank)
    {
        var b = new BankSpec { Bank = bank, DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { A_S2, A_S1, A_St0, A_St1, A_St2, A_St3 });
        b.DecisionDates.AddRange(new[] { A_Dec0, A_Dec1, A_Dec2, A_Dec3 });
        return b;
    }

    /// <summary>A bank on calendar B, decision today, statement already out.</summary>
    private static BankSpec BankB(string bank)
    {
        var b = new BankSpec { Bank = bank, DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { B_S2, B_S1, B_St0, B_St1, B_St2, B_St3 });
        b.DecisionDates.AddRange(new[] { B_Dec0, B_Dec1, B_Dec2, B_Dec3 });
        return b;
    }

    /// <summary>The COMPOSITE spelling of one rung: the fields, no price - the shape a dealer-
    /// sourced run is actually in (the desk's own sheet reads BDP "ADSF2A" for eff/maturity and
    /// BDH "ADSF2A NABZ" for the rate).</summary>
    private static void Fields(BankSpec b, int n, DateTime eff, DateTime? mat) =>
        b.Quote(n, mid: null, eff: eff, mat: mat, sp: Spell.Composite);

    /// <summary>The SOURCE-PAGE spelling of one rung: the price, no date fields.</summary>
    private static void Price(BankSpec b, int n, double mid, double prevClose, double? age = null) =>
        b.Quote(n, mid: mid, prevClose: prevClose, age: age, sp: Spell.Active);

    public static IEnumerable<ScenarioSpec> All()
    {
        foreach (var s in S34()) yield return s;
        foreach (var s in S35()) yield return s;
        foreach (var s in S36()) yield return s;
        foreach (var s in S37()) yield return s;
        foreach (var s in S38()) yield return s;
        foreach (var s in S39()) yield return s;
    }

    // ================================================================ 34

    // The two securities DISAGREE ON PRICE by a round 8bp all the way down the strip, and the
    // strip's own rungs are 10bp apart - so reading the wrong security, or the wrong rung, gives
    // an obviously different number. The composite is the stale one (it prints off NABZ
    // episodically, desk 2026-08-25), and the desk's rule is: PRICE from the source page, DATES
    // from whichever spelling carries the fields.
    private const double S34_Fix = 3.850;                     // RBACOR, still the PRE-cut cash rate
    private const double S34_Src0 = 3.850;                    // run-down, source page
    private const double S34_Src1 = 3.600, S34_Cmp1 = 3.680;  // the just-decided period  D(1)->D(43)
    private const double S34_Src2 = 3.500, S34_Cmp2 = 3.580;  // published row 1          D(43)->D(85)
    private const double S34_Src3 = 3.400, S34_Cmp3 = 3.480;  // published row 2          D(85)->D(127)
    private const double S34_Src4 = 3.350, S34_Cmp4 = 3.430;  // published row 3          D(127)->D(169)

    private static IEnumerable<ScenarioSpec> S34()
    {
        var b = BankA("RBA");
        b.Fix(S34_Fix).FixHist(Cal.D(-70), Cal.D(-1), S34_Fix);

        // dates: composite only (the run-down's composite does not price at all - the thin
        // meeting OIS the dealer page carries and the composite drops)
        Fields(b, 0, A_S1, A_St0);
        Fields(b, 1, A_St0, A_St1);
        Fields(b, 2, A_St1, A_St2);
        Fields(b, 3, A_St2, A_St3);
        Fields(b, 4, A_St3, A_St4);
        // ...and the composite also prints a STALE price on every meeting rung, 8bp above NABZ
        b.Quote(1, mid: S34_Cmp1, prevClose: S34_Cmp1 - 0.040, sp: Spell.Composite);
        b.Quote(2, mid: S34_Cmp2, prevClose: S34_Cmp2 - 0.040, sp: Spell.Composite);
        b.Quote(3, mid: S34_Cmp3, prevClose: S34_Cmp3 - 0.040, sp: Spell.Composite);
        b.Quote(4, mid: S34_Cmp4, prevClose: S34_Cmp4 - 0.040, sp: Spell.Composite);

        // prices: the source page, no date fields at all
        Price(b, 0, S34_Src0, S34_Src0);
        Price(b, 1, S34_Src1, S34_Src1 - 0.040);
        Price(b, 2, S34_Src2, S34_Src2 - 0.040);
        Price(b, 3, S34_Src3, S34_Src3 - 0.040);
        Price(b, 4, S34_Src4, S34_Src4 - 0.040);

        // history, contract by contract. The source page's tape is the true one; the composite's
        // rides 8bp above it, so a change column anchored on the wrong contributor is off by 8bp.
        b.Contract(A_S1, A_Bounds, Cal.D(-70), Cal.D(-1), S34_Fix, Spell.Active);
        b.ContractStep(A_St0, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Src1 - 0.040, S34_Src1, Spell.Active);
        b.ContractStep(A_St1, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Src2 - 0.040, S34_Src2, Spell.Active);
        b.ContractStep(A_St2, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Src3 - 0.040, S34_Src3, Spell.Active);
        b.ContractStep(A_St3, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Src4 - 0.040, S34_Src4, Spell.Active);
        b.Contract(A_S1, A_Bounds, Cal.D(-70), Cal.D(-1), S34_Fix + 0.080, Spell.Composite);
        b.ContractStep(A_St0, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Cmp1 - 0.040, S34_Cmp1, Spell.Composite);
        b.ContractStep(A_St1, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Cmp2 - 0.040, S34_Cmp2, Spell.Composite);
        b.ContractStep(A_St2, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Cmp3 - 0.040, S34_Cmp3, Spell.Composite);
        b.ContractStep(A_St3, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S34_Cmp4 - 0.040, S34_Cmp4, Spell.Composite);

        var spec = new ScenarioSpec
        {
            Id = 34,
            Name = "RBA cuts TODAY - source page prices, composite dates",
            Question = "When the dealer page carries the price and only the composite carries " +
                       "SW_EFF_DT/MATURITY, does every published row take its DATES from the " +
                       "composite and its PRICE from the source page - on the day the two " +
                       "disagree most?",
        };
        spec.Banks.Add(b);

        // ---- derivation -------------------------------------------------------------------
        // The gate: meeting 1 resolves to A_St0 = D(+1) off the run-down's MATURITY (composite);
        // its decision is today and the statement is out, so the decided period leaves the board
        // and quotes[0] becomes its own OIS. That rung's SOURCE mid is 3.600, so
        //     re-based fixing = 3.600   (not RBACOR 3.850, not the composite's 3.680)
        // Priced = (mid - 3.600) x 100, on the SOURCE mids:
        //     row 1  D(+43)  (3.500 - 3.600) x 100 = -10.0
        //     row 2  D(+85)  (3.400 - 3.600) x 100 = -20.0   Step = -20.0 - -10.0 = -10.0
        //     row 3  D(+127) (3.350 - 3.600) x 100 = -25.0   Step = -25.0 - -20.0 =  -5.0
        // Had the composite been published instead, row 1 would read 3.580 off a 3.680 base -
        // the same Priced on a different mid, which is exactly why the MID is asserted too.
        // Changes: every source contract stepped +4bp on the decision, and the stitched series
        // is flat at (mid - 0.040) at D(-1)/D(-7)/D(-31), so
        //     d1 = w1 = m1 = (mid - (mid - 0.040)) x 100 = +4.0 on every row.
        // Off the COMPOSITE tape the same anchors sit 8bp higher and every change would read
        // -4.0 instead.
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBA",
            Fixing = S34_Src1, Rebased = true,
            Front = new FrontExpect(A_Dec1, A_St1, S34_Src2, S34_Src1, -10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //    start  end     mid        priced  step    d1     w1     m1
                new(A_St1, A_St2, S34_Src2, -10.0, null,  +4.0, +4.0, +4.0),
                new(A_St2, A_St3, S34_Src3, -20.0, -10.0, +4.0, +4.0, +4.0),
                new(A_St3, A_St4, S34_Src4, -25.0,  -5.0, +4.0, +4.0, +4.0),
            },
        });
        spec.NotesNotContain.Add("CHECK");
        spec.NotesNotContain.Add("STALE");
        spec.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("RBA")!;
            // the composite's prices must not have reached ANY surface
            foreach (var (label, v) in new[]
                     { ("row 1", S34_Cmp2), ("row 2", S34_Cmp3), ("row 3", S34_Cmp4), ("fixing", S34_Cmp1) })
            {
                string txt = v.ToString("0.000", Inv);
                if (s.BlastText.Contains(txt))
                    msgs.Add($"the blast carries the COMPOSITE price {txt} ({label}) - the run " +
                             "prices off NABZ");
                if (s.SheetHtml.Contains(txt))
                    msgs.Add($"the sheet email carries the COMPOSITE price {txt} ({label})");
            }
            // ...and the run-down's own maturity (a composite-only field) is what dated meeting 1,
            // so the decided period must be gone
            if (run.Rows.Any(r => r.Date == A_St0))
                msgs.Add("the period the RBA just decided is still on the board after the statement");
            if (!s.BlastText.Contains("*)"))
                msgs.Add("the blast does not mark the fixing as re-based");
            return msgs;
        });
        yield return spec;
    }

    // ================================================================ 35

    // THE RE-BASE FALLBACK, both legs, in one scenario:
    //   RBA  - the decided rung has no live mid on either spelling, but the SOURCE PAGE has
    //          closes, so the re-base takes that contract's last close BEFORE the decision day.
    //   BOC  - same, and the source page has NO history at all, so it must fall through to the
    //          COMPOSITE's closes (the audit case of 2026-08-26: a contributor page with no
    //          history left the re-base silently undone and overstated Priced by the whole
    //          delivered step for the entire decision-to-start week).
    private const double S35_A_Fix = 3.850;                 // RBACOR, pre-cut
    private const double S35_A_Pre1 = 3.560, S35_A_Post1 = 3.600;   // decided period, NO live mid
    private const double S35_A_2 = 3.500, S35_A_3 = 3.400, S35_A_4 = 3.350;
    private const double S35_B_Fix = 2.500;                 // CAONREPO, pre-hike
    private const double S35_B_Pre1 = 2.710, S35_B_Post1 = 2.750;   // decided period, NO live mid
    private const double S35_B_2 = 2.800, S35_B_3 = 2.870, S35_B_4 = 2.900;

    private static IEnumerable<ScenarioSpec> S35()
    {
        // ---- RBA: fall back to the SOURCE PAGE's own closes -------------------------------
        var a = BankA("RBA");
        a.Fix(S35_A_Fix).FixHist(Cal.D(-70), Cal.D(-1), S35_A_Fix);
        Fields(a, 0, A_S1, A_St0);
        Fields(a, 1, A_St0, A_St1);        // dates for the decided rung, but nobody prices it
        Fields(a, 2, A_St1, A_St2);
        Fields(a, 3, A_St2, A_St3);
        Fields(a, 4, A_St3, A_St4);
        Price(a, 0, S35_A_Fix, S35_A_Fix);
        Price(a, 2, S35_A_2, S35_A_2 - 0.040);
        Price(a, 3, S35_A_3, S35_A_3 - 0.040);
        Price(a, 4, S35_A_4, S35_A_4 - 0.040);
        a.Contract(A_S1, A_Bounds, Cal.D(-70), Cal.D(-1), S35_A_Fix, Spell.Active);
        a.ContractStep(A_St0, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S35_A_Pre1, S35_A_Post1, Spell.Active);
        a.ContractStep(A_St1, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S35_A_2 - 0.040, S35_A_2, Spell.Active);
        a.ContractStep(A_St2, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S35_A_3 - 0.040, S35_A_3, Spell.Active);
        a.ContractStep(A_St3, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S35_A_4 - 0.040, S35_A_4, Spell.Active);

        // ---- BOC: the source page has no tape, so the COMPOSITE closes must carry it -------
        var c = BankB("BOC");
        c.Fix(S35_B_Fix).FixHist(Cal.D(-70), Cal.D(-1), S35_B_Fix);
        Fields(c, 0, B_S1, B_St0);
        Fields(c, 1, B_St0, B_St1);
        Fields(c, 2, B_St1, B_St2);
        Fields(c, 3, B_St2, B_St3);
        Fields(c, 4, B_St3, B_St4);
        Price(c, 0, S35_B_Fix, S35_B_Fix);
        Price(c, 2, S35_B_2, S35_B_2 - 0.040);
        Price(c, 3, S35_B_3, S35_B_3 - 0.040);
        Price(c, 4, S35_B_4, S35_B_4 - 0.040);
        c.Contract(B_S1, B_Bounds, Cal.D(-70), Cal.D(-1), S35_B_Fix, Spell.Composite);
        c.ContractStep(B_St0, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S35_B_Pre1, S35_B_Post1, Spell.Composite);
        c.ContractStep(B_St1, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S35_B_2 - 0.040, S35_B_2, Spell.Composite);
        c.ContractStep(B_St2, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S35_B_3 - 0.040, S35_B_3, Spell.Composite);
        c.ContractStep(B_St3, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S35_B_4 - 0.040, S35_B_4, Spell.Composite);

        var spec = new ScenarioSpec
        {
            Id = 35,
            Name = "Nobody prices the decided rung - the re-base falls back to closes",
            Question = "With no live mid on the just-decided period, does Priced re-base onto " +
                       "that contract's last close BEFORE the decision - from the source page " +
                       "when it has one, from the composite when it does not?",
        };
        spec.Banks.Add(a);
        spec.Banks.Add(c);

        // ---- derivation -------------------------------------------------------------------
        // RBA. quotes[0] after the gate shift is the decided period's rung: it has a composite
        // MATURITY (so the board still knows the dates) but no Mid on either spelling, so the
        // re-base walks History.GetDaily("ADSF1A NABZ Curncy") and takes the LAST point strictly
        // before the decision day. The A_St0 contract sits on rung 1 from D(-42) to today, and
        // its close on D(-1) is the PRE-decision 3.560 - the post-decision 3.600 is today's and
        // is excluded by design (decision-day closes are unanchorable).
        //     re-based fixing = 3.560   (not 3.600, not RBACOR 3.850)
        //     row 1  D(+43)  (3.500 - 3.560) x 100 =  -6.0
        //     row 2  D(+85)  (3.400 - 3.560) x 100 = -16.0   Step = -10.0
        //     row 3  D(+127) (3.350 - 3.560) x 100 = -21.0   Step =  -5.0
        //     d1/w1/m1 = +4.0 on every row (each contract stepped +4bp on the decision)
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBA",
            Fixing = S35_A_Pre1, Rebased = true,
            Front = new FrontExpect(A_Dec1, A_St1, S35_A_2, S35_A_Pre1, -6.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(A_St1, A_St2, S35_A_2,  -6.0, null,  +4.0, +4.0, +4.0),
                new(A_St2, A_St3, S35_A_3, -16.0, -10.0, +4.0, +4.0, +4.0),
                new(A_St3, A_St4, S35_A_4, -21.0,  -5.0, +4.0, +4.0, +4.0),
            },
        });
        // BOC. Identical mechanism one fallback deeper: "CDSF1A BMOD Curncy" has no stored
        // history at all, so it drops to "CDSF1A Curncy". Its D(-1) close is 2.710.
        //     re-based fixing = 2.710   (not 2.750, not CAONREPO 2.500)
        //     row 1  D(+50)  (2.800 - 2.710) x 100 =  +9.0
        //     row 2  D(+99)  (2.870 - 2.710) x 100 = +16.0   Step = +7.0
        //     row 3  D(+148) (2.900 - 2.710) x 100 = +19.0   Step = +3.0
        // The change anchors take the same fallback (source series first, composite second), so
        //     d1/w1/m1 = +4.0 on every row.
        // If the fallback did NOT exist the base would stay CAONREPO 2.500 and row 1 would print
        // +30.0 - the full delivered hike booked twice, every day until the period starts.
        spec.Expect.Add(new BankExpect
        {
            Bank = "BOC",
            Fixing = S35_B_Pre1, Rebased = true,
            Front = new FrontExpect(B_Dec1, B_St1, S35_B_2, S35_B_Pre1, +9.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(B_St1, B_St2, S35_B_2,  +9.0, null,  +4.0, +4.0, +4.0),
                new(B_St2, B_St3, S35_B_3, +16.0, +7.0,  +4.0, +4.0, +4.0),
                new(B_St3, B_St4, S35_B_4, +19.0, +3.0,  +4.0, +4.0, +4.0),
            },
        });
        spec.NotesNotContain.Add("CHECK");
        spec.NotesNotContain.Add("STALE");
        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // the un-rebased base is the failure mode this exists to prevent - assert it is gone
            foreach (var (bank, stale) in new[] { ("RBA", S35_A_Fix), ("BOC", S35_B_Fix) })
            {
                var run = s.Run(bank)!;
                if (run.RefPct is { } rp && Math.Abs(rp - stale) < 1e-9)
                    msgs.Add($"{bank}: the base is still the stale o/n fixing {stale.ToString("0.000", Inv)} " +
                             "- the re-base fallback did not fire");
                if (!run.RefRebased)
                    msgs.Add($"{bank}: the base changed but is not marked re-based");
            }
            // ...and the DECISION-DAY close must never be the anchor
            var rba = s.Run("RBA")!;
            if (rba.RefPct is { } v && Math.Abs(v - S35_A_Post1) < 1e-9)
                msgs.Add("RBA: the re-base used TODAY's close (a decision-day close is unanchorable)");
            var boc = s.Run("BOC")!;
            if (boc.RefPct is { } w && Math.Abs(w - S35_B_Post1) < 1e-9)
                msgs.Add("BOC: the re-base used TODAY's close (a decision-day close is unanchorable)");
            return msgs;
        });
        yield return spec;
    }

    // ================================================================ 36

    private const double S36_N_Fix = 3.000;                                 // NZOCRS, pre-hike
    private const double S36_N_1 = 3.250, S36_N_2 = 3.350, S36_N_3 = 3.450; // rungs 1..3
    private const double S36_B_Fix = 2.500;                                 // CAONREPO, pre-hike
    private const double S36_B_1 = 2.750, S36_B_2 = 2.800, S36_B_3 = 2.870, S36_B_4 = 2.900;

    private static IEnumerable<ScenarioSpec> S36()
    {
        // ---- RBNZ: a far rung prints NO price, so the run stops there ---------------------
        var n = BankA("RBNZ");
        n.Fix(S36_N_Fix).FixHist(Cal.D(-70), Cal.D(-1), S36_N_Fix);
        Fields(n, 0, A_S1, A_St0);
        Fields(n, 1, A_St0, A_St1);
        Fields(n, 2, A_St1, A_St2);
        Fields(n, 3, A_St2, A_St3);
        Fields(n, 4, A_St3, A_St4);        // the composite still publishes the DATES here...
        Price(n, 0, S36_N_Fix, S36_N_Fix);
        Price(n, 1, S36_N_1, S36_N_1 - 0.040);
        Price(n, 2, S36_N_2, S36_N_2 - 0.040);
        Price(n, 3, S36_N_3, S36_N_3 - 0.040);
        //                                  ...but NOBODY prices rung 4 - the gap under test
        n.Contract(A_S1, A_Bounds, Cal.D(-70), Cal.D(-1), S36_N_Fix, Spell.Active);
        n.ContractStep(A_St0, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S36_N_1 - 0.040, S36_N_1, Spell.Active);
        n.ContractStep(A_St1, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S36_N_2 - 0.040, S36_N_2, Spell.Active);
        n.ContractStep(A_St2, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S36_N_3 - 0.040, S36_N_3, Spell.Active);
        n.ContractStep(A_St3, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, 3.500, 3.540, Spell.Active);

        // ---- BOC: the last quoted rung prices but publishes no MATURITY -------------------
        var c = BankB("BOC");
        c.Fix(S36_B_Fix).FixHist(Cal.D(-70), Cal.D(-1), S36_B_Fix);
        Fields(c, 0, B_S1, B_St0);
        Fields(c, 1, B_St0, B_St1);
        Fields(c, 2, B_St1, B_St2);
        Fields(c, 3, B_St2, B_St3);
        Fields(c, 4, B_St3, NoEnd);        // start documented, end NOT - the hard-data rule case
        Price(c, 0, S36_B_Fix, S36_B_Fix);
        Price(c, 1, S36_B_1, S36_B_1 - 0.040);
        Price(c, 2, S36_B_2, S36_B_2 - 0.040);
        Price(c, 3, S36_B_3, S36_B_3 - 0.040);
        Price(c, 4, S36_B_4, S36_B_4 - 0.040);
        c.Contract(B_S1, B_Bounds, Cal.D(-70), Cal.D(-1), S36_B_Fix, Spell.Active);
        c.ContractStep(B_St0, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S36_B_1 - 0.040, S36_B_1, Spell.Active);
        c.ContractStep(B_St1, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S36_B_2 - 0.040, S36_B_2, Spell.Active);
        c.ContractStep(B_St2, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S36_B_3 - 0.040, S36_B_3, Spell.Active);
        c.ContractStep(B_St3, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S36_B_4 - 0.040, S36_B_4, Spell.Active);

        var spec = new ScenarioSpec
        {
            Id = 36,
            Name = "A far rung goes unquoted on a decision day - the run truncates",
            Question = "When the documentation runs out mid-strip, does the run stop dead " +
                       "rather than invent a row, and does the last row's Maturity tell the " +
                       "truth on every surface?",
        };
        spec.Banks.Add(n);
        spec.Banks.Add(c);

        // ---- derivation -------------------------------------------------------------------
        // RBNZ. The gate drops the decided period; quotes[0] is its own OIS at 3.250, so
        //     re-based fixing = 3.250
        //     row 1  D(+43)  (3.350 - 3.250) x 100 = +10.0                 maturity D(+85)
        //     row 2  D(+85)  (3.450 - 3.250) x 100 = +20.0   Step = +10.0  maturity D(+127)
        // and then the run STOPS. D(+127) is a perfectly well documented start (the composite
        // publishes it) with a documented end D(+169) - but no security prices it, and the
        // hard-data rule forbids a curve-implied mid. Two rows, not three, and no row anywhere
        // dated D(+127).
        //     d1/w1/m1 = +4.0 on both rows.
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBNZ",
            Fixing = S36_N_1, Rebased = true,
            Front = new FrontExpect(A_Dec1, A_St1, S36_N_2, S36_N_1, +10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(A_St1, A_St2, S36_N_2, +10.0, null,  +4.0, +4.0, +4.0),
                new(A_St2, A_St3, S36_N_3, +20.0, +10.0, +4.0, +4.0, +4.0),
            },
        });
        // BOC. Every rung prices, but the deepest one publishes no MATURITY, so the meeting after
        // it is undocumented. Its row still publishes (its START came from the previous rung's
        // maturity) and its Maturity cell must be BLANK - never the config's next grid date.
        //     re-based fixing = 2.750
        //     row 1  D(+50)  (2.800 - 2.750) x 100 =  +5.0                 maturity D(+99)
        //     row 2  D(+99)  (2.870 - 2.750) x 100 = +12.0   Step = +7.0   maturity D(+148)
        //     row 3  D(+148) (2.900 - 2.750) x 100 = +15.0   Step = +3.0   maturity BLANK
        spec.Expect.Add(new BankExpect
        {
            Bank = "BOC",
            Fixing = S36_B_1, Rebased = true,
            Front = new FrontExpect(B_Dec1, B_St1, S36_B_2, S36_B_1, +5.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(B_St1, B_St2, S36_B_2,  +5.0, null, +4.0, +4.0, +4.0),
                new(B_St2, B_St3, S36_B_3, +12.0, +7.0, +4.0, +4.0, +4.0),
                new(B_St3, NoEnd, S36_B_4, +15.0, +3.0, +4.0, +4.0, +4.0),
            },
        });
        spec.NotesNotContain.Add("CHECK");
        spec.NotesNotContain.Add("STALE");
        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            string dropped = D(A_St3);
            var blast = Render.Blast(s.BlastText);
            var sheet = Render.Sheet(s.Xlsx);
            var mail = Render.Email(s.SheetHtml);

            // NO INVENTED ROW: nothing may START at the unpriced period. (The dropped date is
            // still legitimately reachable as the MATURITY of the last published row - the
            // period's end is documented even though its own rate is not - so this looks at
            // start cells only. First cut of this check scanned every cell and tripped on
            // exactly that maturity; the assertion was wrong, not the product.)
            foreach (var (name, blk) in new[]
                     { ("blast", blast.GetValueOrDefault("RBNZ")), ("workbook", sheet.GetValueOrDefault("RBNZ")),
                       ("sheet email", mail.GetValueOrDefault("RBNZ")) })
                if (blk != null && blk.Rows.Any(r => r.Length > 0 && Render.Norm(r[0]) == dropped))
                    msgs.Add($"the {name} carries a row STARTING {dropped} - nothing prices that period");
            if (s.WeeklyText.Replace("\r\n", "\n").Split('\n')
                .Any(l => Render.Norm(l).StartsWith(dropped)))
                msgs.Add($"the plaintext email carries a row starting {dropped}");
            if (s.Run("RBNZ")!.Rows.Any(r => r.Date == A_St3))
                msgs.Add($"the report itself publishes a row dated {dropped}");

            // every surface must agree on the SHORTENED run
            foreach (var (bank, want) in new[] { ("RBNZ", 2), ("BOC", 3) })
            {
                if (blast.TryGetValue(bank, out var bb) && bb.Rows.Count != want)
                    msgs.Add($"{bank}: the blast shows {bb.Rows.Count} rows, the run has {want}");
                if (sheet.TryGetValue(bank, out var bx) && bx.Rows.Count != want)
                    msgs.Add($"{bank}: the workbook shows {bx.Rows.Count} rows, the run has {want}");
                if (mail.TryGetValue(bank, out var bm) && bm.Rows.Count != want)
                    msgs.Add($"{bank}: the sheet email shows {bm.Rows.Count} rows, the run has {want}");
            }
            // the undocumented end must be BLANK on the attachment and the inline table, not
            // back-filled from config\meetings.json
            if (sheet.TryGetValue("BOC", out var bocX) && bocX.Rows.Count == 3
                && bocX.Rows[2].Length > 1 && bocX.Rows[2][1].Length > 0)
                msgs.Add($"BOC: the workbook prints a maturity '{bocX.Rows[2][1]}' on a row whose " +
                         "end no ticker documents");
            if (mail.TryGetValue("BOC", out var bocM) && bocM.Rows.Count == 3
                && bocM.Rows[2].Length > 1 && Render.Norm(bocM.Rows[2][1]).Length > 0)
                msgs.Add($"BOC: the sheet email prints a maturity '{Render.Norm(bocM.Rows[2][1])}' " +
                         "on a row whose end no ticker documents");
            return msgs;
        });
        yield return spec;
    }

    // ================================================================ 37

    private const double S37_A_Fix = 4.100;                                  // RBACOR, pre-cut
    private const double S37_A_1 = 3.850, S37_A_2 = 3.750, S37_A_3 = 3.650, S37_A_4 = 3.600;
    private const double S37_N_Fix = 3.000;                                  // NZOCRS, pre-hike
    private const double S37_N_1 = 3.250, S37_N_2 = 3.350, S37_N_3 = 3.420, S37_N_4 = 3.450;
    private const double StaleAge = 200.0;   // minutes; every other quote rides the 5m default

    private static IEnumerable<ScenarioSpec> S37()
    {
        // ---- RBA: the FRONT row's feed is the one that went quiet -------------------------
        var a = BankA("RBA");
        a.Fix(S37_A_Fix).FixHist(Cal.D(-70), Cal.D(-1), S37_A_Fix);
        Fields(a, 0, A_S1, A_St0);
        Fields(a, 1, A_St0, A_St1);
        Fields(a, 2, A_St1, A_St2);
        Fields(a, 3, A_St2, A_St3);
        Fields(a, 4, A_St3, A_St4);
        Price(a, 0, S37_A_Fix, S37_A_Fix);
        Price(a, 1, S37_A_1, S37_A_1 - 0.040);
        Price(a, 2, S37_A_2, S37_A_2 - 0.040, age: StaleAge);   // becomes published row 1
        Price(a, 3, S37_A_3, S37_A_3 - 0.040);
        Price(a, 4, S37_A_4, S37_A_4 - 0.040);
        a.Contract(A_S1, A_Bounds, Cal.D(-70), Cal.D(-1), S37_A_Fix, Spell.Active);
        a.ContractStep(A_St0, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S37_A_1 - 0.040, S37_A_1, Spell.Active);
        a.ContractStep(A_St1, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S37_A_2 - 0.040, S37_A_2, Spell.Active);
        a.ContractStep(A_St2, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S37_A_3 - 0.040, S37_A_3, Spell.Active);
        a.ContractStep(A_St3, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S37_A_4 - 0.040, S37_A_4, Spell.Active);

        // ---- RBNZ: an INTERIOR row's feed went quiet --------------------------------------
        var n = BankB("RBNZ");
        n.Fix(S37_N_Fix).FixHist(Cal.D(-70), Cal.D(-1), S37_N_Fix);
        Fields(n, 0, B_S1, B_St0);
        Fields(n, 1, B_St0, B_St1);
        Fields(n, 2, B_St1, B_St2);
        Fields(n, 3, B_St2, B_St3);
        Fields(n, 4, B_St3, B_St4);
        Price(n, 0, S37_N_Fix, S37_N_Fix);
        Price(n, 1, S37_N_1, S37_N_1 - 0.040);
        Price(n, 2, S37_N_2, S37_N_2 - 0.040);
        Price(n, 3, S37_N_3, S37_N_3 - 0.040, age: StaleAge);   // becomes published row 2
        Price(n, 4, S37_N_4, S37_N_4 - 0.040);
        n.Contract(B_S1, B_Bounds, Cal.D(-70), Cal.D(-1), S37_N_Fix, Spell.Active);
        n.ContractStep(B_St0, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S37_N_1 - 0.040, S37_N_1, Spell.Active);
        n.ContractStep(B_St1, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S37_N_2 - 0.040, S37_N_2, Spell.Active);
        n.ContractStep(B_St2, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S37_N_3 - 0.040, S37_N_3, Spell.Active);
        n.ContractStep(B_St3, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, S37_N_4 - 0.040, S37_N_4, Spell.Active);

        var spec = new ScenarioSpec
        {
            Id = 37,
            Name = "A published rung's feed is >1h quiet on decision day",
            Question = "Does the STALE watch fire on the right rung, name it, and say so loudly " +
                       "when the quiet feed is the FRONT - the number the whole desk reads?",
        };
        spec.Banks.Add(a);
        spec.Banks.Add(n);

        // ---- derivation -------------------------------------------------------------------
        // Staleness is judged as (age - the snapshot's 10th-percentile baseline). Every other
        // quote here carries the harness default 5m, so the baseline is 5 and the aged rung
        // reports 200 - 5 = 195m, comfortably over the 60m bar.
        //
        // RBA (cut). Gate drops the decided period; quotes[0] = its own OIS 3.850.
        //     re-based fixing = 3.850
        //     row 1  D(+43)  (3.750 - 3.850) x 100 = -10.0   <- the STALE rung, and the FRONT
        //     row 2  D(+85)  (3.650 - 3.850) x 100 = -20.0   Step = -10.0
        //     row 3  D(+127) (3.600 - 3.850) x 100 = -25.0   Step =  -5.0
        //     d1/w1/m1 = +4.0 on every row
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBA",
            Fixing = S37_A_1, Rebased = true,
            Front = new FrontExpect(A_Dec1, A_St1, S37_A_2, S37_A_1, -10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(A_St1, A_St2, S37_A_2, -10.0, null,  +4.0, +4.0, +4.0),
                new(A_St2, A_St3, S37_A_3, -20.0, -10.0, +4.0, +4.0, +4.0),
                new(A_St3, A_St4, S37_A_4, -25.0,  -5.0, +4.0, +4.0, +4.0),
            },
        });
        // RBNZ (hike). Same shape, the quiet rung is the SECOND published row.
        //     re-based fixing = 3.250
        //     row 1  D(+50)  (3.350 - 3.250) x 100 = +10.0
        //     row 2  D(+99)  (3.420 - 3.250) x 100 = +17.0   Step = +7.0   <- the STALE rung
        //     row 3  D(+148) (3.450 - 3.250) x 100 = +20.0   Step = +3.0
        //     d1/w1/m1 = +4.0 on every row
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBNZ",
            Fixing = S37_N_1, Rebased = true,
            Front = new FrontExpect(B_Dec1, B_St1, S37_N_2, S37_N_1, +10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(B_St1, B_St2, S37_N_2, +10.0, null, +4.0, +4.0, +4.0),
                new(B_St2, B_St3, S37_N_3, +17.0, +7.0, +4.0, +4.0, +4.0),
                new(B_St3, B_St4, S37_N_4, +20.0, +3.0, +4.0, +4.0, +4.0),
            },
        });
        spec.NotesContain.Add("STALE: RBA");
        spec.NotesContain.Add("STALE: RBNZ");
        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            string? rba = s.Notes.FirstOrDefault(x => x.StartsWith("STALE: RBA"));
            string? rbnz = s.Notes.FirstOrDefault(x => x.StartsWith("STALE: RBNZ"));
            if (rba == null) msgs.Add("no STALE note for the RBA at all");
            else
            {
                if (!rba.Contains("INCLUDING THE FRONT"))
                    msgs.Add("RBA's stale rung IS the front row and the note does not say so: " + rba);
                if (!rba.Contains(D(A_St1)))
                    msgs.Add($"RBA's stale note does not name the rung {D(A_St1)}: {rba}");
                if (!rba.Contains("195m"))
                    msgs.Add("RBA's stale note does not report the baseline-adjusted age 195m: " + rba);
                if (!rba.Contains("1 published rung(s)"))
                    msgs.Add("RBA's stale note does not count exactly one quiet rung: " + rba);
            }
            if (rbnz == null) msgs.Add("no STALE note for the RBNZ at all");
            else
            {
                if (rbnz.Contains("INCLUDING THE FRONT"))
                    msgs.Add("RBNZ's stale rung is INTERIOR but the note claims the front: " + rbnz);
                if (!rbnz.Contains(D(B_St2)))
                    msgs.Add($"RBNZ's stale note does not name the rung {D(B_St2)}: {rbnz}");
            }
            // the warning is non-blocking by design: the numbers must still publish
            if (s.Run("RBA")!.Rows.Count != 3 || s.Run("RBNZ")!.Rows.Count != 3)
                msgs.Add("a stale feed suppressed rows - the STALE note is a warning, not a filter");
            return msgs;
        });
        yield return spec;
    }

    // ================================================================ 38

    private const double S38_A_Fix = 3.850;                    // RBACOR, pre-cut
    private const double S38_A_1 = 3.600;                      // decided period
    private const double S38_A_2 = 3.500;                      // row 1
    private const double S38_A_Bad = 3.000;                    // row 2 - the MISPRINT as printed
    private const double S38_A_Mid = 3.450;                    // row 2 - the neighbour midpoint
    private const double S38_A_4 = 3.400;                      // row 3
    private const double S38_N_Fix = 3.000;                    // NZOCRS, pre-cut
    private const double S38_N_1 = 2.500;                      // decided period (50bp delivered)
    private const double S38_N_2 = 2.150;                      // row 1 - the FRONT, gapping hard
    private const double S38_N_3 = 2.520, S38_N_4 = 2.550;

    private static IEnumerable<ScenarioSpec> S38()
    {
        // ---- RBA: an interior rung prints an impossible rate ------------------------------
        var a = BankA("RBA");
        a.Fix(S38_A_Fix).FixHist(Cal.D(-70), Cal.D(-1), S38_A_Fix);
        Fields(a, 0, A_S1, A_St0);
        Fields(a, 1, A_St0, A_St1);
        Fields(a, 2, A_St1, A_St2);
        Fields(a, 3, A_St2, A_St3);
        Fields(a, 4, A_St3, A_St4);
        Price(a, 0, S38_A_Fix, S38_A_Fix);
        Price(a, 1, S38_A_1, S38_A_1 - 0.040);
        Price(a, 2, S38_A_2, S38_A_2 - 0.040);
        Price(a, 3, S38_A_Bad, S38_A_Mid - 0.040);   // the live print is the misprint
        Price(a, 4, S38_A_4, S38_A_4 - 0.040);
        // the TAPE is clean - only today's live print is broken, which is what a misprint is
        a.Contract(A_S1, A_Bounds, Cal.D(-70), Cal.D(-1), S38_A_Fix, Spell.Active);
        a.ContractStep(A_St0, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S38_A_1 - 0.040, S38_A_1, Spell.Active);
        a.ContractStep(A_St1, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S38_A_2 - 0.040, S38_A_2, Spell.Active);
        a.ContractStep(A_St2, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S38_A_Mid - 0.040, S38_A_Mid, Spell.Active);
        a.ContractStep(A_St3, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S38_A_4 - 0.040, S38_A_4, Spell.Active);

        // ---- RBNZ: the FRONT legitimately gaps, and must be left alone --------------------
        var n = BankB("RBNZ");
        n.Fix(S38_N_Fix).FixHist(Cal.D(-70), Cal.D(-1), S38_N_Fix);
        Fields(n, 0, B_S1, B_St0);
        Fields(n, 1, B_St0, B_St1);
        Fields(n, 2, B_St1, B_St2);
        Fields(n, 3, B_St2, B_St3);
        Fields(n, 4, B_St3, B_St4);
        Price(n, 0, S38_N_Fix, S38_N_Fix);
        Price(n, 1, S38_N_1, 2.460);
        Price(n, 2, S38_N_2, 2.480);
        Price(n, 3, S38_N_3, 2.490);
        Price(n, 4, S38_N_4, 2.510);
        // the pre-decision tape is ORDERLY (2.460 / 2.480 / 2.490 / 2.510); the V appears only
        // when the statement lands, so nothing in the lookback window is judged implausible
        n.Contract(B_S1, B_Bounds, Cal.D(-70), Cal.D(-1), S38_N_Fix, Spell.Active);
        n.ContractStep(B_St0, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, 2.460, S38_N_1, Spell.Active);
        n.ContractStep(B_St1, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, 2.480, S38_N_2, Spell.Active);
        n.ContractStep(B_St2, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, 2.490, S38_N_3, Spell.Active);
        n.ContractStep(B_St3, B_Bounds, Cal.D(-70), Cal.D(0), B_Dec0, 2.510, S38_N_4, Spell.Active);

        var spec = new ScenarioSpec
        {
            Id = 38,
            Name = "Neighbour misprint guard on a decision day - interior yes, FRONT never",
            Question = "Does an impossible interior print get replaced by the neighbour midpoint, " +
                       "flagged and noted - while a front row that legitimately gaps on the day " +
                       "is published exactly as it printed?",
        };
        spec.Banks.Add(a);
        spec.Banks.Add(n);

        // ---- derivation -------------------------------------------------------------------
        // RBA. After the gate shift the live rungs are 3.600 / 3.500 / 3.000 / 3.400. Row 2 is
        // interior, its neighbours are 3.500 and 3.400 (10bp apart, well inside the 25bp
        // agreement bar) and their midpoint is 3.450; the print is 45bp below it, so it is
        // rejected and replaced:
        //     published mid  = (3.500 + 3.400) / 2 = 3.450,  ticker -45.0bp off
        // Row 3 is NOT rejected: the guard judges it against the RAW neighbours 3.000 and rung 5
        // (unquoted), so it has no usable pair at all.
        //     re-based fixing = 3.600
        //     row 1  D(+43)  (3.500 - 3.600) x 100 = -10.0
        //     row 2  D(+85)  (3.450 - 3.600) x 100 = -15.0   Step = -5.0   <- synthesized
        //     row 3  D(+127) (3.400 - 3.600) x 100 = -20.0   Step = -5.0
        // The change columns ride the (clean) tape, which stepped +4bp on the decision, and the
        // synthesized row's published mid 3.450 sits 4bp above its own D(-1) anchor 3.410:
        //     d1/w1/m1 = +4.0 on every row.
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBA",
            Fixing = S38_A_1, Rebased = true,
            Front = new FrontExpect(A_Dec1, A_St1, S38_A_2, S38_A_1, -10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(A_St1, A_St2, S38_A_2,   -10.0, null, +4.0, +4.0, +4.0),
                // the rejected print is MASKED (desk 2026-08-27: "we should never have to invent
                // mids"): the row keeps its real 3.000 internally, publishes a label instead of a
                // number, and the step chain steps over it - so the NEXT row's Step is the
                // cumulative -20.0 - (-10.0) = -10.0, exactly as it is across a Y/E turn.
                new(A_St2, A_St3, 3.000, -60.0, null, null, null, null),
                new(A_St3, A_St4, S38_A_4,   -20.0, -10.0, +4.0, +4.0, +4.0),
            },
        });
        // RBNZ. A 50bp cut lands and the market prices a one-meeting undershoot: the live rungs
        // are 2.500 / 2.150 / 2.520 / 2.550. Note what that is - the FRONT sits 36bp below the
        // midpoint of its two neighbours (2.500 and 2.520, only 2bp apart). Every ingredient the
        // guard tests for is present. It must NOT fire: the edge row is never judged, because the
        // front meeting is the one that gaps for real, and "fixing" it would replace the
        // most-read number on the board with 2.510 - a 36bp fabrication.
        //     re-based fixing = 2.500
        //     row 1  D(+50)  (2.150 - 2.500) x 100 = -35.0
        //     row 2  D(+99)  (2.520 - 2.500) x 100 =  +2.0   Step = +37.0
        //     row 3  D(+148) (2.550 - 2.500) x 100 =  +5.0   Step =  +3.0
        //     d1/w1/m1: the front repriced from 2.480 to 2.150 => (2.150 - 2.480) x 100 = -33.0
        //               row 2   (2.520 - 2.490) x 100 = +3.0
        //               row 3   (2.550 - 2.510) x 100 = +4.0
        // A 33bp one-day move clears OutlierGuard's 12bp/30bp absolute bars, so this run also
        // earns CHECK notes on the front's d1 and w1 - correct, and worth the desk knowing that
        // any surprise decision day will produce them.
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBNZ",
            Fixing = S38_N_1, Rebased = true,
            Front = new FrontExpect(B_Dec1, B_St1, S38_N_2, S38_N_1, -35.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(B_St1, B_St2, S38_N_2, -35.0, null,  -33.0, -33.0, -33.0),
                new(B_St2, B_St3, S38_N_3,  +2.0, +37.0,  +3.0,  +3.0,  +3.0),
                new(B_St3, B_St4, S38_N_4,  +5.0,  +3.0,  +4.0,  +4.0,  +4.0),
            },
        });
        spec.NotesContain.Add($"CHECK: RBA {D(A_St2)} publishes NO mid");
        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var rba = s.Run("RBA")!;
            var rbnz = s.Run("RBNZ")!;

            // the rejected row publishes NO number, on any surface
            if (rba.Rows.Count > 1 && !rba.Rows[1].Rejected)
                msgs.Add($"RBA {D(A_St2)}: the impossible print was not rejected");
            if (rba.Rows.Count > 1 && rba.Rows[1].MaskLabel.Length == 0)
                msgs.Add($"RBA {D(A_St2)}: a rejected row must publish a label in place of its mid");
            foreach (var (name, text) in new[]
                     { ("sheet email", s.SheetHtml), ("card email", s.WeeklyHtml),
                       ("chat blast", s.BlastText), ("plaintext email", s.WeeklyText) })
                if (text.Contains("3.450"))
                    msgs.Add($"the {name} publishes 3.450 - the neighbour midpoint. The app must " +
                             "never invent a mid; the row is meant to publish a label.");
            if (s.Xlsx.Any(r => r.Any(c => c.Contains("3.450"))))
                msgs.Add("the xlsx attachment publishes 3.450 - the neighbour midpoint");

            // ...and the FRONT row, which legitimately gaps on a decision day, is untouched
            if (rbnz.Rows.Count > 0 && rbnz.Rows[0].Rejected)
                msgs.Add("the guard rejected the FRONT row - it is meant to be exempt, because the " +
                         "front meeting is the one that gaps for real");
            if (rbnz.Rows.Count > 0 && Math.Abs(rbnz.Rows[0].MidPct - 2.150) > 1e-9)
                msgs.Add($"the RBNZ front publishes {rbnz.Rows[0].MidPct:0.000}, not its real 2.150 print");
            return msgs;
        });
        yield return spec;
    }

    // ================================================================ 39

    private const double S39_M_0 = 4.000;                                    // MPC run-down
    private const double S39_M_1 = 3.750, S39_M_2 = 3.650, S39_M_3 = 3.550, S39_M_4 = 3.500;
    private const double S39_A_0 = 3.850;
    private const double S39_A_1 = 3.600, S39_A_2 = 3.500, S39_A_3 = 3.400, S39_A_4 = 3.350;

    private static IEnumerable<ScenarioSpec> S39()
    {
        // ---- MPC: same-day start, so NOTHING can stand in for the missing fixing ----------
        var m = new BankSpec { Bank = "MPC", DecisionTimeLondon = Cal.TimePassed };
        m.Dates.AddRange(new[] { M_P2, M_P1, M_D0, M_D1, M_D2, M_D3 });
        m.DecisionDates.AddRange(new[] { M_D0, M_D1, M_D2, M_D3 });
        // NO m.Fix(...): SONIO/N is unquoted. Its HISTORY is still there - the outage is live.
        m.FixHist(Cal.D(-70), Cal.D(-1), S39_M_0);
        m.Quote(0, mid: S39_M_0, prevClose: S39_M_0, eff: M_P1, mat: M_D0);
        m.Quote(1, mid: S39_M_1, prevClose: S39_M_1 - 0.040, eff: M_D0, mat: M_D1);
        m.Quote(2, mid: S39_M_2, prevClose: S39_M_2 - 0.040, eff: M_D1, mat: M_D2);
        m.Quote(3, mid: S39_M_3, prevClose: S39_M_3 - 0.040, eff: M_D2, mat: M_D3);
        m.Quote(4, mid: S39_M_4, prevClose: S39_M_4 - 0.040, eff: M_D3, mat: M_D4);
        m.Contract(M_P1, M_Bounds, Cal.D(-70), Cal.D(-1), S39_M_0);
        m.ContractStep(M_D0, M_Bounds, Cal.D(-70), Cal.D(0), M_D0, S39_M_1 - 0.040, S39_M_1);
        m.ContractStep(M_D1, M_Bounds, Cal.D(-70), Cal.D(0), M_D0, S39_M_2 - 0.040, S39_M_2);
        m.ContractStep(M_D2, M_Bounds, Cal.D(-70), Cal.D(0), M_D0, S39_M_3 - 0.040, S39_M_3);
        m.ContractStep(M_D3, M_Bounds, Cal.D(-70), Cal.D(0), M_D0, S39_M_4 - 0.040, S39_M_4);

        // ---- RBA: lagged start, so the re-base rescues the column -------------------------
        var a = BankA("RBA");
        // NO a.Fix(...) either - RBACOR is out on the same day
        a.FixHist(Cal.D(-70), Cal.D(-1), S39_A_0);
        Fields(a, 0, A_S1, A_St0);
        Fields(a, 1, A_St0, A_St1);
        Fields(a, 2, A_St1, A_St2);
        Fields(a, 3, A_St2, A_St3);
        Fields(a, 4, A_St3, A_St4);
        Price(a, 0, S39_A_0, S39_A_0);
        Price(a, 1, S39_A_1, S39_A_1 - 0.040);
        Price(a, 2, S39_A_2, S39_A_2 - 0.040);
        Price(a, 3, S39_A_3, S39_A_3 - 0.040);
        Price(a, 4, S39_A_4, S39_A_4 - 0.040);
        a.Contract(A_S1, A_Bounds, Cal.D(-70), Cal.D(-1), S39_A_0, Spell.Active);
        a.ContractStep(A_St0, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S39_A_1 - 0.040, S39_A_1, Spell.Active);
        a.ContractStep(A_St1, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S39_A_2 - 0.040, S39_A_2, Spell.Active);
        a.ContractStep(A_St2, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S39_A_3 - 0.040, S39_A_3, Spell.Active);
        a.ContractStep(A_St3, A_Bounds, Cal.D(-70), Cal.D(0), A_Dec0, S39_A_4 - 0.040, S39_A_4, Spell.Active);

        var spec = new ScenarioSpec
        {
            Id = 39,
            Name = "The o/n fixing ticker is unquoted on a decision day",
            Question = "With no fixing print, do Priced, Step and '% of 25bp' go BLANK on every " +
                       "surface instead of being invented - while the mids still publish?",
        };
        spec.Banks.Add(m);
        spec.Banks.Add(a);

        // ---- derivation -------------------------------------------------------------------
        // MPC. The period the MPC just decided BEGINS today, so the announced-but-not-yet-
        // effective re-base cannot apply (it is gated on today < period start). With SONIO/N
        // unquoted there is no base at all:
        //     fixing  = blank
        //     Priced  = blank on every row  (Priced = (mid - fixing) x 100 and there is no fixing)
        //     Step    = blank on every row  (Step differences Priced)
        //     % 25bp  = blank on the front line
        // The MIDS are real prints and must still publish, and so must the change columns, which
        // are mid-vs-mid and need no fixing at all:
        //     row 1  D(+42)  3.650   d1/w1/m1 = (3.650 - 3.610) x 100 = +4.0
        //     row 2  D(+84)  3.550   d1/w1/m1 = +4.0
        //     row 3  D(+126) 3.500   d1/w1/m1 = +4.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "MPC",
            // FIXED 2026-08-27: a dark o/n ticker no longer costs the whole Priced column. The
            // re-base supplies the decided period's own OIS (3.750) as the base, and every surface
            // marks it "(rebased)" so nobody reads it as a printed SONIA.
            Fixing = 3.750, Rebased = true,
            Front = new FrontExpect(M_D1, M_D1, S39_M_2, 3.750, -10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(M_D1, M_D2, S39_M_2, -10.0, null, +4.0, +4.0, +4.0),
                new(M_D2, M_D3, S39_M_3, -20.0, -10.0, +4.0, +4.0, +4.0),
                new(M_D3, M_D4, S39_M_4, -25.0, -5.0, +4.0, +4.0, +4.0),
            },
        });
        // RBA. Same outage, lagged start: the decided period does not begin until D(+1), so the
        // re-base fires and takes that period's own live OIS. The column survives the outage.
        //     re-based fixing = 3.600
        //     row 1  D(+43)  (3.500 - 3.600) x 100 = -10.0
        //     row 2  D(+85)  (3.400 - 3.600) x 100 = -20.0   Step = -10.0
        //     row 3  D(+127) (3.350 - 3.600) x 100 = -25.0   Step =  -5.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBA",
            Fixing = S39_A_1, Rebased = true,
            Front = new FrontExpect(A_Dec1, A_St1, S39_A_2, S39_A_1, -10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(A_St1, A_St2, S39_A_2, -10.0, null,  +4.0, +4.0, +4.0),
                new(A_St2, A_St3, S39_A_3, -20.0, -10.0, +4.0, +4.0, +4.0),
                new(A_St3, A_St4, S39_A_4, -25.0,  -5.0, +4.0, +4.0, +4.0),
            },
        });
        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var blast = Render.Blast(s.BlastText);
            var sheet = Render.Sheet(s.Xlsx);
            var mail = Render.Email(s.SheetHtml);

            // the base is a swap mid, not a printed fixing - every surface must SAY so
            foreach (var (name, blk2) in new[]
                     { ("blast", blast.GetValueOrDefault("MPC")), ("workbook", sheet.GetValueOrDefault("MPC")),
                       ("sheet email", mail.GetValueOrDefault("MPC")) })
            {
                if (blk2 == null) { msgs.Add($"MPC is missing from the {name}"); continue; }
                if (!blk2.Rebased)
                    msgs.Add($"the {name} prints a fixing of '{blk2.FixingValue}' with no re-based " +
                             "marker, although SONIA is unquoted and the number is a swap mid");
            }
            return msgs;
        });
        yield return spec;
    }
}
