using System;

namespace RateDesk.Core.Risk
{
    /// <summary>Resolved trade size: the DV01 and the notional that produces it.</summary>
    public readonly struct SizingResult
    {
        public double Dv01 { get; init; }
        public double Notional { get; init; }
    }

    /// <summary>The desk's ONE sizing rule: density (dv01 per 1mm) + an optional explicit
    /// notional or dv01 target -> the notional/dv01 pair to trade. Every product goes through
    /// here so an unsized query means the same size everywhere (headline tiles, cashflows,
    /// ladder, blotter) instead of each path inventing its own default.</summary>
    public static class RiskSizer
    {
        /// <summary>Desk default risk for an unsized trade. The ONLY place this number lives.</summary>
        public const double DefaultDv01Usd = 25_000.0;

        /// <summary>Derived notionals are rounded to this lot. A dv01 target backs out to
        /// 16,470,219 but the desk trades 16.5mm, so the round lot IS the trade.</summary>
        public const double NotionalLot = 500_000.0;

        /// <summary>Round a DERIVED notional to the nearest tradeable lot. Deliberately shifts the
        /// realised dv01 slightly off target — the round number is what gets dealt. An explicitly
        /// typed notional is never passed through here; it is traded exactly as entered.</summary>
        public static double RoundNotional(double notional)
        {
            double rounded = Math.Round(notional / NotionalLot, MidpointRounding.AwayFromZero) * NotionalLot;
            // a sub-lot notional would round to zero (no trade at all) — keep it exact instead of
            // either zeroing it or inflating it to a full lot
            return rounded == 0.0 && notional != 0.0 ? notional : rounded;
        }

        /// <param name="densityPerMm">Absolute dv01 of a 1,000,000-notional trade, in the trade's
        /// OWN currency — i.e. Pricer.Price(spec at 1e6, curves).Annuity01, or an equivalent
        /// analytic density for legs with no QLNet swap behind them (ladder/meeting legs).</param>
        /// <param name="explicitNotional">User-typed notional (already in trade ccy), else null.</param>
        /// <param name="explicitDv01">User-typed dv01 target, already FX-converted INTO the trade
        /// ccy, else null — in which case <see cref="DefaultDv01Usd"/> applies.</param>
        public static SizingResult Resolve(double densityPerMm,
            double? explicitNotional = null, double? explicitDv01 = null)
        {
            // a degenerate density (no annuity, zero-length period) must not produce Infinity
            double density = Math.Max(Math.Abs(densityPerMm), 1e-9);

            // an explicit notional always wins — the trader said the face amount
            if (explicitNotional.HasValue)
            {
                double n = explicitNotional.Value;
                return new SizingResult { Notional = n, Dv01 = density * n / 1_000_000.0 };
            }

            // dv01 target -> notional, rounded to a tradeable lot. The dv01 is then RE-DERIVED from
            // the rounded notional so the reported risk is the risk of the trade actually done,
            // not the target it was aimed at.
            double dv01 = explicitDv01 ?? DefaultDv01Usd;
            double sized = RoundNotional(dv01 / density * 1_000_000.0);
            return new SizingResult { Notional = sized, Dv01 = density * sized / 1_000_000.0 };
        }
    }
}
