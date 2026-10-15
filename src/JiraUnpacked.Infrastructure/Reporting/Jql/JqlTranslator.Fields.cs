using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>The fields JQL names as the filter does: status, type, priority, category by name; key; summary; parent.</summary>
public sealed partial class JqlTranslator
{
    /// <summary>What a condition named: strings, whether (Empty) was among them, and smart sprint keys.</summary>
    private sealed record Pick(List<string> Names, bool WantsEmpty, List<string> Smart);

    /// <summary><c>value</c> for Eq/Neq, <c>values</c> for In/NotIn; null = (Empty); <c>{"smart": key}</c> = a smart sprint value.</summary>
    private static Pick ReadPick(FilterCondition c)
    {
        var names = new List<string>();
        var smart = new List<string>();
        var wantsEmpty = false;
        IEnumerable<JsonElement> items = c.Op is FilterOperator.In or FilterOperator.NotIn
            ? c.Values ?? new List<JsonElement>()
            : c.Value is { } v ? new[] { v } : Array.Empty<JsonElement>();
        foreach (var e in items)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    if (!names.Contains(e.GetString()!)) names.Add(e.GetString()!);
                    break;
                case JsonValueKind.Null:
                    wantsEmpty = true;
                    break;
                case JsonValueKind.Object when e.TryGetProperty("smart", out var k) && k.ValueKind == JsonValueKind.String:
                    if (!smart.Contains(k.GetString()!)) smart.Add(k.GetString()!);
                    break;
            }
        }
        return new Pick(names, wantsEmpty, smart);
    }

    /// <summary>
    /// Every dictionary-like condition once its values are JQL literals. Is / is any of: the list,
    /// any <paramref name="extra"/> alternatives (smart sprints) and (Empty), OR-ed — with nothing at
    /// all to match it is left out with <paramref name="whyNone"/> (JQL has no "false"). Is not / is
    /// none of: the exclusions (and <paramref name="extra"/>) AND-ed. JQL's != and not in already
    /// leave empty values out, which is this list's rule too — so with nothing to exclude they mean
    /// "has a value", exactly as the list reads them.
    /// </summary>
    private static Clause Dictionary(
        string field, FilterOperator op, IReadOnlyList<string> literals, bool wantsEmpty,
        IReadOnlyList<string> extra, string whyNone)
    {
        var parts = new List<string>();
        switch (op)
        {
            case FilterOperator.IsNull:
                return Clause.Of($"{field} is EMPTY");
            case FilterOperator.IsNotNull:
                return Clause.Of($"{field} is not EMPTY");
            case FilterOperator.Eq or FilterOperator.In:
                if (literals.Count == 1 && op == FilterOperator.Eq) parts.Add($"{field} = {literals[0]}");
                else if (literals.Count > 0) parts.Add($"{field} in {JqlText.List(literals)}");
                parts.AddRange(extra);
                if (wantsEmpty) parts.Add($"{field} is EMPTY");
                return parts.Count == 0 ? Clause.Out(whyNone) : Clause.Of(JqlText.Join(parts, "OR"));
            case FilterOperator.Neq or FilterOperator.NotIn:
                if (literals.Count == 1 && op == FilterOperator.Neq) parts.Add($"{field} != {literals[0]}");
                else if (literals.Count > 0) parts.Add($"{field} not in {JqlText.List(literals)}");
                parts.AddRange(extra);
                return Clause.Of(parts.Count == 0 ? $"{field} is not EMPTY" : JqlText.Join(parts, "AND"));
            default:
                return Clause.Out($"JQL has no '{op}' for {field}");
        }
    }

    /// <summary>status, issuetype, priority, statusCategory: by name, as Jira matches them.</summary>
    private static Clause Names(string field, FilterCondition c)
    {
        var pick = ReadPick(c);
        return Dictionary(field, c.Op, pick.Names.Select(JqlText.Quote).ToList(), pick.WantsEmpty,
            Array.Empty<string>(), "it names no value");
    }

    private const string TextSearchWarning =
        "JQL text search is word-based, not substring: summary ~ matches whole words, so it can find more or fewer issues than \"contains\" does here.";

    private const string SearchReserved = "+-&|!(){}[]^~*?:\\/";

    /// <summary>
    /// Text for a <c>~</c> search: the characters Jira's text search (Lucene) reserves get a backslash,
    /// so "a:b" or "c++" search for those words instead of being parsed as syntax. Quote then doubles
    /// the backslash, which is how Jira expects it inside a JQL string.
    /// </summary>
    private static string EscapeSearch(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (SearchReserved.Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>summary: ~ for contains and starts with (a trailing *), NOT … ~ for does not contain.</summary>
    private static Clause Summary(FilterCondition c, Run run)
    {
        if (c.Op == FilterOperator.IsNull) return Clause.Of("summary is EMPTY");
        if (c.Op == FilterOperator.IsNotNull) return Clause.Of("summary is not EMPTY");
        if (c.Value is not { ValueKind: JsonValueKind.String } v || string.IsNullOrWhiteSpace(v.GetString()))
            return Clause.Out("it has no text to search for");
        var text = EscapeSearch(v.GetString()!);
        var clause = c.Op switch
        {
            FilterOperator.Contains => Clause.Of($"summary ~ {JqlText.Quote(text)}"),
            FilterOperator.NotContains => Clause.Of($"NOT summary ~ {JqlText.Quote(text)}"),
            FilterOperator.StartsWith => Clause.Of($"summary ~ {JqlText.Quote(text + "*")}"),
            _ => Clause.Out($"JQL has no '{c.Op}' for summary"),
        };
        if (clause.Jql is not null) run.Warn(TextSearchWarning);
        return clause;
    }

    /// <summary>jiraKey: whole keys only — JQL cannot match part of one.</summary>
    private static Clause Key(FilterCondition c)
    {
        if (c.Op is FilterOperator.Contains or FilterOperator.StartsWith)
            return Clause.Out("JQL cannot match part of an issue key");
        var keys = ReadPick(c).Names.Select(JqlText.Quote).ToList();
        if (keys.Count == 0) return Clause.Out("it names no key");
        return c.Op switch
        {
            FilterOperator.Eq => Clause.Of($"key = {keys[0]}"),
            FilterOperator.In => Clause.Of($"key in {JqlText.List(keys)}"),
            FilterOperator.NotIn => Clause.Of($"key not in {JqlText.List(keys)}"),
            _ => Clause.Out($"JQL has no '{c.Op}' for key"),
        };
    }

    /// <summary>parent: by the parent's Jira issue id, which is what the filter holds.</summary>
    private static Clause Parent(FilterCondition c) => c.Op switch
    {
        FilterOperator.IsNull => Clause.Of("parent is EMPTY"),
        FilterOperator.IsNotNull => Clause.Of("parent is not EMPTY"),
        FilterOperator.Eq when c.Value is { ValueKind: JsonValueKind.String } v => Clause.Of($"parent = {JqlText.Id(v.GetString()!)}"),
        _ => Clause.Out("it names no parent"),
    };
}
