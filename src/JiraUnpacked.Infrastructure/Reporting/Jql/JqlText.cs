using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// How values are written into JQL. Every string a filter carries goes through <see cref="Quote"/>:
/// nothing from a request reaches the JQL unquoted or unescaped.
/// </summary>
internal static class JqlText
{
    private static readonly Regex CustomFieldId = new(@"^customfield_([0-9]+)\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// "x" as a JQL string literal: <c>\</c> and <c>"</c> escaped, newline, carriage return and tab
    /// written <c>\n</c> <c>\r</c> <c>\t</c>, and any other control character <c>\uXXXX</c> — a raw
    /// one would break the line or be rejected by Jira.
    /// </summary>
    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

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
