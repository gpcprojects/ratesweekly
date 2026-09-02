using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE WHOLE EMAIL ON A DECISION DAY - the report the desk actually sends, not one run
/// in isolation.
///
/// Everything here is derived by hand from the synthetic market, on the same discipline as
/// Group00/01:
///   Priced = (mid - published fixing) * 100
///   Step   = Priced - the previous CLEAN Priced (a Y/E turn row is masked and skipped)
///   d1/w1/m1 = (live mid - the SAME CONTRACT's mark then) * 100, read off the stitched series,
///   so a quiet contract on a stepping rung must print 0.0 and never the inter-contract gap.
///
/// The calendar shapes in play and what each does on the day:
///   lag 0   (FOMC, MPC)                decision day IS the period start - no re-base possible
///   lag 1   (RBA, RBNZ, BOC, NORGES)   a one-day re-base window
///   lag 6   (ECB, BOJ, RIKSBANK here)  a six-day re-base window
///   rollsAtPeriodStart (RIKSBANK)      history renumbers at the START, while the BOARD still
///                                      rolls on the announcement clock</summary>
public static class Group09_WholeEmail
{
    private static readonly DateTime H0 = Cal.D(-70);      // history start, business days only

    // ------------------------------------------------------------------ shared builders

    /// <summary>A bank with NO decision anywhere near today: a perfectly quiet tape (every
    /// CONTRACT flat over the whole window) on a stepping rung number. Every change column must
    /// therefore print 0.0 - any non-zero is the inter-contract gap leaking through a roll.
    ///
    /// <para>Geometry: decisions at n1, n1+sp, n1+2sp, n1+3sp with the two settled ones at
    /// n1-sp and n1-2sp; the swap period each governs starts <paramref name="lag"/> days later.
    /// Config keeps SETTLED starts in "dates" (the loader migrates them into pastDates and
    /// derives their announcements as start-lag) and FUTURE announcements in "decisionDates",
    /// exactly as the shipped file does.</para>
    ///
    /// <para>Levels: rung k quotes <paramref name="m1"/> + (k-1)*<paramref name="d"/>, with d
    /// always 5-15bp so a mis-rung read lands visibly off.</para></summary>
    private static (BankSpec B, DateTime[] Dec, DateTime[] St) Quiet(
        string name, int lag, int n1, int sp, double fix, double m1, double d,
        bool rollsAtPeriodStart = false)
    {
        var dec = new[] { Cal.D(n1), Cal.D(n1 + sp), Cal.D(n1 + 2 * sp), Cal.D(n1 + 3 * sp) };
        var pdec = new[] { Cal.D(n1 - 2 * sp), Cal.D(n1 - sp) };
        var st = dec.Select(x => x.AddDays(lag)).ToArray();
        var pst = pdec.Select(x => x.AddDays(lag)).ToArray();
        // the dates the family RENUMBERS on: announcements everywhere but the Riksbank
        var bounds = rollsAtPeriodStart ? pst.Concat(st).ToArray() : pdec.Concat(dec).ToArray();
        var mids = new[] { m1, m1 + d, m1 + 2 * d, m1 + 3 * d };

        var b = new BankSpec { Bank = name };
        b.Dates.AddRange(pst);
        b.Dates.AddRange(st);
        b.DecisionDates.AddRange(dec);
        b.Fix(fix).FixHist(H0, Cal.D(-1), fix);

        // rung 0 = the run-down that matures at the next period start; 1..3 the meeting periods
        b.Quote(0, mid: fix, prevClose: fix, eff: pst[1], mat: st[0]);
        for (int i = 0; i < 3; i++)
            b.Quote(i + 1, mid: mids[i], prevClose: mids[i], eff: st[i], mat: st[i + 1]);

        // history, CONTRACT by contract - flat rates, stepping ticker numbers
        b.Contract(pst[1], bounds, H0, Cal.D(-1), fix);
        for (int i = 0; i < 4; i++) b.Contract(st[i], bounds, H0, Cal.D(-1), mids[i]);
        return (b, dec, st);
    }

    /// <summary>The rendered CB front table, cell by cell: Bank / Decision / Start / OIS Mid /
    /// Fixing / Priced / % 25bp.</summary>
    private static List<string[]> Front(Surfaces s) => Render.EmailFront(s.SheetHtml);

    private static string D(DateTime d) => d.ToString("dd-MMM-yy",
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Assert the front table's ROW ORDER by bank name - the sort key is
    /// (decision ?? start), which is not the same as the start order.</summary>
    private static IEnumerable<string> FrontOrder(Surfaces s, params string[] banks)
    {
        var got = s.Report.Fronts.Select(f => f.Bank).ToList();
        if (!got.SequenceEqual(banks))
            yield return $"front table order is [{string.Join(", ", got)}], expected " +
                         $"[{string.Join(", ", banks)}]";
        var rendered = Front(s).Select(r => Render.Norm(r[0]).Split(' ')[0]).ToList();
        if (!rendered.SequenceEqual(banks))
            yield return $"RENDERED front order is [{string.Join(", ", rendered)}], expected " +
                         $"[{string.Join(", ", banks)}]";
    }

    /// <summary>Assert one rendered front line's Decision cell and its "% 25bp" cell.</summary>
    private static IEnumerable<string> FrontCells(Surfaces s, string bank, string decisionCell,
        string pctCell)
    {
        var row = Front(s).FirstOrDefault(r => Render.Norm(r[0]).StartsWith(bank + " "));
        if (row == null) { yield return $"{bank}: no rendered front line"; yield break; }
        if (Render.Norm(row[1]) != decisionCell)
            yield return $"{bank} front Decision cell '{Render.Norm(row[1])}' != '{decisionCell}'";
        if (Render.Norm(row[6]) != pctCell)
            yield return $"{bank} front % 25bp cell '{Render.Norm(row[6])}' != '{pctCell}'";
    }

    public static IEnumerable<ScenarioSpec> All()
    {
        foreach (var s in Realistic()) yield return s;
        foreach (var s in TwoOnOneDay()) yield return s;
        foreach (var s in EmptyCalendar()) yield return s;
        foreach (var s in OrderAndSigns()) yield return s;
        foreach (var s in EverySurface()) yield return s;
    }

    // ================================================================== 46

    /// <summary>All nine banks, ONE of them (the ECB) cutting today. The other eight are quiet
    /// tapes whose rows, prices and (zero) changes must be exactly what they would be with no
    /// decision anywhere in the report - a decision is a per-schedule event and must not leak.</summary>
    private static IEnumerable<ScenarioSpec> Realistic()
    {
        // ---- the eight bystanders (name, lag, next decision, spacing, fixing, front mid, step)
        var mpc = Quiet("MPC", 0, 12, 42, 4.000, 3.900, -0.100);
        var rba = Quiet("RBA", 1, 15, 42, 3.600, 3.650, +0.050);
        var rbnz = Quiet("RBNZ", 1, 18, 42, 2.750, 2.700, -0.050);
        var fomc = Quiet("FOMC", 0, 21, 42, 3.900, 3.780, -0.120);
        var boc = Quiet("BOC", 1, 24, 42, 2.250, 2.325, +0.075);
        var norges = Quiet("NORGES", 1, 27, 42, 4.250, 4.180, -0.070);
        var boj = Quiet("BOJ", 6, 20, 42, 0.500, 0.560, +0.060);
        // a 30-day Riksbank spacing keeps every published period inside 2026, so no Y/E turn row
        // muddies the control (the turn is exercised on its own in scenario 50)
        var riks = Quiet("RIKSBANK", 6, 8, 30, 1.900, 1.815, -0.085, rollsAtPeriodStart: true);

        // ---- the ECB, cutting 25bp today, period starts in 6 days, feed NOT re-pointed -------
        var eDec = new[] { Cal.D(0), Cal.D(49), Cal.D(98), Cal.D(147) };
        var eSt = new[] { Cal.D(6), Cal.D(55), Cal.D(104), Cal.D(153) };
        var eSt4 = Cal.D(202);
        var ePastSt = new[] { Cal.D(-92), Cal.D(-50) };
        // renumber boundaries = the ANNOUNCEMENTS (past ones derived by the loader as start - 6)
        var eBounds = new[] { Cal.D(-98), Cal.D(-56), eDec[0], eDec[1], eDec[2], eDec[3] };

        const double eFix = 2.500;                    // ESTRON - still the PRE-cut rate all day
        // a fully-anticipated cut: each meeting contract slips exactly 1bp on the day
        const double ePre0 = 2.260, ePost0 = 2.250;   // the period the decision governs
        const double ePre1 = 2.210, ePost1 = 2.200;
        const double ePre2 = 2.160, ePost2 = 2.150;
        const double ePre3 = 2.110, ePost3 = 2.100;

        var ecb = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        ecb.Dates.AddRange(ePastSt);
        ecb.Dates.AddRange(eSt);
        ecb.DecisionDates.AddRange(eDec);
        ecb.Fix(eFix).FixHist(H0, Cal.D(-1), eFix);
        ecb.Quote(0, mid: eFix, prevClose: eFix, eff: ePastSt[1], mat: eSt[0]);
        ecb.Quote(1, mid: ePost0, prevClose: ePre0, eff: eSt[0], mat: eSt[1]);
        ecb.Quote(2, mid: ePost1, prevClose: ePre1, eff: eSt[1], mat: eSt[2]);
        ecb.Quote(3, mid: ePost2, prevClose: ePre2, eff: eSt[2], mat: eSt[3]);
        ecb.Quote(4, mid: ePost3, prevClose: ePre3, eff: eSt[3], mat: eSt4);
        ecb.Contract(ePastSt[1], eBounds, H0, Cal.D(-1), eFix);
        ecb.ContractStep(eSt[0], eBounds, H0, Cal.D(0), eDec[0], ePre0, ePost0);
        ecb.ContractStep(eSt[1], eBounds, H0, Cal.D(0), eDec[0], ePre1, ePost1);
        ecb.ContractStep(eSt[2], eBounds, H0, Cal.D(0), eDec[0], ePre2, ePost2);
        ecb.ContractStep(eSt[3], eBounds, H0, Cal.D(0), eDec[0], ePre3, ePost3);

        var spec = new ScenarioSpec
        {
            Id = 46,
            Name = "Nine banks, ONE decides today (ECB cuts) - the report as sent",
            Question = "On the day one bank moves, does the whole report still hold: the decider " +
                       "rolls and re-bases, and the other eight are untouched, row for row?",
        };
        foreach (var b in new[] { mpc.B, rba.B, rbnz.B, fomc.B, boc.B, norges.B, boj.B, riks.B, ecb })
            spec.Banks.Add(b);

        // ---------------- the eight bystanders: flat contracts, so every change is 0.0 --------
        // Priced = (mid - fixing) * 100; Step = the ladder increment.
        void QuietExpect((BankSpec B, DateTime[] Dec, DateTime[] St) q, double fix,
            double m1, double d, double pct25)
        {
            double p1 = (m1 - fix) * 100.0;
            double p2 = (m1 + d - fix) * 100.0;
            double p3 = (m1 + 2 * d - fix) * 100.0;
            spec.Expect.Add(new BankExpect
            {
                Bank = q.B.Bank,
                Fixing = fix,
                Rebased = false,
                Front = new FrontExpect(q.Dec[0], q.St[0], m1, fix, p1, Rebased: false),
                Rows = new List<RowExpect>
                {
                    new(q.St[0], q.St[1], m1,         p1, null,      0.0, 0.0, 0.0),
                    new(q.St[1], q.St[2], m1 + d,     p2, d * 100.0, 0.0, 0.0, 0.0),
                    new(q.St[2], q.St[3], m1 + 2 * d, p3, d * 100.0, 0.0, 0.0, 0.0),
                },
            });
            spec.Custom.Add(s => FrontCells(s, q.B.Bank, D(q.Dec[0]),
                pct25.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture) + "%"));
        }

        // MPC    4.000 fixing, 3.900/3.800/3.700 -> Priced -10.0/-20.0/-30.0, Step -10.0
        QuietExpect(mpc, 4.000, 3.900, -0.100, -40);      // -10.0 / 25 = -40%
        // RBA    3.600 fixing, 3.650/3.700/3.750 -> Priced  +5.0/+10.0/+15.0, Step +5.0
        QuietExpect(rba, 3.600, 3.650, +0.050, +20);
        // RBNZ   2.750 fixing, 2.700/2.650/2.600 -> Priced  -5.0/-10.0/-15.0, Step -5.0
        QuietExpect(rbnz, 2.750, 2.700, -0.050, -20);
        // FOMC   3.900 fixing, 3.780/3.660/3.540 -> Priced -12.0/-24.0/-36.0, Step -12.0
        QuietExpect(fomc, 3.900, 3.780, -0.120, -48);
        // BOC    2.250 fixing, 2.325/2.400/2.475 -> Priced  +7.5/+15.0/+22.5, Step +7.5
        QuietExpect(boc, 2.250, 2.325, +0.075, +30);
        // NORGES 4.250 fixing, 4.180/4.110/4.040 -> Priced  -7.0/-14.0/-21.0, Step -7.0
        QuietExpect(norges, 4.250, 4.180, -0.070, -28);
        // BOJ    0.500 fixing, 0.560/0.620/0.680 -> Priced  +6.0/+12.0/+18.0, Step +6.0
        QuietExpect(boj, 0.500, 0.560, +0.060, +24);
        // RIKS   1.900 fixing, 1.815/1.730/1.645 -> Priced  -8.5/-17.0/-25.5, Step -8.5
        QuietExpect(riks, 1.900, 1.815, -0.085, -34);

        // ---------------- the decider --------------------------------------------------------
        // The gate rolls the just-decided period (starts D+6) off the board the moment the
        // statement is out, and Priced re-bases onto THAT period's own OIS (2.250) because
        // ESTRON still prints 2.500 for six more days.
        //   row 1  (2.200 - 2.250) * 100 = -5.0     Step: first row, blank
        //   row 2  (2.150 - 2.250) * 100 = -10.0    Step -10.0 - (-5.0)  = -5.0
        //   row 3  (2.100 - 2.250) * 100 = -15.0    Step -15.0 - (-10.0) = -5.0
        //   d: every contract slipped 0.010 today => -1.0bp on 1d, 1w and 1m alike
        spec.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = ePost0,
            Rebased = true,
            Front = new FrontExpect(eDec[1], eSt[1], ePost1, ePost0, -5.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(eSt[1], eSt[2], ePost1,  -5.0, null, -1.0, -1.0, -1.0),
                new(eSt[2], eSt[3], ePost2, -10.0, -5.0, -1.0, -1.0, -1.0),
                new(eSt[3], eSt4,   ePost3, -15.0, -5.0, -1.0, -1.0, -1.0),
            },
        });
        spec.Custom.Add(s => FrontCells(s, "ECB", D(eDec[1]), "-20%"));   // -5.0 / 25 = -20%

        // sorted by (decision ?? start): RIKS 8, MPC 12, RBA 15, RBNZ 18, BOJ 20, FOMC 21,
        // BOC 24, NORGES 27, ECB 49 (its OWN next decision, the decided one having rolled off)
        spec.Custom.Add(s => FrontOrder(s, "RIKSBANK", "MPC", "RBA", "RBNZ", "BOJ", "FOMC",
            "BOC", "NORGES", "ECB"));

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // the decided period must be gone from EVERY surface
            if (s.Run("ECB")!.Rows.Any(r => r.Date == eSt[0]))
                msgs.Add("the period the ECB just decided is still on the board after the statement");
            var blk = Render.Blast(s.BlastText).GetValueOrDefault("ECB");
            if (blk != null && blk.Rows.Any(r => r[0] == D(eSt[0])))
                msgs.Add("the blast still carries the just-decided ECB period");
            // exactly ONE bank may be re-based, and exactly one dagger belongs in the front table
            var reb = s.Report.Fronts.Where(f => f.RefRebased).Select(f => f.Bank).ToList();
            if (!reb.SequenceEqual(new[] { "ECB" }))
                msgs.Add($"re-based front lines are [{string.Join(", ", reb)}], expected [ECB]");
            // the * convention (desk 2026-09-02): the front-table fixing cell and the ECB
            // runs-block fixing cell are the two starred italics; the disclaimer rides once
            // under each table
            int stars = s.SheetHtml.Split("*</i>").Length - 1;
            if (stars != 2)
                msgs.Add($"the sheet email carries {stars} starred fixing cell(s), expected 2 " +
                         "(the ECB front cell and its runs-block cell)");
            if (!s.SheetHtml.Contains("has been adjusted to reflect hike/cut"))
                msgs.Add("the * disclaimer is missing from the sheet email");
            // every one of the nine must reach every daily surface
            if (s.Report.Runs.Count != 9) msgs.Add($"{s.Report.Runs.Count} runs published, expected 9");
            if (s.Report.Fronts.Count != 9) msgs.Add($"{s.Report.Fronts.Count} front lines, expected 9");
            var blast = Render.Blast(s.BlastText);
            foreach (var name in new[] { "ECB", "MPC", "RBA", "RBNZ", "FOMC", "BOC", "NORGES",
                                         "BOJ", "RIKSBANK" })
                if (!blast.ContainsKey(name)) msgs.Add($"{name} is missing from the blast");
            // the eight bystanders must be un-re-based and carry no rebased marker anywhere
            foreach (var name in new[] { "MPC", "RBA", "RBNZ", "FOMC", "BOC", "NORGES",
                                         "BOJ", "RIKSBANK" })
                if (blast.TryGetValue(name, out var b2) && b2.Rebased)
                    msgs.Add($"{name} blast block claims a rebased fixing on someone else's decision day");
            return msgs;
        });

        spec.NotesNotContain.Add("CHECK");
        spec.NotesNotContain.Add("FUTURES GUARD");
        spec.NotesNotContain.Add("STALE");
        yield return spec;
    }

    // ================================================================== 47

    /// <summary>Norges and the Riksbank announce on the SAME day - the real Nordic calendar. One
    /// is a 1-day-lag family that renumbers at the announcement, the other a 6-day-lag family
    /// that renumbers at the PERIOD START (rollsAtPeriodStart) while its BOARD still rolls on the
    /// announcement clock. Both must roll and re-base; a quiet FOMC proves neither leaks.</summary>
    private static IEnumerable<ScenarioSpec> TwoOnOneDay()
    {
        // ---- NORGES: hikes 25bp today, the period it decided starts TOMORROW -----------------
        var nDec = new[] { Cal.D(0), Cal.D(42), Cal.D(84), Cal.D(126) };
        var nSt = new[] { Cal.D(1), Cal.D(43), Cal.D(85), Cal.D(127) };
        var nSt4 = Cal.D(169);
        var nPastSt = new[] { Cal.D(-83), Cal.D(-41) };
        var nBounds = new[] { Cal.D(-84), Cal.D(-42), nDec[0], nDec[1], nDec[2], nDec[3] };

        const double nFix = 4.000;                                    // NOWA, still pre-hike
        const double nPre0 = 4.230, nPost0 = 4.250;                   // the just-decided period
        const double nPre1 = 4.290, nPost1 = 4.310;
        const double nPre2 = 4.340, nPost2 = 4.360;
        const double nPre3 = 4.380, nPost3 = 4.400;

        var nor = new BankSpec { Bank = "NORGES", DecisionTimeLondon = Cal.TimePassed };
        nor.Dates.AddRange(nPastSt); nor.Dates.AddRange(nSt);
        nor.DecisionDates.AddRange(nDec);
        nor.Fix(nFix).FixHist(H0, Cal.D(-1), nFix);
        nor.Quote(0, mid: nFix, prevClose: nFix, eff: nPastSt[1], mat: nSt[0]);
        nor.Quote(1, mid: nPost0, prevClose: nPre0, eff: nSt[0], mat: nSt[1]);
        nor.Quote(2, mid: nPost1, prevClose: nPre1, eff: nSt[1], mat: nSt[2]);
        nor.Quote(3, mid: nPost2, prevClose: nPre2, eff: nSt[2], mat: nSt[3]);
        nor.Quote(4, mid: nPost3, prevClose: nPre3, eff: nSt[3], mat: nSt4);
        nor.Contract(nPastSt[1], nBounds, H0, Cal.D(-1), nFix);
        nor.ContractStep(nSt[0], nBounds, H0, Cal.D(0), nDec[0], nPre0, nPost0);
        nor.ContractStep(nSt[1], nBounds, H0, Cal.D(0), nDec[0], nPre1, nPost1);
        nor.ContractStep(nSt[2], nBounds, H0, Cal.D(0), nDec[0], nPre2, nPost2);
        nor.ContractStep(nSt[3], nBounds, H0, Cal.D(0), nDec[0], nPre3, nPost3);

        // ---- RIKSBANK: cuts 25bp today, period starts in 6 days, feed renumbers at the START --
        var kDec = new[] { Cal.D(0), Cal.D(30), Cal.D(60), Cal.D(90) };
        var kSt = new[] { Cal.D(6), Cal.D(36), Cal.D(66), Cal.D(96) };
        var kSt4 = Cal.D(126);
        var kPastSt = new[] { Cal.D(-54), Cal.D(-24) };
        // rollsAtPeriodStart => the renumber boundaries are the STARTS, never the announcements
        var kBounds = new[] { kPastSt[0], kPastSt[1], kSt[0], kSt[1], kSt[2], kSt[3] };

        const double kFix = 1.500;                                    // SWESTR, still pre-cut
        const double kPre0 = 1.270, kPost0 = 1.250;
        const double kPre1 = 1.190, kPost1 = 1.170;
        const double kPre2 = 1.140, kPost2 = 1.120;
        const double kPre3 = 1.100, kPost3 = 1.080;

        var riks = new BankSpec { Bank = "RIKSBANK", DecisionTimeLondon = Cal.TimePassed };
        riks.Dates.AddRange(kPastSt); riks.Dates.AddRange(kSt);
        riks.DecisionDates.AddRange(kDec);
        riks.Fix(kFix).FixHist(H0, Cal.D(-1), kFix);
        riks.Quote(0, mid: kFix, prevClose: kFix, eff: kPastSt[1], mat: kSt[0]);
        riks.Quote(1, mid: kPost0, prevClose: kPre0, eff: kSt[0], mat: kSt[1]);
        riks.Quote(2, mid: kPost1, prevClose: kPre1, eff: kSt[1], mat: kSt[2]);
        riks.Quote(3, mid: kPost2, prevClose: kPre2, eff: kSt[2], mat: kSt[3]);
        riks.Quote(4, mid: kPost3, prevClose: kPre3, eff: kSt[3], mat: kSt4);
        riks.Contract(kPastSt[1], kBounds, H0, Cal.D(-1), kFix);
        riks.ContractStep(kSt[0], kBounds, H0, Cal.D(0), kDec[0], kPre0, kPost0);
        riks.ContractStep(kSt[1], kBounds, H0, Cal.D(0), kDec[0], kPre1, kPost1);
        riks.ContractStep(kSt[2], kBounds, H0, Cal.D(0), kDec[0], kPre2, kPost2);
        riks.ContractStep(kSt[3], kBounds, H0, Cal.D(0), kDec[0], kPre3, kPost3);

        // ---- FOMC: the untouched control ------------------------------------------------------
        var fomc = Quiet("FOMC", 0, 21, 42, 3.900, 3.780, -0.120);

        var spec = new ScenarioSpec
        {
            Id = 47,
            Name = "NORGES and RIKSBANK decide on the SAME day (hike + cut)",
            Question = "Two decisions in one report, one hike one cut, one 1-day lag and one " +
                       "6-day period-start roll: does each roll and re-base on its own calendar " +
                       "without touching the other?",
        };
        spec.Banks.Add(fomc.B);
        spec.Banks.Add(nor);
        spec.Banks.Add(riks);

        // NORGES, re-based onto 4.250 (NOWA still prints 4.000 until tomorrow):
        //   row 1 (4.310 - 4.250) * 100 = +6.0   Step blank
        //   row 2 (4.360 - 4.250) * 100 = +11.0  Step +5.0
        //   row 3 (4.400 - 4.250) * 100 = +15.0  Step +4.0
        //   d: each contract gained 0.020 today => +2.0bp on 1d/1w/1m
        spec.Expect.Add(new BankExpect
        {
            Bank = "NORGES",
            Fixing = nPost0,
            Rebased = true,
            Front = new FrontExpect(nDec[1], nSt[1], nPost1, nPost0, +6.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(nSt[1], nSt[2], nPost1,  +6.0, null, +2.0, +2.0, +2.0),
                new(nSt[2], nSt[3], nPost2, +11.0, +5.0, +2.0, +2.0, +2.0),
                new(nSt[3], nSt4,   nPost3, +15.0, +4.0, +2.0, +2.0, +2.0),
            },
        });

        // RIKSBANK, re-based onto 1.250:
        //   row 1 (1.170 - 1.250) * 100 = -8.0   Step blank
        //   row 2 (1.120 - 1.250) * 100 = -13.0  Step -5.0
        //   row 3 (1.080 - 1.250) * 100 = -17.0  Step -4.0
        //   d: each contract lost 0.020 today => -2.0bp on 1d/1w/1m
        spec.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            Fixing = kPost0,
            Rebased = true,
            Front = new FrontExpect(kDec[1], kSt[1], kPost1, kPost0, -8.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(kSt[1], kSt[2], kPost1,  -8.0, null, -2.0, -2.0, -2.0),
                new(kSt[2], kSt[3], kPost2, -13.0, -5.0, -2.0, -2.0, -2.0),
                new(kSt[3], kSt4,   kPost3, -17.0, -4.0, -2.0, -2.0, -2.0),
            },
        });

        // FOMC, quiet: 3.780/3.660/3.540 against a 3.900 fixing
        spec.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = 3.900,
            Rebased = false,
            Front = new FrontExpect(fomc.Dec[0], fomc.St[0], 3.780, 3.900, -12.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(fomc.St[0], fomc.St[1], 3.780, -12.0,  null, 0.0, 0.0, 0.0),
                new(fomc.St[1], fomc.St[2], 3.660, -24.0, -12.0, 0.0, 0.0, 0.0),
                new(fomc.St[2], fomc.St[3], 3.540, -36.0, -12.0, 0.0, 0.0, 0.0),
            },
        });

        // sort key (decision ?? start): FOMC 21, RIKSBANK 30, NORGES 42
        spec.Custom.Add(s => FrontOrder(s, "FOMC", "RIKSBANK", "NORGES"));
        spec.Custom.Add(s => FrontCells(s, "NORGES", D(nDec[1]), "+24%"));    // +6.0 / 25
        spec.Custom.Add(s => FrontCells(s, "RIKSBANK", D(kDec[1]), "-32%"));  // -8.0 / 25
        spec.Custom.Add(s => FrontCells(s, "FOMC", D(fomc.Dec[0]), "-48%"));  // -12.0 / 25

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            foreach (var (bank, gone) in new[] { ("NORGES", nSt[0]), ("RIKSBANK", kSt[0]) })
                if (s.Run(bank)!.Rows.Any(r => r.Date == gone))
                    msgs.Add($"{bank}: the period it just decided ({D(gone)}) is still on the board");
            var blast = Render.Blast(s.BlastText);
            foreach (var bank in new[] { "NORGES", "RIKSBANK" })
                if (!blast.TryGetValue(bank, out var b) || !b.Rebased)
                    msgs.Add($"{bank}: the blast does not mark the fixing rebased");
            if (blast.TryGetValue("FOMC", out var f) && f.Rebased)
                msgs.Add("FOMC: rebased on a day it did not decide");
            var reb = s.Report.Fronts.Where(x => x.RefRebased).Select(x => x.Bank)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!reb.SequenceEqual(new[] { "NORGES", "RIKSBANK" }))
                msgs.Add($"re-based front lines are [{string.Join(", ", reb)}], expected [NORGES, RIKSBANK]");
            return msgs;
        });

        spec.NotesNotContain.Add("CHECK");
        spec.NotesNotContain.Add("STALE");
        yield return spec;
    }

    // ================================================================== 48

    /// <summary>A calendar nobody topped up: the ECB's decisionDates list is EMPTY and its next
    /// swap period starts in three days. The front table must say so with "{start}*" plus the
    /// footnote rather than invent a decision - and the run must still publish.
    ///
    /// <para>THE DESK CONSEQUENCE, and it is the point of the scenario: with no decision on file
    /// there is nothing to gate on, so the JUST-DECIDED period stays on the front and its Priced
    /// is measured against the stale o/n fixing. The market repriced 25bp three days ago; the
    /// front line therefore reads "+26.0bp priced / +104% of 25bp" for a hike already delivered.
    /// The only warning the reader gets is the asterisk.</para></summary>
    private static IEnumerable<ScenarioSpec> EmptyCalendar()
    {
        var eSt = new[] { Cal.D(3), Cal.D(52), Cal.D(101), Cal.D(150) };
        var ePastSt = new[] { Cal.D(-88), Cal.D(-39) };
        // With decisionDates empty the app cannot derive announcements (MeetingCalendar.LagIsStable
        // needs at least one paired decision), so its renumber boundaries ARE the period starts -
        // and the synthetic feed is seeded to match, i.e. a family that has not re-pointed yet.
        var eBounds = ePastSt.Concat(eSt).ToArray();

        const double eFix = 2.000;                 // ESTRON, still the pre-hike rate
        const double p1a = 2.010, p1b = 2.260;     // the just-decided period: a +25bp surprise
        const double p2a = 2.090, p2b = 2.340;
        const double p3a = 2.170, p3b = 2.420;
        const double p4a = 2.250, p4b = 2.500;
        var step = Cal.D(-3);                      // the (unrecorded) announcement, 3 days ago

        var ecb = new BankSpec { Bank = "ECB" };   // decisionDates deliberately left EMPTY
        ecb.Dates.AddRange(ePastSt);
        ecb.Dates.AddRange(eSt);
        ecb.Fix(eFix).FixHist(H0, Cal.D(-1), eFix);
        ecb.Quote(0, mid: eFix, prevClose: eFix, eff: ePastSt[1], mat: eSt[0]);
        ecb.Quote(1, mid: p1b, prevClose: p1b, eff: eSt[0], mat: eSt[1]);
        ecb.Quote(2, mid: p2b, prevClose: p2b, eff: eSt[1], mat: eSt[2]);
        ecb.Quote(3, mid: p3b, prevClose: p3b, eff: eSt[2], mat: eSt[3]);
        ecb.Contract(ePastSt[1], eBounds, H0, Cal.D(-1), eFix);
        ecb.ContractStep(eSt[0], eBounds, H0, Cal.D(-1), step, p1a, p1b);
        ecb.ContractStep(eSt[1], eBounds, H0, Cal.D(-1), step, p2a, p2b);
        ecb.ContractStep(eSt[2], eBounds, H0, Cal.D(-1), step, p3a, p3b);
        ecb.ContractStep(eSt[3], eBounds, H0, Cal.D(-1), step, p4a, p4b);

        var mpc = Quiet("MPC", 0, 10, 42, 4.000, 3.900, -0.100);   // the calendared control

        var spec = new ScenarioSpec
        {
            Id = 48,
            Name = "Empty decisionDates with the period start 3 days out",
            Question = "With no decision calendar at all, does the front table say '{start}*' " +
                       "and footnote it instead of inventing a decision - and do the rows still " +
                       "publish honestly?",
        };
        spec.Banks.Add(ecb);
        spec.Banks.Add(mpc.B);

        // No decisionDates => no gate roll, no re-base: the fixing stays the raw 2.000.
        //   row 1 (2.260 - 2.000) * 100 = +26.0   Step blank
        //   row 2 (2.340 - 2.000) * 100 = +34.0   Step +8.0
        //   row 3 (2.420 - 2.000) * 100 = +42.0   Step +8.0
        //   d1: the level shift was 3 days ago, so yesterday already carried it => 0.0
        //   d1w / d1m: those anchors predate the shift => +25.0 on every row
        spec.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = eFix,
            Rebased = false,
            Front = new FrontExpect(null, eSt[0], p1b, eFix, +26.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(eSt[0], eSt[1], p1b, +26.0, null, 0.0, +25.0, +25.0),
                new(eSt[1], eSt[2], p2b, +34.0, +8.0, 0.0, +25.0, +25.0),
                new(eSt[2], eSt[3], p3b, +42.0, +8.0, 0.0, +25.0, +25.0),
            },
        });
        spec.Expect.Add(new BankExpect
        {
            Bank = "MPC",
            Fixing = 4.000,
            Rebased = false,
            Front = new FrontExpect(mpc.Dec[0], mpc.St[0], 3.900, 4.000, -10.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(mpc.St[0], mpc.St[1], 3.900, -10.0,  null, 0.0, 0.0, 0.0),
                new(mpc.St[1], mpc.St[2], 3.800, -20.0, -10.0, 0.0, 0.0, 0.0),
                new(mpc.St[2], mpc.St[3], 3.700, -30.0, -10.0, 0.0, 0.0, 0.0),
            },
        });

        // sort key: the ECB has no decision, so it sorts on its START (D+3), ahead of MPC's D+10
        spec.Custom.Add(s => FrontOrder(s, "ECB", "MPC"));
        spec.Custom.Add(s => FrontCells(s, "ECB", D(eSt[0]) + "*", "+104%"));   // +26.0 / 25
        spec.Custom.Add(s => FrontCells(s, "MPC", D(mpc.Dec[0]), "-40%"));

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            if (s.Front("ECB")?.Decision != null)
                msgs.Add("the ECB front line invented a decision date from an empty calendar");
            if (!s.SheetHtml.Contains("swap-period start shown"))
                msgs.Add("the '*' footnote is missing from the sheet email");
            if (s.SheetHtml.Contains("*</i>"))
                msgs.Add("a re-based dagger appeared although no decision is on file");
            if (s.Run("ECB")!.Rows.Count != 3)
                msgs.Add($"ECB published {s.Run("ECB")!.Rows.Count} row(s) - an empty decision " +
                         "calendar must not suppress the run");
            if (Render.Blast(s.BlastText).GetValueOrDefault("ECB")?.Rows.Count != 3)
                msgs.Add("the blast does not carry the ECB's three rows");
            return msgs;
        });

        spec.NotesContain.Add("has NO decisionDates");   // F12: the state that disables the roll now warns
        yield return spec;
    }

    // ================================================================== 49

    /// <summary>FRONT TABLE ORDERING AND SIGNS. Five banks whose fronts sit on five different
    /// dates: the table sorts on (decision ?? start), which is deliberately NOT the start order
    /// here - the ECB's decision (D+20) precedes the RBNZ's start (D+21) even though the ECB's
    /// own period starts last of the five. Hikes, cuts and a dead-flat front cover the three
    /// branches of the "% 25bp" format.</summary>
    private static IEnumerable<ScenarioSpec> OrderAndSigns()
    {
        //  bank  lag  decision  start  front Priced   % 25bp
        //  MPC    0     D+9      D+9      +12.5        +50%
        //  FOMC   0     D+16     D+16     -12.5        -50%
        //  ECB    6     D+20     D+26     -25.0       -100%
        //  RBNZ   1    (none)    D+21      +7.5        +30%
        //  RBA    1     D+23     D+24       0.0          0%
        var mpc = Quiet("MPC", 0, 9, 42, 4.000, 4.125, +0.100);
        var fomc = Quiet("FOMC", 0, 16, 42, 3.900, 3.775, -0.100);
        var ecb = Quiet("ECB", 6, 20, 49, 2.500, 2.250, -0.080);
        var rba = Quiet("RBA", 1, 23, 42, 3.600, 3.600, +0.100);

        // RBNZ: the same quiet shape, but with NO decisionDates, so it sorts on its START
        var rSt = new[] { Cal.D(21), Cal.D(63), Cal.D(105), Cal.D(147) };
        var rPastSt = new[] { Cal.D(-63), Cal.D(-21) };
        var rBounds = rPastSt.Concat(rSt).ToArray();
        var rbnz = new BankSpec { Bank = "RBNZ" };
        rbnz.Dates.AddRange(rPastSt); rbnz.Dates.AddRange(rSt);
        rbnz.Fix(2.750).FixHist(H0, Cal.D(-1), 2.750);
        rbnz.Quote(0, mid: 2.750, prevClose: 2.750, eff: rPastSt[1], mat: rSt[0]);
        rbnz.Quote(1, mid: 2.825, prevClose: 2.825, eff: rSt[0], mat: rSt[1]);
        rbnz.Quote(2, mid: 2.925, prevClose: 2.925, eff: rSt[1], mat: rSt[2]);
        rbnz.Quote(3, mid: 3.025, prevClose: 3.025, eff: rSt[2], mat: rSt[3]);
        rbnz.Contract(rPastSt[1], rBounds, H0, Cal.D(-1), 2.750);
        rbnz.Contract(rSt[0], rBounds, H0, Cal.D(-1), 2.825);
        rbnz.Contract(rSt[1], rBounds, H0, Cal.D(-1), 2.925);
        rbnz.Contract(rSt[2], rBounds, H0, Cal.D(-1), 3.025);
        rbnz.Contract(rSt[3], rBounds, H0, Cal.D(-1), 3.125);

        var spec = new ScenarioSpec
        {
            Id = 49,
            Name = "Front table: order by decision-or-start, and the sign of '% of 25bp'",
            Question = "Does the front table sort on the DECISION (not the period start), and " +
                       "does '% 25bp' render a hike, a cut and a flat front correctly?",
        };
        spec.Banks.Add(mpc.B); spec.Banks.Add(fomc.B); spec.Banks.Add(ecb.B);
        spec.Banks.Add(rbnz); spec.Banks.Add(rba.B);

        // MPC   4.125/4.225/4.325 vs 4.000 -> Priced +12.5/+22.5/+32.5, Step +10.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "MPC", Fixing = 4.000, Rebased = false,
            Front = new FrontExpect(mpc.Dec[0], mpc.St[0], 4.125, 4.000, +12.5, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(mpc.St[0], mpc.St[1], 4.125, +12.5,  null, 0.0, 0.0, 0.0),
                new(mpc.St[1], mpc.St[2], 4.225, +22.5, +10.0, 0.0, 0.0, 0.0),
                new(mpc.St[2], mpc.St[3], 4.325, +32.5, +10.0, 0.0, 0.0, 0.0),
            },
        });
        // FOMC  3.775/3.675/3.575 vs 3.900 -> Priced -12.5/-22.5/-32.5, Step -10.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "FOMC", Fixing = 3.900, Rebased = false,
            Front = new FrontExpect(fomc.Dec[0], fomc.St[0], 3.775, 3.900, -12.5, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(fomc.St[0], fomc.St[1], 3.775, -12.5,  null, 0.0, 0.0, 0.0),
                new(fomc.St[1], fomc.St[2], 3.675, -22.5, -10.0, 0.0, 0.0, 0.0),
                new(fomc.St[2], fomc.St[3], 3.575, -32.5, -10.0, 0.0, 0.0, 0.0),
            },
        });
        // ECB   2.250/2.170/2.090 vs 2.500 -> Priced -25.0/-33.0/-41.0, Step -8.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "ECB", Fixing = 2.500, Rebased = false,
            Front = new FrontExpect(ecb.Dec[0], ecb.St[0], 2.250, 2.500, -25.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(ecb.St[0], ecb.St[1], 2.250, -25.0, null, 0.0, 0.0, 0.0),
                new(ecb.St[1], ecb.St[2], 2.170, -33.0, -8.0, 0.0, 0.0, 0.0),
                new(ecb.St[2], ecb.St[3], 2.090, -41.0, -8.0, 0.0, 0.0, 0.0),
            },
        });
        // RBNZ  2.825/2.925/3.025 vs 2.750 -> Priced +7.5/+17.5/+27.5, Step +10.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBNZ", Fixing = 2.750, Rebased = false,
            Front = new FrontExpect(null, rSt[0], 2.825, 2.750, +7.5, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(rSt[0], rSt[1], 2.825,  +7.5,  null, 0.0, 0.0, 0.0),
                new(rSt[1], rSt[2], 2.925, +17.5, +10.0, 0.0, 0.0, 0.0),
                new(rSt[2], rSt[3], 3.025, +27.5, +10.0, 0.0, 0.0, 0.0),
            },
        });
        // RBA   3.600/3.700/3.800 vs 3.600 -> Priced 0.0/+10.0/+20.0, Step +10.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "RBA", Fixing = 3.600, Rebased = false,
            Front = new FrontExpect(rba.Dec[0], rba.St[0], 3.600, 3.600, 0.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(rba.St[0], rba.St[1], 3.600,   0.0,  null, 0.0, 0.0, 0.0),
                new(rba.St[1], rba.St[2], 3.700, +10.0, +10.0, 0.0, 0.0, 0.0),
                new(rba.St[2], rba.St[3], 3.800, +20.0, +10.0, 0.0, 0.0, 0.0),
            },
        });

        // THE ORDER. Keys: MPC 9, FOMC 16, ECB 20 (decision), RBNZ 21 (start), RBA 23.
        // Sorting on the START instead would give MPC, FOMC, RBNZ, RBA, ECB - a different table.
        spec.Custom.Add(s => FrontOrder(s, "MPC", "FOMC", "ECB", "RBNZ", "RBA"));
        spec.Custom.Add(s => FrontCells(s, "MPC", D(mpc.Dec[0]), "+50%"));
        spec.Custom.Add(s => FrontCells(s, "FOMC", D(fomc.Dec[0]), "-50%"));
        spec.Custom.Add(s => FrontCells(s, "ECB", D(ecb.Dec[0]), "-100%"));
        spec.Custom.Add(s => FrontCells(s, "RBNZ", D(rSt[0]) + "*", "+30%"));
        spec.Custom.Add(s => FrontCells(s, "RBA", D(rba.Dec[0]), "0%"));

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // the flat front must print "0.0", never "+0.0" (desk formatting rule)
            var row = Front(s).FirstOrDefault(r => Render.Norm(r[0]).StartsWith("RBA "));
            if (row != null && Render.Norm(row[5]) != "0.0")
                msgs.Add($"RBA front Priced cell is '{Render.Norm(row[5])}', expected '0.0'");
            // the start-only line must not borrow a neighbour's decision date
            if (s.Front("RBNZ")?.Decision != null)
                msgs.Add("the RBNZ front line acquired a decision date it does not have");
            return msgs;
        });

        spec.NotesContain.Add("has NO decisionDates");   // F12: the state that disables the roll now warns
        yield return spec;
    }

    // ================================================================== 50

    /// <summary>THE HARDEST SINGLE DAY, over every surface at once: a 25bp SURPRISE ECB cut with
    /// the gate roll and the announced-but-not-yet-effective re-base; a Riksbank run carrying a
    /// Y/E TURN row in the MIDDLE of the strip (so the step chain has to skip it and the row
    /// after it carries the CUMULATIVE move); a BOJ run TRUNCATED by a rung that prices without
    /// date fields (so its last row publishes a BLANK maturity); and the CHECK note the surprise
    /// necessarily raises. The universal invariants then run the blast, the workbook, the sheet
    /// email, the card email, the plaintext and the frozen report over all of it.
    ///
    /// <para>THIS SCENARIO IS EXPECTED TO STAY RED ON TWO UNIVERSAL-INVARIANT LINES, both
    /// diagnosed, neither a wrong number on any surface. Left red deliberately: an expectation
    /// moved to match the output would hide them.</para>
    /// <list type="number">
    /// <item>"a Y/E turn row must publish no numbers" - the FROZEN REPORT keeps the turn
    /// period's real print (Mid 1.400, Priced -55.0) on the row every surface masks. DESIGN.md
    /// section 12 says the row keeps its print internally, so this is deliberate; the exposure is
    /// that report.json is the offline rebuild source and -55.0bp reads as a monster cut to any
    /// consumer that forgets to test TurnPeriod.</item>
    /// <item>"the card email does not label the Y/E turn row" - it does. WeeklyEmail's meeting-row
    /// path hard-codes "Y/E&amp;nbsp;Turn" while its own FRONT-table path and every other surface
    /// use RunsTable.TurnLabel ("Y/E Turn"). Identical to a reader; the invariant's substring test
    /// does not normalise the entity. A harness limitation over a product label-duplication wart.</item>
    /// </list></summary>
    private static IEnumerable<ScenarioSpec> EverySurface()
    {
        // ---- ECB: a 25bp SURPRISE cut today, 20bp of it delivered on the day ------------------
        var eDec = new[] { Cal.D(0), Cal.D(49), Cal.D(98), Cal.D(147) };
        var eSt = new[] { Cal.D(6), Cal.D(55), Cal.D(104), Cal.D(153) };
        var eSt4 = Cal.D(202);
        var ePastSt = new[] { Cal.D(-92), Cal.D(-50) };
        var eBounds = new[] { Cal.D(-98), Cal.D(-56), eDec[0], eDec[1], eDec[2], eDec[3] };

        const double eFix = 2.500;
        const double ePre0 = 2.450, ePost0 = 2.250;   // -20bp on the day: the surprise
        const double ePre1 = 2.400, ePost1 = 2.200;
        const double ePre2 = 2.350, ePost2 = 2.150;
        const double ePre3 = 2.300, ePost3 = 2.100;

        var ecb = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        ecb.Dates.AddRange(ePastSt); ecb.Dates.AddRange(eSt);
        ecb.DecisionDates.AddRange(eDec);
        ecb.Fix(eFix).FixHist(H0, Cal.D(-1), eFix);
        ecb.Quote(0, mid: eFix, prevClose: eFix, eff: ePastSt[1], mat: eSt[0]);
        ecb.Quote(1, mid: ePost0, prevClose: ePre0, eff: eSt[0], mat: eSt[1]);
        ecb.Quote(2, mid: ePost1, prevClose: ePre1, eff: eSt[1], mat: eSt[2]);
        ecb.Quote(3, mid: ePost2, prevClose: ePre2, eff: eSt[2], mat: eSt[3]);
        ecb.Quote(4, mid: ePost3, prevClose: ePre3, eff: eSt[3], mat: eSt4);
        ecb.Contract(ePastSt[1], eBounds, H0, Cal.D(-1), eFix);
        ecb.ContractStep(eSt[0], eBounds, H0, Cal.D(0), eDec[0], ePre0, ePost0);
        ecb.ContractStep(eSt[1], eBounds, H0, Cal.D(0), eDec[0], ePre1, ePost1);
        ecb.ContractStep(eSt[2], eBounds, H0, Cal.D(0), eDec[0], ePre2, ePost2);
        ecb.ContractStep(eSt[3], eBounds, H0, Cal.D(0), eDec[0], ePre3, ePost3);

        // ---- RIKSBANK: a Y/E turn period in the MIDDLE of the strip ---------------------------
        var kDec = new[] { Cal.D(6), Cal.D(48), Cal.D(90), Cal.D(132), Cal.D(174) };
        var kSt = new[] { Cal.D(12), Cal.D(54), Cal.D(96), Cal.D(138), Cal.D(180) };
        var kPastSt = new[] { Cal.D(-72), Cal.D(-30) };
        var kBounds = kPastSt.Concat(kSt).ToArray();      // rollsAtPeriodStart

        const double kFix = 1.950;
        const double kM1 = 1.900, kM2 = 1.850, kTurn = 1.400, kM4 = 1.800, kM5 = 1.750;

        var riks = new BankSpec { Bank = "RIKSBANK" };
        riks.Dates.AddRange(kPastSt); riks.Dates.AddRange(kSt);
        riks.DecisionDates.AddRange(kDec);
        riks.Fix(kFix).FixHist(H0, Cal.D(-1), kFix);
        riks.Quote(0, mid: kFix, prevClose: kFix, eff: kPastSt[1], mat: kSt[0]);
        riks.Quote(1, mid: kM1, prevClose: kM1, eff: kSt[0], mat: kSt[1]);
        riks.Quote(2, mid: kM2, prevClose: kM2, eff: kSt[1], mat: kSt[2]);
        riks.Quote(3, mid: kTurn, prevClose: kTurn, eff: kSt[2], mat: kSt[3]);
        riks.Quote(4, mid: kM4, prevClose: kM4, eff: kSt[3], mat: kSt[4]);
        riks.Contract(kPastSt[1], kBounds, H0, Cal.D(-1), kFix);
        riks.Contract(kSt[0], kBounds, H0, Cal.D(-1), kM1);
        riks.Contract(kSt[1], kBounds, H0, Cal.D(-1), kM2);
        riks.Contract(kSt[2], kBounds, H0, Cal.D(-1), kTurn);
        riks.Contract(kSt[3], kBounds, H0, Cal.D(-1), kM4);
        riks.Contract(kSt[4], kBounds, H0, Cal.D(-1), kM5);

        // ---- BOJ: rung 3 prices but publishes NO date fields, so the run truncates there ------
        var jDec = new[] { Cal.D(20), Cal.D(62), Cal.D(104), Cal.D(146) };
        var jSt = new[] { Cal.D(26), Cal.D(68), Cal.D(110), Cal.D(152) };
        var jPastSt = new[] { Cal.D(-58), Cal.D(-16) };
        var jBounds = new[] { Cal.D(-64), Cal.D(-22), jDec[0], jDec[1], jDec[2], jDec[3] };

        const double jFix = 0.500;
        const double jM1 = 0.560, jM2 = 0.620, jM3 = 0.680, jM4 = 0.740;

        var boj = new BankSpec { Bank = "BOJ" };
        boj.Dates.AddRange(jPastSt); boj.Dates.AddRange(jSt);
        boj.DecisionDates.AddRange(jDec);
        boj.Fix(jFix).FixHist(H0, Cal.D(-1), jFix);
        boj.Quote(0, mid: jFix, prevClose: jFix, eff: jPastSt[1], mat: jSt[0]);
        boj.Quote(1, mid: jM1, prevClose: jM1, eff: jSt[0], mat: jSt[1]);
        boj.Quote(2, mid: jM2, prevClose: jM2, eff: jSt[1], mat: jSt[2]);
        boj.Quote(3, mid: jM3, prevClose: jM3);            // priced, UNDATED - the truncation
        boj.Contract(jPastSt[1], jBounds, H0, Cal.D(-1), jFix);
        boj.Contract(jSt[0], jBounds, H0, Cal.D(-1), jM1);
        boj.Contract(jSt[1], jBounds, H0, Cal.D(-1), jM2);
        boj.Contract(jSt[2], jBounds, H0, Cal.D(-1), jM3);
        boj.Contract(jSt[3], jBounds, H0, Cal.D(-1), jM4);

        var spec = new ScenarioSpec
        {
            Id = 50,
            Name = "One report, every surface: surprise cut + re-base + Y/E turn + truncated run",
            Question = "On the hardest day the calendar can produce, do the blast, the workbook, " +
                       "the sheet email, the card email, the plaintext and the frozen report all " +
                       "still say the same thing?",
        };
        spec.Banks.Add(ecb);
        spec.Banks.Add(riks);
        spec.Banks.Add(boj);

        // ECB, re-based onto 2.250 (the just-decided period's own OIS):
        //   row 1 (2.200 - 2.250) * 100 = -5.0    Step blank
        //   row 2 (2.150 - 2.250) * 100 = -10.0   Step -5.0
        //   row 3 (2.100 - 2.250) * 100 = -15.0   Step -5.0
        //   d: every contract fell 0.200 today => -20.0bp on 1d/1w/1m
        spec.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = ePost0,
            Rebased = true,
            Front = new FrontExpect(eDec[1], eSt[1], ePost1, ePost0, -5.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(eSt[1], eSt[2], ePost1,  -5.0, null, -20.0, -20.0, -20.0),
                new(eSt[2], eSt[3], ePost2, -10.0, -5.0, -20.0, -20.0, -20.0),
                new(eSt[3], eSt4,   ePost3, -15.0, -5.0, -20.0, -20.0, -20.0),
            },
        });

        // RIKSBANK, quiet, four rows with the third spanning the year end:
        //   row 1 (1.900 - 1.950) * 100 = -5.0    Step blank
        //   row 2 (1.850 - 1.950) * 100 = -10.0   Step -5.0
        //   row 3  Y/E Turn - no numbers published anywhere, and no Step of its own
        //   row 4 (1.800 - 1.950) * 100 = -15.0   Step = -15.0 - (-10.0) = -5.0, the CUMULATIVE
        //         move priced across the masked meeting plus its own
        spec.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            Fixing = kFix,
            Rebased = false,
            Front = new FrontExpect(kDec[0], kSt[0], kM1, kFix, -5.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(kSt[0], kSt[1], kM1,  -5.0,  null, 0.0, 0.0, 0.0),
                new(kSt[1], kSt[2], kM2, -10.0,  -5.0, 0.0, 0.0, 0.0),
                new(kSt[2], kSt[3], null, null,  null, null, null, null, Turn: true),
                new(kSt[3], kSt[4], kM4, -15.0,  -5.0, 0.0, 0.0, 0.0),
            },
        });

        // BOJ, quiet and truncated: three rows, the last with NO maturity (its end is undocumented)
        //   (0.560 - 0.500) * 100 = +6.0 / +12.0 / +18.0, Step +6.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "BOJ",
            Fixing = jFix,
            Rebased = false,
            Front = new FrontExpect(jDec[0], jSt[0], jM1, jFix, +6.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(jSt[0], jSt[1], jM1,  +6.0, null, 0.0, 0.0, 0.0),
                new(jSt[1], jSt[2], jM2, +12.0, +6.0, 0.0, 0.0, 0.0),
                new(jSt[2], null,   jM3, +18.0, +6.0, 0.0, 0.0, 0.0),
            },
        });

        // sort key (decision ?? start): RIKSBANK 6, BOJ 20, ECB 49
        spec.Custom.Add(s => FrontOrder(s, "RIKSBANK", "BOJ", "ECB"));
        spec.Custom.Add(s => FrontCells(s, "RIKSBANK", D(kDec[0]), "-20%"));
        spec.Custom.Add(s => FrontCells(s, "BOJ", D(jDec[0]), "+24%"));
        spec.Custom.Add(s => FrontCells(s, "ECB", D(eDec[1]), "-20%"));

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();

            // 1. the turn row is labelled on EVERY surface and carries no numbers anywhere.
            //    The HTML surfaces &nbsp;-join the label (Word/Outlook must not break it), so the
            //    entity is normalised away before comparing - the READER sees "Y/E Turn" either way.
            foreach (var (name, text) in new[]
                     { ("blast", s.BlastText), ("sheet email", s.SheetHtml),
                       ("card email", s.WeeklyHtml), ("plaintext", s.WeeklyText) })
                if (!text.Replace("&nbsp;", " ").Contains("Y/E Turn"))
                    msgs.Add($"the Y/E turn row is not labelled in the {name}");
            if (!s.Xlsx.Any(r => r.Any(c => c.Contains("Y/E Turn"))))
                msgs.Add("the Y/E turn row is not labelled in the workbook Runs sheet");
            // the turn print itself (1.400) must never be published as a number
            if (s.BlastText.Contains("1.400") || s.SheetHtml.Contains("1.400"))
                msgs.Add("the Y/E turn period's raw print leaked onto a surface as a number");

            // 2. the truncated BOJ run: last row, blank maturity, in the workbook AND the email
            foreach (var (name, blocks) in new[]
                     { ("workbook", Render.Sheet(s.Xlsx)), ("email", Render.Email(s.SheetHtml)) })
            {
                var b = blocks.GetValueOrDefault("BOJ");
                if (b == null) { msgs.Add($"BOJ missing from the {name}"); continue; }
                if (b.Rows.Count != 3)
                { msgs.Add($"BOJ shows {b.Rows.Count} row(s) in the {name}, expected 3"); continue; }
                if (Render.Norm(b.Rows[2][1]).Length != 0)
                    msgs.Add($"BOJ's last row shows a maturity '{Render.Norm(b.Rows[2][1])}' in the " +
                             $"{name}, but its end date is not documented - it must be blank");
            }

            // 3. the decided period is gone and the re-base is visible and singular
            if (s.Run("ECB")!.Rows.Any(r => r.Date == eSt[0]))
                msgs.Add("the period the ECB just decided is still on the board");
            var reb = s.Report.Fronts.Where(f => f.RefRebased).Select(f => f.Bank).ToList();
            if (!reb.SequenceEqual(new[] { "ECB" }))
                msgs.Add($"re-based front lines are [{string.Join(", ", reb)}], expected [ECB]");

            // 4. the CHECK the surprise necessarily raises: -20.0bp of d1 is over the 12bp
            //    sanity bar, on all three ECB rows and on nothing else
            var checks = s.Notes.Where(n => n.StartsWith("CHECK")).ToList();
            if (checks.Count != 3)
                msgs.Add($"{checks.Count} CHECK note(s), expected 3 (one per ECB row over the " +
                         $"12bp d1 bar): {string.Join(" || ", checks)}");
            foreach (var n in checks)
                if (!n.Contains("ECB") || !n.Contains("1d"))
                    msgs.Add($"unexpected CHECK note: {n}");

            return msgs;
        });

        spec.NotesContain.Add("CHECK");
        spec.NotesNotContain.Add("STALE");
        spec.NotesNotContain.Add("FUTURES GUARD");
        yield return spec;
    }
}
