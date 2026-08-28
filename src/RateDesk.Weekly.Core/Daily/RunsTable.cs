using RateDesk.Core;

namespace RateDesk.Weekly.Core.Daily
{
    /// <summary>THE daily runs table — ONE definition of the block/row shape, the column set and
    /// the number formats, consumed by the xlsx Runs sheet (DailyBook.WriteRunsSheet) AND the
    /// emails' sheet-style tables (SheetEmail). Desk 2026-08-26: "the xls on daily attachment,
    /// blast, and inline content (both daily and weekly) should all have the same data" — with
    /// one builder there is no second enumeration to drift. The blast keeps its own renderer
    /// (unchanged by desk instruction) and prints the same rows minus Maturity.
    ///
    /// Formats live here too, so the sheet's displayed number and the email's text are the same
    /// string by construction: rates 0.000, changes +0.0/-0.0/0.0, dates dd-MMM-yy, all
    /// invariant-culture.</summary>
    public static class RunsTable
    {
        private static readonly System.Globalization.CultureInfo Inv =
            System.Globalization.CultureInfo.InvariantCulture;

        public const string RateFmt = "0.000";
        public const string BpFmt = "+0.0;-0.0;0.0";
        public const string DateFmt = "dd-MMM-yy";
        /// <summary>Kept as the one public name for the turn label; the row itself now
        /// carries whichever label applies (RateDesk.Core.MaskLabels).</summary>
        public const string TurnLabel = RateDesk.Core.MaskLabels.Turn;

        /// <summary>DRAX blue, MUTED — a 30% tint of the logo blue (#01A2E6, sampled from
        /// assets\jbdh_banner.jpg) over white, desk 2026-08-26 ("more faded... a bit more
        /// muted"). Chosen to carry the same visual weight as the grey band it replaces:
        /// luminance 215 against the old #D9D9D9's 217, so the header reads as a tint rather
        /// than a shout and bold black sits on it cleanly. The band has NO internal dividers
        /// on either surface — it is one continuous strip, as the grey one was.</summary>
        public const string BrandBlue = "#B3E3F8";
        /// <summary>The logo blue at full strength, for anything that needs the brand itself.</summary>
        public const string BrandBlueFull = "#01A2E6";
        /// <summary>The xls's hairline grid. The EMAIL carries no grid at all — gridlines and
        /// conditional formatting are the only two differences the desk wants between the
        /// attachment and the inline content.</summary>
        public const string GridLine = "#BFBFBF";

        /// <summary>Excel column width (characters) → CSS pixels, so the email's columns are the
        /// SAME measure as the sheet's: Excel's own 7px-per-character plus 5px of cell padding.</summary>
        public static int PxForChars(double chars) => (int)Math.Round(chars * 7 + 5);

        /// <summary>Column headers, in the desk's order: Mid | Priced | Step, then the changes
        /// (desk 2026-08-26, "mid/priced/step everywhere *everywhere*").</summary>
        public static readonly string[] Headers =
        {
            "StartDate", "Maturity", "Mid", "Priced (bp)", "Step (bp)",
            "Δ 1d (bp)", "Δ 1w (bp)", "Δ 1m (bp)",
        };

        /// <summary>MaskLabel non-empty = the row publishes that label instead of numbers
        /// (a Y/E turn, or a print the neighbour guard rejected). The app never publishes a
        /// manufactured mid, so there is no "synthesized" flag any more (desk 2026-08-27).</summary>
        public sealed record Row(DateTime Start, DateTime? End, double Mid, double? PricedBp,
            double? StepBp, double? D1Bp, double? W1Bp, double? M1Bp, string MaskLabel)
        {
            public bool Masked => MaskLabel.Length > 0;
        }

        public sealed record Block(string Bank, string Flag, string FixingLabel, double? FixingPct,
            bool Rebased, List<Row> Rows, string RebasedLabel = " (rebased)");

        public static string Title(DateTime asOf) =>
            $"DRAX OIS Runs {asOf.ToString("dMMMyy", Inv)}";

        /// <summary>The blocks the daily products publish, in the desk's blast order. A run with
        /// no rows is omitted (the CHECK note names it — CompoundedFixing.Stamp).</summary>
        public static List<Block> Build(WeeklyReport rep)
        {
            var blocks = new List<Block>();
            foreach (var (runName, flag, fixing) in DailyBlast.Blocks)
            {
                var run = DailyBlast.Find(rep, runName);
                if (run == null || run.Rows.Count == 0) continue;
                var rows = new List<Row>();
                for (int i = 0; i < run.Rows.Count; i++)
                {
                    var m = run.Rows[i];
                    // the period end: the row's own resolved EndDate, else the next row's start
                    var end = m.EndDate ?? (i + 1 < run.Rows.Count ? run.Rows[i + 1].Date : (DateTime?)null);
                    rows.Add(new Row(m.Date, end, m.MidPct, m.PricedBp, m.StepBp,
                        m.D1Bp, m.W1Bp, m.M1Bp, m.MaskLabel));
                }
                blocks.Add(new Block(runName, flag, fixing, run.RefPct, run.RefRebased, rows,
                    run.RebasedLabel));
            }
            return blocks;
        }

        public static string DateText(DateTime d) => d.ToString(DateFmt, Inv);
        public static string RateText(double v) => v.ToString(RateFmt, Inv);
        public static string BpText(double v) => v.ToString(BpFmt, Inv);
    }
}
