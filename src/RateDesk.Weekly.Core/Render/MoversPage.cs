using System.Globalization;
using System.Text;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>The Movers Summary hub — the site's landing page (index.html; the shared nav has
    /// linked it there since the nav existed). Two sections, DM and EM, each led by three hero
    /// cards (the biggest outsized movers, with a sparkline and the stats a PM reaches for) over
    /// a pure-ranking table. Same shell, tokens and table idiom as the currency pages.</summary>
    public static class MoversPage
    {
        public static string Build(MoversResult mv)
            => Page.Shell("DRAX Swaps — Weekly Rates Analysis — Movers", "movers",
                "DRAX Swaps - Weekly Rates Analysis - Movers", Body(mv));

        /// <summary>The hub's panels without the shell. <paramref name="href"/> resolves an
        /// instrument's link target — its currency page file by default; hash anchors in the
        /// single-file edition.</summary>
        public static string Body(MoversResult mv, Func<Mover, string>? href = null)
        {
            href ??= m => m.PageFile;
            var body = new StringBuilder();

            // NO blurb — desk rule 2026-08-11 (same discipline as dodgeball's weekly): no week
            // line, no G3/method paragraph, no gate counts, no pending-feature panel. Operational
            // notes go to the CLI; the methodology lives in DESIGN.md §5. Do not re-add any of it
            // without the desk asking. The two sections are ordinary panels, so the shared
            // auto-fit grid tiles DM and EM side by side on a widescreen and stacks them narrow.
            body.Append(Section("DM — outsized movers on the week", mv.DmHeroes, mv.DmRanked, href));
            body.Append(Section("EM — outsized movers on the week (EM · LATAM · ASIA EM)",
                mv.EmHeroes, mv.EmRanked, href));

            return body.ToString();
        }

        private static string Section(string title, List<Mover> heroes, List<Mover> ranked,
            Func<Mover, string> href)
        {
            var sb = new StringBuilder();
            sb.Append($"<section class=\"rw-panel\"><header class=\"rw-panel-head\"><h3>{Viz.Esc(title)}</h3></header>");

            if (ranked.Count == 0)
            {
                sb.Append("<div class=\"rw-empty\">no instruments passed the data gates for this section</div></section>");
                return sb.ToString();
            }

            sb.Append("<div class=\"rw-heroes\">");
            foreach (var m in heroes) sb.Append(Hero(m, href));
            sb.Append("</div>");

            var heroSet = new HashSet<Mover>(heroes);
            var rest = ranked.Where(m => !heroSet.Contains(m)).Take(12).ToList();
            if (rest.Count > 0)
            {
                sb.Append("<table class=\"rw-mv\"><thead><tr>")
                  .Append("<th class=\"l\">#</th><th class=\"l\">instrument</th><th class=\"l\">type</th>")
                  .Append("<th>level</th><th>1w bp</th><th>z</th><th>1m bp</th><th>wk vol</th><th>45d range</th>")
                  .Append("</tr></thead><tbody>");
                int rank = heroes.Count;
                foreach (var m in rest)
                {
                    rank++;
                    sb.Append("<tr>")
                      .Append($"<td class=\"l\">{rank}</td>")
                      .Append($"<td class=\"l\"><a href=\"{href(m)}\">{Viz.Esc(m.Label)}</a></td>")
                      .Append($"<td class=\"l\"><span class=\"rw-kind\">{Viz.Esc(m.Kind)}</span></td>")
                      .Append($"<td>{Viz.Esc(m.LevelText)}</td>")
                      .Append(Bp(m.W1Bp))
                      .Append($"<td>{ZText(m)}</td>")
                      .Append(m.M1Bp is { } m1 ? Bp(m1) : "<td class=\"rw-nil\">—</td>")
                      .Append(m.VolRatio is { } vr
                          ? $"<td>{vr.ToString("0.0", CultureInfo.InvariantCulture)}×</td>"
                          : "<td class=\"rw-nil\">—</td>")
                      .Append($"<td>{Viz.Esc(m.RangeText)}</td>")
                      .Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");
            return sb.ToString();
        }

        private static string Hero(Mover m, Func<Mover, string> href)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"rw-hero\">");
            sb.Append("<div class=\"rw-hero-top\">")
              .Append($"<a class=\"rw-hero-name\" href=\"{href(m)}\">{Viz.Esc(m.Label)}</a>")
              .Append($"<span class=\"rw-kind\">{Viz.Esc(m.Kind)}</span></div>");

            string cls = m.W1Bp > 0 ? "rw-upbp" : m.W1Bp < 0 ? "rw-downbp" : "rw-flatbp";
            sb.Append($"<div class=\"rw-hero-move\"><b class=\"{cls}\">{m.W1Bp.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}bp</b>")
              .Append($" on the week · <b>{Math.Abs(m.Z).ToString("0.0", CultureInfo.InvariantCulture)}σ</b>")
              .Append(m.ZIsEst ? "<span class=\"rw-est\">est</span>" : "")
              .Append("</div>");

            sb.Append(Spark(m));

            sb.Append("<div class=\"rw-stat-row\">")
              .Append($"<div class=\"rw-stat\"><b>{Viz.Esc(m.LevelText)}</b><span>level</span></div>")
              .Append(m.M1Bp is { } m1
                  ? $"<div class=\"rw-stat\"><b>{m1.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}</b><span>1m bp</span></div>" : "")
              .Append(m.VolRatio is { } vr
                  ? $"<div class=\"rw-stat\"><b>{vr.ToString("0.0", CultureInfo.InvariantCulture)}×</b><span>wk vol vs norm</span></div>" : "")
              .Append($"<div class=\"rw-stat\"><b>{Viz.Esc(m.RangeText)}</b><span>45d range</span></div>")
              .Append("</div></div>");
            return sb.ToString();
        }

        /// <summary>Static sparkline: the stored window with the last 7 calendar days shaded, so
        /// the size of the week reads against the run-up at a glance. No hover — at this size the
        /// numbers beside it are the reading surface.</summary>
        private static string Spark(Mover m)
        {
            var pts = m.Spark;
            if (pts.Count < 2) return "";
            const int W = 340, H = 64, ml = 2, mr = 2, mt = 4, mb = 4;
            double lo = pts.Min(p => p.Value), hi = pts.Max(p => p.Value);
            if (hi - lo < 1e-9) { hi += 0.5; lo -= 0.5; }
            double pad = (hi - lo) * 0.06;
            lo -= pad; hi += pad;
            var t0 = pts[0].Date;
            double span = Math.Max(1, (pts[^1].Date - t0).TotalDays);
            double SX(DateTime d) => ml + (d - t0).TotalDays / span * (W - ml - mr);
            double SY(double v) => mt + (hi - v) / (hi - lo) * (H - mt - mb);

            var sb = new StringBuilder();
            sb.Append($"<svg class=\"rw-spark\" viewBox=\"0 0 {W} {H}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"last 45 days\">");

            var wkStart = m.Spark[^1].Date.AddDays(-WeeklyCurves.WeekDays);
            if (wkStart > t0)
            {
                double x = SX(wkStart);
                sb.Append($"<rect x=\"{Viz.F(x, 1)}\" y=\"0\" width=\"{Viz.F(W - mr - x, 1)}\" height=\"{H}\" ")
                  .Append("fill=\"var(--rw-week)\" fill-opacity=\"0.10\"/>");
            }

            var d = new StringBuilder("M");
            foreach (var p in pts)
                d.Append(Viz.F(SX(p.Date), 1)).Append(' ').Append(Viz.F(SY(p.Value), 1)).Append(" L");
            d.Length -= 2; // trailing " L"
            sb.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"var(--rw-today)\" stroke-width=\"1.6\" ")
              .Append("stroke-linejoin=\"round\" stroke-linecap=\"round\" vector-effect=\"non-scaling-stroke\"/>");
            sb.Append($"<circle cx=\"{Viz.F(SX(pts[^1].Date), 1)}\" cy=\"{Viz.F(SY(pts[^1].Value), 1)}\" r=\"2.6\" ")
              .Append("fill=\"var(--rw-week)\"/>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string Bp(double bp)
        {
            string cls = Math.Abs(bp) < 0.05 ? "rw-flatbp" : bp > 0 ? "rw-upbp" : "rw-downbp";
            return $"<td class=\"rw-bp {cls}\">{bp.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}</td>";
        }

        private static string ZText(Mover m) =>
            $"{Math.Abs(m.Z).ToString("0.0", CultureInfo.InvariantCulture)}σ" +
            (m.ZIsEst ? "<span class=\"rw-est\">est</span>" : "");
    }
}
