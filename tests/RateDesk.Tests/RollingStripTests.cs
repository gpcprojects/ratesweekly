using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Tests
{
    public class RollingStripTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-strip-" + Guid.NewGuid().ToString("N"));
        private HistoryStore Store() => new(Path.Combine(_dir, "h.db"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly DateTime AsOf = new(2026, 8, 4);
        private static string Tk(int n) => $"XX{n} Curncy";

        private static void Put(HistoryStore s, int n, DateTime d, double v) =>
            s.UpsertDaily(Tk(n), new[] { new HistPoint(d, v) }, excludeToday: false);

        [Fact]
        public void NoRollInWindow_ComparesTheSameTicker()
        {
            using var store = Store();
            var next = AsOf.AddDays(20);          // the only boundary, in the future
            Put(store, 1, AsOf, 3.50);
            Put(store, 1, AsOf.AddDays(-7), 3.40);

            var t = RollingStrip.Build("t", store, AsOf,
                new[] { ("m1", next) }, new[] { next }, Tk);

            Assert.Equal(3.50, t.Rows[0].Mid!.Value, 10);
            Assert.Equal(3.40, t.Rows[0].WeekLevel!.Value, 10);   // ticker 1 a week ago
        }

        [Fact]
        public void RollInsideWindow_ReadsTheTickerThatPointedAtThisContractThen()
        {
            // A decision settled 3 days ago. A week ago this contract was the SECOND upcoming one,
            // so its level then lives under ticker 2 — reading ticker 1's own old close would book
            // the whole inter-meeting step as a market move (the +12bp phantom-CoD bug).
            using var store = Store();
            var settled = AsOf.AddDays(-3);
            var thisOne = AsOf.AddDays(40);

            Put(store, 1, AsOf, 3.50);
            Put(store, 1, AsOf.AddDays(-7), 3.00);   // WRONG source: was the settled meeting
            Put(store, 2, AsOf.AddDays(-7), 3.45);   // RIGHT source: this contract, a week ago

            var t = RollingStrip.Build("t", store, AsOf,
                new[] { ("m1", thisOne) }, new[] { settled, thisOne }, Tk);

            Assert.Equal(3.45, t.Rows[0].WeekLevel!.Value, 10);
            Assert.Equal(5.0, t.Rows[0].Mid!.Value * 100 - t.Rows[0].WeekLevel!.Value * 100, 6);
            Assert.Contains(t.Notes, n => n.Contains("roll"));
        }

        [Fact]
        public void InteriorRowMilesFromBothNeighbours_IsReplacedByTheirMidpoint()
        {
            // The real Riksbank case: a live two-sided 1.387 between 1.848 and 2.086.
            using var store = Store();
            var d = new[] { AsOf.AddDays(20), AsOf.AddDays(60), AsOf.AddDays(100) };
            Put(store, 1, AsOf, 1.848);
            Put(store, 2, AsOf, 1.387);
            Put(store, 3, AsOf, 2.086);

            var t = RollingStrip.Build("t", store, AsOf,
                new[] { ("a", d[0]), ("b", d[1]), ("c", d[2]) }, d, Tk);

            Assert.Equal((1.848 + 2.086) / 2, t.Rows[1].Mid!.Value, 10);
            Assert.EndsWith("*", t.Rows[1].Label);   // marked inline; hover carries the reason
            Assert.Contains(t.Notes, n => n.Contains("neighbour midpoint"));
        }

        [Fact]
        public void PlausibleInteriorRow_IsLeftAlone()
        {
            using var store = Store();
            var d = new[] { AsOf.AddDays(20), AsOf.AddDays(60), AsOf.AddDays(100) };
            Put(store, 1, AsOf, 1.850);
            Put(store, 2, AsOf, 1.960);   // 3bp off the midpoint — a normal shape
            Put(store, 3, AsOf, 2.080);

            var t = RollingStrip.Build("t", store, AsOf,
                new[] { ("a", d[0]), ("b", d[1]), ("c", d[2]) }, d, Tk);

            Assert.Equal(1.960, t.Rows[1].Mid!.Value, 10);
            Assert.DoesNotContain("*", t.Rows[1].Label);
        }

        [Fact]
        public void EdgeRowIsNeverGuarded()
        {
            // The front contract legitimately gaps — only interior rows have two neighbours to judge against.
            using var store = Store();
            var d = new[] { AsOf.AddDays(20), AsOf.AddDays(60), AsOf.AddDays(100) };
            Put(store, 1, AsOf, 1.000);
            Put(store, 2, AsOf, 2.000);
            Put(store, 3, AsOf, 2.010);

            var t = RollingStrip.Build("t", store, AsOf,
                new[] { ("a", d[0]), ("b", d[1]), ("c", d[2]) }, d, Tk);

            Assert.Equal(1.000, t.Rows[0].Mid!.Value, 10);
        }

        [Fact]
        public void LookbackOnADecisionDay_ReadsTheDayBefore_WithThePreRollIndex()
        {
            // A decision exactly 7 days before asOf: the 1w target IS the boundary. That day's
            // close is unattributable — the numbered families re-point non-uniformly during the
            // decision day (the dodgeball 16:30 probe) — so the strip reads the day BEFORE, from
            // the ticker that pointed at this contract pre-roll (index 2, not 1).
            using var store = Store();
            var boundary = AsOf.AddDays(-7);
            var thisOne = AsOf.AddDays(40);

            Put(store, 1, AsOf, 3.50);
            Put(store, 1, boundary, 9.99);               // decision-day chaos close — never read
            Put(store, 2, boundary, 9.99);
            Put(store, 2, boundary.AddDays(-1), 3.45);   // the honest pre-decision close

            var t = RollingStrip.Build("t", store, AsOf,
                new[] { ("m1", thisOne) }, new[] { boundary, thisOne }, Tk);

            Assert.Equal(3.45, t.Rows[0].WeekLevel!.Value, 10);
        }

        [Fact]
        public void WeekendLookbackOverAFridayDecision_StepsPastTheBoundaryClose()
        {
            // 1w target on a Sunday, decision the preceding Friday: the walk-back would land on
            // the Friday boundary close. It must recompute from the Thursday instead — and with
            // the shifted index, because before the roll this contract lived under ticker 2.
            using var store = Store();
            var asOf = new DateTime(2026, 8, 2);          // a Sunday
            var boundary = new DateTime(2026, 7, 24);     // the Friday decision
            var thisOne = asOf.AddDays(40);

            Put(store, 1, new DateTime(2026, 7, 31), 3.50);
            Put(store, 1, boundary, 9.99);                // the close the walk-back would hit
            Put(store, 2, boundary.AddDays(-1), 3.45);    // Thursday — the honest read

            var t = RollingStrip.Build("t", store, asOf,
                new[] { ("m1", thisOne) }, new[] { boundary, thisOne }, Tk);

            Assert.Equal(3.45, t.Rows[0].WeekLevel!.Value, 10);
        }

        [Fact]
        public void ForMeetings_AnnouncedDecision_RollsTheFrontOffTheStrip()
        {
            // The live RIKSBANK shape on the dashboards: store as-of 19-Aug, decision 20-Aug at
            // 08:30 London, decided period starts 26-Aug, next meeting 24-Sep → 30-Sep. Before
            // the statement the 26-Aug row fronts the strip; after it, the strip opens at 30-Sep —
            // and 30-Sep's mid must still read rung 2, the ticker that points at it AT asOf
            // (the store knows nothing of the intraday re-point).
            using var store = Store();
            var asOf = new DateTime(2026, 8, 19);
            Put(store, 1, asOf, 1.66);   // rung 1 @ asOf = the 26-Aug (decided-today) period
            Put(store, 2, asOf, 1.74);   // rung 2 @ asOf = the 30-Sep period

            var sched = new RateDesk.Core.MeetingScheduleDef
            {
                Name = "TESTRIKS", Ccy = "SEK", Header = "t",
                Tickers = new List<string> { "XX{N}" },
                Dates = new List<DateTime> { new(2026, 8, 26), new(2026, 9, 30), new(2026, 11, 11) },
                DecisionDates = new List<DateTime> { new(2026, 8, 20), new(2026, 9, 24) },
                DecisionTimeLondon = "08:30",
            };

            var before = RollingStrip.ForMeetings(sched, store, asOf,
                nowLondon: new DateTime(2026, 8, 20, 7, 0, 0));
            Assert.Equal(new DateTime(2026, 8, 26), before.Rows[0].Contract);
            Assert.Equal(1.66, before.Rows[0].Mid!.Value, 10);

            var after = RollingStrip.ForMeetings(sched, store, asOf,
                nowLondon: new DateTime(2026, 8, 20, 9, 0, 0));
            Assert.Equal(new DateTime(2026, 9, 30), after.Rows[0].Contract);
            Assert.Equal(1.74, after.Rows[0].Mid!.Value, 10);   // rung 2, not rung 1
        }

        [Fact]
        public void ForMeetings_MarksYearEndSpanningPeriods_AndPanelsLabelThem()
        {
            using var store = Store();
            int y = 2026;
            var asOf = new DateTime(y, 8, 19);
            Put(store, 1, asOf, 1.77);
            Put(store, 2, asOf, 1.47);   // the turn-dominated period
            Put(store, 3, asOf, 2.10);

            var sched = new RateDesk.Core.MeetingScheduleDef
            {
                Name = "TESTYE", Ccy = "SEK", Header = "t",
                Tickers = new List<string> { "XX{N}" },
                Dates = new List<DateTime>
                    { new(y, 9, 30), new(y, 12, 23), new(y + 1, 2, 10), new(y + 1, 3, 24) },
                MarkTurnPeriods = true,
            };
            var t = RollingStrip.ForMeetings(sched, store, asOf,
                nowLondon: new DateTime(y, 8, 19, 12, 0, 0));

            Assert.False(t.Rows[0].Turn);
            Assert.True(t.Rows[1].Turn);     // [23-Dec, 10-Feb) spans the year-end
            Assert.False(t.Rows[2].Turn);

            var pts = RateDesk.Weekly.Core.Render.Panels.From(t);
            Assert.Equal("Y/E Turn", pts[1].Flag);
            Assert.Null(pts[1].Now);         // stays off the chart — no y-scale distortion
            Assert.Null(pts[0].Flag);
        }

        [Fact]
        public void TrailingUnquotedRowsAreDropped_NotPublishedBlank()
        {
            using var store = Store();
            var d = new[] { AsOf.AddDays(20), AsOf.AddDays(60), AsOf.AddDays(100) };
            Put(store, 1, AsOf, 1.85);
            Put(store, 2, AsOf, 1.95);
            // ticker 3 never quotes — the family ends

            var t = RollingStrip.Build("t", store, AsOf,
                new[] { ("a", d[0]), ("b", d[1]), ("c", d[2]) }, d, Tk);

            Assert.Equal(2, t.Rows.Count);
        }
    }
}
