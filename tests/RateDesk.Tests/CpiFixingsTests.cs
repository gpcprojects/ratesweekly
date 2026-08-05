using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Tests
{
    public class CpiFixingsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-cpi-" + Guid.NewGuid().ToString("N"));
        private HistoryStore Store() => new(Path.Combine(_dir, "h.db"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly DateTime AsOf = new(2026, 8, 4);
        private static readonly CpiFixings.Family Usd =
            new("USD", "USSWIF", "CPI", CpiFixings.FixUnit.IndexLevel, "CPURNSA Index");

        /// <summary>Seed the twelve calendar-month tickers as they really stand in Aug-2026: the
        /// next unfixed month is July, so USSWIF7..12 are 2026 and USSWIF1..6 are 2027. Maturity is
        /// always the reference month + 3.</summary>
        private static void SeedUsd(HistoryStore s, DateTime observed)
        {
            for (int m = 1; m <= 12; m++)
            {
                int year = m >= 7 ? 2026 : 2027;
                var refMonth = new DateTime(year, m, 1);
                var tk = $"USSWIF{m} Curncy";
                s.SetMaturity(tk, observed, refMonth.AddMonths(3));
                s.UpsertDaily(tk, new[] { new HistPoint(AsOf, 330 + m) }, excludeToday: false);
            }
        }

        [Fact]
        public void OrdersFromTheNextFixingToFix_NotFromJanuary()
        {
            using var store = Store();
            SeedUsd(store, AsOf);

            var lad = CpiFixings.Build(Usd, store, AsOf);

            Assert.Equal(12, lad.Rows.Count);
            Assert.Equal("Jul 26", lad.Rows[0].Label);     // next to print, not "Jan"
            Assert.Equal("Dec 26", lad.Rows[5].Label);
            Assert.Equal("Jun 27", lad.Rows[^1].Label);    // wraps into next year
        }

        [Fact]
        public void DerivesTheLagFromMaturity_NotAHardcodedConstant()
        {
            using var store = Store();
            // A 2-month market (GBP RPI): maturity is reference month + 2.
            var gbp = new CpiFixings.Family("GBP", "BPSWIF", "RPI", CpiFixings.FixUnit.YoYBp, "UKRPI Index");
            for (int m = 1; m <= 12; m++)
            {
                int year = m >= 7 ? 2026 : 2027;
                var tk = $"BPSWIF{m} Curncy";
                store.SetMaturity(tk, AsOf, new DateTime(year, m, 1).AddMonths(2));
                store.UpsertDaily(tk, new[] { new HistPoint(AsOf, 300 + m) }, excludeToday: false);
            }

            var lad = CpiFixings.Build(gbp, store, AsOf);

            Assert.Equal("Jul 26", lad.Rows[0].Label);
            Assert.Contains(lad.Notes, n => n.Contains("lag 2m"));
        }

        [Fact]
        public void YoyMarketsAreScaledFromBpToPercent()
        {
            using var store = Store();
            var gbp = new CpiFixings.Family("GBP", "BPSWIF", "RPI", CpiFixings.FixUnit.YoYBp, "UKRPI Index");
            store.SetMaturity("BPSWIF7 Curncy", AsOf, new DateTime(2026, 7, 1).AddMonths(2));
            store.UpsertDaily("BPSWIF7 Curncy", new[] { new HistPoint(AsOf, 328.5) }, excludeToday: false);

            var lad = CpiFixings.Build(gbp, store, AsOf);

            Assert.Equal(3.285, lad.Rows[0].Now!.Value, 6);   // 328.5bp read as a rate, not an index
        }

        [Fact]
        public void ARollInsideTheWindowSuppressesTheChange_RatherThanBookingAYearOfDrift()
        {
            // The ticker meant Jul-2026 a week ago and means Jul-2027 now: those are different
            // contracts and the old month is fixed and quoted by nobody, so there is no honest
            // comparison to draw.
            using var store = Store();
            var tk = "USSWIF7 Curncy";
            store.SetMaturity(tk, AsOf.AddDays(-10), new DateTime(2026, 7, 1).AddMonths(3));
            store.SetMaturity(tk, AsOf, new DateTime(2027, 7, 1).AddMonths(3));
            store.UpsertDaily(tk, new[]
            {
                new HistPoint(AsOf.AddDays(-7), 334.0),
                new HistPoint(AsOf, 341.5),
            }, excludeToday: false);

            var lad = CpiFixings.Build(Usd, store, AsOf);
            var row = lad.Rows.Single();

            Assert.NotNull(row.Now);
            Assert.Null(row.W1Bp);      // not +750bp of "weekly move"
            Assert.Contains(lad.Notes, n => n.Contains("rolled"));
        }

        [Fact]
        public void NoRoll_KeepsTheChange()
        {
            using var store = Store();
            var tk = "USSWIF7 Curncy";
            var mat = new DateTime(2026, 7, 1).AddMonths(3);
            store.SetMaturity(tk, AsOf.AddDays(-10), mat);
            store.SetMaturity(tk, AsOf, mat);
            store.UpsertDaily(tk, new[]
            {
                new HistPoint(AsOf.AddDays(-7), 334.0),
                new HistPoint(AsOf, 334.2),
            }, excludeToday: false);

            var row = CpiFixings.Build(Usd, store, AsOf).Rows.Single();

            Assert.Equal(20.0, row.W1Bp!.Value, 6);
        }

        [Fact]
        public void SanityNoteFires_WhenAQuoteContradictsTheDeclaredUnit()
        {
            using var store = Store();
            store.SetMaturity("USSWIF7 Curncy", AsOf, new DateTime(2026, 7, 1).AddMonths(3));
            store.UpsertDaily("USSWIF7 Curncy", new[] { new HistPoint(AsOf, 3.2) }, excludeToday: false);
            store.UpsertDaily("CPURNSA Index", new[] { new HistPoint(AsOf, 333.95) }, excludeToday: false);

            var lad = CpiFixings.Build(Usd, store, AsOf);
            var note = CpiFixings.SanityNote(Usd, store, AsOf, lad.Rows);

            Assert.NotNull(note);   // declared an index level, quote looks like a rate
        }
    }
}
