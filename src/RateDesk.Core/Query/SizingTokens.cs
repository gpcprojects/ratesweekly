using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RateDesk.Core.Query
{
    /// <summary>What a query's sizing tokens asked for. Null fields mean "not specified".</summary>
    public sealed class SizingRequest
    {
        public double? Dv01 { get; init; }
        public double? Notional { get; init; }
        /// <summary>Currency the dv01 was expressed in ("$25k" -> USD). USD by desk default.</summary>
        public string Dv01Ccy { get; init; } = "USD";
        public bool Any => Dv01.HasValue || Notional.HasValue;
    }

    /// <summary>The desk's sizing vocabulary, in ONE place.
    ///
    /// <para><see cref="Query.QueryParser"/> and <see cref="Trades.CommandParser"/> both accept
    /// "$25k"/"dv01:25k"/"100mm", and they used to carry private copies of these regexes that could
    /// drift apart. The regexes live here now; each parser still applies its own token-consumption
    /// rules, because QueryParser is deliberately context-sensitive about the tenor-vs-notional
    /// ambiguity of "18m" (a tenor after an IMM leg, millions elsewhere) and the ticket/meeting
    /// grammars have no such ambiguity to resolve.</para>
    ///
    /// <para>Desk conventions encoded here: a bare "25k" is a USD DV01 (k-amounts are risk, m/b are
    /// notionals); a currency symbol or 3-letter tag on either side of the number names the DV01
    /// currency ("€25k", "25keur"); "dv01"/"risk" as a standalone word takes the NEXT token as its
    /// size ("dv01 25k").</para></summary>
    public static class SizingTokens
    {
        /// <summary>"dv01:25k" / "risk=eur25k" / "01:25".</summary>
        public static readonly Regex Dv01Rx =
            new(@"^(?:dv01|risk|01)[:=]([a-z]{3})?(\d+(?:\.\d+)?)(k|mm|mio|m)?$", RegexOptions.IgnoreCase);
        /// <summary>Currency-PREFIXED risk: "$25k", "€1m", "jpy25k".</summary>
        public static readonly Regex CcyRiskRx =
            new(@"^([$€£¥]|[a-z]{3})(\d+(?:\.\d+)?)(k|mm|mio|m|b)?$", RegexOptions.IgnoreCase);
        /// <summary>Currency-SUFFIXED risk: "25keur".</summary>
        public static readonly Regex RiskSufRx =
            new(@"^(\d+(?:\.\d+)?)(k|mm|mio|m|b)([a-z]{3})$", RegexOptions.IgnoreCase);
        /// <summary>A plain size: "100mm" / "25k" / "1b". Whether it is a notional, a dv01 or a
        /// month-tenor is the calling parser's decision.</summary>
        public static readonly Regex NotionalRx =
            new(@"^(\d+(?:\.\d+)?)(k|mm|mio|m|b|bn)$", RegexOptions.IgnoreCase);

        public static readonly IReadOnlyDictionary<char, string> RiskSymbols = new Dictionary<char, string>
        {
            ['$'] = "USD", ['€'] = "EUR", ['£'] = "GBP", ['¥'] = "JPY",
        };

        private static readonly HashSet<string> MonthWords = new(StringComparer.OrdinalIgnoreCase)
        { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>"dv01" / "risk" standing alone — the size is the next token.</summary>
        public static bool IsRiskWord(string t) =>
            t.Equals("dv01", StringComparison.OrdinalIgnoreCase)
            || t.Equals("risk", StringComparison.OrdinalIgnoreCase);

        public static double SizeSuffix(double v, string suf) => suf.ToLowerInvariant() switch
        {
            "k" => v * 1e3, "m" or "mm" or "mio" => v * 1e6, "b" or "bn" => v * 1e9, _ => v,
        };

        /// <summary>A month word must never be read as a dv01 currency tag — "25kjul" is not a
        /// July-denominated risk, and meeting queries are full of month words.</summary>
        public static bool IsMonthWord(string t) => MonthWords.Contains(t);

        /// <summary>Risk (DV01) token? Recognises every prefixed/suffixed/tagged form, and a bare
        /// "25k" (USD by convention). Notionals in m/b are NOT risk and return false.</summary>
        /// <param name="bareKIsRisk">Apply the bare-"25k"-is-risk convention. False for the ticket
        /// grammar, where "gbp 10y 250k" has always meant a 250,000 NOTIONAL and silently
        /// reinterpreting it as a 250,000 dv01 would turn a tiny trade into a huge one. There, risk
        /// needs an explicit marker ("dv01:250k", "$250k").</param>
        public static bool TryRisk(string t, out double dv01, out string? ccy, bool bareKIsRisk = true)
        {
            dv01 = 0;
            ccy = null;

            var mdv = Dv01Rx.Match(t);
            if (mdv.Success)
            {
                if (mdv.Groups[1].Success) ccy = mdv.Groups[1].Value.ToUpperInvariant();
                dv01 = SizeSuffix(double.Parse(mdv.Groups[2].Value), mdv.Groups[3].Value);
                return true;
            }

            var msf = RiskSufRx.Match(t);
            if (msf.Success && !IsMonthWord(msf.Groups[3].Value))
            {
                ccy = msf.Groups[3].Value.ToUpperInvariant();
                dv01 = SizeSuffix(double.Parse(msf.Groups[1].Value), msf.Groups[2].Value);
                return true;
            }

            var mcr = CcyRiskRx.Match(t);
            if (mcr.Success)
            {
                var tag = mcr.Groups[1].Value;
                string? resolved = tag.Length == 1 && RiskSymbols.TryGetValue(tag[0], out var sym)
                    ? sym
                    : tag.Length == 3 && !IsMonthWord(tag) ? tag.ToUpperInvariant() : null;
                if (resolved != null)
                {
                    ccy = resolved;
                    dv01 = SizeSuffix(double.Parse(mcr.Groups[2].Value), mcr.Groups[3].Value);
                    return true;
                }
            }

            // bare "25k" — k-amounts are risk by desk convention, m/b are notionals
            var mn = NotionalRx.Match(t);
            if (bareKIsRisk && mn.Success && mn.Groups[2].Value.Equals("k", StringComparison.OrdinalIgnoreCase))
            {
                ccy = "USD";
                dv01 = SizeSuffix(double.Parse(mn.Groups[1].Value), mn.Groups[2].Value);
                return true;
            }
            return false;
        }

        /// <summary>Pull every sizing token out of a token list, leaving the grammar tokens in their
        /// ORIGINAL ORDER so a downstream parse error still names the real offender.
        ///
        /// <para>Used by grammars with no tenor-vs-notional ambiguity to resolve — the meeting query,
        /// where "25m" can only be a notional because the grammar has no tenor concept. QueryParser
        /// does NOT use this: it has to weigh each size token against the legs seen so far.</para>
        ///
        /// <para>A size-shaped but malformed token is left in <paramref name="remaining"/> rather than
        /// swallowed, so the caller's own error names it.</para></summary>
        public static SizingRequest Extract(IReadOnlyList<string> tokens, out List<string> remaining)
        {
            remaining = new List<string>();
            double? dv01 = null, notional = null;
            string? ccy = null;
            bool pendingRisk = false;

            void SetCcy(string c)
            {
                if (ccy != null && ccy != c)
                    throw new FormatException($"mixed dv01 currencies ({ccy} and {c}) — use one.");
                ccy = c;
            }

            foreach (var t in tokens)
            {
                if (IsRiskWord(t)) { pendingRisk = true; continue; }

                if (pendingRisk)
                {
                    // "dv01 25k" and "dv01 25" both size; anything else means the word was stray
                    if (TryRisk(t, out var pv, out var pc)) { dv01 = pv; if (pc != null) SetCcy(pc); }
                    else if (double.TryParse(t, out var bare)) dv01 = bare;
                    else throw new FormatException("dv01 needs a size, e.g. \"dv01 25k\".");
                    pendingRisk = false;
                    continue;
                }

                if (TryRisk(t, out var v, out var c))
                {
                    dv01 = v;
                    if (c != null) SetCcy(c);
                    continue;
                }

                var mn = NotionalRx.Match(t);
                if (mn.Success)
                {
                    notional = SizeSuffix(double.Parse(mn.Groups[1].Value), mn.Groups[2].Value);
                    continue;
                }

                remaining.Add(t);
            }
            if (pendingRisk) throw new FormatException("dv01 needs a size, e.g. \"dv01 25k\".");

            return new SizingRequest { Dv01 = dv01, Notional = notional, Dv01Ccy = ccy ?? "USD" };
        }
    }
}
