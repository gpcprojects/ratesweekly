using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Tests
{
    /// <summary>Calibration guards for the price-based roll detector. Both directions matter and
    /// both were observed for real in the store: the BPSWIF6 roll (295.75 → 420.25 on 2026-07-22,
    /// when June RPI printed and the ticker re-pointed a year forward) must be caught, and the
    /// 2026-07-22 repricing of the rest of the GBP fixing strip — a genuine 2-3% market move on
    /// the same day — must NOT be, or real changes get silently blanked.</summary>
    public class RollDetectTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-roll-" + Guid.NewGuid().ToString("N"));
        private HistoryStore Store() => new(Path.Combine(_dir, "h.db"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly DateTime AsOf = new(2026, 8, 4);

        private static void Seed(HistoryStore s, string tk, double[] values)
        {
            var pts = new List<HistPoint>();
            for (int i = 0; i < values.Length; i++)
                pts.Add(new HistPoint(AsOf.AddDays(-(values.Length - 1 - i)), values[i]));
            s.UpsertDaily(tk, pts, excludeToday: false);
        }

        [Fact]
        public void CatchesARealRoll()
        {
            // the shape of BPSWIF6: a quiet series that steps by a third of its own level
            using var store = Store();
            Seed(store, "T", new[]
            {
                295.75, 295.75, 295.25, 294.00, 293.00, 292.00, 290.50, 292.00, 291.00, 294.00, 295.50,
                420.25, 431.50, 416.00, 396.25, 387.25, 409.25, 395.00, 388.50,
            });

            Assert.True(RollDetect.LooksRolled(store, "T", AsOf.AddDays(-31), AsOf));
        }

        [Fact]
        public void LeavesABusyDataDayAlone()
        {
            // the shape of BPSWIF7 on the same date: a 2.3%-of-level repricing, ~10x its median
            // step. Real, and it must survive.
            using var store = Store();
            Seed(store, "T", new[]
            {
                319.00, 319.00, 320.00, 320.00, 320.50, 328.00, 330.00, 330.00, 330.00,
                330.00, 330.00, 330.00, 329.50, 328.50,
            });

            Assert.False(RollDetect.LooksRolled(store, "T", AsOf.AddDays(-31), AsOf));
        }

        [Fact]
        public void LeavesAnOrdinarySeriesAlone()
        {
            using var store = Store();
            Seed(store, "T", new[]
            {
                335.12, 335.39, 335.43, 335.51, 335.54, 335.65, 335.55, 335.34,
                335.22, 335.42, 335.23, 335.14, 334.95, 334.67,
            });

            Assert.False(RollDetect.LooksRolled(store, "T", AsOf.AddDays(-31), AsOf));
        }

        [Fact]
        public void TooLittleHistoryIsNotAnAccusation()
        {
            using var store = Store();
            Seed(store, "T", new[] { 100.0, 400.0 });

            Assert.False(RollDetect.LooksRolled(store, "T", AsOf.AddDays(-31), AsOf));
        }
    }
}
