using System;
using System.Text.RegularExpressions;
using QLNet;

namespace RateDesk.Core.Dates
{
    public static class TenorUtil
    {
        private static readonly Regex Rx = new(@"^(\d+)\s*(D|W|M|Y|P)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxCompound = new(@"(\d+)\s*(D|W|M|Y|P)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Parse "5Y","18M","1W","10D","13P" (P = 28-day periods). Compound "1Y6M" also allowed.</summary>
        public static Period Parse(string s)
        {
            s = s.Trim();
            var m = Rx.Match(s);
            if (m.Success) return One(int.Parse(m.Groups[1].Value), m.Groups[2].Value);

            // compound e.g. 1Y6M
            var ms = RxCompound.Matches(s);
            if (ms.Count == 0) throw new FormatException($"Bad tenor '{s}'");
            int months = 0, days = 0;
            foreach (Match mm in ms)
            {
                int n = int.Parse(mm.Groups[1].Value);
                switch (char.ToUpperInvariant(mm.Groups[2].Value[0]))
                {
                    case 'Y': months += 12 * n; break;
                    case 'M': months += n; break;
                    case 'W': days += 7 * n; break;
                    case 'P': days += 28 * n; break;
                    case 'D': days += n; break;
                }
            }
            if (months > 0 && days == 0) return new Period(months, TimeUnit.Months);
            if (days > 0 && months == 0) return new Period(days, TimeUnit.Days);
            throw new FormatException($"Mixed day/month tenor unsupported: '{s}'");
        }

        private static Period One(int n, string unit) => char.ToUpperInvariant(unit[0]) switch
        {
            'D' => new Period(n, TimeUnit.Days),
            'W' => new Period(n, TimeUnit.Weeks),
            'M' => new Period(n, TimeUnit.Months),
            'Y' => new Period(n, TimeUnit.Years),
            'P' => new Period(4 * n, TimeUnit.Weeks), // 28-day periods (MXN)
            _ => throw new FormatException($"Bad tenor unit '{unit}'"),
        };

        /// <summary>Approximate length in months, for tenor-band comparisons (AUD 3Y switch etc.).</summary>
        public static double ApproxMonths(Period p) => p.units() switch
        {
            TimeUnit.Years => p.length() * 12.0,
            TimeUnit.Months => p.length(),
            TimeUnit.Weeks => p.length() * 7.0 / 30.4375,
            TimeUnit.Days => p.length() / 30.4375,
            _ => throw new ArgumentOutOfRangeException(),
        };

        public static string Format(Period p)
        {
            if (p.units() == TimeUnit.Weeks && p.length() % 4 == 0 && p.length() >= 4)
                return $"{p.length() / 4}P";
            var u = p.units() switch
            {
                TimeUnit.Days => "D",
                TimeUnit.Weeks => "W",
                TimeUnit.Months => "M",
                TimeUnit.Years => "Y",
                _ => "?",
            };
            return $"{p.length()}{u}";
        }
    }
}
