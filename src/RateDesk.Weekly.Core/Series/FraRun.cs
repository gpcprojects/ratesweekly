using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core.Render;

namespace RateDesk.Weekly.Core.Series
{
    /// <summary>Quoted FRA strips — the front-end run the desk actually trades.
    ///
    /// Two shapes exist in config and they must not be confused (a Dodgeball lesson): a tenor like
    /// "3X6" is a ROLLING month pair, while a plain "3M" pillar is an IMM CONTRACT whose real date
    /// comes from the security's MATURITY. Both are FORWARDS, never par levels.
    ///
    /// The IMM strips are positional generics that roll quarterly, so a 1-month lookback straddles
    /// a roll about a third of the time and a naive same-ticker difference books the inter-contract
    /// step as a market move. Those runs therefore go through <see cref="RollingStrip"/> with the
    /// IMM dates as roll boundaries; the rolling-month strips (CZK/HUF/PLN/EUR "{root}FR0AG" style)
    /// do NOT roll positionally and are read directly.</summary>
    public static class FraRun
    {
        /// <summary>Third Wednesday IMM dates spanning the lookback window plus the strip's reach.</summary>
        private static IEnumerable<DateTime> ImmDates(DateTime around, int backMonths = 6, int fwdMonths = 36)
        {
            var start = new DateTime(around.Year, around.Month, 1).AddMonths(-backMonths);
            for (int i = 0; i <= backMonths + fwdMonths; i++)
            {
                var m = start.AddMonths(i);
                if (m.Month % 3 != 0) continue;           // Mar/Jun/Sep/Dec
                var d = new DateTime(m.Year, m.Month, 1);
                int wed = ((int)DayOfWeek.Wednesday - (int)d.DayOfWeek + 7) % 7;
                yield return d.AddDays(wed + 14);          // third Wednesday
            }
        }

        public static (List<LadderPoint> Rows, List<string> Notes) Build(
            CurrencyConfig cfg, string src, HistoryStore store, DateTime asOf)
        {
            var notes = new List<string>();
            var curve = cfg.Irs?.Curve ?? cfg.Ois?.Curve;
            if (curve == null) return (new(), notes);

            var fras = curve.Where(p => p.Enabled &&
                            p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)).ToList();
            if (fras.Count == 0) return (new(), notes);

            // A positional IMM strip is one whose tenors are not rolling "AxB" month pairs.
            bool positional = fras.All(p => !p.Tenor.Contains('X', StringComparison.OrdinalIgnoreCase));

            if (!positional)
            {
                var rows = new List<LadderPoint>();
                foreach (var p in fras)
                {
                    var tk = ConfigStore.ResolveTicker(p.Ticker, src);
                    var now = store.ValueAsOf(tk, asOf);
                    if (now is null) continue;
                    rows.Add(new LadderPoint(p.Tenor.ToUpperInvariant(), now,
                        store.ValueAsOf(tk, asOf.AddDays(-WeeklyCurves.WeekDays)),
                        store.ValueAsOf(tk, asOf.AddDays(-WeeklyCurves.MonthDays))));
                }
                notes.Add("rolling month pairs (e.g. 1X7) — these do not roll positionally, so changes are like-for-like");
                return (rows, notes);
            }

            // Positional IMM contracts: resolve each slot's contract date, then read history
            // through the roll-aware lookup so a quarterly roll inside the window can't be booked
            // as a market move.
            var tickers = fras.Select(p => ConfigStore.ResolveTicker(p.Ticker, src)).ToList();
            var contracts = new List<(string, DateTime)>();
            var imms = ImmDates(asOf).ToList();
            var future = imms.Where(d => d > asOf).OrderBy(d => d).ToList();
            for (int i = 0; i < tickers.Count && i < future.Count; i++)
                contracts.Add((future[i].ToString("MMM-yy"), future[i]));

            if (contracts.Count == 0) return (new(), notes);

            var strip = RollingStrip.Build($"{cfg.Ccy} FRA strip", store, asOf, contracts, imms,
                n => n - 1 < tickers.Count ? tickers[n - 1] : tickers[^1], tickers.Count);
            notes.AddRange(strip.Notes);
            notes.Add("quoted IMM contracts; slots are positional generics, so lookbacks follow the contract, not the ticker");
            return (Panels.From(strip), notes);
        }
    }
}
