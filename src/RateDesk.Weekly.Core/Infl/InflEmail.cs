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
            // the weekly email's own helpers, replicated (they are internal to Core by design)
            string Td(string inner, string extra = "") =>
                $"<td nowrap style=\"{WeeklyEmail.EmFont}padding:3px 8px;font-size:11.5px;" +
                $"white-space:nowrap;mso-line-height-rule:exactly;line-height:15px;{extra}\">{inner}</td>";
            string MH(string s, bool right = true) =>
                Td($"<b>{s}</b>", $"background:{WeeklyEmail.EmHead};{(right ? "text-align:right;" : "")}" +
                                  $"border-bottom:2px solid {WeeklyEmail.EmAccent};padding:4px 8px;");
            string RowBg(int i) => i % 2 == 1 ? "background:#f5f7fa;" : "";
            string Num(double? v, string fmt, int rI) => v is { } x
                ? Td(x.ToString(fmt), $"text-align:right;color:{WeeklyEmail.EmMut};{RowBg(rI)}")
                : Td("&nbsp;", RowBg(rI));

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
                if (slot++ > 0) sb.Append("<td style=\"font-size:1px;line-height:1px;\" width=\"8\">&nbsp;</td>");
                sb.Append("<td valign=\"top\">");
                var rows = rowsByFam.TryGetValue(key, out var r) ? r : new List<InflHistory.DisplayRow>();
                var shown = rows.Take(Math.Max(0, rows.Count - 1)).ToList();   // drop the furthest fixing
                string np = nextPrints != null && nextPrints.TryGetValue(key, out var d)
                    ? $"Next Print: {d:dd-MMM-yy}" : "";
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
                sb.Append("<tr>" + MH("Month", false) + MH("Base") + MH("Mid") + MH("YoY %") + MH("MoM %")
                          + MH("Δ1d") + MH("Δ1w") + MH("Δ1m") + "</tr>");
                int rI = 0;
                foreach (var row in shown)
                {
                    string bg = RowBg(rI);
                    sb.Append("<tr>" +
                        Td($"<b>{row.RefMonth:MMM yy}</b>", bg) +
                        Num(row.BaseV, "0.00", rI) +
                        (row.Mid is { } mid
                            ? Td($"<b>{mid:0.00}</b>", $"text-align:right;{bg}")
                            : Td("&nbsp;", bg)) +
                        Num(row.Yoy, "0.00", rI) +
                        Num(row.Mom, "0.00", rI) +
                        Num(row.D1, "+0.00;-0.00;0.00", rI) +
                        Num(row.W1, "+0.00;-0.00;0.00", rI) +
                        Num(row.M1, "+0.00;-0.00;0.00", rI) +
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
