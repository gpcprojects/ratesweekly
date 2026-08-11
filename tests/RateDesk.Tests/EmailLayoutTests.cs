using RateDesk.Core;

namespace RateDesk.Tests
{
    /// <summary>The email layout rules the desk set on 2026-08-11: each grid section renders as
    /// ONE table (no 6-currency chunking), 26px spacer columns sit between currency groups, the
    /// meeting cards carry 26px of vertical air, and the Priced column wears the heat ramp.</summary>
    public class EmailLayoutTests
    {
        private static WeeklyReport Report(int ccysInSection = 9)
        {
            var rep = new WeeklyReport();
            var sec = new WeeklySection { Title = "EM · LATAM" };
            for (int i = 0; i < ccysInSection; i++)
            {
                var c = new WeeklyCcy { Ccy = "C" + i.ToString("00") };
                c.Cells.Add(new WeeklyCell { Label = "1y", Mid = 3.5, W1Bp = 4.0, M1Bp = -1.0 });
                c.Cells.Add(new WeeklyCell { Label = "1y1y", Mid = 3.6, W1Bp = 0.5, M1Bp = 2.0 });
                sec.Ccys.Add(c);
            }
            rep.Sections.Add(sec);
            return rep;
        }

        [Fact]
        public void ForwardGrid_IsOneTablePerSection_NoChunking()
        {
            // nine currencies used to split into 6 + 3 "(cont.)" blocks
            var html = WeeklyEmail.Html(Report(9));
            Assert.DoesNotContain("(cont.)", html);
            Assert.Contains("EM · LATAM", html);
            // one header cell per currency, all in the single table
            for (int i = 0; i < 9; i++) Assert.Contains($">C{i:00}</td>", html);
        }

        [Fact]
        public void ForwardGrid_HasSpacerColumnsBetweenCurrencyGroups()
        {
            var html = WeeklyEmail.Html(Report(3));
            // 3 currencies => 2 spacer columns of the CB cards' 26px unit in the colgroup
            int spacers = html.Split("<col style=\"width:26px;\">").Length - 1;
            Assert.True(spacers >= 2, $"expected >=2 26px spacer columns, saw {spacers}");
        }

        [Fact]
        public void MeetingCards_PricedWearsTheHeatRamp()
        {
            var rep = new WeeklyReport();
            var run = new WeeklyRun { Title = "TEST · USD", RefPct = 4.0 };
            run.Rows.Add(new WeeklyMeeting { Date = new DateTime(2026, 9, 16), MidPct = 3.9, PricedBp = -8.0 });
            run.Rows.Add(new WeeklyMeeting { Date = new DateTime(2026, 10, 28), MidPct = 3.95, PricedBp = 8.0 });
            run.Rows.Add(new WeeklyMeeting { Date = new DateTime(2026, 12, 9), MidPct = 3.96, PricedBp = 0.5 });
            rep.Runs.Add(run);

            var html = WeeklyEmail.Html(rep);
            Assert.Contains($"background:{WeeklyEmail.HeatHex(-8.0)}", html);   // deep cut = red fill
            Assert.Contains($"background:{WeeklyEmail.HeatHex(8.0)}", html);    // deep hike = green fill
            Assert.Null(WeeklyEmail.HeatHex(0.5));                              // quiet rows stay unfilled
        }

        [Fact]
        public void MeetingCards_CarryTheSpacingUnitVertically()
        {
            var rep = new WeeklyReport();
            rep.Runs.Add(new WeeklyRun { Title = "TEST · USD" });
            var html = WeeklyEmail.Html(rep);
            Assert.Contains("padding:0 0 26px 0", html);
        }

        [Fact]
        public void SpacerCells_PinTheirOwnMetrics_SoRowsCannotInflate()
        {
            // Word gives an unstyled &nbsp; cell its Normal paragraph style and inflates EVERY
            // row in the table to match it — the 2026-08-11 pasted-grid bloat. Spacers must carry
            // 1px font metrics (the Sp() principle) so the data cells alone set the row height.
            var html = WeeklyEmail.Html(Report(3));
            Assert.Contains("font-size:1px;line-height:1px;border:none;", html);
            Assert.DoesNotContain("<td style=\"border:none;\">&nbsp;</td>", html);
        }

        [Fact]
        public void SectionTitle_IsACaptionAboveTheTable_NotACornerCell()
        {
            // compact rows would wrap "EM · LATAM" to three lines inside the 62px corner cell —
            // the caption-above-table pattern is the CB cards' own, one design language
            var html = WeeklyEmail.Html(Report(2));
            Assert.Contains(">EM · LATAM</div>", html);
        }

        [Fact]
        public void FrontTable_PricedWearsTheHeatRampToo()
        {
            var rep = new WeeklyReport();
            rep.Fronts.Add(new WeeklyFront
            {
                Bank = "TEST", Ccy = "USD",
                Decision = new DateTime(2026, 9, 16), StartDate = new DateTime(2026, 9, 16),
                MidPct = 3.76, RefPct = 4.0, PricedBp = -9.0,
            });
            var html = WeeklyEmail.Html(rep);
            Assert.Contains($"background:{WeeklyEmail.HeatHex(-9.0)}", html);
        }
    }
}
