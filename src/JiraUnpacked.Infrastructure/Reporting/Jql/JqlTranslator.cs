using System.Text;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// A filter as JQL: <c>project = "KEY" AND (tree) ORDER BY …</c>. Pure — the ids it needs come in
/// a <see cref="JqlLookup"/>, "now" from the clock — so every row of the spec's table is a unit test.
/// It expects a filter IssueFilterCompiler has already accepted (IssueJqlService checks first), yet
/// never throws on a shape it does not expect: that part is left out, with a warning.
///
/// A part JQL cannot express is LEFT OUT, never approximated silently, and the warning says which
/// way that moves the result (see <see cref="TranslateGroup"/>). Split by concern: this file walks
/// the tree and sorts; .Fields, .Lookups, .Dates and .History translate conditions.
/// </summary>
public sealed partial class JqlTranslator
{
    private readonly IClock _clock;

    /// <summary>The clock fixes the quarters, which JQL has no functions for (see .Dates).</summary>
    public JqlTranslator(IClock clock) => _clock = clock;

    public JqlTranslation Translate(
        string projectKey, FilterGroup? filter, string? sortBy, bool sortDescending, JqlLookup lookup)
    {
        var run = new Run(lookup);
        string? where = null;
        if (filter is not null && filter.Disabled != true)
        {
            var (clause, pending) = TranslateGroup(filter, run);
            where = clause;
            // Nothing of the filter is left: the project clause alone matches every issue in it.
            foreach (var what in pending) run.Warn(LeftOut(what, wider: true));
        }

        var jql = new StringBuilder("project = ").Append(JqlText.Quote(projectKey));
        if (where is not null) jql.Append(" AND ").Append(where);
        jql.Append(' ').Append(OrderBy(sortBy, sortDescending, run));
        return new JqlTranslation(jql.ToString(), run.Warnings);
    }

    /// <summary>Running state: the lookup, and the warnings in the order they arose, each once.</summary>
    private sealed class Run
    {
        public Run(JqlLookup lookup) => Lookup = lookup;

        public JqlLookup Lookup { get; }

        public List<string> Warnings { get; } = new();

        public void Warn(string message)
        {
            if (!Warnings.Contains(message)) Warnings.Add(message);
        }
    }

    /// <summary>A condition's JQL — or, when JQL cannot express it, why it is left out.</summary>
    private readonly record struct Clause(string? Jql, string? Why)
    {
        public static Clause Of(string jql) => new(jql, null);

        public static Clause Out(string why) => new(null, why);
    }

    /// <summary>
    /// The group's JQL, or null when nothing of it is left — then its left-out parts come back
    /// PENDING, because whether dropping them widens or narrows depends on the group holding this
    /// one. Where something is left, each part dropped here is settled by this group's own logic:
    /// under All a dropped part was a requirement (the JQL is wider), under Any an alternative
    /// (narrower). And and Or are monotone, so that direction holds all the way to the root.
    /// </summary>
    private (string? Jql, List<string> Pending) TranslateGroup(FilterGroup g, Run run)
    {
        var parts = new List<string>();
        var pending = new List<string>();
        foreach (var c in (g.Conditions ?? new List<FilterCondition>()).Where(c => c.Disabled != true))
        {
            var clause = TranslateCondition(c, run);
            if (clause.Jql is not null) parts.Add(clause.Jql);
            else pending.Add($"{Describe(c)} ({clause.Why})");
        }
        foreach (var child in (g.Groups ?? new List<FilterGroup>()).Where(x => x.Disabled != true))
        {
            var (jql, childPending) = TranslateGroup(child, run);
            if (jql is not null) parts.Add(jql);
            pending.AddRange(childPending);   // only a child with nothing left hands any back
        }
        if (parts.Count == 0) return (null, pending);

        foreach (var what in pending) run.Warn(LeftOut(what, wider: g.Logic == FilterLogic.And));
        return (JqlText.Join(parts, g.Logic == FilterLogic.Or ? "OR" : "AND"), new List<string>());
    }

    private static string LeftOut(string what, bool wider) => wider
        ? $"Left out {what}, so the JQL is wider than this filter: it can match issues this list does not show."
        : $"Left out {what}, so the JQL is narrower than this filter: it can miss issues this list shows.";

    /// <summary>Switches on the field's canonical name: the compiler accepts any casing, so the translator must too.</summary>
    private Clause TranslateCondition(FilterCondition c, Run run) =>
        (FilterFields.All.TryGetValue(c.Field, out var known) ? known.Name : c.Field) switch
    {
        "status" => Names("status", c),
        "issueType" => Names("issuetype", c),
        "priority" => Names("priority", c),
        "statusCategory" => Names("statusCategory", c),
        "assignee" => Assignee(c, run),
        "sprint" => Sprint(c, run),
        "team" => Team(c, run),
        "storyPoints" => StoryPoints(c, run),
        "createdAt" => DateField("created", c, run),
        "updatedAt" => DateField("updated", c, run),
        "summary" => Summary(c, run),
        "jiraKey" => Key(c),
        "parent" => Parent(c),
        "history" => History(c, run),
        _ => Clause.Out($"JQL has no field for '{c.Field}'"),
    };

    /// <summary>A condition as the warnings name it: "assignee Eq Ghost", "history ChangeCount of Sprint".</summary>
    private static string Describe(FilterCondition c)
    {
        var shown = new List<string>();
        if (c.Value is { ValueKind: not JsonValueKind.Null } v) shown.Add(Show(v));
        if (c.Values is { } vs) shown.AddRange(vs.Select(Show));
        return shown.Count == 0 ? $"\"{c.Field} {c.Op}\"" : $"\"{c.Field} {c.Op} {string.Join(", ", shown)}\"";
    }

    private static string Show(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => "(Empty)",
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Object when e.TryGetProperty("historyField", out var hf) && hf.ValueKind == JsonValueKind.String
            => $"of {hf.GetString()}",
        _ => e.GetRawText(),
    };

    /// <summary>The list's sort columns JQL orders by the same field (team and storyPoints go through their mappings).</summary>
    private static readonly IReadOnlyDictionary<string, string> SortFieldsJql = new Dictionary<string, string>
    {
        ["jiraKey"] = "key", ["summary"] = "summary", ["status"] = "status", ["statusCategory"] = "statusCategory",
        ["issueType"] = "issuetype", ["priority"] = "priority", ["assignee"] = "assignee", ["sprint"] = "sprint",
        ["createdAt"] = "created", ["updatedAt"] = "updated",
    };

    /// <summary>
    /// <c>ORDER BY field dir, key ASC</c> — the list's own tiebreaker — or by key alone when JQL
    /// cannot sort by the list's column.
    /// </summary>
    private static string OrderBy(string? sortBy, bool descending, Run run)
    {
        var name = sortBy ?? SortFields.Default;
        var field = name switch
        {
            "team" => JqlText.CustomField(run.Lookup.TeamField),
            "storyPoints" => JqlText.CustomField(run.Lookup.StoryPointsField),
            _ => SortFieldsJql.GetValueOrDefault(name),
        };
        if (field is null)
        {
            run.Warn($"The JQL cannot sort by '{name}', so it sorts by key.");
            return "ORDER BY key ASC";
        }
        var direction = descending ? "DESC" : "ASC";
        return field == "key" ? $"ORDER BY key {direction}" : $"ORDER BY {field} {direction}, key ASC";
    }
}
