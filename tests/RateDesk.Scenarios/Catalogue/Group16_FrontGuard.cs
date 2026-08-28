using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE MISPRINT GUARD'S TWO ARMS DISAGREE ABOUT THE FRONT ROW.
///
/// Thin meeting-OIS families misprint with a straight face, so an interior rung more than 25bp
/// from its neighbours' midpoint (while those neighbours agree with each other) is replaced by
/// that midpoint. The LIVE guard deliberately refuses to judge the FRONT row:
///
///     int lo = k - 1, hi = k + 1;  ...  if (lo >= 1 && ...)     // PricingServiceBoards.cs:924
///     // "Edge rows are never judged - the front meeting is the one that gaps for real."
///
/// The STITCHER carries its own copy of the same guard, for the same reason, but keys the
/// exemption on the generic index rather than the row position:
///
///     if (loN != null &amp;&amp; hiN != null &amp;&amp; idx - 1 >= 1)        // PricingServiceBoards.cs:1506
///
/// On a decision day the front published row's recent history is read at idx = 2 - the newest
/// window starts at today's boundary and contains only today - so the test passes and the FRONT
/// contract's own closes are rewritten to the neighbour midpoint. The published Mid keeps the
/// real print; the change columns are differenced against the fabricated one.</summary>
public static class Group16_FrontGuard
{
    public static IEnumerable<ScenarioSpec> All()
    {
        DateTime P2 = Cal.D(-84), P1 = Cal.D(-42), D0 = Cal.D(0);
        DateTime M1 = Cal.D(42), M2 = Cal.D(84), M3 = Cal.D(126), M4 = Cal.D(168);
        var bounds = new[] { P2, P1, D0, M1, M2, M3, M4 };

        const double fix = 2.500;
        const double cur = 2.460;                        // the period the decision starts today
        const double m1Was = 2.480, m1Now = 2.150;       // the front row gaps, from yesterday
        const double m2 = 2.490, m3 = 2.500, m4 = 2.505;

        var spec = new ScenarioSpec
        {
            Id = 62,
            Name = "A legitimately gapping front row on a decision day - guarded in history, not live",
            Question = "The live guard spares the front row because the front is the one that gaps " +
                       "for real. Does the stitched series that feeds its change columns spare it too?",
        };

        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { P2, P1, D0, M1, M2, M3, M4 });
        b.DecisionDates.AddRange(new[] { D0, M1, M2, M3, M4 });
        b.Fix(fix).FixHist(Cal.D(-70), Cal.D(-1), fix);

        // feed not re-pointed: rung 1 still quotes the period starting today
        b.Quote(0, mid: fix, prevClose: fix, eff: P1, mat: D0);
        b.Quote(1, mid: cur, prevClose: cur, eff: D0, mat: M1);
        b.Quote(2, mid: m1Now, prevClose: m1Now, eff: M1, mat: M2);
        b.Quote(3, mid: m2, prevClose: m2, eff: M2, mat: M3);
        b.Quote(4, mid: m3, prevClose: m3, eff: M3, mat: M4);

        b.Contract(P1, bounds, Cal.D(-70), Cal.D(0), fix);
        b.Contract(D0, bounds, Cal.D(-70), Cal.D(0), cur);
        // the front contract re-priced YESTERDAY and has stayed there: 2.480 -> 2.150
        b.ContractStep(M1, bounds, Cal.D(-70), Cal.D(0), Cal.Bd(-1), m1Was, m1Now);
        b.Contract(M2, bounds, Cal.D(-70), Cal.D(0), m2);
        b.Contract(M3, bounds, Cal.D(-70), Cal.D(0), m3);
        b.Contract(M4, bounds, Cal.D(-70), Cal.D(0), m4);
        spec.Banks.Add(b);

        // WHAT THE DESK SHOULD READ, derived by hand.
        //   Priced = (mid - 2.500) x 100  ->  -35.0 / -1.0 / 0.0
        //   Step   = blank / +34.0 / +1.0
        //   the front contract closed at 2.150 YESTERDAY and is 2.150 now  -> d1 = 0.0
        //   a week and a month ago the same contract closed at 2.480       -> w1 = m1 = -33.0
        //   the other two contracts never moved                            -> 0.0 across
        spec.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = 2.460,
            Rebased = true,
            Rows = new List<RowExpect>
            {
                new(M1, M2, m1Now, -31.0, null, 0.0, -33.0, -33.0),
                new(M2, M3, m2, +3.0, +34.0, 0.0, 0.0, 0.0),
                new(M3, M4, m3, +4.0, +1.0, 0.0, 0.0, 0.0),
            },
        });

        spec.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var run = s.Run("FOMC")!;
            var front = run.Rows.FirstOrDefault();
            if (front == null) return msgs;

            // the live mid must be the real print - the guard's front exemption working
            if (Math.Abs(front.MidPct - m1Now) > 1e-9)
                msgs.Add($"the published front mid is {front.MidPct:0.000}; the live guard is " +
                         $"supposed to spare the front row, whose real print is {m1Now:0.000}");
            if (front.MidSource.StartsWith("interp", StringComparison.OrdinalIgnoreCase))
                msgs.Add("the live guard rejected the FRONT row's print - it is meant to be exempt");

            // ...and the row must not then difference that real print against a fabricated anchor
            double midpoint = (cur + m2) / 2.0;
            if (front.D1Bp is { } d1 && Math.Abs(d1 - (m1Now - midpoint) * 100.0) < 0.5)
                msgs.Add($"the front row publishes Mid {front.MidPct:0.000} and a 1d change of " +
                         $"{d1:+0.0;-0.0;0.0}bp on a contract that did not move today. The anchor " +
                         $"is {midpoint:0.000} - the midpoint of the two neighbouring generics - " +
                         "so the same row asserts a real price and a change measured from an " +
                         "invented one. No dagger, no CHECK note, nothing marks it.");
            return msgs;
        });
        yield return spec;
    }
}
