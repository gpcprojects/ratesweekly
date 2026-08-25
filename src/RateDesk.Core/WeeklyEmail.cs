using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RateDesk.Core
{
    /// <summary>The WEEKLY report's email (CF_HTML fragment) and plain-text renderings — pure
    /// string builders over <see cref="WeeklyReport"/>, shared by Dodgeball and the standalone
    /// weekly app so there is exactly ONE definition of what the email looks like.
    ///
    /// RATESWEEKLY DIVERGENCE (2026-08-11, deliberate — candidate to cherry-pick back): Html and
    /// PlainText take optional dashboard-link hooks (ccyHref wraps currency header cells in
    /// anchors; a header carries the movers strip and a footer the "dashboards updated" line,
    /// DESIGN.md §4). Defaults are null, so with no arguments the rendering is byte-identical to
    /// dodgeball's.</summary>
    public static class WeeklyEmail
    {
    // fixed light styling: emails are white regardless of the app theme
    // (public here, not internal: RatesWeekly's EmailBuilder composes the footer in another assembly)
    public const string EmTxt = "#1a1d23", EmMut = "#66707f", EmLine = "#c9cfd8",
        EmHead = "#eef1f5", EmAccent = "#8a4a12";

    /// <summary>Email heat fill, monitor convention: GREEN = higher yield, RED = lower.
    /// Empty (no fill) under 2bp — colour marks movers; ramp 2→10bp; opaque pastels of the
    /// monitor's own colours so black text stays legible.</summary>
    public static string? HeatHex(double bp)
    {
        if (Math.Abs(bp) < 2.0) return null;
        double t = 0.16 + 0.44 * Math.Min(1.0, (Math.Abs(bp) - 2.0) / 8.0);
        static string Lerp(byte b, double t2) => ((byte)(0xFF + (b - 0xFF) * t2)).ToString("x2");
        return bp > 0
            ? "#" + Lerp(0x24, t) + Lerp(0xB3, t) + Lerp(0x58, t)   // white -> monitor green
            : "#" + Lerp(0xE0, t) + Lerp(0x45, t) + Lerp(0x33, t);  // white -> monitor red
    }

    // Word/Outlook does NOT inherit font-family into table cells — an outer div's Calibri becomes
    // Times New Roman the moment the content is a table, which is most of why the pasted report
    // looked nothing like the app. Every cell therefore carries the font itself.
    public const string EmFont = "font-family:Calibri,'Segoe UI',Arial,sans-serif;";

    /// <summary>At most this many currencies in one forward-grid table. 11 DM currencies x 3
    /// columns was 33 columns across one table: unreadably cramped pasted, and the first thing to
    /// collapse when the reader resizes. Stacked blocks of six read like a page instead of a
    /// spreadsheet dump.</summary>
    internal const int WeeklyCcysPerBlock = 6;

    /// <summary>Fixed-geometry table open tag + colgroup. table-layout:fixed with explicit column
    /// widths is what makes the paste survive: without it Word autofits to content and the grid
    /// reflows (differently) every time the reader resizes the window.</summary>
    internal static string TableOpen(IEnumerable<int> widths, string margin = "0 0 14px 0")
    {
        var sb = new StringBuilder();
        sb.Append($"<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;" +
                  $"table-layout:fixed;margin:{margin};mso-table-lspace:0pt;mso-table-rspace:0pt;\"><colgroup>");
        foreach (var w in widths) sb.Append($"<col style=\"width:{w}px;\">");
        sb.Append("</colgroup>");
        return sb.ToString();
    }

    /// <summary>Which report sections a rendering includes — the email settings tickboxes
    /// (desk 2026-08-21). Composition only: the report always carries everything.</summary>
    public readonly record struct EmailParts(bool Front = true, bool Runs = true, bool Grid = true)
    {
        // explicit args: `new()` binds the implicit parameterless struct ctor (all FALSE),
        // not the primary-constructor defaults — the all-off-renders-everything trap inverted
        public static readonly EmailParts All = new(true, true, true);
    }

    public static string Html(WeeklyReport rep, Func<string, string?>? ccyHref = null,
        string? footerHtml = null, string? headerHtml = null, EmailParts? partsOpt = null)
    {
        var parts = partsOpt ?? EmailParts.All;   // null = everything; all-false = truly empty
        var sb = new StringBuilder();
        // Outlook-safe anchor: inherit the cell's ink so the header stays a header; underline is
        // the only affordance. Absent a URL the cell renders exactly as before.
        string CcyLabel(string ccy) => ccyHref?.Invoke(ccy) is string u
            ? $"<a href=\"{u}\" style=\"color:inherit;text-decoration:underline;\">{ccy}</a>"
            : ccy;
        // line-height pinned EXACTLY: left to itself, Word picks its own line spacing per table
        // and near-identical tables render with visibly different row heights (the CB front vs
        // the meeting cards, 2026-08-11). One shared cell helper = one row height everywhere.
        // NOWRAP is just as load-bearing: Word SHRINKS any table wider than the window
        // proportionally, and once a change cell drops below its text width the value wraps and
        // doubles the row (the DM line, 2026-08-11). nowrap forbids the wrap, so a too-wide
        // table scrolls instead of mangling — row heights become window-independent.
        string Td(string inner, string extra = "") =>
            $"<td nowrap style=\"{EmFont}padding:3px 8px;font-size:11.5px;white-space:nowrap;mso-line-height-rule:exactly;line-height:15px;{extra}\">{inner}</td>";
        string Sep() => $"border-right:1px solid {EmLine};";
        // Spacer cell with 1px metrics — an UNSTYLED &nbsp; cell picks up Word's Normal style
        // (11pt + paragraph spacing) and inflates EVERY row in the table to its height, which is
        // exactly what happened to the 2026-08-11 grid rebuild. Same principle as Sp().
        // The WIDTH must live ON THE CELL (css + the width attribute): Word ignores colgroup
        // widths and sizes columns from cells, so a widthless 1px-font spacer collapses to
        // nothing — which is why the 16px colgroup separators rendered as no gap at all.
        string Gap(int w = 8) =>
            $"<td nowrap width=\"{w}\" style=\"{EmFont}font-size:1px;line-height:1px;border:none;width:{w}px;\">&nbsp;</td>";
        string RowBg(int rI) => rI % 2 == 1 ? "background:#f5f7fa;" : "";
        string ChgTd(double? v, bool topLine = false, bool sep = false, int rI = 0)
        {
            string tl = topLine ? $"border-top:1px solid {EmLine};" : "";
            string sp = sep ? Sep() : "";
            if (v is not double d) return Td("&nbsp;", tl + sp + RowBg(rI));
            string bg = HeatHex(d) is string h ? $"background:{h};" : RowBg(rI) + $"color:{EmMut};";
            return Td(d.ToString("+0.0;-0.0"), $"text-align:right;{bg}{tl}{sp}");
        }
        string Inv(DateTime d) => d.ToString("dd-MMM-yy", System.Globalization.CultureInfo.InvariantCulture);
        // Word-safe vertical spacer: div/table margins get half-ignored by Outlook's renderer,
        // an explicit-height div does not
        string Sp(int px) => $"<div style=\"font-size:{px}px;line-height:{px}px;\">&nbsp;</div>";
        // section title with a RULE between the title and its table — the rule is a table-cell
        // border (survives Word where a styled <hr> or a div border would not), full report width
        string H2(string s) =>
            TableOpen(new[] { 1168 }, "0") +
            $"<tr><td nowrap style=\"{EmFont}font-size:14.5px;font-weight:bold;color:{EmTxt};" +
            $"border-bottom:1px solid {EmLine};padding:4px 1px 5px 1px;\">{s}</td></tr></table>" + Sp(8);

        sb.Append($"<div style=\"{EmFont}color:{EmTxt};font-size:14px;\">");
        if (headerHtml != null) sb.Append(headerHtml);
        // ---- 1. CB Front Meeting Market Pricing ----
        if (parts.Front && rep.Fronts.Count > 0)
        {
            bool anyStartOnly = false;
            sb.Append(H2("CB Front Meeting Market Pricing"));
            sb.Append(TableOpen(new[] { 136, 108, 104, 82, 86, 90, 64 }));
            string FH(string s, bool right = false) =>
                Td($"<b>{s}</b>", $"background:{EmHead};{(right ? "text-align:right;" : "")}" +
                                  $"border-bottom:2px solid {EmAccent};padding:4px 8px;");
            sb.Append("<tr>" + FH("Central Bank") + FH("Decision Date") + FH("Start Date")
                + FH("OIS Mid", true) + FH("Fixing", true) + FH("Priced (bp)", true)
                + FH("% 25bp", true) + "</tr>");
            int fr = 0;
            foreach (var f in rep.Fronts)
            {
                string rb = RowBg(fr++);
                anyStartOnly |= f.Decision == null;
                // NO heat on Priced (desk 2026-08-11: "doesn't look as good as I thought") — the
                // emphasis column is % 25bp: the priced move as a share of a standard 25bp step,
                // signed by direction, deliberately uncapped (+50bp priced = +200%).
                string pct = f.PricedBp is double pv
                    ? (pv / 25.0 * 100.0).ToString("+0;-0;0") + "%" : "&nbsp;";
                sb.Append("<tr>" +
                    Td($"<b>{f.Bank}</b> <span style=\"color:{EmMut};font-size:10px;\">{CcyLabel(f.Ccy)}</span>", rb) +
                    Td(f.Decision is { } dd ? Inv(dd) : Inv(f.StartDate) + " *", rb) +
                    Td(Inv(f.StartDate), rb) +
                    // a year-end-spanning front period prints the turn, not the policy path —
                    // label it rather than publish a number that reads as a cut (desk 2026-08-20)
                    (f.TurnPeriod
                        ? Td($"<i>Y/E Turn</i>", $"text-align:right;color:{EmMut};{rb}") +
                          Td(f.RefPct is double rp3 ? rp3.ToString("0.000") : "&nbsp;", $"text-align:right;color:{EmMut};{rb}") +
                          Td("&nbsp;", rb) + Td("&nbsp;", rb)
                        : Td($"<b>{f.MidPct:0.000}</b>", $"text-align:right;{rb}") +
                          Td(f.RefPct is double rp2 ? rp2.ToString("0.000") : "&nbsp;", $"text-align:right;color:{EmMut};{rb}") +
                          Td(f.PricedBp is double p2 ? p2.ToString("+0.0;-0.0") : "&nbsp;", $"text-align:right;color:{EmMut};{rb}") +
                          Td($"<b>{pct}</b>", $"text-align:right;{rb}")) +
                    "</tr>");
            }
            sb.Append("</table>");
            if (anyStartOnly)
                sb.Append($"<div style=\"{EmFont}font-size:10px;color:{EmMut};margin:-10px 0 14px 2px;\">" +
                          "* swap-period start shown (no decision calendar for this bank)</div>");
        }

        // ---- 2. Central Bank OIS Meetings (3 cards per row) ----
        var runs = parts.Runs ? rep.Runs : new List<WeeklyRun>();
        if (runs.Count > 0)
        {
            sb.Append(Sp(10));
            sb.Append(H2("Central Bank OIS Meetings"));
        }
        for (int i = 0; i < runs.Count; i += 3)
        {
            sb.Append(TableOpen(new[] { 428, 8, 428, 8, 428 }, "0 0 8px 0"));
            sb.Append("<tr>");
            for (int k = 0; k < 3; k++)
            {
                if (k > 0) sb.Append(Gap());
                if (i + k >= runs.Count) { sb.Append("<td nowrap style=\"border:none;\">&nbsp;</td>"); continue; }
                var run = runs[i + k];
                // air below each card row matches the 26px column spacers (desk spec 2026-08-11:
                // vertical gaps between currencies = the horizontal ones) — padding, because Word
                // honours cell padding where it drops table margins
                sb.Append("<td nowrap style=\"vertical-align:top;padding:0 0 26px 0;\">");
                sb.Append($"<div style=\"{EmFont}font-weight:bold;font-size:12.5px;color:{EmTxt};margin:0 0 3px 1px;\">{run.Title}" +
                          (run.RefPct is double rp ? $" <span style=\"font-weight:normal;color:{EmMut};font-size:10px;\">fixing {rp:0.000}</span>" : "")
                          + "</div>");
                sb.Append(TableOpen(new[] { 76, 56, 58, 52, 56, 60, 60 }, "0"));
                string MH(string s, bool right = true) =>
                    Td($"<b>{s}</b>", $"background:{EmHead};{(right ? "text-align:right;" : "")}" +
                                      $"border-bottom:2px solid {EmAccent};padding:4px 8px;");
                sb.Append("<tr>" + MH("StartDate", false) + MH("Mid") + MH("Priced") + MH("Step")
                    + MH("1d Chg") + MH("1w Chg") + MH("1m Chg") + "</tr>");
                int mr = 0;
                foreach (var m in run.Rows)
                {
                    string rb = RowBg(mr++);
                    if (m.TurnPeriod)
                    {
                        // year-end-spanning period: the average carries the SWESTR-style turn
                        // dislocation — label it, never publish it as a policy expectation
                        sb.Append("<tr>" +
                            Td(Inv(m.Date), rb) +
                            Td($"<i>Y/E Turn</i>", $"text-align:right;color:{EmMut};{rb}") +
                            Td("&nbsp;", rb) + Td("&nbsp;", rb) + Td("&nbsp;", rb) + Td("&nbsp;", rb) + Td("&nbsp;", rb) +
                            "</tr>");
                        continue;
                    }
                    // Priced stays plain muted text — the heat experiment was reverted on the
                    // desk's read (2026-08-11); heat belongs to the CHANGE columns only
                    sb.Append("<tr>" +
                        Td(Inv(m.Date), rb) +
                        Td($"<b>{m.MidPct:0.000}</b>", $"text-align:right;{rb}") +
                        Td(m.PricedBp is double p ? p.ToString("+0.0;-0.0") : "&nbsp;", $"text-align:right;color:{EmMut};{rb}") +
                        Td(m.StepBp is double st ? st.ToString("+0.0;-0.0") : "&nbsp;", $"text-align:right;color:{EmMut};{rb}") +
                        ChgTd(m.D1Bp, false, false, mr - 1) +
                        ChgTd(m.W1Bp, false, false, mr - 1) + ChgTd(m.M1Bp, false, false, mr - 1) + "</tr>");
                }
                sb.Append("</table></td>");
            }
            sb.Append("</tr></table>");
        }

        // ---- 3. Forward Rates Summary ----
        // RATESWEEKLY DIVERGENCE (desk spec 2026-08-11): one table per grid LINE — DM, EM · LATAM,
        // ASIA EM — every currency of the line side by side, a 26px spacer column between currency
        // groups and 26px of air between the lines (the CB cards' own spacing unit). Each line
        // still stops at its own last populated row: a capped line must not print empty rows.
        var secs = parts.Grid ? rep.Sections : new List<WeeklySection>();
        if (secs.Count > 0)
        {
            sb.Append(Sp(6));
            sb.Append(H2("Forward Rates Summary"));
        }
        foreach (var sec in secs)
        {
            var group = sec.Ccys;
            if (group.Count == 0) continue;
            var labels = group[0].Cells.Select(cl => cl.Label).ToList();
            int lastRow = -1;
            for (int rI = 0; rI < labels.Count; rI++)
                if (group.Any(c => rI < c.Cells.Count && c.Cells[rI].Mid != null)) lastRow = rI;
            if (lastRow < 0) continue;

            // 8px separator columns between currency groups (desk-tuned 2026-08-11: 26 = holes,
            // 6 = nothing, 16 = too wide, 8 = the seam)
            var widths = new List<int> { 62 };
            for (int gI = 0; gI < group.Count; gI++)
            {
                widths.Add(62); widths.Add(50); widths.Add(50);
                if (gI < group.Count - 1) widths.Add(8);
            }

            // section title as a caption ABOVE the table — the CB cards' own pattern, and it
            // cannot wrap inside the 62px corner cell the way "EM · LATAM" would
            sb.Append($"<div style=\"{EmFont}font-weight:bold;font-size:12.5px;color:{EmTxt};" +
                      $"margin:0 0 3px 1px;\">{sec.Title}</div>");
            sb.Append(TableOpen(widths, "0"));

            sb.Append("<tr>").Append(Td("&nbsp;", $"background:{EmHead};"));
            for (int gI = 0; gI < group.Count; gI++)
            {
                sb.Append($"<td colspan=\"3\" nowrap style=\"{EmFont}padding:3px 8px;font-size:12px;" +
                          $"background:{EmHead};text-align:center;font-weight:bold;\">{CcyLabel(group[gI].Ccy)}</td>");
                if (gI < group.Count - 1) sb.Append(Gap());
            }
            sb.Append("</tr>");

            sb.Append("<tr>").Append(Td("&nbsp;", $"background:{EmHead};border-bottom:2px solid {EmAccent};"));
            for (int gI = 0; gI < group.Count; gI++)
            {
                string h = $"background:{EmHead};text-align:center;color:{EmMut};font-size:9.5px;" +
                           $"border-bottom:2px solid {EmAccent};";
                sb.Append(Td("mid", h)).Append(Td("1w", h)).Append(Td("1m", h));
                if (gI < group.Count - 1) sb.Append(Gap());
            }
            sb.Append("</tr>");

            for (int rI = 0; rI <= lastRow; rI++)
            {
                bool tl = rI is 1 or 5; // spot | 1y-window forwards | longer windows
                string tls = tl ? $"border-top:1px solid {EmLine};" : "";
                sb.Append("<tr>").Append(Td($"<b>{labels[rI]}</b>",
                    $"background:{EmHead};{tls}{Sep()}"));
                for (int gI = 0; gI < group.Count; gI++)
                {
                    var c = group[gI];
                    var cell = rI < c.Cells.Count ? c.Cells[rI] : null;
                    if (cell?.Mid is not double mid)
                    {
                        sb.Append(Td("&nbsp;", tls + RowBg(rI)))
                          .Append(ChgTd(null, tl, false, rI)).Append(ChgTd(null, tl, false, rI));
                    }
                    else
                    {
                        sb.Append(Td($"<b>{mid:0.000}</b>", $"text-align:right;{tls}{RowBg(rI)}"));
                        sb.Append(ChgTd(cell.W1Bp, tl, false, rI)).Append(ChgTd(cell.M1Bp, tl, false, rI));
                    }
                    if (gI < group.Count - 1) sb.Append(Gap());
                }
                sb.Append("</tr>");
            }
            // the air below each grid line lives INSIDE the table as an exact-height row — the
            // SAME mechanism as the CB cards' bottom padding, so table-bottom→next-title matches
            // the cards to the pixel (a free-standing Sp() div picks up Word paragraph spacing
            // and renders fatter than the cards' clean 26px)
            sb.Append($"<tr><td colspan=\"{widths.Count}\" nowrap height=\"26\" " +
                      $"style=\"{EmFont}font-size:1px;line-height:1px;border:none;\">&nbsp;</td></tr>");
            sb.Append("</table>");
        }

        if (footerHtml != null) sb.Append(footerHtml);
        sb.Append("</div>");
        return sb.ToString();
    }

    public static string PlainText(WeeklyReport rep, string? footerText = null, string? headerText = null,
        EmailParts? partsOpt = null)
    {
        var parts = partsOpt ?? EmailParts.All;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (headerText != null) sb.AppendLine(headerText);
        if (parts.Front && rep.Fronts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("CB Front Meeting Market Pricing");
            sb.AppendLine("Central Bank\tDecision Date\tStart Date\tOIS Mid\tFixing\tPriced (bp)");
            foreach (var f in rep.Fronts)
                sb.AppendLine($"{f.Bank} {f.Ccy}\t{(f.Decision ?? f.StartDate).ToString("dd-MMM-yy", inv)}{(f.Decision == null ? " *" : "")}\t" +
                    $"{f.StartDate.ToString("dd-MMM-yy", inv)}\t" +
                    (f.TurnPeriod
                        ? $"Y/E Turn\t{(f.RefPct is double rt ? rt.ToString("0.000") : "")}\t"
                        : $"{f.MidPct:0.000}\t{(f.RefPct is double rr ? rr.ToString("0.000") : "")}\t" +
                          $"{(f.PricedBp is double p ? p.ToString("+0.0;-0.0") : "")}"));
        }
        var truns = parts.Runs ? rep.Runs : new List<WeeklyRun>();
        if (truns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Central Bank OIS Meetings");
        }
        foreach (var run in truns)
        {
            sb.AppendLine();
            sb.AppendLine(run.Title + (run.RefPct is double rp ? $"  fixing {rp:0.000}" : ""));
            sb.AppendLine("StartDate\tMid\tPriced\tStep\t1d Chg\t1w Chg\t1m Chg");
            foreach (var m in run.Rows)
                sb.AppendLine(m.TurnPeriod
                    ? $"{m.Date.ToString("dd-MMM-yy", inv)}\tY/E Turn\t\t\t\t\t"
                    : $"{m.Date.ToString("dd-MMM-yy", inv)}\t{m.MidPct:0.000}\t{m.PricedBp:+0.0;-0.0}\t{m.StepBp:+0.0;-0.0}\t" +
                      $"{(m.D1Bp is double d1 ? d1.ToString("+0.0;-0.0") : "")}\t" +
                      $"{(m.W1Bp is double w ? w.ToString("+0.0;-0.0") : "")}\t{(m.M1Bp is double m1 ? m1.ToString("+0.0;-0.0") : "")}");
        }

        var tsecs = parts.Grid ? rep.Sections : new List<WeeklySection>();
        if (tsecs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Forward Rates Summary");
        }
        foreach (var sec in tsecs)
        {
            var labels = sec.Ccys[0].Cells.Select(cl => cl.Label).ToList();
            sb.AppendLine();
            sb.AppendLine(sec.Title);
            sb.AppendLine("\t" + string.Join("\t", sec.Ccys.Select(c => $"{c.Ccy} mid\t1w\t1m")));
            for (int rI = 0; rI < labels.Count; rI++)
            {
                sb.Append(labels[rI]);
                foreach (var c in sec.Ccys)
                {
                    var cell = c.Cells[rI];
                    if (cell.Mid is not double mid) { sb.Append("\t\t\t"); continue; }
                    sb.Append('\t').Append(cell.IsSpread ? mid.ToString("+0.0;-0.0") : mid.ToString("0.000"));
                    sb.Append('\t').Append(cell.W1Bp is double w ? w.ToString("+0.0;-0.0") : "");
                    sb.Append('\t').Append(cell.M1Bp is double m ? m.ToString("+0.0;-0.0") : "");
                }
                sb.AppendLine();
            }
        }
        if (footerText != null) { sb.AppendLine(); sb.AppendLine(footerText); }
        return sb.ToString();
    }
}
}
