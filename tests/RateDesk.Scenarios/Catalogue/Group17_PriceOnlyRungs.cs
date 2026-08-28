using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE EMERGENCY CUT ON A MACHINE THAT HAS NO MATURITY HISTORY.
///
/// Scenario 21 proves the inter-meeting fault is fixed when the store has recorded what each rung
/// pointed at on each past day. But that recording only began on 2026-08-26 and accrues one run at
/// a time, so for the next few weeks every 1m lookback reaches back past it - and an emergency cut
/// is exactly the event that will not wait.
///
/// PRICES, though, ARE backfilled: the first run on a machine seeds 45 days per ticker. So these
/// two scenarios withhold the maturity records entirely (`RecordMaturities = false`, i.e. a store
/// that has prices and nothing else) and ask whether the strip's own price history is enough.
///
/// 63 is the emergency. 64 is its control: the identical market with NO emergency, which must come
/// out right too - a detector that "fixes" the broken case by breaking the sound one is worthless.</summary>
public static class Group17_PriceOnlyRungs
{
    // the ECB's ordinary grid, announcements six days before each period start
    private static readonly DateTime A0 = Cal.D(-98), S0 = Cal.D(-92);
    private static readonly DateTime A1 = Cal.D(-49), S1 = Cal.D(-43);
    private static readonly DateTime Sched = Cal.D(12), SchedSt = Cal.D(18);
    private static readonly DateTime A3 = Cal.D(61), S3 = Cal.D(67);
    private static readonly DateTime A4 = Cal.D(110), S4 = Cal.D(116);
    private static readonly DateTime A5 = Cal.D(159), S5 = Cal.D(165);
    // two more meetings that the family quotes but the run does not publish. Real strips are
    // deeper than the run they feed, and the scan needs a rung ABOVE the deepest published one
    // to check it against - so the scenario has to be as deep as the real thing.
    private static readonly DateTime A6 = Cal.D(208), S6 = Cal.D(214);
    private static readonly DateTime A7 = Cal.D(257), S7 = Cal.D(263);

    private const double Fix = 2.000;
    // a sloped strip, so the renumbering is legible in the prices
    private const double VCur = 2.050;    // the period running now (started S1)
    private const double VSchd = 2.200;   // the period starting SchedSt
    private const double V3 = 2.320;
    private const double V4 = 2.430;
    private const double V5 = 2.520;
    private const double V6 = 2.600;
    private const double V7 = 2.660;

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 63
        {
            // An unscheduled 50bp cut is announced TODAY, effective in six days. Yesterday the
            // strip had no such period: EESF1A was the SchedSt contract. Today a new front exists
            // and every contract has been pushed one rung further out.
            DateTime emg = Cal.D(0), emgSt = Cal.D(6);
            const double VEmg = 1.550;      // the cut period, 50bp below

            var spec = new ScenarioSpec
            {
                Id = 63,
                Name = "Emergency cut, price history only - no maturity records at all",
                Question = "On a machine whose store holds the 45-day price seed and nothing else, " +
                           "does an inter-meeting decision still leave every change column reading " +
                           "its own contract?",
            };

            var b = new BankSpec
            {
                Bank = "ECB",
                DecisionTimeLondon = Cal.TimePassed,
                RecordMaturities = false,        // the whole point: prices, and nothing else
            };
            b.Dates.AddRange(new[] { S0, S1, emgSt, SchedSt, S3, S4, S5, S6, S7 });
            b.DecisionDates.AddRange(new[] { emg, Sched, A3, A4, A5, A6, A7 });
            b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

            // TODAY, after the announcement: a new front, everything pushed out one
            b.Quote(0, mid: VCur, prevClose: VCur, eff: S1, mat: emgSt);
            b.Quote(1, mid: VEmg, prevClose: VSchd, eff: emgSt, mat: SchedSt);
            b.Quote(2, mid: VSchd, prevClose: V3, eff: SchedSt, mat: S3);
            b.Quote(3, mid: V3, prevClose: V4, eff: S3, mat: S4);
            b.Quote(4, mid: V4, prevClose: V5, eff: S4, mat: S5);
            b.Quote(5, mid: V5, prevClose: V6, eff: S5, mat: S6);

            // HISTORY under the market's ACTUAL past numbering: the emergency did not exist, so
            // the grid was the ordinary one and it rolled normally when the running period was
            // decided at A1. Seeded contract by contract against that boundary list.
            // four rungs of depth after the roll: the scan needs three rungs that can tell
            // the hypotheses apart, and a rung only counts when BOTH days quote it
            var pre = new[] { A0, A1, Sched, A3, A4, A5, A6, A7 };
            var from = Cal.D(-70); var to = Cal.D(-1);
            b.Contract(S1, pre, from, to, VCur);
            b.Contract(SchedSt, pre, from, to, VSchd);
            b.Contract(S3, pre, from, to, V3);
            b.Contract(S4, pre, from, to, V4);
            b.Contract(S5, pre, from, to, V5);
            b.Contract(S6, pre, from, to, V6);
            b.Contract(S7, pre, from, to, V7);
            spec.Banks.Add(b);

            // WHAT THE DESK SHOULD READ. The strip never moved: every contract is worth exactly
            // what it was worth yesterday, so every change column is 0.0. Only the ticker numbers
            // changed. Priced is measured against the re-based emergency period (1.550):
            //   SchedSt (2.200 - 1.550) x 100 = +65.0
            //   S3      (2.320 - 1.550) x 100 = +77.0   step +12.0
            //   S4      (2.430 - 1.550) x 100 = +88.0   step +11.0
            spec.Expect.Add(new BankExpect
            {
                Bank = "ECB",
                Fixing = VEmg,
                Rebased = true,
                Front = new FrontExpect(Sched, SchedSt, VSchd, VEmg, +65.0, Rebased: true),
                Rows = new List<RowExpect>
                {
                    new(SchedSt, S3, VSchd, +65.0, null, 0.0, 0.0, 0.0),
                    new(S3, S4, V3, +77.0, +12.0, 0.0, 0.0, 0.0),
                    new(S4, S5, V4, +88.0, +11.0, 0.0, 0.0, 0.0),
                    new(S5, S6, V5, +97.0, +9.0, 0.0, 0.0, 0.0),
                },
            });
            spec.Custom.Add(s =>
            {
                var msgs = new List<string>();
                var run = s.Run("ECB")!;
                foreach (var m in run.Rows)
                    foreach (var (label, v) in new[] { ("1d", m.D1Bp), ("1w", m.W1Bp), ("1m", m.M1Bp) })
                        if (v is { } x && Math.Abs(x) > 0.05)
                            msgs.Add($"{m.Date:dd-MMM-yy}: {label} reads {x:+0.0;-0.0;0.0}bp on a strip " +
                                     "that did not move. Only the ticker numbers changed, and with no " +
                                     "maturity records the prices are the only thing that can say so.");
                // and the run must SAY it re-read the numbering, in words a junior can act on
                if (!s.Notes.Any(n => n.Contains("not numbered the way the meeting calendar implies",
                        StringComparison.Ordinal)))
                    msgs.Add("the run corrected the numbering but does not say so anywhere");
                return msgs;
            });
            yield return spec;
        }

        // ---------------------------------------------------------------- 64
        {
            // THE CONTROL. Same bank, same prices, same missing records - but no emergency. The
            // calendar is right, and the detector must leave it alone and say nothing.
            var spec = new ScenarioSpec
            {
                Id = 64,
                Name = "No emergency, price history only - the detector stays out of the way",
                Question = "With the calendar correct and no maturity records, does the price scan " +
                           "leave a sound run untouched and unremarked?",
            };

            var b = new BankSpec
            {
                Bank = "ECB",
                DecisionTimeLondon = Cal.TimeNotYetPassed,   // nothing announced yet today
                RecordMaturities = false,
            };
            b.Dates.AddRange(new[] { S0, S1, SchedSt, S3, S4, S5, S6, S7 });
            b.DecisionDates.AddRange(new[] { Sched, A3, A4, A5, A6, A7 });
            b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

            b.Quote(0, mid: VCur, prevClose: VCur, eff: S1, mat: SchedSt);
            b.Quote(1, mid: VSchd, prevClose: VSchd, eff: SchedSt, mat: S3);
            b.Quote(2, mid: V3, prevClose: V3, eff: S3, mat: S4);
            b.Quote(3, mid: V4, prevClose: V4, eff: S4, mat: S5);
            b.Quote(4, mid: V5, prevClose: V5, eff: S5, mat: S6);

            var pre2 = new[] { A0, A1, Sched, A3, A4, A5, A6, A7 };
            var from = Cal.D(-70); var to = Cal.D(-1);
            b.Contract(S1, pre2, from, to, VCur);
            b.Contract(SchedSt, pre2, from, to, VSchd);
            b.Contract(S3, pre2, from, to, V3);
            b.Contract(S4, pre2, from, to, V4);
            b.Contract(S5, pre2, from, to, V5);
            b.Contract(S6, pre2, from, to, V6);
            b.Contract(S7, pre2, from, to, V7);
            spec.Banks.Add(b);

            // nothing announced, nothing renumbered, nothing moved
            //   Priced against the printed 2.000 fixing: +20.0 / +32.0 / +43.0
            spec.Expect.Add(new BankExpect
            {
                Bank = "ECB",
                Fixing = Fix,
                Rebased = false,
                Front = new FrontExpect(Sched, SchedSt, VSchd, Fix, +20.0, Rebased: false),
                Rows = new List<RowExpect>
                {
                    new(SchedSt, S3, VSchd, +20.0, null, 0.0, 0.0, 0.0),
                    new(S3, S4, V3, +32.0, +12.0, 0.0, 0.0, 0.0),
                    new(S4, S5, V4, +43.0, +11.0, 0.0, 0.0, 0.0),
                    new(S5, S6, V5, +52.0, +9.0, 0.0, 0.0, 0.0),
                },
            });
            spec.NotesNotContain.Add("not numbered the way the meeting calendar implies");
            yield return spec;
        }
    }
}
