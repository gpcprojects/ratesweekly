using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using RateDesk.Core;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Daily;
using Xunit;

namespace RateDesk.Tests
{
    /// <summary>SHEET STYLE (desk 2026-08-26) — the emails' default body is the ATTACHMENT'S OWN
    /// TABLE. These tests pin the two properties that matter: the inline content and the xls
    /// carry identical data, and the rendering obeys the Word/Outlook and mobile rules.</summary>
    public class SheetEmailTests
    {
        private static WeeklyReport Fixture()
        {
            var rep = new WeeklyReport { AsOf = new DateTime(2026, 8, 26, 16, 15, 0) };
            var ecb = new WeeklyRun { Title = "ECB · EUR", RefName = "ESTRON Index", RefPct = 2.189 };
            ecb.Rows.Add(new WeeklyMeeting
            {
                Date = new(2026, 9, 16), EndDate = new(2026, 11, 4), MidPct = 2.430,
                PricedBp = 24.1, StepBp = null, D1Bp = -0.1, W1Bp = 0.0, M1Bp = 1.8, MidSource = "ticker",
            });
            ecb.Rows.Add(new WeeklyMeeting
            {
                Date = new(2026, 11, 4), EndDate = new(2026, 12, 23), MidPct = 2.477,
                PricedBp = 28.8, StepBp = 4.7, D1Bp = -1.0, W1Bp = -1.2, M1Bp = -2.1, MidSource = "ticker",
            });
            rep.Runs.Add(ecb);
            var sek = new WeeklyRun { Title = "RIKSBANK · SEK", RefName = "SWESTR Index", RefPct = 1.641 };
            sek.Rows.Add(new WeeklyMeeting
            {
                Date = new(2026, 9, 30), EndDate = new(2026, 11, 11), MidPct = 1.715,
                PricedBp = 7.4, StepBp = 6.5, D1Bp = -0.2, W1Bp = -1.1, M1Bp = -6.4, MidSource = "ticker",
            });
            sek.Rows.Add(new WeeklyMeeting   // the Y/E turn label row
            {
                Date = new(2026, 12, 23), EndDate = new(2027, 2, 10), MidPct = 1.393,
                TurnPeriod = true, MidSource = "ticker",
            });
            rep.Runs.Add(sek);
            rep.Fronts.Add(new WeeklyFront
            {
                Bank = "ECB", Ccy = "EUR", Decision = new(2026, 9, 10), StartDate = new(2026, 9, 16),
                MidPct = 2.430, RefPct = 2.189, PricedBp = 24.1,
            });
            return rep;
        }

        /// <summary>Every number the xls Runs sheet displays appears in the email body, in the
        /// same string form — one RunsTable feeds both, so a drift is a compile-time impossibility
        /// and this test is the proof for the desk.</summary>
        [Fact]
        public void SheetBody_CarriesExactlyTheXlsData()
        {
            var rep = Fixture();
            var html = SheetEmail.Body(rep, front: true, runs: true).Replace('‑', '-');

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Runs");
            DailyBook.WriteRunsSheet(ws, rep);

            int checkedCells = 0;
            foreach (var cell in ws.CellsUsed())
            {
                string shown = cell.DataType switch
                {
                    XLDataType.Number => cell.GetDouble().ToString(
                        cell.Style.NumberFormat.Format == RunsTable.BpFmt
                            ? RunsTable.BpFmt : RunsTable.RateFmt,
                        System.Globalization.CultureInfo.InvariantCulture),
                    XLDataType.DateTime => RunsTable.DateText(cell.GetDateTime()),
                    _ => cell.GetString(),
                };
                if (shown.Length == 0) continue;
                // a header's UNITS drop to a quiet second line by design ("Priced" / "bp"), so
                // those are checked as stem + unit; every other string must appear verbatim
                // (allowing the &nbsp;-joined form Word requires)
                if (shown.EndsWith(" (bp)", StringComparison.Ordinal))
                {
                    Assert.Contains(shown[..^5].Replace(" ", "&nbsp;"), html);
                    Assert.Contains(">bp<", html);
                }
                else
                {
                    var needle = shown.Replace(" ", "&nbsp;");
                    Assert.True(html.Contains(shown) || html.Contains(needle),
                        $"xls cell {cell.Address} \"{shown}\" is missing from the email body");
                }
                checkedCells++;
            }
            Assert.True(checkedCells > 40, $"fixture too thin to prove anything ({checkedCells} cells)");
        }

        [Fact]
        public void SheetBody_IsStacked_NotTheWideCardGrid()
        {
            var html = SheetEmail.Body(Fixture(), true, true);
            // no 428px card columns and no 1168px section rule — the card grid measured ~1300px,
            // which phones scaled to about a third
            Assert.DoesNotContain("width:428px", html);
            Assert.DoesNotContain("width:1168px", html);
            // every table holds the single 488px measure (or narrower)
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(html, @"width:(\d+)px;max-width")
                         .Cast<System.Text.RegularExpressions.Match>())
                Assert.True(int.Parse(m.Groups[1].Value) <= 500,
                    $"a sheet table is {m.Groups[1].Value}px wide — too wide for a phone");
        }

        [Fact]
        public void SheetBody_ObeysTheWordRules()
        {
            var html = SheetEmail.Body(Fixture(), true, true);
            // widths on EVERY cell, as attribute AND style (Word ignores colgroup)
            int tds = System.Text.RegularExpressions.Regex.Matches(html, "<td").Count;
            int widthAttrs = System.Text.RegularExpressions.Regex.Matches(html, "<td[^>]*width=\"").Count;
            int colspans = System.Text.RegularExpressions.Regex.Matches(html, "<td[^>]*colspan=\"").Count;
            Assert.True(widthAttrs + colspans >= tds - 2,
                $"{tds - widthAttrs - colspans} cell(s) carry no width — Word will autosize them");
            // multi-word text nbsp-joined, negatives non-breaking, exact line heights
            Assert.Contains("closing&nbsp;run", html);
            Assert.Contains("mso-line-height-rule:exactly", html);
            Assert.Contains("‑", html);                       // U+2011 in the negative changes
            Assert.DoesNotContain(">-1.0<", html);            // never a breakable hyphen-minus
        }

        [Fact]
        public void SheetBody_CarriesTheResponsiveRules_AndDropsMaturityOnPhones()
        {
            var html = SheetEmail.Body(Fixture(), true, true);
            Assert.Contains("@media only screen and (max-width:620px)", html);
            Assert.Contains("@media only screen and (max-width:440px)", html);
            Assert.Contains(".rwm{display:none!important}", html);
            Assert.Contains("-webkit-text-size-adjust:100%", html);
            // the Maturity column (header + every data cell) is the one tagged for the drop
            int rwm = System.Text.RegularExpressions.Regex.Matches(html, "class=\"rw[ch] rwm\"|class=\"rwh rwm\"").Count;
            Assert.True(rwm >= 6, $"only {rwm} Maturity cells tagged rwm — the phone rule would tear the table");
        }

        [Fact]
        public void SheetBody_ConditionallyFormatsChangesOnly()
        {
            var rep = Fixture();
            rep.Runs[0].Rows.Clear();
            rep.Runs[0].Rows.Add(new WeeklyMeeting
            {
                Date = new(2026, 9, 16), EndDate = new(2026, 11, 4), MidPct = 2.430,
                PricedBp = 40.0,      // Priced must stay PLAIN (desk 2026-08-11: no heat there)
                StepBp = 4.7, D1Bp = -9.0,   // well past the 2bp ramp: must be painted
                MidSource = "ticker",
            });
            var html = SheetEmail.Body(rep, false, true);
            var red = WeeklyEmail.HeatHex(-9.0)!;
            Assert.Contains($"background:{red}", html);
            // the painted cell is the change, not Priced: the Priced text sits in a muted cell
            var pricedIdx = html.IndexOf("+40.0", StringComparison.Ordinal);
            Assert.True(pricedIdx > 0);
            var cellStart = html.LastIndexOf("<td", pricedIdx, StringComparison.Ordinal);
            Assert.DoesNotContain("background:#", html[cellStart..pricedIdx]);
        }

        [Fact]
        public void TurnRows_AreLabelled_NotPublishedAsNumbers()
        {
            var html = SheetEmail.Body(Fixture(), false, true);
            Assert.Contains("Y/E&nbsp;Turn", html);
            Assert.DoesNotContain("1.393", html);   // the turn-dominated print never renders
        }

        [Fact]
        public void Defaults_AreSheetOn_CardsOff()
        {
            var s = new EmailSettings();
            Assert.True(s.DailySheetStyle);
            Assert.True(s.WeeklySheetStyle);
            Assert.False(s.DailyCardStyle);
            Assert.False(s.WeeklyCardStyle);
        }

        [Fact]
        public void Defaults_SurviveAnOlderSettingsFile()
        {
            // an install written before 2026-08-26 has no style keys — it must come up on the
            // new default, not on "everything false"
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "rw-set-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, EmailSettings.FileName),
                    "{\"WeeklyFrontTable\":true,\"DailyOisRuns\":true}");
                var s = EmailSettings.Load(dir);
                Assert.True(s.DailySheetStyle);
                Assert.True(s.WeeklySheetStyle);
                Assert.False(s.DailyCardStyle);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void CardStyle_StillRendersUnchanged_WhenTickedBackOn()
        {
            // the legacy layout is preserved verbatim as an option
            var rep = Fixture();
            var cards = WeeklyEmail.Html(rep, partsOpt: WeeklyEmail.EmailParts.All);
            Assert.Contains("width:428px", cards);      // the 3-across card grid
            Assert.Contains("1d&nbsp;Chg", cards);
        }
    }
}
