using System.Text.Json;
using System.Text.Json.Nodes;

namespace RateDesk.Scenarios.Harness;

/// <summary>Writes the scenario's config\meetings.json next to the harness exe - the SAME
/// override path the shipped app documents ("a config\meetings.json next to the exe overrides
/// this file"). Each run starts from the REAL shipped entry for the bank, so the ticker roots,
/// contributor sources, fixing tickers, day counts and guard futures are the production ones;
/// only the calendar (dates / pastDates / decisionDates / decision time) and a handful of
/// explicitly named flags are replaced.
///
/// It must run BEFORE anything touches MeetingsStore.Schedules, which caches for the life of
/// the process. That is why every scenario gets its own process.</summary>
public static class ConfigWriter
{
    public const string TemplateFile = "meetings.template.json";

    public static string Write(ScenarioSpec spec)
    {
        var tplPath = Path.Combine(AppContext.BaseDirectory, TemplateFile);
        if (!File.Exists(tplPath))
            throw new FileNotFoundException(
                $"{TemplateFile} is missing from the output directory - the csproj copies the " +
                "shipped config\\meetings.json there.", tplPath);

        var doc = JsonNode.Parse(File.ReadAllText(tplPath), null, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!.AsObject();

        var template = doc["runs"]!.AsArray();
        var runs = new JsonArray();

        foreach (var bank in spec.Banks)
        {
            var src = template.FirstOrDefault(n =>
                          string.Equals(n?["name"]?.GetValue<string>(), bank.Bank,
                              StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException(
                          $"scenario {spec.Id}: no shipped run named '{bank.Bank}' in {TemplateFile}");

            var run = JsonNode.Parse(src.ToJsonString())!.AsObject();

            run["dates"] = Dates(bank.Dates);
            run["pastDates"] = Dates(bank.PastDates);
            run["decisionDates"] = Dates(bank.DecisionDates);
            if (bank.DecisionTimeLondon != null) run["decisionTimeLondon"] = bank.DecisionTimeLondon;
            if (bank.RollsAtPeriodStart is { } rp) run["rollsAtPeriodStart"] = rp;
            if (bank.MarkTurnPeriods is { } mt) run["markTurnPeriods"] = mt;
            if (bank.TrustConfigDates is { } tc) run["trustConfigDates"] = tc;
            if (bank.Source != null) run["source"] = bank.Source;
            if (bank.DisableGuardFutures) run.Remove("guardFutures");

            runs.Add(run);
        }

        var outObj = new JsonObject { ["runs"] = runs };
        var dir = Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "meetings.json");
        // ToJsonString(options) needs a TypeInfoResolver on a parsed node; go through a document
        // to pretty-print instead
        using var parsed = JsonDocument.Parse(outObj.ToJsonString());
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            parsed.WriteTo(w);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static JsonArray Dates(IEnumerable<DateTime> ds)
    {
        var a = new JsonArray();
        foreach (var d in ds.OrderBy(d => d))
            a.Add(d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return a;
    }
}
