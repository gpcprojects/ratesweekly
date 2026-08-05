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
    /// Do not hand-tweak these without re-running the validator.
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
              --rw-today:#104281; --rw-week:#3987e5; --rw-month:#86b6ef;
              --rw-up:#1a7f37; --rw-down:#c5342f; --rw-flat:#f0efec;
            }
            @media (prefers-color-scheme:dark){
              :root:where(:not([data-theme=light])){
                color-scheme:dark;
                --rw-surface:#1a1a19; --rw-plane:#0d0d0d;
                --rw-ink:#fff; --rw-ink2:#c3c2b7; --rw-muted:#898781;
                --rw-grid:#2c2c2a; --rw-axis:#383835; --rw-border:rgba(255,255,255,.10);
                --rw-today:#cde2fb; --rw-week:#5598e7; --rw-month:#256abf;
                --rw-up:#3fb950; --rw-down:#f07470; --rw-flat:#383835;
              }
            }
            :root[data-theme=dark]{
              color-scheme:dark;
              --rw-surface:#1a1a19; --rw-plane:#0d0d0d;
              --rw-ink:#fff; --rw-ink2:#c3c2b7; --rw-muted:#898781;
              --rw-grid:#2c2c2a; --rw-axis:#383835; --rw-border:rgba(255,255,255,.10);
              --rw-today:#cde2fb; --rw-week:#5598e7; --rw-month:#256abf;
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
