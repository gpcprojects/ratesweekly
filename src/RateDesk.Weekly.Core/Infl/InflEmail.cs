using System.Text;
using RateDesk.Core;

namespace RateDesk.Weekly.Core.Infl
{
    /// <summary>The INFLATION FIXING RUNS email section (desk 2026-08-25) — three cards
    /// (CPI · CPURNSA, RPI · UKRPI, HICP · CPTFEMU) in the weekly email's own visual language,
    /// appended below the OIS meeting tables on the daily and below the forward grid on the
    /// weekly. Rows are the desk screen's shape — Month | Base | Mid | YoY% | MoM% | Δ index
    /// 1d/1w/1m — derived once in InflHistory.BuildDisplayRows from the run's marks and the
    /// unified fixings history; the furthest fixing is dropped (its monthly change cannot
    /// exist yet — the incumbent's own rule). "Next Print" is Bloomberg's ECO_RELEASE_DT,
    /// omitted when unavailable, never guessed. Rendered at RUN time and persisted to out\
    /// so click-time composition appends the frozen fragment under the current tickboxes.</summary>
    public static class InflEmail
    {
        public const string DailyHtmlFile = "daily_infl.html";
        public const string DailyTextFile = "daily_infl.txt";
        public const string WeeklyHtmlFile = "weekly_infl.html";
        public const string WeeklyTextFile = "weekly_infl.txt";

        private static readonly (string Key, string Label, string Index)[] Cards =
        {
            ("CPI", "CPI", "CPURNSA"), ("RPI", "RPI", "UKRPI"), ("HICP", "HICP", "CPTFEMU"),
        };

        /// <summary>Build both flavours from the same data and persist them (prefix "daily_" or
        /// "weekly_"). Returns the html fragment for immediate use.</summary>
        public static string WriteFragments(HistoryStore store,
            Dictionary<string, List<InflHistory.Mark>>? marks,
            Dictionary<string, DateTime>? nextPrints, DateTime asOf, string outDir, bool daily)
        {
            marks ??= InflHistory.LatestMarks(store);
            var rows = new Dictionary<string, List<InflHistory.DisplayRow>>();
            foreach (var fam in InflHistory.Families)
                rows[fam.Key] = InflHistory.BuildDisplayRows(store, fam,
                    marks.TryGetValue(fam.Key, out var m) ? m : new List<InflHistory.Mark>(), asOf);
            var html = Html(rows, nextPrints);
            var text = PlainText(rows, nextPrints);
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, daily ? DailyHtmlFile : WeeklyHtmlFile), html);
            File.WriteAllText(Path.Combine(outDir, daily ? DailyTextFile : WeeklyTextFile), text);
            return html;
        }

        public static string Html(Dictionary<string, List<InflHistory.DisplayRow>> rowsByFam,
            Dictionary<string, DateTime>? nextPrints)
        {
            // INVARIANT culture for the whole rendering (audit 2026-08-26) — same rule as
            // WeeklyEmail: the fragment must print "May 26"/"312.55" on every desk machine
            var wasCulture = System.Globalization.CultureInfo.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.InvariantCulture;
            try { return HtmlCore(rowsByFam, nextPrints); }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = wasCulture; }
        }

        private static string HtmlCore(Dictionary<string, List<InflHistory.DisplayRow>> rowsByFam,
            Dictionary<string, DateTime>? nextPrints)
        {
            // the weekly email's own helpers, replicated (they are internal to Core by design).
            // WIDTHS LIVE ON EVERY CELL (attribute + css): Outlook renders through Word, which
            // ignores colgroup widths and sizes columns from cells — without this the cards
            // collapsed and wrapped ("Aug 26" onto two lines) on other desks (2026-08-25). All
            // multi-word cell text uses &nbsp; — Word breaks at spaces even under nowrap.
            var colW = new[] { 48, 58, 58, 48, 48, 56, 56, 56 };
            string Td(string inner, int col, string extra = "") =>
                $"<td nowrap width=\"{colW[col]}\" style=\"{WeeklyEmail.EmFont}padding:3px 8px;" +
                $"font-size:11.5px;width:{colW[col]}px;white-space:nowrap;" +
                $"mso-line-height-rule:exactly;line-height:15px;{extra}\">{inner}</td>";
            string MH(string s, int col, bool right = true) =>
                Td($"<b>{s.Replace(" ", "&nbsp;")}</b>", col,
                   $"background:{WeeklyEmail.EmHead};{(right ? "text-align:right;" : "")}" +
                   $"border-bottom:2px solid {WeeklyEmail.EmAccent};padding:4px 8px;");
            string RowBg(int i) => i % 2 == 1 ? "background:#f5f7fa;" : "";
            // Word line-breaks AFTER a hyphen-minus even under nowrap ("-0.46" split across two
            // lines on the desk's RPI card, 2026-08-25) — negative values use U+2011, the
            // non-breaking hyphen, which renders identically and cannot break
            static string NoBreak(string s) => s.Replace("-", "‑");
            string Num(double? v, string fmt, int col, int rI) => v is { } x
                ? Td(NoBreak(x.ToString(fmt)), col, $"text-align:right;color:{WeeklyEmail.EmMut};{RowBg(rI)}")
                : Td("&nbsp;", col, RowBg(rI));
            // Δ columns carry the OIS cards' heat (desk 2026-08-25): the index-point change is
            // scaled to implied YoY bp through the row's own base so the monitor ramp applies
            string ChgTd(double? v, double? scaleBase, int col, int rI)
            {
                if (v is not { } x) return Td("&nbsp;", col, RowBg(rI));
                string bg = scaleBase is { } b && b > 0 && WeeklyEmail.HeatHex(x / b * 10000.0) is { } h
                    ? $"background:{h};" : RowBg(rI) + $"color:{WeeklyEmail.EmMut};";
                return Td(NoBreak(x.ToString("+0.00;-0.00;0.00")), col, $"text-align:right;{bg}");
            }

            var sb = new StringBuilder();
            // section header, same 1168px rule the other sections use
            sb.Append($"<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;" +
                      "table-layout:fixed;margin:0;\"><colgroup><col style=\"width:1168px;\"></colgroup>" +
                      $"<tr><td nowrap style=\"{WeeklyEmail.EmFont}font-size:14.5px;font-weight:bold;" +
                      $"color:{WeeklyEmail.EmTxt};border-bottom:1px solid {WeeklyEmail.EmLine};" +
                      "padding:4px 1px 5px 1px;\">Inflation Fixing Runs</td></tr></table>" +
                      "<div style=\"font-size:8px;line-height:8px;\">&nbsp;</div>");

            sb.Append("<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;" +
                      "table-layout:fixed;margin:0 0 14px 0;\"><colgroup>" +
                      "<col style=\"width:428px;\"><col style=\"width:8px;\"><col style=\"width:428px;\">" +
                      "<col style=\"width:8px;\"><col style=\"width:428px;\"></colgroup><tr>");
            int slot = 0;
            foreach (var (key, label, index) in Cards)
            {
                var rows = rowsByFam.TryGetValue(key, out var r) ? r : new List<InflHistory.DisplayRow>();
                var shown = rows.Take(Math.Max(0, rows.Count - 1)).ToList();   // drop the furthest fixing
                if (shown.Count == 0) continue;   // no ghost header-only card (audit 2026-08-26)
                if (slot++ > 0) sb.Append("<td style=\"font-size:1px;line-height:1px;\" width=\"8\">&nbsp;</td>");
                sb.Append("<td valign=\"top\">");
                string np = nextPrints != null && nextPrints.TryGetValue(key, out var d)
                    ? $"Next&nbsp;Print:&nbsp;{d:dd-MMM-yy}" : "";
                sb.Append("<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;" +
                          "table-layout:fixed;width:428px;\"><colgroup>" +
                          "<col style=\"width:48px;\"><col style=\"width:58px;\"><col style=\"width:58px;\">" +
                          "<col style=\"width:48px;\"><col style=\"width:48px;\"><col style=\"width:56px;\">" +
                          "<col style=\"width:56px;\"><col style=\"width:56px;\"></colgroup>");
                sb.Append($"<tr><td colspan=\"5\" nowrap style=\"{WeeklyEmail.EmFont}font-size:12px;" +
                          $"font-weight:bold;color:{WeeklyEmail.EmTxt};padding:2px 8px 3px 8px;\">{label} " +
                          $"<span style=\"color:{WeeklyEmail.EmMut};font-weight:normal;\">· {index}</span></td>" +
                          $"<td colspan=\"3\" nowrap style=\"{WeeklyEmail.EmFont}font-size:10.5px;" +
                          $"color:{WeeklyEmail.EmMut};text-align:right;padding:2px 8px 3px 8px;\">{np}</td></tr>");
                sb.Append("<tr>" + MH("Month", 0, false) + MH("Base", 1) + MH("Mid", 2) + MH("YoY %", 3)
                          + MH("MoM %", 4) + MH("Δ1d", 5) + MH("Δ1w", 6) + MH("Δ1m", 7) + "</tr>");
                int rI = 0;
                foreach (var row in shown)
                {
                    string bg = RowBg(rI);
                    sb.Append("<tr>" +
                        Td($"<b>{row.RefMonth:MMM}&nbsp;{row.RefMonth:yy}</b>", 0, bg) +
                        Num(row.BaseV, "0.00", 1, rI) +
                        (row.Mid is { } mid
                            ? Td($"<b>{mid:0.00}</b>", 2, $"text-align:right;{bg}")
                            : Td("&nbsp;", 2, bg)) +
                        Num(row.Yoy, "0.00", 3, rI) +
                        Num(row.Mom, "0.00", 4, rI) +
                        ChgTd(row.D1, row.BaseV ?? row.Mid, 5, rI) +
                        ChgTd(row.W1, row.BaseV ?? row.Mid, 6, rI) +
                        ChgTd(row.M1, row.BaseV ?? row.Mid, 7, rI) +
                        "</tr>");
                    rI++;
                }
                sb.Append("</table></td>");
            }
            sb.Append("</tr></table>");
            return sb.ToString();
        }

        public static string PlainText(Dictionary<string, List<InflHistory.DisplayRow>> rowsByFam,
            Dictionary<string, DateTime>? nextPrints)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("INFLATION FIXING RUNS");
            foreach (var (key, label, index) in Cards)
            {
                var rows = rowsByFam.TryGetValue(key, out var r) ? r : new List<InflHistory.DisplayRow>();
                var shown = rows.Take(Math.Max(0, rows.Count - 1)).ToList();
                if (shown.Count == 0) continue;
                sb.AppendLine();
                sb.Append($"{label} · {index}");
                if (nextPrints != null && nextPrints.TryGetValue(key, out var d))
                    sb.Append($"   Next Print: {d.ToString("dd-MMM-yy", inv)}");
                sb.AppendLine();
                sb.AppendLine("Month\tBase\tMid\tYoY %\tMoM %\tΔ1d\tΔ1w\tΔ1m");
                foreach (var row in shown)
                    sb.AppendLine(string.Join("\t",
                        row.RefMonth.ToString("MMM yy", inv),
                        F(row.BaseV, "0.00"), F(row.Mid, "0.00"), F(row.Yoy, "0.00"), F(row.Mom, "0.00"),
                        F(row.D1, "+0.00;-0.00;0.00"), F(row.W1, "+0.00;-0.00;0.00"), F(row.M1, "+0.00;-0.00;0.00")));
            }
            return sb.ToString();

            static string F(double? v, string fmt) =>
                v is { } x ? x.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) : "";
        }
    }
}
