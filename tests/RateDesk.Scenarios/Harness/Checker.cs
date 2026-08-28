using System.Globalization;
using RateDesk.Core;
using RateDesk.Weekly.Core.Daily;

namespace RateDesk.Scenarios.Harness;

/// <summary>Compares a scenario's stated expectations against what the app published. Expected
/// values are written out as NUMBERS by the scenario author, derived by hand from the synthetic
/// market - never by calling the code under test.</summary>
public static class Checker
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double BpTol = 0.05;
    private const double RateTol = 1e-9;

    private static string R(double? v) => v is { } x ? x.ToString("0.000", Inv) : "(blank)";
    private static string B(double? v) => v is { } x ? x.ToString("+0.0;-0.0;0.0", Inv) : "(blank)";
    private static string D(DateTime? d) => d is { } x ? x.ToString("dd-MMM-yy", Inv) : "(blank)";

    public static List<string> Run(Surfaces s)
    {
        var f = new List<string>();

        foreach (var e in s.Spec.Expect)
        {
            var run = s.Run(e.Bank);
            var front = s.Front(e.Bank);

            if (e.NoRun)
            {
                if (run != null && run.Rows.Count > 0)
                    f.Add($"{e.Bank}: expected NO published run, got {run.Rows.Count} row(s)");
                continue;
            }
            if (run == null) { f.Add($"{e.Bank}: no run in the report at all"); continue; }

            if (e.Fixing is { } wf)
            {
                if (run.RefPct is not { } gf) f.Add($"{e.Bank}: expected fixing {R(wf)}, none published");
                else if (Math.Abs(gf - wf) > RateTol) f.Add($"{e.Bank}: fixing {R(gf)} != expected {R(wf)}");
            }
            if (e.Rebased is { } wr && run.RefRebased != wr)
                f.Add($"{e.Bank}: fixing re-based flag is {run.RefRebased}, expected {wr}");

            if (e.NoFront && front != null)
                f.Add($"{e.Bank}: expected no front-table line, got one starting {D(front.StartDate)}");

            if (e.Front is { } fe)
            {
                if (front == null) f.Add($"{e.Bank}: expected a front-table line, there is none");
                else
                {
                    if (!Any.Is(fe.Decision))
                    {
                        if (fe.Decision is { } wd)
                        {
                            if (front.Decision != wd)
                                f.Add($"{e.Bank} front: decision {D(front.Decision)} != expected {D(wd)}");
                        }
                        else if (front.Decision != null)
                            f.Add($"{e.Bank} front: expected NO decision date (a start-only '*' line), " +
                                  $"got {D(front.Decision)}");
                    }
                    if (!Any.Is(fe.Start) && front.StartDate != fe.Start)
                        f.Add($"{e.Bank} front: start {D(front.StartDate)} != expected {D(fe.Start)}");
                    if (!Any.Is(fe.Mid) && Math.Abs(front.MidPct - fe.Mid) > RateTol)
                        f.Add($"{e.Bank} front: mid {R(front.MidPct)} != expected {R(fe.Mid)}");
                    CmpBp(f, $"{e.Bank} front fixing", front.RefPct, fe.Fixing, RateTol * 100);
                    CmpBp(f, $"{e.Bank} front Priced", front.PricedBp, fe.Priced, BpTol);
                    if (front.RefRebased != fe.Rebased)
                        f.Add($"{e.Bank} front: re-based flag is {front.RefRebased}, expected {fe.Rebased}");
                    if (front.TurnPeriod != fe.Turn)
                        f.Add($"{e.Bank} front: turn flag is {front.TurnPeriod}, expected {fe.Turn}");
                }
            }

            if (e.RowCount is { } rc && run.Rows.Count != rc)
                f.Add($"{e.Bank}: published {run.Rows.Count} row(s), expected {rc}");

            if (e.Rows is { } rows)
            {
                if (run.Rows.Count != rows.Count)
                    f.Add($"{e.Bank}: published {run.Rows.Count} row(s), expected {rows.Count} " +
                          $"[got {string.Join(", ", run.Rows.Select(x => D(x.Date)))}]");
                for (int i = 0; i < Math.Min(rows.Count, run.Rows.Count); i++)
                {
                    var w = rows[i]; var g = run.Rows[i];
                    string tag = $"{e.Bank} row {i + 1} ({D(w.Start)})";
                    if (g.Date != w.Start)
                        f.Add($"{tag}: start is {D(g.Date)}, expected {D(w.Start)}");
                    if (!Any.Is(w.End))
                    {
                        if (w.End is { } we)
                        {
                            if (g.EndDate != we) f.Add($"{tag}: maturity {D(g.EndDate)} != expected {D(we)}");
                        }
                        else if (g.EndDate != null)
                            f.Add($"{tag}: expected a BLANK maturity, got {D(g.EndDate)}");
                    }
                    if (g.TurnPeriod != w.Turn)
                        f.Add($"{tag}: turn flag is {g.TurnPeriod}, expected {w.Turn}");
                    if (!w.Turn)
                    {
                        if (!Any.Is(w.Mid) && w.Mid is { } wm && Math.Abs(g.MidPct - wm) > RateTol)
                            f.Add($"{tag}: mid {R(g.MidPct)} != expected {R(wm)}");
                        CmpBp(f, tag + " Priced", g.PricedBp, w.Priced, BpTol);
                        CmpBp(f, tag + " Step", g.StepBp, w.Step, BpTol);
                        CmpBp(f, tag + " d1", g.D1Bp, w.D1, BpTol);
                        CmpBp(f, tag + " w1", g.W1Bp, w.W1, BpTol);
                        CmpBp(f, tag + " m1", g.M1Bp, w.M1, BpTol);
                    }
                }
            }
        }

        // ORDINAL, not case-insensitive: scenarios write NotesNotContain("CHECK") meaning the
        // PREFIX, and a case-insensitive match also hits the ordinary English word inside a
        // sentence ("check the calendars against ..."), which is not a CHECK note at all.
        foreach (var want in s.Spec.NotesContain)
            if (!s.Notes.Any(n => n.Contains(want, StringComparison.Ordinal)))
                f.Add($"expected a note containing '{want}'; notes were: " +
                      (s.Notes.Count == 0 ? "(none)" : string.Join(" || ", s.Notes)));

        foreach (var bad in s.Spec.NotesNotContain)
            foreach (var n in s.Notes.Where(n => n.Contains(bad, StringComparison.Ordinal)))
                f.Add($"unexpected note containing '{bad}': {n}");

        foreach (var (bank, day, start, rate, d1) in s.Spec.HistoryRowExpect)
        {
            if (!s.HistoryRows.TryGetValue(bank, out var rows))
            { f.Add($"{bank}: no history rows built (set CheckHistoryRows)"); continue; }
            var hit = rows.FirstOrDefault(r => r.Day.Date == day.Date && r.Start.Date == start.Date);
            if (hit == null)
            {
                f.Add($"{bank} history: no row for day {D(day)} / period {D(start)} " +
                      $"(built {rows.Count} row(s) over {string.Join(",", rows.Select(r => D(r.Day)).Distinct())})");
                continue;
            }
            if (Math.Abs(hit.Rate - rate) > RateTol)
                f.Add($"{bank} history {D(day)}/{D(start)}: rate {R(hit.Rate)} != expected {R(rate)}");
            CmpBp(f, $"{bank} history {D(day)}/{D(start)} d1", hit.D1, d1, BpTol);
        }

        foreach (var custom in s.Spec.Custom)
        {
            try { f.AddRange(custom(s)); }
            catch (Exception ex) { f.Add("custom check threw: " + ex.Message); }
        }

        return f;
    }

    private static void CmpBp(List<string> f, string tag, double? got, double? want, double tol)
    {
        if (Any.Is(want)) return;
        if (want is { } w)
        {
            if (got is not { } g) f.Add($"{tag}: blank, expected {B(w)}");
            else if (Math.Abs(g - w) > tol) f.Add($"{tag}: {B(g)} != expected {B(w)}");
        }
        else if (got.HasValue) f.Add($"{tag}: expected BLANK, got {B(got)}");
    }
}
