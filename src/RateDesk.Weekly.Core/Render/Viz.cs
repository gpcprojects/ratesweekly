using System.Globalization;
using System.Text;
using System.Text.Json;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>Design tokens. The three curve lines are an ORDINAL ramp, not categorical hues:
    /// today / 1w / 1m are the same measure at three times, so recency is encoded as darkness on
    /// one hue. Both ramps were run through the data-viz validator (validate_palette.py --ordinal)
    /// and pass monotone-lightness, adjacent-ΔL, light-end contrast and single-hue in BOTH modes:
    ///   light #86b6ef → #3987e5 → #104281   (light end 2.06:1 vs #fcfcfb)
    ///   dark  #256abf → #5598e7 → #cde2fb   (light end 3.23:1 vs #1a1a19)
    /// Do not hand-tweak these without re-running the validator.</summary>
    public static class Viz
    {
        public const string SeriesToday = "var(--rw-today)";
        public const string SeriesWeek = "var(--rw-week)";
        public const string SeriesMonth = "var(--rw-month)";

        /// <summary>CSS custom properties for both themes. The theme toggle stamps data-theme on
        /// the root and must win over the OS setting in both directions.</summary>
        public const string ThemeCss = """
            :root{
              color-scheme:light;
              --rw-surface:#fcfcfb; --rw-plane:#f9f9f7;
              --rw-ink:#0b0b0b; --rw-ink2:#52514e; --rw-muted:#898781;
              --rw-grid:#e1e0d9; --rw-axis:#c3c2b7; --rw-border:rgba(11,11,11,.10);
              --rw-today:#104281; --rw-week:#3987e5; --rw-month:#86b6ef;
              --rw-up:#c5342f; --rw-down:#2a78d6; --rw-flat:#f0efec;
            }
            @media (prefers-color-scheme:dark){
              :root:where(:not([data-theme=light])){
                color-scheme:dark;
                --rw-surface:#1a1a19; --rw-plane:#0d0d0d;
                --rw-ink:#fff; --rw-ink2:#c3c2b7; --rw-muted:#898781;
                --rw-grid:#2c2c2a; --rw-axis:#383835; --rw-border:rgba(255,255,255,.10);
                --rw-today:#cde2fb; --rw-week:#5598e7; --rw-month:#256abf;
                --rw-up:#e66767; --rw-down:#3987e5; --rw-flat:#383835;
              }
            }
            :root[data-theme=dark]{
              color-scheme:dark;
              --rw-surface:#1a1a19; --rw-plane:#0d0d0d;
              --rw-ink:#fff; --rw-ink2:#c3c2b7; --rw-muted:#898781;
              --rw-grid:#2c2c2a; --rw-axis:#383835; --rw-border:rgba(255,255,255,.10);
              --rw-today:#cde2fb; --rw-week:#5598e7; --rw-month:#256abf;
              --rw-up:#e66767; --rw-down:#3987e5; --rw-flat:#383835;
            }
            """;

        public static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
        public static string F(double v, int dp = 3) => v.ToString("F" + dp, CultureInfo.InvariantCulture);

        /// <summary>Axis ticks on clean round numbers (1/2/2.5/5 × 10^n).</summary>
        public static List<double> Ticks(double min, double max, int target = 5)
        {
            if (double.IsNaN(min) || double.IsNaN(max) || min > max) return new();
            if (Math.Abs(max - min) < 1e-9) { min -= 0.05; max += 0.05; }
            double raw = (max - min) / Math.Max(2, target);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double step = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * mag;
            var list = new List<double>();
            for (double t = Math.Ceiling(min / step) * step; t <= max + 1e-9; t += step) list.Add(t);
            return list;
        }
    }

    public sealed record ChartSeries(string Name, string Color, IReadOnlyList<(double X, double Y)> Points);

    /// <summary>Inline-SVG line chart: 2px lines, hairline solid grid, legend always present for
    /// ≥2 series, ONE selective end-label (the Today line — the point of the chart), plus a
    /// crosshair/tooltip hover layer and a table-view twin so no value is gated behind hover.
    /// Self-contained by construction: no external libraries, nothing fetched at view time.</summary>
    public static class LineChart
    {
        public static string Render(
            string id, string title, string subtitle,
            IReadOnlyList<ChartSeries> series,
            string xLabel, string yLabel,
            Func<double, string> xFmt, Func<double, string> yFmt,
            int height = 300, string xKind = "years")
        {
            var live = series.Where(s => s.Points.Count > 0).ToList();
            var sb = new StringBuilder();
            sb.Append($"<figure class=\"rw-card\" data-xfmt=\"{Viz.Esc(xKind)}\" id=\"{Viz.Esc(id)}\">");
            sb.Append($"<figcaption><h3>{Viz.Esc(title)}</h3>");
            if (!string.IsNullOrEmpty(subtitle)) sb.Append($"<p class=\"rw-sub\">{Viz.Esc(subtitle)}</p>");
            sb.Append("</figcaption>");

            if (live.Count == 0)
            {
                sb.Append("<div class=\"rw-empty\">no data in the store for this section yet</div></figure>");
                return sb.ToString();
            }

            double xMin = live.Min(s => s.Points.Min(p => p.X)), xMax = live.Max(s => s.Points.Max(p => p.X));
            double yMin = live.Min(s => s.Points.Min(p => p.Y)), yMax = live.Max(s => s.Points.Max(p => p.Y));
            double pad = Math.Max((yMax - yMin) * 0.12, 0.02);
            yMin -= pad; yMax += pad;
            if (Math.Abs(xMax - xMin) < 1e-9) xMax = xMin + 1;

            const int W = 720, ml = 52, mr = 76, mt = 10, mb = 34;
            int pw = W - ml - mr, ph = height - mt - mb;
            double SX(double x) => ml + (x - xMin) / (xMax - xMin) * pw;
            double SY(double y) => mt + (yMax - y) / (yMax - yMin) * ph;

            // legend — identity is never color-alone; every series is named here
            sb.Append("<div class=\"rw-legend\">");
            foreach (var s in series)
                sb.Append($"<span class=\"rw-key\"><i style=\"background:{s.Color}\"></i>{Viz.Esc(s.Name)}</span>");
            sb.Append("</div>");

            sb.Append($"<svg class=\"rw-svg\" viewBox=\"0 0 {W} {height}\" preserveAspectRatio=\"xMidYMid meet\" ")
              .Append($"role=\"img\" aria-label=\"{Viz.Esc(title)}\">");

            // horizontal gridlines + y ticks (solid hairlines, one step off surface)
            foreach (var t in Viz.Ticks(yMin, yMax))
            {
                double y = SY(t);
                sb.Append($"<line x1=\"{ml}\" y1=\"{Viz.F(y, 1)}\" x2=\"{ml + pw}\" y2=\"{Viz.F(y, 1)}\" class=\"rw-grid\"/>");
                sb.Append($"<text x=\"{ml - 8}\" y=\"{Viz.F(y + 3.5, 1)}\" class=\"rw-tick rw-tick-y\">{Viz.Esc(yFmt(t))}</text>");
            }
            // x ticks from the densest series
            var xs = live.OrderByDescending(s => s.Points.Count).First().Points.Select(p => p.X).ToList();
            int stride = Math.Max(1, (int)Math.Ceiling(xs.Count / 8.0));
            for (int i = 0; i < xs.Count; i += stride)
                sb.Append($"<text x=\"{Viz.F(SX(xs[i]), 1)}\" y=\"{height - mb + 18}\" class=\"rw-tick rw-tick-x\">{Viz.Esc(xFmt(xs[i]))}</text>");
            sb.Append($"<line x1=\"{ml}\" y1=\"{mt + ph}\" x2=\"{ml + pw}\" y2=\"{mt + ph}\" class=\"rw-axis\"/>");

            // lines — drawn oldest first so Today sits on top
            foreach (var s in live)
            {
                var d = new StringBuilder();
                for (int i = 0; i < s.Points.Count; i++)
                    d.Append(i == 0 ? "M" : "L").Append(Viz.F(SX(s.Points[i].X), 1)).Append(' ').Append(Viz.F(SY(s.Points[i].Y), 1)).Append(' ');
                sb.Append($"<path d=\"{d.ToString().Trim()}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"2\" ")
                  .Append("stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
            }

            // ONE selective direct label: the end of the last (Today) series
            var lead = live[^1];
            var last = lead.Points[^1];
            sb.Append($"<circle cx=\"{Viz.F(SX(last.X), 1)}\" cy=\"{Viz.F(SY(last.Y), 1)}\" r=\"4\" fill=\"{lead.Color}\" ")
              .Append("stroke=\"var(--rw-surface)\" stroke-width=\"2\"/>");
            sb.Append($"<text x=\"{Viz.F(SX(last.X) + 10, 1)}\" y=\"{Viz.F(SY(last.Y) + 4, 1)}\" class=\"rw-endlab\">{Viz.Esc(yFmt(last.Y))}</text>");

            // hover layer
            sb.Append($"<line class=\"rw-cross\" x1=\"0\" y1=\"{mt}\" x2=\"0\" y2=\"{mt + ph}\" style=\"display:none\"/>");
            sb.Append($"<rect class=\"rw-hit\" x=\"{ml}\" y=\"{mt}\" width=\"{pw}\" height=\"{ph}\" fill=\"transparent\"/>");
            sb.Append("</svg>");

            var payload = new
            {
                ml, mr, mt, mb, W, height, xMin, xMax, yMin, yMax,
                series = live.Select(s => new { name = s.Name, color = s.Color, pts = s.Points.Select(p => new[] { p.X, p.Y }) }),
            };
            sb.Append($"<script type=\"application/json\" class=\"rw-data\">{JsonSerializer.Serialize(payload)}</script>");
            sb.Append("<div class=\"rw-tip\" hidden></div>");

            // table-view twin — every value reachable without hover
            sb.Append("<details class=\"rw-table\"><summary>Table view</summary><table><thead><tr>")
              .Append($"<th>{Viz.Esc(xLabel)}</th>");
            foreach (var s in live) sb.Append($"<th>{Viz.Esc(s.Name)}</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var x in live.OrderByDescending(s => s.Points.Count).First().Points.Select(p => p.X))
            {
                sb.Append($"<tr><td>{Viz.Esc(xFmt(x))}</td>");
                foreach (var s in live)
                {
                    var hit = s.Points.FirstOrDefault(p => Math.Abs(p.X - x) < 1e-9, (X: double.NaN, Y: double.NaN));
                    sb.Append("<td>").Append(double.IsNaN(hit.Y) ? "—" : Viz.Esc(yFmt(hit.Y))).Append("</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append($"</tbody></table><p class=\"rw-sub\">{Viz.Esc(yLabel)}</p></details>");
            sb.Append("</figure>");
            return sb.ToString();
        }

        /// <summary>Convenience: the standard today / 1w / 1m triple.</summary>
        public static string FromTriple(
            string id, CurveTriple t, string title, string subtitle,
            string xLabel, Func<double, string> xFmt, int height = 300, string xKind = "years")
        {
            var series = new List<ChartSeries>
            {
                new("1m ago", Viz.SeriesMonth, t.Month.Select(p => (p.Years, p.RatePct)).ToList()),
                new("1w ago", Viz.SeriesWeek, t.Week.Select(p => (p.Years, p.RatePct)).ToList()),
                new($"{t.AsOf:d MMM}", Viz.SeriesToday, t.Today.Select(p => (p.Years, p.RatePct)).ToList()),
            };
            return Render(id, title, subtitle, series, xLabel, "rate, %",
                xFmt, v => Viz.F(v, 3) + "%", height, xKind);
        }
    }
}
