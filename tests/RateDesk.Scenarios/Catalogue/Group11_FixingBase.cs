using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios.Catalogue;

/// <summary>WHAT "PRICED" IS MEASURED AGAINST once a bank has moved.
///
/// The app's own design rule (DESIGN.md section 12): "between a decision and the start of the
/// period it decided, the o/n fixing still prints the OLD rate ... so priced-vs-fixing would
/// overstate every row by the full just-delivered change for up to a week. Inside that window the
/// base re-bases AUTOMATICALLY onto the just-decided period's own OIS."
///
/// That window is `today &lt; period start`. Eight of the ten runs have a decision-to-start lag of
/// one to seven days, so the window exists and the re-base fires. FOMC and MPC start the period
/// ON the decision date - lag zero - so the window is empty and the re-base can never fire for
/// them, while their o/n fixings (EFFR, SONIA) are published a day in arrears and carry the old
/// rate for the rest of the decision day and the whole of the next.
///
/// These scenarios establish what the desk would actually read, and put the two behaviours in one
/// email so the difference is not theoretical.</summary>
public static class Group11_FixingBase
{
    public static IEnumerable<ScenarioSpec> All()
    {
        // ---------------------------------------------------------------- 54
        {
            var B = Cal.Bd(-1);                                  // the Fed cut, yesterday
            var Pb = Cal.D(-84); var Pa = Cal.D(-42);
            var M1 = B.AddDays(42); var M2 = B.AddDays(84);
            var M3 = B.AddDays(126); var M4 = B.AddDays(168);
            var bounds = new[] { Pb, Pa, B, M1, M2, M3, M4 };

            const double oldFix = 3.900;    // EFFR still printing the PRE-cut rate
            const double newRate = 3.650;   // what the market says the current period now pays
            const double m1 = 3.560, m2 = 3.500, m3 = 3.460, m4 = 3.430;

            var spec = new ScenarioSpec
            {
                Id = 54,
                Name = "The day after a Fed cut: what is Priced measured against?",
                Question = "EFFR is published a day in arrears, so the morning after a cut the " +
                           "fixing still prints the old rate. Does Priced still use it?",
            };
            var b = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
            b.Dates.AddRange(new[] { Pb, Pa, B, M1, M2, M3, M4 });
            b.DecisionDates.AddRange(new[] { B, M1, M2, M3, M4 });
            b.Fix(oldFix).FixHist(Cal.D(-70), Cal.PrevBd(B), oldFix);

            // rung 0 is the CURRENT period, and the market prices it at the NEW policy rate
            b.Quote(0, mid: newRate, prevClose: newRate, eff: B, mat: M1);
            b.Quote(1, mid: m1, prevClose: m1, eff: M1, mat: M2);
            b.Quote(2, mid: m2, prevClose: m2, eff: M2, mat: M3);
            b.Quote(3, mid: m3, prevClose: m3, eff: M3, mat: M4);

            var clean = Cal.PrevBd(B);
            b.Contract(Pa, bounds, Cal.D(-70), clean, oldFix);
            b.Contract(B, bounds, Cal.D(-70), clean, newRate);
            b.Contract(M1, bounds, Cal.D(-70), clean, m1);
            b.Contract(M2, bounds, Cal.D(-70), clean, m2);
            b.Contract(M3, bounds, Cal.D(-70), clean, m3);
            b.Contract(M4, bounds, Cal.D(-70), clean, m4);
            spec.Banks.Add(b);

            // Priced = (mid - fixing) * 100 against the STALE fixing:
            //   M1: (3.560 - 3.900) * 100 = -34.0     M2: -40.0     M3: -44.0
            // Against the rate that is actually in force (3.650) they would be -9 / -15 / -19.
            spec.Expect.Add(new BankExpect
            {
                Bank = "FOMC",
                Fixing = 3.650,
                Rebased = true,
                Rows = new List<RowExpect>
                {
                    new(M1, M2, m1, -9.0, null, Any.Num, Any.Num, Any.Num),
                    new(M2, M3, m2, -15.0, -6.0, Any.Num, Any.Num, Any.Num),
                    new(M3, M4, m3, -19.0, -4.0, Any.Num, Any.Num, Any.Num),
                },
            });
            spec.Custom.Add(s =>
            {
                var msgs = new List<string>();
                var run = s.Run("FOMC")!;
                // The base for "what is priced" should represent the rate now in force. The
                // market's own statement of that rate is the CURRENT period's OIS, which the run
                // already holds. A base more than a few bp away from it means every Priced and
                // every % of 25bp on the board carries the already-delivered move.
                if (run.RefPct is { } fixv && Math.Abs(fixv - newRate) * 100.0 > 5.0)
                    msgs.Add($"Priced is measured against {fixv:0.000}, but the period now running " +
                             $"is quoted at {newRate:0.000} - a {Math.Abs(fixv - newRate) * 100.0:0}bp " +
                             "gap, which is the cut the Fed already delivered. Every Priced and " +
                             "every % of 25bp on the board is overstated by it, and the fixing " +
                             "carries no marker to say so (RefRebased is false, so no dagger and " +
                             "no '(rebased)' anywhere).");
                if (s.Front("FOMC") is { PricedBp: { } p })
                {
                    double honest = (m1 - newRate) * 100.0;
                    if (Math.Abs(p - honest) > 5.0)
                        msgs.Add($"the front line reads {p:+0.0;-0.0;0.0}bp priced into the next " +
                                 $"meeting; measured against the rate now in force it is " +
                                 $"{honest:+0.0;-0.0;0.0}bp. The front table is the most-read row " +
                                 "on the board.");
                }
                return msgs;
            });
            yield return spec;
        }

        // ---------------------------------------------------------------- 55
        {
            // BOTH decide today. Same email, same table, two different bases.
            var T = Cal.D(0);
            var fPb = Cal.D(-84); var fPa = Cal.D(-42);
            var fM1 = Cal.D(42); var fM2 = Cal.D(84); var fM3 = Cal.D(126); var fM4 = Cal.D(168);
            var fB = new[] { fPb, fPa, T, fM1, fM2, fM3, fM4 };
            const double fFix = 3.900, fNew = 3.650;
            const double fm1 = 3.560, fm2 = 3.500, fm3 = 3.460, fm4 = 3.430;

            var eS2 = Cal.D(-92); var eS1 = Cal.D(-50);
            var eSt0 = Cal.D(6); var eD1 = Cal.D(49); var eSt1 = Cal.D(55);
            var eD2 = Cal.D(98); var eSt2 = Cal.D(104);
            var eD3 = Cal.D(147); var eSt3 = Cal.D(153); var eSt4 = Cal.D(202);
            var eB = new[] { Cal.D(-98), Cal.D(-56), T, eD1, eD2, eD3 };
            const double eFix = 2.150, eNew = 1.900;   // the ECB cut 25bp too
            const double em1 = 1.850, em2 = 1.820, em3 = 1.800;

            var spec = new ScenarioSpec
            {
                Id = 55,
                Name = "Fed and ECB both cut today - one table, two bases",
                Question = "On a day two banks move, does the CB front table answer 'what is " +
                           "priced' the same way for both?",
            };

            var fed = new BankSpec { Bank = "FOMC", DecisionTimeLondon = Cal.TimePassed };
            fed.Dates.AddRange(new[] { fPb, fPa, T, fM1, fM2, fM3, fM4 });
            fed.DecisionDates.AddRange(new[] { T, fM1, fM2, fM3, fM4 });
            fed.Fix(fFix).FixHist(Cal.D(-70), Cal.D(-1), fFix);
            fed.Quote(0, mid: fNew, prevClose: fFix, eff: T, mat: fM1);
            fed.Quote(1, mid: fm1, prevClose: fm1, eff: fM1, mat: fM2);
            fed.Quote(2, mid: fm2, prevClose: fm2, eff: fM2, mat: fM3);
            fed.Quote(3, mid: fm3, prevClose: fm3, eff: fM3, mat: fM4);
            fed.Contract(fPa, fB, Cal.D(-70), Cal.D(-1), fFix);
            fed.Contract(T, fB, Cal.D(-70), Cal.D(-1), fNew);
            fed.Contract(fM1, fB, Cal.D(-70), Cal.D(-1), fm1);
            fed.Contract(fM2, fB, Cal.D(-70), Cal.D(-1), fm2);
            fed.Contract(fM3, fB, Cal.D(-70), Cal.D(-1), fm3);
            fed.Contract(fM4, fB, Cal.D(-70), Cal.D(-1), fm4);
            spec.Banks.Add(fed);

            var ecb = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
            ecb.Dates.AddRange(new[] { eS2, eS1, eSt0, eSt1, eSt2, eSt3 });
            ecb.DecisionDates.AddRange(new[] { T, eD1, eD2, eD3 });
            ecb.Fix(eFix).FixHist(Cal.D(-70), Cal.D(-1), eFix);
            ecb.Quote(0, mid: eFix, prevClose: eFix, eff: eS1, mat: eSt0);
            ecb.Quote(1, mid: eNew, prevClose: 2.140, eff: eSt0, mat: eSt1);
            ecb.Quote(2, mid: em1, prevClose: em1, eff: eSt1, mat: eSt2);
            ecb.Quote(3, mid: em2, prevClose: em2, eff: eSt2, mat: eSt3);
            ecb.Quote(4, mid: em3, prevClose: em3, eff: eSt3, mat: eSt4);
            ecb.Contract(eS1, eB, Cal.D(-70), Cal.D(-1), eFix);
            ecb.Contract(eSt0, eB, Cal.D(-70), Cal.D(-1), eNew);
            ecb.Contract(eSt1, eB, Cal.D(-70), Cal.D(-1), em1);
            ecb.Contract(eSt2, eB, Cal.D(-70), Cal.D(-1), em2);
            ecb.Contract(eSt3, eB, Cal.D(-70), Cal.D(-1), em3);
            spec.Banks.Add(ecb);

            // ECB: re-based onto the just-decided period's OIS (1.900), so
            //   eSt1 Priced = (1.850 - 1.900) * 100 = -5.0
            // FOMC: NOT re-based, so
            //   fM1 Priced = (3.560 - 3.900) * 100 = -34.0, of which 25 is already delivered
            spec.Expect.Add(new BankExpect
            {
                Bank = "ECB", Fixing = eNew, Rebased = true,
                Front = new FrontExpect(eD1, eSt1, em1, eNew, -5.0, Rebased: true),
            });
            spec.Expect.Add(new BankExpect
            {
                // FIXED 2026-08-27: the FOMC line re-bases too, so both columns headed
                // "Priced (bp)" are measured against the rate each bank just set.
                //   (3.560 - 3.650) x 100 = -9.0
                Bank = "FOMC", Fixing = fNew, Rebased = true,
                Front = new FrontExpect(fM1, fM1, fm1, fNew, -9.0, Rebased: true),
            });
            spec.Custom.Add(s =>
            {
                var msgs = new List<string>();
                var fedRun = s.Run("FOMC")!;
                var ecbRun = s.Run("ECB")!;
                bool fedRebased = fedRun.RefRebased, ecbRebased = ecbRun.RefRebased;
                if (fedRebased != ecbRebased)   // must now be equal - one table, one base rule
                    msgs.Add($"both banks cut 25bp today, and the same table answers 'what is " +
                             $"priced' against two different bases: ECB re-based onto the " +
                             $"just-decided period ({ecbRun.RefPct:0.000}), FOMC still against the " +
                             $"pre-cut fixing ({fedRun.RefPct:0.000}). The ECB line is marked with " +
                             "a dagger; the FOMC line has no marker, so nothing on the page tells " +
                             "the reader the two columns are not comparable.");
                return msgs;
            });
            yield return spec;
        }
    }
}
