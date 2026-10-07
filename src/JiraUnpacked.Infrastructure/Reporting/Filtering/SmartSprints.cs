namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// Sprint values resolved from Sprint.State at query time rather than by name. On the wire a
/// smart value is a JSON OBJECT, {"smart": "active"} — never a string, so it cannot collide with
/// a sprint someone named "active". Keys are exact; states are compared exactly as Jira sends
/// them (lower case).
/// </summary>
internal static class SmartSprints
{
    internal sealed record Smart(string Key, string Label, IReadOnlyList<string> States);

    /// <summary>In the order the picker offers them.</summary>
    internal static readonly IReadOnlyList<Smart> All = new[]
    {
        new Smart("active", "Active sprint", new[] { "active" }),
        new Smart("future", "Future sprints", new[] { "future" }),
        new Smart("open", "Open sprints (active + future)", new[] { "active", "future" }),
        new Smart("closed", "Closed sprints", new[] { "closed" }),
    };

    internal static Smart? Find(string key) => All.FirstOrDefault(s => s.Key == key);
}
