using RateDesk.Core;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>WHEN a run's marks count as the close (desk 2026-08-25). London wall clock:
    ///
    ///   before 15:30      — PRE-CLOSE: the run works, marks are live mids, and a CHECK note
    ///                       flags it (nothing stops an early run on a half-day, but nobody
    ///                       should mistake a morning snap for a close);
    ///   15:30 – 16:14:59  — TOLERANCE BAND: the current mid IS saved as the close, as pressed;
    ///   16:15 onwards     — the published marks are the 16:15-London snap (from intraday
    ///                       bars), not the live mid at press time — pressing at 17:00 must not
    ///                       publish 17:00 marks as the close. Tickers without bars fall back
    ///                       to the live mid, counted and logged.
    ///
    /// Applied by overwriting the snapshot mids in place, so every downstream surface (boards,
    /// fronts, blast, workbooks, save-down books, inflation marks) rides the same numbers.</summary>
    public static class SnapDiscipline
    {
        public enum Mode { PreClose, LiveAsClose, Snap1615 }

        public static readonly TimeSpan BandStart = new(15, 30, 0);
        public static readonly TimeSpan SnapAt = new(16, 15, 0);

        public static Mode Resolve(DateTime londonNow) =>
            londonNow.TimeOfDay < BandStart ? Mode.PreClose
            : londonNow.TimeOfDay < SnapAt ? Mode.LiveAsClose
            : Mode.Snap1615;

        /// <summary>Apply the discipline to a fresh snapshot. Returns the mode and, for a
        /// pre-close run, the CHECK note the report should carry (the app pops CHECK notes).
        /// <paramref name="nowLondon"/>: the run's ONE London clock read (audit 2026-08-31,
        /// scenario 101) — a builder that reads LondonNow() here and again for MarksAsOfLondon
        /// can straddle midnight and stamp the gate a day after its own prices; the caller
        /// resolves the clock once and passes the same value everywhere.</summary>
        public static (Mode Mode, string? Note) Apply(IHistoryProvider bars, RatesSnapshot snap,
            IEnumerable<string> tickers, Action<string>? log = null, DateTime? nowLondon = null)
        {
            var now = nowLondon ?? DecisionClock.LondonNow();
            var mode = Resolve(now);
            switch (mode)
            {
                case Mode.PreClose:
                    var note = $"{OutlierGuard.Prefix}: PRE-CLOSE RUN — London {now:HH:mm}, " +
                               "marks are live mids, not closes — verify before distribution";
                    log?.Invoke("! " + note);
                    return (mode, note);

                case Mode.LiveAsClose:
                    log?.Invoke($"snap: London {now:HH:mm} — inside the 15:30-16:15 band, " +
                                "live mids save as the close");
                    return (mode, null);

                default:
                    int snapped = 0, fallback = 0;
                    foreach (var t in tickers.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var q = snap.Get(t);
                        if (q?.Mid is null) continue;   // never quoted — nothing to pin
                        double? v = null;
                        try
                        {
                            // compare LONDON date to LONDON date — bars are keyed on the London
                            // calendar, and a machine east of London is already on tomorrow's
                            // LOCAL date at 16:15 London, which silently unpinned every mark
                            // (audit 2026-08-26)
                            var s = bars.GetLondonSnaps(t, 4, SnapAt);
                            if (s.Count > 0 && s[^1].Date.Date == now.Date) v = s[^1].Value;
                        }
                        catch { /* bars unavailable — fall back below */ }
                        if (v is { } sv)
                        {
                            snap.Update(t, sv, sv, sv);
                            snapped++;
                        }
                        else fallback++;
                    }
                    log?.Invoke($"snap: London {now:HH:mm} — marks pinned to the 16:15 snap " +
                                $"({snapped} ticker(s); {fallback} without bars stay live)");
                    // a partially-pinned board mixes 16:15 marks with press-time mids in ONE
                    // Priced column (audit 2026-08-31, scenario 104) — said in the run notes,
                    // not only the log. Informational, not CHECK: far rungs without a trade
                    // today are routine and a daily modal trains the desk to click through.
                    return (mode, fallback > 0
                        ? $"SNAP: {fallback} published ticker(s) had no 16:15 bar today — their " +
                          $"marks are live at press time, beside {snapped} pinned to 16:15"
                        : null);
            }
        }
    }
}
