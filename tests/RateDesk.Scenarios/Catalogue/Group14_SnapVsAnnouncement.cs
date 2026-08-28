using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE FOMC ANNOUNCES AFTER THE CLOSE.
///
/// Two clocks govern a decision-day run, and for one bank they are in the wrong order:
///
///   · the desk's official snap is 16:15 London (SnapDiscipline.SnapAt). From 16:15 the published
///     marks are PINNED to that snap - "pressing at 17:00 must not publish 17:00 marks as the
///     close" - so every meeting ticker's mid is overwritten with its 16:15 bar;
///   · the front roll fires at the bank's announcement time (meetings.json decisionTimeLondon).
///     Nine of the ten runs announce before 16:15. The FOMC announces at 19:00.
///
/// So a daily run pressed after a Fed statement carries 16:15 marks - taken while the market was
/// still waiting - on a board whose front row has already rolled past the decision. This scenario
/// builds exactly that state and records what the desk would read.</summary>
public static class Group14_SnapVsAnnouncement
{
    public static IEnumerable<ScenarioSpec> All()
    {
        DateTime pB = Cal.D(-84), pA = Cal.D(-42), d0 = Cal.D(0);
        DateTime m1 = Cal.D(42), m2 = Cal.D(84), m3 = Cal.D(126), m4 = Cal.D(168);
        var bounds = new[] { pB, pA, d0, m1, m2, m3, m4 };

        const double fix = 3.900;
        // the 16:15 marks: a 25bp cut ~fully priced, but not yet delivered
        const double cur = 3.660;    // the period starting today
        const double v1 = 3.560, v2 = 3.500, v3 = 3.460, v4 = 3.430;

        var spec = new ScenarioSpec
        {
            Id = 58,
            Name = "FOMC day: the 16:15 close precedes the 19:00 statement",
            Question = "A run pressed after the Fed statement publishes 16:15 marks on a board " +
                       "that has already rolled. What does the desk actually send?",
        };

        // the REAL FOMC time, and marks pinned to the 16:15 snap - a run pressed at 19:30
        spec.MarksAsOfLondon = new TimeSpan(16, 15, 0);
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = "19:00" };
        b.Dates.AddRange(new[] { pB, pA, d0, m1, m2, m3, m4 });
        b.DecisionDates.AddRange(new[] { d0, m1, m2, m3, m4 });
        b.Fix(fix).FixHist(Cal.D(-70), Cal.D(-1), fix);

        // SnapDiscipline has pinned every mid to its 16:15 bar - PRE-statement prices, and the
        // feed has not re-pointed either (it re-points at the announcement, an hour from now)
        b.Quote(0, mid: fix, prevClose: fix, eff: pA, mat: d0);
        b.Quote(1, mid: cur, prevClose: cur, eff: d0, mat: m1);
        b.Quote(2, mid: v1, prevClose: v1, eff: m1, mat: m2);
        b.Quote(3, mid: v2, prevClose: v2, eff: m2, mat: m3);
        b.Quote(4, mid: v3, prevClose: v3, eff: m3, mat: m4);

        // a quiet week into the meeting: nothing moved, because nothing had happened by 16:15
        b.Contract(pA, bounds, Cal.D(-70), Cal.D(-1), fix);
        b.Contract(d0, bounds, Cal.D(-70), Cal.D(-1), cur);
        b.Contract(m1, bounds, Cal.D(-70), Cal.D(-1), v1);
        b.Contract(m2, bounds, Cal.D(-70), Cal.D(-1), v2);
        b.Contract(m3, bounds, Cal.D(-70), Cal.D(-1), v3);
        b.Contract(m4, bounds, Cal.D(-70), Cal.D(-1), v4);
        spec.Banks.Add(b);

        // Priced against the (pre-cut, and in any case day-in-arrears) EFFR of 3.900:
        //   m1: (3.560 - 3.900) * 100 = -34.0 ; m2: -40.0 ; m3: -44.0
        // and no change anywhere, because by 16:15 nothing had happened.
        spec.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            // FIXED 2026-08-27: the board is gated by the clock the PRICES belong to. At 16:15
            // the statement has not happened, so nothing rolls and nothing re-bases - the run is
            // the close, and the close came first.
            //   Priced = (mid - 3.900) x 100 : -24.0 / -34.0 / -40.0 / -44.0
            Fixing = fix,
            Rebased = false,
            Front = new FrontExpect(d0, d0, cur, fix, -24.0, Rebased: false),
            Rows = new List<RowExpect>
            {
                new(d0, m1, cur, -24.0, null, 0.0, 0.0, 0.0),
                new(m1, m2, v1, -34.0, -10.0, 0.0, 0.0, 0.0),
                new(m2, m3, v2, -40.0, -6.0, 0.0, 0.0, 0.0),
                new(m3, m4, v3, -44.0, -4.0, 0.0, 0.0, 0.0),
            },
        });

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("FOMC")!;

            // 1. the row the market was pricing when these marks were taken must STILL be on
            //    the board - the statement came after the close
            if (run.Rows.Any(r => r.Date == d0) == false)
                msgs.Add($"the 16:15 closing run dropped the period starting {d0:dd-MMM-yy} because " +
                         "of a statement that had not happened when these marks were taken - the " +
                         "board's shape would know something none of its numbers do.");

            // 2. ...and the run says so, rather than leaving a reader to wonder
            if (!s.Notes.Any(n => n.Contains("after this run", StringComparison.Ordinal)))
                msgs.Add("nothing in the run says the FOMC statement lands after these marks");
            return msgs;
        });
        yield return spec;
    }
}
