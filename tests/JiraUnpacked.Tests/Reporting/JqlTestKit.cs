using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>Builders for the JQL translator's tests: conditions, groups, lookups, and the WHERE part of a result.</summary>
internal static class JqlTestKit
{
    /// <summary>A Thursday noon: this quarter is Oct–Dec 2026, the last one Jul–Sep.</summary>
    internal static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    internal static JqlTranslator Sut() => new(new FakeClock(Now));

    internal static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    /// <summary>A single-value condition; no value for IsNull / IsNotNull.</summary>
    internal static FilterCondition C(string field, FilterOperator op, object? value = null) =>
        new(field, op, value is null ? null : J(value), null);

    /// <summary>A list condition. A lone null must be passed as <c>new object?[] { null }</c> (params would take it as the array).</summary>
    internal static FilterCondition L(string field, FilterOperator op, params object?[] values) =>
        new(field, op, null, values.Select(J).ToList());

    internal static FilterGroup All(params FilterCondition[] cs) => new(FilterLogic.And, cs.ToList(), null);

    internal static FilterGroup Any(params FilterCondition[] cs) => new(FilterLogic.Or, cs.ToList(), null);

    internal static FilterGroup With(this FilterGroup g, params FilterGroup[] groups) => g with { Groups = groups.ToList() };

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Ids(params (string Name, string[] Ids)[] rows) =>
        rows.ToDictionary(r => r.Name, r => (IReadOnlyList<string>)r.Ids, StringComparer.Ordinal);

    internal static JqlTranslation Translate(
        FilterGroup? filter, JqlLookup? lookup = null, string? sortBy = null, bool sortDescending = true) =>
        Sut().Translate("ACME", filter, sortBy, sortDescending, lookup ?? JqlLookup.Empty);

    /// <summary>The part between <c>project = "ACME" AND </c> and <c> ORDER BY</c>.</summary>
    internal static string Where(JqlTranslation t)
    {
        const string head = "project = \"ACME\" AND ";
        Assert.StartsWith(head, t.Jql);
        return t.Jql[head.Length..t.Jql.LastIndexOf(" ORDER BY ", StringComparison.Ordinal)];
    }

    internal static string Where(FilterCondition c, JqlLookup? lookup = null) => Where(Translate(All(c), lookup));
}
