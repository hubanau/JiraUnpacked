using System.Globalization;
using System.Text.RegularExpressions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// How values are written into JQL. Every string a filter carries goes through <see cref="Quote"/>:
/// nothing from a request reaches the JQL unquoted or unescaped.
/// </summary>
internal static class JqlText
{
    private static readonly Regex CustomFieldId = new(@"^customfield_(\d+)\z", RegexOptions.CultureInvariant);

    /// <summary>"x", with \ and " escaped — the two characters a JQL string literal reserves.</summary>
    internal static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>A Jira id: bare when it is all digits (sprint and issue ids, as Jira's own examples write them), quoted otherwise.</summary>
    internal static string Id(string id) => id.Length > 0 && id.All(char.IsAsciiDigit) ? id : Quote(id);

    internal static string List(IEnumerable<string> literals) => "(" + string.Join(", ", literals) + ")";

    internal static string Number(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>A UTC instant as a JQL date: "yyyy-MM-dd" at midnight, "yyyy-MM-dd HH:mm" otherwise.</summary>
    internal static string Date(DateTime utc) => Quote(utc.TimeOfDay == TimeSpan.Zero
        ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    /// <summary>"customfield_10016" → "cf[10016]"; anything else is not a field JQL can name this way → null.</summary>
    internal static string? CustomField(string? jiraFieldId) =>
        jiraFieldId is not null && CustomFieldId.Match(jiraFieldId) is { Success: true } m ? $"cf[{m.Groups[1].Value}]" : null;

    /// <summary>One part as it is; several joined by <paramref name="op"/> inside parentheses, so the result is always one operand.</summary>
    internal static string Join(IReadOnlyList<string> parts, string op) =>
        parts.Count == 1 ? parts[0] : "(" + string.Join($" {op} ", parts) + ")";
}
