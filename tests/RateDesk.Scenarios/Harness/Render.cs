using System.Text.RegularExpressions;

namespace RateDesk.Scenarios.Harness;

/// <summary>Readers for the rendered surfaces. Every surface is parsed back into rows so a
/// scenario compares STRUCTURE, not substrings: "the blast block for MPC has these seven columns
/// in this order" is a real check; "the email contains 3.775 somewhere" is not.</summary>
public static class Render
{
    public const char NbHyphen = '‑';   // the email's non-breaking hyphen
    public const char Nbsp = ' ';

    public static string Norm(string s) => s
        .Replace(NbHyphen, '-')
        .Replace(Nbsp, ' ')
        .Replace("&nbsp;", " ")
        .Replace("&amp;", "&")
        .Trim();

    public sealed class Block
    {
        public string Bank = "";
        public string FixingLabel = "";
        public string FixingValue = "";
        public bool Rebased;
        public List<string[]> Rows = new();
    }

    // ------------------------------------------------------------ blast (plain text)

    /// <summary>Blocks out of the chat blast. Columns: StartDate Mid Priced Step d1 w1 m1
    /// (Maturity is deliberately absent - the desk's standing blast rule).</summary>
    public static Dictionary<string, Block> Blast(string text)
    {
        var res = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        Block? cur = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = Regex.Match(line, @"^\{[A-Z]{2}\}\s+(\S+) Run(?:\s+\((.+?)\s+([\d.]+)(\*)?\))?\s*$");
            if (m.Success)
            {
                cur = new Block
                {
                    Bank = m.Groups[1].Value,
                    FixingLabel = m.Groups[2].Success ? m.Groups[2].Value : "",
                    FixingValue = m.Groups[3].Success ? m.Groups[3].Value : "",
                    Rebased = m.Groups[4].Success,
                };
                res[cur.Bank] = cur;
                continue;
            }
            if (cur == null) continue;
            if (line.TrimStart().StartsWith("StartDate")) continue;
            if (string.IsNullOrWhiteSpace(line)) { cur = null; continue; }
            var cells = Regex.Split(line.Trim(), @"\s+");
            cur.Rows.Add(cells);
        }
        return res;
    }

    // ------------------------------------------------------------ workbook

    /// <summary>Blocks out of the Runs sheet grid. Columns: StartDate Maturity Mid Priced Step
    /// d1 w1 m1.</summary>
    public static Dictionary<string, Block> Sheet(List<List<string>> grid)
    {
        var res = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        Block? cur = null;
        bool afterHeader = false;
        foreach (var row in grid)
        {
            string c0 = row.Count > 0 ? row[0].Trim() : "";
            var m = Regex.Match(c0, @"^(\S+) closing run$");
            if (m.Success)
            {
                cur = new Block { Bank = m.Groups[1].Value };
                res[cur.Bank] = cur;
                afterHeader = false;
                continue;
            }
            if (cur == null) continue;
            if (c0.Contains(" fixing"))
            {
                cur.FixingLabel = c0.Substring(0, c0.IndexOf(" fixing", StringComparison.Ordinal));
                var fv = row.Count > 1 ? row[1].Trim() : "";
                cur.Rebased = fv.EndsWith("*", StringComparison.Ordinal);   // desk 2026-09-02
                cur.FixingValue = fv.TrimEnd('*');
                continue;
            }
            if (c0 == "StartDate") { afterHeader = true; continue; }
            if (!afterHeader) continue;
            if (string.IsNullOrWhiteSpace(c0)) { cur = null; afterHeader = false; continue; }
            cur.Rows.Add(row.Select(x => x.Trim()).ToArray());
        }
        return res;
    }

    // ------------------------------------------------------------ sheet-style email (HTML)

    /// <summary>Every table row of an HTML fragment as its cell texts, in document order.</summary>
    public static List<string[]> HtmlRows(string html)
    {
        var rows = new List<string[]>();
        foreach (Match tr in Regex.Matches(html, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline))
        {
            var cells = new List<string>();
            foreach (Match td in Regex.Matches(tr.Groups[1].Value, "<t[dh][^>]*>(.*?)</t[dh]>",
                         RegexOptions.Singleline))
                cells.Add(Norm(Regex.Replace(td.Groups[1].Value, "<.*?>", "", RegexOptions.Singleline)));
            rows.Add(cells.ToArray());
        }
        return rows;
    }

    /// <summary>Blocks out of the sheet-style email body - the facsimile of the workbook.
    /// Same eight columns as the sheet.</summary>
    public static Dictionary<string, Block> Email(string html)
    {
        var res = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        Block? cur = null;
        bool afterHeader = false;
        foreach (var row in HtmlRows(html))
        {
            string c0 = row.Length > 0 ? row[0].Trim() : "";
            var m = Regex.Match(c0, @"^(\S+) closing run$");
            if (m.Success)
            {
                cur = new Block { Bank = m.Groups[1].Value };
                res[cur.Bank] = cur;
                afterHeader = false;
                continue;
            }
            if (cur == null) continue;
            if (c0.Contains(" fixing"))
            {
                cur.FixingLabel = c0.Substring(0, c0.IndexOf(" fixing", StringComparison.Ordinal));
                var fv = row.Length > 1 ? row[1].Trim() : "";
                cur.Rebased = fv.EndsWith("*", StringComparison.Ordinal);   // desk 2026-09-02
                cur.FixingValue = fv.TrimEnd('*');
                continue;
            }
            if (c0 == "StartDate") { afterHeader = true; continue; }
            if (!afterHeader) continue;
            if (string.IsNullOrWhiteSpace(c0)) { cur = null; afterHeader = false; continue; }
            cur.Rows.Add(row);
        }
        return res;
    }

    // ------------------------------------------------------------ card email (WeeklyEmail.Html)

    /// <summary>The meeting CARDS of the card-style email body. Cards sit three to a table row,
    /// so the document is split on each card's own title div rather than walked row by row.
    /// Columns: StartDate Mid Priced Step 1d 1w 1m.</summary>
    public static Dictionary<string, Block> Cards(string html)
    {
        var res = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        // "<div ...font-size:12.5px...>FOMC · USD <span ...>fixing 3.900†&nbsp;(rebased)</span></div>"
        var titles = Regex.Matches(html, "font-size:12\\.5px[^>]*>(.*?)</div>", RegexOptions.Singleline);
        for (int i = 0; i < titles.Count; i++)
        {
            var raw = titles[i].Groups[1].Value;
            var text = Norm(Regex.Replace(raw, "<.*?>", "", RegexOptions.Singleline));
            var m = Regex.Match(text, @"^(\S+)\s*·");
            if (!m.Success) continue;
            var blk = new Block { Bank = m.Groups[1].Value };
            var fx = Regex.Match(text, @"fixing\s+([\d.]+)(\*)?");
            if (fx.Success) { blk.FixingValue = fx.Groups[1].Value; blk.Rebased = fx.Groups[2].Success; }

            int from = titles[i].Index + titles[i].Length;
            int to = i + 1 < titles.Count ? titles[i + 1].Index : html.Length;
            foreach (var row in HtmlRows(html.Substring(from, to - from)))
            {
                if (row.Length != 7) continue;
                if (row[0] == "StartDate") continue;
                if (!DateTime.TryParseExact(row[0].Replace(NbHyphen, '-'), "dd-MMM-yy",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _)) continue;
                blk.Rows.Add(row);
            }
            res[blk.Bank] = blk;
        }
        return res;
    }

    /// <summary>The CB front table of the CARD email: Bank+ccy / Decision / Start / Mid /
    /// Fixing / Priced / %25bp.</summary>
    public static List<string[]> CardFront(string html)
    {
        var rows = new List<string[]>();
        int start = html.IndexOf("CB Front Meeting Market Pricing", StringComparison.Ordinal);
        if (start < 0) return rows;
        int end = html.IndexOf("Central Bank OIS Meetings", StringComparison.Ordinal);
        var slice = end > start ? html.Substring(start, end - start) : html.Substring(start);
        foreach (var row in HtmlRows(slice))
        {
            if (row.Length != 7) continue;
            if (row[0] == "Central Bank") continue;
            if (string.IsNullOrWhiteSpace(row[0])) continue;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The CB front table rows of the sheet-style email: Bank/Decision/Start/Mid/
    /// Fixing/Priced/%25bp.</summary>
    public static List<string[]> EmailFront(string html)
    {
        var rows = new List<string[]>();
        bool inTable = false;
        foreach (var row in HtmlRows(html))
        {
            string c0 = row.Length > 0 ? row[0].Trim() : "";
            if (c0 == "CB Front Meeting Market Pricing") { inTable = true; continue; }
            if (!inTable) continue;
            if (c0 == "Central Bank") continue;
            if (string.IsNullOrWhiteSpace(c0)) { if (rows.Count > 0) inTable = false; continue; }
            if (c0.StartsWith("*") || c0.StartsWith("†")) { inTable = false; continue; }
            rows.Add(row);
        }
        return rows;
    }
}
