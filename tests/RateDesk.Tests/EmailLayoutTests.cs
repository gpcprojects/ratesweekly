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
        public void ForwardGrid_HasSeparatorColumnsBetweenCurrencyGroups()
        {
            var html = WeeklyEmail.Html(Report(3));
            // Word IGNORES colgroup widths and sizes columns from cells — the separator width
            // must ride on the spacer CELL itself (css + width attribute), or the seam
            // collapses to nothing in the paste (the 2026-08-11 invisible-spacer saga).
            int seps = new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape("<td nowrap width=\"8\"")).Matches(html).Count;
            Assert.True(seps >= 2, $"expected >=2 width-carrying separator cells, saw {seps}");
            // and the air below each grid line is an exact-height IN-TABLE row (the cards'
            // padding mechanism) so bottom→title matches the CB cards to the pixel
            Assert.Contains("height=\"26\"", html);
        }

        [Fact]
        public void EveryCell_ForbidsWrapping_SoWordCannotDoubleRows()
        {
            // Word shrinks any table wider than the window; once a change cell drops below its
            // text width the value wraps and the whole row doubles (the DM line, 2026-08-11).
            // nowrap makes a too-wide table scroll instead of mangling.
            var html = WeeklyEmail.Html(Report(3));
            Assert.Contains("<td nowrap", html);
            Assert.DoesNotContain("<td style=", html);   // every Td-built cell carries the attribute
        }

        [Fact]
        public void Priced_CarriesNoHeat_AnywhereAnymore()
        {
            // the heat-on-Priced experiment was reverted (desk 2026-08-11): heat belongs to the
            // CHANGE columns only. W1/M1 left null here, so any heat fill could only have come
            // from a Priced cell — there must be none.
            var rep = new WeeklyReport();
            var run = new WeeklyRun { Title = "TEST · USD", RefPct = 4.0 };
            run.Rows.Add(new WeeklyMeeting { Date = new DateTime(2026, 9, 16), MidPct = 3.9, PricedBp = -8.0 });
            run.Rows.Add(new WeeklyMeeting { Date = new DateTime(2026, 10, 28), MidPct = 3.95, PricedBp = 8.0 });
            rep.Runs.Add(run);
            rep.Fronts.Add(new WeeklyFront
            {
                Bank = "TEST", Ccy = "USD",
                Decision = new DateTime(2026, 9, 16), StartDate = new DateTime(2026, 9, 16),
                MidPct = 3.9, RefPct = 4.0, PricedBp = -8.0,
            });

            var html = WeeklyEmail.Html(rep);
            Assert.DoesNotContain($"background:{WeeklyEmail.HeatHex(-8.0)}", html);
            Assert.DoesNotContain($"background:{WeeklyEmail.HeatHex(8.0)}", html);
        }

        [Fact]
        public void FrontTable_PricesTheStandardStep_AsAnUncappedPercentage()
        {
            var rep = new WeeklyReport();
            void Front(string bank, double priced) => rep.Fronts.Add(new WeeklyFront
            {
                Bank = bank, Ccy = "USD",
                Decision = new DateTime(2026, 9, 16), StartDate = new DateTime(2026, 9, 16),
                MidPct = 3.9, RefPct = 4.0, PricedBp = priced,
            });
            Front("HIKE20", 20.0);     // 20/25  -> +80%
            Front("CUT12H", -12.5);    //        -> -50%
            Front("BIG50", 50.0);      // past 100% is deliberate -> +200%
            Front("FLAT", 0.0);

            var html = WeeklyEmail.Html(rep);
            Assert.Contains("% 25bp", html);
            Assert.Contains("<b>+80%</b>", html);
            Assert.Contains("<b>-50%</b>", html);
            Assert.Contains("<b>+200%</b>", html);
            Assert.Contains("<b>0%</b>", html);
        }

        [Fact]
        public void MeetingCards_CarryA1dChangeColumn_WithHeat()
        {
            var rep = new WeeklyReport();
            var run = new WeeklyRun { Title = "TEST · USD", RefPct = 4.0 };
            run.Rows.Add(new WeeklyMeeting
            {
                Date = new DateTime(2026, 9, 16), MidPct = 3.9,
                D1Bp = 3.5, W1Bp = null, M1Bp = null,   // only 1d carries a value → any heat is 1d's
            });
            rep.Runs.Add(run);

            var html = WeeklyEmail.Html(rep);
            Assert.Contains("<b>1d Chg</b>", html);
            Assert.Contains($"background:{WeeklyEmail.HeatHex(3.5)}", html);

            var txt = WeeklyEmail.PlainText(rep);
            Assert.Contains("1d Chg\t1w Chg\t1m Chg", txt);
            Assert.Contains("+3.5", txt);
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
        public void EveryDataCell_PinsItsLineHeight_SoTablesShareOneRowHeight()
        {
            // Word chooses its own line spacing per table when line-height is unset — the CB
            // front rendered taller than the meeting cards from identical declared metrics.
            var rep = new WeeklyReport();
            rep.Fronts.Add(new WeeklyFront
            {
                Bank = "TEST", Ccy = "USD",
                Decision = new DateTime(2026, 9, 16), StartDate = new DateTime(2026, 9, 16),
                MidPct = 3.76,
            });
            var run = new WeeklyRun { Title = "TEST · USD" };
            run.Rows.Add(new WeeklyMeeting { Date = new DateTime(2026, 9, 16), MidPct = 3.9 });
            rep.Runs.Add(run);

            var html = WeeklyEmail.Html(rep);
            Assert.Contains("mso-line-height-rule:exactly;line-height:15px", html);
            Assert.DoesNotContain("padding:5px 8px", html);   // front header matches the cards' 4px
        }

    }
}
