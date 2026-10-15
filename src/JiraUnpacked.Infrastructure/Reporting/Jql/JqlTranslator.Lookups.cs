using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>assignee, sprint, team and story points: the fields JQL names by id or by custom field.</summary>
public sealed partial class JqlTranslator
{
    private const string SprintWarning =
        "Jira matches an issue to every sprint it has been in; this list matches only its latest sprint, so sprint conditions can match more issues in Jira.";

    /// <summary>
    /// names → literal ids through <paramref name="ids"/>, every id a name has. A name with no id
    /// matches no issue in this list either (the filter resolves names the same way, and this
    /// project's issues only point at this project's rows), so leaving it out of the JQL keeps the
    /// meaning — the warning still says so, because someone typed it.
    /// </summary>
    private static List<string> Resolve(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, IReadOnlyList<string>> ids,
        Func<string, string> literal, string what, Run run)
    {
        var literals = new List<string>();
        var unknown = new List<string>();
        foreach (var name in names)
        {
            if (ids.TryGetValue(name, out var found) && found.Count > 0)
            {
                foreach (var id in found)
                {
                    var l = literal(id);
                    if (!literals.Contains(l)) literals.Add(l);
                }
            }
            else
            {
                unknown.Add(name);
            }
        }
        if (unknown.Count > 0)
            run.Warn($"No {what} in this project's data is named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; this list matches no issue by that name either, so the JQL leaves it out.");
        return literals;
    }

    /// <summary>assignee: by account id — the only stable way JQL names a user.</summary>
    private static Clause Assignee(FilterCondition c, Run run)
    {
        var pick = ReadPick(c);
        var ids = Resolve(pick.Names, run.Lookup.AccountIds, JqlText.Quote, "Jira account", run);
        return Dictionary("assignee", c.Op, ids, pick.WantsEmpty, Array.Empty<string>(),
            "no Jira account in this project's data has that name");
    }

    /// <summary>The JQL for each smart sprint value, and for "not" it (empty excluded, as the list's rule says).</summary>
    private static readonly IReadOnlyDictionary<string, (string Is, string IsNot)> SmartSprintJql =
        new Dictionary<string, (string, string)>
        {
            ["active"] = ("(sprint in openSprints() AND sprint not in futureSprints())",
                          "(sprint not in openSprints() OR sprint in futureSprints())"),
            ["future"] = ("sprint in futureSprints()", "sprint not in futureSprints()"),
            ["open"] = ("sprint in openSprints()", "sprint not in openSprints()"),
            ["closed"] = ("sprint in closedSprints()", "sprint not in closedSprints()"),
        };

    /// <summary>sprint: names by Jira sprint id, smart values by Jira's sprint functions.</summary>
    private static Clause Sprint(FilterCondition c, Run run)
    {
        var pick = ReadPick(c);
        var ids = Resolve(pick.Names, run.Lookup.SprintIds, JqlText.Id, "sprint", run);
        var negative = c.Op is FilterOperator.Neq or FilterOperator.NotIn;
        var smart = pick.Smart.Where(SmartSprintJql.ContainsKey)
            .Select(k => negative ? SmartSprintJql[k].IsNot : SmartSprintJql[k].Is)
            .ToList();
        var clause = Dictionary("sprint", c.Op, ids, pick.WantsEmpty, smart, "no sprint in this project has that name");
        if (clause.Jql is not null && c.Op is not (FilterOperator.IsNull or FilterOperator.IsNotNull))
            run.Warn(SprintWarning);
        return clause;
    }

    /// <summary>A mapped custom field as cf[N] — or why it cannot be named.</summary>
    private static (string? Field, string? Why) Mapped(string? jiraFieldId, string role) =>
        jiraFieldId is null ? (null, $"no {role} field is mapped for this project")
        : JqlText.CustomField(jiraFieldId) is { } cf ? (cf, null)
        : (null, $"the {role} mapping '{jiraFieldId}' is not a custom field JQL can name");

    /// <summary>team: the project's Team custom field, by Jira team id.</summary>
    private static Clause Team(FilterCondition c, Run run)
    {
        var (field, why) = Mapped(run.Lookup.TeamField, "Team");
        if (field is null) return Clause.Out(why!);
        var pick = ReadPick(c);
        var ids = Resolve(pick.Names, run.Lookup.TeamIds, JqlText.Quote, "team", run);
        return Dictionary(field, c.Op, ids, pick.WantsEmpty, Array.Empty<string>(), "no team in this project has that name");
    }

    /// <summary>storyPoints: the project's Story Points custom field; comparisons and Between as they are.</summary>
    private static Clause StoryPoints(FilterCondition c, Run run)
    {
        var (field, why) = Mapped(run.Lookup.StoryPointsField, "Story Points");
        if (field is null) return Clause.Out(why!);
        switch (c.Op)
        {
            case FilterOperator.IsNull:
                return Clause.Of($"{field} is EMPTY");
            case FilterOperator.IsNotNull:
                return Clause.Of($"{field} is not EMPTY");
            case FilterOperator.Between:
            {
                var parts = new List<string>();
                if (Number(c.Values, 0) is { } lo) parts.Add($"{field} >= {JqlText.Number(lo)}");
                if (Number(c.Values, 1) is { } hi) parts.Add($"{field} <= {JqlText.Number(hi)}");
                return parts.Count == 0 ? Clause.Out("it has no bounds") : Clause.Of(JqlText.Join(parts, "AND"));
            }
        }
        if (c.Value is not { ValueKind: JsonValueKind.Number } v || !v.TryGetDecimal(out var d))
            return Clause.Out("it has no number to compare with");
        var symbol = c.Op switch
        {
            FilterOperator.Eq => "=",
            FilterOperator.Neq => "!=",
            FilterOperator.Lt => "<",
            FilterOperator.Lte => "<=",
            FilterOperator.Gt => ">",
            FilterOperator.Gte => ">=",
            _ => null,
        };
        return symbol is null
            ? Clause.Out($"JQL has no '{c.Op}' for story points")
            : Clause.Of($"{field} {symbol} {JqlText.Number(d)}");
    }

    private static decimal? Number(List<JsonElement>? values, int at) =>
        values is { } vs && vs.Count > at && vs[at].ValueKind == JsonValueKind.Number && vs[at].TryGetDecimal(out var d)
            ? d
            : null;
}
