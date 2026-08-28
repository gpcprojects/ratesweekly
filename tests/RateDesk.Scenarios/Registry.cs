using System.Reflection;
using RateDesk.Scenarios.Harness;

namespace RateDesk.Scenarios;

/// <summary>Finds every scenario group by reflection: any public static class in the
/// RateDesk.Scenarios.Catalogue namespace with a public static method
/// <c>IEnumerable&lt;ScenarioSpec&gt; All()</c> contributes its scenarios. Groups are separate
/// files so several authors can add scenarios without ever touching the same one.</summary>
public static class Registry
{
    private static List<ScenarioSpec>? _all;

    public static IReadOnlyList<ScenarioSpec> All() => _all ??= Build();

    private static List<ScenarioSpec> Build()
    {
        var all = new List<ScenarioSpec>();
        foreach (var t in Assembly.GetExecutingAssembly().GetTypes()
                     .Where(t => t.IsAbstract && t.IsSealed && t.IsPublic
                                 && t.Namespace == "RateDesk.Scenarios.Catalogue")
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var m = t.GetMethod("All", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            if (m == null || !typeof(IEnumerable<ScenarioSpec>).IsAssignableFrom(m.ReturnType)) continue;
            all.AddRange((IEnumerable<ScenarioSpec>)m.Invoke(null, null)!);
        }

        var dupes = all.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
            throw new InvalidOperationException("duplicate scenario id(s): " + string.Join(", ", dupes));

        return all.OrderBy(s => s.Id).ToList();
    }
}
