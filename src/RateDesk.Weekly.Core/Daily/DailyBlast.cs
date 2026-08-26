using System.Text;
using RateDesk.Core;

namespace RateDesk.Weekly.Core.Daily
{
    /// <summary>The daily OIS chat blast — the app-owned replacement for the incumbent sheet's
    /// Blast tab (desk 2026-08-20, "improve immediately"): same block-per-bank shape and
    /// Bloomberg-chat country flags the readers know, but bp-denominated change columns, a
    /// 1w column, Priced In, and automatic Y/E Turn labelling (the sheet managed the SWESTR
    /// turn with a hand-typed "don't blast" note). T is the live 16:30-discipline mid off the
    /// meeting boards; Δ1d anchors YESTERDAY'S 16:30 SNAP via the roll-stitched series — the
    /// deterministic version of the sheet's T−1 (which was whatever yesterday's click saved).</summary>
    public static class DailyBlast
    {
        /// <summary>Blast order, chat flag, and fixing label per run — lifted from the incumbent
        /// sheet's CreateOISRuns BLOCKS list (SEK now Bloomberg SKSF, desk 2026-08-20).</summary>
        public static readonly (string Run, string Flag, string Fixing)[] Blocks =
        {
            ("ECB", "{EU}", "€STR"),
            ("MPC", "{GB}", "SONIA"),
            ("RBA", "{AU}", "RBA cash"),
            ("RBNZ", "{NZ}", "NZ OCR"),
            ("FOMC", "{US}", "EFFR"),
            ("BOC", "{CA}", "CORRA"),
            ("NORGES", "{NO}", "NOWA"),
            ("BOJ", "{JN}", "TONA"),
            ("RIKSBANK", "{SW}", "SWESTR"),
        };

        /// <summary>ONE run-lookup predicate for every surface that resolves a bank block from
        /// the report (blast, both workbooks — audit 2026-08-26: three subtly different
        /// predicates could disagree). Titles are "FOMC · USD" by construction.</summary>
        public static WeeklyRun? Find(WeeklyReport rep, string runName) =>
            rep.Runs.FirstOrDefault(r =>
                r.Title.Split('·')[0].Trim().Equals(runName, StringComparison.OrdinalIgnoreCase));

        public static string Render(WeeklyReport rep)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            // title verbatim from the incumbent blast (flag order included) — readers' muscle memory
            sb.AppendLine("{EU} {GB} {AU} {NZ} {US} {CA} {JN} {NO} {SW} London EOD OIS Run — "
                          + rep.AsOf.ToString("dd-MMM-yy", inv));

            foreach (var (runName, flag, fixing) in Blocks)
            {
                var run = Find(rep, runName);
                if (run == null || run.Rows.Count == 0) continue;

                sb.AppendLine();
                sb.Append($"{flag} {runName} Run");
                if (run.RefPct is { } rp)
                    sb.Append($"   ({fixing} {rp.ToString("0.000", inv)}{(run.RefRebased ? " rebased" : "")})");
                if (run.CompoundedPct is { } cp) sb.Append($"   (cmpd {cp.ToString("0.000", inv)})");
                sb.AppendLine();
                // same table as the workbook's Runs sheet, minus Maturity (IB window widths —
                // desk 2026-08-25); fixed-width columns so a chat paste reads as a table
                sb.AppendLine($"{"StartDate",-10} {"Mid",7} {"Step",6} {"Priced",7} {"Δ1d",6} {"Δ1w",6} {"Δ1m",6}");

                for (int i = 0; i < run.Rows.Count; i++)
                {
                    var m = run.Rows[i];
                    string start = m.Date.ToString("dd-MMM-yy", inv);
                    if (m.TurnPeriod)
                    {
                        sb.AppendLine($"{start,-10} {"Y/E Turn",7}");
                        continue;
                    }
                    sb.AppendLine(
                        $"{start,-10} {m.MidPct.ToString("0.000", inv),7} " +
                        $"{Bp(m.StepBp),6} {Bp(m.PricedBp),7} {Bp(m.D1Bp),6} {Bp(m.W1Bp),6} {Bp(m.M1Bp),6}");
                }
            }
            return sb.ToString();
        }

        /// <summary>The blast as an HTML TABLE (desk 2026-08-25): COPY BLAST puts this on the
        /// clipboard as CF_HTML so a Bloomberg-chat paste renders as a grid. It replicates the
        /// attached DRAX OIS Runs workbook's Runs sheet EXACTLY — same title, "closing run" and
        /// fixing rows, same grey header band, same number formats, no borders — MINUS the
        /// Maturity column (IB window widths, the desk's standing blast rule). Everything lives
        /// in ONE table so no row is dropped by the paste. Plain text rides along as fallback.</summary>
        public static string Html(WeeklyReport rep)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            const int cols = 7;
            static string Td(string s, string extra = "") =>
                $"<td nowrap style=\"font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:11px;" +
                $"padding:1px 8px;white-space:nowrap;{extra}\">{s}</td>";
            static string Wide(string s, string extra = "") =>
                $"<td nowrap colspan=\"{cols}\" style=\"font-family:Calibri,'Segoe UI',Arial,sans-serif;" +
                $"font-size:11px;padding:1px 8px;white-space:nowrap;{extra}\">{s}</td>";
            static string Blank() =>
                $"<tr><td colspan=\"{cols}\" style=\"font-size:6px;line-height:6px;\">&nbsp;</td></tr>";
            static string Num(double? v) =>
                Td(v is { } x ? x.ToString("+0.0;-0.0;0.0",
                    System.Globalization.CultureInfo.InvariantCulture) : "&nbsp;", "text-align:right;");

            var sb = new StringBuilder();
            // border="0" — Word-targeted pastes must never pick up default table borders
            sb.Append("<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;\">");
            // row 1 of the sheet: the bold title
            sb.Append("<tr>" + Wide($"<b>DRAX OIS Runs {rep.AsOf.ToString("dMMMyy", inv)}</b>") + "</tr>");
            sb.Append(Blank());
            foreach (var (runName, _, fixing) in Blocks)
            {
                var run = Find(rep, runName);
                if (run == null || run.Rows.Count == 0) continue;

                sb.Append("<tr>" + Wide($"<b>{runName} closing run</b>") + "</tr>");
                sb.Append("<tr>" + Td($"{fixing} fixing" + (run.RefRebased ? " (rebased)" : ""))
                    + Td(run.RefPct is { } rp ? rp.ToString("0.000", inv) : "&nbsp;", "text-align:right;")
                    + (run.CompoundedPct is { } cp
                        ? Td("compounded") + Td(cp.ToString("0.000", inv), "text-align:right;")
                        : Td("&nbsp;") + Td("&nbsp;"))
                    + Td("&nbsp;") + Td("&nbsp;") + Td("&nbsp;") + "</tr>");
                sb.Append("<tr>");
                foreach (var h in new[]
                         { "StartDate", "Mid", "Step (bp)", "Priced (bp)", "Δ 1d (bp)", "Δ 1w (bp)", "Δ 1m (bp)" })
                    sb.Append(Td($"<b>{h}</b>", "background:#d9d9d9;" + (h == "StartDate" ? "" : "text-align:right;")));
                sb.Append("</tr>");
                for (int i = 0; i < run.Rows.Count; i++)
                {
                    var m = run.Rows[i];
                    string start = m.Date.ToString("dd-MMM-yy", inv);
                    if (m.TurnPeriod)
                    {
                        sb.Append("<tr>" + Td(start) + Td("<i>Y/E Turn</i>", "text-align:right;")
                                  + Td("&nbsp;") + Td("&nbsp;") + Td("&nbsp;") + Td("&nbsp;") + Td("&nbsp;") + "</tr>");
                        continue;
                    }
                    sb.Append("<tr>" + Td(start)
                        + Td(m.MidPct.ToString("0.000", inv), "text-align:right;")
                        + Num(m.StepBp) + Num(m.PricedBp) + Num(m.D1Bp) + Num(m.W1Bp) + Num(m.M1Bp) + "</tr>");
                }
                sb.Append(Blank());
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        private static string Bp(double? v) =>
            v is { } x ? x.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) : "—";
    }
}
