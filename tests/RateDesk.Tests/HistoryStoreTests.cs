using RateDesk.Core.Market;
using RateDesk.Weekly.Core;

namespace RateDesk.Tests
{
    public class HistoryStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-tests-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "history.db");

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* WAL handles release lazily */ }
        }

        private static HistPoint P(int daysAgo, double v) => new(DateTime.Today.AddDays(-daysAgo), v);

        [Fact]
        public void RoundTrip_AscendingWithinLookback()
        {
            using var store = new HistoryStore(DbPath);
            store.UpsertDaily("USOSFR5 Curncy", new[] { P(3, 3.51), P(1, 3.53), P(2, 3.52) });

            var h = store.GetDaily("USOSFR5 Curncy", 10);
            Assert.Equal(3, h.Count);
            Assert.True(h[0].Date < h[1].Date && h[1].Date < h[2].Date);
            Assert.Equal(3.53, h[^1].Value, 10);
        }

        [Fact]
        public void Lookback_CutsOldPoints()
        {
            using var store = new HistoryStore(DbPath);
            store.UpsertDaily("T", new[] { P(40, 1.0), P(5, 2.0) });
            Assert.Single(store.GetDaily("T", 10));
            Assert.Equal(2, store.GetDaily("T", 60).Count);
        }

        [Fact]
        public void Upsert_OverwritesRestatedClose_AndDedupes()
        {
            using var store = new HistoryStore(DbPath);
            store.UpsertDaily("T", new[] { P(2, 1.00) });
            store.UpsertDaily("T", new[] { P(2, 1.25) }); // restated print self-heals
            var h = store.GetDaily("T", 10);
            Assert.Single(h);
            Assert.Equal(1.25, h[0].Value, 10);
        }

        [Fact]
        public void Upsert_ExcludesToday_UnlessAsked()
        {
            using var store = new HistoryStore(DbPath);
            store.UpsertDaily("T", new[] { P(0, 9.9), P(1, 1.0) });
            Assert.Single(store.GetDaily("T", 10)); // intraday last is not a settled close

            store.UpsertDaily("T", new[] { P(0, 9.9) }, excludeToday: false);
            Assert.Equal(2, store.GetDaily("T", 10).Count);
        }

        [Fact]
        public void LastDate_NullWhenUnseeded_ElseMax()
        {
            using var store = new HistoryStore(DbPath);
            Assert.Null(store.LastDate("T"));
            store.UpsertDaily("T", new[] { P(9, 1.0), P(4, 1.1) });
            Assert.Equal(DateTime.Today.AddDays(-4), store.LastDate("T"));
        }

        [Fact]
        public void GetDaily_EmptyForUnknownTicker()
        {
            using var store = new HistoryStore(DbPath);
            Assert.Empty(store.GetDaily("NOPE Curncy", 100));
        }

        [Fact]
        public void ValueAsOf_WalksBackToLastAvailableClose()
        {
            using var store = new HistoryStore(DbPath);
            store.UpsertDaily("T", new[] { P(10, 1.0), P(3, 2.0) });

            Assert.Equal(2.0, store.ValueAsOf("T", DateTime.Today.AddDays(-1))!.Value, 10);
            Assert.Equal(2.0, store.ValueAsOf("T", DateTime.Today.AddDays(-3))!.Value, 10);
            // between the two stored closes → the older one, as the desk would read it
            Assert.Equal(1.0, store.ValueAsOf("T", DateTime.Today.AddDays(-5))!.Value, 10);
            // before any close → nothing, never an invented value
            Assert.Null(store.ValueAsOf("T", DateTime.Today.AddDays(-20)));
        }

        [Fact]
        public void SeededDepth_IsZeroUntilSet_AndIsMonotone()
        {
            using var store = new HistoryStore(DbPath);
            Assert.Equal(0, store.SeededDepth("T"));

            store.SetSeededDepth("T", 45);
            Assert.Equal(45, store.SeededDepth("T"));

            store.SetSeededDepth("T", 750);
            Assert.Equal(750, store.SeededDepth("T"));

            // a shallow maintain pass must never lower a deep seed's watermark, or the ticker
            // would be re-seeded at depth on every subsequent run
            store.SetSeededDepth("T", 45);
            Assert.Equal(750, store.SeededDepth("T"));
        }

        [Fact]
        public void SeededDepth_IsWhatDrivesDeepening_NotRowExistence()
        {
            // The regression this guards: bucketing on "has any row" made raising the seed depth a
            // silent no-op, because a shallow-seeded ticker looks identical to a deep one.
            using var store = new HistoryStore(DbPath);
            store.UpsertDaily("T", new[] { P(3, 1.0) });
            store.SetSeededDepth("T", 45);

            Assert.NotNull(store.LastDate("T"));          // has rows...
            Assert.True(store.SeededDepth("T") < 750);    // ...but is NOT deep enough
        }

        [Fact]
        public void TickersCoveringDate_CountsOnlyThoseWithACloseAtOrBefore()
        {
            using var store = new HistoryStore(DbPath);
            store.UpsertDaily("OLD", new[] { P(40, 1.0) });
            store.UpsertDaily("NEW", new[] { P(2, 1.0) });

            Assert.Equal(2, store.TickersCoveringDate(DateTime.Today));
            Assert.Equal(1, store.TickersCoveringDate(DateTime.Today.AddDays(-31)));
        }

        [Fact]
        public void SurvivesReopen()
        {
            using (var store = new HistoryStore(DbPath))
                store.UpsertDaily("T", new[] { P(3, 1.0) });
            using (var store = new HistoryStore(DbPath))
            {
                Assert.Equal(1, store.TickerCount());
                Assert.Single(store.GetDaily("T", 10));
            }
        }
    }
}
