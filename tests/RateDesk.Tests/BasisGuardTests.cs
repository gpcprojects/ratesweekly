using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core;
using RateDesk.Core.Analytics;
using RateDesk.Core.Market;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>The failure mode these lock down: a level series and the live mid on DIFFERENT
    /// bases. Every statistic that ranks one inside the other then computes correctly and means
    /// nothing — %ile 100 on a series the level has never traded in, z-scores of 8-11, and an
    /// AT RANGE of 186% (a position inside min..max, which cannot exceed 100 by construction).
    ///
    /// <para>The live case was a USD M40/M45/M50 5Y fly: IMM-dated legs have no forward-ticker
    /// history, fall to the annuity-less par approximation, and the approximation error does not
    /// cancel across -1/+2/-1 weights. Two defences are tested here — the combined-series anchor
    /// that stops it happening, and the guard that refuses to publish if it happens anyway.</para></summary>
    public class BasisGuardTests
    {
        /// <summary>Series in bp with a controllable daily wobble, ending at <paramref name="endAt"/>.</summary>
        private static List<HistPoint> Wobble(double centre, double amp, int days, double step = 0.15)
        {
            var start = new DateTime(2025, 8, 1);
            var pts = new List<HistPoint>(days);
            for (int i = 0; i < days; i++)
                pts.Add(new HistPoint(start.AddDays(i),
                    centre + amp * Math.Sin(i * 2 * Math.PI / 90.0) + step * ((i % 7) - 3)));
            return pts;
        }

        [Fact]
        public void SameBasis_PublishesEverything()
        {
            var pts = Wobble(centre: 4.0, amp: 2.0, days: 400);
            // a plausible intraday move away from the last close — this must NOT read as a basis break
            var s = SeriesStats.Compute(pts, liveLast: pts[^1].Value + 0.4, changeScale: 1.0);

            Assert.Null(s.SuppressReason);
            Assert.False(s.Suppressed);
            Assert.NotNull(s.Percentile1y);
            Assert.NotNull(s.Range1yPct);
            Assert.NotNull(s.ZScore1y);
            Assert.NotNull(s.Chg1w);
            Assert.NotNull(s.Min1y);
        }

        [Fact]
        public void OffBasisLevel_SuppressesEveryCrossBasisStat()
        {
            // the shape of the live fault: history spent the year around 1.4..6.3bp, mid says 10.55
            var pts = Wobble(centre: 3.85, amp: 2.45, days: 400);
            var s = SeriesStats.Compute(pts, liveLast: 10.55, changeScale: 1.0);

            Assert.NotNull(s.SuppressReason);
            Assert.True(s.Suppressed);

            // nothing that compares the live level to the series survives
            Assert.Null(s.Percentile1y);
            Assert.Null(s.Range1yPct);
            Assert.Null(s.ZScore1y);
            Assert.Null(s.ZScore3m);
            Assert.Null(s.ZScore6m);
            Assert.Null(s.Mean1y);
            Assert.Null(s.Min1y);
            Assert.Null(s.Max1y);
            Assert.Null(s.Chg1w);
            Assert.Null(s.Chg1m);
            Assert.Null(s.Chg3m);
            Assert.Null(s.Chg6m);
            Assert.Null(s.Chg1y);
            Assert.Null(s.ChgYtd);
            // including 1d: the caller's exact prev-close reprice may replace it, but the
            // history-derived value carries the same offset and must not stand in for it
            Assert.Null(s.Chg1d);

            // ...and everything shift-invariant does: these are built from differences, so a
            // constant basis offset cannot touch them and they stay honest
            Assert.NotNull(s.RealizedVol1yBp);
            Assert.NotNull(s.RealizedVol3mBp);
            Assert.True(s.BasisGap > 4.0, $"gap {s.BasisGap}");
        }

        /// <summary>Detector 2's reason for existing: a gap small enough to land INSIDE a wide range
        /// leaves every output looking perfectly reasonable while every z-score is wrong.</summary>
        [Fact]
        public void OffBasisButInsideTheRange_IsStillCaught()
        {
            var start = new DateTime(2025, 8, 1);
            var pts = new List<HistPoint>();
            for (int i = 0; i < 400; i++)
                pts.Add(new HistPoint(start.AddDays(i), 50.0 + 45.0 * Math.Sin(i * 2 * Math.PI / 180.0)));

            double lastClose = pts[^1].Value;
            var s = SeriesStats.Compute(pts, liveLast: lastClose + 30.0, changeScale: 1.0);

            // the level lands well inside min..max, so detector 1 cannot see it
            Assert.InRange(lastClose + 30.0, s.Min1y ?? -1e9, s.Max1y ?? 1e9);
            Assert.NotNull(s.SuppressReason);
            Assert.Null(s.ZScore1y);
        }

        /// <summary>The invariant the screen violated. Whatever the inputs, a position-in-range is
        /// a position in a range: publish a number in [0,100] or publish nothing.</summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(3.0)]
        [InlineData(6.0)]
        [InlineData(10.55)]
        [InlineData(-40.0)]
        [InlineData(500.0)]
        public void RangePosition_IsNeverImpossible(double liveLast)
        {
            var pts = Wobble(centre: 3.85, amp: 2.45, days: 400);
            var s = SeriesStats.Compute(pts, liveLast: liveLast, changeScale: 1.0);
            if (s.Range1yPct is double r)
                Assert.InRange(r, 0.0, 100.0);
            if (s.Percentile1y is double p)
                Assert.InRange(p, 0.0, 100.0);
        }

        /// <summary>No published figure may be NaN or infinite — a degenerate series must come back
        /// empty-handed, not with arithmetic garbage that formats into a tile.</summary>
        [Fact]
        public void DegenerateSeries_PublishesNoNonFiniteNumbers()
        {
            var flat = Enumerable.Range(0, 300)
                .Select(i => new HistPoint(new DateTime(2025, 8, 1).AddDays(i), 2.0)).ToList();
            foreach (var s in new[]
            {
                SeriesStats.Compute(flat, liveLast: 2.0, changeScale: 1.0),
                SeriesStats.Compute(flat, liveLast: 900.0, changeScale: 1.0),
                SeriesStats.Compute(new List<HistPoint>(), liveLast: 1.0, changeScale: 1.0),
            })
            {
                foreach (var v in new[]
                {
                    s.Percentile1y, s.Range1yPct, s.ZScore1y, s.ZScore3m, s.ZScore6m, s.Mean1y,
                    s.Std1yBp, s.RealizedVol1yBp, s.RealizedVol3mBp, s.HalfLifeDays, s.Chg1w, s.Chg1y,
                })
                {
                    if (v is double d)
                        Assert.True(double.IsFinite(d), $"non-finite published: {d}");
                }
            }
        }

        /// <summary>MID O'RIDE enters a hypothetical level on purpose — often outside the range, which
        /// is the whole point of asking "where would this trade score". The guard must judge the
        /// TRUE mid, not the typed one, or the feature disables itself exactly when it is used.</summary>
        [Fact]
        public void MidOverride_IsNotMistakenForABrokenHistory()
        {
            var pts = Wobble(centre: 3.85, amp: 2.45, days: 400);
            double trueMid = pts[^1].Value + 0.2;

            var s = SeriesStats.Compute(pts, liveLast: 40.0, changeScale: 1.0, basisRef: trueMid);

            Assert.Null(s.SuppressReason);
            Assert.NotNull(s.ZScore1y);
            Assert.True(s.ZScore1y > 5, "an entered level far above the range should score far above it");
            // the impossible-figure rule still binds: rank stats stay inside their own bounds
            if (s.Range1yPct is double r) Assert.InRange(r, 0.0, 100.0);
        }

        /// <summary>A constant shift is the only correction allowed: it must leave every daily change,
        /// the realized vol and the mean-reversion half-life exactly as they were. That is what makes
        /// anchoring safe to apply to a level series and its roll overlays together.</summary>
        [Fact]
        public void ConstantShift_PreservesEveryDifferenceBasedStat()
        {
            var pts = Wobble(centre: 3.85, amp: 2.45, days: 400);
            var shifted = pts.Select(p => new HistPoint(p.Date, p.Value + 5.35)).ToList();

            var a = SeriesStats.Compute(pts, liveLast: pts[^1].Value, changeScale: 1.0);
            var b = SeriesStats.Compute(shifted, liveLast: shifted[^1].Value, changeScale: 1.0);

            Assert.Equal(a.RealizedVol1yBp!.Value, b.RealizedVol1yBp!.Value, 9);
            Assert.Equal(a.RealizedVol3mBp!.Value, b.RealizedVol3mBp!.Value, 9);
            Assert.Equal(a.HalfLifeDays!.Value, b.HalfLifeDays!.Value, 6);
            Assert.Equal(a.Chg1w!.Value, b.Chg1w!.Value, 9);
            Assert.Equal(a.Chg1y!.Value, b.Chg1y!.Value, 9);
            Assert.Equal(a.ZScore1y!.Value, b.ZScore1y!.Value, 9);
            Assert.Equal(a.Percentile1y!.Value, b.Percentile1y!.Value, 9);
        }
    }

    /// <summary>Meeting-run date resolution. The rule under test: a row is labelled with the START of
    /// the period its own quote covers. That is the previous rung's maturity only while the periods
    /// are contiguous — true for nine of the ten families, false for the BOJ.</summary>
    public class MeetingDateTests
    {
        // ResolveMeetingDates reads only the snapshot; the store just satisfies the constructor
        private static PricingService Service(RatesSnapshot snap)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratedesk_meet_cfg");
            System.IO.Directory.CreateDirectory(dir);
            var usd = TestConfigs.Usd();
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "usd.json"),
                System.Text.Json.JsonSerializer.Serialize(usd));
            return new PricingService(RateDesk.Core.Config.ConfigStore.LoadFromDirectory(dir), snap);
        }

        private static MeetingScheduleDef Sched(string pattern) => new()
        {
            Name = "TEST", Ccy = "USD", Header = "t", Tickers = new List<string> { pattern },
        };

        private static void Rung(RatesSnapshot snap, string pattern, int n, DateTime? eff, DateTime mat)
        {
            var tk = pattern.Replace("{N}", n.ToString()) + " Curncy";
            snap.SetMaturity(tk, mat);
            if (eff is DateTime e) snap.SetEffective(tk, e);
        }

        [Fact]
        public void ContiguousFamily_IsUnchangedByTheEffectiveDatePreference()
        {
            // FOMC shape: every period starts exactly where the previous one matured
            var snap = new RatesSnapshot();
            const string p = "TESTFED{N}";
            Rung(snap, p, 0, new DateTime(2026, 8, 11), new DateTime(2026, 9, 16));
            Rung(snap, p, 1, new DateTime(2026, 9, 16), new DateTime(2026, 10, 28));
            Rung(snap, p, 2, new DateTime(2026, 10, 28), new DateTime(2026, 12, 9));

            var d = Service(snap).ResolveMeetingDates(Sched(p)).Dates;

            Assert.Equal(new DateTime(2026, 9, 16), d[1]);
            Assert.Equal(new DateTime(2026, 10, 28), d[2]);
            Assert.Equal(new DateTime(2026, 12, 9), d[3]);
        }

        [Fact]
        public void BojShape_LabelsThePeriodStart_NotThePrecedingDecision()
        {
            // real JYSOMPM geometry, 2026-08-07: rung 2 quotes 02-Nov -> 18-Dec while rung 1 matured
            // on the 30-Oct DECISION. Labelling row 2 "30-Oct" names the decision, not the period.
            var snap = new RatesSnapshot();
            const string p = "TESTBOJ{N}";
            Rung(snap, p, 0, new DateTime(2026, 8, 12), new DateTime(2026, 9, 24));
            Rung(snap, p, 1, new DateTime(2026, 9, 24), new DateTime(2026, 10, 30));
            Rung(snap, p, 2, new DateTime(2026, 11, 2), new DateTime(2026, 12, 18));
            Rung(snap, p, 3, new DateTime(2026, 12, 21), new DateTime(2027, 1, 22));

            var d = Service(snap).ResolveMeetingDates(Sched(p)).Dates;

            Assert.Equal(new DateTime(2026, 9, 24), d[1]);
            Assert.Equal(new DateTime(2026, 11, 2), d[2]);   // NOT 30-Oct
            Assert.Equal(new DateTime(2026, 12, 21), d[3]);  // NOT 18-Dec
        }

        [Fact]
        public void AliasedRungs_EndTheFamily()
        {
            // Bloomberg aliases past-the-end numbers back to #1 (USSOFED10, JYSOMPM10): the maturity
            // stops increasing, and a run that trusted the NUMBER would inject bogus past pillars.
            var snap = new RatesSnapshot();
            const string p = "TESTALIAS{N}";
            Rung(snap, p, 0, new DateTime(2026, 8, 11), new DateTime(2026, 9, 16));
            Rung(snap, p, 1, new DateTime(2026, 9, 16), new DateTime(2026, 10, 28));
            Rung(snap, p, 2, new DateTime(2026, 9, 16), new DateTime(2026, 10, 28)); // alias of #1

            var d = Service(snap).ResolveMeetingDates(Sched(p)).Dates;

            Assert.Equal(2, d.Count);
            Assert.DoesNotContain(d, kv => kv.Key > 2);
        }

        [Fact]
        public void DecisionWeek_RunDownMaturityArtifact_TheRungsOwnEffectiveDateWins()
        {
            // live RBA, 2026-08-11 (decision day): the run-down ADSF0A printed maturity 13-Aug —
            // a T+1 settlement artifact — while ADSF1A's own SW_EFF_DT said 12-Aug, the true
            // period start (decision 11-Aug + 1d). One day of disagreement about the SAME
            // boundary resolves to the rung's own field, not the neighbour's maturity. Found by
            // tools\audit_email_dates.py against the live terminal.
            var snap = new RatesSnapshot();
            const string p = "TESTRBA{N}";
            Rung(snap, p, 0, new DateTime(2026, 8, 12), new DateTime(2026, 8, 13));  // artifact
            Rung(snap, p, 1, new DateTime(2026, 8, 12), new DateTime(2026, 9, 30));
            Rung(snap, p, 2, new DateTime(2026, 9, 30), new DateTime(2026, 11, 4));

            var d = Service(snap).ResolveMeetingDates(Sched(p)).Dates;

            Assert.Equal(new DateTime(2026, 8, 12), d[1]);   // NOT the 13-Aug artifact
            Assert.Equal(new DateTime(2026, 9, 30), d[2]);
        }

        [Fact]
        public void BetweenDecisionAndEffect_PricedRebasesOffTheDecidedPeriod()
        {
            // The ECB shape: a change announced Thursday starts the following Wednesday. Until
            // then the o/n fixing prints the OLD rate, and priced-vs-fixing would overstate the
            // whole run by the just-delivered change. The base must re-base AUTOMATICALLY onto
            // the decided period's own OIS — zero touch (desk 2026-08-11).
            var snap = new RatesSnapshot();
            const string p = "TESTECB{N}";
            var dec = DateTime.Today.AddDays(-2);
            var eff = DateTime.Today.AddDays(4);

            Rung(snap, p, 0, eff, eff.AddDays(42));                 // the decided period
            Rung(snap, p, 1, eff.AddDays(42), eff.AddDays(84));     // next undecided
            snap.Update(p.Replace("{N}", "0") + " Curncy", null, null, 2.25);  // NEW rate
            snap.Update(p.Replace("{N}", "1") + " Curncy", null, null, 2.30);
            snap.Update("TESTFIX Index", null, null, 2.00);         // fixing still on the OLD rate

            var sched = new MeetingScheduleDef
            {
                Name = "TESTW", Ccy = "USD", Header = "t",
                Tickers = new List<string> { p },
                RefTicker = "TESTFIX Index",
                DecisionDates = new List<DateTime> { dec },
                Dates = new List<DateTime> { eff, eff.AddDays(42), eff.AddDays(84) },
            };

            var run = Service(snap).MeetingRun(sched);

            Assert.NotNull(run.RefPct);
            Assert.Equal(2.25, run.RefPct!.Value, 6);   // the decided period's rate, not 2.00
        }

        [Fact]
        public void OutsideTheDecisionWindow_TheFixingStands()
        {
            var snap = new RatesSnapshot();
            const string p = "TESTSTD{N}";
            var eff = DateTime.Today.AddDays(30);
            Rung(snap, p, 0, DateTime.Today.AddDays(-12), eff);
            Rung(snap, p, 1, eff, eff.AddDays(42));
            snap.Update(p.Replace("{N}", "0") + " Curncy", null, null, 2.10);
            snap.Update(p.Replace("{N}", "1") + " Curncy", null, null, 2.20);
            snap.Update("TESTFIX Index", null, null, 2.00);

            var sched = new MeetingScheduleDef
            {
                Name = "TESTW2", Ccy = "USD", Header = "t",
                Tickers = new List<string> { p },
                RefTicker = "TESTFIX Index",
                DecisionDates = new List<DateTime> { DateTime.Today.AddDays(-40) },  // long settled
                Dates = new List<DateTime>
                    { DateTime.Today.AddDays(-40), eff, eff.AddDays(42) },
            };

            var run = Service(snap).MeetingRun(sched);
            Assert.Equal(2.00, run.RefPct!.Value, 6);
        }

        [Fact]
        public void AnImplausibleEffectiveDate_IsIgnoredRatherThanTrusted()
        {
            // a start before the previous maturity, or past its own end, is a bad field — not a
            // settlement convention. The maturity-derived date stands.
            var snap = new RatesSnapshot();
            const string p = "TESTBAD{N}";
            Rung(snap, p, 0, new DateTime(2026, 8, 11), new DateTime(2026, 9, 16));
            Rung(snap, p, 1, new DateTime(2020, 1, 1), new DateTime(2026, 10, 28));   // absurdly early
            Rung(snap, p, 2, new DateTime(2027, 6, 1), new DateTime(2026, 12, 9));    // after its own end

            var d = Service(snap).ResolveMeetingDates(Sched(p)).Dates;

            Assert.Equal(new DateTime(2026, 9, 16), d[1]);
            Assert.Equal(new DateTime(2026, 10, 28), d[2]);
        }
    }

    /// <summary>Shipped config\meetings.json. These are data invariants, not code paths: a drifted
    /// grid silently inserts a phantom meeting and every row below it reads the wrong period.</summary>
    public class MeetingsConfigTests
    {
        private static IEnumerable<MeetingScheduleDef> Runs =>
            MeetingsStore.Schedules.Where(s => string.IsNullOrEmpty(s.Kind));

        [Fact]
        public void EveryRunsDatesStrictlyIncrease()
        {
            foreach (var s in Runs)
            {
                var d = s.Dates;
                for (int i = 1; i < d.Count; i++)
                    Assert.True(d[i] > d[i - 1], $"{s.Name}: {d[i]:yyyy-MM-dd} follows {d[i - 1]:yyyy-MM-dd}");
            }
        }

        /// <summary>The 14-day clustering used to migrate settled dates and to de-duplicate a config
        /// date against a ticker maturity is only safe while no two real meetings sit inside it.
        /// Assert the property rather than trusting the comment that claims it.</summary>
        [Fact]
        public void NoTwoMeetingsFallInsideTheClusteringWindow()
        {
            foreach (var s in Runs)
                for (int i = 1; i < s.Dates.Count; i++)
                    Assert.True((s.Dates[i] - s.Dates[i - 1]).TotalDays >= 14,
                        $"{s.Name}: {s.Dates[i - 1]:yyyy-MM-dd} -> {s.Dates[i]:yyyy-MM-dd}");
        }

        [Fact]
        public void DecisionDatesStrictlyIncreaseAndNeverFollowTheirOwnPeriodStart()
        {
            foreach (var s in Runs)
            {
                for (int i = 1; i < s.DecisionDates.Count; i++)
                    Assert.True(s.DecisionDates[i] > s.DecisionDates[i - 1], $"{s.Name} decisionDates");

                // each decision must pair with a period start at or after it, within a settlement lag
                foreach (var dd in s.DecisionDates.Where(x => x >= DateTime.Today))
                {
                    var start = s.Dates.Where(x => x >= dd).OrderBy(x => x).FirstOrDefault();
                    if (start == default) continue;
                    Assert.True((start - dd).TotalDays <= 10,
                        $"{s.Name}: decision {dd:yyyy-MM-dd} has no period start within 10d (nearest {start:yyyy-MM-dd})");
                }
            }
        }

        /// <summary>BOJ is the family the run-date rule exists for, so its shape is pinned: a full
        /// decision calendar, and every start strictly after its decision (never on it).</summary>
        [Fact]
        public void Boj_HasAFullDecisionCalendarWithStartsAfterEachDecision()
        {
            var boj = MeetingsStore.Schedules.Single(s => s.Name == "BOJ");
            var future = boj.Dates.Where(d => d > DateTime.Today).ToList();

            Assert.True(boj.DecisionDates.Count >= future.Count,
                $"BOJ: {future.Count} future periods but only {boj.DecisionDates.Count} decision dates");

            foreach (var start in future)
            {
                var dec = boj.DecisionDates.Where(x => x < start).OrderByDescending(x => x).FirstOrDefault();
                Assert.True(dec != default, $"BOJ: no decision before period start {start:yyyy-MM-dd}");
                double lag = (start - dec).TotalDays;
                Assert.InRange(lag, 1.0, 7.0);   // settlement lag, never zero and never a week+
            }
        }
    }
}
