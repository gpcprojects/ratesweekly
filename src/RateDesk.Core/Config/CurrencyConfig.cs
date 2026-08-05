using System.Collections.Generic;

namespace RateDesk.Core.Config
{
    /// <summary>Per-currency market conventions + curve definition. Loaded from config/currencies/*.json.</summary>
    public sealed class CurrencyConfig
    {
        public string Ccy { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        /// <summary>Calendar spec, e.g. "USD" or joint "GBP+USD".</summary>
        public string Calendar { get; set; } = "";
        public int SpotLag { get; set; } = 2;
        /// <summary>Quote age (minutes) at which this currency's prices are flagged as re-stamped
        /// rather than traded. 120 by default; a market whose session genuinely goes quiet for longer
        /// can be tuned here rather than in code. 0 disables the age test for that currency, leaving
        /// only the relative bid/ask width signal.</summary>
        public double StaleQuoteMinutes { get; set; } = 120;
        public string DefaultSource { get; set; } = "BGN";
        public List<string> AltSources { get; set; } = new();
        /// <summary>What a bare trade request means for this currency: "OIS" or "IRS".</summary>
        public string DefaultProduct { get; set; } = "OIS";
        /// <summary>"OIS" = discount on the OIS curve (cleared standard); "SELF" = single-curve self-discounting.</summary>
        public string Discounting { get; set; } = "OIS";
        public string Interpolation { get; set; } = "LogLinearDF";
        public List<string> Ccps { get; set; } = new();
        public OisConfig? Ois { get; set; }
        public IrsConfig? Irs { get; set; }
        /// <summary>Additional named quoted ladders (Fed Funds OIS, CPI/RPI/HICP inflation, BRL DI...).
        /// Surfaced on the analytics board via live quotes + history; not bootstrapped for DV01 in v1.</summary>
        public List<Ladder> Ladders { get; set; } = new();
        public string Notes { get; set; } = "";
    }

    /// <summary>A named quoted curve ladder used for the analytics/history board and outright-by-tenor lookups.</summary>
    public sealed class Ladder
    {
        public string Name { get; set; } = "";
        /// <summary>"RATE" (par/OIS swap rate) or "INFLATION" (zero-coupon breakeven).</summary>
        public string Kind { get; set; } = "RATE";
        /// <summary>Query aliases, e.g. ["ff","fedfunds"] or ["cpi","uscpi"].</summary>
        public List<string> Aliases { get; set; } = new();
        public string Dcc { get; set; } = "ACT/360";
        public string FixingTicker { get; set; } = "";
        /// <summary>Bloomberg FWCM forward-curve id ("S0042" for Fed Funds) for forward cross-checks.</summary>
        public string FwdCurveId { get; set; } = "";
        public List<PillarDef> Pillars { get; set; } = new();
        /// <summary>Dated-contract ticker pattern with {MY} = month letter + 2-digit year
        /// (BRL DI: "OD{MY} Comdty" -> ODF27). Enables jan27/f27-style queries.</summary>
        public string DatedPattern { get; set; } = "";
        /// <summary>Forward-point ticker pattern with {A}/{B} = start/tenor years
        /// (USD CPI: "FWISUS{A}{B} Index" -> FWISUS55). Used to cross-check implied forwards.</summary>
        public string FwdTickerPattern { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    public sealed class OisConfig
    {
        public string IndexName { get; set; } = "";
        public string OnFixingTicker { get; set; } = "";
        /// <summary>Day count of the overnight index itself (float leg accrual).</summary>
        public string IndexDcc { get; set; } = "ACT/360";
        /// <summary>"Annual","Semiannual","Quarterly","Every28Days". Both legs pay on this schedule.</summary>
        public string FixedFreq { get; set; } = "Annual";
        public string FixedDcc { get; set; } = "ACT/360";
        public string Bdc { get; set; } = "ModifiedFollowing";
        /// <summary>Payment delay in business days after period end (e.g. USD SOFR 2, GBP SONIA 0).</summary>
        public int PayLag { get; set; } = 0;
        public int FixingDays { get; set; } = 0;
        /// <summary>Tenors strictly below this pay a single coupon at maturity (standard OIS &lt; 1Y).</summary>
        public string ShortZeroCouponUnder { get; set; } = "1Y";
        /// <summary>Bloomberg forward-curve id for forward cross-checks: an FWCM id ("S0490") by default,
        /// or a year-pair security prefix ("EUSA") when FwdTickerStyle says so.</summary>
        public string FwdCurveId { get; set; } = "";
        /// <summary>"fwcm" (default) = "{id}FS {start}{tenor} BLC Curncy"; "yearPair" = "{id}0101 Curncy".
        /// MUST match the index basis of the pillars below — see ForwardTicker for why.</summary>
        public string FwdTickerStyle { get; set; } = "";
        public List<PillarDef> Curve { get; set; } = new();
        /// <summary>CCP/clearing variants of this curve built from quoted basis ladders (e.g. JPY JSCC vs LCH).</summary>
        public List<CurveVariantDef> Variants { get; set; } = new();
    }

    /// <summary>A curve variant defined by a basis ladder on top of the base curve:
    /// variantRate% = baseRate% + Sign * basisQuoteBp / 100.</summary>
    public sealed class CurveVariantDef
    {
        public string Name { get; set; } = "";
        /// <summary>+1 if the quote is (variant - base); -1 if quoted (base - variant).</summary>
        public double Sign { get; set; } = 1;
        /// <summary>Basis quotes in BASIS POINTS by tenor.</summary>
        public List<PillarDef> Pillars { get; set; } = new();
        public string Notes { get; set; } = "";
    }

    public sealed class IrsConfig
    {
        public string Bdc { get; set; } = "ModifiedFollowing";
        /// <summary>Bloomberg forward-curve id for forward cross-checks: an FWCM id ("S0232") by default,
        /// or a year-pair security prefix ("EUSA") when FwdTickerStyle says so.</summary>
        public string FwdCurveId { get; set; } = "";
        /// <summary>"fwcm" (default) or "yearPair" — see ForwardTicker. Must match the index basis the
        /// curve is bootstrapped from: EUR's pillars are EUSA (vs 6M), so its forwards are EUSA too.</summary>
        public string FwdTickerStyle { get; set; } = "";
        /// <summary>Separate FWCM id for the SHORT band (AUD q/q = S0303): used when the leg's
        /// tenor-rule convention IS the short band and the short ladder's quotes cover the leg's end
        /// (SwapBuilder.ShortBandMaxYears), or an explicit 3M index tag is given.</summary>
        public string FwdCurveIdShort { get; set; } = "";
        /// <summary>SUPERSEDED 2026-08-04 by ShortBandMaxYears (routing now follows the leg that
        /// actually prices). Parsed for config compatibility; no longer read.</summary>
        public double FwdShortMaxYears { get; set; } = 3.0;
        /// <summary>OIS-shaped synthetic front end, for ccys whose FRAs publish no API prices (NOK):
        /// forward IBOR = OIS forward + today's fixing spread, plus the quoted short-vs-long tenor
        /// basis strip for the long-index leg. Requires an OIS curve in the same config.</summary>
        public OisFrontDef? FrontFromOis { get; set; }
        /// <summary>Tenor-banded conventions ordered by MaxTenor; entry with null MaxTenor is the default band.
        /// e.g. AUD: &lt;=3Y quarterly fixed vs 3M BBSW, then semi vs 6M BBSW.</summary>
        public List<IrsLegConv> Legs { get; set; } = new();
        public List<PillarDef> Curve { get; set; } = new();
    }

    public sealed class IrsLegConv
    {
        /// <summary>Inclusive upper bound tenor for this band ("3Y"), null = no bound (default band).</summary>
        public string? MaxTenor { get; set; }
        public string FixedFreq { get; set; } = "Semiannual";
        public string FixedDcc { get; set; } = "ACT/365F";
        public string FloatIndex { get; set; } = "";
        public string FloatTenor { get; set; } = "3M";
        public string FloatFreq { get; set; } = "Quarterly";
        public string FloatDcc { get; set; } = "ACT/360";
        public int FixingDays { get; set; } = 2;
        public string FixingTicker { get; set; } = "";
    }

    public sealed class OisFrontDef
    {
        /// <summary>Short-vs-long IBOR tenor basis swap quotes in bp (e.g. NKBFVC1..10), full securities;
        /// trailing digits = tenor years. Linear interp, flat outside.</summary>
        public List<string> BasisTickers { get; set; } = new();
        /// <summary>Short-index IMM FRA contracts (NKF30001…, full securities). When quoted, the
        /// REAL strip (+ basis) shapes the long-leg front instead of the OIS-derived shape.</summary>
        public List<string> FraTickers { get; set; } = new();
        /// <summary>Index period of the strip contracts (3M FRAs even in single-6M-leg markets).</summary>
        public string StripTenor { get; set; } = "3M";
    }

    public sealed class PillarDef
    {
        /// <summary>"1W","3M","5Y","18M" or "13P" (P = 28-day periods, MXN).</summary>
        public string Tenor { get; set; } = "";
        /// <summary>"OIS" | "SWAP" | "DEPO".</summary>
        public string Type { get; set; } = "SWAP";
        /// <summary>Base ticker without pricing source ("USOSFR5"); if it ends with " Index" it is used literally.</summary>
        public string Ticker { get; set; } = "";
        /// <summary>Float-index tenor band this quote belongs to ("3M"/"6M"); default = by tenor rule.
        /// Lets e.g. AUD ADSWAP4Q (4Y quoted quarterly) extend the 3M projection curve.</summary>
        public string? Band { get; set; }
        public bool Enabled { get; set; } = true;
    }
}
