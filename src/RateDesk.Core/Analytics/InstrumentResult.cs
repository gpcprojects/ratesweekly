using System;
using System.Collections.Generic;
using QLNet;
using RateDesk.Core.Market;

namespace RateDesk.Core.Analytics
{
    /// <summary>Per-leg detail for outrights and multi-leg structures.
    /// Weight/Dv01/Notional are observable so the UI can edit them inline and recompute.</summary>
    public sealed class LegResult : System.ComponentModel.INotifyPropertyChanged
    {
        public string Label { get; init; } = "";
        private double _weight = 1.0, _notional, _dv01, _ratePct;
        private double? _bbgFwdPct, _altRatePct;
        public double Weight { get => _weight; set { _weight = value; On(nameof(Weight)); } }
        public Date Effective { get; init; } = new Date();
        public Date Maturity { get; init; } = new Date();
        public double RatePct
        {
            get => _ratePct;
            set { _ratePct = value; On(nameof(RatePct)); On(nameof(BasisDisplay)); On(nameof(BasisAlert)); On(nameof(AltBasisDisplay)); }
        }
        /// <summary>Bloomberg FWCM forward ticker value (%) when available.</summary>
        public double? BbgFwdPct
        {
            get => _bbgFwdPct;
            set { _bbgFwdPct = value; On(nameof(BbgFwdPct)); On(nameof(BasisDisplay)); On(nameof(BasisAlert)); }
        }
        public string? BbgFwdTicker { get; set; }
        /// <summary>bootstrapped minus FWCM, bp.</summary>
        public double? BasisBp => BbgFwdPct.HasValue ? (RatePct - BbgFwdPct.Value) * 100.0 : null;
        public string BasisDisplay => BasisBp.HasValue
            ? (BasisBp.Value >= 0 ? "+" : "-") + Math.Abs(BasisBp.Value).ToString("0.0")
            : "";
        /// <summary>Bootstrapped mid differs from the (inferred/interpolated) FWCM mid by more than 2bp.</summary>
        public bool BasisAlert => BasisBp.HasValue && Math.Abs(BasisBp.Value) > 2.0;
        public double Notional { get => _notional; set { _notional = value; On(nameof(Notional)); } }
        public double Dv01 { get => _dv01; set { _dv01 = value; On(nameof(Dv01)); } }
        /// <summary>DV01 per 1mm notional — lets the UI convert an edited DV01 back to notional.</summary>
        public double DensityPerMm { get; set; }
        private string _overrideRateText = "";
        /// <summary>Manually-entered traded/strike rate (text — blank means no override, i.e. ATM).</summary>
        public string OverrideRateText
        {
            get => _overrideRateText;
            set { _overrideRateText = value ?? ""; On(nameof(OverrideRateText)); On(nameof(OverrideRatePct)); }
        }
        public double? OverrideRatePct =>
            double.TryParse(_overrideRateText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v != 0 ? v : null;
        public string HistoryNote { get; set; } = "";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
        /// <summary>Rate off a CCP variant curve (e.g. JSCC) when configured.</summary>
        public double? AltRatePct
        {
            get => _altRatePct;
            set { _altRatePct = value; On(nameof(AltRatePct)); On(nameof(AltBasisDisplay)); }
        }
        public string AltName { get; set; } = "";
        public double? AltBasisBp => AltRatePct.HasValue ? (AltRatePct.Value - RatePct) * 100.0 : null;
        public string AltBasisDisplay => AltBasisBp.HasValue
            ? (AltBasisBp.Value >= 0 ? "+" : "-") + Math.Abs(AltBasisBp.Value).ToString("0.00")
            : "";
    }

    public sealed class InstrumentResult
    {
        public string Query { get; init; } = "";
        public string Label { get; init; } = "";
        public string Ccy { get; init; } = "";
        public string Kind { get; init; } = "";        // Outright / Forward / Spread / Fly / Inflation / Ladder
        public string Unit { get; init; } = "%";        // "%" or "bp"
        public string Source { get; init; } = "";
        public string ConventionSummary { get; init; } = "";

        // live
        public double? Mid { get; set; }
        /// <summary>Set only under a mid o'ride: the REAL curve mid, while Mid carries the user's
        /// entered level (which every stat is then scored against). Null = no override active.</summary>
        public double? MidTrue { get; set; }
        public double? Bid { get; set; }
        public double? Ask { get; set; }
        public double? BidAskWideBp { get; set; }
        public string? PrimaryTicker { get; set; }

        // curve-derived (rate instruments)
        public double? ParRatePct { get; set; }
        public double? Npv { get; set; }
        public double? Annuity01 { get; set; }
        public double? Dv01 { get; set; }
        public Date? Effective { get; set; }
        public Date? Maturity { get; set; }
        /// <summary>Static-curve roll at 1M/3M/6M/9M/1Y, bp; positive = rolls to a lower rate. The SHAPE
        /// across horizons is the point — it says when a year of roll is actually realised — and
        /// <see cref="RollOverlays"/> reads its timing from these same values. Named for history: a
        /// par-rate difference has no accrual term, so this is roll, not roll plus carry.</summary>
        public List<KeyValuePair<string, double>> RollBp { get; } = new();

        /// <summary>Leg-level detail (1 row for outrights; 2/3 for curves/flies).</summary>
        public List<LegResult> Legs { get; } = new();
        /// <summary>Structure DV01 basis: per-unit-weight risk (each leg's dv01 = weight x this).</summary>
        public double? StructDv01 { get; set; }
        /// <summary>Signed net DV01 across legs (shown when legs are sized explicitly, i.e. not dv01-neutral).</summary>
        public double? NetDv01 { get; set; }
        /// <summary>Structure level off the CCP variant curve (e.g. JSCC), same unit as Mid.</summary>
        public double? AltMid { get; set; }
        public string AltName { get; set; } = "";

        /// <summary>CROSS-MARKET (BETA): two markets priced independently, quoted larger-minus-smaller.
        /// The GUI shows the orange BETA badge off this.</summary>
        public bool IsCross { get; set; }
        /// <summary>The two sides' own queries when <see cref="IsCross"/> ("aud 5y5y", "usd 5y5y") —
        /// what the blotter books, one row per side.</summary>
        public (string a, string b)? CrossSides { get; set; }

        // history-derived
        public SeriesStats? Stats { get; set; }
        public IReadOnlyList<HistPoint> History { get; set; } = Array.Empty<HistPoint>();
        /// <summary>Full (un-sliced) history the stats were computed on — kept so a cross-market
        /// combination can difference full series rather than lookback slices.</summary>
        public IReadOnlyList<HistPoint>? FullHistory { get; set; }
        /// <summary>Roll-destination series, one per horizon: where the trade WILL BE in 3m/6m/9m/1y —
        /// the structure aged by the horizon, evaluated on each history date's quotes. Same units as History.</summary>
        public List<(string Label, IReadOnlyList<HistPoint> Series)> RollOverlays { get; } = new();

        /// <summary>Regression hedge ratio for 2-leg spreads: slope of far-leg daily changes on
        /// near-leg daily changes (1y window). dv01-neutral is only P&L-neutral if this is ~1.</summary>
        public double? EmpBeta { get; set; }
        public double? EmpBetaR2 { get; set; }

        /// <summary>Quotes on this curve that are being re-stamped rather than traded. Populated in full
        /// for the tooltip — but see <see cref="CurveStale"/> before showing anything loud.</summary>
        public List<Market.StaleQuote> StaleQuotes { get; } = new();

        /// <summary>How many nodal points were examined, i.e. the denominator for <see cref="CurveStale"/>.</summary>
        public int StaleAssessed { get; set; }

        /// <summary>True when MOST of the curve's nodal points are stale — the market has gone dark, which
        /// is the case worth interrupting someone for (every NZD contributor frozen at Wellington's close).
        ///
        /// <para>A handful of stale points is NOT this. The long tail and the very front of a curve go
        /// quiet routinely — USD 50Y being re-stamped says nothing about USD — and a warning that fires on
        /// that is a warning nobody reads. The full list stays in <see cref="StaleQuotes"/> either way, so
        /// nothing is hidden, it just is not shouted.</para></summary>
        public bool CurveStale =>
            StaleAssessed > 0 && StaleQuotes.Count >= Math.Ceiling(StaleAssessed * StaleCurveFraction);

        /// <summary>Fraction of nodal points that must be stale before the curve counts as dark. 0.5 =
        /// "most". Deliberately a majority and not a tuned constant: the signal is a whole market going
        /// quiet, and anything less is the normal ragged edge of a curve.</summary>
        public const double StaleCurveFraction = 0.5;

        public List<string> Notes { get; } = new();
        public double ElapsedMs { get; set; }

        /// <summary>The single headline number the board shows (mid if quoted, else par/level).</summary>
        public double? Headline => Mid ?? ParRatePct;
    }
}
