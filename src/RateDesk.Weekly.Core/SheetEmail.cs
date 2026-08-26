using System.Text;
using RateDesk.Core;
using RateDesk.Weekly.Core.Daily;
using RateDesk.Weekly.Core.Infl;

namespace RateDesk.Weekly.Core
{
    /// <summary>SHEET STYLE — the emails' DEFAULT body since desk 2026-08-26: the inline content
    /// IS the attachment's table, so the xls, the blast and both emails read as one product.
    /// Same rows, same column order (Mid | Priced | Step), same number strings — all straight off
    /// RunsTable / InflHistory.BuildDisplayRows, the writers the attachments use.
    ///
    /// DESIGN (the brief was "align it with aesthetic principles"):
    ///   · one table per bank, STACKED in a single column — never the 3-across card grid, whose
    ///     ~1300px width forced phones to scale the whole mail to ~30% and made it unreadable;
    ///   · 488px measure, so a 390px phone scales to ~80% and desktop Outlook has air to spare;
    ///   · minimal ink: NO vertical rules and no interior horizontals — one accent hairline under
    ///     the header band, quiet zebra striping to carry the eye across eight columns;
    ///   · one accent (the report's burnt-sienna), everything else ink or muted grey;
    ///   · the headline number (Mid) bold, its derivations muted, so a row reads in one glance;
    ///   · conditional formatting on the CHANGE columns only — the monitor's green-up/red-down
    ///     ramp (desk 2026-08-11: no heat on Priced), so the tape is legible without reading;
    ///   · two-line units in the header ("Priced" / "(bp)") to keep the xls's own labels while
    ///     holding the measure — the numbers stay wide enough to never wrap.
    ///
    /// WORD/OUTLOOK RULES (learned the hard way, 2026-08-25): every width lives ON the cell as
    /// attribute AND style (Word ignores colgroup), multi-word text is &amp;nbsp;-joined (Word
    /// breaks at spaces even under nowrap), negatives use U+2011 (Word breaks after a
    /// hyphen-minus), and each block is ONE table (a paste drops divs between tables).
    ///
    /// MOBILE: a media-query block shrinks type and padding under 620px and drops the Maturity
    /// column under 440px (the phone-portrait case) — clients that honour it get a tighter table,
    /// Word ignores it and shows the full sheet. Duplicate emission is harmless: the rules are
    /// identical, so a body plus an inflation fragment may each carry one.</summary>
    public static class SheetEmail
    {
        // ---- design tokens ----
        private const string Font = WeeklyEmail.EmFont;
        private const string Ink = WeeklyEmail.EmTxt;      // #1a1d23
        private const string Mut = WeeklyEmail.EmMut;      // #66707f
        private const string Accent = WeeklyEmail.EmAccent; // #8a4a12
        private const string Band = "#f0f3f7";             // header band
        private const string Zebra = "#f7f9fb";            // alternate row
        private const int Measure = 488;                   // the single column's width

        // widths chosen so the widest CONTENT (a bold header, a dd-MMM-yy date, a signed bp
        // figure) clears its cell with the 8px padding — a number must never wrap
        private static readonly int[] RunW = { 76, 76, 56, 58, 54, 56, 56, 56 };   // 488
        private static readonly int[] FrontW = { 96, 74, 74, 60, 58, 58, 58 };     // 478
        private static readonly int[] InflW = { 60, 64, 64, 52, 52, 58, 58, 60 };  // 468

        private static readonly System.Globalization.CultureInfo Inv =
            System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>The responsive rules. Emitted at the top of any sheet-style fragment.</summary>
        public const string Style =
            "<style type=\"text/css\">" +
            "@media only screen and (max-width:620px){" +
            ".rwc{font-size:10.5px!important;padding:2px 5px!important}" +
            ".rwh{font-size:9.5px!important;padding:3px 5px!important}" +
            ".rwt{font-size:11.5px!important}}" +
            "@media only screen and (max-width:440px){" +
            ".rwm{display:none!important}" +
            ".rwc{font-size:10px!important;padding:2px 4px!important}" +
            ".rwh{font-size:9px!important;padding:2px 4px!important}}" +
            "</style>";

        // ---- cell helpers (Word-safe by construction) ----

        private static string Nb(string s) => s.Replace(" ", "&nbsp;");
        /// <summary>U+2011: renders as a hyphen, cannot break a line.</summary>
        private static string NoBrk(string s) => s.Replace("-", "‑");

        private static string Cell(string inner, int w, string extra = "", string cls = "rwc") =>
            $"<td nowrap width=\"{w}\" class=\"{cls}\" style=\"{Font}width:{w}px;padding:3px 8px;" +
            $"font-size:11.5px;white-space:nowrap;mso-line-height-rule:exactly;line-height:15px;" +
            $"{extra}\">{inner}</td>";

        /// <summary>Header cell: the xls's own label, with its units dropped to a quiet second
        /// line so the column can hold the measure without ever wrapping a number. Bottom-aligned
        /// so single- and two-line headers share a baseline.</summary>
        private static string Head(string label, int w, bool right, string? inner = null,
            string cls = "rwh")
        {
            string Unit(string u) =>
                $"<br><span style=\"font-weight:normal;font-size:9px;color:{Mut};\">{u}</span>";
            inner ??= label switch
            {
                var s when s.EndsWith(" (bp)", StringComparison.Ordinal) => Nb(s[..^5]) + Unit("bp"),
                var s when s.EndsWith(" %", StringComparison.Ordinal) => Nb(s[..^2]) + Unit("%"),
                var s => Nb(s),
            };
            return $"<td nowrap valign=\"bottom\" width=\"{w}\" class=\"{cls}\" style=\"{Font}width:{w}px;" +
                   $"padding:4px 8px;font-size:10.5px;font-weight:bold;color:{Ink};background:{Band};" +
                   $"border-bottom:2px solid {Accent};white-space:nowrap;mso-line-height-rule:exactly;" +
                   $"line-height:13px;{(right ? "text-align:right;" : "")}\">{inner}</td>";
        }

        private static string Bg(int rowIndex) => rowIndex % 2 == 1 ? $"background:{Zebra};" : "";

        /// <summary>A change cell with the monitor's conditional formatting: green = higher yield,
        /// red = lower, nothing under 2bp. Blank stays blank — never a manufactured 0.</summary>
        private static string ChangeCell(double? v, int w, int rowIndex, string cls = "rwc")
        {
            if (v is not double d) return Cell("&nbsp;", w, Bg(rowIndex), cls);
            string bg = WeeklyEmail.HeatHex(d) is string h
                ? $"background:{h};color:{Ink};"
                : Bg(rowIndex) + $"color:{Mut};";
            return Cell(NoBrk(RunsTable.BpText(d)), w, $"text-align:right;{bg}", cls);
        }

        private static string TableOpen(int width) =>
            $"<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;" +
            $"table-layout:fixed;width:{width}px;max-width:100%;margin:0;mso-table-lspace:0pt;" +
            "mso-table-rspace:0pt;\">";

        /// <summary>Section title with the report's full-measure rule, and a muted caption naming
        /// the sheet this content IS — the reader can see the inline table and the attachment are
        /// the same thing (desk 2026-08-26). Titles stay plain text: a 488px cell has no pressure
        /// to break, the same as the card sections the desk verified across machines.</summary>
        private static string SectionTitle(string s, string? caption = null) =>
            TableOpen(Measure) +
            $"<tr><td nowrap class=\"rwt\" style=\"{Font}font-size:14px;font-weight:bold;color:{Ink};" +
            $"border-bottom:1px solid {Accent};padding:2px 1px 5px 1px;letter-spacing:0.2px;\">{s}" +
            (caption is null ? ""
                : $"<span style=\"font-weight:normal;font-size:9.5px;color:{Mut};\">" +
                  $"&nbsp;&nbsp;{caption}</span>")
            + "</td></tr></table>" + Spacer(9, Measure);

        private static string Spacer(int px, int width) =>
            $"<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;" +
            $"width:{width}px;max-width:100%;\"><tr><td width=\"{width}\" height=\"{px}\" " +
            $"style=\"width:{width}px;font-size:1px;line-height:{px}px;\">&nbsp;</td></tr></table>";

        // ---- the body ----

        /// <summary>The sheet-style email body: the CB front table (when ticked) and the per-bank
        /// run tables (when ticked). Inflation rides in its own frozen fragment.</summary>
        public static string Body(WeeklyReport rep, bool front, bool runs)
        {
            var was = System.Globalization.CultureInfo.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = Inv;
            try
            {
                var sb = new StringBuilder();
                sb.Append(Style);
                // -webkit-text-size-adjust stops iOS inflating the type and breaking the measure
                sb.Append($"<div style=\"{Font}color:{Ink};font-size:13px;-webkit-text-size-adjust:100%;\">");
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
            sb.Append(SectionTitle("CB Front Meeting Market Pricing"));
            sb.Append(TableOpen(Measure));
            sb.Append("<tr>"
                + Head("Central Bank", FrontW[0], false) + Head("Decision", FrontW[1], false)
                + Head("Start", FrontW[2], false) + Head("OIS Mid", FrontW[3], true)
                + Head("Fixing", FrontW[4], true) + Head("Priced (bp)", FrontW[5], true)
                + Head("% 25bp", FrontW[6], true) + "</tr>");
            bool anyStartOnly = false, anyRebased = false;
            int i = 0;
            foreach (var f in rep.Fronts)
            {
                string rb = Bg(i++);
                anyStartOnly |= f.Decision == null;
                anyRebased |= f.RefRebased;
                string pct = f.PricedBp is double pv
                    ? NoBrk((pv / 25.0 * 100.0).ToString("+0;-0;0", Inv)) + "%" : "&nbsp;";
                sb.Append("<tr>"
                    + Cell($"<b>{f.Bank}</b>&nbsp;<span style=\"color:{Mut};font-size:9.5px;\">{f.Ccy}</span>",
                        FrontW[0], rb)
                    + Cell(NoBrk(f.Decision is { } dd ? RunsTable.DateText(dd)
                        : RunsTable.DateText(f.StartDate) + "*"), FrontW[1], $"color:{Ink};{rb}")
                    + Cell(NoBrk(RunsTable.DateText(f.StartDate)), FrontW[2], $"color:{Mut};{rb}")
                    + (f.TurnPeriod
                        ? Cell("<i>Y/E&nbsp;Turn</i>", FrontW[3], $"text-align:right;color:{Mut};{rb}")
                        : Cell($"<b>{RunsTable.RateText(f.MidPct)}</b>", FrontW[3], $"text-align:right;{rb}"))
                    + Cell(f.RefPct is double rp
                        ? RunsTable.RateText(rp) + (f.RefRebased ? "†" : "") : "&nbsp;",
                        FrontW[4], $"text-align:right;color:{Mut};{rb}")
                    + (f.TurnPeriod
                        ? Cell("&nbsp;", FrontW[5], rb)
                        : Cell(f.PricedBp is double p2 ? NoBrk(RunsTable.BpText(p2)) : "&nbsp;",
                            FrontW[5], $"text-align:right;color:{Mut};{rb}"))
                    + (f.TurnPeriod
                        ? Cell("&nbsp;", FrontW[6], rb)
                        : Cell($"<b>{pct}</b>", FrontW[6], $"text-align:right;{rb}"))
                    + "</tr>");
            }
            sb.Append("</table>");
            if (anyStartOnly || anyRebased)
            {
                sb.Append(Spacer(4, Measure));
                sb.Append(TableOpen(Measure) + $"<tr><td style=\"{Font}font-size:9.5px;color:{Mut};" +
                    "padding:0 1px;line-height:13px;\">"
                    + (anyStartOnly ? "*&nbsp;swap-period start shown (no decision calendar)" : "")
                    + (anyStartOnly && anyRebased ? "<br>" : "")
                    + (anyRebased ? "†&nbsp;fixing re-based onto the just-decided period's OIS — the new rate has not printed yet" : "")
                    + "</td></tr></table>");
            }
            sb.Append(Spacer(24, Measure));
            return sb.ToString();
        }

        private static string RunTables(WeeklyReport rep)
        {
            var blocks = RunsTable.Build(rep);
            if (blocks.Count == 0) return "";
            var sb = new StringBuilder();
            // the caption names the attachment these tables ARE, as-of included
            sb.Append(SectionTitle("Central Bank OIS Meetings", RunsTable.Title(rep.AsOf)));
            bool anySynth = false;
            foreach (var b in blocks)
            {
                sb.Append(TableOpen(Measure));
                // block head: the bank, then its fixing — the sheet's own two title rows
                sb.Append($"<tr><td colspan=\"{RunW.Length}\" nowrap class=\"rwt\" style=\"{Font}" +
                    $"font-size:12.5px;font-weight:bold;color:{Ink};padding:0 8px 1px 1px;\">" +
                    $"{Nb(b.Bank + " closing run")}</td></tr>");
                sb.Append($"<tr><td colspan=\"{RunW.Length}\" nowrap style=\"{Font}font-size:10px;" +
                    $"color:{Mut};padding:0 8px 4px 1px;\">" +
                    Nb(b.FixingLabel + " fixing") +
                    (b.FixingPct is { } fp ? "&nbsp;" + RunsTable.RateText(fp) : "") +
                    (b.Rebased ? "&nbsp;†&nbsp;(rebased)" : "") + "</td></tr>");
                sb.Append("<tr>");
                for (int c = 0; c < RunsTable.Headers.Length; c++)
                    // column 1 (Maturity) carries rwm: the phone-portrait media query drops it
                    sb.Append(Head(RunsTable.Headers[c], RunW[c], c >= 2,
                        cls: c == 1 ? "rwh rwm" : "rwh"));
                sb.Append("</tr>");
                int i = 0;
                foreach (var m in b.Rows)
                {
                    string rb = Bg(i);
                    sb.Append("<tr>");
                    sb.Append(Cell(NoBrk(RunsTable.DateText(m.Start)), RunW[0], rb));
                    sb.Append(Cell(m.End is { } e ? NoBrk(RunsTable.DateText(e)) : "&nbsp;",
                        RunW[1], $"color:{Mut};{rb}", "rwc rwm"));
                    if (m.Turn)
                    {
                        sb.Append(Cell($"<i>{Nb(RunsTable.TurnLabel)}</i>", RunW[2],
                            $"text-align:right;color:{Mut};{rb}"));
                        for (int c = 3; c < RunW.Length; c++) sb.Append(Cell("&nbsp;", RunW[c], rb));
                    }
                    else
                    {
                        if (m.Synthetic) anySynth = true;
                        sb.Append(Cell($"<b>{RunsTable.RateText(m.Mid)}{(m.Synthetic ? "†" : "")}</b>",
                            RunW[2], $"text-align:right;{rb}"));
                        sb.Append(Cell(m.PricedBp is double p ? NoBrk(RunsTable.BpText(p)) : "&nbsp;",
                            RunW[3], $"text-align:right;color:{Mut};{rb}"));
                        sb.Append(Cell(m.StepBp is double st ? NoBrk(RunsTable.BpText(st)) : "&nbsp;",
                            RunW[4], $"text-align:right;color:{Mut};{rb}"));
                        sb.Append(ChangeCell(m.D1Bp, RunW[5], i));
                        sb.Append(ChangeCell(m.W1Bp, RunW[6], i));
                        sb.Append(ChangeCell(m.M1Bp, RunW[7], i));
                    }
                    sb.Append("</tr>");
                    i++;
                }
                // the air below each block lives INSIDE the table as an exact-height row — a
                // free-standing div picks up Word's paragraph spacing and renders fatter
                sb.Append($"<tr><td colspan=\"{RunW.Length}\" height=\"22\" style=\"{Font}font-size:1px;" +
                          "line-height:22px;border:none;\">&nbsp;</td></tr>");
                sb.Append("</table>");
            }
            if (anySynth)
                sb.Append(TableOpen(Measure) + $"<tr><td style=\"{Font}font-size:9.5px;color:{Mut};" +
                    "padding:0 1px 14px 1px;line-height:13px;\">" +
                    "†&nbsp;mid is the neighbour midpoint — the quoted print was rejected as implausible" +
                    "</td></tr></table>");
            return sb.ToString();
        }

        // ---- inflation, same language ----

        /// <summary>The inflation fixing runs in sheet style — a mirror of the DRAX Fixing Runs
        /// sheet: one stacked table per family, the furthest fixing dropped (its monthly change
        /// cannot exist yet), heat on the three index-change columns.</summary>
        public static string InflHtml(Dictionary<string, List<InflHistory.DisplayRow>> rowsByFam,
            Dictionary<string, DateTime>? nextPrints)
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
                var sb = new StringBuilder();
                var body = new StringBuilder();
                foreach (var (key, title, index) in fams)
                {
                    var rows = rowsByFam.TryGetValue(key, out var r) ? r : new List<InflHistory.DisplayRow>();
                    var shown = rows.Take(Math.Max(0, rows.Count - 1)).ToList();
                    if (shown.Count == 0) continue;   // no ghost header-only block
                    body.Append(TableOpen(Measure));
                    body.Append($"<tr><td colspan=\"5\" nowrap class=\"rwt\" style=\"{Font}font-size:12.5px;" +
                        $"font-weight:bold;color:{Ink};padding:0 8px 1px 1px;\">{Nb(title)}</td>" +
                        $"<td colspan=\"3\" nowrap style=\"{Font}font-size:9.5px;color:{Mut};" +
                        "text-align:right;padding:0 8px 1px 1px;\">"
                        + (nextPrints != null && nextPrints.TryGetValue(key, out var np)
                            ? Nb("Next Print: ") + NoBrk(RunsTable.DateText(np)) : "&nbsp;")
                        + "</td></tr>");
                    body.Append($"<tr><td colspan=\"5\" nowrap style=\"{Font}font-size:10px;color:{Mut};" +
                        $"padding:0 8px 4px 1px;\">{index}</td>" +
                        $"<td colspan=\"3\" nowrap style=\"{Font}font-size:9.5px;color:{Mut};" +
                        "text-align:right;padding:0 8px 4px 1px;\">" + Nb("Index Change") + "</td></tr>");
                    // the sheet's own labels; "Base Index"/"Mid Index" break at their own space
                    (string L, string? Inner)[] hdr =
                    {
                        ("Month", null), ("Base Index", "Base<br>Index"), ("Mid Index", "Mid<br>Index"),
                        ("YoY %", null), ("MoM %", null), ("Daily", null), ("Weekly", null), ("Monthly", null),
                    };
                    body.Append("<tr>");
                    for (int c = 0; c < hdr.Length; c++)
                        body.Append(Head(hdr[c].L, InflW[c], c > 0, hdr[c].Inner));
                    body.Append("</tr>");
                    int i = 0;
                    foreach (var row in shown)
                    {
                        string rb = Bg(i);
                        string Num(double? v, int w) => v is double x
                            ? Cell(NoBrk(x.ToString("0.00", Inv)), w, $"text-align:right;color:{Mut};{rb}")
                            : Cell("&nbsp;", w, rb);
                        // the index-point change scaled through the row's own base, so the
                        // monitor's bp ramp applies to an index move
                        string Chg(double? v, int w) => v is double x
                            ? Cell(NoBrk(x.ToString("+0.00;-0.00;0.00", Inv)), w,
                                "text-align:right;" + ((row.BaseV ?? row.Mid) is double bs && bs > 0
                                    && WeeklyEmail.HeatHex(x / bs * 10000.0) is string h
                                        ? $"background:{h};color:{Ink};" : rb + $"color:{Mut};"))
                            : Cell("&nbsp;", w, rb);
                        body.Append("<tr>"
                            + Cell($"<b>{NoBrk(row.RefMonth.ToString("MMM-yy", Inv))}</b>", InflW[0], rb)
                            + Num(row.BaseV, InflW[1])
                            + (row.Mid is double mid
                                ? Cell($"<b>{mid.ToString("0.00", Inv)}</b>", InflW[2], $"text-align:right;{rb}")
                                : Cell("&nbsp;", InflW[2], rb))
                            + Num(row.Yoy, InflW[3]) + Num(row.Mom, InflW[4])
                            + Chg(row.D1, InflW[5]) + Chg(row.W1, InflW[6]) + Chg(row.M1, InflW[7])
                            + "</tr>");
                        i++;
                    }
                    body.Append($"<tr><td colspan=\"8\" height=\"22\" style=\"{Font}font-size:1px;" +
                                "line-height:22px;border:none;\">&nbsp;</td></tr>");
                    body.Append("</table>");
                }
                if (body.Length == 0) return "";
                sb.Append(Style);
                sb.Append($"<div style=\"{Font}color:{Ink};font-size:13px;-webkit-text-size-adjust:100%;\">");
                sb.Append(SectionTitle("Inflation Fixing Runs"));
                sb.Append(body);
                sb.Append("</div>");
                return sb.ToString();
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
        }
    }
}
