using System.Globalization;
using System.Text;
using System.Text.Json;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly.Core.Render
{
    /// <summary>Design tokens. The three curve lines follow the EMPHASIS pattern: today is the
    /// point of the chart and wears the highest-contrast ink; 1w and 1m are context and take two
    /// strongly separated hues. A one-hue ramp was tried first and rejected on the desk's read —
    /// the shades were too close to tell apart at a glance when the lines nearly overlap.
    /// The two context hues were validated (validate_palette.py) in both modes: adjacent CVD
    /// ΔE 9.2 light / 9.4 dark and normal-vision ΔE 27.6 / 26.5, comfortably clear of the gates.
    /// The light-mode aqua sits at 2.74:1 on the surface, which the always-visible table satisfies
    /// as relief. Do not hand-tweak these without re-running the validator.
    ///
    /// CHANGE cells are a separate, deliberately non-accessible choice: GREEN = higher yield,
    /// RED = lower, matching the desk's existing Dodgeball weekly email (desk call 2026-08-05).
    /// Green/red is the worst pair for colour-blind readers, so the sign is ALWAYS printed in the
    /// text too ("+4.2" / "-1.8") — colour never carries the direction on its own.</summary>
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
              --rw-today:#0b0b0b; --rw-week:#eb6834; --rw-month:#1baf7a;
              --rw-up:#1a7f37; --rw-down:#c5342f; --rw-flat:#f0efec;
            }
            @media (prefers-color-scheme:dark){
              :root:where(:not([data-theme=light])){
                color-scheme:dark;
                --rw-surface:#1a1a19; --rw-plane:#0d0d0d;
                --rw-ink:#fff; --rw-ink2:#c3c2b7; --rw-muted:#898781;
                --rw-grid:#2c2c2a; --rw-axis:#383835; --rw-border:rgba(255,255,255,.10);
                --rw-today:#ffffff; --rw-week:#d95926; --rw-month:#199e70;
                --rw-up:#3fb950; --rw-down:#f07470; --rw-flat:#383835;
              }
            }
            :root[data-theme=dark]{
              color-scheme:dark;
              --rw-surface:#1a1a19; --rw-plane:#0d0d0d;
              --rw-ink:#fff; --rw-ink2:#c3c2b7; --rw-muted:#898781;
              --rw-grid:#2c2c2a; --rw-axis:#383835; --rw-border:rgba(255,255,255,.10);
              --rw-today:#ffffff; --rw-week:#d95926; --rw-month:#199e70;
              --rw-up:#3fb950; --rw-down:#f07470; --rw-flat:#383835;
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
}
