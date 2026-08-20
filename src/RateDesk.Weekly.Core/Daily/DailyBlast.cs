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

        public static string Render(WeeklyReport rep)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            // title verbatim from the incumbent blast (flag order included) — readers' muscle memory
            sb.AppendLine("{EU} {GB} {AU} {NZ} {US} {CA} {JN} {NO} {SW} London EOD OIS Run — "
                          + rep.AsOf.ToString("dd-MMM-yy", inv));

            foreach (var (runName, flag, fixing) in Blocks)
            {
                var run = rep.Runs.FirstOrDefault(r =>
                    r.Title.StartsWith(runName + " ", StringComparison.OrdinalIgnoreCase)
                    || r.Title.Split('·')[0].Trim().Equals(runName, StringComparison.OrdinalIgnoreCase));
                if (run == null || run.Rows.Count == 0) continue;

                sb.AppendLine();
                sb.Append($"{flag} {runName} Run");
                if (run.RefPct is { } rp) sb.Append($"   ({fixing} {rp.ToString("0.000", inv)})");
                sb.AppendLine();
                sb.AppendLine($"{"Start",-10} {"End",-10} {"Mid",7} {"Δ1d",6} {"Δ1w",6} {"Step",6} {"Priced",7}");

                for (int i = 0; i < run.Rows.Count; i++)
                {
                    var m = run.Rows[i];
                    string start = m.Date.ToString("dd-MMM-yy", inv);
                    string end = i + 1 < run.Rows.Count
                        ? run.Rows[i + 1].Date.ToString("dd-MMM-yy", inv) : "—";
                    if (m.TurnPeriod)
                    {
                        sb.AppendLine($"{start,-10} {end,-10} {"Y/E Turn",7}");
                        continue;
                    }
                    sb.AppendLine(
                        $"{start,-10} {end,-10} {m.MidPct.ToString("0.000", inv),7} " +
                        $"{Bp(m.D1Bp),6} {Bp(m.W1Bp),6} {Bp(m.StepBp),6} {Bp(m.PricedBp),7}");
                }
            }
            return sb.ToString();
        }

        private static string Bp(double? v) =>
            v is { } x ? x.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) : "—";
    }
}
