using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;

namespace RateDesk.Tests
{
    /// <summary>The zero-touch calendar police: every UPDATE must demand calendar work loudly,
    /// weeks before stale data could misprint a date or a lookback.</summary>
    public class CalendarHealthTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-cal-" + Guid.NewGuid().ToString("N"));
        private HistoryStore Store() => new(Path.Combine(_dir, "h.db"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly DateTime AsOf = new(2026, 8, 11);
        private const string Pat = "TESTCAL{N}";
        private static string Tk(int n) => Pat.Replace("{N}", n.ToString()) + " Curncy";

        private static MeetingScheduleDef Sched(
            IEnumerable<DateTime> dates, IEnumerable<DateTime>? decisions = null) => new()
        {
            Name = "TESTCAL", Ccy = "USD", Header = "t",
            Tickers = new List<string> { Pat },
            Dates = dates.ToList(),
            DecisionDates = (decisions ?? Array.Empty<DateTime>()).ToList(),
            // a FULLY covered calendar now includes the announcement time — the decision-day
            // front roll is gated on it (2026-08-20)
            DecisionTimeLondon = "12:00",
        };

        [Fact]
        public void DecisionCalendarRunningDry_IsFlaggedBeforeItBites()
        {
            using var store = Store();
            var sched = Sched(
                dates: new[] { AsOf.AddDays(20), AsOf.AddDays(60), AsOf.AddDays(100) },
                decisions: new[] { AsOf.AddDays(19) });   // covers the first period only

            var w = CalendarHealth.Check(new[] { sched }, new RatesSnapshot(), store, AsOf);

            Assert.Contains(w, x => x.Contains("no decision date") && x.Contains("top up"));
        }

        [Fact]
        public void FullyCoveredCalendars_RaiseNothing()
        {
            using var store = Store();
            var sched = Sched(
                dates: new[] { AsOf.AddDays(20), AsOf.AddDays(60) },
                decisions: new[] { AsOf.AddDays(19), AsOf.AddDays(59) });

            var w = CalendarHealth.Check(new[] { sched }, new RatesSnapshot(), store, AsOf);

            Assert.Empty(w);
        }

        [Fact]
        public void ATickerPeriodMissingFromTheGrid_IsAPhantomMeetingWarning()
        {
            using var store = Store();
            var snap = new RatesSnapshot();
            snap.SetEffective(Tk(1), AsOf.AddDays(40));   // the grid knows nothing about this
            snap.SetMaturity(Tk(1), AsOf.AddDays(80));
            var sched = Sched(dates: new[] { AsOf.AddDays(20) });

            var w = CalendarHealth.Check(new[] { sched }, snap, store, AsOf);

            Assert.Contains(w, x => x.Contains("not in") && x.Contains("phantom"));
        }

        [Fact]
        public void AnObservedRoll_WithNoCalendarBoundaryNearby_IsFlagged()
        {
            using var store = Store();
            // the store watched rung 1 re-point on the 5th — but the calendar has no boundary there
            store.SetMaturity(Tk(1), AsOf.AddDays(-7), AsOf.AddDays(30));
            store.SetMaturity(Tk(1), AsOf.AddDays(-6), AsOf.AddDays(30));
            store.SetMaturity(Tk(1), AsOf.AddDays(-5), AsOf.AddDays(70));   // the re-point
            var sched = Sched(dates: new[] { AsOf.AddDays(30), AsOf.AddDays(70) });

            var w = CalendarHealth.Check(new[] { sched }, new RatesSnapshot(), store, AsOf);

            Assert.Contains(w, x => x.Contains("re-pointed") && x.Contains("no calendar boundary"));
        }

        [Fact]
        public void AnObservedRoll_ExplainedByTheCalendar_IsQuiet()
        {
            using var store = Store();
            store.SetMaturity(Tk(1), AsOf.AddDays(-7), AsOf.AddDays(30));
            store.SetMaturity(Tk(1), AsOf.AddDays(-5), AsOf.AddDays(70));
            var boundary = AsOf.AddDays(-6);              // decision the day before the observed change
            var sched = Sched(
                dates: new[] { AsOf.AddDays(30), AsOf.AddDays(70) },
                decisions: new[] { boundary, AsOf.AddDays(29), AsOf.AddDays(69) });

            var w = CalendarHealth.Check(new[] { sched }, new RatesSnapshot(), store, AsOf);

            Assert.DoesNotContain(w, x => x.Contains("re-pointed"));
        }

        [Fact]
        public void ARollObservedLate_IsExplainedByAnyBoundaryInTheObservationWindow()
        {
            // updates run WEEKLY: the RBA rolled at its 11-Aug decision but the store first saw
            // the new maturity on the 20th. The re-point is only known to lie in (prev update,
            // this update] — a boundary anywhere in that window explains it. Judging the
            // observation date alone false-flagged this live on 2026-08-20.
            using var store = Store();
            store.SetMaturity(Tk(1), AsOf.AddDays(-10), AsOf.AddDays(30));
            store.SetMaturity(Tk(1), AsOf.AddDays(-1), AsOf.AddDays(70));   // first sighting, 9d later
            var sched = Sched(
                dates: new[] { AsOf.AddDays(30), AsOf.AddDays(70) },
                decisions: new[] { AsOf.AddDays(-8), AsOf.AddDays(29), AsOf.AddDays(69) });

            var w = CalendarHealth.Check(new[] { sched }, new RatesSnapshot(), store, AsOf);

            Assert.DoesNotContain(w, x => x.Contains("re-pointed"));
        }
    }
}
