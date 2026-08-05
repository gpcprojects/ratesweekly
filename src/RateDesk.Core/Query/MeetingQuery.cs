using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QLNet;
using RateDesk.Core.Trades;

namespace RateDesk.Core.Query
{
    /// <summary>Meeting-dated query grammar: "jul fomc" (period rate), "jul sep fomc" (meeting
    /// spread), "jul sep dec boe" (fly), "usd jul fomc 5y" (a swap ANCHORED on the meeting date).
    /// CB keyword or currency code selects the run; month tokens (jan..dec, optional 2-digit year)
    /// select the meetings; an optional trailing tenor turns it into an anchored swap.</summary>
    public static class MeetingQuery
    {
        private static readonly Regex MonthRx = new(
            @"^(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*(\d{2})?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fed"] = "FOMC", ["fomc"] = "FOMC",
            ["boe"] = "MPC", ["mpc"] = "MPC",
            ["riks"] = "RIKSBANK", ["riksbank"] = "RIKSBANK",
            ["norge"] = "NORGES", ["norges"] = "NORGES",
        };

        private static readonly string[] MonthNames =
            { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>Trailing tenor of an anchor query ("usd jul fomc 5y"). Digit-led, where every
        /// month and CB token is letter-led — no shape overlap, so "jul sep boe" is provably
        /// unaffected. Space-separated only; there is no glued form to get wrong.</summary>
        private static readonly Regex TenorRx = new(@"^(\d+)(d|w|m|y)$", RegexOptions.IgnoreCase);

        /// <param name="tokens">Grammar tokens ONLY — sizing tokens ("$25k", "100mm") must already be
        /// stripped by <see cref="SizingTokens.Extract"/>. Passing them in used to fail the whole
        /// parse on the first size token, after which QueryParser blamed the month instead.</param>
        /// <param name="tenor">Set when a trailing tenor turns this into a meeting-ANCHORED swap
        /// rather than a meeting-period rate.</param>
        public static bool TryParse(IReadOnlyList<string> tokens, out string runName,
            out List<(int Month, int? Year)> months, out string ccy, out Period? tenor)
        {
            runName = "";
            ccy = "";
            tenor = null;
            months = new List<(int, int?)>();
            // 2..4 grammar tokens, +1 for an optional trailing tenor. "jul sep dec boe" was already at
            // the old cap of 4, leaving no headroom for the tenor at all.
            if (tokens.Count < 2 || tokens.Count > 5) return false;

            string? run = null;
            for (int ti = 0; ti < tokens.Count; ti++)
            {
                var tok = tokens[ti].ToLowerInvariant();

                if (ti == tokens.Count - 1 && TenorRx.IsMatch(tok))
                {
                    tenor = Dates.TenorUtil.Parse(tok);
                    continue;
                }

                // CB name / alias / run name
                var name = Aliases.TryGetValue(tok, out var al) ? al : tok.ToUpperInvariant();
                var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                    s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                // currency code selects that ccy's run
                sched ??= tok.Length == 3
                    ? MeetingsStore.Schedules.FirstOrDefault(s => s.Ccy.Equals(tok, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (sched != null && !MonthRx.IsMatch(tok))
                {
                    if (run != null && run != sched.Name) return false;
                    run = sched.Name;
                    continue;
                }

                var m = MonthRx.Match(tok);
                if (!m.Success) return false; // unknown token -> not a meeting query
                int month = Array.IndexOf(MonthNames, m.Groups[1].Value.ToLowerInvariant()) + 1;
                int? year = m.Groups[2].Success ? 2000 + int.Parse(m.Groups[2].Value) : null;
                months.Add((month, year));
            }
            if (run == null || months.Count is < 1 or > 3) return false;
            // an anchored swap has ONE anchor by definition — "jul sep fomc 5y" is a semantic error,
            // not something to misparse into a spread of 5y swaps
            if (tenor != null && months.Count != 1)
                throw new FormatException(
                    $"A meeting-anchored swap takes one meeting, not {months.Count} — " +
                    "drop the tenor for a meeting spread/fly, or name a single meeting.");
            runName = run;
            ccy = MeetingsStore.Schedules.First(s => s.Name == run).Ccy.ToUpperInvariant();
            return true;
        }
    }
}
