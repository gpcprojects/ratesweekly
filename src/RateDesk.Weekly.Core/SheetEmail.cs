using System.Text;
using RateDesk.Core;
using RateDesk.Weekly.Core.Daily;
using RateDesk.Weekly.Core.Infl;

namespace RateDesk.Weekly.Core
{
    /// <summary>SHEET STYLE — the emails' default body (desk 2026-08-26). The inline content IS
    /// the attachment: "beyond this i barely want to be able to tell the difference between what's
    /// in the email and what's in the xls. Only differences should be grid lines and conditional
    /// formatting exists on the email but not on the xls."
    ///
    /// So this is a FACSIMILE of the workbook, not a design of its own:
    ///   · the same title row, the same per-block "{BANK} closing run" / "{fixing} fixing" lines,
    ///     the same one-line column headers, the same blank row between blocks;
    ///   · the same COLUMN MEASURE — Excel's own widths converted at 7px/char + 5px padding, so
    ///     the columns line up with the sheet a reader has open beside it;
    ///   · Excel's own alignment: header text left, every number AND date right (Excel right-aligns
    ///     dates because they are numbers), black ink on white, nothing muted, no zebra;
    ///   · the DRAX-blue header band that the sheet now carries (RunsTable.BrandBlue);
    ///   · NO grid: the sheet is gridded, the email is not — that is difference one;
    ///   · conditional formatting on the three change columns — difference two, and the only ink
    ///     the sheet does not have (the monitor's green-up / red-down ramp, nothing under 2bp).
    ///
    /// WORD/OUTLOOK rules still hold underneath (they change nothing visible): every width lives
    /// ON the cell as attribute AND style, multi-word text is &amp;nbsp;-joined, negatives use
    /// U+2011, each block is ONE table, line-heights are pinned. MOBILE: a media-query block
    /// shrinks type and padding under 700px and drops the Maturity column under 470px; Word
    /// ignores it, so desktop Outlook shows the full sheet.</summary>
    public static class SheetEmail
    {
        private const string Font = WeeklyEmail.EmFont;
        private const string Ink = "#000000";                 // Excel's own ink
        private const string Blue = RunsTable.BrandBlue;
        private const int TitlePad = 1;

        // COLUMN WIDTHS (desk 2026-08-26, "pull all the numbers a bit closer together"): each
        // column is exactly as wide as its widest CONTENT at 11px — which is always the one-line
        // header, never a number — plus the 8px of padding. Excel's own 10/12-character columns
        // left 30-40px of dead air per column; these close it up while keeping every header on
        // one line and every number un-wrapped. 488px total for the runs table.
        // UNIFORM widths, tight gaps (desk 2026-08-26: "all the columns need to be the same
        // width but with smaller space between them otherwise it looks messy"). One width per
        // table = its widest header at 11pt bold + 6px, so nothing wraps and nothing clips:
        // runs "Priced (bp)" ~72px, fixings "Base Index" ~66px, front "Central Bank" ~78px.
        // 3px side padding keeps the columns visually close now everything is left-justified.
        /// <summary>ONE column measure for the ENTIRE email (desk 2026-08-26: "all the columns
        /// are in line and EXACTLY THE SAME ACROSS THE BOARD"): 8 columns of 78px = 624px, used
        /// by the OIS runs, the inflation runs and the front table alike. The front table has
        /// seven labelled columns and carries an eighth blank one so its grid still lines up
        /// with the others, column boundary for column boundary.</summary>
        private const int ColW = 78, Cols = 8, Measure = 624;
        private static readonly int[] RunW = Enumerable.Repeat(ColW, Cols).ToArray();
        private static readonly int[] InflW = Enumerable.Repeat(ColW, Cols).ToArray();
        private static readonly int[] FrontW = Enumerable.Repeat(ColW, Cols).ToArray();

        private static readonly System.Globalization.CultureInfo Inv =
            System.Globalization.CultureInfo.InvariantCulture;

        private static int Sum(int[] w) => w.Sum();

        /// <summary>The responsive rules — emitted at the top of any sheet-style fragment.</summary>
        public const string Style =
            "<style type=\"text/css\">" +
            "@media only screen and (max-width:700px){" +
            ".rwc{font-size:10pt!important;padding:1px 2px!important}" +
            ".rwh{font-size:10pt!important;padding:1px 2px!important}}" +
            "@media only screen and (max-width:520px){" +
            ".rwm{display:none!important}" +
            ".rwc{font-size:9pt!important;padding:1px 2px!important}" +
            ".rwh{font-size:9pt!important;padding:1px 2px!important}}" +
            "</style>";

        private static string Nb(string s) => s.Replace(" ", "&nbsp;");
        private static string NoBrk(string s) => s.Replace("-", "‑");

        /// <summary>One sheet cell. 11px Calibri, the sheet's own row rhythm, no borders.
        /// <paramref name="right"/> is IGNORED since desk 2026-08-26 — "everything EVERYTHING
        /// needs to be left justified", on both surfaces (the sheet sets Left explicitly too,
        /// since Excel would otherwise right-align its numbers and dates). The parameter stays
        /// so the call sites keep documenting which columns are numeric.</summary>
        private static string Cell(string inner, int w, bool right, string extra = "",
            string cls = "rwc") =>
            $"<td nowrap width=\"{w}\" class=\"{cls}\" style=\"{Font}width:{w}px;padding:1px 3px;" +
            $"font-size:11pt;color:{Ink};white-space:nowrap;mso-line-height-rule:exactly;" +
            $"line-height:15pt;text-align:left;{extra}\">{inner}</td>";

        /// <summary>Header cell: the sheet's own label, bold, LEFT-aligned as Excel leaves text,
        /// on the DRAX-blue band.</summary>
        private static string Head(string label, int w, string cls = "rwh") =>
            $"<td nowrap width=\"{w}\" class=\"{cls}\" style=\"{Font}width:{w}px;padding:1px 3px;" +
            $"font-size:11pt;font-weight:bold;color:{Ink};background:{Blue};white-space:nowrap;" +
            $"mso-line-height-rule:exactly;line-height:15pt;text-align:left;\">{Nb(label)}</td>";

        /// <summary>A change cell — the ONE thing the sheet does not have: the monitor's ramp,
        /// green for higher yield, red for lower, nothing under 2bp.</summary>
        private static string ChangeCell(double? v, int w, double scale = 1.0)
        {
            if (v is not double d) return Cell("&nbsp;", w, true);
            string bg = WeeklyEmail.HeatHex(d * scale) is string h ? $"background:{h};" : "";
            return Cell(NoBrk(RunsTable.BpText(d)), w, true, bg);
        }

        private static string TableOpen(int width) =>
            $"<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;" +
            $"table-layout:fixed;width:{width}px;max-width:100%;margin:0;mso-table-lspace:0pt;" +
            "mso-table-rspace:0pt;\">";

        /// <summary>The sheet's own title row (bold, top-left) — a ROW, not a table of its own,
        /// so it shares the section's single column grid.</summary>
        private static string TitleRow(string title, int cols) =>
            $"<tr><td colspan=\"{cols}\" nowrap style=\"{Font}font-size:11pt;font-weight:bold;" +
            $"color:{Ink};padding:1px {TitlePad}px 1px {TitlePad}px;mso-line-height-rule:exactly;" +
            $"line-height:15pt;\">{Nb(title)}</td></tr>";

        private static string BlankRow(int cols) =>
            $"<tr><td colspan=\"{cols}\" height=\"20\" style=\"font-size:1px;line-height:20px;\">" +
            "&nbsp;</td></tr>";

        // ---- body ----

        /// <summary>The sheet-style body: the CB front table (when ticked — it has no sheet
        /// counterpart, so it borrows the same language) and the workbook's own run blocks.</summary>
        public static string Body(WeeklyReport rep, bool front, bool runs)
        {
            var was = System.Globalization.CultureInfo.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = Inv;
            try
            {
                var sb = new StringBuilder();
                sb.Append(Style);
                sb.Append($"<div style=\"{Font}color:{Ink};font-size:11pt;-webkit-text-size-adjust:100%;\">");
                if (front && rep.Fronts.Count > 0) sb.Append(FrontTable(rep));
                if (runs) sb.Append(RunTables(rep));
                sb.Append("</div>");
                return sb.ToString();
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
        }

        private static string FrontTable(WeeklyReport rep)
        {
            var sb = new StringBuilder();
            sb.Append(TableOpen(Measure));
            sb.Append(TitleRow("CB Front Meeting Market Pricing", FrontW.Length)
                + BlankRow(FrontW.Length));
            sb.Append("<tr>" + Head("Central Bank", FrontW[0]) + Head("Decision", FrontW[1])
                + Head("Start", FrontW[2]) + Head("OIS Mid", FrontW[3]) + Head("Fixing", FrontW[4])
                + Head("Priced (bp)", FrontW[5]) + Head("% 25bp", FrontW[6])
                + Head("", FrontW[7]) + "</tr>");
            bool anyStartOnly = false, anyRebased = false;
            foreach (var f in rep.Fronts)
            {
                anyStartOnly |= f.Decision == null;
                anyRebased |= f.RefRebased;
                sb.Append("<tr>"
                    + Cell($"{f.Bank}&nbsp;{f.Ccy}", FrontW[0], false)
                    + Cell(NoBrk(f.Decision is { } dd ? RunsTable.DateText(dd)
                        : RunsTable.DateText(f.StartDate) + "*"), FrontW[1], true)
                    + Cell(NoBrk(RunsTable.DateText(f.StartDate)), FrontW[2], true)
                    + (f.Masked
                        ? Cell($"<i>{Nb(f.MaskLabel)}</i>", FrontW[3], true)
                        : Cell(RunsTable.RateText(f.MidPct), FrontW[3], true))
                    + Cell(f.RefPct is double rp
                        ? (f.RefRebased ? $"<i>{RunsTable.RateText(rp)}*</i>" : RunsTable.RateText(rp))
                        : "&nbsp;", FrontW[4], true)
                    + (f.Masked
                        ? Cell("&nbsp;", FrontW[5], true)
                        : ChangeCell(f.PricedBp, FrontW[5], 0))   // scale 0 = no heat on Priced
                    + (f.Masked
                        ? Cell("&nbsp;", FrontW[6], true)
                        : Cell(f.PricedBp is double pv
                            ? NoBrk((pv / 25.0 * 100.0).ToString("+0;-0;0", Inv)) + "%" : "&nbsp;",
                            FrontW[6], true))
                    + Cell("&nbsp;", FrontW[7], false)
                    + "</tr>");
            }
            sb.Append(BlankRow(FrontW.Length));
            if (anyStartOnly || anyRebased)
                sb.Append($"<tr><td colspan=\"{FrontW.Length}\" style=\"{Font}font-size:11pt;" +
                    $"color:{Ink};padding:1px {TitlePad}px;line-height:15pt;\">"
                    + (anyStartOnly ? "*&nbsp;swap-period start shown (no decision calendar)" : "")
                    + (anyStartOnly && anyRebased ? "<br>" : "")
                    + (anyRebased ? $"<i>{RunsTable.RebaseNote}</i>" : "")
                    + "</td></tr>" + BlankRow(FrontW.Length));
            sb.Append("</table>");
            return sb.ToString();
        }

        private static string RunTables(WeeklyReport rep)
        {
            var blocks = RunsTable.Build(rep);
            if (blocks.Count == 0) return "";
            var sb = new StringBuilder();
            // ONE TABLE for every block (desk 2026-08-26: the blocks were separate tables and
            // Word sized each one from its own content, so the columns did not line up between
            // banks). A single table = a single column grid, by construction.
            sb.Append(TableOpen(Measure));
            sb.Append(TitleRow(RunsTable.Title(rep.AsOf), RunW.Length) + BlankRow(RunW.Length));
            foreach (var b in blocks)
            {
                // the sheet's two label rows: the bank, then its fixing with the value in col B
                sb.Append($"<tr><td colspan=\"{RunW.Length}\" nowrap style=\"{Font}font-size:11pt;" +
                    $"font-weight:bold;color:{Ink};padding:1px 3px;mso-line-height-rule:exactly;" +
                    $"line-height:15pt;\">{Nb(b.Bank + " closing run")}</td></tr>");
                sb.Append("<tr>"
                    + Cell(Nb(b.FixingLabel + " fixing"), RunW[0], false)
                    + Cell(b.FixingText.Length > 0
                        ? (b.Rebased ? $"<i>{b.FixingText}</i>" : b.FixingText) : "&nbsp;", RunW[1], true)
                    + Cell("&nbsp;", RunW[2], false) + Cell("&nbsp;", RunW[3], false)
                    + Cell("&nbsp;", RunW[4], false) + Cell("&nbsp;", RunW[5], false)
                    + Cell("&nbsp;", RunW[6], false) + Cell("&nbsp;", RunW[7], false)
                    + "</tr>");
                sb.Append("<tr>");
                for (int c = 0; c < RunsTable.Headers.Length; c++)
                    sb.Append(Head(RunsTable.Headers[c], RunW[c], c == 1 ? "rwh rwm" : "rwh"));
                sb.Append("</tr>");
                foreach (var m in b.Rows)
                {
                    sb.Append("<tr>");
                    sb.Append(Cell(NoBrk(RunsTable.DateText(m.Start)), RunW[0], true));
                    sb.Append(Cell(m.End is { } e ? NoBrk(RunsTable.DateText(e)) : "&nbsp;",
                        RunW[1], true, cls: "rwc rwm"));
                    if (m.Masked)
                    {
                        // the sheet writes the label into the Mid cell, italic
                        sb.Append(Cell($"<i>{Nb(m.MaskLabel)}</i>", RunW[2], false));
                        for (int c = 3; c < RunW.Length; c++) sb.Append(Cell("&nbsp;", RunW[c], true));
                    }
                    else
                    {
                        sb.Append(Cell(RunsTable.RateText(m.Mid), RunW[2], true));
                        sb.Append(Cell(m.PricedBp is double p ? NoBrk(RunsTable.BpText(p)) : "&nbsp;",
                            RunW[3], true));
                        sb.Append(Cell(m.StepBp is double st ? NoBrk(RunsTable.BpText(st)) : "&nbsp;",
                            RunW[4], true));
                        sb.Append(ChangeCell(m.D1Bp, RunW[5]));
                        sb.Append(ChangeCell(m.W1Bp, RunW[6]));
                        sb.Append(ChangeCell(m.M1Bp, RunW[7]));
                    }
                    sb.Append("</tr>");
                }
                sb.Append(BlankRow(RunW.Length));   // the sheet's blank separator row
            }
            // the * disclaimer, snug under the last OIS table and BEFORE the inflation runs,
            // only on a day a fixing actually carries the star (desk 2026-09-02)
            if (blocks.Any(x => x.Rebased))
                sb.Append($"<tr><td colspan=\"{RunW.Length}\" style=\"{Font}font-size:11pt;" +
                    $"font-style:italic;color:{Ink};padding:1px 3px;mso-line-height-rule:exactly;" +
                    $"line-height:15pt;\">{RunsTable.RebaseNote}</td></tr>" + BlankRow(RunW.Length));
            sb.Append("</table>");
            return sb.ToString();
        }

        // ---- inflation: a facsimile of the DRAX Fixing Runs sheet ----

        public static string InflHtml(Dictionary<string, List<InflHistory.DisplayRow>> rowsByFam,
            Dictionary<string, DateTime>? nextPrints, DateTime asOf)
        {
            var was = System.Globalization.CultureInfo.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = Inv;
            try
            {
                (string Key, string Title, string Index)[] fams =
                {
                    ("CPI", "US CPI Fixing Run", "CPURNSA"),
                    ("RPI", "UK RPI Fixing Run", "UKRPI"),
                    ("HICP", "EU HICP Ex-Tobacco Fixing Run", "CPTFEMU"),
                };
                var body = new StringBuilder();
                foreach (var (key, title, index) in fams)
                {
                    var rows = rowsByFam.TryGetValue(key, out var r) ? r : new List<InflHistory.DisplayRow>();
                    var shown = rows.Take(Math.Max(0, rows.Count - 1)).ToList();
                    if (shown.Count == 0) continue;
                    // the sheet's title row: name in col A, "Next Print:" in col D, date in col E
                    body.Append("<tr>"
                        + $"<td colspan=\"3\" nowrap style=\"{Font}font-size:11pt;font-weight:bold;" +
                          $"color:{Ink};padding:1px 3px;mso-line-height-rule:exactly;line-height:15pt;\">" +
                          $"{Nb(title)}</td>"
                        + Cell(nextPrints != null && nextPrints.ContainsKey(key)
                            ? Nb("Next Print:") : "&nbsp;", InflW[3], false)
                        + Cell(nextPrints != null && nextPrints.TryGetValue(key, out var np)
                            ? NoBrk(RunsTable.DateText(np)) : "&nbsp;", InflW[4], true)
                        + Cell("&nbsp;", InflW[5], false) + Cell("&nbsp;", InflW[6], false)
                        + Cell("&nbsp;", InflW[7], false)
                        + "</tr>");
                    // then the index ticker in col A and "Index Change" over the change columns
                    body.Append("<tr>"
                        + Cell(index, InflW[0], false) + Cell("&nbsp;", InflW[1], false)
                        + Cell("&nbsp;", InflW[2], false) + Cell("&nbsp;", InflW[3], false)
                        + Cell("&nbsp;", InflW[4], false)
                        + $"<td colspan=\"3\" nowrap style=\"{Font}font-size:11pt;font-weight:bold;" +
                          $"color:{Ink};padding:1px 3px;mso-line-height-rule:exactly;line-height:15pt;\">" +
                          $"{Nb("Index Change")}</td>"
                        + "</tr>");
                    string[] hdr = { "Month", "Base Index", "Mid Index", "YoY %", "MoM %",
                                     "Daily", "Weekly", "Monthly" };
                    body.Append("<tr>");
                    for (int c = 0; c < hdr.Length; c++) body.Append(Head(hdr[c], InflW[c]));
                    body.Append("</tr>");
                    foreach (var row in shown)
                    {
                        string N(double? v, int w) => Cell(v is double x
                            ? NoBrk(x.ToString("0.00", Inv)) : "&nbsp;", w, true);
                        // the index-point change scaled through the row's own base, so the bp ramp
                        // applies to an index move
                        double sc = (row.BaseV ?? row.Mid) is double bs && bs > 0 ? 10000.0 / bs : 0;
                        string C(double? v, int w) => v is double x
                            ? Cell(NoBrk(x.ToString("+0.00;-0.00;0.00", Inv)), w, true,
                                WeeklyEmail.HeatHex(x * sc) is string h ? $"background:{h};" : "")
                            : Cell("&nbsp;", w, true);
                        body.Append("<tr>"
                            + Cell(NoBrk(row.RefMonth.ToString("MMM-yy", Inv)), InflW[0], true)
                            + N(row.BaseV, InflW[1]) + N(row.Mid, InflW[2])
                            + N(row.Yoy, InflW[3]) + N(row.Mom, InflW[4])
                            + C(row.D1, InflW[5]) + C(row.W1, InflW[6]) + C(row.M1, InflW[7])
                            + "</tr>");
                    }
                    body.Append(BlankRow(InflW.Length));
                }
                if (body.Length == 0) return "";
                var sb = new StringBuilder();
                sb.Append(Style);
                sb.Append($"<div style=\"{Font}color:{Ink};font-size:11pt;-webkit-text-size-adjust:100%;\">");
                // ONE table for all three families — same single-grid rule as the OIS section,
                // and the SAME 624px measure so the two sections line up column for column
                sb.Append(TableOpen(Measure));
                sb.Append(TitleRow(InflRunsXlsx.Title(asOf), InflW.Length) + BlankRow(InflW.Length));
                sb.Append(body);
                sb.Append("</table>");
                sb.Append("</div>");
                return sb.ToString();
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
        }
    }
}
