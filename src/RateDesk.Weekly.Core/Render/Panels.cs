using System.Globalization;
using System.Text;
using System.Text.Json;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>One labelled point of a ladder at three times. Every section on a currency page —
    /// par curve, forward ladder, meeting run, FRA strip, inflation curve — reduces to a list of
    /// these, which is why they can all share one renderer and one interaction model.</summary>
    public sealed record LadderPoint(string Label, double? Now, double? Week, double? Month)
    {
        public double? W1Bp => Now.HasValue && Week.HasValue ? (Now.Value - Week.Value) * 100.0 : null;
        public double? M1Bp => Now.HasValue && Month.HasValue ? (Now.Value - Month.Value) * 100.0 : null;
    }

    /// <summary>Table and chart side by side, cross-highlighting on hover: hovering a table row
    /// marks that point on the chart and vice versa. The table is the primary reading surface —
    /// every value is present as text, so nothing is gated behind a hover — and the chart carries
    /// the shape.</summary>
    public static class Panels
    {
        public static string Linked(
            string id, string title, string subtitle,
            IReadOnlyList<LadderPoint> pts, IReadOnlyList<string> notes,
            int valueDp = 3, string valueSuffix = "%")
        {
            var sb = new StringBuilder();
            sb.Append($"<section class=\"rw-panel\" id=\"{Viz.Esc(id)}\" data-panel=\"{Viz.Esc(id)}\">");
            sb.Append($"<header class=\"rw-panel-head\"><h3>{Viz.Esc(title)}</h3>");
            if (!string.IsNullOrEmpty(subtitle)) sb.Append($"<p class=\"rw-sub\">{Viz.Esc(subtitle)}</p>");
            sb.Append("</header>");

            var live = pts.Where(p => p.Now.HasValue).ToList();
            if (live.Count == 0)
            {
                sb.Append("<div class=\"rw-empty\">no data in the store for this section yet</div></section>");
                return sb.ToString();
            }

            sb.Append("<div class=\"rw-panel-body\">");

            // ---- table -------------------------------------------------------------------
            sb.Append("<div class=\"rw-tblwrap\"><table class=\"rw-lvl\"><thead><tr>")
              .Append("<th>&nbsp;</th><th>level</th><th>1w</th><th>1m</th></tr></thead><tbody>");
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (!p.Now.HasValue) continue;
                sb.Append($"<tr class=\"rw-row\" data-i=\"{i}\" tabindex=\"0\">")
                  .Append($"<td class=\"rw-lab\">{Viz.Esc(p.Label)}</td>")
                  .Append($"<td class=\"rw-val\">{Viz.F(p.Now.Value, valueDp)}</td>")
                  .Append(BpCell(p.W1Bp)).Append(BpCell(p.M1Bp)).Append("</tr>");
            }
            sb.Append("</tbody></table></div>");

            // ---- chart -------------------------------------------------------------------
            sb.Append(Chart(id, pts, valueDp, valueSuffix));
            sb.Append("</div>");

            if (notes.Count > 0)
                sb.Append($"<p class=\"rw-note\">{Viz.Esc(string.Join(" · ", notes))}</p>");
            sb.Append("</section>");
            return sb.ToString();
        }

        /// <summary>Change cell. GREEN = higher yield, RED = lower (the desk's convention). The
        /// sign is always in the text, so the colour is never the sole carrier of direction.</summary>
        private static string BpCell(double? bp)
        {
            if (bp is null) return "<td class=\"rw-bp rw-nil\">—</td>";
            string cls = Math.Abs(bp.Value) < 0.5 ? "rw-flatbp" : bp.Value > 0 ? "rw-upbp" : "rw-downbp";
            return $"<td class=\"rw-bp {cls}\">{bp.Value.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}</td>";
        }

        private static string Chart(string id, IReadOnlyList<LadderPoint> pts, int dp, string suffix)
        {
            const int W = 560, H = 260, ml = 48, mr = 58, mt = 12, mb = 40;
            int pw = W - ml - mr, ph = H - mt - mb;

            var vals = pts.SelectMany(p => new[] { p.Now, p.Week, p.Month })
                          .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            double yMin = vals.Min(), yMax = vals.Max();
            double pad = Math.Max((yMax - yMin) * 0.12, 0.02);
            yMin -= pad; yMax += pad;
            int n = pts.Count;
            double SX(int i) => n <= 1 ? ml + pw / 2.0 : ml + (double)i / (n - 1) * pw;
            double SY(double v) => mt + (yMax - v) / (yMax - yMin) * ph;

            var sb = new StringBuilder();
            sb.Append("<div class=\"rw-chartwrap\">");
            sb.Append("<div class=\"rw-legend\">")
              .Append($"<span class=\"rw-key\"><i style=\"background:{Viz.SeriesMonth}\"></i>1m ago</span>")
              .Append($"<span class=\"rw-key\"><i style=\"background:{Viz.SeriesWeek}\"></i>1w ago</span>")
              .Append($"<span class=\"rw-key\"><i style=\"background:{Viz.SeriesToday}\"></i>latest</span>")
              .Append("</div>");
            sb.Append($"<svg class=\"rw-svg\" viewBox=\"0 0 {W} {H}\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\">");

            foreach (var t in Viz.Ticks(yMin, yMax, 4))
            {
                double y = SY(t);
                sb.Append($"<line x1=\"{ml}\" y1=\"{Viz.F(y, 1)}\" x2=\"{ml + pw}\" y2=\"{Viz.F(y, 1)}\" class=\"rw-grid\"/>")
                  .Append($"<text x=\"{ml - 8}\" y=\"{Viz.F(y + 3.5, 1)}\" class=\"rw-tick rw-tick-y\">{Viz.F(t, dp == 3 ? 2 : dp)}</text>");
            }
            sb.Append($"<line x1=\"{ml}\" y1=\"{mt + ph}\" x2=\"{ml + pw}\" y2=\"{mt + ph}\" class=\"rw-axis\"/>");

            int stride = Math.Max(1, (int)Math.Ceiling(n / 7.0));
            for (int i = 0; i < n; i += stride)
                sb.Append($"<text x=\"{Viz.F(SX(i), 1)}\" y=\"{H - mb + 16}\" class=\"rw-tick rw-tick-x\">{Viz.Esc(pts[i].Label)}</text>");

            // oldest first so the latest line sits on top
            foreach (var (sel, colour) in new (Func<LadderPoint, double?>, string)[]
                     { (p => p.Month, Viz.SeriesMonth), (p => p.Week, Viz.SeriesWeek), (p => p.Now, Viz.SeriesToday) })
            {
                var d = new StringBuilder();
                bool open = false;
                for (int i = 0; i < n; i++)
                {
                    var v = sel(pts[i]);
                    if (v is null) { open = false; continue; }
                    d.Append(open ? "L" : "M").Append(Viz.F(SX(i), 1)).Append(' ').Append(Viz.F(SY(v.Value), 1)).Append(' ');
                    open = true;
                }
                if (d.Length > 0)
                    sb.Append($"<path d=\"{d.ToString().Trim()}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"2\" ")
                      .Append("stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
            }

            // one marker per point on the latest line — the cross-highlight target
            for (int i = 0; i < n; i++)
            {
                if (pts[i].Now is not { } v) continue;
                sb.Append($"<circle class=\"rw-pt\" data-i=\"{i}\" cx=\"{Viz.F(SX(i), 1)}\" cy=\"{Viz.F(SY(v), 1)}\" ")
                  .Append($"r=\"4\" fill=\"{Viz.SeriesToday}\" stroke=\"var(--rw-surface)\" stroke-width=\"2\"/>");
            }
            sb.Append($"<rect class=\"rw-hit\" x=\"{ml}\" y=\"{mt}\" width=\"{pw}\" height=\"{ph}\" fill=\"transparent\"/>");
            sb.Append("</svg>");

            var payload = new
            {
                ml, mr, mt, mb, W, H, n,
                labels = pts.Select(p => p.Label),
                now = pts.Select(p => p.Now),
                week = pts.Select(p => p.Week),
                month = pts.Select(p => p.Month),
                dp,
                suffix,
            };
            sb.Append($"<script type=\"application/json\" class=\"rw-data\">{JsonSerializer.Serialize(payload)}</script>");
            sb.Append("<div class=\"rw-tip\" hidden></div>");
            sb.Append("</div>");
            return sb.ToString();
        }

        // ---- adapters: every series type collapses to LadderPoint ------------------------

        public static List<LadderPoint> From(CurveTriple t) =>
            t.Today.Select(p => new LadderPoint(
                p.Label,
                p.RatePct,
                Find(t.Week, p.Years),
                Find(t.Month, p.Years))).ToList();

        public static List<LadderPoint> From(LadderTriple t)
        {
            var res = new List<LadderPoint>();
            for (int i = 0; i < t.Labels.Count; i++)
                res.Add(new LadderPoint(t.Labels[i], At(t.Today, i), At(t.Week, i), At(t.Month, i)));
            return res;
        }

        public static List<LadderPoint> From(StripTable s) =>
            s.Rows.Select(r => new LadderPoint(r.Label, r.Mid, r.WeekLevel, r.MonthLevel)).ToList();

        private static double? Find(List<CurvePoint> pts, double years)
        {
            foreach (var p in pts) if (Math.Abs(p.Years - years) < 1e-9) return p.RatePct;
            return null;
        }
        private static double? At(List<CurvePoint> pts, int index)
        {
            foreach (var p in pts) if (Math.Abs(p.Years - index) < 1e-9) return p.RatePct;
            return null;
        }
    }
}
