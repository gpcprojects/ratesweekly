using System;
using System.Linq;
using RateDesk.Core;
using RateDesk.Core.Market;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>Regressions for the 2026-08-26 audit fixes in the roll-corrected history walk:
    /// boundary days publish BLANK Δ1d (the old walk resolved both sides to the same
    /// pre-boundary close and published 0.0 — "unanchorable" dressed as "unchanged"), Y/E-turn
    /// rows never enter the history tables, and the window is a true business-day count.</summary>
    public class BankHistoryRowsTests
    {
        [Fact]
        public void BoundaryDay_PublishesBlankD1_NotZero()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-bhr-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                using var store = new RateDesk.Weekly.Core.HistoryStore(
                    System.IO.Path.Combine(dir, "s.db"));
                // SKSF2A closes around the 26-Aug-26 period-start boundary
                store.UpsertDaily("SKSF2A Curncy", new[]
                {
                    new HistPoint(new(2026, 8, 21), 1.720),
                    new HistPoint(new(2026, 8, 24), 1.725),
                    new HistPoint(new(2026, 8, 25), 1.730),
                }, excludeToday: false);
                var sched = MeetingsStore.Schedules.First(s => s.Name == "RIKSBANK");
                var run = new WeeklyRun { Title = "RIKSBANK · SEK" };
                run.Rows.Add(new WeeklyMeeting
                    { Date = new(2026, 9, 30), MidPct = 1.73, EndDate = new(2026, 11, 11) });
                // a Y/E-turn row rides along and must NOT appear in the history
                run.Rows.Add(new WeeklyMeeting
                    { Date = new(2026, 12, 23), MidPct = 1.40, EndDate = new(2027, 2, 10), TurnPeriod = true });

                var rows = RateDesk.Weekly.Core.Daily.DailyBook.BankHistoryRows(
                    store, sched, run, "SKSF{N}A", new DateTime(2026, 8, 27), 4);

                var boundary = rows.First(x => x.Day == new DateTime(2026, 8, 26)
                                               && x.Start == new DateTime(2026, 9, 30));
                Assert.Null(boundary.D1);              // NOT 0.0 — the old fault
                Assert.Equal(1.730, boundary.Rate, 6); // walk-back to the 25-Aug close still fills

                var clean = rows.First(x => x.Day == new DateTime(2026, 8, 25)
                                            && x.Start == new DateTime(2026, 9, 30));
                Assert.NotNull(clean.D1);              // ordinary day differences normally
                Assert.Equal(0.5, clean.D1!.Value, 4); // 1.730 - 1.725

                Assert.DoesNotContain(rows, x => x.Start == new DateTime(2026, 12, 23));  // turn row
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Window_IsABusinessDayCount_EndingBeforeAsOf()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-bhr2-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                using var store = new RateDesk.Weekly.Core.HistoryStore(
                    System.IO.Path.Combine(dir, "s.db"));
                var pts = new System.Collections.Generic.List<HistPoint>();
                for (var d = new DateTime(2026, 7, 1); d <= new DateTime(2026, 8, 25); d = d.AddDays(1))
                    if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                        pts.Add(new HistPoint(d, 1.72));
                store.UpsertDaily("SKSF2A Curncy", pts, excludeToday: false);
                var sched = MeetingsStore.Schedules.First(s => s.Name == "RIKSBANK");
                var run = new WeeklyRun { Title = "RIKSBANK · SEK" };
                run.Rows.Add(new WeeklyMeeting
                    { Date = new(2026, 9, 30), MidPct = 1.72, EndDate = new(2026, 11, 11) });

                var rows = RateDesk.Weekly.Core.Daily.DailyBook.BankHistoryRows(
                    store, sched, run, "SKSF{N}A", new DateTime(2026, 8, 25), 10);

                var days = rows.Select(r => r.Day).Distinct().OrderBy(d => d).ToList();
                Assert.Equal(10, days.Count);                          // a true count, not ~70%
                Assert.True(days[^1] < new DateTime(2026, 8, 25));     // strictly before asOf
                Assert.All(days, d =>
                    Assert.True(d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }
    }
}
