using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>A BANK WHOSE SETTLEMENT LAG IS NOT CONSTANT.
///
/// The meeting families re-point at the ANNOUNCEMENT. The shipped decision calendars list FUTURE
/// announcements only, so past ones are recovered by derivation: period start minus the median
/// decision-to-start lag (`MeetingCalendar.AnnouncementDates`). That derivation is offered ONLY
/// when the lag is stable to within a day (`LagIsStable`) - a sensible refusal to guess.
///
/// The BOJ's lag is not stable. In the shipped config its decision-to-start gaps run 1, 2, 3 and 6
/// days ("settlement is the next Tokyo business day", holiday-dependent). So for the BOJ the
/// derivation is switched off, no past announcement is ever a boundary, and the roll boundary
/// used for every historical lookback is the PERIOD START instead - one to six days after the
/// family actually renumbered.
///
/// This scenario asks what that costs when a change anchor lands in the gap.</summary>
public static class Group12_UnstableLag
{
    public static IEnumerable<ScenarioSpec> All()
    {
        // the settled meeting: announced 9 days ago, its period started 5 days ago (lag 4)
        DateTime aPast = Cal.D(-9), sPast = Cal.D(-5);
        // one before it, with a 6-day lag, and two ahead with 3 and 6 - a spread of 3 days,
        // which is what disables the derivation
        DateTime aOld = Cal.D(-60), sOld = Cal.D(-54);
        DateTime d1 = Cal.D(25), st1 = Cal.D(28);
        DateTime d2 = Cal.D(70), st2 = Cal.D(76);
        DateTime d3 = Cal.D(115), st3 = Cal.D(124);
        DateTime d4 = Cal.D(166), st4 = Cal.D(172);

        // WHERE THE FAMILY ACTUALLY RENUMBERS - the announcements. The synthetic tickers are
        // seeded on this, because it is what Bloomberg's generics do.
        var trueBounds = new[] { aOld, aPast, d1, d2, d3, d4 };

        const double fix = 0.500;
        const double cCur = 0.500;   // the period that started 5 days ago
        const double c1 = 0.560;     // period st1
        const double c2 = 0.610;     // period st2
        const double c3 = 0.650;     // period st3
        const double c4 = 0.680;     // period st4
        const double cOld = 0.450;   // the period before the settled one

        var spec = new ScenarioSpec
        {
            Id = 56,
            Name = "Unstable settlement lag (BOJ shape): a 1w anchor inside the gap",
            Question = "For a bank whose lag varies, the past roll boundary is taken as the period " +
                       "start rather than the announcement. What does a change anchor landing " +
                       "between the two read?",
        };

        var b = new BankSpec { Bank = "BOJ", DecisionTimeLondon = Cal.TimePassed };
        // starts, exactly as the shipped config keeps them (settled ones included)
        b.Dates.AddRange(new[] { sOld, sPast, st1, st2, st3, st4 });
        // FUTURE announcements only - the shipped shape
        b.DecisionDates.AddRange(new[] { d1, d2, d3, d4 });

        b.Fix(fix).FixHist(Cal.D(-70), Cal.D(-1), fix);

        // today's feed: rung 1 quotes the period starting st1
        b.Quote(0, mid: cCur, prevClose: cCur, eff: sPast, mat: st1);
        b.Quote(1, mid: c1, prevClose: c1, eff: st1, mat: st2);
        b.Quote(2, mid: c2, prevClose: c2, eff: st2, mat: st3);
        b.Quote(3, mid: c3, prevClose: c3, eff: st3, mat: st4);

        // a completely quiet market: every contract's rate is constant. Only the ticker number
        // each one lives under changes, and it changes at the ANNOUNCEMENT.
        b.Contract(sOld, trueBounds, Cal.D(-70), Cal.D(-1), cOld);
        b.Contract(sPast, trueBounds, Cal.D(-70), Cal.D(-1), cCur);
        b.Contract(st1, trueBounds, Cal.D(-70), Cal.D(-1), c1);
        b.Contract(st2, trueBounds, Cal.D(-70), Cal.D(-1), c2);
        b.Contract(st3, trueBounds, Cal.D(-70), Cal.D(-1), c3);
        b.Contract(st4, trueBounds, Cal.D(-70), Cal.D(-1), c4);
        spec.Banks.Add(b);

        // Nothing moved, anywhere, all month. Every change column on every row is 0.0.
        //   Priced: (0.560 - 0.500) * 100 = +6.0 ; (0.610 - 0.500) * 100 = +11.0 ; +15.0
        //   Step:   blank ; +5.0 ; +4.0
        spec.Expect.Add(new BankExpect
        {
            Bank = "BOJ",
            Fixing = fix,
            Rebased = false,
            Rows = new List<RowExpect>
            {
                new(st1, st2, c1, +6.0, null, 0.0, 0.0, 0.0),
                new(st2, st3, c2, +11.0, +5.0, 0.0, 0.0, 0.0),
                new(st3, st4, c3, +15.0, +4.0, 0.0, 0.0, 0.0),
            },
        });
        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            // the 1w anchor lands 7 days back - after the announcement (9 days ago) and before
            // the period start (5 days ago), i.e. inside the window where the app's boundary and
            // the family's own renumber disagree
            var run = s.Run("BOJ");
            if (run == null || run.Rows.Count == 0) return msgs;
            foreach (var m in run.Rows)
                if (m.W1Bp is { } w && Math.Abs(w) > 0.05)
                    msgs.Add($"{m.Date:dd-MMM-yy}: a 1w change of {w:+0.0;-0.0;0.0}bp in a week " +
                             "when this contract never moved. The anchor sits between the " +
                             $"announcement ({aPast:dd-MMM-yy}) and the period start " +
                             $"({sPast:dd-MMM-yy}); the family renumbered on the first, the run " +
                             "reads it under the numbering of the second.");
            return msgs;
        });
        yield return spec;
    }
}
