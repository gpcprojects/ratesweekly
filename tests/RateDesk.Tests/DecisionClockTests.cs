using RateDesk.Core.Dates;

namespace RateDesk.Tests
{
    /// <summary>The announcement clock behind the decision-day front roll (desk 2026-08-20):
    /// a decision is ANNOUNCED from its configured London time onward, from the next day when no
    /// time is on file, and a decision pairs with the period it decides via the settlement-lag
    /// bound used everywhere else.</summary>
    public class DecisionClockTests
    {
        private static readonly DateTime Dec = new(2026, 8, 20);   // the live Riksbank case

        [Fact]
        public void BeforeTheTime_NotAnnounced()
            => Assert.False(DecisionClock.Announced(Dec, "08:30", new DateTime(2026, 8, 20, 8, 29, 0)));

        [Fact]
        public void AtTheTime_Announced()
            => Assert.True(DecisionClock.Announced(Dec, "08:30", new DateTime(2026, 8, 20, 8, 30, 0)));

        [Fact]
        public void LaterThatDay_Announced()
            => Assert.True(DecisionClock.Announced(Dec, "08:30", new DateTime(2026, 8, 20, 11, 30, 0)));

        [Fact]
        public void TheDayBefore_NotAnnounced_EvenAfterTheTime()
            => Assert.False(DecisionClock.Announced(Dec, "08:30", new DateTime(2026, 8, 19, 23, 0, 0)));

        [Fact]
        public void AnyLaterDay_Announced_RegardlessOfTime()
            => Assert.True(DecisionClock.Announced(Dec, "08:30", new DateTime(2026, 8, 21, 0, 1, 0)));

        [Fact]
        public void NoTimeOnFile_AnnouncedOnlyFromTheNextDay()
        {
            // unknown intraday state must not roll the front mid-day — that is the honest
            // pre-2026-08-20 behaviour, kept as the degradation path
            Assert.False(DecisionClock.Announced(Dec, "", new DateTime(2026, 8, 20, 23, 59, 0)));
            Assert.True(DecisionClock.Announced(Dec, "", new DateTime(2026, 8, 21, 0, 1, 0)));
        }

        [Fact]
        public void GarbageTime_BehavesLikeNoTime()
            => Assert.False(DecisionClock.Announced(Dec, "tbc", new DateTime(2026, 8, 20, 23, 0, 0)));

        [Fact]
        public void DecisionFor_PairsWithinTheSettlementLag()
        {
            var decisions = new[] { new DateTime(2026, 8, 20), new DateTime(2026, 9, 24) };
            // Riksbank: 20-Aug decision decides the period starting 26-Aug
            Assert.Equal(new DateTime(2026, 8, 20),
                DecisionClock.DecisionFor(decisions, new DateTime(2026, 8, 26)));
            // ...and 24-Sep decides 30-Sep — never 20-Aug, which is 41 days out
            Assert.Equal(new DateTime(2026, 9, 24),
                DecisionClock.DecisionFor(decisions, new DateTime(2026, 9, 30)));
        }

        [Fact]
        public void DecisionFor_NullBeyondTheLag_AndNeverAfterTheStart()
        {
            var decisions = new[] { new DateTime(2026, 8, 20) };
            Assert.Null(DecisionClock.DecisionFor(decisions, new DateTime(2026, 9, 30)));  // 41d — a different meeting
            Assert.Null(DecisionClock.DecisionFor(decisions, new DateTime(2026, 8, 15)));  // decision AFTER the start
            Assert.Null(DecisionClock.DecisionFor(Array.Empty<DateTime>(), new DateTime(2026, 8, 26)));
        }

        [Fact]
        public void DecisionFor_TakesTheLatestWithinTheLag()
        {
            // an emergency meeting inside the lag window: the later decision owns the period
            var decisions = new[] { new DateTime(2026, 8, 18), new DateTime(2026, 8, 21) };
            Assert.Equal(new DateTime(2026, 8, 21),
                DecisionClock.DecisionFor(decisions, new DateTime(2026, 8, 26)));
        }
    }
}
