using System.Reflection;
using System.Text.Json;
using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios;

/// <summary>Central-bank decision scenario runner.
///
///   RateDesk.Scenarios list
///   RateDesk.Scenarios run &lt;id&gt; [--out DIR]
///
/// ONE PROCESS PER SCENARIO, deliberately: config\meetings.json is read once per process
/// through a static Lazy, so a fresh process is the only way to give a scenario its own
/// decision calendar while still exercising the shipped loader, BuildWeekly and renderers.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            switch (verb)
            {
                case "list":
                    foreach (var sp in Registry.All())
                        Console.WriteLine($"{sp.Id,3}  {sp.Name,-46}  {sp.Question}");
                    Console.WriteLine($"\n{Registry.All().Count} scenario(s).");
                    return 0;

                case "run":
                    if (args.Length < 2) { Console.Error.WriteLine("usage: run <id|all> [--out DIR]"); return 2; }
                    if (args[1].Equals("all", StringComparison.OrdinalIgnoreCase)) return RunAll(args);
                    if (!int.TryParse(args[1], out var id))
                    { Console.Error.WriteLine("usage: run <id|all> [--out DIR]"); return 2; }
                    return RunOne(id, OutDir(args, id));

                case "all":
                    return RunAll(args);

                default:
                    Console.Error.WriteLine($"unknown verb '{verb}'");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HARNESS FAILURE: " + ex);
            return 3;
        }
    }

    /// <summary>Every scenario, one child process each, strictly serially: they share the single
    /// config\meetings.json next to the exe, so they must never overlap.</summary>
    private static int RunAll(string[] args)
    {
        string root = "results";
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--out") root = args[i + 1];
        Directory.CreateDirectory(root);

        var dll = Assembly.GetExecutingAssembly().Location;
        var host = Environment.ProcessPath ?? "dotnet";
        var ids = Registry.All().Select(s => s.Id).ToList();
        var rows = new List<(int Id, string Name, bool Pass, int Failures, bool MustFail)>();

        foreach (var id in ids)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(host)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (!host.EndsWith("RateDesk.Scenarios.exe", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(id.ToString());
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(root);

            using var p = System.Diagnostics.Process.Start(psi)!;
            string so = p.StandardOutput.ReadToEnd(), se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Console.Write(so);
            if (se.Length > 0) Console.Error.Write(se);

            var spec = Registry.All().First(s => s.Id == id);
            int fails = -1;
            var rp = Path.Combine(root, id.ToString("00"), "result.json");
            if (File.Exists(rp))
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rp));
                    fails = doc.RootElement.GetProperty("failures").GetArrayLength();
                }
                catch { }
            rows.Add((id, spec.Name, p.ExitCode == 0, fails, spec.MustFail));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| id | scenario | verdict | findings |");
        sb.AppendLine("|---:|---|---|---:|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Id:00} | {r.Name} | {(r.Pass ? "PASS" : "**FAIL**")}" +
                          $"{(r.MustFail ? " (control)" : "")} | {(r.Failures < 0 ? "?" : r.Failures.ToString())} |");
        int bad = rows.Count(r => !r.Pass);
        sb.AppendLine();
        sb.AppendLine($"{rows.Count - bad}/{rows.Count} passed.");
        File.WriteAllText(Path.Combine(root, "summary.md"), sb.ToString());
        Console.WriteLine();
        Console.WriteLine(sb.ToString());
        return bad == 0 ? 0 : 1;
    }

    private static string OutDir(string[] args, int id)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--out") return Path.Combine(args[i + 1], id.ToString("00"));
        return Path.Combine(AppContext.BaseDirectory, "results", id.ToString("00"));
    }

    private static int RunOne(int id, string outDir)
    {
        var spec = Registry.All().FirstOrDefault(s => s.Id == id);
        if (spec == null) { Console.Error.WriteLine($"no scenario {id}"); return 2; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var failures = new List<string>();
        List<string> notes = new();

        // the clock window matters only for scenarios that pin a decision to TODAY
        if (Cal.ClockWindowProblem() is { } clock) failures.Add("CLOCK: " + clock);

        Surfaces? s = null;
        try
        {
            s = Surfaces.Build(spec, outDir);
            notes = s.Notes.ToList();
            failures.AddRange(Invariants.Run(s));
            failures.AddRange(Checker.Run(s));
        }
        catch (Exception ex)
        {
            failures.Add("THREW: " + ex);
        }
        finally { s?.Dispose(); }

        bool pass = spec.MustFail ? failures.Count > 0 : failures.Count == 0;
        var result = new
        {
            id = spec.Id,
            name = spec.Name,
            question = spec.Question,
            mustFail = spec.MustFail,
            pass,
            failures,
            notes,
            ms = sw.ElapsedMilliseconds,
        };

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {spec.Id:00} {spec.Name}");
        if (spec.MustFail)
            Console.WriteLine($"       (positive control - expected to report failures; got {failures.Count})");
        foreach (var x in failures) Console.WriteLine("   ! " + x);
        if (notes.Count > 0)
        {
            Console.WriteLine("   -- run notes --");
            foreach (var n in notes) Console.WriteLine("   . " + n);
        }
        return pass ? 0 : 1;
    }
}
