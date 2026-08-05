using System;
using QLNet;
using RateDesk.Core.Dates;

namespace RateDesk.Core.Trades
{
    public enum ProductKind { Default, OIS, IRS, FRA }
    public enum StartKind { Spot, Imm, Forward, Date }

    public sealed class TradeSpec
    {
        public string Ccy { get; set; } = "";
        public ProductKind Product { get; set; } = ProductKind.Default;

        public StartKind StartKind { get; set; } = StartKind.Spot;
        /// <summary>IMM effective date (3rd Wednesday) when StartKind == Imm.</summary>
        public Date? ImmDate { get; set; }
        public string? ImmCode { get; set; }
        /// <summary>Forward-start offset from spot when StartKind == Forward (e.g. 1Y in "1y5y").</summary>
        public Period? ForwardStart { get; set; }
        public Date? ExplicitStart { get; set; }

        public Period? Tenor { get; set; }
        public Date? ExplicitEnd { get; set; }

        /// <summary>FRA months, e.g. 3x6.</summary>
        public int? FraStartMonths { get; set; }
        public int? FraEndMonths { get; set; }

        /// <summary>Face amount actually priced. Left at the legacy flat default for direct callers;
        /// <see cref="PricingService.PriceCommand"/> overwrites it from the sizing rule below.</summary>
        public double Notional { get; set; } = 10_000_000;
        /// <summary>Notional the user actually TYPED, as opposed to the default sitting in
        /// <see cref="Notional"/>. Without this there is no way to tell "usd 5y 10mm" from "usd 5y".</summary>
        public double? ExplicitNotional { get; set; }
        /// <summary>DV01 to size to, in <see cref="Dv01Ccy"/>. Null means size off
        /// <see cref="ExplicitNotional"/> if given, else the desk default.</summary>
        public double? Dv01Target { get; set; }
        /// <summary>Currency the dv01 target is expressed in ("$25k" -> USD).</summary>
        public string Dv01Ccy { get; set; } = "USD";
        public bool PayFixed { get; set; } = true;
        /// <summary>Traded fixed rate in DECIMAL (0.0425). Null = price at par.</summary>
        public double? FixedRate { get; set; }
        /// <summary>Optional float-index tenor override, e.g. force 3M or 6M on AUD.</summary>
        public Period? FloatTenorOverride { get; set; }
        /// <summary>Pricing source override (e.g. "BMOD"); null = currency default.</summary>
        public string? Source { get; set; }

        public string Describe()
        {
            string start = StartKind switch
            {
                StartKind.Spot => "spot",
                StartKind.Imm => $"IMM {ImmCode} ({ImmDate})",
                StartKind.Forward => $"{TenorUtil.Format(ForwardStart!)} fwd",
                StartKind.Date => ExplicitStart!.ToString(),
                _ => "?",
            };
            string what = Product == ProductKind.FRA
                ? $"{FraStartMonths}x{FraEndMonths} FRA"
                : $"{(Tenor != null ? TenorUtil.Format(Tenor) : ExplicitEnd?.ToString() ?? "?")} {Product}";
            string dir = PayFixed ? "pay" : "rec";
            string rate = FixedRate.HasValue ? $" @ {FixedRate.Value * 100:0.####}%" : " (par)";
            return $"{Ccy} {start} {what} {dir} {Notional:N0}{rate}";
        }
    }
}
