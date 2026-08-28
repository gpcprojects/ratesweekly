using System.Globalization;
using RateDesk.Core;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Daily;

namespace RateDesk.Scenarios.Harness;

/// <summary>The checks that must hold in EVERY scenario, whatever the decision calendar says.
/// They are deliberately about the PUBLISHED output only - what the desk and its clients read -
/// so they cannot pass by agreeing with a bug upstream:
///
///   · the three run surfaces (chat blast, xlsx attachment, sheet-style email body) must be the
///     same table, cell for cell - that is the desk's own instruction ("i barely want to be able
///     to tell the difference between what's in the email and what's in the xls");
///   · Priced must equal Mid - Fixing, and Step must equal the difference of consecutive Priced,
///     on the published numbers themselves;
///   · the front table's line for a bank must be that bank's first published row;
///   · nothing published may be non-finite, out of order, or silently blank where a number is
///     claimed elsewhere;
///   · the frozen report must round-trip (every offline rebuild starts from it).</summary>
public static class Invariants
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double BpTol = 0.05;

    private static string R(double v) => v.ToString(RunsTable.RateFmt, Inv);
    private static string B(double? v) => v is { } x ? x.ToString(RunsTable.BpFmt, Inv) : "";
    private static string D(DateTime d) => d.ToString(RunsTable.DateFmt, Inv);

    public static List<string> Run(Surfaces s)
    {
        var f = new List<string>();
        Integrity(s, f);
        ReportSanity(s, f);
        Consistency(s, f);
        CrossSurface(s, f);
        FrontTable(s, f);
        CardEmail(s, f);
        Dashboards(s, f);
        RoundTrip(s, f);
        return f;
    }

    // ---------------------------------------------------------------- A. integrity

    private static void Integrity(Surfaces s, List<string> f)
    {
        foreach (var p in s.SetupProblems) f.Add(p);
        foreach (var l in s.BuildLog)
            if (l.Contains("threw", StringComparison.OrdinalIgnoreCase))
                f.Add("BUILD: " + l.Split('\n')[0]);
        foreach (var n in s.Notes)
            if (n.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Unhandled", StringComparison.OrdinalIgnoreCase))
                f.Add($"NOTE carries an exception: {n}");
    }

    // ---------------------------------------------------------------- B. report sanity

    private static void ReportSanity(Surfaces s, List<string> f)
    {
        foreach (var run in s.Report.Runs)
        {
            var bank = run.Title.Split('·')[0].Trim();
            for (int i = 0; i < run.Rows.Count; i++)
            {
                var m = run.Rows[i];
                if (i > 0 && m.Date <= run.Rows[i - 1].Date)
                    f.Add($"{bank}: published rows are not in ascending date order " +
                          $"({D(run.Rows[i - 1].Date)} then {D(m.Date)})");
                if (m.EndDate is { } e && e <= m.Date)
                    f.Add($"{bank} {D(m.Date)}: end date {D(e)} is not after the start");
                foreach (var (label, v) in new (string, double?)[]
                         { ("Mid", m.MidPct), ("Priced", m.PricedBp), ("Step", m.StepBp),
                           ("d1", m.D1Bp), ("w1", m.W1Bp), ("m1", m.M1Bp) })
                    if (v is { } x && !double.IsFinite(x))
                        f.Add($"{bank} {D(m.Date)}: {label} is not a finite number ({x})");
                if (!m.Masked && (m.MidPct < -5 || m.MidPct > 30))
                    f.Add($"{bank} {D(m.Date)}: published mid {m.MidPct} is outside any plausible policy range");
                // A turn row KEEPS its real print internally by design ("the row keeps its
                // real print internally (MeetingRow.TurnPeriod)") - Mid and Priced stay in the
                // model so the guards and the futures blend can use them, and every RENDERER
                // replaces them with the label. That rendering is checked surface by surface in
                // CrossSurface/CardEmail; what must not exist even in the model is a STEP or a
                // CHANGE, because those are the numbers the masked meeting makes unrecoverable.
                if (m.Masked && (m.StepBp.HasValue || m.D1Bp.HasValue
                                     || m.W1Bp.HasValue || m.M1Bp.HasValue))
                    f.Add($"{bank} {D(m.Date)}: a masked row ({m.MaskLabel}) carries a step/change " +
                          "(step or 1d/1w/1m); the masked meeting makes those unrecoverable");
            }
        }
    }

    // ---------------------------------------------------------------- C. internal consistency

    private static void Consistency(Surfaces s, List<string> f)
    {
        foreach (var run in s.Report.Runs)
        {
            var bank = run.Title.Split('·')[0].Trim();

            // Priced is, by definition, the published mid against the published fixing
            if (run.RefPct is { } fix)
                foreach (var m in run.Rows.Where(x => !x.Masked))
                {
                    double want = (m.MidPct - fix) * 100.0;
                    if (m.PricedBp is not { } got)
                        f.Add($"{bank} {D(m.Date)}: Priced is blank although the fixing {R(fix)} is published");
                    else if (Math.Abs(got - want) > BpTol)
                        f.Add($"{bank} {D(m.Date)}: Priced {B(got)} != (mid {R(m.MidPct)} - fixing " +
                              $"{R(fix)}) = {B(want)}");
                }

            // Step differences consecutive Priced, skipping any turn row (the row after a turn
            // carries the CUMULATIVE move across the masked meeting and its own)
            double? lastClean = null;
            bool first = true;
            foreach (var m in run.Rows)
            {
                if (m.Masked) continue;
                if (first)
                {
                    if (m.StepBp.HasValue)
                        f.Add($"{bank} {D(m.Date)}: the first published row has a Step " +
                              $"({B(m.StepBp)}) - there is nothing before it to step from");
                    first = false;
                }
                else if (lastClean is { } prev && m.PricedBp is { } p)
                {
                    double want = p - prev;
                    if (m.StepBp is not { } got)
                        f.Add($"{bank} {D(m.Date)}: Step is blank although both Priced values are published");
                    else if (Math.Abs(got - want) > BpTol)
                        f.Add($"{bank} {D(m.Date)}: Step {B(got)} != Priced {B(p)} - previous Priced " +
                              $"{B(prev)} = {B(want)}");
                }
                if (m.PricedBp is { } pv) lastClean = pv;
            }

            // the front line IS the run's first row
            var front = s.Report.Fronts.FirstOrDefault(x =>
                x.Bank.Equals(bank, StringComparison.OrdinalIgnoreCase));
            if (run.Rows.Count > 0)
            {
                if (front == null)
                    f.Add($"{bank}: publishes {run.Rows.Count} row(s) but has no line in the front table");
                else
                {
                    var r0 = run.Rows[0];
                    if (front.StartDate != r0.Date)
                        f.Add($"{bank}: front table start {D(front.StartDate)} != first run row {D(r0.Date)}");
                    if (Math.Abs(front.MidPct - r0.MidPct) > 1e-9)
                        f.Add($"{bank}: front table mid {R(front.MidPct)} != first run row {R(r0.MidPct)}");
                    if ((front.PricedBp ?? double.NaN) is var fp && r0.PricedBp is { } rp0
                        && (!front.PricedBp.HasValue || Math.Abs(fp - rp0) > BpTol))
                        f.Add($"{bank}: front table Priced {B(front.PricedBp)} != first run row {B(rp0)}");
                    if (front.Masked != r0.Masked)
                        f.Add($"{bank}: front table mask flag disagrees with the first run row");
                }
            }
        }
    }

    // ---------------------------------------------------------------- D. one table, three surfaces

    private static void CrossSurface(Surfaces s, List<string> f)
    {
        var blast = Render.Blast(s.BlastText);
        var xls = Render.Sheet(s.Xlsx);
        var mail = Render.Email(s.SheetHtml);

        var expected = s.Report.Runs
            .Where(r => r.Rows.Count > 0
                        && DailyBlast.Blocks.Any(b => b.Run.Equals(r.Title.Split('·')[0].Trim(),
                            StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Title.Split('·')[0].Trim())
            .ToList();

        foreach (var bank in expected)
        {
            if (!blast.ContainsKey(bank)) { f.Add($"{bank}: missing from the chat blast"); continue; }
            if (!xls.ContainsKey(bank)) { f.Add($"{bank}: missing from the workbook Runs sheet"); continue; }
            if (!mail.ContainsKey(bank)) { f.Add($"{bank}: missing from the sheet-style email body"); continue; }

            var run = DailyBlast.Find(s.Report, bank)!;
            var bb = blast[bank]; var bx = xls[bank]; var bm = mail[bank];

            if (bb.Rows.Count != run.Rows.Count)
                f.Add($"{bank}: blast shows {bb.Rows.Count} row(s), the report publishes {run.Rows.Count}");
            if (bx.Rows.Count != run.Rows.Count)
                f.Add($"{bank}: workbook shows {bx.Rows.Count} row(s), the report publishes {run.Rows.Count}");
            if (bm.Rows.Count != run.Rows.Count)
                f.Add($"{bank}: email shows {bm.Rows.Count} row(s), the report publishes {run.Rows.Count}");

            // the fixing line
            string wantFix = run.RefPct is { } rp ? R(rp) : "";
            if (bb.FixingValue != wantFix)
                f.Add($"{bank}: blast fixing '{bb.FixingValue}' != published fixing '{wantFix}'");
            if (bx.FixingValue != wantFix)
                f.Add($"{bank}: workbook fixing '{bx.FixingValue}' != published fixing '{wantFix}'");
            if (bm.FixingValue != wantFix)
                f.Add($"{bank}: email fixing '{bm.FixingValue}' != published fixing '{wantFix}'");
            foreach (var (name, blk) in new[] { ("blast", bb), ("workbook", bx), ("email", bm) })
                if (blk.Rebased != run.RefRebased)
                    f.Add($"{bank}: {name} rebased marker is {blk.Rebased}, the run says {run.RefRebased}");

            int n = Math.Min(run.Rows.Count, Math.Min(bb.Rows.Count, Math.Min(bx.Rows.Count, bm.Rows.Count)));
            for (int i = 0; i < n; i++)
            {
                var m = run.Rows[i];
                var wantMid = m.Masked ? m.MaskLabel : R(m.MidPct);

                // blast: StartDate Mid Priced Step d1 w1 m1   (no Maturity, no dagger)
                var br = Flatten(bb.Rows[i]);
                Cell(f, bank, i, "blast", "StartDate", br, 0, D(m.Date));
                // the dagger that marks a guard-synthesized mid is NOT asserted here either way:
                // the emails carry it, the blast and the workbook do not, and which of those is
                // right is a question for a scenario, not something this invariant should freeze
                // into place (scenario 38)
                Cell(f, bank, i, "blast", "Mid", br, 1,
                    m.Masked ? m.MaskLabel : R(m.MidPct), allowDagger: true);
                if (!m.Masked)
                {
                    Cell(f, bank, i, "blast", "Priced", br, 2, Dash(m.PricedBp));
                    Cell(f, bank, i, "blast", "Step", br, 3, Dash(m.StepBp));
                    Cell(f, bank, i, "blast", "d1", br, 4, Dash(m.D1Bp));
                    Cell(f, bank, i, "blast", "w1", br, 5, Dash(m.W1Bp));
                    Cell(f, bank, i, "blast", "m1", br, 6, Dash(m.M1Bp));
                }

                // workbook / email: StartDate Maturity Mid Priced Step d1 w1 m1
                foreach (var (name, blk, dagger) in new[]
                         { ("workbook", bx, false), ("email", bm, true) })
                {
                    var rr = blk.Rows[i].Select(Render.Norm).ToArray();
                    Cell(f, bank, i, name, "StartDate", rr, 0, D(m.Date));
                    Cell(f, bank, i, name, "Maturity", rr, 1, m.EndDate is { } e ? D(e) : "");
                    Cell(f, bank, i, name, "Mid", rr, 2,
                        dagger ? wantMid : (m.Masked ? m.MaskLabel : R(m.MidPct)),
                        allowDagger: !dagger);
                    if (!m.Masked)
                    {
                        Cell(f, bank, i, name, "Priced", rr, 3, B(m.PricedBp));
                        Cell(f, bank, i, name, "Step", rr, 4, B(m.StepBp));
                        Cell(f, bank, i, name, "d1", rr, 5, B(m.D1Bp));
                        Cell(f, bank, i, name, "w1", rr, 6, B(m.W1Bp));
                        Cell(f, bank, i, name, "m1", rr, 7, B(m.M1Bp));
                    }
                    else
                        for (int c = 3; c < 8; c++)
                            Cell(f, bank, i, name, $"col{c}", rr, c, "");
                }
            }
        }

        // a flat change must print 0.0, never +0.0 (desk formatting rule)
        if (s.SheetHtml.Contains(">+0.0<")) f.Add("email renders '+0.0' for a flat change");
        if (s.BlastText.Contains(" +0.0")) f.Add("blast renders '+0.0' for a flat change");
    }

    private static string Dash(double? v) => v is { } x ? x.ToString(RunsTable.BpFmt, Inv) : "—";

    /// <summary>A masked blast row is "date  label"; a multi-word label ("Y/E Turn") splits on
    /// whitespace, so rejoin it.</summary>
    private static string[] Flatten(string[] cells)
    {
        if (cells.Length >= 3 && cells[1] == "Y/E" && cells[2] == "Turn")
            return new[] { cells[0], RateDesk.Core.MaskLabels.Turn };
        return cells;
    }

    private static void Cell(List<string> f, string bank, int i, string surface, string col,
        string[] row, int idx, string want, bool allowDagger = false)
    {
        string got = idx < row.Length ? Render.Norm(row[idx]) : "";
        if (allowDagger && got.EndsWith("†", StringComparison.Ordinal))
            got = got[..^1];
        if (got != want)
            f.Add($"{bank} row {i + 1}: {surface} {col} shows '{got}', the report publishes '{want}'");
    }

    // ---------------------------------------------------------------- F. front table

    private static void FrontTable(Surfaces s, List<string> f)
    {
        var rows = Render.EmailFront(s.SheetHtml);
        if (rows.Count != s.Report.Fronts.Count)
        {
            f.Add($"front table renders {rows.Count} line(s), the report holds {s.Report.Fronts.Count}");
            return;
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var fr = s.Report.Fronts[i];
            var r = rows[i].Select(Render.Norm).ToArray();
            string bank = $"{fr.Bank} {fr.Ccy}";
            if (r[0] != bank) f.Add($"front line {i + 1}: bank cell '{r[0]}' != '{bank}'");
            string wantDec = fr.Decision is { } dd ? D(dd) : D(fr.StartDate) + "*";
            if (r[1] != wantDec)
                f.Add($"front {fr.Bank}: decision cell '{r[1]}' != '{wantDec}'");
            if (r[2] != D(fr.StartDate))
                f.Add($"front {fr.Bank}: start cell '{r[2]}' != '{D(fr.StartDate)}'");
            string wantMid = fr.Masked ? fr.MaskLabel : R(fr.MidPct);
            if (r[3] != wantMid) f.Add($"front {fr.Bank}: mid cell '{r[3]}' != '{wantMid}'");
            string wantFix = fr.RefPct is { } rp ? R(rp) + (fr.RefRebased ? "†" : "") : "";
            if (r[4] != wantFix) f.Add($"front {fr.Bank}: fixing cell '{r[4]}' != '{wantFix}'");
            string wantPriced = fr.Masked ? "" : B(fr.PricedBp);
            if (r[5] != wantPriced) f.Add($"front {fr.Bank}: priced cell '{r[5]}' != '{wantPriced}'");
            string wantPct = fr.Masked || fr.PricedBp is not { } pv
                ? "" : (pv / 25.0 * 100.0).ToString("+0;-0;0", Inv) + "%";
            if (r[6] != wantPct) f.Add($"front {fr.Bank}: % of 25bp cell '{r[6]}' != '{wantPct}'");
        }
        if (s.Report.Fronts.Any(x => x.Decision == null)
            && !s.SheetHtml.Contains("swap-period start shown"))
            f.Add("a front line shows a start-only date but the '*' footnote is missing");
        if (s.Report.Fronts.Any(x => x.RefRebased) && !s.SheetHtml.Contains("re-based onto"))
            f.Add("a front line carries the rebased dagger but the '†' footnote is missing");
    }

    // ---------------------------------------------------------------- G. card email

    private static void CardEmail(Surfaces s, List<string> f)
    {
        var cards = Render.Cards(s.WeeklyHtml);

        foreach (var run in s.Report.Runs)
        {
            var bank = run.Title.Split('·')[0].Trim();

            foreach (var m in run.Rows.Where(x => !x.Masked))
                if (!s.WeeklyText.Contains(R(m.MidPct)))
                    f.Add($"{bank} {D(m.Date)}: mid {R(m.MidPct)} is missing from the plaintext email");

            if (!cards.TryGetValue(bank, out var c))
            { f.Add($"{bank}: no meeting card in the card email"); continue; }

            string wantFix = run.RefPct is { } rp ? R(rp) : "";
            if (c.FixingValue != wantFix)
                f.Add($"{bank}: card email fixing '{c.FixingValue}' != published fixing '{wantFix}'");
            if (c.Rebased != run.RefRebased)
                f.Add($"{bank}: card email rebased marker is {c.Rebased}, the run says {run.RefRebased}");
            if (c.Rows.Count != run.Rows.Count)
            {
                f.Add($"{bank}: card email shows {c.Rows.Count} row(s), the report publishes {run.Rows.Count}");
                continue;
            }
            for (int i = 0; i < run.Rows.Count; i++)
            {
                var m = run.Rows[i];
                var r = c.Rows[i].Select(Render.Norm).ToArray();
                Cell(f, bank, i, "card email", "StartDate", r, 0, D(m.Date));
                if (m.Masked)
                {
                    Cell(f, bank, i, "card email", "Mid", r, 1, m.MaskLabel);
                    for (int k = 2; k < 7; k++) Cell(f, bank, i, "card email", $"col{k}", r, k, "");
                    continue;
                }
                Cell(f, bank, i, "card email", "Mid", r, 1, R(m.MidPct));
                Cell(f, bank, i, "card email", "Priced", r, 2, B(m.PricedBp));
                Cell(f, bank, i, "card email", "Step", r, 3, B(m.StepBp));
                Cell(f, bank, i, "card email", "1d", r, 4, B(m.D1Bp));
                Cell(f, bank, i, "card email", "1w", r, 5, B(m.W1Bp));
                Cell(f, bank, i, "card email", "1m", r, 6, B(m.M1Bp));
            }
        }

        // ...and the card email's own front table
        var fronts = Render.CardFront(s.WeeklyHtml);
        if (fronts.Count != s.Report.Fronts.Count)
        {
            f.Add($"card email front table renders {fronts.Count} line(s), the report holds " +
                  $"{s.Report.Fronts.Count}");
            return;
        }
        for (int i = 0; i < fronts.Count; i++)
        {
            var fr = s.Report.Fronts[i];
            var r = fronts[i].Select(Render.Norm).ToArray();
            if (!r[0].StartsWith(fr.Bank, StringComparison.Ordinal))
                f.Add($"card front line {i + 1}: bank cell '{r[0]}' does not name {fr.Bank}");
            string wantDec = fr.Decision is { } dd ? D(dd) : D(fr.StartDate) + " *";
            if (r[1] != wantDec) f.Add($"card front {fr.Bank}: decision cell '{r[1]}' != '{wantDec}'");
            if (r[2] != D(fr.StartDate))
                f.Add($"card front {fr.Bank}: start cell '{r[2]}' != '{D(fr.StartDate)}'");
            if (r[3] != (fr.Masked ? fr.MaskLabel : R(fr.MidPct)))
                f.Add($"card front {fr.Bank}: mid cell '{r[3]}' != " +
                      $"'{(fr.Masked ? fr.MaskLabel : R(fr.MidPct))}'");
            string wantFix2 = fr.RefPct is { } rp2 ? R(rp2) + (fr.RefRebased ? "†" : "") : "";
            if (r[4] != wantFix2) f.Add($"card front {fr.Bank}: fixing cell '{r[4]}' != '{wantFix2}'");
            if (!fr.Masked)
            {
                if (r[5] != B(fr.PricedBp))
                    f.Add($"card front {fr.Bank}: priced cell '{r[5]}' != '{B(fr.PricedBp)}'");
                string wantPct = fr.PricedBp is { } pv
                    ? (pv / 25.0 * 100.0).ToString("+0;-0;0", Inv) + "%" : "";
                if (r[6] != wantPct)
                    f.Add($"card front {fr.Bank}: % of 25bp cell '{r[6]}' != '{wantPct}'");
            }
        }
    }

    // ---------------------------------------------------------------- G2. dashboards

    /// <summary>The dashboard meeting strip derives its rows a DIFFERENT way from the email -
    /// config period starts and stored closes, rather than ticker maturities and live mids. The
    /// two products go out on the same day, so they must at least agree on WHICH MEETING IS
    /// NEXT. A disagreement there means the desk publishes two different front meetings.</summary>
    private static void Dashboards(Surfaces s, List<string> f)
    {
        foreach (var (bank, strip) in s.Strips)
        {
            var run = DailyBlast.Find(s.Report, bank);
            if (run == null || run.Rows.Count == 0 || strip.Rows.Count == 0) continue;
            if (strip.Rows[0].Contract.Date != run.Rows[0].Date.Date)
                f.Add($"{bank}: the dashboard strip's front meeting is " +
                      $"{D(strip.Rows[0].Contract)} but the email publishes {D(run.Rows[0].Date)}");
            foreach (var r in strip.Rows)
                if (r.Mid is { } m && !double.IsFinite(m))
                    f.Add($"{bank}: dashboard strip row {D(r.Contract)} has a non-finite level");
        }
    }

    // ---------------------------------------------------------------- H. frozen report

    private static void RoundTrip(Surfaces s, List<string> f)
    {
        var path = Path.Combine(s.ArtifactDir, "report.json");
        if (!File.Exists(path)) { f.Add("the frozen report was not written"); return; }
        var back = ReportStore.Load(path);
        if (back == null) { f.Add("the frozen report does not load back"); return; }
        if (back.Runs.Count != s.Report.Runs.Count)
            f.Add($"frozen report holds {back.Runs.Count} run(s), the live one {s.Report.Runs.Count}");
        if (back.Fronts.Count != s.Report.Fronts.Count)
            f.Add($"frozen report holds {back.Fronts.Count} front line(s), the live one {s.Report.Fronts.Count}");
        for (int i = 0; i < Math.Min(back.Runs.Count, s.Report.Runs.Count); i++)
        {
            var a = s.Report.Runs[i]; var b = back.Runs[i];
            if (a.Rows.Count != b.Rows.Count)
            { f.Add($"{a.Title}: frozen report holds {b.Rows.Count} row(s), the live one {a.Rows.Count}"); continue; }
            if ((a.RefPct ?? -1) != (b.RefPct ?? -1)) f.Add($"{a.Title}: fixing did not round-trip");
            if (a.RefRebased != b.RefRebased) f.Add($"{a.Title}: rebased flag did not round-trip");
            for (int k = 0; k < a.Rows.Count; k++)
            {
                var x = a.Rows[k]; var y = b.Rows[k];
                if (x.Date != y.Date || Math.Abs(x.MidPct - y.MidPct) > 1e-9
                    || (x.PricedBp ?? 0) != (y.PricedBp ?? 0) || (x.StepBp ?? 0) != (y.StepBp ?? 0)
                    || (x.D1Bp ?? 0) != (y.D1Bp ?? 0) || (x.W1Bp ?? 0) != (y.W1Bp ?? 0)
                    || (x.M1Bp ?? 0) != (y.M1Bp ?? 0) || x.TurnPeriod != y.TurnPeriod || x.Rejected != y.Rejected)
                    f.Add($"{a.Title} {D(x.Date)}: the row did not round-trip through the frozen report");
            }
        }
    }
}
