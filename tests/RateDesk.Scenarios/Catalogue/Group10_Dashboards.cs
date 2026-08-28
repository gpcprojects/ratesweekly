using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>THE DASHBOARDS on a decision day.
///
/// The weekly run publishes 28 per-currency pages to a PUBLIC, investor-facing site, and each one
/// carries a meeting-dated OIS strip for its central bank. That strip is a SECOND derivation of
/// the same numbers: `RollingStrip.ForMeetings` takes its rows from the config period grid and
/// its values from stored closes, where the email takes rows from the tickers' own maturities and
/// values from live mids. Two derivations of one truth is exactly where a decision day breaks
/// things, so the strip gets its own scenarios.
///
/// The level a strip row publishes must be THAT CONTRACT's own most recent stored close. Since
/// the store never books today's print, that is yesterday's close, read from whichever rung
/// carried the contract YESTERDAY - which on a decision day is not the rung that carries it
/// today.</summary>
public static class Group10_Dashboards
{
    private static readonly DateTime P2 = Cal.D(-84), P1 = Cal.D(-42), D0 = Cal.D(0);
    private static readonly DateTime D1 = Cal.D(42), D2 = Cal.D(84), D3 = Cal.D(126), D4 = Cal.D(168);
    private static readonly DateTime[] Bounds = { P2, P1, D0, D1, D2, D3, D4 };

    private const double Fix = 3.900;
    // the contracts are 10bp apart, so reading the neighbouring rung is unmistakable
    private const double Pre0 = 3.660, Post0 = 3.650;   // period starting TODAY (just decided)
    private const double Pre1 = 3.560, Post1 = 3.550;   // D1
    private const double Pre2 = 3.500, Post2 = 3.490;   // D2
    private const double Pre3 = 3.460, Post3 = 3.450;   // D3
    private const double Pre4 = 3.430, Post4 = 3.420;   // D4

    /// <summary>FOMC, 25bp cut delivered today, feed not yet re-pointed.</summary>
    private static BankSpec Fomc(string decisionTime)
    {
        var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = decisionTime };
        b.Dates.AddRange(new[] { P2, P1, D0, D1, D2, D3, D4 });
        b.DecisionDates.AddRange(new[] { D0, D1, D2, D3, D4 });
        b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

        b.Quote(0, mid: Fix, prevClose: Fix, eff: P1, mat: D0);
        b.Quote(1, mid: Post0, prevClose: Pre0, eff: D0, mat: D1);
        b.Quote(2, mid: Post1, prevClose: Pre1, eff: D1, mat: D2);
        b.Quote(3, mid: Post2, prevClose: Pre2, eff: D2, mat: D3);
        b.Quote(4, mid: Post3, prevClose: Pre3, eff: D3, mat: D4);

        b.Contract(P1, Bounds, Cal.D(-70), Cal.D(-1), Fix);
        b.ContractStep(D0, Bounds, Cal.D(-70), Cal.D(0), D0, Pre0, Post0);
        b.ContractStep(D1, Bounds, Cal.D(-70), Cal.D(0), D0, Pre1, Post1);
        b.ContractStep(D2, Bounds, Cal.D(-70), Cal.D(0), D0, Pre2, Post2);
        b.ContractStep(D3, Bounds, Cal.D(-70), Cal.D(0), D0, Pre3, Post3);
        b.ContractStep(D4, Bounds, Cal.D(-70), Cal.D(0), D0, Pre4, Post4);
        return b;
    }

    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 51
        var day = new ScenarioSpec
        {
            Id = 51,
            Name = "Dashboard strip on a decision day",
            Question = "On the day the Fed cuts, does the published per-currency dashboard strip " +
                       "carry each meeting period's own level and its own 1w/1m change?",
        };
        day.Banks.Add(Fomc(Cal.TimePassed));
        day.Expect.Add(new BankExpect
        {
            Bank = "FOMC",
            Fixing = 3.650,
            Rebased = true,
            Front = new FrontExpect(D1, D1, Post1, 3.650, -10.0, Rebased: true),
            Rows = new List<RowExpect>
            {
                new(D1, D2, Post1, -10.0, null, -1.0, -1.0, -1.0),
                new(D2, D3, Post2, -16.0, -6.0, -1.0, -1.0, -1.0),
                new(D3, D4, Post3, -20.0, -4.0, -1.0, -1.0, -1.0),
            },
        });
        day.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var strip = s.Strips["FOMC"];

            // THE DERIVATION, BY HAND.
            // The strip is a CLOSE-based product: the store never books today's print, so each
            // row's level must be that contract's close from YESTERDAY. Yesterday the family had
            // not renumbered, so:
            //     the D1 contract sat on rung 2  -> 3.560
            //     the D2 contract sat on rung 3  -> 3.500
            //     the D3 contract sat on rung 4  -> 3.460
            // Its own 1w and 1m levels are the SAME contract a week / a month ago, which the
            // strip already resolves with the boundary-day step-back: 3.560 / 3.500 / 3.460.
            // A quiet week before the decision therefore means level == 1w level == 1m level,
            // i.e. a ZERO change on every row. Anything else is the neighbouring contract.
            // four rows: every contract whose rung the store can document. (It was three while
            // the level came off RungAt + ValueAsOf; resolving through RolledValue documents the
            // fourth as well.)
            var want = new (DateTime Contract, double Level)[]
            {
                (D1, Pre1), (D2, Pre2), (D3, Pre3), (D4, Pre4),
            };

            if (strip.Rows.Count != want.Length)
                msgs.Add($"dashboard strip has {strip.Rows.Count} row(s), expected {want.Length}");

            for (int i = 0; i < Math.Min(strip.Rows.Count, want.Length); i++)
            {
                var r = strip.Rows[i];
                if (r.Contract.Date != want[i].Contract.Date)
                    msgs.Add($"dashboard row {i + 1}: contract {r.Contract:dd-MMM-yy} != " +
                             $"expected {want[i].Contract:dd-MMM-yy}");
                if (r.Mid is not { } mid)
                    msgs.Add($"dashboard row {i + 1} ({r.Contract:dd-MMM-yy}): no level published");
                else if (Math.Abs(mid - want[i].Level) > 1e-9)
                    msgs.Add($"dashboard row {i + 1} ({r.Contract:dd-MMM-yy}): level {mid:0.000} != " +
                             $"expected {want[i].Level:0.000} - that is the {(mid > want[i].Level ? "PREVIOUS" : "NEXT")} " +
                             $"contract's level, read off the wrong rung on the decision day");
                // and the change the panel renders from those two levels
                if (r.Mid is { } m2 && r.WeekLevel is { } w2 && Math.Abs((m2 - w2) * 100.0) > 0.05)
                    msgs.Add($"dashboard row {i + 1} ({r.Contract:dd-MMM-yy}): renders a 1w change of " +
                             $"{(m2 - w2) * 100.0:+0.0;-0.0;0.0}bp on a week in which this contract " +
                             $"did not move (level {m2:0.000} vs 1w level {w2:0.000})");
                if (r.Mid is { } m3 && r.MonthLevel is { } mo3 && Math.Abs((m3 - mo3) * 100.0) > 0.05)
                    msgs.Add($"dashboard row {i + 1} ({r.Contract:dd-MMM-yy}): renders a 1m change of " +
                             $"{(m3 - mo3) * 100.0:+0.0;-0.0;0.0}bp on a month in which this contract " +
                             $"did not move (level {m3:0.000} vs 1m level {mo3:0.000})");
            }

            // and the email, which derives the same rows a different way, must agree on the level
            var run = s.Run("FOMC")!;
            for (int i = 0; i < Math.Min(strip.Rows.Count, run.Rows.Count); i++)
                if (strip.Rows[i].Mid is { } sm && Math.Abs(sm - run.Rows[i].MidPct) * 100.0 > 25.0)
                    msgs.Add($"dashboard and email disagree by {Math.Abs(sm - run.Rows[i].MidPct) * 100.0:0.0}bp " +
                             $"on {strip.Rows[i].Contract:dd-MMM-yy} (dashboard {sm:0.000}, email " +
                             $"{run.Rows[i].MidPct:0.000}) - two products, one day, two answers");
            return msgs;
        });
        yield return day;

        // ---------------------------------------------------------------- 52
        var before = new ScenarioSpec
        {
            Id = 52,
            Name = "Dashboard strip on the morning of a decision day (control)",
            Question = "Same day, same data, BEFORE the statement: the strip must be right, so " +
                       "any error at 51 is caused by the decision itself and not by the setup.",
        };
        before.Banks.Add(Fomc(Cal.TimeNotYetPassed));
        before.Custom.Add(s =>
        {
            var msgs = new List<string>();
            var strip = s.Strips["FOMC"];
            // Nothing has been announced, so the period starting TODAY is still being priced and
            // still belongs on the strip. The strip renders as of the store's newest close
            // (yesterday), so every row carries its own contract's yesterday close:
            //     D0 -> rung 1 -> 3.660,  D1 -> rung 2 -> 3.560,
            //     D2 -> rung 3 -> 3.500,  D3 -> rung 4 -> 3.460
            var want = new (DateTime Contract, double Level)[]
            {
                (D0, Pre0), (D1, Pre1), (D2, Pre2), (D3, Pre3),
            };
            for (int i = 0; i < Math.Min(strip.Rows.Count, want.Length); i++)
            {
                var r = strip.Rows[i];
                if (r.Contract.Date != want[i].Contract.Date)
                    msgs.Add($"pre-statement dashboard row {i + 1}: contract {r.Contract:dd-MMM-yy} " +
                             $"!= expected {want[i].Contract:dd-MMM-yy}");
                else if (r.Mid is { } mid && Math.Abs(mid - want[i].Level) > 1e-9)
                    msgs.Add($"pre-statement dashboard row {i + 1} ({r.Contract:dd-MMM-yy}): level " +
                             $"{mid:0.000} != expected {want[i].Level:0.000}");
            }
            return msgs;
        });
        yield return before;

        // ---------------------------------------------------------------- 53
        // The render happens the DAY AFTER a decision, so the store's newest close - which is
        // what the dashboards render as of - IS the renumber day. Every other consumer in the
        // codebase refuses to source a value from a boundary day, because the family re-points
        // NON-UNIFORMLY through it (the product's own probe, 30-Jul-26 MPC: "1A rolled, 2A not,
        // 3A/4A alternating"). RollingStrip.Build's published LEVEL is the one place that reads
        // it anyway: it maps the rung as of asOf but reads a close from a day whose numbering is
        // in flight, while its own 1w/1m levels step back off the boundary as the rule requires.
        var after = new ScenarioSpec
        {
            Id = 53,
            Name = "Dashboard strip rendered the day after a decision",
            Question = "When the newest stored close IS the decision day, does each dashboard row " +
                       "still carry its own contract, or the neighbouring one?",
        };
        {
            var B = Cal.Bd(-1);                        // the decision, yesterday
            var Pb = Cal.D(-84); var Pa = Cal.D(-42);  // the two settled meetings before it
            var M1 = B.AddDays(42); var M2 = B.AddDays(84);
            var M3 = B.AddDays(126); var M4 = B.AddDays(168);
            var bounds = new[] { Pb, Pa, B, M1, M2, M3, M4 };
            const double fix = 3.900;
            // pre-decision / post-decision level of each contract (the cut was fully priced, so
            // the forward contracts move 1bp on the day)
            const double bPre = 3.660, bPost = 3.650;
            const double m1Pre = 3.560, m1Post = 3.550;
            const double m2Pre = 3.500, m2Post = 3.490;
            const double m3Pre = 3.460, m3Post = 3.450;
            const double m4Pre = 3.430;

            var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
            b.Dates.AddRange(new[] { Pb, Pa, B, M1, M2, M3, M4 });
            b.DecisionDates.AddRange(new[] { B, M1, M2, M3, M4 });
            b.Fix(fix).FixHist(Cal.D(-70), Cal.PrevBd(B), fix);

            // TODAY's feed has fully re-pointed (a day has passed): rung 1 quotes M1
            b.Quote(0, mid: bPost, prevClose: bPost, eff: B, mat: M1);
            b.Quote(1, mid: m1Post, prevClose: m1Post, eff: M1, mat: M2);
            b.Quote(2, mid: m2Post, prevClose: m2Post, eff: M2, mat: M3);
            b.Quote(3, mid: m3Post, prevClose: m3Post, eff: M3, mat: M4);

            // history UP TO THE DAY BEFORE the decision: quiet, contract by contract
            var lastClean = Cal.PrevBd(B);
            b.Contract(Pa, bounds, Cal.D(-70), lastClean, fix);
            b.Contract(B, bounds, Cal.D(-70), lastClean, bPre);
            b.Contract(M1, bounds, Cal.D(-70), lastClean, m1Pre);
            b.Contract(M2, bounds, Cal.D(-70), lastClean, m2Pre);
            b.Contract(M3, bounds, Cal.D(-70), lastClean, m3Pre);
            b.Contract(M4, bounds, Cal.D(-70), lastClean, m4Pre);

            // ...and the DECISION DAY's own closes, seeded rung by rung in the state the product
            // itself documents: rung 1 has re-pointed, rungs 2+ have not.
            //   rung 1 (re-pointed)     -> M1's post-decision level
            //   rung 2 (still pre-roll) -> M1's post-decision level  (pre-roll rung 2 = M1)
            //   rung 3 (still pre-roll) -> M2's
            //   rung 4 (still pre-roll) -> M3's
            b.Close(1, B, B, m1Post).Snap(1, B, B, m1Post);
            b.Close(2, B, B, m1Post).Snap(2, B, B, m1Post);
            b.Close(3, B, B, m2Post).Snap(3, B, B, m2Post);
            b.Close(4, B, B, m3Post).Snap(4, B, B, m3Post);

            after.Banks.Add(b);
            after.Custom.Add(s =>
            {
                var msgs = new List<string>();
                var strip = s.Strips["FOMC"];
                // Each row must carry ITS OWN contract. Two values are defensible for a row:
                // that contract's last clean pre-decision close, or its decision-day value.
                // Anything else is a DIFFERENT CONTRACT on the row, which is the fault.
                //     M1 -> 3.560 or 3.550
                //     M2 -> 3.500 or 3.490
                //     M3 -> 3.460 or 3.450
                var want = new (DateTime Contract, double Clean, double Day)[]
                {
                    (M1, m1Pre, m1Post), (M2, m2Pre, m2Post), (M3, m3Pre, m3Post),
                };
                for (int i = 0; i < Math.Min(strip.Rows.Count, want.Length); i++)
                {
                    var r = strip.Rows[i];
                    if (r.Contract.Date != want[i].Contract.Date)
                    {
                        msgs.Add($"row {i + 1}: contract {r.Contract:dd-MMM-yy} != expected " +
                                 $"{want[i].Contract:dd-MMM-yy}");
                        continue;
                    }
                    if (r.Mid is { } mid
                        && Math.Abs(mid - want[i].Clean) > 1e-9 && Math.Abs(mid - want[i].Day) > 1e-9)
                        msgs.Add($"row {i + 1} ({r.Contract:dd-MMM-yy}): level {mid:0.000} is " +
                                 $"neither this contract's clean close {want[i].Clean:0.000} nor its " +
                                 $"decision-day value {want[i].Day:0.000} - it is the NEIGHBOURING " +
                                 $"contract, read off a rung that had not re-pointed by the close");
                    // the change the panel renders: this contract did not move over the week
                    if (r.Mid is { } m2v && r.WeekLevel is { } w2v && Math.Abs((m2v - w2v) * 100.0) > 1.05)
                        msgs.Add($"row {i + 1} ({r.Contract:dd-MMM-yy}): renders a 1w change of " +
                                 $"{(m2v - w2v) * 100.0:+0.0;-0.0;0.0}bp; this contract moved 1.0bp " +
                                 $"(level {m2v:0.000} vs 1w level {w2v:0.000})");
                }
                // the email, derived from live mids and ticker maturities, is the cross-check
                var run = s.Run("FOMC")!;
                for (int i = 0; i < Math.Min(strip.Rows.Count, run.Rows.Count); i++)
                    if (strip.Rows[i].Contract.Date == run.Rows[i].Date.Date
                        && strip.Rows[i].Mid is { } sm
                        && Math.Abs(sm - run.Rows[i].MidPct) * 100.0 > 5.0)
                        msgs.Add($"dashboard and email disagree by " +
                                 $"{Math.Abs(sm - run.Rows[i].MidPct) * 100.0:0.0}bp on " +
                                 $"{strip.Rows[i].Contract:dd-MMM-yy} (dashboard {sm:0.000}, " +
                                 $"email {run.Rows[i].MidPct:0.000})");
                return msgs;
            });
        }
        yield return after;

        // ---------------------------------------------------------------- 59
        // The ECB variant, and the likelier one. The product's own store-close probe found the
        // EESF composite re-pointing "between the 24-Jul and 27-Jul CLOSES" around a 23-Jul
        // announcement - i.e. NOT re-pointed by the announcement day's own close. So when the
        // weekly render happens the next day, and its asOf is that close, EVERY strip row reads
        // one rung too near, not just the ones that lagged.
        var ecb = new ScenarioSpec
        {
            Id = 59,
            Name = "Dashboard strip after an ECB announcement, feed not re-pointed by the close",
            Question = "The render's asOf is the announcement day's close and the family had not " +
                       "renumbered by then. Which contract does each strip row carry?",
        };
        {
            var A = Cal.Bd(-1);                       // the announcement, yesterday
            var S = A.AddDays(6);                     // the period it decided
            var A1 = A.AddDays(49); var N1 = A1.AddDays(6);
            var A2 = A.AddDays(98); var N2 = A2.AddDays(6);
            var A3 = A.AddDays(147); var N3 = A3.AddDays(6);
            var A0 = A.AddDays(-49); var S0 = A0.AddDays(6);
            var bounds = new[] { A0, A, A1, A2, A3 };

            const double fx = 2.000;
            const double vCur = 2.250, v1 = 2.300, v2 = 2.330, v3 = 2.350;

            var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
            b.Dates.AddRange(new[] { S0, S, N1, N2, N3 });
            b.DecisionDates.AddRange(new[] { A, A1, A2, A3 });
            b.Fix(fx).FixHist(Cal.D(-70), Cal.PrevBd(A), fx);

            // today the feed HAS re-pointed (a day has passed) - only the stored close is stale
            b.Quote(0, mid: vCur, prevClose: vCur, eff: S, mat: N1);
            b.Quote(1, mid: v1, prevClose: v1, eff: N1, mat: N2);
            b.Quote(2, mid: v2, prevClose: v2, eff: N2, mat: N3);
            b.Quote(3, mid: v3, prevClose: v3, eff: N3, mat: N3.AddDays(49));

            // flat contracts, so the only thing that can move a published level is reading the
            // wrong rung. Seeded through the announcement day: Contract() puts that day's close
            // under the PRE-announcement numbering, which is what the probe observed.
            b.Contract(S, bounds, Cal.D(-70), A, vCur);
            b.Contract(N1, bounds, Cal.D(-70), A, v1);
            b.Contract(N2, bounds, Cal.D(-70), A, v2);
            b.Contract(N3, bounds, Cal.D(-70), A, v3);
            ecb.Banks.Add(b);

            ecb.Custom.Add(s =>
            {
                var msgs = new List<string>();
                var strip = s.Strips["ECB"];
                var want = new (DateTime Contract, double Level)[] { (N1, v1), (N2, v2), (N3, v3) };
                for (int i = 0; i < Math.Min(strip.Rows.Count, want.Length); i++)
                {
                    var r = strip.Rows[i];
                    if (r.Contract.Date != want[i].Contract.Date)
                    {
                        msgs.Add($"row {i + 1}: contract {r.Contract:dd-MMM-yy} != expected " +
                                 $"{want[i].Contract:dd-MMM-yy}");
                        continue;
                    }
                    if (r.Mid is { } mid && Math.Abs(mid - want[i].Level) > 1e-9)
                        msgs.Add($"row {i + 1} ({r.Contract:dd-MMM-yy}): published level {mid:0.000} " +
                                 $"!= this contract's own close {want[i].Level:0.000} - it is the " +
                                 "neighbouring contract");
                    if (r.Mid is { } mv && r.WeekLevel is { } wv && Math.Abs((mv - wv) * 100.0) > 0.05)
                        msgs.Add($"row {i + 1} ({r.Contract:dd-MMM-yy}): renders a 1w change of " +
                                 $"{(mv - wv) * 100.0:+0.0;-0.0;0.0}bp on a contract that never moved");
                }
                return msgs;
            });
        }
        yield return ecb;
    }
}
