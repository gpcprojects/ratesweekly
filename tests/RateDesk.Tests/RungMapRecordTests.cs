using System;
using System.Collections.Generic;
using RateDesk.Core;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>The boundary-day step-back must never throw away a recorded answer.
    ///
    /// THE LIVE FAULT (SKSF, 27-Aug-26, caught on the desk's own screen). The Riksbank family
    /// renumbers at the PERIOD START, which was 26-Aug. The store held SKSF1A on 26-Aug with
    /// SW_EFF_DT 30-Sep - Bloomberg stating plainly that the 30-Sep contract was rung 1 that day.
    /// RungFor stepped back off the boundary to 25-Aug BEFORE consulting the record, found nothing
    /// recorded there, fell through to the calendar count and answered rung 2. The front row's
    /// change-on-day then anchored on SKSF2A's 1.815 close instead of SKSF1A's 1.715 and published
    /// -10.6bp where the truth was -0.6bp.
    ///
    /// The step-back is right when a calendar is all we have - a boundary day's numbering is
    /// genuinely ambiguous then. It is wrong the moment the day itself is on the record.</summary>
    public class RungMapRecordTests
    {
        private static readonly DateTime Start = new(2026, 8, 26);   // the period start = the boundary
        private static readonly DateTime Next = new(2026, 9, 30);    // the contract in question
        private static readonly DateTime After = new(2026, 11, 11);

        private static MeetingScheduleDef Sked() => new()
        {
            Name = "TESTSEK",
            Ccy = "SEK",
            Header = "t",
            Tickers = new List<string> { "TSKSF{N}A" },
            RollsAtPeriodStart = true,
            Dates = new List<DateTime> { Start, Next, After, new(2026, 12, 23) },
        };

        /// <summary>The real SKSF shape on 26-Aug: rung 1 IS the 30-Sep contract, and the store
        /// says so. Nothing is recorded for 25-Aug, which is what made the step-back fatal.</summary>
        private static DateTime? Recorded(int rung, DateTime day)
        {
            if (day.Date != Start) return null;
            return rung switch { 1 => Next, 2 => After, _ => null };
        }

        [Fact]
        public void OnABoundaryDay_TheRecordWins_NotTheStepBack()
        {
            var map = new MeetingRungMap(Sked(), null, Recorded);
            Assert.Equal(1, map.RungFor(Next, Start));
            Assert.Equal(2, map.RungFor(After, Start));
        }

        [Fact]
        public void WithoutARecord_TheBoundaryDayStillStepsBack()
        {
            // the old behaviour is the FALLBACK, not the bug: with nothing recorded, a boundary
            // day's numbering is unknowable and reading it under the previous day's is the
            // honest choice
            var map = new MeetingRungMap(Sked());
            Assert.Equal(2, map.RungFor(Next, Start));
        }

        [Fact]
        public void ARecordOnAnOrdinaryDay_StillWins()
        {
            var map = new MeetingRungMap(Sked(), null,
                (rung, day) => day.Date == new DateTime(2026, 8, 20) && rung == 3 ? Next : null);
            Assert.Equal(3, map.RungFor(Next, new DateTime(2026, 8, 20)));
        }

        /// <summary>THE SECOND HALF OF THE SAME LIVE FAULT. Only SKSF rungs 0-3 carry date fields;
        /// 4A, 5A and 6A are price-only, so the contracts beyond the year-end turn are never on the
        /// record at all. Fixing the recorded rows left those still reading the calendar count with
        /// its boundary-day step-back, one rung too far: the 10-Feb row anchored on SKSF5A's 2.147
        /// instead of SKSF4A's 2.014 and printed -8.7bp against a truth of +4.6bp.
        ///
        /// The recorded rungs are evidence about the FAMILY, not just about themselves - if rung 1
        /// is the 30-Sep contract then the whole strip's numbering that day follows. Calibrate off
        /// them and the unrecorded contracts come out right too.</summary>
        [Fact]
        public void AnUnrecordedContract_IsNumberedOffTheRecordedOnes()
        {
            var sked = Sked();
            sked.Dates = new List<DateTime>
            {
                Start, Next, After, new(2026, 12, 23), new(2027, 2, 10), new(2027, 3, 31),
            };
            var map = new MeetingRungMap(sked, null, Recorded);
            // rungs 1 and 2 are recorded; 3, 4 and 5 are not, and must follow from them
            Assert.Equal(3, map.RungFor(new DateTime(2026, 12, 23), Start));
            Assert.Equal(4, map.RungFor(new DateTime(2027, 2, 10), Start));
            Assert.Equal(5, map.RungFor(new DateTime(2027, 3, 31), Start));
        }

        /// <summary>Calibration is only evidence while the recorded rungs agree. Mid-renumber a
        /// family can sit half-rolled - the ECB probe found exactly that - and two rungs implying
        /// different offsets is the signature. Abstain, and let the documented fallback answer.</summary>
        [Fact]
        public void RecordedRungsThatDisagree_AbstainRatherThanPickOne()
        {
            var sked = Sked();
            sked.Dates = new List<DateTime>
            {
                Start, Next, After, new(2026, 12, 23), new(2027, 2, 10),
            };
            // rung 1 says the family rolled (1 == 30-Sep); rung 2 says it did not (2 == 23-Dec,
            // which under rung 1's numbering would be rung 3). Offsets 0 and -1: no answer.
            var map = new MeetingRungMap(sked, null, (rung, day) =>
                day.Date != Start ? null
                : rung switch { 1 => Next, 2 => new DateTime(2026, 12, 23), _ => (DateTime?)null });
            // the step-back fallback: from 25-Aug, boundaries up to 10-Feb are
            // 26-Aug, 30-Sep, 11-Nov, 23-Dec, 10-Feb = 5
            Assert.Equal(5, map.RungFor(new DateTime(2027, 2, 10), Start));
        }

        [Fact]
        public void ARecordThatNamesNoRung_FallsThroughRatherThanGuessing()
        {
            // the day is recorded, but no rung carried this contract - the map must not invent one
            var map = new MeetingRungMap(Sked(), null,
                (rung, day) => day.Date == Start && rung == 1 ? After : null);
            // falls back to the step-back + calendar count, which is the documented behaviour
            Assert.Equal(2, map.RungFor(Next, Start));
        }
    }
}
