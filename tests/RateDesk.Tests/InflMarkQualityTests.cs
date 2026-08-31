using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Infl;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>THE INFLATION MARK-QUALITY TESTS (2026-08-31).
    ///
    /// Two bad marks reached the desk a working day apart and BOTH were found by a human reading
    /// the sheet, because nothing here covered the inflation path at all — the 64-scenario suite
    /// never references InflHistory, so every OIS change gets 64 scenarios and every inflation
    /// change got nothing. These are the regressions for both, and for the detector that should
    /// have caught them.
    ///
    ///   · Mar-27 (BPSWIF3) 27-Aug-26 — the CLOSE was 415.000 while the tenor traded 429-435 all
    ///     day. A bad tick printed twice and the second landed on the last bar, so it became the
    ///     close. The 16:15 snap was 435.500 and right.
    ///   · Nov-26 (BPSWIF11) 28-Aug-26 — the SNAP was 434.250 while the close was 439.375.
    ///     Neither was wrong: the last trade before 16:15 really was 434.250, the tenor jumped AT
    ///     16:15 and held 439.375 for five hours.
    ///
    /// They point OPPOSITE ways, which is the whole point: any fixed preference for one source is
    /// wrong about half the time, so the strip has to arbitrate per tenor per day.</summary>
    public class InflMarkQualityTests
    {
        private static readonly DateTime D1 = new(2026, 8, 27);   // the anchor day
        private static readonly DateTime D0 = new(2026, 8, 26);   // the day before it

        /// <summary>A strip of twelve tenors that all moved +2bp, except the one under test.
        /// Closes are the base series; the caller decides what the snap disagrees by.</summary>
        private sealed class Bars : IHistoryProvider
        {
            public Dictionary<string, Dictionary<DateTime, double>> Snaps = new();
            public IReadOnlyList<HistPoint> GetDaily(string ticker, int lookbackDays)
                => Array.Empty<HistPoint>();
            public void Prefetch(IEnumerable<string> tickers, int lookbackDays) { }
            public IReadOnlyList<HistPoint> GetLondonSnaps(string ticker, int days, TimeSpan tod)
                => Snaps.TryGetValue(ticker, out var d)
                    ? d.Select(kv => new HistPoint(kv.Key, kv.Value)).ToList()
                    : (IReadOnlyList<HistPoint>)Array.Empty<HistPoint>();
        }

        private static (HistoryStore store, Bars bars, string dir) Strip(
            double closeOnD1, double snapOnD1, int tenorUnderTest)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-infl-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            var store = new HistoryStore(System.IO.Path.Combine(dir, "s.db"));
            var bars = new Bars();
            for (int m = 1; m <= 12; m++)
            {
                var tk = $"BPSWIF{m} Curncy";
                double d0 = 400.0 + m, d1c = m == tenorUnderTest ? closeOnD1 : d0 + 2.0;
                store.UpsertDaily(tk, new[] { new HistPoint(D0, d0), new HistPoint(D1, d1c) },
                    excludeToday: false);
                // RefMonth walks BACK from maturity to find the fixing month, so the maturity
                // has to sit AFTER it - three months, the shape of the real contract
                var mat = new DateTime(2027, m, 15).AddMonths(3);
                store.SetMaturity(tk, D0, mat);
                store.SetMaturity(tk, D1, mat);
                bars.Snaps[tk] = new Dictionary<DateTime, double>
                {
                    [D0] = d0,
                    [D1] = m == tenorUnderTest ? snapOnD1 : d0 + 2.0,
                };
            }
            return (store, bars, dir);
        }

        private static double? MarkOn(HistoryStore store, int tenor, DateTime day)
        {
            foreach (var r in store.GetFixingHistory("RPI"))
                if (r.Date.Date == day.Date && r.Fix.EndsWith($"-{tenor:00}")) return r.Value;
            return null;
        }

        /// <summary>Mar-27's shape: the CLOSE is the outlier, so the snap must win.</summary>
        [Fact]
        public void ABadClose_LosesToTheSnap()
        {
            var (store, bars, dir) = Strip(closeOnD1: 380.0, snapOnD1: 405.0, tenorUnderTest: 3);
            try
            {
                InflHistory.Maintain(store, null, 45, bars);
                // strip moved +2; the close implies -23, the snap +2 -> the snap is believed
                Assert.Equal(405.0, MarkOn(store, 3, D1) ?? double.NaN, 3);
            }
            finally { store.Dispose(); try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Nov-26's shape: the SNAP is the outlier, so the close must win. This is the
        /// case that switching wholesale onto snaps got wrong.</summary>
        [Fact]
        public void ABadSnap_LosesToTheClose()
        {
            var (store, bars, dir) = Strip(closeOnD1: 413.0, snapOnD1: 390.0, tenorUnderTest: 11);
            try
            {
                InflHistory.Maintain(store, null, 45, bars);
                // strip moved +2; the close implies +2, the snap -21 -> the close is believed
                Assert.Equal(413.0, MarkOn(store, 11, D1) ?? double.NaN, 3);
            }
            finally { store.Dispose(); try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>The control. When the WHOLE strip moves — an Ofgem cap reset, a CPI print —
        /// nothing is a lone mover and nothing may be second-guessed. A detector that "fixes" a
        /// real repricing is worse than none.</summary>
        [Fact]
        public void AWholeStripMove_IsLeftAlone()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-infl-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            using var store = new HistoryStore(System.IO.Path.Combine(dir, "s.db"));
            var bars = new Bars();
            try
            {
                for (int m = 1; m <= 12; m++)
                {
                    var tk = $"BPSWIF{m} Curncy";
                    double d0 = 400.0 + m;
                    store.UpsertDaily(tk, new[] { new HistPoint(D0, d0), new HistPoint(D1, d0 - 12.0) },
                        excludeToday: false);
                    var mat = new DateTime(2027, m, 15).AddMonths(3);
                    store.SetMaturity(tk, D0, mat);
                    store.SetMaturity(tk, D1, mat);
                    bars.Snaps[tk] = new Dictionary<DateTime, double>
                        { [D0] = d0, [D1] = d0 - 12.0 };
                }
                InflHistory.Maintain(store, null, 45, bars);
                for (int m = 1; m <= 12; m++)
                    Assert.Equal(400.0 + m - 12.0, MarkOn(store, m, D1) ?? double.NaN, 3);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>With no bars at all — no terminal, or a rung with no intraday — the closes
        /// still serve and nothing is dropped. Maintain must degrade, not fail.</summary>
        [Fact]
        public void WithNoSnapsAtAll_TheClosesStillServe()
        {
            var (store, _, dir) = Strip(closeOnD1: 403.0, snapOnD1: 999.0, tenorUnderTest: 3);
            try
            {
                InflHistory.Maintain(store, null, 45, bars: null);
                Assert.Equal(403.0, MarkOn(store, 3, D1) ?? double.NaN, 3);
            }
            finally { store.Dispose(); try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>12bp in the family's own unit, so the same cut applies whether the family
        /// quotes YoY bp (RPI/HICP, base ~410) or an index level (CPI, base ~335).</summary>
        [Fact]
        public void TheLoneMoverCut_IsTwelveBpOfTheBaseIndex()
        {
            Assert.Equal(12.0, InflHistory.LoneMoverBp, 6);
            Assert.Equal(0.4932, InflHistory.LoneMoverBp / 10000.0 * 411.0, 4);   // RPI Mar-27
            Assert.Equal(0.4020, InflHistory.LoneMoverBp / 10000.0 * 335.0, 4);   // CPI
        }
    }
}
