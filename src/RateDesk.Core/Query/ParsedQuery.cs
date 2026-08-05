using System.Collections.Generic;
using QLNet;
using RateDesk.Core.Trades;

namespace RateDesk.Core.Query
{
    public enum QueryShape { Outright, Spread, Fly, ForwardGrid }

    /// <summary>What the user asked to see. Analytics computes everything; Focus just drives emphasis/CLI output.</summary>
    public enum Focus { All, Mid, Roll, Carry, History, ZScore, Vol }

    public sealed class Leg
    {
        public StartKind StartKind { get; set; } = StartKind.Spot;
        public Period? ForwardStart { get; set; }
        public Date? ImmDate { get; set; }
        public string? ImmCode { get; set; }
        public Date? ExplicitStart { get; set; }
        public Date? ExplicitEnd { get; set; }
        public Period? Tenor { get; set; }

        /// <summary>A FRA leg. Names match <see cref="Trades.TradeSpec"/> 1:1 so LegSpec is a copy.
        /// Rolling FRAs carry both months ("aud 3x6 fra"); IMM-dated FRAs carry an
        /// <see cref="ImmDate"/> and take their length from the index tenor ("sek u26 fra").</summary>
        public bool IsFra { get; set; }
        public int? FraStartMonths { get; set; }
        public int? FraEndMonths { get; set; }

        /// <summary>Desk label for a meeting-anchored leg ("FOMC JUL-26"). Cosmetic: used by
        /// <see cref="Describe"/> only, so the headline reads as the trade a trader asked for rather
        /// than a bare anchor date.</summary>
        public string? MeetingLabel { get; set; }

        public string Describe()
        {
            if (IsFra)
                return FraStartMonths.HasValue && FraEndMonths.HasValue
                    ? $"{FraStartMonths}x{FraEndMonths} FRA"
                    : $"{ImmCode} FRA";
            string tenor = Tenor != null ? Dates.TenorUtil.Format(Tenor) : ExplicitEnd?.ToString() ?? "?";
            return StartKind switch
            {
                StartKind.Spot => tenor,
                StartKind.Forward => $"{Dates.TenorUtil.Format(ForwardStart!)}{tenor}",
                StartKind.Imm => $"{ImmCode} {tenor}",
                StartKind.Date => MeetingLabel != null
                    ? $"{MeetingLabel} {tenor}"
                    : $"{ExplicitStart:dd-MMM-yy}+{tenor}",
                _ => tenor,
            };
        }
    }

    public sealed class ParsedQuery
    {
        public string Raw { get; set; } = "";
        public QueryShape Shape { get; set; } = QueryShape.Outright;
        public Focus Focus { get; set; } = Focus.All;
        public CurveTarget Target { get; set; } = null!;

        /// <summary>1 leg = outright/forward; 2 = curve spread (-1/+1); 3 = fly (-1/+2/-1).</summary>
        public List<Leg> Legs { get; } = new();
        public Leg? Main => Legs.Count > 0 ? Legs[0] : null;

        // trade economics
        public double Notional { get; set; } = 10_000_000;
        /// <summary>Target DV01 (in Dv01Ccy per bp). When set, notionals are derived from it.</summary>
        public double? Dv01Target { get; set; }
        /// <summary>Currency the DV01 inputs are expressed in ("$25k"=USD, "¥25k"/"jpy25k"=JPY).
        /// Default USD per desk convention; converted to the trade currency at spot.</summary>
        public string Dv01Ccy { get; set; } = "USD";
        /// <summary>Custom structure weights (w:1/2/1). Unsigned values get the standard sign
        /// pattern (-/+ for curves, -/+/- for flies); signed values are used as given.</summary>
        public List<double>? Weights { get; set; }
        /// <summary>Explicit per-leg notionals ("33m x 50m x 20m") — DV01s computed from them.</summary>
        public List<double>? LegNotionals { get; set; }
        /// <summary>Explicit per-leg DV01s ("$20k x $40k x $20k") — notionals computed from them.</summary>
        public List<double>? LegDv01s { get; set; }
        /// <summary>"$20k wings": Dv01Target applies to the wings of a fly (belly gets 2x).</summary>
        public bool WingsSizing { get; set; }
        /// <summary>"$25k belly": the body leg carries the dv01 target, wings half each.</summary>
        public bool BellySizing { get; set; }
        /// <summary>Per-leg float-index tenor overrides (AUD qq/ss): entry i applies to leg i.</summary>
        public List<Period?>? IndexOverrides { get; set; }
        /// <summary>Dated ladder contract code (BRL DI): month letter + 2-digit year, e.g. "F27".</summary>
        public string? DatedCode { get; set; }
        public bool PayFixed { get; set; } = true;
        public double? FixedRate { get; set; }
        public string? Source { get; set; }

        /// <summary>Mid o'ride: user-entered level in HEADLINE units (% for outrights/forwards,
        /// bp for structures). Replaces the curve mid before stats so every z/Δ/percentile/range
        /// stat is scored at the entered level; the true curve mid is kept on MidTrue.</summary>
        public double? MidOverride { get; set; }

        /// <summary>Meeting-dated query ("jul sep fomc"): run name + requested meeting months.
        /// 1 month = the meeting-period rate; 2 = spread between periods; 3 = fly.</summary>
        public string? MeetingRun { get; set; }
        public List<(int Month, int? Year)>? MeetingMonths { get; set; }

        /// <summary>CB run a meeting-ANCHORED swap ("usd jul fomc 5y") took its start date from.
        /// Deliberately NOT <see cref="MeetingRun"/>: four sites use <c>MeetingRun != null</c> as a
        /// proxy for "meeting query with no Legs", and an anchored swap has real legs. Used only to
        /// pull the run's tickers into the snapshot so the anchor resolves off ticker maturities
        /// (authoritative) rather than falling back to config/meetings.json.</summary>
        /// <summary>Ladder whose strip this trade must price on instead of the currency's default OIS
        /// curve. Set either explicitly by the query ("usd ff 29jul26 16sep26") or implicitly for a
        /// meeting-dated trade whose central bank declares a policyLadder — USD meeting dates are Fed
        /// Funds while USD tenor swaps stay SOFR.</summary>
        public string? CurveLadder { get; set; }

        public string? AnchorRun { get; set; }
        /// <summary>Which meeting the anchor names, kept so the date can be RE-resolved once the run's
        /// tickers are in the snapshot — parsing happens before any snapshot exists.</summary>
        public (int Month, int? Year)? AnchorMeeting { get; set; }

        /// <summary>Level-only analysis: skip histories/stats/overlays (blotter refresh loop).</summary>
        public bool SkipHistory { get; set; }

        /// <summary>CROSS-MARKET (BETA): the OTHER side of a two-market spread ("aud vs usd 5y5y",
        /// "nok sek 10y", "usd v gbp 5s10s"). This object is side A, Cross is side B; both sides are
        /// complete single-market queries. Analysis prices each side independently and quotes
        /// larger-minus-smaller so the spread prints positive.</summary>
        public ParsedQuery? Cross { get; set; }

        /// <summary>The user typed "ois"/"irs" explicitly — a missing curve must ERROR, never
        /// silently fall back to the other product.</summary>
        public bool ProductExplicit { get; set; }
    }
}
