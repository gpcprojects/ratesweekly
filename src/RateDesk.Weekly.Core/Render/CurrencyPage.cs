using System.Globalization;
using System.Text;
using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>Builds one currency's dashboard page from the history store.
    /// Sections auto-drop where the market doesn't exist (no meetings run, no inflation ladder),
    /// and sections that need more history than the store holds render an explicit "pending"
    /// panel — never a fabricated series.</summary>
    public static class CurrencyPage
    {
        public static string Build(CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf)
        {
            var body = new StringBuilder();
            var par = WeeklyCurves.ParCurve(cfg, src, store, asOf);
            var fwd = WeeklyCurves.AnnualForwards(par, cfg.Ccy);

            body.Append(Tiles(par, cfg.Ccy));
            body.Append("<div class=\"rw-grid2\">");
            body.Append(LineChart.FromTriple(
                "par", par, $"{cfg.Ccy} par curve",
                "swap rate by tenor · today vs 1 week and 1 month ago",
                "tenor", v => v >= 1 ? $"{v:0.#}y" : $"{v * 12:0}m"));
            body.Append(LineChart.FromTriple(
                "fwd", fwd, $"{cfg.Ccy} annual forwards",
                "1y1y … 9y1y, derived from the par ladder",
                "forward start", v => $"{v:0}y1y", xKind: "fwd"));
            body.Append("</div>");

            body.Append(Pending("Rolling correlations",
                "2y vs oil, and 10y vs US 10y and DXY, on a 63-day window over ~2 years",
                "needs ~2.5 years of history; the store currently holds ~1 month. " +
                "These charts light up automatically once the history is deepened."));

            if (par.Notes.Count > 0)
                body.Append($"<p class=\"rw-note\">{Viz.Esc(string.Join(" · ", par.Notes))}</p>");

            return Page.Shell(
                $"{cfg.Ccy} — RatesWeekly", cfg.Ccy, $"{cfg.Ccy}",
                $"close of {asOf:dddd d MMMM yyyy}", body.ToString(),
                DateTime.Now.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture));
        }

        /// <summary>Headline stat tiles: the number IS the chart for a single current value.</summary>
        private static string Tiles(CurveTriple par, string ccy)
        {
            var sb = new StringBuilder("<div class=\"rw-grid2\" style=\"margin-bottom:16px\"><div class=\"rw-card\">");
            sb.Append("<h3>Levels</h3><p class=\"rw-sub\">mid, % · change in bp vs 1 week ago</p>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;margin-top:8px;font-size:13px\">");
            sb.Append("<thead><tr><th style=\"text-align:left;padding:3px 6px;color:var(--rw-muted);font-size:11px\">tenor</th>")
              .Append("<th style=\"text-align:right;padding:3px 6px;color:var(--rw-muted);font-size:11px\">mid</th>")
              .Append("<th style=\"text-align:right;padding:3px 6px;color:var(--rw-muted);font-size:11px\">1w</th>")
              .Append("<th style=\"text-align:right;padding:3px 6px;color:var(--rw-muted);font-size:11px\">1m</th></tr></thead><tbody>");

            foreach (var want in new[] { 2.0, 5.0, 10.0, 30.0 })
            {
                var t = par.Today.FirstOrDefault(p => Math.Abs(p.Years - want) < 0.1);
                if (t.Label is null) continue;
                var w = par.Week.FirstOrDefault(p => Math.Abs(p.Years - want) < 0.1);
                var m = par.Month.FirstOrDefault(p => Math.Abs(p.Years - want) < 0.1);
                sb.Append($"<tr><td style=\"padding:3px 6px\">{Viz.Esc(t.Label)}</td>")
                  .Append($"<td style=\"text-align:right;padding:3px 6px;font-variant-numeric:tabular-nums;font-weight:600\">{Viz.F(t.RatePct)}</td>")
                  .Append(Bp(t, w)).Append(Bp(t, m)).Append("</tr>");
            }
            sb.Append("</tbody></table></div>");

            // curve shape tile
            sb.Append("<div class=\"rw-card\"><h3>Curve</h3><p class=\"rw-sub\">slope, bp · change vs 1 week ago</p>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;margin-top:8px;font-size:13px\"><tbody>");
            foreach (var (a, b, name) in new[] { (2.0, 10.0, "2s10s"), (5.0, 30.0, "5s30s") })
            {
                var sNow = Slope(par.Today, a, b); var sW = Slope(par.Week, a, b); var sM = Slope(par.Month, a, b);
                if (sNow is null) continue;
                sb.Append($"<tr><td style=\"padding:3px 6px\">{name}</td>")
                  .Append($"<td style=\"text-align:right;padding:3px 6px;font-variant-numeric:tabular-nums;font-weight:600\">{sNow.Value:+0.0;-0.0}</td>")
                  .Append(BpRaw(sNow, sW)).Append(BpRaw(sNow, sM)).Append("</tr>");
            }
            sb.Append("</tbody></table></div></div>");
            return sb.ToString();

            static double? Slope(List<CurvePoint> pts, double a, double b)
            {
                var pa = pts.FirstOrDefault(p => Math.Abs(p.Years - a) < 0.1);
                var pb = pts.FirstOrDefault(p => Math.Abs(p.Years - b) < 0.1);
                return pa.Label is null || pb.Label is null ? null : (pb.RatePct - pa.RatePct) * 100.0;
            }
            static string Bp(CurvePoint now, CurvePoint then) =>
                then.Label is null ? Cell(null) : Cell((now.RatePct - then.RatePct) * 100.0);
            static string BpRaw(double? now, double? then) =>
                now is null || then is null ? Cell(null) : Cell(now.Value - then.Value);
            static string Cell(double? bp)
            {
                if (bp is null)
                    return "<td style=\"text-align:right;padding:3px 6px;color:var(--rw-muted)\">—</td>";
                // diverging: warm = higher rate, cool = lower. Sign is also in the text, so the
                // colour is never the only carrier.
                string col = Math.Abs(bp.Value) < 0.5 ? "var(--rw-muted)"
                    : bp.Value > 0 ? "var(--rw-up)" : "var(--rw-down)";
                return $"<td style=\"text-align:right;padding:3px 6px;font-variant-numeric:tabular-nums;color:{col}\">"
                     + bp.Value.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "</td>";
            }
        }

        private static string Pending(string title, string sub, string why) =>
            $"<figure class=\"rw-card\" style=\"margin-top:16px\"><figcaption><h3>{Viz.Esc(title)}</h3>"
          + $"<p class=\"rw-sub\">{Viz.Esc(sub)}</p></figcaption>"
          + $"<div class=\"rw-pending\">{Viz.Esc(why)}</div></figure>";
    }
}
