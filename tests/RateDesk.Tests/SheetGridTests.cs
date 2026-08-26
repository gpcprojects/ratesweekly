using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using RateDesk.Core;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Daily;
using RateDesk.Weekly.Core.Infl;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>THE GRID (desk 2026-08-26: "all the columns are in line and EXACTLY THE SAME
    /// ACROSS THE BOARD"). The bank blocks used to be separate tables, so Word sized each one
    /// from its own content and the columns did not line up between banks. Now every section is
    /// ONE table on ONE grid — 8 columns of 78px, 624px, front table included (it carries an
    /// eighth blank column so its boundaries match). These tests fail if that ever drifts.</summary>
    public class SheetGridTests
    {
        private static WeeklyReport Report()
        {
            var rep = new WeeklyReport { AsOf = new DateTime(2026, 8, 26, 16, 15, 0) };
            foreach (var (bank, ccy) in new[] { ("ECB", "EUR"), ("MPC", "GBP"), ("RIKSBANK", "SEK") })
            {
                var run = new WeeklyRun { Title = $"{bank} · {ccy}", RefPct = 2.189 };
                run.Rows.Add(new WeeklyMeeting
                {
                    Date = new(2026, 9, 16), EndDate = new(2026, 11, 4), MidPct = 2.430,
                    PricedBp = 24.1, StepBp = 4.7, D1Bp = -0.1, W1Bp = -12.5, M1Bp = 1.8,
                    MidSource = "ticker",
                });
                run.Rows.Add(new WeeklyMeeting
                {
                    Date = new(2026, 12, 23), EndDate = new(2027, 2, 10), MidPct = 1.393,
                    TurnPeriod = true, MidSource = "ticker",
                });
                rep.Runs.Add(run);
            }
            rep.Fronts.Add(new WeeklyFront
            {
                Bank = "RIKSBANK", Ccy = "SEK", Decision = new(2026, 9, 24),
                StartDate = new(2026, 9, 30), MidPct = 1.715, RefPct = 1.643, PricedBp = 7.2,
            });
            return rep;
        }

        private static List<InflHistory.DisplayRow> InflRows() => new()
        {
            new(new DateTime(2026, 8, 1), 323.98, 334.94, 3.38, 0.31, -0.06, -0.06, -0.42),
            new(new DateTime(2026, 9, 1), 324.80, 335.76, 3.37, 0.24, -0.12, -0.29, -0.10),
            new(new DateTime(2026, 10, 1), 325.60, 335.54, 3.05, -0.07, -0.14, -0.28, -0.19),
        };

        public static IEnumerable<object[]> Fragments()
        {
            yield return new object[] { "body", SheetEmail.Body(Report(), true, true), 2 };
            yield return new object[]
            {
                "inflation",
                SheetEmail.InflHtml(new Dictionary<string, List<InflHistory.DisplayRow>>
                    { ["CPI"] = InflRows(), ["RPI"] = InflRows() }, null, new DateTime(2026, 8, 26)),
                1,
            };
        }

        [Theory]
        [MemberData(nameof(Fragments))]
        public void EveryColumnIsTheSameWidth_AndEverySectionIsOneTable(
            string name, string html, int expectedTables)
        {
            var widths = Regex.Matches(html, @"<td[^>]*width=""(\d+)""")
                .Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
            Assert.True(widths.Count == 1 && widths[0] == 78,
                $"{name}: columns are not a uniform 78px — found {string.Join(",", widths)}");

            var measures = Regex.Matches(html, @"width:(\d+)px;max-width")
                .Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
            Assert.True(measures.Count == 1 && measures[0] == 624,
                $"{name}: tables do not share the 624px measure — {string.Join(",", measures)}");

            // one table per section: a bank block can no longer be sized on its own content
            Assert.Equal(expectedTables, Regex.Matches(html, "<table").Count);

            // and every row spans exactly the eight columns (plain cells + colspans)
            foreach (var row in Regex.Matches(html, "<tr>(.*?)</tr>", RegexOptions.Singleline)
                         .Select(m => m.Groups[1].Value))
            {
                int cells = Regex.Matches(row, "<td").Count;
                int withSpan = Regex.Matches(row, "colspan=").Count;
                int spanned = Regex.Matches(row, @"colspan=""(\d+)""")
                    .Sum(m => int.Parse(m.Groups[1].Value));
                Assert.Equal(8, cells - withSpan + spanned);
            }
        }

        [Fact]
        public void TheInflationTableIsTheSameWidthAsTheOisTable()
        {
            // desk 2026-08-26: "widen the inflation tables to be the same total width as the ois"
            int Measure(string h) => Regex.Matches(h, @"width:(\d+)px;max-width")
                .Select(m => int.Parse(m.Groups[1].Value)).Distinct().Single();
            Assert.Equal(
                Measure(SheetEmail.Body(Report(), true, true)),
                Measure(SheetEmail.InflHtml(new Dictionary<string, List<InflHistory.DisplayRow>>
                    { ["CPI"] = InflRows() }, null, new DateTime(2026, 8, 26))));
        }

        [Fact]
        public void TheSheetsCarryTheConditionalFormattingToo()
        {
            // desk 2026-08-26: heat now lives on BOTH surfaces, change columns only
            var rep = Report();
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Runs");
            DailyBook.WriteRunsSheet(ws, rep);
            var hdr = ws.CellsUsed().First(c => c.GetString() == "StartDate").Address.RowNumber;
            var row = hdr + 1;
            // -12.5bp in the Δ1w column is well past the 2bp ramp: it must be painted, and with
            // the SAME colour the email paints for the same number
            var w1 = ws.Cell(row, 7);
            Assert.Equal(-12.5, w1.GetDouble(), 6);
            Assert.Equal(XLColor.FromHtml(WeeklyEmail.HeatHex(-12.5)!).Color.ToArgb(),
                w1.Style.Fill.BackgroundColor.Color.ToArgb());
            // ...and Priced/Step stay plain (desk 2026-08-11: heat belongs to the changes only).
            // Compared against the StartDate cell, which is unpainted by construction — an
            // unfilled ClosedXML cell reports indexed colour 64, not XLColor.NoColor.
            var unpainted = ws.Cell(row, 1).Style.Fill.BackgroundColor;
            Assert.Equal(unpainted, ws.Cell(row, 4).Style.Fill.BackgroundColor);
            Assert.Equal(unpainted, ws.Cell(row, 5).Style.Fill.BackgroundColor);
            Assert.NotEqual(unpainted, w1.Style.Fill.BackgroundColor);
        }
    }
}
