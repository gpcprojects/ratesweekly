using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Series;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>The price-read renumber detector. What matters about it is not that it spots a
    /// roll - it is that it REFUSES to answer when the prices cannot say, because the caller
    /// publishes a number based on the answer. Every "must abstain" test here is load-bearing.</summary>
    public class RungShiftScanTests : IDisposable
    {
        private readonly string _dir;
        private readonly HistoryStore _store;
        private static readonly DateTime AsOf = new DateTime(2026, 8, 27);

        public RungShiftScanTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "rw-shift-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _store = new HistoryStore(Path.Combine(_dir, "h.db"));
        }

        public void Dispose()
        {
            _store.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string Tk(int n) => $"TEST{n}A Curncy";

        /// <summary>Seed the strip day by day. <paramref name="levels"/> gives, for each day, the
        /// value of rung 1, 2, 3 ... in order — i.e. exactly what the tickers printed.</summary>
        private void Seed(Dictionary<DateTime, double[]> levels)
        {
            int maxRung = levels.Values.Max(v => v.Length);
            for (int n = 1; n <= maxRung; n++)
            {
                var pts = levels.Where(kv => kv.Value.Length >= n)
                                .Select(kv => new HistPoint(kv.Key, kv.Value[n - 1]))
                                .ToList();
                _store.UpsertDaily(Tk(n), pts, excludeToday: false);
            }
        }

        /// <summary>Business days ending the day before AsOf.</summary>
        private static List<DateTime> Days(int n)
        {
            var days = new List<DateTime>();
            var d = AsOf.AddDays(-1);
            while (days.Count < n)
            {
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
                d = d.AddDays(-1);
            }
            days.Reverse();
            return days;
        }

        // a sloped strip: each rung 12bp above the one before, so every pair discriminates
        private static double[] Strip(double front) =>
            new[] { front, front + 0.12, front + 0.24, front + 0.36, front + 0.48 };

        [Fact]
        public void QuietStrip_NothingRenumbered_AndItSaysSo()
        {
            var days = Days(10);
            var lv = days.ToDictionary(d => d, _ => Strip(2.00));
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);

            Assert.All(scan, s => Assert.Equal(RungShiftScan.Verdict.Confirmed, s.Verdict));
            Assert.All(scan, s => Assert.Equal(0, s.Shift));
            Assert.Equal(0, RungShiftScan.ShiftSince(scan, days[0]));
        }

        [Fact]
        public void AMeetingPasses_TheWholeStripStepsUpOne()
        {
            // on the roll day every rung takes what the NEXT rung held the day before
            var days = Days(10);
            var lv = new Dictionary<DateTime, double[]>();
            for (int i = 0; i < days.Count; i++)
                lv[days[i]] = i < 5
                    ? Strip(2.00)
                    : new[] { 2.12, 2.24, 2.36, 2.48, 2.60 };   // Strip(2.00) shifted along itself
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);
            var roll = scan.Single(s => s.Shift != 0);

            Assert.Equal(days[5], roll.Day);
            Assert.Equal(1, roll.Shift);
            Assert.Equal(RungShiftScan.Verdict.Confirmed, roll.Verdict);
            Assert.Contains("a meeting passed", roll.Why);
            // a contract sat one rung FURTHER OUT before the roll
            Assert.Equal(1, RungShiftScan.ShiftSince(scan, days[0]));
            Assert.Equal(0, RungShiftScan.ShiftSince(scan, days[6]));
        }

        [Fact]
        public void AnUnscheduledMeetingIsInserted_TheStripStepsTheOtherWay()
        {
            // THE CASE THE CALENDAR CANNOT GET RIGHT. A meeting appears at the front, so every
            // contract moves one rung FURTHER OUT: today's rung 2 holds yesterday's rung 1.
            var days = Days(10);
            var lv = new Dictionary<DateTime, double[]>();
            for (int i = 0; i < days.Count; i++)
                lv[days[i]] = i < 6
                    ? Strip(2.00)
                    : new[] { 1.90, 2.00, 2.12, 2.24, 2.36 };   // a new front, the rest pushed out
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);
            var ins = scan.Single(s => s.Shift != 0);

            Assert.Equal(days[6], ins.Day);
            Assert.Equal(-1, ins.Shift);
            Assert.Equal(RungShiftScan.Verdict.Confirmed, ins.Verdict);
            Assert.Contains("extra meeting appeared", ins.Why);
            Assert.Equal(-1, RungShiftScan.ShiftSince(scan, days[0]));
        }

        [Fact]
        public void AFlatStrip_Abstains_AndThatIsHarmless()
        {
            // Every rung the same: no hypothesis can be told from another. The scan must say so
            // rather than pick one - and note that every hypothesis yields the SAME anchor here,
            // which is why abstaining costs nothing.
            var days = Days(10);
            var lv = days.ToDictionary(d => d, _ => new[] { 2.00, 2.00, 2.00, 2.00, 2.00 });
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);

            Assert.All(scan, s => Assert.Equal(0, s.Shift));      // never a phantom roll
            Assert.Equal(0, RungShiftScan.ShiftSince(scan, days[0]) ?? 0);
        }

        [Fact]
        public void ABigParallelMarketMove_IsNotMistakenForARoll()
        {
            // the whole curve drops 30bp overnight. Every rung moved, but NOT onto its
            // neighbour's old value - the shape is intact, so nothing renumbered.
            var days = Days(10);
            var lv = new Dictionary<DateTime, double[]>();
            for (int i = 0; i < days.Count; i++)
                lv[days[i]] = i < 5 ? Strip(2.00) : Strip(1.70);
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);

            Assert.DoesNotContain(scan, s => s.Verdict == RungShiftScan.Verdict.Confirmed && s.Shift != 0);
        }

        [Fact]
        public void AMoveThatHalfLooksLikeARoll_Abstains_RatherThanGuess()
        {
            // the curve steepens by roughly one rung-gap but not cleanly: the roll hypothesis
            // fits, badly, and so does staying put. Neither wins - so neither is published.
            var days = Days(10);
            var lv = new Dictionary<DateTime, double[]>();
            for (int i = 0; i < days.Count; i++)
                lv[days[i]] = i < 5
                    ? Strip(2.00)
                    : new[] { 2.07, 2.20, 2.30, 2.44, 2.55 };   // near the roll, not on it
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);
            var suspect = scan.Single(s => s.Day == days[5]);

            Assert.Equal(RungShiftScan.Verdict.Unknown, suspect.Verdict);
            Assert.Null(RungShiftScan.ShiftSince(scan, days[0]));   // the chain is broken, so no answer
        }

        [Fact]
        public void OneUnknownDayBreaksTheWholeChain()
        {
            // a hole in the middle: the scan may be certain either side, but the total is not
            // knowable, and a partial chain is a guess wearing a number's clothes.
            var days = Days(10);
            var lv = new Dictionary<DateTime, double[]>();
            for (int i = 0; i < days.Count; i++)
                lv[days[i]] = i == 4
                    ? new[] { 2.07, 2.20, 2.30, 2.44, 2.55 }
                    : (i < 4 ? Strip(2.00) : Strip(2.00));
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);

            Assert.Null(RungShiftScan.ShiftSince(scan, days[0]));
            Assert.Equal(0, RungShiftScan.ShiftSince(scan, days[5]));  // clean stretch still answers
        }

        [Fact]
        public void TooFewRungs_Abstains()
        {
            // two rungs is not three simultaneous confirmations
            var days = Days(8);
            var lv = new Dictionary<DateTime, double[]>();
            for (int i = 0; i < days.Count; i++)
                lv[days[i]] = i < 4 ? new[] { 2.00, 2.12 } : new[] { 2.12, 2.24 };
            Seed(lv);

            var scan = RungShiftScan.Scan(_store, Tk, days[0], days[^1]);

            Assert.DoesNotContain(scan, s => s.Verdict == RungShiftScan.Verdict.Confirmed && s.Shift != 0);
        }
    }
}
