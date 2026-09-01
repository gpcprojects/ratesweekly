using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE IMPLIED BASE, BANK BY BANK (desk ask 2026-09-01): when a bank hikes or cuts,
/// Priced must be measured against the rate the decision implies — old fixing ± the delivered
/// move — from the moment the statement is out until the o/n fixing genuinely prints the new
/// rate, and must then RESET onto the printed fixing.
///
/// THE MECHANISM (desk 2026-09-01, superseding the 2026-08-11 stub-mid re-base as the primary
/// path): each bank's own POLICY TARGET ticker documents the delivered move — Δ = target now −
/// the target's last pre-decision close — and inside the announcement→effective(+fixingLagDays)
/// window the base is the FIXING PRINT + Δ. Never the decided period's OIS ("the stub mid"),
/// which carries basis and intra-period expectations; the stub survives only as the flagged
/// fallback when the target data is missing (the rest of the suite, which seeds no policy
/// quotes, exercises that fallback unchanged). The base resets to the print alone the moment
/// the fixing has genuinely moved by half the delta, and hard-stops at windowEnd regardless.
///
/// 66-74: mid-window, one per bank shape the config ships — same-day start with a 1-day
///        fixing lag (FOMC, MPC), 1-day lag (RBA, RBNZ, BOC, NORGES), ~week lag (ECB, BOJ),
///        the period-start-renumbering Riksbank — hikes and cuts mixed, 15bp and 50bp moves
///        included so the sign and the odd sizes both travel. Assertions focus on the BASE:
///        RefPct = fixing + Δ, Rebased = true, every Priced/Step measured off it. Change
///        columns are Any (Groups 03/05 own those).
/// 75-79: the reset, one per lag shape: the window has closed and the fixing prints the new
///        rate — RefPct = the PRINT, Rebased = false, no dagger.
/// 80:    the KICK-IN reset inside a still-open window (the RBNZ OCR re-prints same-day):
///        base = the print, never print + Δ twice.
///
/// All dates are BUSINESS-DAY anchored (Cal.Bd / date+7 / NextBd) — deliberately not joining
/// the weekday-fragile list in FINDINGS.md.</summary>
public static class Group19_ImpliedBase
{
    /// <summary>Period start for a decision under each shape.</summary>
    private static DateTime SameDay(DateTime d) => d;
    private static DateTime NextBd1(DateTime d) => Cal.NextBd(d.AddDays(1));
    private static DateTime Plus7(DateTime d) => d.AddDays(7);

    /// <summary>One implied-base scenario. <paramref name="starts"/> = {prevStart, st0..st4};
    /// <paramref name="decs"/> = {dec0..dec3} (dec0 = the decision under test).
    /// <paramref name="mids"/> = the three published row mids (st1, st2, st3).
    /// <paramref name="expectedBase"/> = what RefPct must equal (implied mid-window, the
    /// printed fixing after the reset). <paramref name="repointed"/> = the generics have
    /// renumbered (every family but SKSF re-points at the announcement; SKSF at the start).</summary>
    /// <summary>The shipped policy-target ticker per bank (mirrors config\meetings.json —
    /// probed by NAME 2026-09-01). The scenarios seed these so the policy-delta base runs its
    /// PRIMARY path; the stub-mid fallback is exercised by the rest of the suite, which seeds
    /// no policy quotes at all.</summary>
    private static readonly Dictionary<string, string> Policy = new()
    {
        ["FOMC"] = "FDTR Index", ["MPC"] = "UKBRBASE Index", ["ECB"] = "EUORDEPO Index",
        ["BOJ"] = "BOJDTR Index", ["RIKSBANK"] = "SWRRATEI Index", ["RBA"] = "RBATCTR Index",
        ["RBNZ"] = "NZOCRS Index", ["BOC"] = "CABROVER Index", ["NORGES"] = "NOBRDEP Index",
    };

    private static ScenarioSpec Make(int id, string name, string question, string bank,
        string? decisionTime, DateTime[] starts, DateTime[] decs,
        double liveFix, double expectedBase, bool rebased, double[] mids,
        bool repointed, double rung0Mid,
        double polOld, double polNew,
        (DateTime StepOn, double Before, double After)? fixStep = null,
        bool markTurnOff = false, bool policyIsFixing = false)
    {
        var sp = starts[0]; var st0 = starts[1];
        var st1 = starts[2]; var st2 = starts[3]; var st3 = starts[4]; var st4 = starts[5];

        var b = new BankSpec { Bank = bank, DecisionTimeLondon = decisionTime };
        if (markTurnOff) b.MarkTurnPeriods = false;   // the base is under test, not the turn
        b.Dates.AddRange(starts);
        b.DecisionDates.AddRange(decs);
        b.Fix(liveFix);
        if (fixStep is { } fs) b.FixHistStep(Cal.D(-70), Cal.D(-1), fs.StepOn, fs.Before, fs.After);
        else b.FixHist(Cal.D(-70), Cal.D(-1), liveFix);

        // the policy TARGET: live print = the post-decision level; history = old level up to
        // the decision, new level from it — the pre-decision close is what the delta reads.
        // RBNZ's target IS its fixing (the OCR): the fixing seed above already carries both,
        // and a separate policy quote would overwrite the o/n print with the announced level
        // a day before the OCR actually re-prints.
        if (!policyIsFixing)
        {
            var pol = Policy[bank];
            b.Extras.Add(new ExtraQuote(pol, Mid: polNew, PrevClose: polOld));
            foreach (var d in Cal.BusinessDays(Cal.D(-70), Cal.D(-1)))
                b.RawCloses.Add((pol, d, d < decs[0].Date ? polOld : polNew));
        }

        if (repointed)
        {
            // post-announcement numbering: rung 0 fronts the just-decided (or running) period
            b.Quote(0, mid: rung0Mid, prevClose: rung0Mid, eff: st0, mat: st1);
            b.Quote(1, mid: mids[0], prevClose: mids[0], eff: st1, mat: st2);
            b.Quote(2, mid: mids[1], prevClose: mids[1], eff: st2, mat: st3);
            b.Quote(3, mid: mids[2], prevClose: mids[2], eff: st3, mat: st4);
        }
        else
        {
            // SKSF-shaped: still OLD numbering (renumbers at the period start) — the
            // announced-gate shift must roll the decided rung 1 off and re-base onto it
            b.Quote(0, mid: liveFix, prevClose: liveFix, eff: sp, mat: st0);
            b.Quote(1, mid: rung0Mid, prevClose: rung0Mid, eff: st0, mat: st1);
            b.Quote(2, mid: mids[0], prevClose: mids[0], eff: st1, mat: st2);
            b.Quote(3, mid: mids[1], prevClose: mids[1], eff: st2, mat: st3);
            b.Quote(4, mid: mids[2], prevClose: mids[2], eff: st3, mat: st4);
        }

        double p1 = (mids[0] - expectedBase) * 100.0;
        double p2 = (mids[1] - expectedBase) * 100.0;
        double p3 = (mids[2] - expectedBase) * 100.0;

        var s = new ScenarioSpec { Id = id, Name = name, Question = question };
        s.Banks.Add(b);
        s.Expect.Add(new BankExpect
        {
            Bank = bank,
            Fixing = expectedBase, Rebased = rebased,
            Front = new FrontExpect(decs[1], st1, mids[0], expectedBase, p1, Rebased: rebased),
            Rows = new List<RowExpect>
            {
                new(st1, st2, mids[0], p1, null,          Any.Num, Any.Num, Any.Num),
                new(st2, st3, mids[1], p2, p2 - p1,       Any.Num, Any.Num, Any.Num),
                new(st3, st4, mids[2], p3, p3 - p2,       Any.Num, Any.Num, Any.Num),
            },
        });
        s.NotesNotContain.Add("CHECK");
        return s;
    }

    /// <summary>{prevStart, st0..st4} from a decision and a shape, meetings every 6 weeks
    /// (42 days keeps every derived date on the decision's own weekday).</summary>
    private static (DateTime[] Starts, DateTime[] Decs) Grid(DateTime dec0, Func<DateTime, DateTime> shape)
    {
        var decs = new[] { dec0, dec0.AddDays(42), dec0.AddDays(84), dec0.AddDays(126) };
        var starts = new[]
        {
            shape(dec0.AddDays(-42)),
            shape(decs[0]), shape(decs[1]), shape(decs[2]), shape(decs[3]),
            shape(dec0.AddDays(168)),
        };
        return (starts, decs);
    }

    public static IEnumerable<ScenarioSpec> All()
    {
        // ------------------------------------------------------------------ 66 FOMC hike +25
        // dec yesterday(bd), period started the same day, EFFR reports in arrears AND the new
        // target applies the day after the start (fixingLagDays 1) — so TODAY, one business day
        // in, the printed 4.330 is still the old rate and the base must be the implied 4.580.
        {
            var dec = Cal.Bd(-1);
            var (st, dc) = Grid(dec, SameDay);
            yield return Make(66,
                "FOMC hiked 25bp yesterday - EFFR still prints the old rate",
                "One business day after a same-day-start hike, with the fixing lawfully a day " +
                "behind, is every Priced measured against oldFix+25 = 4.580, dagger on?",
                "FOMC", null, st, dc,
                liveFix: 4.330, expectedBase: 4.580, rebased: true,
                // rows: 4.630 -> +5.0 | 4.680 -> +10.0 (step +5.0) | 4.720 -> +14.0 (step +4.0)
                mids: new[] { 4.630, 4.680, 4.720 }, repointed: true, rung0Mid: 4.580,
                polOld: 4.330, polNew: 4.580);
        }
        // ------------------------------------------------------------------- 67 MPC cut -25
        // same shape as FOMC (fixingLagDays 1 landed in config with v0.18.0 — this scenario is
        // the lock: before it, the window closed a day early and today's base would read the
        // stale 4.000 with no dagger, putting +25bp of phantom Priced on every GBP row).
        {
            var dec = Cal.Bd(-1);
            var (st, dc) = Grid(dec, SameDay);
            yield return Make(67,
                "MPC cut 25bp yesterday - SONIA still prints the old rate",
                "The morning after a Bank Rate cut, with SONIA published in arrears, is every " +
                "Priced measured against oldFix-25 = 3.750 rather than the stale 4.000?",
                "MPC", null, st, dc,
                liveFix: 4.000, expectedBase: 3.750, rebased: true,
                // rows: 3.700 -> -5.0 | 3.660 -> -9.0 (step -4.0) | 3.630 -> -12.0 (step -3.0)
                mids: new[] { 3.700, 3.660, 3.630 }, repointed: true, rung0Mid: 3.750,
                polOld: 4.000, polNew: 3.750);
        }
        // ------------------------------------------------------------------- 68 ECB hike +25
        // announced two business days ago, effective in ~5 days: the whole gap rides the
        // implied 2.250 while ESTR prints 2.000.
        {
            var dec = Cal.Bd(-2);
            var (st, dc) = Grid(dec, Plus7);
            yield return Make(68,
                "ECB hiked 25bp, the period starts next week - ESTR still 2.000",
                "Mid-way through the announcement-to-effective gap, is the base the implied " +
                "2.250 on every row, dagger on, until the deposit-rate change bites?",
                "ECB", null, st, dc,
                liveFix: 2.000, expectedBase: 2.250, rebased: true,
                mids: new[] { 2.300, 2.350, 2.390 }, repointed: true, rung0Mid: 2.250,
                polOld: 2.000, polNew: 2.250);
        }
        // ------------------------------------------------------------------- 69 BOJ hike +15
        // a non-quarter-point move: the implied base must carry the odd size exactly.
        {
            var dec = Cal.Bd(-2);
            var (st, dc) = Grid(dec, Plus7);
            yield return Make(69,
                "BOJ hiked 15bp, effective next week - TONAR still 0.750",
                "A 15bp move: is the base the implied 0.900 - the exact odd size, not a " +
                "quarter-point assumption - until TONAR prints it?",
                "BOJ", null, st, dc,
                liveFix: 0.750, expectedBase: 0.900, rebased: true,
                mids: new[] { 0.950, 1.000, 1.040 }, repointed: true, rung0Mid: 0.900,
                polOld: 0.750, polNew: 0.900);
        }
        // -------------------------------------------------------------- 70 RIKSBANK hike +25
        // the one family that does NOT re-point at the announcement: the announced-gate shift
        // must roll the decided rung off and the re-base must read that same rung's own OIS.
        {
            var dec = Cal.Bd(-2);
            var (st, dc) = Grid(dec, Plus7);
            yield return Make(70,
                "RIKSBANK hiked 25bp, SKSF renumbers only at the period start",
                "With the feed still old-numbered for another week, does the gate roll the " +
                "decided period off and re-base onto its own quote - implied 2.000?",
                "RIKSBANK", null, st, dc,
                liveFix: 1.750, expectedBase: 2.000, rebased: true,
                mids: new[] { 2.050, 2.100, 2.140 }, repointed: false, rung0Mid: 2.000,
                polOld: 1.750, polNew: 2.000, markTurnOff: true);
        }
        // -------------------------------------------------------------------- 71 RBA cut -25
        // 1-day lag, decided yesterday, effective today; AONIA reports yesterday = old rate.
        {
            var dec = Cal.Bd(-1);
            var (st, dc) = Grid(dec, NextBd1);
            yield return Make(71,
                "RBA cut 25bp yesterday, effective today - AONIA still 3.600",
                "On the period's first day, with the cash-rate fixing a day in arrears, is the " +
                "base the implied 3.350 on every AUD row?",
                "RBA", null, st, dc,
                liveFix: 3.600, expectedBase: 3.350, rebased: true,
                mids: new[] { 3.300, 3.260, 3.230 }, repointed: true, rung0Mid: 3.350,
                polOld: 3.600, polNew: 3.350);
        }
        // ------------------------------------------------------------------- 72 RBNZ cut -50
        // a DOUBLE cut announced this morning (statement out), effective tomorrow: the OCR
        // print still says 2.500 all day, the base must already say 2.000. The OCR is also the
        // policy ticker, so on the decision day itself the delta reads 0 — this scenario
        // exercises the DECISION-DAY STUB BRIDGE (the one place the decided period's OIS still
        // serves, flagged, until the target print moves).
        {
            var dec = Cal.D(0);
            var (st, dc) = Grid(dec, NextBd1);
            yield return Make(72,
                "RBNZ cut 50bp TODAY, effective tomorrow - the OCR prints 2.500 all day",
                "A 50bp surprise, announced hours ago: is the base the implied 2.000 - the " +
                "full half-point, signed right - until the OCR itself re-prints?",
                "RBNZ", Cal.TimePassed, st, dc,
                liveFix: 2.500, expectedBase: 2.000, rebased: true,
                mids: new[] { 1.950, 1.910, 1.880 }, repointed: true, rung0Mid: 2.000,
                polOld: 2.500, polNew: 2.000, policyIsFixing: true);
        }
        // -------------------------------------------------------------------- 73 BOC cut -25
        {
            var dec = Cal.Bd(-1);
            var (st, dc) = Grid(dec, NextBd1);
            yield return Make(73,
                "BOC cut 25bp yesterday, effective today - CORRA still 2.250",
                "Same 1-day shape in CAD: base = implied 2.000 on every row until CORRA " +
                "catches up?",
                "BOC", null, st, dc,
                liveFix: 2.250, expectedBase: 2.000, rebased: true,
                mids: new[] { 1.950, 1.910, 1.880 }, repointed: true, rung0Mid: 2.000,
                polOld: 2.250, polNew: 2.000);
        }
        // ----------------------------------------------------------------- 74 NORGES hike +25
        {
            var dec = Cal.Bd(-1);
            var (st, dc) = Grid(dec, NextBd1);
            yield return Make(74,
                "NORGES hiked 25bp yesterday, effective today - NOWA still 4.000",
                "And the mirror image in NOK: base = implied 4.250 until NOWA prints it?",
                "NORGES", null, st, dc,
                liveFix: 4.000, expectedBase: 4.250, rebased: true,
                mids: new[] { 4.300, 4.350, 4.390 }, repointed: true, rung0Mid: 4.250,
                polOld: 4.000, polNew: 4.250);
        }

        // ================================================================ THE RESET, by shape
        // ------------------------------------------------- 75 MPC (same-day start, fixLag 1)
        // cut three business days ago; SONIA printed the new 3.750 from the day after the
        // start. The window (eff + 1bd) closed before today: base = the PRINTED fixing, no
        // dagger. This also locks the window LENGTH: with fixingLagDays 1 the re-base held
        // through eff+1bd and not a day longer.
        {
            var dec = Cal.Bd(-3);
            var (st, dc) = Grid(dec, SameDay);
            yield return Make(75,
                "MPC cut settled - SONIA prints 3.750, the re-base is OFF",
                "Three business days after the cut, with the new Bank Rate in SONIA's print, " +
                "is Priced measured against the printed 3.750 with the dagger gone?",
                "MPC", null, st, dc,
                liveFix: 3.750, expectedBase: 3.750, rebased: false,
                mids: new[] { 3.700, 3.660, 3.630 }, repointed: true, rung0Mid: 3.750,
                polOld: 4.000, polNew: 3.750,
                fixStep: (Cal.NextBd(Cal.Bd(-3).AddDays(1)), 4.000, 3.750));
        }
        // ------------------------------------------------------------- 76 RBNZ (1-day lag)
        {
            var dec = Cal.Bd(-3);
            var st0 = NextBd1(dec);
            var (st, dc) = Grid(dec, NextBd1);
            yield return Make(76,
                "RBNZ cut settled - the OCR prints 2.000, the re-base is OFF",
                "With the OCR itself now printing the new rate, is the base the print and " +
                "the dagger gone?",
                "RBNZ", null, st, dc,
                liveFix: 2.000, expectedBase: 2.000, rebased: false,
                mids: new[] { 1.950, 1.910, 1.880 }, repointed: true, rung0Mid: 2.000,
                polOld: 2.500, polNew: 2.000,
                fixStep: (st0, 2.500, 2.000), policyIsFixing: true);
        }
        // -------------------------------------------------------------- 77 ECB (week lag)
        {
            var dec = Cal.Bd(-8);
            var st0 = Plus7(dec);
            var (st, dc) = Grid(dec, Plus7);
            yield return Make(77,
                "ECB hike settled - ESTR prints 2.250, the re-base is OFF",
                "The period started days ago and ESTR carries the new rate: base = the print, " +
                "Rebased = false, on every EUR row?",
                "ECB", null, st, dc,
                liveFix: 2.250, expectedBase: 2.250, rebased: false,
                mids: new[] { 2.300, 2.350, 2.390 }, repointed: true, rung0Mid: 2.250,
                polOld: 2.000, polNew: 2.250,
                fixStep: (Cal.NextBd(st0), 2.000, 2.250));
        }
        // ------------------------------------------- 78 RIKSBANK (renumbers at the start)
        {
            var dec = Cal.Bd(-8);
            var st0 = Plus7(dec);
            var (st, dc) = Grid(dec, Plus7);
            yield return Make(78,
                "RIKSBANK hike settled - SKSF renumbered at the start, SWESTR prints 2.000",
                "Past the period start the family has renumbered and the fixing prints the " +
                "new rate: base = the print, no gate shift, no dagger?",
                "RIKSBANK", null, st, dc,
                liveFix: 2.000, expectedBase: 2.000, rebased: false,
                mids: new[] { 2.050, 2.100, 2.140 }, repointed: true, rung0Mid: 2.000,
                polOld: 1.750, polNew: 2.000,
                fixStep: (Cal.NextBd(st0), 1.750, 2.000),
                markTurnOff: true);
        }
        // ---------------------------------------------------------------- 79 BOJ (week lag)
        {
            var dec = Cal.Bd(-8);
            var st0 = Plus7(dec);
            var (st, dc) = Grid(dec, Plus7);
            yield return Make(79,
                "BOJ hike settled - TONAR prints 0.900, the re-base is OFF",
                "The odd 15bp size again, now in the print itself: base = 0.900 exactly, " +
                "Rebased = false?",
                "BOJ", null, st, dc,
                liveFix: 0.900, expectedBase: 0.900, rebased: false,
                mids: new[] { 0.950, 1.000, 1.040 }, repointed: true, rung0Mid: 0.900,
                polOld: 0.750, polNew: 0.900,
                fixStep: (Cal.NextBd(st0), 0.750, 0.900));
        }

        // --------------------------------------------- 80 RBNZ: the KICK-IN, window still open
        // The OCR is its own fixing and re-prints the new rate the day the change takes effect,
        // while the calendar window is still open (fixLag 0, today = eff). Adding the delta to
        // an already-moved print would double-count the cut — base 1.500, Priced +45 where the
        // truth is −5. The kick-in test (fixing moved ≥ half the delta since the decision, same
        // sign) must hand the base straight to the print: 2.000, Rebased = false.
        {
            var dec = Cal.Bd(-1);
            var (st, dc) = Grid(dec, NextBd1);
            yield return Make(80,
                "RBNZ cut 50bp yesterday, effective TODAY - the OCR already prints 2.000",
                "The window is still open but the fixing has genuinely kicked in: is the base " +
                "the print itself — never print + Δ a second time — with the dagger gone?",
                "RBNZ", null, st, dc,
                liveFix: 2.000, expectedBase: 2.000, rebased: false,
                mids: new[] { 1.950, 1.910, 1.880 }, repointed: true, rung0Mid: 2.000,
                polOld: 2.500, polNew: 2.000,
                fixStep: (Cal.D(0), 2.500, 2.000), policyIsFixing: true);
        }
    }
}
