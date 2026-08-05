using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;

namespace RateDesk.Core.Trades
{
    /// <summary>
    /// Desk-style trade command parser. Examples:
    ///   "usd 5y"                      spot 5Y USD (default product), par
    ///   "aud m26 5y pay 100m"         AUD IMM Jun-26 start 5Y payer, 100mm
    ///   "eur 1y5y rec @ 2.85"         EUR 1Y-forward 5Y receiver at 2.85%
    ///   "gbp 10y 250k ois"            explicit product; bare "250k" is a NOTIONAL here
    ///   "usd 5y $25k"                 size to a 25,000 USD dv01 (also "dv01:25k", "dv01 25k", "€25k")
    ///   "eur 3x6 fra"                 FRA
    ///   "aud 5y 3s"                   force 3M BBSW leg
    ///   "cad 10y src:BMOD"            pricing source override
    /// </summary>
    public static class CommandParser
    {
        private static readonly Regex TenorRx = new(@"^(\d+)(d|w|m|y|p)$", RegexOptions.IgnoreCase);
        private static readonly Regex FwdTenorRx = new(@"^(\d+)(m|y)(\d+)(m|y)$", RegexOptions.IgnoreCase);
        private static readonly Regex FraRx = new(@"^(\d{1,2})x(\d{1,3})$", RegexOptions.IgnoreCase);
        // shared with QueryParser via SizingTokens so the two grammars can't drift apart
        private static Regex NotionalRx => Query.SizingTokens.NotionalRx;
        private static readonly Regex RateRx = new(@"^@?(\d+(?:\.\d+)?)%?$", RegexOptions.IgnoreCase);
        private static readonly Regex SourceRx = new(@"^src[:=](\w+)$", RegexOptions.IgnoreCase);
        private static readonly Regex IdxOverrideRx = new(@"^([136])s$", RegexOptions.IgnoreCase);
        private static readonly Regex ImmTenorRx = new(@"^(?:imm)?([hmuz]\d{2})[-\s]?(\d+[my])$", RegexOptions.IgnoreCase);

        public static TradeSpec Parse(string command, ConfigStore configs)
        {
            var spec = new TradeSpec();
            var tenors = new List<Period>();
            bool rateSet = false;
            bool pendingRisk = false;   // spaced "dv01 25k"

            var tokens = command.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var raw in tokens)
            {
                var t = raw.Trim();

                // ----- dv01 sizing, shared vocabulary with the analytics query bar -----
                // bareKIsRisk:false — "gbp 10y 250k" has always meant a 250,000 notional on this
                // grammar, and reinterpreting it as a 250,000 dv01 would silently turn a tiny trade
                // into a huge one. Risk needs an explicit marker here.
                if (Query.SizingTokens.IsRiskWord(t)) { pendingRisk = true; continue; }
                if (pendingRisk)
                {
                    if (Query.SizingTokens.TryRisk(t, out var pv, out var pc, bareKIsRisk: true))
                    { spec.Dv01Target = pv; if (pc != null) spec.Dv01Ccy = pc; }
                    else if (double.TryParse(t, out var pbare)) spec.Dv01Target = pbare;
                    else throw new FormatException("dv01 needs a size, e.g. \"dv01 25k\".");
                    pendingRisk = false;
                    continue;
                }
                if (Query.SizingTokens.TryRisk(t, out var rv, out var rc, bareKIsRisk: false))
                {
                    spec.Dv01Target = rv;
                    if (rc != null) spec.Dv01Ccy = rc;
                    continue;
                }

                // currency
                if (t.Length == 3 && configs.TryGet(t, out var cfg)) { spec.Ccy = cfg.Ccy; continue; }

                // product keywords
                switch (t.ToLowerInvariant())
                {
                    case "ois": spec.Product = ProductKind.OIS; continue;
                    case "irs": case "swap": spec.Product = ProductKind.IRS; continue;
                    case "fra": spec.Product = ProductKind.FRA; continue;
                    case "pay": case "payer": case "p": spec.PayFixed = true; continue;
                    case "rec": case "receive": case "receiver": case "r": spec.PayFixed = false; continue;
                    case "spot": spec.StartKind = StartKind.Spot; continue;
                }

                // combined IMM+tenor: "m26-5y" / "M26 5y" handled via tokens too
                var mit = ImmTenorRx.Match(t);
                if (mit.Success && ImmUtil.TryParse(mit.Groups[1].Value, out var immD1))
                {
                    spec.StartKind = StartKind.Imm;
                    spec.ImmDate = immD1;
                    spec.ImmCode = mit.Groups[1].Value.ToUpperInvariant();
                    tenors.Add(TenorUtil.Parse(mit.Groups[2].Value));
                    continue;
                }

                // IMM code alone
                if (ImmUtil.TryParse(t, out var immD))
                {
                    spec.StartKind = StartKind.Imm;
                    spec.ImmDate = immD;
                    spec.ImmCode = t.ToUpperInvariant().Replace("IMM", "");
                    continue;
                }

                // FRA "3x6"
                var mfra = FraRx.Match(t);
                if (mfra.Success)
                {
                    spec.Product = ProductKind.FRA;
                    spec.FraStartMonths = int.Parse(mfra.Groups[1].Value);
                    spec.FraEndMonths = int.Parse(mfra.Groups[2].Value);
                    continue;
                }

                // combined forward tenor "1y5y" / "18m10y"
                var mfwd = FwdTenorRx.Match(t);
                if (mfwd.Success)
                {
                    tenors.Add(TenorUtil.Parse(mfwd.Groups[1].Value + mfwd.Groups[2].Value));
                    tenors.Add(TenorUtil.Parse(mfwd.Groups[3].Value + mfwd.Groups[4].Value));
                    continue;
                }

                // notional vs month-tenor ambiguity: "100m" = 100mm notional, "18m" = 18 months.
                // Rule: bare "Nm" with N <= 36 is a tenor; "mm"/"k"/"b" and larger "Nm" are notional.
                var mn = NotionalRx.Match(t);
                if (mn.Success)
                {
                    double v = double.Parse(mn.Groups[1].Value);
                    var suffix = mn.Groups[2].Value.ToLowerInvariant();
                    bool ambiguousMonthTenor = suffix == "m" && v <= 36 && v == Math.Floor(v);
                    if (!ambiguousMonthTenor)
                    {
                        spec.Notional = Query.SizingTokens.SizeSuffix(v, suffix);
                        spec.ExplicitNotional = spec.Notional;   // typed, not the flat default
                        continue;
                    }
                }

                // simple tenor
                var mt = TenorRx.Match(t);
                if (mt.Success) { tenors.Add(TenorUtil.Parse(t)); continue; }

                // float index tenor override "3s"/"6s"/"1s"
                var mi = IdxOverrideRx.Match(t);
                if (mi.Success)
                {
                    spec.FloatTenorOverride = new Period(int.Parse(mi.Groups[1].Value), TimeUnit.Months);
                    continue;
                }

                // pricing source
                var msrc = SourceRx.Match(t);
                if (msrc.Success) { spec.Source = msrc.Groups[1].Value.ToUpperInvariant(); continue; }

                // rate (requires @ prefix or % suffix, or a decimal point to disambiguate from tenor counts)
                var mr = RateRx.Match(t);
                if (mr.Success && (t.StartsWith("@") || t.EndsWith("%") || t.Contains('.')))
                {
                    spec.FixedRate = double.Parse(mr.Groups[1].Value) / 100.0;
                    rateSet = true;
                    continue;
                }

                throw new FormatException($"Cannot parse token '{t}' in \"{command}\"");
            }

            if (pendingRisk)
                throw new FormatException("dv01 needs a size, e.g. \"dv01 25k\".");
            if (spec.ExplicitNotional.HasValue && spec.Dv01Target.HasValue)
                throw new FormatException("Give a notional OR a dv01, not both.");
            if (string.IsNullOrEmpty(spec.Ccy))
                throw new FormatException("No currency recognised. Start with a currency code, e.g. \"usd 5y\".");

            // tenor assembly: 2 tenors = forward start + tenor; 1 = spot tenor
            if (spec.Product != ProductKind.FRA)
            {
                if (tenors.Count == 2)
                {
                    if (spec.StartKind == StartKind.Imm)
                        throw new FormatException("Give either an IMM start or a forward start, not both.");
                    spec.StartKind = StartKind.Forward;
                    spec.ForwardStart = tenors[0];
                    spec.Tenor = tenors[1];
                }
                else if (tenors.Count == 1)
                {
                    spec.Tenor = tenors[0];
                }
                else if (tenors.Count == 0)
                {
                    throw new FormatException("No tenor given (e.g. \"5y\").");
                }
                else
                {
                    throw new FormatException("Too many tenors.");
                }
            }

            _ = rateSet;
            return spec;
        }
    }
}
