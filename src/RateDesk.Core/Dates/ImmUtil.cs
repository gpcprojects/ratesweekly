using System;
using System.Text.RegularExpressions;
using QLNet;

namespace RateDesk.Core.Dates
{
    /// <summary>IMM (International Monetary Market) date handling: H/M/U/Z + 2-digit year codes,
    /// effective date = 3rd Wednesday of Mar/Jun/Sep/Dec.</summary>
    public static class ImmUtil
    {
        private static readonly Regex Rx = new(@"^(?:IMM)?([HMUZ])(\d{2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryParse(string token, out Date immDate)
        {
            immDate = new Date();
            var m = Rx.Match(token.Trim());
            if (!m.Success) return false;
            int month = char.ToUpperInvariant(m.Groups[1].Value[0]) switch
            {
                'H' => 3, 'M' => 6, 'U' => 9, 'Z' => 12, _ => 0,
            };
            int yy = int.Parse(m.Groups[2].Value);
            int year = 2000 + yy; // 2-digit codes: 00-99 => 2000-2099
            immDate = ThirdWednesday(month, year);
            return true;
        }

        public static Date ThirdWednesday(int month, int year)
        {
            var first = new Date(1, (Month)month, year);
            int firstDow = (int)first.DayOfWeek; // 0=Sunday..6=Saturday (System.DayOfWeek)
            int wednesday = (int)DayOfWeek.Wednesday;
            int offset = (wednesday - firstDow + 7) % 7;
            return first + (offset + 14);
        }

        /// <summary>Format a date's IMM code if it's an IMM month (e.g. Jun-2026 -> "M26").</summary>
        public static string? CodeFor(Date d)
        {
            char? c = d.month() switch { 3 => 'H', 6 => 'M', 9 => 'U', 12 => 'Z', _ => (char?)null };
            return c == null ? null : $"{c}{d.year() % 100:00}";
        }
    }
}
