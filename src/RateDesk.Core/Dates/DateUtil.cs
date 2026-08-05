using System;
using System.Globalization;
using System.Text.RegularExpressions;
using QLNet;

namespace RateDesk.Core.Dates
{
    /// <summary>Flexible desk-style date parsing: 25-jun-31, 25jun31, 25Jun2031, 25/06/31,
    /// 25/6/2031, 25.06.31, 2031-06-25, 20310625.</summary>
    public static class DateUtil
    {
        private static readonly string[] Formats =
        {
            "d-MMM-yy", "d-MMM-yyyy", "dd-MMM-yy", "dd-MMM-yyyy",
            "dMMMyy", "dMMMyyyy", "ddMMMyy", "ddMMMyyyy",
            "d/M/yy", "d/M/yyyy", "dd/MM/yy", "dd/MM/yyyy",
            "d.M.yy", "d.M.yyyy", "dd.MM.yy", "dd.MM.yyyy",
            "yyyy-MM-dd", "yyyyMMdd",
        };

        public static bool TryParseDate(string token, out Date date)
        {
            date = new Date();
            var t = token.Trim();
            if (t.Length < 5) return false; // avoid swallowing tenors etc.
            foreach (var f in Formats)
            {
                if (DateTime.TryParseExact(t, f, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt))
                {
                    if (dt.Year < 100) dt = dt.AddYears(2000);
                    if (dt.Year < 1990 || dt.Year > 2120) return false;
                    date = new Date(dt.Day, (Month)dt.Month, dt.Year);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Pre-normalise free text before tokenising:
        /// "25 jun 31" -> "25-jun-31";  "+ 5y" -> "+5y";  "- 5y" -> "-5y".</summary>
        public static string Normalize(string text)
        {
            var s = Regex.Replace(text,
                @"\b(\d{1,2})\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+(\d{2,4})\b",
                "$1-$2-$3", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(\d{1,2})\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*-(\d{2,4})\b",
                "$1-$2-$3", RegexOptions.IgnoreCase);
            // glue sign to a following tenor:  "+ 5y" -> "+5y"
            s = Regex.Replace(s, @"([+-])\s+(\d+\s*(?:d|w|m|y|p)\b)", "$1$2", RegexOptions.IgnoreCase);
            // split "date+5y" / "date-5y" glued forms so the sign is its own token
            s = Regex.Replace(s, @"(\d{2,4})([+])(\d+(?:d|w|m|y|p))\b", "$1 $2$3", RegexOptions.IgnoreCase);
            return s;
        }
    }
}
