using System.Globalization;
using RateDesk.Core;
using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>WHAT THE BANK ACTUALLY DID - the six shapes a decision day comes in, all on a
/// LAGGED-START family (ECB: announcement today, the new rate effective six days later) and all
/// AFTER the statement, which is the state every one of these runs is published in.
///
/// THE GEOMETRY (16-20), identical in all five so only the market changes:
///
///     S2 ....... S1 ......|today = Dec0|.. St0 ....... Dec1 .. St1 ....... Dec2 .. St2 ...
///     settled    settled   announcement    +6d          next            then
///
/// The family renumbers at the ANNOUNCEMENT, so the roll boundaries are Dec0/Dec1/Dec2/Dec3 plus
/// the two settled announcements derived as start-6 (D-98, D-56). The feed has NOT re-pointed:
/// rung 1 still quotes the period the decision governs, which is exactly the state the time-gated
/// roll exists for. Once the gate fires the board publishes St1/St2/St3 and Priced re-bases onto
/// the just-decided period's own OIS (quotes[0] after the shift).
///
/// HISTORY is seeded PRE-DECISION only, [-70d, -1d]. Today's own point is never an anchor
/// (ChangeToBp takes the last point at-or-before today-1), so seeding it would change nothing and
/// leaving it out keeps every derivation below readable: each change column is
/// (live mid - that same CONTRACT's own pre-decision mark) x 100.
///
/// 16 and 17 give each contract a second level a month back (a step on D-20) so that Delta-1m
/// separates from Delta-1d/1w: on a fully-priced move the day change is half a basis point, and a
/// one-rung mis-read has to be visible in SOMETHING.</summary>
public static class Group04_MoveSizes
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- shared calendar (16-20)

    private static readonly DateTime S2 = Cal.D(-92), S1 = Cal.D(-50);
    private static readonly DateTime Dec0 = Cal.D(0), St0 = Cal.D(6);
    private static readonly DateTime Dec1 = Cal.D(49), St1 = Cal.D(55);
    private static readonly DateTime Dec2 = Cal.D(98), St2 = Cal.D(104);
    private static readonly DateTime Dec3 = Cal.D(147), St3 = Cal.D(153);
    private static readonly DateTime St4 = Cal.D(202);

    /// <summary>The dates the family RENUMBERS on: the announcements, the two settled ones
    /// derived as start-6. MeetingCalendar.AnnouncementDates derives exactly these and the app's
    /// 14-day cluster keeps exactly these.</summary>
    private static readonly DateTime[] Bounds = { Cal.D(-98), Cal.D(-56), Dec0, Dec1, Dec2, Dec3 };

    private static readonly DateTime HistFrom = Cal.D(-70), HistTo = Cal.D(-1), MonthStep = Cal.D(-20);

    /// <summary>One contract's three marks: where it stood a month ago, where it closed
    /// yesterday, and its live mid now. Mo == Yd means the contract sat still all month and only
    /// the decision moved it.</summary>
    private sealed record Lvl(double Mo, double Yd, double Lv);

    /// <summary>The ECB run. p0 is the period the decision GOVERNS (starts D+6; it rolls off the
    /// board once the statement is out and becomes the re-base target); p1/p2/p3 are the three
    /// published rows.</summary>
    private static BankSpec Ecb(double fixing, Lvl p0, Lvl p1, Lvl p2, Lvl p3)
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { S2, S1, St0, St1, St2, St3 });
        b.DecisionDates.AddRange(new[] { Dec0, Dec1, Dec2, Dec3 });
        // ESTR still prints the OLD rate - the change is effective at St0, six days out
        b.Fix(fixing).FixHist(HistFrom, HistTo, fixing);

        // THE FEED HAS NOT RE-POINTED: rung 1 still quotes the just-decided period, and each
        // rung's PrevClose is that SAME contract's mark yesterday (no re-point => no shift).
        b.Quote(0, mid: fixing, prevClose: fixing, eff: S1, mat: St0);
        b.Quote(1, mid: p0.Lv, prevClose: p0.Yd, eff: St0, mat: St1);
        b.Quote(2, mid: p1.Lv, prevClose: p1.Yd, eff: St1, mat: St2);
        b.Quote(3, mid: p2.Lv, prevClose: p2.Yd, eff: St2, mat: St3);
        b.Quote(4, mid: p3.Lv, prevClose: p3.Yd, eff: St3, mat: St4);

        // history, CONTRACT by contract, pre-decision only
        b.Contract(S1, Bounds, HistFrom, HistTo, fixing);
        b.ContractStep(St0, Bounds, HistFrom, HistTo, MonthStep, p0.Mo, p0.Yd);
        b.ContractStep(St1, Bounds, HistFrom, HistTo, MonthStep, p1.Mo, p1.Yd);
        b.ContractStep(St2, Bounds, HistFrom, HistTo, MonthStep, p2.Mo, p2.Yd);
        b.ContractStep(St3, Bounds, HistFrom, HistTo, MonthStep, p3.Mo, p3.Yd);
        return b;
    }

    // ---------------------------------------------------------------- shared assertions

    private static string D(DateTime d) => d.ToString("dd-MMM-yy", Inv);
    private static string Bp(double v) => v.ToString("+0.0;-0.0;0.0", Inv);

    /// <summary>The exact OutlierGuard absolute-bar note for one row/column
    /// (OutlierGuard.Check, src\RateDesk.Core\OutlierGuard.cs:43).</summary>
    private static string AbsBarNote(DateTime row, string horizon, double v, double bar) =>
        $"CHECK: ECB {D(row)} Δ{horizon} {Bp(v)}bp exceeds the {bar:0}bp sanity bar";

    /// <summary>The period the statement just decided must be gone from every surface.</summary>
    private static IEnumerable<string> DecidedPeriodIsGone(Surfaces s)
    {
        var msgs = new List<string>();
        var run = s.Run("ECB");
        if (run != null && run.Rows.Any(r => r.Date == St0))
            msgs.Add($"the period the ECB just decided ({D(St0)}) is still on the board after the statement");
        var blk = Render.Blast(s.BlastText).GetValueOrDefault("ECB");
        if (blk != null && blk.Rows.Any(r => r.Length > 0 && r[0] == D(St0)))
            msgs.Add("the blast still carries the just-decided period");
        var xls = Render.Sheet(s.Xlsx).GetValueOrDefault("ECB");
        if (xls != null && xls.Rows.Any(r => r.Length > 0 && r[0] == D(St0)))
            msgs.Add("the workbook still carries the just-decided period");
        return msgs;
    }

    /// <summary>The run notes are an operator gate, not client copy: a CHECK must reach the notes
    /// and must NOT reach anything the desk sends out.</summary>
    private static IEnumerable<string> ChecksStayOutOfTheMail(Surfaces s)
    {
        var msgs = new List<string>();
        foreach (var (name, text) in new[]
                 {
                     ("sheet email body", s.SheetHtml), ("card email", s.WeeklyHtml),
                     ("plaintext email", s.WeeklyText), ("chat blast", s.BlastText),
                 })
        {
            if (text.Contains("CHECK", StringComparison.Ordinal))
                msgs.Add($"a CHECK note leaked into the {name}");
            if (text.Contains("verify before distribution", StringComparison.OrdinalIgnoreCase))
                msgs.Add($"the 'verify before distribution' wording leaked into the {name}");
        }
        foreach (var row in s.Xlsx)
            foreach (var cell in row)
                if (cell.Contains("CHECK", StringComparison.Ordinal))
                    msgs.Add("a CHECK note leaked into the workbook Runs sheet");
        return msgs;
    }

    /// <summary>Exactly this many CHECK notes, no more: an unexpected extra flag is as much a
    /// defect as a missing one - a popup that cries wolf stops being read.</summary>
    private static Func<Surfaces, IEnumerable<string>> ExactlyNChecks(int n) => s =>
    {
        var checks = s.Notes.Where(x => x.StartsWith("CHECK", StringComparison.Ordinal)).ToList();
        return checks.Count == n
            ? Array.Empty<string>()
            : new[]
            {
                $"expected exactly {n} CHECK note(s), got {checks.Count}: " +
                (checks.Count == 0 ? "(none)" : string.Join(" || ", checks)),
            };
    };

    /// <summary>Every published mid must be a real print. The interior neighbour-misprint guard
    /// replaces a row with the neighbour midpoint and daggers it - right for a thin family's
    /// misprint, a disaster for a genuine policy move.</summary>
    private static IEnumerable<string> EveryMidIsAPrint(Surfaces s)
    {
        var msgs = new List<string>();
        var run = s.Run("ECB");
        if (run != null)
            foreach (var m in run.Rows)
                if (!m.MidSource.Equals("ticker", StringComparison.Ordinal))
                    msgs.Add($"{D(m.Date)}: mid source is '{m.MidSource}', expected the raw ticker " +
                             "print - a genuine move was rejected as a misprint");
        foreach (var n in s.Notes)
            if (n.Contains("neighbour midpoint", StringComparison.OrdinalIgnoreCase))
                msgs.Add("the neighbour-misprint guard fired on a genuine move: " + n);
        var mail = Render.Email(s.SheetHtml).GetValueOrDefault("ECB");
        if (mail != null)
            foreach (var r in mail.Rows)
                if (r.Length > 2 && r[2].Contains('†'))
                    msgs.Add($"email mid cell '{r[2]}' carries the synthesized-mid dagger");
        return msgs;
    }

    /// <summary>Sign discipline across every surface. want = +1 for a hike (nothing signed may
    /// print negative), -1 for a cut (nothing signed may print positive). Only the signed columns
    /// are read - dates carry hyphens and mids carry no sign at all.</summary>
    private static Func<Surfaces, IEnumerable<string>> SignsAllOneWay(int want) => s =>
    {
        var msgs = new List<string>();
        char bad = want > 0 ? '-' : '+';
        string what = want > 0 ? "a CUT sign after a hike" : "a HIKE sign after a cut";

        var blast = Render.Blast(s.BlastText).GetValueOrDefault("ECB");
        if (blast != null)
            foreach (var r in blast.Rows)
                for (int i = 2; i < r.Length && i <= 6; i++)          // Priced Step d1 w1 m1
                    if (r[i].Length > 0 && r[i][0] == bad)
                        msgs.Add($"blast {r[0]} col{i} prints '{r[i]}' - {what}");

        foreach (var (name, blk) in new[]
                 {
                     ("workbook", Render.Sheet(s.Xlsx).GetValueOrDefault("ECB")),
                     ("email", Render.Email(s.SheetHtml).GetValueOrDefault("ECB")),
                 })
            if (blk != null)
                foreach (var r in blk.Rows)
                    for (int i = 3; i < r.Length && i <= 7; i++)      // Priced Step d1 w1 m1
                    {
                        var c = Render.Norm(r[i]);
                        if (c.Length > 0 && c[0] == bad)
                            msgs.Add($"{name} {r[0]} col{i} prints '{c}' - {what}");
                    }

        var front = Render.EmailFront(s.SheetHtml)
            .FirstOrDefault(x => x.Length > 0 && x[0].StartsWith("ECB", StringComparison.Ordinal));
        if (front != null && front.Length > 6)
        {
            if (front[5].Length > 0 && front[5][0] == bad)
                msgs.Add($"front-table Priced prints '{front[5]}' - {what}");
            if (front[6].Length > 0 && front[6][0] == bad)
                msgs.Add($"front-table % of 25bp prints '{front[6]}' - {what}");
        }
        return msgs;
    };

    /// <summary>The card email and the plaintext carry the same signed Priced numbers, and never
    /// their mirror image - the two surfaces the cross-surface invariant does not walk cell by
    /// cell.</summary>
    private static Func<Surfaces, IEnumerable<string>> PricedReachesTheCards(params double[] priced) => s =>
    {
        var msgs = new List<string>();
        foreach (var (name, text) in new[]
                 {
                     ("card email", Render.Norm(s.WeeklyHtml)),
                     ("plaintext email", Render.Norm(s.WeeklyText)),
                 })
            foreach (var p in priced)
            {
                if (!text.Contains(Bp(p), StringComparison.Ordinal))
                    msgs.Add($"{name} does not carry the published Priced {Bp(p)}");
                if (text.Contains(Bp(-p), StringComparison.Ordinal))
                    msgs.Add($"{name} carries {Bp(-p)} - the mirror image of the published {Bp(p)}");
            }
        return msgs;
    };

    // ================================================================ the scenarios

    public static IEnumerable<ScenarioSpec> All()
    {
        // ============================================================ 16
        // 25bp HIKE, FULLY PRICED. ESTR still prints 2.000; the new 2.250 starts in six days.
        // The market had it right, so the decided period barely moves (+0.5bp) and so does the
        // rest of the strip.
        //
        //  contract      month ago   yesterday   live      day move
        //  St0 (D+6)       2.200       2.245     2.250      +0.5   <- decided period / re-base target
        //  St1             2.210       2.255     2.260      +0.5
        //  St2             2.280       2.320     2.325      +0.5
        //  St3             2.380       2.420     2.425      +0.5
        //
        // ref    = St0's own live mid = 2.250   (re-based; ESTR's 2.000 is six days stale)
        // Priced = (mid - 2.250) x 100  :  (2.260-2.250)x100 = +1.0
        //                                  (2.325-2.250)x100 = +7.5
        //                                  (2.425-2.250)x100 = +17.5
        // Step   = Priced - previous    :  blank, +7.5-(+1.0) = +6.5, +17.5-(+7.5) = +10.0
        // d1/w1  = (live - yesterday)   :  +0.5 / +0.5 / +0.5   (level flat from D-20 to D-1, so
        //                                  the 1d and 1w anchors sit on the same value)
        // m1     = (live - month ago)   :  (2.260-2.210)x100 = +5.0
        //                                  (2.325-2.280)x100 = +4.5
        //                                  (2.425-2.380)x100 = +4.5
        //
        // The front Priced of +1.0 IS the point of this scenario: measured against the stale
        // 2.000 fixing it would read +26.0 - the whole delivered hike, republished as if it were
        // still to come.
        var hikePriced = new ScenarioSpec
        {
            Id = 16,
            Name = "ECB hikes 25bp TODAY, fully priced (lagged start)",
            Question = "When the market had the hike right, does the board print a front Priced of " +
                       "a basis point instead of the whole delivered 25bp, and does every " +
                       "Priced/Step/change cell down the strip tie out?",
        };
        hikePriced.Banks.Add(Ecb(2.000,
            p0: new Lvl(2.200, 2.245, 2.250),
            p1: new Lvl(2.210, 2.255, 2.260),
            p2: new Lvl(2.280, 2.320, 2.325),
            p3: new Lvl(2.380, 2.420, 2.425)));
        hikePriced.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = 2.250, Rebased = true,
            Front = new FrontExpect(Dec1, St1, 2.260, 2.250, +1.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                //        start end     mid   priced   step     d1     w1     m1
                new(St1, St2, 2.260, +1.0, null, +0.5, +0.5, +5.0),
                new(St2, St3, 2.325, +7.5, +6.5, +0.5, +0.5, +4.5),
                new(St3, St4, 2.425, +17.5, +10.0, +0.5, +0.5, +4.5),
            },
        });
        hikePriced.NotesNotContain.Add("CHECK");
        hikePriced.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        hikePriced.NotesNotContain.Add("STALE");
        hikePriced.Custom.Add(DecidedPeriodIsGone);
        hikePriced.Custom.Add(EveryMidIsAPrint);
        hikePriced.Custom.Add(SignsAllOneWay(+1));
        hikePriced.Custom.Add(PricedReachesTheCards(+1.0, +7.5, +17.5));
        hikePriced.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // a silently swapped base is worse than no base: the re-base must be visible
            if (Render.Blast(s.BlastText).GetValueOrDefault("ECB") is { Rebased: false })
                msgs.Add("the blast fixing line does not say 'rebased'");
            if (!s.SheetHtml.Contains('†'))
                msgs.Add("the re-based fixing carries no dagger in the email");
            return msgs;
        });
        yield return hikePriced;

        // ============================================================ 17
        // 25bp CUT, FULLY PRICED - the exact mirror of 16 about the 2.000 fixing.
        //
        //  contract      month ago   yesterday   live      day move
        //  St0 (D+6)       1.800       1.755     1.750      -0.5
        //  St1             1.790       1.745     1.740      -0.5
        //  St2             1.720       1.680     1.675      -0.5
        //  St3             1.620       1.580     1.575      -0.5
        //
        // ref    = 1.750 (re-based onto St0's own OIS)
        // Priced = (1.740-1.750)x100 = -1.0 ; (1.675-1.750)x100 = -7.5 ; (1.575-1.750)x100 = -17.5
        // Step   = blank ; -7.5-(-1.0) = -6.5 ; -17.5-(-7.5) = -10.0
        // d1/w1  = -0.5 / -0.5 / -0.5
        // m1     = (1.740-1.790)x100 = -5.0 ; (1.675-1.720)x100 = -4.5 ; (1.575-1.620)x100 = -4.5
        // front % of 25bp = -1.0 / 25 x 100 = -4%
        var cutPriced = new ScenarioSpec
        {
            Id = 17,
            Name = "ECB cuts 25bp TODAY, fully priced (lagged start)",
            Question = "Mirror image of the hike: does every sign survive the trip through the " +
                       "blast, the workbook, the sheet email, the cards and the plaintext - can a " +
                       "cut print as a hike anywhere?",
        };
        cutPriced.Banks.Add(Ecb(2.000,
            p0: new Lvl(1.800, 1.755, 1.750),
            p1: new Lvl(1.790, 1.745, 1.740),
            p2: new Lvl(1.720, 1.680, 1.675),
            p3: new Lvl(1.620, 1.580, 1.575)));
        cutPriced.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = 1.750, Rebased = true,
            Front = new FrontExpect(Dec1, St1, 1.740, 1.750, -1.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(St1, St2, 1.740, -1.0, null, -0.5, -0.5, -5.0),
                new(St2, St3, 1.675, -7.5, -6.5, -0.5, -0.5, -4.5),
                new(St3, St4, 1.575, -17.5, -10.0, -0.5, -0.5, -4.5),
            },
        });
        cutPriced.NotesNotContain.Add("CHECK");
        cutPriced.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        cutPriced.NotesNotContain.Add("STALE");
        cutPriced.Custom.Add(DecidedPeriodIsGone);
        cutPriced.Custom.Add(EveryMidIsAPrint);
        cutPriced.Custom.Add(SignsAllOneWay(-1));
        cutPriced.Custom.Add(PricedReachesTheCards(-1.0, -7.5, -17.5));
        yield return cutPriced;

        // ============================================================ 18
        // SURPRISE 25bp CUT: nothing was priced, so the whole forward strip drops ~25bp at once.
        //
        //  contract      pre-decision   live      day move
        //  St0 (D+6)        2.000       1.750      -25.0
        //  St1              1.980       1.720      -26.0
        //  St2              1.930       1.650      -28.0
        //  St3              1.880       1.590      -29.0
        //
        // ref    = 1.750 (re-based onto St0's own OIS - the surprise is IN that print)
        // Priced = (1.720-1.750)x100 = -3.0 ; (1.650-1.750)x100 = -10.0 ; (1.590-1.750)x100 = -16.0
        // Step   = blank ; -10.0-(-3.0) = -7.0 ; -16.0-(-10.0) = -6.0
        // d1 = w1 = m1 (the pre-decision level is flat right back through the 1m anchor):
        //          -26.0 / -28.0 / -29.0
        //
        // OutlierGuard by hand (src\RateDesk.Core\OutlierGuard.cs:38-46): the absolute bars are
        // 12/30/50bp for 1d/1w/1m and fire per row whatever the cross-section does. |-26|, |-28|,
        // |-29| all clear 12 so all three rows flag on d1; none clears 30 (w1) or 50 (m1); the
        // cross-sectional test needs >= 4 populated rows and this run publishes 3. So EXACTLY
        // three CHECK notes, and none of them may reach the mail.
        var surprise25 = new ScenarioSpec
        {
            Id = 18,
            Name = "ECB SURPRISE 25bp cut - nothing was priced",
            Question = "When a whole strip legitimately drops 25bp, are the numbers still right, " +
                       "does the absolute Delta-1d bar flag every row for a human, and do those " +
                       "flags stay out of what the desk mails?",
        };
        surprise25.Banks.Add(Ecb(2.000,
            p0: new Lvl(2.000, 2.000, 1.750),
            p1: new Lvl(1.980, 1.980, 1.720),
            p2: new Lvl(1.930, 1.930, 1.650),
            p3: new Lvl(1.880, 1.880, 1.590)));
        surprise25.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = 1.750, Rebased = true,
            Front = new FrontExpect(Dec1, St1, 1.720, 1.750, -3.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(St1, St2, 1.720, -3.0, null, -26.0, -26.0, -26.0),
                new(St2, St3, 1.650, -10.0, -7.0, -28.0, -28.0, -28.0),
                new(St3, St4, 1.590, -16.0, -6.0, -29.0, -29.0, -29.0),
            },
        });
        surprise25.NotesContain.Add(AbsBarNote(St1, "1d", -26.0, 12));
        surprise25.NotesContain.Add(AbsBarNote(St2, "1d", -28.0, 12));
        surprise25.NotesContain.Add(AbsBarNote(St3, "1d", -29.0, 12));
        surprise25.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        surprise25.Custom.Add(ExactlyNChecks(3));
        surprise25.Custom.Add(ChecksStayOutOfTheMail);
        surprise25.Custom.Add(DecidedPeriodIsGone);
        surprise25.Custom.Add(EveryMidIsAPrint);
        surprise25.Custom.Add(SignsAllOneWay(-1));
        surprise25.Custom.Add(PricedReachesTheCards(-3.0, -10.0, -16.0));
        yield return surprise25;

        // ============================================================ 19
        // SURPRISE 50bp CUT. Same shape, twice the size.
        //
        //  contract      pre-decision   live      day move
        //  St0 (D+6)        2.000       1.500      -50.0
        //  St1              1.990       1.480      -51.0
        //  St2              1.940       1.410      -53.0
        //  St3              1.900       1.345      -55.5
        //
        // ref    = 1.500
        // Priced = (1.480-1.500)x100 = -2.0 ; (1.410-1.500)x100 = -9.0 ; (1.345-1.500)x100 = -15.5
        // Step   = blank ; -9.0-(-2.0) = -7.0 ; -15.5-(-9.0) = -6.5
        // d1 = w1 = m1 = -51.0 / -53.0 / -55.5
        //
        // CAN A REAL 50bp SURPRISE TRIP THE NEIGHBOUR-MISPRINT GUARD? Worked through: no.
        // The guard (src\RateDesk.Core\PricingServiceBoards.cs:917-928) rejects interior row n when
        //     |m(n) - (m(n-1)+m(n+1))/2| > 25bp   AND   |m(n-1) - m(n+1)| < 25bp.
        // Writing s1 = m(n)-m(n-1) and s2 = m(n+1)-m(n),
        //     m(n) - (m(n-1)+m(n+1))/2 = (s1 - s2)/2,
        // so it tests the CURVATURE of the strip, not its level. A surprise shifts the whole strip
        // together and leaves s1 and s2 alone: here s1 = -7.0bp and s2 = -6.5bp, so the test
        // statistic is 0.25bp against a 25bp bar. Only a V - one step down and the next step back
        // up, the two differing by more than 50bp - can trip it, and no two consecutive meeting
        // periods price that. Row 1 is never judged (no lower neighbour) and row 3 has no quoted
        // upper neighbour, so only row 2 is even eligible. EveryMidIsAPrint asserts it.
        //
        // OutlierGuard: every row clears the 12bp d1 bar AND the 30bp w1 bar AND the 50bp m1 bar
        // (51.0 / 53.0 / 55.5) - nine CHECK notes off one honest decision. The cross-sectional
        // test still needs >= 4 rows, so it adds nothing.
        var surprise50 = new ScenarioSpec
        {
            Id = 19,
            Name = "ECB SURPRISE 50bp cut - twice the size, nothing priced",
            Question = "Does anything clip, reject or interpolate away a genuine 50bp move - and " +
                       "how many CHECK notes does one honest emergency-sized decision produce?",
        };
        surprise50.Banks.Add(Ecb(2.000,
            p0: new Lvl(2.000, 2.000, 1.500),
            p1: new Lvl(1.990, 1.990, 1.480),
            p2: new Lvl(1.940, 1.940, 1.410),
            p3: new Lvl(1.900, 1.900, 1.345)));
        surprise50.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = 1.500, Rebased = true,
            Front = new FrontExpect(Dec1, St1, 1.480, 1.500, -2.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(St1, St2, 1.480, -2.0, null, -51.0, -51.0, -51.0),
                new(St2, St3, 1.410, -9.0, -7.0, -53.0, -53.0, -53.0),
                new(St3, St4, 1.345, -15.5, -6.5, -55.5, -55.5, -55.5),
            },
        });
        foreach (var (row, v) in new[] { (St1, -51.0), (St2, -53.0), (St3, -55.5) })
        {
            surprise50.NotesContain.Add(AbsBarNote(row, "1d", v, 12));
            surprise50.NotesContain.Add(AbsBarNote(row, "1w", v, 30));
            surprise50.NotesContain.Add(AbsBarNote(row, "1m", v, 50));
        }
        surprise50.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        surprise50.Custom.Add(ExactlyNChecks(9));
        surprise50.Custom.Add(ChecksStayOutOfTheMail);
        surprise50.Custom.Add(DecidedPeriodIsGone);
        surprise50.Custom.Add(EveryMidIsAPrint);
        surprise50.Custom.Add(SignsAllOneWay(-1));
        surprise50.Custom.Add(PricedReachesTheCards(-2.0, -9.0, -15.5));
        yield return surprise50;

        // ============================================================ 20
        // HAWKISH HOLD. A 25bp cut was priced; the bank held. The forward strip RISES on the day,
        // the just-decided period rolls off, and the re-base picks up the UNCHANGED rate off that
        // period's own OIS.
        //
        //  contract      pre-decision   live      day move
        //  St0 (D+6)        1.760       1.995      +23.5   <- back to ~the unchanged 2.000
        //  St1              1.700       1.930      +23.0
        //  St2              1.640       1.860      +22.0
        //  St3              1.590       1.800      +21.0
        //
        // ref    = 1.995 (re-based onto St0's own OIS, which now prints the rate that did NOT move)
        // Priced = (1.930-1.995)x100 = -6.5 ; (1.860-1.995)x100 = -13.5 ; (1.800-1.995)x100 = -19.5
        // Step   = blank ; -13.5-(-6.5) = -7.0 ; -19.5-(-13.5) = -6.0
        // d1 = w1 = m1 = +23.0 / +22.0 / +21.0
        //
        // The signs deliberately DISAGREE here: the day change is +23bp (hawkish) while Priced
        // stays negative (the strip still prices easing from here). That is the correct reading
        // and the one a sign-flip bug would destroy, so no SignsAllOneWay check on this one.
        //
        // OutlierGuard: +23.0/+22.0/+21.0 all clear the 12bp d1 bar, none clears 30bp (w1) or
        // 50bp (m1) => exactly three CHECK notes.
        var hawkishHold = new ScenarioSpec
        {
            Id = 20,
            Name = "ECB HOLDS - a 25bp cut was priced (hawkish hold)",
            Question = "When the bank does nothing and the strip jumps 23bp, does the decided " +
                       "period still roll off, and does the re-base pick up the UNCHANGED rate " +
                       "rather than a phantom move?",
        };
        hawkishHold.Banks.Add(Ecb(2.000,
            p0: new Lvl(1.760, 1.760, 1.995),
            p1: new Lvl(1.700, 1.700, 1.930),
            p2: new Lvl(1.640, 1.640, 1.860),
            p3: new Lvl(1.590, 1.590, 1.800)));
        hawkishHold.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = 1.995, Rebased = true,
            Front = new FrontExpect(Dec1, St1, 1.930, 1.995, -6.5, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(St1, St2, 1.930, -6.5, null, +23.0, +23.0, +23.0),
                new(St2, St3, 1.860, -13.5, -7.0, +22.0, +22.0, +22.0),
                new(St3, St4, 1.800, -19.5, -6.0, +21.0, +21.0, +21.0),
            },
        });
        hawkishHold.NotesContain.Add(AbsBarNote(St1, "1d", +23.0, 12));
        hawkishHold.NotesContain.Add(AbsBarNote(St2, "1d", +22.0, 12));
        hawkishHold.NotesContain.Add(AbsBarNote(St3, "1d", +21.0, 12));
        hawkishHold.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        hawkishHold.Custom.Add(ExactlyNChecks(3));
        hawkishHold.Custom.Add(ChecksStayOutOfTheMail);
        hawkishHold.Custom.Add(DecidedPeriodIsGone);
        hawkishHold.Custom.Add(EveryMidIsAPrint);
        hawkishHold.Custom.Add(PricedReachesTheCards(-6.5, -13.5, -19.5));
        hawkishHold.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // a hold must not be published as a move: the base the strip is measured against has
            // to be the rate that did NOT change, to within the run-down OIS's own basis
            var run = s.Run("ECB");
            if (run?.RefPct is { } r && Math.Abs(r - 2.000) * 100.0 > 1.0)
                msgs.Add($"the re-based fixing {r.ToString("0.000", Inv)} is more than 1bp from the " +
                         "UNCHANGED policy rate 2.000 - a hold has been re-based onto a move");
            return msgs;
        });
        yield return hawkishHold;

        // ============================================================ 21
        // INTER-MEETING EMERGENCY CUT - where two of the roll rules collide.
        //
        //   S2 ..... S1 ....|today = DecE|.. StE ..... Dec1 .... St1 ....... Dec2 .. St2 ...
        //   settled  settled  UNSCHEDULED    +6d       +12d      +18d
        //
        // DecE is announced TODAY, off calendar, and takes effect at StE = D+6. The REGULARLY
        // SCHEDULED Governing Council sits at Dec1 = D+12 - twelve days out, i.e. INSIDE the
        // 14-day window MeetingRungMap clusters over - and its own period starts at St1 = D+18.
        //
        // MODELLED FEED STATE: the strip has re-pointed and quotes the new period, because
        // nothing else can publish it (ResolveMeetingDates is driven by ticker maturities). So
        // today rung 1 is the emergency period and rungs 2..4 are St1/St2/St3. HISTORY is the
        // PRE-EMERGENCY strip: yesterday there was no D+6 period at all, so EESF1A was St1,
        // EESF2A was St2, EESF3A was St3 and EESF4A was the D+165 period. Those are E_PreBounds.
        //
        //  contract        yesterday   live      its own day move
        //  StE  (D+6)         n/a      1.500     n/a - the period did not exist yesterday
        //  St1  (D+18)       1.930     1.470     (1.470-1.930)x100 = -46.0
        //  St2  (D+67)       1.890     1.400     (1.400-1.890)x100 = -49.0
        //  St3  (D+116)      1.850     1.345     (1.345-1.850)x100 = -50.5
        //  (D+165)           1.820      -        present only as rung 4's prior close
        //
        // WHAT SHOULD BE PUBLISHED
        //  * the gate: DecisionFor(decisions, StE) = DecE (6 days, inside the 10-day settlement
        //    lag) and it is announced, so the emergency period leaves the board. The next front
        //    pairs with Dec1, which has not happened, so the shift stops at one. Rows: St1/St2/St3.
        //  * the re-base: quotes[0] after the shift IS the emergency period, eff D+6 >= DecE, so
        //    ref = 1.500 - the new policy rate straight off the market print. Rebased = true.
        //  * Priced = (1.470-1.500)x100 = -3.0 ; (1.400-1.500)x100 = -10.0 ; (1.345-1.500)x100 = -15.5
        //    Step   = blank ; -10.0-(-3.0) = -7.0 ; -15.5-(-10.0) = -5.5
        //    (CORRECTED after the first run: the third row was first written -16.5 / -6.5, copied
        //     across from scenario 19 whose third row sits 1bp lower against the same ref. The
        //     arithmetic above is the right one - 1.500 - 1.345 = 0.155 = 15.5bp - and the product
        //     agreed with it; my derivation, not the product, was wrong.)
        //  * the front line pairs St1 with Dec1 (6 days, within the lag), NOT with today's
        //    emergency (18 days away, outside it).
        //  * every change column is that contract's OWN move: -46.0 / -49.0 / -50.5 on 1d, 1w and
        //    1m alike, the pre-emergency level being flat back through the 1m anchor.
        //
        // AND THE ROLL BOUNDARIES. MeetingRungMap clusters candidates at 14 days keeping the
        // EARLIEST of each cluster (src\RateDesk.Core\MeetingRungMap.cs:44-48), on the documented
        // premise that "no two real meetings of one bank sit within 14 days". An inter-meeting
        // decision breaks precisely that premise: DecE (today) and Dec1 (D+12) are twelve days
        // apart, so Dec1 - a real announcement, a date this family genuinely renumbers on - is
        // dropped. Asserted directly below: from D+12 the feed renumbers and the stitcher will
        // not know.
        var emergency = new ScenarioSpec
        {
            Id = 21,
            Name = "ECB INTER-MEETING emergency cut, 12 days before the scheduled meeting",
            Question = "An unscheduled 50bp cut today, effective in six days, with the regular " +
                       "meeting twelve days out: does the board roll and re-base correctly, do the " +
                       "change columns still measure each contract against itself, and does the " +
                       "14-day cluster keep the scheduled announcement as a roll boundary?",
        };
        emergency.Banks.Add(Emergency());
        emergency.Expect.Add(new BankExpect
        {
            Bank = "ECB",
            Fixing = 1.500, Rebased = true,
            Front = new FrontExpect(E_Dec1, E_St1, 1.470, 1.500, -3.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(E_St1, E_St2, 1.470, -3.0, null, -46.0, -46.0, -46.0),
                new(E_St2, E_St3, 1.400, -10.0, -7.0, -49.0, -49.0, -49.0),
                new(E_St3, E_St4, 1.345, -15.5, -5.5, -50.5, -50.5, -50.5),
            },
        });
        emergency.NotesContain.Add("CHECK: ECB");        // a 50bp day cannot pass unflagged
        emergency.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
        emergency.Custom.Add(ChecksStayOutOfTheMail);
        emergency.Custom.Add(EveryMidIsAPrint);
        emergency.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var sched = MeetingsStore.Schedules.First(x =>
                x.Name.Equals("ECB", StringComparison.OrdinalIgnoreCase));
            var map = new MeetingRungMap(sched);
            // The 14-day cluster still drops the scheduled announcement when an unscheduled one
            // lands inside its window - that is inherent to clustering, and the config invariant
            // it rests on ("no two real meetings within 14 days") is exactly what an emergency
            // meeting breaks. What CHANGED (fix 2026-08-27) is that the numbering no longer
            // depends on it: MeetingRungMap now prefers Bloomberg's own per-day record of what
            // each rung pointed at, so the derived boundary list is a fallback rather than the
            // authority, and the change columns above come out right regardless.
            //
            // The residual exposure is history OLDER than the store's maturity records. Assert
            // the thing that actually matters - that the published changes are each contract's
            // own move - and keep the boundary observation as a note, not a failure.
            var pat = sched.Tickers.First(t => t.Contains("{N}"));
            var withRecords = new MeetingRungMap(sched, null,
                (n, d) => s.Store.EffectiveOn(pat.Replace("{N}", n.ToString()) + " Curncy", d));
            if (withRecords.RungFor(E_St1, Cal.D(-1)) is { } r && r != 1)
                msgs.Add($"with the store's own records in hand, {D(E_St1)} should resolve to rung 1 " +
                         $"yesterday - before the emergency existed, the market had one fewer rung - " +
                         $"but the map says {r}");
            if (map.RungFor(E_St1, Cal.D(-1)) == 1)
                msgs.Add("the DERIVED boundary list resolved this correctly, which means the " +
                         "scenario is no longer exercising the case it was built for");
            if (!map.IsBoundary(E_Dec0))
                msgs.Add($"the unscheduled announcement {D(E_Dec0)} is not a roll boundary");
            var run = s.Run("ECB");
            if (run != null && run.Rows.Any(r => r.Date == E_StE))
                msgs.Add($"the emergency-decided period ({D(E_StE)}) is still published");
            return msgs;
        });
        yield return emergency;
    }

    // ---------------------------------------------------------------- 21's calendar and market

    private static readonly DateTime E_S2 = Cal.D(-92), E_S1 = Cal.D(-50);
    private static readonly DateTime E_Dec0 = Cal.D(0), E_StE = Cal.D(6);       // the unscheduled cut
    private static readonly DateTime E_Dec1 = Cal.D(12), E_St1 = Cal.D(18);     // the scheduled meeting
    private static readonly DateTime E_Dec2 = Cal.D(61), E_St2 = Cal.D(67);
    private static readonly DateTime E_Dec3 = Cal.D(110), E_St3 = Cal.D(116);
    private static readonly DateTime E_St4 = Cal.D(165);

    /// <summary>The renumber dates as they stood BEFORE the unscheduled decision - the numbering
    /// every pre-emergency mark was recorded under. There is NO boundary on D+0: nobody knew an
    /// announcement was coming and the D+6 period did not exist, so yesterday EESF1A was the D+18
    /// period. D+159 is the announcement for the D+165 period (start - 6), needed so rung 4
    /// resolves for that contract.</summary>
    private static readonly DateTime[] E_PreBounds =
        { Cal.D(-98), Cal.D(-56), E_Dec1, E_Dec2, E_Dec3, Cal.D(159) };

    private static BankSpec Emergency()
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        // the config the desk must keep after an inter-meeting decision: the new effective date
        // joins the period grid, the unscheduled announcement joins decisionDates
        b.Dates.AddRange(new[] { E_S2, E_S1, E_StE, E_St1, E_St2, E_St3 });
        b.DecisionDates.AddRange(new[] { E_Dec0, E_Dec1, E_Dec2, E_Dec3 });
        b.Fix(2.000).FixHist(HistFrom, HistTo, 2.000);

        // live quotes: the strip has re-pointed and now carries the emergency period on rung 1.
        // each rung's PrevClose is the contract THAT TICKER pointed at yesterday, one rung out.
        b.Quote(0, mid: 2.000, prevClose: 2.000, eff: E_S1, mat: E_StE);
        b.Quote(1, mid: 1.500, prevClose: 1.930, eff: E_StE, mat: E_St1);
        b.Quote(2, mid: 1.470, prevClose: 1.890, eff: E_St1, mat: E_St2);
        b.Quote(3, mid: 1.400, prevClose: 1.850, eff: E_St2, mat: E_St3);
        b.Quote(4, mid: 1.345, prevClose: 1.820, eff: E_St3, mat: E_St4);

        // history under the PRE-EMERGENCY numbering; no series for the D+6 period, which did not
        // exist before today
        b.Contract(E_S1, E_PreBounds, HistFrom, HistTo, 2.000);
        b.Contract(E_St1, E_PreBounds, HistFrom, HistTo, 1.930);
        b.Contract(E_St2, E_PreBounds, HistFrom, HistTo, 1.890);
        b.Contract(E_St3, E_PreBounds, HistFrom, HistTo, 1.850);
        b.Contract(E_St4, E_PreBounds, HistFrom, HistTo, 1.820);
        return b;
    }
}
