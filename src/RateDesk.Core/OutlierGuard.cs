namespace RateDesk.Core
{
    /// <summary>Cross-sectional outlier flag on the meeting boards (desk 2026-08-25, prompted
    /// by the BOJ run whose Δ1m read +11/+4.9/+10.6 down the strip). A change column whose rows
    /// share the same window should move roughly together; one row far from the rest is either
    /// a real meeting-odds shift (the BOJ case: ~6bp of hike probability migrating Oct→Dec, the
    /// neighbouring steps mirror-imaged) or a bad anchor print — and nobody can tell which from
    /// the email. So the row is FLAGGED for a manual look before distribution, never suppressed:
    /// the numbers stay on the boards, the CHECK note names them.
    ///
    /// Threshold: |x − median| &gt; max(4bp, 4×MAD) with ≥4 populated rows — the MAD term keeps
    /// quiet days quiet (tiny wiggles never clear the 4bp floor) and volatile days honest (when
    /// the whole strip is jumpy, MAD grows and only true one-row breaks flag).</summary>
    public static class OutlierGuard
    {
        public const string Prefix = "CHECK";
        public const double FloorBp = 4.0;
        public const double MadMult = 4.0;

        /// <summary>Absolute-size flags (desk 2026-08-25, after the +65.7bp SEK phantom): a
        /// meeting-OIS change this large is rare enough that it ALWAYS deserves eyes, even when
        /// a whole strip moved together and the cross-sectional test can't see it.</summary>
        public const double AbsD1Bp = 12.0, AbsW1Bp = 30.0, AbsM1Bp = 50.0;

        public static List<string> Check(WeeklyReport rep)
        {
            // notes render in the CHECK popup and the log — INVARIANT like every other surface
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var notes = new List<string>();
            foreach (var run in rep.Runs)
            {
                var name = run.Title.Split('·')[0].Trim();
                foreach (var (label, sel, abs) in new (string, Func<WeeklyMeeting, double?>, double)[]
                         { ("Δ1d", m => m.D1Bp, AbsD1Bp), ("Δ1w", m => m.W1Bp, AbsW1Bp), ("Δ1m", m => m.M1Bp, AbsM1Bp) })
                {
                    var vals = run.Rows.Where(m => !m.TurnPeriod)
                        .Select(m => (m, v: sel(m)))
                        .Where(x => x.v.HasValue)
                        .Select(x => (x.m, v: x.v!.Value)).ToList();
                    // absolute-size flag first — fires even on a single row or a uniform strip
                    foreach (var (m, v) in vals)
                        if (Math.Abs(v) > abs)
                            notes.Add($"{Prefix}: {name} {m.Date.ToString("dd-MMM-yy", inv)} {label} " +
                                      $"{v.ToString("+0.0;-0.0;0.0", inv)}bp exceeds the {abs:0}bp sanity bar — " +
                                      "verify before distribution");
                    if (vals.Count < 4) continue;
                    // the FRONT row is EXCLUDED from the cross-sectional test (desk 2026-08-26,
                    // the RBNZ -1.0-vs--20.3 false flag): a front converging on the fixing
                    // legitimately decouples from the strip — its own pricer showed the same
                    // shape. The absolute bars above still cover it.
                    var front = run.Rows.FirstOrDefault(m => !m.TurnPeriod);
                    var body = vals.Where(x => !ReferenceEquals(x.m, front)).ToList();
                    if (body.Count < 3) continue;
                    double med = Median(body.Select(x => x.v));
                    double mad = Median(body.Select(x => Math.Abs(x.v - med)));
                    double thresh = Math.Max(FloorBp, MadMult * mad);
                    foreach (var (m, v) in body)
                        if (Math.Abs(v - med) > thresh && Math.Abs(v) <= abs)
                            notes.Add($"{Prefix}: {name} {m.Date.ToString("dd-MMM-yy", inv)} {label} " +
                                      $"{v.ToString("+0.0;-0.0;0.0", inv)}bp vs run median {med.ToString("+0.0;-0.0;0.0", inv)}bp — " +
                                      "verify before distribution");
                }
            }
            return notes;
        }

        /// <summary>The same flag for any labelled strip of changes (the inflation-fixings
        /// ladders once integrated take this path).</summary>
        public static List<string> CheckStrip(string stripName, string changeLabel,
            IEnumerable<(string RowLabel, double Value)> rows)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var vals = rows.ToList();
            var notes = new List<string>();
            if (vals.Count < 4) return notes;
            double med = Median(vals.Select(x => x.Value));
            double mad = Median(vals.Select(x => Math.Abs(x.Value - med)));
            double thresh = Math.Max(FloorBp, MadMult * mad);
            foreach (var (label, v) in vals)
                if (Math.Abs(v - med) > thresh)
                    notes.Add($"{Prefix}: {stripName} {label} {changeLabel} {v.ToString("+0.0;-0.0;0.0", inv)}bp vs " +
                              $"strip median {med.ToString("+0.0;-0.0;0.0", inv)}bp — verify before distribution");
            return notes;
        }

        private static double Median(IEnumerable<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
        }
    }
}
