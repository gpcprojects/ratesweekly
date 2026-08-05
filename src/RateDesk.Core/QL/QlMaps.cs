using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;

namespace RateDesk.Core.QL
{
    /// <summary>String -> QLNet object mapping for calendars, day counts, frequencies, currencies.</summary>
    public static class QlMaps
    {
        public static Calendar MakeCalendar(string spec)
        {
            var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return new WeekendsOnly();
            Calendar cal = One(parts[0]);
            for (int i = 1; i < parts.Length; i++)
                cal = new JointCalendar(cal, One(parts[i]), JointCalendar.JointCalendarRule.JoinHolidays);
            return cal;
        }

        private static Calendar One(string c)
        {
            try
            {
                switch (c.ToUpperInvariant())
                {
                    case "USD": return new UnitedStates(UnitedStates.Market.GovernmentBond);
                    case "USD-NYSE": return new UnitedStates(UnitedStates.Market.NYSE);
                    case "GBP": return new UnitedKingdom();
                    case "EUR": case "TARGET": return new TARGET();
                    case "JPY": return new Japan();
                    case "CHF": return new Switzerland();
                    case "AUD": return new Australia();
                    case "NZD": return new NewZealand();
                    case "CAD": return new Canada();
                    case "SEK": return new Sweden();
                    case "NOK": return new Norway();
                    case "DKK": return new Denmark();
                    case "PLN": return new Poland();
                    case "CZK": return new CzechRepublic();
                    case "HUF": return new Hungary();
                    case "ZAR": return new SouthAfrica();
                    case "SGD": return new Singapore();
                    case "HKD": return new HongKong();
                    case "KRW": return new SouthKorea();
                    case "TWD": return new Taiwan();
                    case "THB": return new Thailand();
                    case "CNY": return new China();
                    case "INR": return new India();
                    case "ILS": return new Israel();
                    case "MXN": return new Mexico();
                    case "BRL": return new Brazil();
                    default: return new WeekendsOnly(); // CLP, COP, others without a QLNet calendar
                }
            }
            catch
            {
                return new WeekendsOnly();
            }
        }

        public static DayCounter MakeDayCounter(string s) => s.ToUpperInvariant().Replace(" ", "") switch
        {
            "ACT/360" => new Actual360(),
            "ACT/365F" or "ACT/365" => new Actual365Fixed(),
            "30/360" or "30U/360" or "BOND" => new Thirty360(Thirty360.Thirty360Convention.BondBasis),
            "30E/360" => new Thirty360(Thirty360.Thirty360Convention.European),
            "ACT/ACT" or "ACT/ACTISDA" => new ActualActual(ActualActual.Convention.ISDA),
            "BUS/252" or "ACT/252" => new Business252(new Brazil()),
            _ => throw new ArgumentException($"Unknown day count '{s}'"),
        };

        public static Frequency MakeFrequency(string s) => s.ToUpperInvariant() switch
        {
            "ANNUAL" => Frequency.Annual,
            "SEMIANNUAL" or "SEMI" => Frequency.Semiannual,
            "QUARTERLY" => Frequency.Quarterly,
            "MONTHLY" => Frequency.Monthly,
            "EVERY28DAYS" or "28D" => Frequency.EveryFourthWeek,
            "ONCE" or "ZEROCOUPON" => Frequency.Once,
            _ => throw new ArgumentException($"Unknown frequency '{s}'"),
        };

        public static Period PeriodOf(Frequency f) => f switch
        {
            Frequency.Annual => new Period(1, TimeUnit.Years),
            Frequency.Semiannual => new Period(6, TimeUnit.Months),
            Frequency.Quarterly => new Period(3, TimeUnit.Months),
            Frequency.Monthly => new Period(1, TimeUnit.Months),
            Frequency.EveryFourthWeek => new Period(4, TimeUnit.Weeks),
            _ => throw new ArgumentException($"No period for frequency '{f}'"),
        };

        public static BusinessDayConvention MakeBdc(string s) => s.ToUpperInvariant().Replace(" ", "") switch
        {
            "MODIFIEDFOLLOWING" or "MF" => BusinessDayConvention.ModifiedFollowing,
            "FOLLOWING" or "F" => BusinessDayConvention.Following,
            "PRECEDING" or "P" => BusinessDayConvention.Preceding,
            "UNADJUSTED" => BusinessDayConvention.Unadjusted,
            _ => throw new ArgumentException($"Unknown BDC '{s}'"),
        };

        private static readonly Dictionary<string, Currency> CcyMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = new USDCurrency(), ["EUR"] = new EURCurrency(), ["GBP"] = new GBPCurrency(),
            ["JPY"] = new JPYCurrency(), ["CHF"] = new CHFCurrency(), ["AUD"] = new AUDCurrency(),
            ["NZD"] = new NZDCurrency(), ["CAD"] = new CADCurrency(), ["SEK"] = new SEKCurrency(),
            ["NOK"] = new NOKCurrency(), ["DKK"] = new DKKCurrency(), ["PLN"] = new PLNCurrency(),
            ["CZK"] = new CZKCurrency(), ["HUF"] = new HUFCurrency(), ["ZAR"] = new ZARCurrency(),
            ["SGD"] = new SGDCurrency(), ["HKD"] = new HKDCurrency(), ["KRW"] = new KRWCurrency(),
            ["TWD"] = new TWDCurrency(), ["THB"] = new THBCurrency(), ["CNY"] = new CNYCurrency(),
            ["INR"] = new INRCurrency(), ["ILS"] = new ILSCurrency(), ["MXN"] = new MXNCurrency(),
            ["BRL"] = new BRLCurrency(), ["CLP"] = new CLPCurrency(), ["COP"] = new COPCurrency(),
        };

        public static Currency MakeCurrency(string ccy) =>
            CcyMap.TryGetValue(ccy, out var c) ? c : new USDCurrency();
    }
}
