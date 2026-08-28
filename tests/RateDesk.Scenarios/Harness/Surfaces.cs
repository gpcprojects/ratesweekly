using System.Globalization;
using ClosedXML.Excel;
using RateDesk.Core;
using RateDesk.Core.Config;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Daily;

namespace RateDesk.Scenarios.Harness;

/// <summary>Everything one scenario produces: the report the app freezes, and every surface the
/// desk actually reads. Built by driving the PRODUCTION calls in the production order - the same
/// sequence DailyBuilder.Build runs after its Bloomberg snapshot:
///     BuildWeekly(meetingsOnly) -> CompoundedFixing.Stamp -> FuturesGuard -> OutlierGuard
/// then the renderers. Nothing here re-implements a number.</summary>
public sealed class Surfaces : IDisposable
{
    public required ScenarioSpec Spec { get; init; }
    public required WeeklyReport Report { get; init; }
    public required PricingService Svc { get; init; }
    public required HistoryStore Store { get; init; }
    public required ConfigStore Configs { get; init; }

    /// <summary>Per bank, the raw run result (CoD, mid source, warning, stale notes) - the layer
    /// under the report, so a failure can be localised.</summary>
    public Dictionary<string, MeetingRunResult> Runs { get; init; } = new();

    public required string SheetHtml { get; init; }
    public required string WeeklyHtml { get; init; }
    public required string WeeklyText { get; init; }
    public required string BlastText { get; init; }
    public required string BlastHtml { get; init; }
    public required List<RunsTable.Block> Blocks { get; init; }
    /// <summary>The Runs sheet as displayed strings, row-major.</summary>
    public required List<List<string>> Xlsx { get; init; }
    public Dictionary<string, List<DailyBook.HistRow>> HistoryRows { get; init; } = new();
    /// <summary>The DASHBOARD meeting strips - a separate derivation (config dates + store
    /// closes) from the email's (ticker maturities + live mids), and the other thing the desk
    /// publishes on a decision day.</summary>
    public Dictionary<string, RateDesk.Weekly.Core.Series.StripTable> Strips { get; init; } = new();
    /// <summary>What CalendarHealth would say. It runs in the WEEKLY UpdateEngine only
    /// (UpdateEngine.cs:100) - the daily run never calls it - so it is exposed here separately
    /// rather than folded into the report notes, which is exactly the distinction a scenario
    /// about a missing decision time needs to make.</summary>
    public List<string> CalendarWarnings { get; init; } = new();
    public List<string> SetupProblems { get; init; } = new();
    public List<string> BuildLog { get; init; } = new();
    public string ArtifactDir { get; init; } = "";

    public List<string> Notes => Report.Notes;

    public WeeklyRun? Run(string bank) => DailyBlast.Find(Report, bank);
    public WeeklyFront? Front(string bank) =>
        Report.Fronts.FirstOrDefault(f => f.Bank.Equals(bank, StringComparison.OrdinalIgnoreCase));
    public RunsTable.Block? Block(string bank) =>
        Blocks.FirstOrDefault(b => b.Bank.Equals(bank, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Store.Dispose();

    // ---------------------------------------------------------------- build

    public static Surfaces Build(ScenarioSpec spec, string artifactDir)
    {
        var problems = new List<string>();
        var log = new List<string>();

        ConfigWriter.Write(spec);

        var configs = ConfigStore.LoadEmbedded();
        var snap = new RatesSnapshot();
        var svc = new PricingService(configs, snap);

        Directory.CreateDirectory(artifactDir);
        var storePath = Path.Combine(artifactDir, "history.db");
        if (File.Exists(storePath)) File.Delete(storePath);
        var store = new HistoryStore(storePath);

        var hist = Market.Seed(spec, svc, snap, store, configs, problems, artifactDir);
        svc.History = hist;
        svc.ObservedShifts = RateDesk.Weekly.Core.Series.RungShiftScan.Bind(store);
        // the marks' own clock, when the scenario models a pinned snap (SnapDiscipline does this
        // in production from 16:15 London)
        if (spec.MarksAsOfLondon is { } mk)
            svc.MarksAsOfLondon = Cal.LondonNow().Date + mk;

        // --- the production sequence, minus the Bloomberg legs ---
        var rep = svc.BuildWeekly(meetingsPerRun: 8, meetingsOnly: true);
        CompoundedFixing.Stamp(rep, svc, configs, log.Add);
        rep.Notes.AddRange(FuturesGuard.Check(svc));
        rep.Notes.AddRange(OutlierGuard.Check(rep));
        rep.Notes.AddRange(RateDesk.Weekly.Core.Daily.DailyBuilder.LateAnnouncementNotes(svc));
        // the daily build folds the calendar guard into its notes (fix 2026-08-27) — mirror it
        var calWarnEarly = new List<string>();
        try { calWarnEarly.AddRange(CalendarHealth.Check(MeetingsStore.Schedules, snap, store, DateTime.Today)); }
        catch (Exception ex) { log.Add("CalendarHealth threw: " + ex); }
        rep.Notes.AddRange(calWarnEarly);

        var runs = new Dictionary<string, MeetingRunResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var bank in spec.Banks)
        {
            var sched = MeetingsStore.Schedules.First(s =>
                s.Name.Equals(bank.Bank, StringComparison.OrdinalIgnoreCase));
            try { runs[bank.Bank] = svc.MeetingRun(sched, 8); }
            catch (Exception ex) { log.Add($"MeetingRun({bank.Bank}) threw: {ex}"); }
        }

        // --- the surfaces ---
        var sheetHtml = SheetEmail.Body(rep, front: true, runs: true);
        var weeklyHtml = WeeklyEmail.Html(rep);
        var weeklyText = WeeklyEmail.PlainText(rep);
        var blastText = DailyBlast.Render(rep);
        var blastHtml = DailyBlast.Html(rep);
        var blocks = RunsTable.Build(rep);

        var grid = new List<List<string>>();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Runs");
            DailyBook.WriteRunsSheet(ws, rep);
            var used = ws.RangeUsed();
            if (used != null)
                foreach (var row in used.Rows())
                {
                    var line = new List<string>();
                    foreach (var c in row.Cells()) line.Add(Display(c));
                    grid.Add(line);
                }
            try { wb.SaveAs(Path.Combine(artifactDir, "Runs.xlsx")); } catch { }
        }

        var histRows = new Dictionary<string, List<DailyBook.HistRow>>(StringComparer.OrdinalIgnoreCase);
        if (spec.CheckHistoryRows)
            foreach (var bank in spec.Banks)
            {
                var sched = MeetingsStore.Schedules.First(s =>
                    s.Name.Equals(bank.Bank, StringComparison.OrdinalIgnoreCase));
                var run = DailyBlast.Find(rep, bank.Bank);
                var pat = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                if (run == null || pat == null) continue;
                try
                {
                    histRows[bank.Bank] = DailyBook.BankHistoryRows(
                        store, sched, run, pat, rep.AsOf, spec.HistoryDays);
                }
                catch (Exception ex) { log.Add($"BankHistoryRows({bank.Bank}) threw: {ex}"); }
            }

        var strips = new Dictionary<string, RateDesk.Weekly.Core.Series.StripTable>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var bank in spec.Banks)
        {
            var sched = MeetingsStore.Schedules.First(s =>
                s.Name.Equals(bank.Bank, StringComparison.OrdinalIgnoreCase));
            try
            {
                // PRODUCTION asOf: the render path uses store.LatestDate() - the newest close
                // in the whole store, which is normally the previous business day (today's
                // print is never booked as a close). MainWindow.xaml.cs:596, Cli/Program.cs:93.
                strips[bank.Bank] = RateDesk.Weekly.Core.Series.RollingStrip.ForMeetings(
                    sched, store, store.LatestDate() ?? DateTime.Today,
                    source: svc.MeetingSrc(sched));
            }
            catch (Exception ex) { log.Add($"RollingStrip({bank.Bank}) threw: {ex}"); }
        }

        var calWarn = calWarnEarly;

        // the frozen report the offline paths rebuild from must survive a round trip
        try
        {
            var rp = Path.Combine(artifactDir, "report.json");
            ReportStore.Save(rep, rp);
        }
        catch (Exception ex) { log.Add("ReportStore.Save threw: " + ex); }

        WriteArtifacts(artifactDir, sheetHtml, weeklyHtml, weeklyText, blastText, blastHtml, grid, rep);
        try
        {
            File.WriteAllText(Path.Combine(artifactDir, "calendar_health.txt"),
                calWarn.Count == 0 ? "(no calendar warnings)" : string.Join(Environment.NewLine, calWarn));
        }
        catch { }
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (bank, st) in strips)
            {
                sb.AppendLine($"{bank}: {st.Rows.Count} row(s)  asOf {st.AsOf:dd-MMM-yy}");
                foreach (var r in st.Rows)
                    sb.AppendLine($"   {r.Contract:dd-MMM-yy} mid={r.Mid?.ToString("0.000") ?? "-"} " +
                                  $"1w={r.WeekLevel?.ToString("0.000") ?? "-"} " +
                                  $"1m={r.MonthLevel?.ToString("0.000") ?? "-"} turn={r.Turn} tk={r.Ticker}");
                foreach (var n in st.Notes) sb.AppendLine("   note: " + n);
            }
            File.WriteAllText(Path.Combine(artifactDir, "strips.txt"), sb.ToString());
        }
        catch { }

        return new Surfaces
        {
            Spec = spec, Report = rep, Svc = svc, Store = store, Configs = configs,
            Runs = runs, SheetHtml = sheetHtml, WeeklyHtml = weeklyHtml, WeeklyText = weeklyText,
            BlastText = blastText, BlastHtml = blastHtml, Blocks = blocks, Xlsx = grid,
            HistoryRows = histRows, Strips = strips, CalendarWarnings = calWarn,
            SetupProblems = problems, BuildLog = log,
            ArtifactDir = artifactDir,
        };
    }

    private static string Display(IXLCell c)
    {
        if (c.IsEmpty()) return "";
        if (c.DataType == XLDataType.Number)
        {
            var fmt = c.Style.NumberFormat.Format;
            var v = c.GetDouble();
            return string.IsNullOrEmpty(fmt)
                ? v.ToString("0.####", CultureInfo.InvariantCulture)
                : v.ToString(fmt, CultureInfo.InvariantCulture);
        }
        if (c.DataType == XLDataType.DateTime)
            return c.GetDateTime().ToString(RunsTable.DateFmt, CultureInfo.InvariantCulture);
        return c.GetString();
    }

    private static void WriteArtifacts(string dir, string sheet, string html, string text,
        string blast, string blastHtml, List<List<string>> grid, WeeklyReport rep)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, "email_sheet.html"),
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>sheet email</title></head>" +
                "<body style=\"margin:14px;background:#fff\">" + sheet + "</body></html>");
            File.WriteAllText(Path.Combine(dir, "email_cards.html"),
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>card email</title></head>" +
                "<body style=\"margin:14px;background:#fff\">" + html + "</body></html>");
            File.WriteAllText(Path.Combine(dir, "email.txt"), text);
            File.WriteAllText(Path.Combine(dir, "blast.txt"), blast);
            File.WriteAllText(Path.Combine(dir, "blast.html"), blastHtml);
            File.WriteAllText(Path.Combine(dir, "runs_sheet.txt"),
                string.Join(Environment.NewLine, grid.Select(r => string.Join(" | ", r))));
            File.WriteAllText(Path.Combine(dir, "notes.txt"),
                string.Join(Environment.NewLine, rep.Notes));
        }
        catch { /* artifacts are a convenience */ }
    }
}
