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
