using System.Globalization;
using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>GUARDS, NOTES AND THE DISTRIBUTION GATE.
///
/// Everything here is about the SECOND line of defence: the numbers are published either way,
/// and the question is whether the desk is TOLD when a decision day produces something that
/// deserves eyes before it goes to clients — and, just as important, whether that telling stays
/// out of the client-facing surfaces.
///
/// GEOMETRY (FOMC shape throughout: the decision day IS the period start, the family renumbers
/// at the announcement, so the time gate rolls the just-decided period off the board and no
/// announced-but-not-yet-effective re-base is possible):
///
///     P2 ......... P1 .........|TODAY = D0|......... D1 ......... D2 ......... D3 ...
///     settled      settled      decided today        published rows start here
///
/// The feed has NOT re-pointed in any of these (the state a run minutes after the statement is
/// actually in): rung 1 still quotes the period that started today, and MeetingRun's uniform
/// gate shift is what puts D1 on the front.</summary>
public static class Group08_Guards
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string Dt(DateTime d) => d.ToString("dd-MMM-yy", Inv);

    // ---------------------------------------------------------------- shared calendar pieces

    private static readonly DateTime P2 = Cal.D(-84), P1 = Cal.D(-42), D0 = Cal.D(0);

    private const double Fix = 3.900;         // FEDL01 — still the PRE-decision rate all day

    /// <summary>Seed ONE contract's whole lookback as four flat levels, chosen so each of the
    /// three change anchors lands on its own level and nothing else:
    ///
    ///   [-70, -27] = the Δ1m level   (the 1m anchor is today−28 … today−31, always inside)
    ///   [-26,  -7] = the Δ1w level   (the 1w anchor is exactly today−7, the last day of this leg)
    ///   [ -6,  -1] = the Δ1d level   (the 1d anchor is today−1, or the last b/d before it)
    ///   [  0,   0] = today's post-decision level (the live mid)
    ///
    /// Persistent LEVEL steps, never one-day blips: the Hampel despike filter rewrites isolated
    /// bumps above ~4.4bp and would manufacture a finding. (The stitcher takes 16:15 London snaps
    /// over stored closes inside 50 days, and snaps are not despiked at all, so every anchor here
    /// is the raw seeded number.)</summary>
    private static void Path(BankSpec b, DateTime contract, DateTime[] bounds,
        double m1, double w1, double d1, double today)
    {
        b.Contract(contract, bounds, Cal.D(-70), Cal.D(-27), m1);
        b.Contract(contract, bounds, Cal.D(-26), Cal.D(-7), w1);
        b.Contract(contract, bounds, Cal.D(-6), Cal.D(-1), d1);
        b.Contract(contract, bounds, Cal.D(0), Cal.D(0), today);
    }

    /// <summary>The 30-day Fed Funds contracts FuturesGuard can probe, all quoted at the same
    /// price. The guard picks the first delivery month that starts after today AND is fully
    /// covered by the published rows; seeding a strip rather than guessing that month keeps the
    /// scenario independent of what day of the month the suite is run on. The geometry below
    /// guarantees whichever month it lands on sits INSIDE the front published period, so the
    /// day-weighted blend is that row's own mid exactly.</summary>
    private static IEnumerable<ExtraQuote> FedFunds(double price)
    {
        // the same month walk FuturesGuard.CheckRun does, off the same DateTime.Today
        var m = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        for (int i = 0; i < 12; i++)
        {
            m = m.AddMonths(1);
            yield return new ExtraQuote($"FF{"FGHJKMNQUVXZ"[m.Month - 1]}{m.Year % 10} Comdty", Mid: price);
        }
    }

    /// <summary>An ECB whose contributor page still PRICES every rung but has lost its date
    /// fields (SW_EFF_DT / maturity) — the exact silent-drop-out the completeness gate was
    /// written for. Under the hard-data rule a row needs its date from the tickers' own fields,
    /// so nothing publishes and the bank disappears from every surface. Its decision is TODAY,
    /// which is the day it would be noticed least and matter most.</summary>
    private static BankSpec DatelessEcb()
    {
        var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
        b.Dates.AddRange(new[] { Cal.D(-92), Cal.D(-50), Cal.D(6), Cal.D(55), Cal.D(104) });
        b.DecisionDates.AddRange(new[] { Cal.D(0), Cal.D(49), Cal.D(98) });
        b.Fix(2.000).FixHist(Cal.D(-70), Cal.D(-1), 2.000);
        // prices, no eff/maturity anywhere
        b.Quote(0, mid: 2.000);
        b.Quote(1, mid: 2.250);
        b.Quote(2, mid: 2.300);
        b.Quote(3, mid: 2.330);
        return b;
    }

    /// <summary>Notes carrying the CHECK prefix — the ones that gate distribution in the app
    /// (MainWindow.ConfirmChecks filters on exactly this).</summary>
    private static List<string> Checks(Surfaces s) =>
        s.Notes.Where(n => n.StartsWith("CHECK:", StringComparison.Ordinal)).ToList();

    public static IEnumerable<ScenarioSpec> All()
    {
        // ================================================================ 40
        // OUTLIER GUARD — THE ABSOLUTE BARS, on a genuine surprise.
        //
        // The Fed hikes when nothing was priced. The front period's own contract has been
        // repricing for a month and jumps again on the statement; the rest of the strip moves
        // less. Deliberately THREE published rows, so the cross-sectional test (which needs 4
        // populated rows) cannot fire and the absolute bars are the only thing under test.
        {
            var D1 = Cal.D(42); var D2 = Cal.D(84); var D3 = Cal.D(126); var D4 = Cal.D(168);
            var bounds = new[] { P2, P1, D0, D1, D2, D3, D4 };

            var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
            b.Dates.AddRange(new[] { P2, P1, D0, D1, D2, D3, D4 });
            b.DecisionDates.AddRange(new[] { D0, D1, D2, D3, D4 });
            b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

            // FEED NOT RE-POINTED: rung 1 is still the period that started today
            b.Quote(0, mid: Fix, prevClose: Fix, eff: P1, mat: D0);
            b.Quote(1, mid: 4.400, prevClose: 4.050, eff: D0, mat: D1);   // decided today — rolls off
            b.Quote(2, mid: 4.250, prevClose: 4.100, eff: D1, mat: D2);   // becomes the front
            b.Quote(3, mid: 4.350, prevClose: 4.250, eff: D2, mat: D3);
            b.Quote(4, mid: 4.450, prevClose: 4.370, eff: D3, mat: D4);

            //                          Δ1m lvl  Δ1w lvl  Δ1d lvl  today
            b.Contract(P1, bounds, Cal.D(-70), Cal.D(-1), Fix);       // the settled current period
            Path(b, D0, bounds, 3.650, 3.850, 4.050, 4.400);
            Path(b, D1, bounds, 3.700, 3.900, 4.100, 4.250);
            Path(b, D2, bounds, 3.900, 4.070, 4.250, 4.350);
            Path(b, D3, bounds, 4.050, 4.230, 4.370, 4.450);
            Path(b, D4, bounds, 4.150, 4.310, 4.470, 4.550);

            var sp = new ScenarioSpec
            {
                Id = 40,
                Name = "OutlierGuard ABSOLUTE bars — a surprise hike breaches 12/30/50bp",
                Question = "When a decision moves the front contract 15bp on the day, 35bp on the " +
                           "week and 55bp on the month, does the run carry a CHECK note per " +
                           "horizon naming the bank, the row and the value — and are the numbers " +
                           "themselves published untouched?",
            };
            sp.Banks.Add(b);
            sp.Expect.Add(new BankExpect
            {
                Bank = "FOMC",
                // same-day start ⇒ no announced-but-not-yet-effective re-base is possible;
                // Priced is measured against the stale EFFR all day, by design
                Fixing = 4.400,
                Rebased = true,
                // Priced = (4.250 − 3.900) × 100 = +35.0
                Front = new FrontExpect(D1, D1, 4.250, 4.400, -15.0, Rebased: true),
                Rows = new List<RowExpect>
                {
                    // Priced = (4.250−3.900)×100 = +35.0 ; first row ⇒ no Step
                    // Δ1d = (4.250−4.100)×100 = +15.0   > 12 bar
                    // Δ1w = (4.250−3.900)×100 = +35.0   > 30 bar
                    // Δ1m = (4.250−3.700)×100 = +55.0   > 50 bar
                    new(D1, D2, 4.250, -15.0, null, +15.0, +35.0, +55.0),
                    // Priced = (4.350−3.900)×100 = +45.0 ; Step = 45.0−35.0 = +10.0
                    // Δ1d = (4.350−4.250)×100 = +10.0 ; Δ1w = (4.350−4.070)×100 = +28.0
                    // Δ1m = (4.350−3.900)×100 = +45.0   — all three inside their bars
                    new(D2, D3, 4.350, -5.0, +10.0, +10.0, +28.0, +45.0),
                    // Priced = (4.450−3.900)×100 = +55.0 ; Step = 55.0−45.0 = +10.0
                    // Δ1d = (4.450−4.370)×100 = +8.0 ; Δ1w = (4.450−4.230)×100 = +22.0
                    // Δ1m = (4.450−4.050)×100 = +40.0
                    new(D3, D4, 4.450, +5.0, +10.0, +8.0, +22.0, +40.0),
                },
            });
            sp.NotesContain.Add($"CHECK: FOMC {Dt(D1)} Δ1d +15.0bp exceeds the 12bp sanity bar — verify before distribution");
            sp.NotesContain.Add($"CHECK: FOMC {Dt(D1)} Δ1w +35.0bp exceeds the 30bp sanity bar — verify before distribution");
            sp.NotesContain.Add($"CHECK: FOMC {Dt(D1)} Δ1m +55.0bp exceeds the 50bp sanity bar — verify before distribution");
            sp.NotesNotContain.Add("run median");          // 3 rows ⇒ the cross-sectional test must stay silent
            sp.NotesNotContain.Add("FUTURES GUARD");
            sp.NotesNotContain.Add("STALE");
            sp.Custom.Add(s =>
            {
                var msgs = new List<string>();
                var checks = Checks(s);
                if (checks.Count != 3)
                    msgs.Add($"expected exactly 3 CHECK notes (one per horizon, front row only), got " +
                             $"{checks.Count}: {string.Join(" || ", checks)}");
                // the bars flag, they never suppress: the flagged cells must still be on the blast
                var blk = Render.Blast(s.BlastText).GetValueOrDefault("FOMC");
                if (blk == null) msgs.Add("FOMC is missing from the blast although it was only flagged");
                else if (blk.Rows.Count > 0 &&
                         (blk.Rows[0].Length < 7 || blk.Rows[0][4] != "+15.0" || blk.Rows[0][5] != "+35.0"
                          || blk.Rows[0][6] != "+55.0"))
                    msgs.Add("the flagged front row's Δ1d/Δ1w/Δ1m are not published verbatim on the blast: "
                             + string.Join(" ", blk.Rows[0]));
                return msgs;
            });
            yield return sp;
        }

        // ================================================================ 41
        // OUTLIER GUARD — THE CROSS-SECTIONAL FLAG, and the FRONT-ROW EXEMPTION.
        //
        // Five published rows. The BODY moves together on Δ1d (+1.0 / +1.0 / +9.0 / +2.0) with
        // one row far off; the FRONT decouples hard the other way (−10.0) because it is
        // converging on the fixing into the decision — legitimate, and the reason the front is
        // excluded (desk 2026-08-26, the RBNZ −1.0-vs-−20.3 false flag). Δ1w and Δ1m are
        // deliberately uniform so exactly one flag can fire in the whole run.
        {
            var D1 = Cal.D(42); var D2 = Cal.D(84); var D3 = Cal.D(126);
            var D4 = Cal.D(168); var D5 = Cal.D(210); var D6 = Cal.D(252);
            var bounds = new[] { P2, P1, D0, D1, D2, D3, D4, D5, D6 };

            var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
            b.Dates.AddRange(new[] { P2, P1, D0, D1, D2, D3, D4, D5, D6 });
            b.DecisionDates.AddRange(new[] { D0, D1, D2, D3, D4, D5, D6 });
            b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

            b.Quote(0, mid: Fix, prevClose: Fix, eff: P1, mat: D0);
            b.Quote(1, mid: 4.150, prevClose: 4.140, eff: D0, mat: D1);   // decided today — rolls off
            b.Quote(2, mid: 4.250, prevClose: 4.350, eff: D1, mat: D2);   // the front: −10.0 on the day
            b.Quote(3, mid: 4.350, prevClose: 4.340, eff: D2, mat: D3);
            b.Quote(4, mid: 4.450, prevClose: 4.440, eff: D3, mat: D4);
            b.Quote(5, mid: 4.550, prevClose: 4.460, eff: D4, mat: D5);   // the body outlier: +9.0
            b.Quote(6, mid: 4.650, prevClose: 4.630, eff: D5, mat: D6);

            //                          Δ1m lvl  Δ1w lvl  Δ1d lvl  today
            b.Contract(P1, bounds, Cal.D(-70), Cal.D(-1), Fix);
            Path(b, D0, bounds, 4.070, 4.100, 4.140, 4.150);
            Path(b, D1, bounds, 4.170, 4.200, 4.350, 4.250);   // front: +8 / +5 / −10
            Path(b, D2, bounds, 4.270, 4.300, 4.340, 4.350);   //        +8 / +5 / +1
            Path(b, D3, bounds, 4.370, 4.400, 4.440, 4.450);   //        +8 / +5 / +1
            Path(b, D4, bounds, 4.470, 4.500, 4.460, 4.550);   //        +8 / +5 / +9  ← the flag
            Path(b, D5, bounds, 4.570, 4.600, 4.630, 4.650);   //        +8 / +5 / +2
            Path(b, D6, bounds, 4.620, 4.650, 4.680, 4.700);

            var sp = new ScenarioSpec
            {
                Id = 41,
                Name = "OutlierGuard CROSS-SECTIONAL — one body row flags, the front is exempt",
                Question = "With four body rows moving together and one far off the median, does " +
                           "exactly that row earn a CHECK note — and does a front row decoupling " +
                           "twice as hard in the opposite direction stay silent, as the exemption " +
                           "intends?",
            };
            sp.Banks.Add(b);
            sp.Expect.Add(new BankExpect
            {
                Bank = "FOMC",
                Fixing = 4.150,
                Rebased = true,
                Front = new FrontExpect(D1, D1, 4.250, 4.150, +10.0, Rebased: true),
                Rows = new List<RowExpect>
                {
                    // Priced = (4.250−3.900)×100 = +35.0 ; Δ1d = (4.250−4.350)×100 = −10.0
                    // Δ1w = (4.250−4.200)×100 = +5.0 ; Δ1m = (4.250−4.170)×100 = +8.0
                    new(D1, D2, 4.250, +10.0, null, -10.0, +5.0, +8.0),
                    // Priced +45.0 ; Step = 45−35 = +10.0 ; Δ1d = (4.350−4.340)×100 = +1.0
                    new(D2, D3, 4.350, +20.0, +10.0, +1.0, +5.0, +8.0),
                    // Priced +55.0 ; Step +10.0 ; Δ1d = (4.450−4.440)×100 = +1.0
                    new(D3, D4, 4.450, +30.0, +10.0, +1.0, +5.0, +8.0),
                    // Priced +65.0 ; Step +10.0 ; Δ1d = (4.550−4.460)×100 = +9.0  ← the outlier
                    new(D4, D5, 4.550, +40.0, +10.0, +9.0, +5.0, +8.0),
                    // Priced +75.0 ; Step +10.0 ; Δ1d = (4.650−4.630)×100 = +2.0
                    new(D5, D6, 4.650, +50.0, +10.0, +2.0, +5.0, +8.0),
                },
            });
            // body Δ1d = [+1.0, +1.0, +9.0, +2.0] → sorted [1,1,2,9] → median (1+2)/2 = +1.5
            // |x − 1.5| = [0.5, 0.5, 7.5, 0.5] → MAD = (0.5+0.5)/2 = 0.5
            // threshold = max(4.0bp floor, 4 × 0.5) = 4.0bp → only |9.0 − 1.5| = 7.5 clears it
            sp.NotesContain.Add($"CHECK: FOMC {Dt(D4)} Δ1d +9.0bp vs run median +1.5bp — verify before distribution");
            sp.NotesNotContain.Add("sanity bar");   // every |Δ| is inside 12/30/50
            sp.NotesNotContain.Add("FUTURES GUARD");
            sp.NotesNotContain.Add("STALE");
            sp.Custom.Add(s =>
            {
                var msgs = new List<string>();
                var checks = Checks(s);
                if (checks.Count != 1)
                    msgs.Add($"expected exactly ONE CHECK note (the body outlier), got {checks.Count}: " +
                             string.Join(" || ", checks));
                // THE EXEMPTION: the front's −10.0 is the largest |Δ1d| in the run and must not flag
                foreach (var n in checks.Where(n => n.Contains(Dt(D1), StringComparison.Ordinal)))
                    msgs.Add("the FRONT row was flagged although it is exempt from the cross-sectional " +
                             "test (a front converging on the fixing legitimately decouples): " + n);
                return msgs;
            });
            yield return sp;
        }

        // ================================================================ 42 / 43
        // THE FUTURES GUARD, agreeing and disagreeing.
        //
        // FuturesGuard walks forward one delivery month at a time from the first of THIS month
        // and takes the first window that (a) has not started, (b) is covered on both ends by the
        // published rows, (c) quotes. The meeting grid below puts the second published meeting
        // 70 days after the first, so whichever month the walk lands on lies wholly INSIDE the
        // front published period whatever today's date is — and a 30-day-average ("monthavg")
        // contract over a window with no meeting boundary in it blends to that single row's mid.
        //   blend  = 4.250  (the front row's own mid, every calendar day of the window)
        //   implied = 100 − price
        // 42 prices the contract AT the blend (100 − 4.250 = 95.750 → Δ 0.0bp, inside the 2.5bp
        // tolerance); 43 prices it a full policy step away (95.500 → implied 4.500 → Δ +25.0bp).
        {
            var D1 = Cal.D(42); var D2 = Cal.D(112); var D3 = Cal.D(154); var D4 = Cal.D(196);
            var bounds = new[] { P2, P1, D0, D1, D2, D3, D4 };

            BankSpec Fomc(double ffPrice)
            {
                var b = new BankSpec
                {
                    Bank = "FOMC",
                    DecisionTimeLondon = Cal.TimePassed,
                    DisableGuardFutures = false,       // keep the SHIPPED guard wired
                };
                b.Dates.AddRange(new[] { P2, P1, D0, D1, D2, D3, D4 });
                b.DecisionDates.AddRange(new[] { D0, D1, D2, D3, D4 });
                b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

                b.Quote(0, mid: Fix, prevClose: Fix, eff: P1, mat: D0);
                b.Quote(1, mid: 4.150, prevClose: 4.140, eff: D0, mat: D1);   // decided today
                b.Quote(2, mid: 4.250, prevClose: 4.240, eff: D1, mat: D2);   // the front
                b.Quote(3, mid: 4.350, prevClose: 4.340, eff: D2, mat: D3);
                b.Quote(4, mid: 4.400, prevClose: 4.390, eff: D3, mat: D4);

                // a quiet tape apart from the +1bp the statement delivered
                b.Contract(P1, bounds, Cal.D(-70), Cal.D(-1), Fix);
                b.ContractStep(D0, bounds, Cal.D(-70), Cal.D(0), D0, 4.140, 4.150);
                b.ContractStep(D1, bounds, Cal.D(-70), Cal.D(0), D0, 4.240, 4.250);
                b.ContractStep(D2, bounds, Cal.D(-70), Cal.D(0), D0, 4.340, 4.350);
                b.ContractStep(D3, bounds, Cal.D(-70), Cal.D(0), D0, 4.390, 4.400);
                b.ContractStep(D4, bounds, Cal.D(-70), Cal.D(0), D0, 4.420, 4.430);

                b.Extras.AddRange(FedFunds(ffPrice));
                return b;
            }

            // the SAME published table in both — a guard flags, it never edits a row
            List<RowExpect> Rows() => new()
            {
                // Priced = (4.250−3.900)×100 = +35.0 ; first row ⇒ no Step
                // Δ1d = Δ1w = Δ1m = (4.250−4.240)×100 = +1.0
                new(D1, D2, 4.250, +10.0, null, +1.0, +1.0, +1.0),
                // Priced = (4.350−3.900)×100 = +45.0 ; Step = 45−35 = +10.0
                new(D2, D3, 4.350, +20.0, +10.0, +1.0, +1.0, +1.0),
                // Priced = (4.400−4.150)×100 = +25.0 ; Step = 25−20 = +5.0
                new(D3, D4, 4.400, +25.0, +5.0, +1.0, +1.0, +1.0),
            };

            // FIXED 2026-08-27: the re-base now fires on a same-day-start family, so the base is
            // the just-decided period's own OIS (4.150), not the pre-hike EFFR (3.900). Every
            // Priced drops by the delivered 25bp.
            BankExpect Expect() => new()
            {
                Bank = "FOMC",
                Fixing = 4.150,
                Rebased = true,
                Front = new FrontExpect(D1, D1, 4.250, 4.150, +10.0, Rebased: true),
                Rows = Rows(),
            };

            // ------------------------------------------------------------ 42
            var ok = new ScenarioSpec
            {
                Id = 42,
                Name = "FUTURES GUARD agrees — exchange-settled FF matches the meeting blend",
                Question = "On a decision day with the gate rolling the decided period off, does " +
                           "the independent Fed Funds cross-check reproduce the meeting rows' own " +
                           "blend and say so?",
            };
            ok.Banks.Add(Fomc(95.750));           // 100 − 95.750 = 4.250 = the blend
            ok.Expect.Add(Expect());
            ok.NotesContain.Add("futures guard FOMC ok");
            ok.NotesContain.Add("implies 4.250 vs meeting blend 4.250");
            ok.NotesContain.Add("(Δ+0.0bp ≤ 2.5)");
            ok.NotesNotContain.Add("FUTURES GUARD TRIGGERED");
            ok.NotesNotContain.Add("skipped");     // the guard must actually have run
            ok.NotesNotContain.Add("CHECK");
            ok.NotesNotContain.Add("STALE");
            yield return ok;

            // ------------------------------------------------------------ 43
            var bad = new ScenarioSpec
            {
                Id = 43,
                Name = "FUTURES GUARD disagrees — FF a full step away from the blend",
                Question = "When the exchange-settled contract implies a rate a policy step away " +
                           "from what the meeting rows blend to, does the run raise FUTURES GUARD " +
                           "TRIGGERED — and are the published rows left exactly as they were?",
            };
            bad.Banks.Add(Fomc(95.500));          // 100 − 95.500 = 4.500 vs a 4.250 blend
            bad.Expect.Add(Expect());             // identical table to 42: flagged, never edited
            bad.NotesContain.Add("FUTURES GUARD TRIGGERED — FOMC");
            bad.NotesContain.Add("implies 4.500 but the meeting rows blend to 4.250");
            bad.NotesContain.Add("(Δ+25.0bp > 2.5bp tolerance)");
            bad.NotesNotContain.Add("futures guard FOMC ok");
            // no OUTLIER flags here — every |Δ| is 1.0bp, and only 3 rows publish
            bad.NotesNotContain.Add("sanity bar");
            bad.NotesNotContain.Add("run median");
            bad.NotesNotContain.Add("STALE");
            bad.Custom.Add(s =>
            {
                var msgs = new List<string>();
                // a triggered guard must not quietly change, drop or blank a row
                var run = s.Run("FOMC");
                if (run == null || run.Rows.Count != 3)
                    msgs.Add($"the triggered guard changed the published run ({run?.Rows.Count ?? -1} rows, " +
                             "expected 3) — the guard flags, it must never edit");
                var blk = Render.Blast(s.BlastText).GetValueOrDefault("FOMC");
                if (blk == null || blk.Rows.Count != 3)
                    msgs.Add("the blast lost the FOMC block (or rows) after the guard triggered");
                else if (blk.Rows[0][1] != "4.250")
                    msgs.Add($"the blast front mid is '{blk.Rows[0][1]}', not the published 4.250 — " +
                             "a guard breach must not rewrite a price");

                // ---- THE DISTRIBUTION GATE -------------------------------------------------
                // A breach here means an EXCHANGE-SETTLED contract disagrees with the meeting
                // rows by a full policy step on a decision day — the roll/calendar/re-base fault
                // the guard exists to catch (FuturesGuard.cs:20-27, DESIGN.md §12 "treat this as
                // a roll/calendar/re-base fault until proven otherwise"). The product's own
                // mechanism for "this must be SEEN before anything is written" is the CHECK
                // prefix: MainWindow.ConfirmChecks (src\RateDesk.Weekly\MainWindow.xaml.cs:310)
                // filters `n.StartsWith(OutlierGuard.Prefix + ":")` and cancels the whole daily
                // build on No, and CompoundedFixing's completeness gate deliberately borrows that
                // prefix for exactly this reason ("must demand eyes, not just a log line",
                // CompoundedFixing.cs:101-108).
                //
                // So the expectation, derived from the desk rule and from the app's own contract:
                // when the futures guard triggers, at least one CHECK-prefixed note must name it.
                // A 12.1bp Δ1d — usually a real market move — blocks publication today; a 25bp
                // futures-vs-blend break, which is almost never anything but a fault, does not.
                if (!Checks(s).Any(n => n.Contains("FUTURES GUARD", StringComparison.Ordinal)
                                        || n.Contains("futures", StringComparison.OrdinalIgnoreCase)))
                    msgs.Add("the FUTURES GUARD breach carries no CHECK-prefixed note, so it does not " +
                             "reach the pre-publish gate (MainWindow.ConfirmChecks filters 'CHECK:'): " +
                             "the blast, the workbook, the shared-drive copy and the email are all " +
                             "written with no prompt, on the one signal that is almost never anything " +
                             "but a roll/calendar/re-base fault. Notes were: " + string.Join(" || ", s.Notes));
                return msgs;
            });
            yield return bad;
        }

        // ================================================================ 44
        // THE COMPLETENESS GATE.
        //
        // Two banks in the config, one healthy, one whose contributor page still prices but has
        // lost its date fields on the very day it decides. Under the hard-data rule the dateless
        // bank publishes nothing — and a table quietly one bank short is exactly what must never
        // be mailed. The gate lives in CompoundedFixing.Stamp and must raise a CHECK-prefixed
        // note, because CHECK is the prefix the app's pre-publish popup filters on: an ordinary
        // note would be a log line nobody reads.
        {
            var D1 = Cal.D(42); var D2 = Cal.D(112); var D3 = Cal.D(154); var D4 = Cal.D(196);
            var bounds = new[] { P2, P1, D0, D1, D2, D3, D4 };

            var fomc = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
            fomc.Dates.AddRange(new[] { P2, P1, D0, D1, D2, D3, D4 });
            fomc.DecisionDates.AddRange(new[] { D0, D1, D2, D3, D4 });
            fomc.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);
            fomc.Quote(0, mid: Fix, prevClose: Fix, eff: P1, mat: D0);
            fomc.Quote(1, mid: 4.150, prevClose: 4.140, eff: D0, mat: D1);
            fomc.Quote(2, mid: 4.250, prevClose: 4.240, eff: D1, mat: D2);
            fomc.Quote(3, mid: 4.350, prevClose: 4.340, eff: D2, mat: D3);
            fomc.Quote(4, mid: 4.400, prevClose: 4.390, eff: D3, mat: D4);
            fomc.Contract(P1, bounds, Cal.D(-70), Cal.D(-1), Fix);
            fomc.ContractStep(D0, bounds, Cal.D(-70), Cal.D(0), D0, 4.140, 4.150);
            fomc.ContractStep(D1, bounds, Cal.D(-70), Cal.D(0), D0, 4.240, 4.250);
            fomc.ContractStep(D2, bounds, Cal.D(-70), Cal.D(0), D0, 4.340, 4.350);
            fomc.ContractStep(D3, bounds, Cal.D(-70), Cal.D(0), D0, 4.390, 4.400);
            fomc.ContractStep(D4, bounds, Cal.D(-70), Cal.D(0), D0, 4.420, 4.430);

            var sp = new ScenarioSpec
            {
                Id = 44,
                Name = "COMPLETENESS GATE — a bank that publishes nothing on its decision day",
                Question = "When a configured bank drops out of the report entirely on the day it " +
                           "decides, does the run demand eyes with a CHECK note naming it — or " +
                           "would the desk mail a table silently one bank short?",
            };
            sp.Banks.Add(fomc);
            sp.Banks.Add(DatelessEcb());
            sp.Expect.Add(new BankExpect
            {
                Bank = "FOMC",
                Fixing = 4.150,
                Rebased = true,
                Front = new FrontExpect(D1, D1, 4.250, 4.150, +10.0, Rebased: true),
                Rows = new List<RowExpect>
                {
                    new(D1, D2, 4.250, +10.0, null, +1.0, +1.0, +1.0),
                    new(D2, D3, 4.350, +20.0, +10.0, +1.0, +1.0, +1.0),
                    new(D3, D4, 4.400, +25.0, +5.0, +1.0, +1.0, +1.0),
                },
            });
            sp.Expect.Add(new BankExpect { Bank = "ECB", NoRun = true });
            sp.NotesContain.Add("CHECK: ECB produced NO rows — the run is missing from every surface; " +
                                "verify before distribution");
            sp.Custom.Add(s =>
            {
                var msgs = new List<string>();
                // the note must be CHECK-prefixed: that prefix IS the pre-publish gate
                // (MainWindow.ConfirmChecks filters notes starting "CHECK:")
                if (!Checks(s).Any(n => n.Contains("ECB", StringComparison.Ordinal)))
                    msgs.Add("no CHECK-prefixed note names the missing ECB — an un-prefixed note does " +
                             "not gate publication, so the blast/workbook/email would be written with " +
                             "the bank silently absent. Notes were: " + string.Join(" || ", s.Notes));
                if (s.Front("ECB") != null)
                    msgs.Add("the ECB has a front-table line although it published no rows");
                // and it really is absent everywhere the desk looks
                if (Render.Blast(s.BlastText).ContainsKey("ECB")) msgs.Add("the ECB block is on the blast after all");
                if (Render.Sheet(s.Xlsx).ContainsKey("ECB")) msgs.Add("the ECB block is in the workbook after all");
                if (Render.Email(s.SheetHtml).ContainsKey("ECB")) msgs.Add("the ECB block is in the sheet email after all");
                if (!Render.Blast(s.BlastText).ContainsKey("FOMC")) msgs.Add("the healthy FOMC block is missing from the blast");
                return msgs;
            });
            yield return sp;
        }

        // ================================================================ 45
        // NOTES ARE NOT CONTENT.
        //
        // A decision day that trips four different note families at once:
        //   · an ABSOLUTE outlier bar   (front Δ1d = +20.0bp)
        //   · a STALE feed warning      (the front's own rung 95m quiet against a 5m baseline)
        //   · a FUTURES GUARD breach    (FF a step away from the blend)
        //   · the COMPLETENESS gate     (a dateless ECB drops out)
        // Every one of them is an INTERNAL instruction to the desk. None of that text may reach
        // the chat blast, the workbook, the sheet-style email body or the plaintext email — those
        // go to clients, and "verify before distribution" printed in a client mail is worse than
        // the fault it warns about.
        {
            var D1 = Cal.D(42); var D2 = Cal.D(112); var D3 = Cal.D(154); var D4 = Cal.D(196);
            var bounds = new[] { P2, P1, D0, D1, D2, D3, D4 };

            var b = new BankSpec
            {
                Bank = "FOMC",
                DecisionTimeLondon = Cal.TimePassed,
                DisableGuardFutures = false,
            };
            b.Dates.AddRange(new[] { P2, P1, D0, D1, D2, D3, D4 });
            b.DecisionDates.AddRange(new[] { D0, D1, D2, D3, D4 });
            b.Fix(Fix).FixHist(Cal.D(-70), Cal.D(-1), Fix);

            b.Quote(0, mid: Fix, prevClose: Fix, eff: P1, mat: D0);
            b.Quote(1, mid: 4.150, prevClose: 4.100, eff: D0, mat: D1);
            // the FRONT rung: 95m since its last tick against a 5m baseline ⇒ 90m stale
            b.Quote(2, mid: 4.250, prevClose: 4.050, eff: D1, mat: D2, age: 95.0);
            b.Quote(3, mid: 4.350, prevClose: 4.330, eff: D2, mat: D3);
            b.Quote(4, mid: 4.400, prevClose: 4.390, eff: D3, mat: D4);

            //                          Δ1m lvl  Δ1w lvl  Δ1d lvl  today
            b.Contract(P1, bounds, Cal.D(-70), Cal.D(-1), Fix);
            Path(b, D0, bounds, 3.700, 3.900, 3.950, 4.150);
            Path(b, D1, bounds, 3.800, 4.000, 4.050, 4.250);   // +45 / +25 / +20 ← the Δ1d bar
            Path(b, D2, bounds, 3.900, 4.100, 4.330, 4.350);   // +45 / +25 / +2
            Path(b, D3, bounds, 3.950, 4.150, 4.390, 4.400);   // +45 / +25 / +1
            Path(b, D4, bounds, 3.980, 4.180, 4.420, 4.430);

            b.Extras.AddRange(FedFunds(95.500));               // implied 4.500 vs a 4.250 blend

            var sp = new ScenarioSpec
            {
                Id = 45,
                Name = "NOTES ARE NOT CONTENT — four note families, none of them in the output",
                Question = "With a CHECK bar, a STALE feed, a triggered futures guard and a bank " +
                           "missing all firing on one decision day, does any of that internal text " +
                           "leak into the blast, the workbook, the sheet email or the plaintext?",
            };
            sp.Banks.Add(b);
            sp.Banks.Add(DatelessEcb());
            sp.Expect.Add(new BankExpect
            {
                Bank = "FOMC",
                Fixing = 4.150,
                Rebased = true,
                Front = new FrontExpect(D1, D1, 4.250, 4.150, +10.0, Rebased: true),
                Rows = new List<RowExpect>
                {
                    // Priced = (4.250−3.900)×100 = +35.0
                    // Δ1d = (4.250−4.050)×100 = +20.0 ; Δ1w = (4.250−4.000)×100 = +25.0
                    // Δ1m = (4.250−3.800)×100 = +45.0
                    new(D1, D2, 4.250, +10.0, null, +20.0, +25.0, +45.0),
                    // Priced +45.0 ; Step +10.0 ; Δ1d = (4.350−4.330)×100 = +2.0
                    new(D2, D3, 4.350, +20.0, +10.0, +2.0, +25.0, +45.0),
                    // Priced +50.0 ; Step +5.0 ; Δ1d = (4.400−4.390)×100 = +1.0
                    new(D3, D4, 4.400, +25.0, +5.0, +1.0, +25.0, +45.0),
                },
            });
            sp.Expect.Add(new BankExpect { Bank = "ECB", NoRun = true });
            // all four families must be PRESENT in the notes...
            sp.NotesContain.Add($"CHECK: FOMC {Dt(D1)} Δ1d +20.0bp exceeds the 12bp sanity bar");
            sp.NotesContain.Add("STALE: FOMC");
            sp.NotesContain.Add("INCLUDING THE FRONT");
            sp.NotesContain.Add("FUTURES GUARD TRIGGERED — FOMC");
            sp.NotesContain.Add("CHECK: ECB produced NO rows");
            sp.Custom.Add(s =>
            {
                var msgs = new List<string>();
                if (s.Notes.Count < 4)
                    msgs.Add($"expected several notes at once, got {s.Notes.Count}");

                var surfaces = new (string Name, string Text)[]
                {
                    ("chat blast", s.BlastText),
                    ("workbook Runs sheet", string.Join("\n", s.Xlsx.Select(r => string.Join("\t", r)))),
                    ("sheet-style email body", s.SheetHtml),
                    ("plaintext email", s.WeeklyText),
                    ("card email", s.WeeklyHtml),
                };

                // (a) no note may appear VERBATIM anywhere the desk sends
                foreach (var n in s.Notes)
                    foreach (var (name, text) in surfaces)
                        if (n.Length > 12 && text.Contains(n, StringComparison.Ordinal))
                            msgs.Add($"the {name} carries a run note verbatim: {n}");

                // (b) nor may the phrases that make a note a note — these belong to the desk's
                // pre-publish popup and the app log, never to a client-facing table
                foreach (var frag in new[]
                         {
                             "CHECK:", "STALE:", "FUTURES GUARD", "futures guard",
                             "verify before distribution", "sanity bar", "run median",
                             "consider another contributor", "meeting blend", "produced NO rows",
                         })
                    foreach (var (name, text) in surfaces)
                        if (text.Contains(frag, StringComparison.Ordinal))
                            msgs.Add($"the {name} contains note text '{frag}'");

                // ...and the flagged numbers themselves are still published, unedited
                var blk = Render.Blast(s.BlastText).GetValueOrDefault("FOMC");
                if (blk == null || blk.Rows.Count != 3)
                    msgs.Add("the flagged/stale/guard-breached FOMC run is not published in full");
                else if (blk.Rows[0][4] != "+20.0")
                    msgs.Add($"the flagged front Δ1d prints '{blk.Rows[0][4]}', not the published +20.0");
                return msgs;
            });
            yield return sp;
        }
    }
}
