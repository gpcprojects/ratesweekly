using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>FIELDS LEAD PRICES — the 02-Sep-26 RBNZ board, locked (desk caught it, twice).
///
/// NDSF renumbers its PRICES at the period start but its FIELDS at the announcement. In the
/// window between, every rung's SW_EFF_DT/MATURITY describe the NEXT contract out while the
/// mid still prices the old one, and the run-down (rung 0) makes the impossible claim of a
/// FUTURE start with no quote. Trusting the fields labelled every mid one meeting late
/// (2.830 published as "10-Dec" when it was the October period) and stitched every change one
/// rung wrong (Δ1d −15.4 published where NAB's own monitor said −8.0 — the desk was right,
/// the app and two of its own "verifications" were wrong, because all three trusted the same
/// lying field).
///
/// The scenario reproduces the exact state: decision announced this morning, period starts
/// tomorrow, fields pre-rolled, prices not, records poisoned for today by the run itself.
/// Expected = the monitor's numbers: rows labelled one meeting EARLIER than their fields
/// claim, the decided period rolled off, and same-rung Δ1d.</summary>
public static class Group20_FieldsLeadPrices
{
    public static IEnumerable<ScenarioSpec> All()
    {
        var dec = Cal.D(0);                      // announced early this morning (time passed)
        var st0 = Cal.NextBd(dec.AddDays(1));    // the decided period starts tomorrow(ish)
        var st1 = st0.AddDays(56);               // meetings every 8 weeks, weekday-stable
        var st2 = st1.AddDays(56);
        var st3 = st2.AddDays(56);
        var st4 = st3.AddDays(56);
        var sp = st0.AddDays(-56);               // the running period's start
        var dec1 = st1.AddDays(-1);              // 1-day lag family
        var dec2 = st2.AddDays(-1);
        var dec3 = st3.AddDays(-1);

        var b = new BankSpec { Bank = "RBNZ", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { sp, st0, st1, st2, st3, st4 });
        b.DecisionDates.AddRange(new[] { dec, dec1, dec2, dec3 });
        // the OCR is its own fixing and still prints the OLD rate until the start
        b.Fix(2.500).FixHist(Cal.D(-70), Cal.D(-1), 2.500);

        // LIVE QUOTES: prices are the OLD numbering (nothing renumbered in the mids — the
        // Sept-analog period still on rung 1 at 2.756), but every FIELD is pre-rolled one out,
        // and rung 0 is the smoking gun: unquoted, claiming a FUTURE start.
        b.Quote(0, mid: null, prevClose: null, eff: st0, mat: st1);
        b.Quote(1, mid: 2.756, prevClose: 2.751, eff: st1, mat: st2);   // PRICE: [st0→st1]
        b.Quote(2, mid: 2.830, prevClose: 2.910, eff: st2, mat: st3);   // PRICE: [st1→st2]
        b.Quote(3, mid: 2.995, prevClose: 3.085, eff: st3, mat: st4);   // PRICE: [st2→st3]

        // stored closes, CONTRACT-true under the OLD numbering (prices never rolled):
        // rung 1 = the decided [st0→st1] period, rung 2 = [st1→st2], rung 3 = [st2→st3]
        b.Close(1, Cal.D(-40), Cal.D(-1), 2.751);
        b.Close(2, Cal.D(-40), Cal.D(-1), 2.910);
        b.Close(3, Cal.D(-40), Cal.D(-1), 3.085);
        // honest records through yesterday (the daily run stamped the OLD fields)
        foreach (var day in Cal.BusinessDays(Cal.D(-40), Cal.D(-1)))
        {
            b.Records.Add((1, day, st0, st1));
            b.Records.Add((2, day, st1, st2));
            b.Records.Add((3, day, st2, st3));
        }
        // today's POISONED records: the run stamps the live (pre-rolled) fields — exactly
        // what production wrote into the store on 02-Sep-26
        b.Records.Add((1, Cal.D(0), st1, st2));
        b.Records.Add((2, Cal.D(0), st2, st3));
        b.Records.Add((3, Cal.D(0), st3, st4));

        var s = new ScenarioSpec
        {
            Id = 81,
            Name = "RBNZ 02-Sep-26: fields pre-rolled, prices not - the desk's monitor is the truth",
            Question = "The announcement is out, the period starts tomorrow, and every field " +
                       "already describes the next contract while every price is the old one. " +
                       "Does the board label each mid by its PRICE's period and difference " +
                       "same-rung - the numbers NAB's own monitor showed?",
        };
        s.Banks.Add(b);

        // Hand arithmetic (the monitor's own column):
        //   the decided [st0] period (2.756 on rung 1) rolls OFF (announced);
        //   front = st1 with rung 2's PRICE 2.830 — NOT the field-claimed st2 —
        //     Δ1d = 2.830 − 2.910 (rung 2's OWN yesterday close) = −8.0;
        //   next = st2 at 2.995, Δ1d = 2.995 − 3.085 = −9.0, Step = (2.995−2.830)·100 = +16.5...
        //     Step chain: Priced(st1) = (2.830 − base)·100, base = stale pre-decision close of
        //     the decided contract = 2.751* → +7.9; Priced(st2) = +24.4; Step = +16.5.
        s.Expect.Add(new BankExpect
        {
            Bank = "RBNZ",
            Fixing = 2.751, Rebased = true,
            Front = new FrontExpect(dec1, st1, 2.830, 2.751, +7.9, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(st1, st2, 2.830, +7.9, null, -8.0, Any.Num, Any.Num),
                new(st2, st3, 2.995, +24.4, +16.5, -9.0, Any.Num, Any.Num),
            },
        });
        s.NotesNotContain.Add("FUTURES GUARD");
        yield return s;

        // ------------------------------------------------------------------ 82 (SEK negative)
        // THE REGRESSION LOCK for the fix's own first bug (desk, within the hour): a start-
        // rolling run-down is ALWAYS unquoted with a spot-forward eff — that alone must never
        // shift a board. SKSF's fields are honest (rung 1's eff IS the next start, no skip),
        // and the rows must publish exactly where the fields put them.
        var kDec0 = Cal.D(-13);                    // decided two weeks ago
        var kSt0 = Cal.D(-7);                      // period started EXACTLY a week ago — the
                                                   // Δ1w anchor lands ON the start day, whose
                                                   // post-roll records must be read honestly
                                                   // (the 26-Aug-26 SKSF shape)
        var kSt1 = kSt0.AddDays(35);               // next start ~4 weeks out
        var kSt2 = kSt1.AddDays(42);
        var kSt3 = kSt2.AddDays(42);
        var kDec1 = kSt1.AddDays(-6);
        var kDec2 = kSt2.AddDays(-6);
        var kSpot = Cal.NextBd(Cal.NextBd(Cal.D(0)));   // T+2, the run-down's daily spot claim

        var k = new BankSpec { Bank = "RIKSBANK", MarkTurnPeriods = false };
        k.Dates.AddRange(new[] { kSt0.AddDays(-42), kSt0, kSt1, kSt2, kSt3 });
        k.DecisionDates.AddRange(new[] { kDec0, kDec1, kDec2 });
        k.Fix(1.629).FixHist(Cal.D(-70), Cal.D(-1), 1.629);
        // the run-down: unquoted, spot-forward eff, maturing at the NEXT start — every day's
        // normal state for this family, and exactly what mis-fired the old detector
        k.Quote(0, mid: null, prevClose: null, eff: kSpot, mat: kSt1);
        k.Quote(1, mid: 1.730, prevClose: 1.732, eff: kSt1, mat: kSt2);   // eff == next start: NO skip
        k.Quote(2, mid: 1.859, prevClose: 1.850, eff: kSt2, mat: kSt3);
        // the contracts lived one rung further out before the start, and the records say so:
        // pre-start days carry the OLD numbering, the start day onward the NEW — exactly what
        // the store accumulated across 26-Aug-26. The Δ1w anchor below must read the START
        // DAY's own record (rung 1 = kSt1's contract at 1.732), never a one-out shift of it.
        k.Close(2, Cal.D(-12), kSt0.AddDays(-1), 1.732);
        k.Close(3, Cal.D(-12), kSt0.AddDays(-1), 1.850);
        k.Close(1, kSt0, Cal.D(-1), 1.732);
        k.Close(2, kSt0, Cal.D(-1), 1.850);
        foreach (var day in Cal.BusinessDays(Cal.D(-12), kSt0.AddDays(-1)))
        {
            k.Records.Add((2, day, kSt1, kSt2));
            k.Records.Add((3, day, kSt2, kSt3));
        }
        foreach (var day in Cal.BusinessDays(kSt0, Cal.D(-1)))
        {
            k.Records.Add((1, day, kSt1, kSt2));
            k.Records.Add((2, day, kSt2, kSt3));
        }

        var s2 = new ScenarioSpec
        {
            Id = 82,
            Name = "RIKSBANK: spot-eff run-down on a quiet day - the board must NOT shift",
            Question = "SKSF0A is unquoted with a T+2 eff every day of its life. Does the " +
                       "fields-lead shift stay OFF when rung 1's eff is exactly the next " +
                       "scheduled start, publishing the rows where Bloomberg's fields put them?",
        };
        s2.Banks.Add(k);
        // rows exactly on the fields: kSt1 @ 1.730 (Δ1d 1.730−1.732 = −0.2), kSt2 @ 1.859
        // (Δ1d +0.9); no phantom spot-dated front row, no relabelled second row. Δ1w anchors
        // ON the start day and must read its post-roll record honestly: 1.730 − 1.732 = −0.2
        // and 1.859 − 1.850 = +0.9 — the broken at-or-after test read one rung out and
        // published −12.0 (the 26-Aug −8.5/+34.2 shape).
        s2.Expect.Add(new BankExpect
        {
            Bank = "RIKSBANK",
            Fixing = 1.629, Rebased = false,
            Front = new FrontExpect(kDec1, kSt1, 1.730, 1.629, +10.1, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(kSt1, kSt2, 1.730, +10.1, null, -0.2, -0.2, Any.Num),
                new(kSt2, kSt3, 1.859, +23.0, +12.9, +0.9, +0.9, Any.Num),
            },
        });
        s2.NotesNotContain.Add("FUTURES GUARD");
        yield return s2;
    }
}
