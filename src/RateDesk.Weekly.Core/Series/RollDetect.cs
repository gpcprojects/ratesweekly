namespace RateDesk.Weekly.Core.Series
{
    /// <summary>Spots a positional ticker re-pointing by reading its own price history.
    ///
    /// The exact test for a roll is "did this security's MATURITY change", and where the store has
    /// maturities on file that is what callers use. But the store only began recording them
    /// recently, so for any window reaching further back there is nothing to compare against —
    /// and silently differencing across a roll is how a weekly ends up publishing a 96bp move that
    /// never happened (BPSWIF6 jumped 295.75 → 420.25 on 2026-07-22 when June RPI printed and the
    /// ticker re-pointed from Jun-2026 to Jun-2027).
    ///
    /// A roll is loud: the series steps by something far outside its own daily range. Requiring the
    /// jump to clear BOTH a multiple of the series' typical daily move AND a share of its own level
    /// keeps a quiet series from tripping on ordinary noise and a volatile one from hiding a roll.</summary>
    public static class RollDetect
    {
        /// <summary>Jump must exceed this multiple of the median absolute daily move…</summary>
        private const double NoiseMultiple = 25.0;

        /// <summary>…and this share of the series' own level.
        ///
        /// Both thresholds are deliberately severe, because the alternative failure is worse:
        /// suppressing a real change is a silent hole in the weekly. Calibrated against the real
        /// events in the store — the BPSWIF6 roll moved 42% of level and 83x the median step, while
        /// the 2026-07-22 RPI print repriced the whole GBP fixing strip by 2-3% of level and about
        /// 10x the median. At 8x/2% those repricings were flagged as rolls and their changes wrongly
        /// blanked; at 25x/15% only the roll trips.
        ///
        /// Note the limit: on an INDEX-quoted family (USD) a roll is only one year of accrual, ~2%,
        /// which no price test can separate from a busy day. That case is covered structurally
        /// instead — the one ticker that rolls each month is dropped from the window — and exactly,
        /// by maturity history, once the store has enough of it.</summary>
        private const double LevelShare = 0.15;

        public static bool LooksRolled(HistoryStore store, string ticker, DateTime from, DateTime to)
        {
            int span = (int)(DateTime.Today - from).TotalDays + 10;
            var win = store.GetDaily(ticker, Math.Max(10, span))
                           .Where(p => p.Date >= from.Date && p.Date <= to.Date)
                           .ToList();
            if (win.Count < 4) return false;

            var steps = new List<double>();
            for (int i = 1; i < win.Count; i++) steps.Add(Math.Abs(win[i].Value - win[i - 1].Value));
            if (steps.Count == 0) return false;

            var sorted = steps.OrderBy(d => d).ToList();
            double median = sorted[sorted.Count / 2];
            double biggest = steps.Max();
            double level = Math.Abs(win[^1].Value);
            if (level < 1e-9) return false;

            return biggest > NoiseMultiple * Math.Max(median, 1e-9)
                && biggest > LevelShare * level;
        }
    }
}
