using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// Smart sprint values: a JSON object resolved from Sprint.State through the resolver, never a
/// string, so a sprint NAMED "active" is still reachable by name.
/// </summary>
public sealed class SmartSprintFilterTests
{
    private static readonly Guid ActiveId = Guid.NewGuid();
    private static readonly Guid FutureId = Guid.NewGuid();
    private static readonly Guid ClosedId = Guid.NewGuid();
    private static readonly Guid NamedActiveId = Guid.NewGuid();   // a CLOSED sprint someone named "active"

    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(DateTime.UtcNow));

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);
    private static JsonElement Smart(string key) => J(new { smart = key });

    /// <summary>The compiler's resolver in miniature: names by name, smart values by state.</summary>
    private static DictNameResolver Sprints(bool withActive = true) => (lookup, names, ct) =>
    {
        var byState = new Dictionary<string, Guid[]>
        {
            ["active"] = withActive ? new[] { ActiveId } : Array.Empty<Guid>(),
            ["future"] = new[] { FutureId },
            ["closed"] = new[] { ClosedId, NamedActiveId },
        };
        var byName = new Dictionary<string, Guid> { ["active"] = NamedActiveId, ["Sprint 7"] = ClosedId };
        IReadOnlyList<Guid> ids = lookup switch
        {
            FilterLookups.SprintState => names.SelectMany(s => byState.TryGetValue(s, out var g) ? g : Array.Empty<Guid>()).ToList(),
            "sprint" => names.Where(byName.ContainsKey).Select(n => byName[n]).ToList(),
            _ => Array.Empty<Guid>(),
        };
        return Task.FromResult(ids);
    };

    private static async Task<Func<Issue, bool>> Pred(FilterCondition c, bool withActive = true)
    {
        var r = await Sut.BuildAsync(new[] { c }, Sprints(withActive), CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static async Task<string> Error(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, Sprints(), CancellationToken.None);
        return Assert.IsType<string>(r.Error);
    }

    private static Issue In(Guid? sprintId) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", SprintId = sprintId,
        JiraCreatedAt = DateTime.UtcNow, JiraUpdatedAt = DateTime.UtcNow,
    };

    private static FilterCondition Values(FilterOperator op, params JsonElement[] values) =>
        new("sprint", op, null, values.ToList());

    [Fact]
    public async Task Each_smart_key_means_its_states()
    {
        var active = await Pred(Values(FilterOperator.In, Smart("active")));
        Assert.True(active(In(ActiveId)));
        Assert.False(active(In(FutureId)));
        Assert.False(active(In(NamedActiveId)));
        Assert.False(active(In(null)));

        var open = await Pred(Values(FilterOperator.In, Smart("open")));
        Assert.True(open(In(ActiveId)));
        Assert.True(open(In(FutureId)));
        Assert.False(open(In(ClosedId)));

        var closed = await Pred(Values(FilterOperator.In, Smart("closed")));
        Assert.True(closed(In(ClosedId)));
        Assert.True(closed(In(NamedActiveId)));
        Assert.False(closed(In(ActiveId)));

        var future = await Pred(new FilterCondition("sprint", FilterOperator.Eq, Smart("future"), null));
        Assert.True(future(In(FutureId)));
        Assert.False(future(In(ActiveId)));
    }

    [Fact]
    public async Task Negated_smart_values_follow_the_empty_rule()
    {
        var notActive = await Pred(new FilterCondition("sprint", FilterOperator.Neq, Smart("active"), null));
        Assert.True(notActive(In(FutureId)));
        Assert.False(notActive(In(ActiveId)));
        Assert.False(notActive(In(null)));   // "is not the active sprint" says nothing about no sprint

        var notOpen = await Pred(Values(FilterOperator.NotIn, Smart("open")));
        Assert.True(notOpen(In(ClosedId)));
        Assert.False(notOpen(In(FutureId)));
        Assert.False(notOpen(In(null)));
    }

    [Fact]
    public async Task Smart_values_names_and_empty_mix_in_one_list()
    {
        var pred = await Pred(Values(FilterOperator.In, Smart("active"), J("Sprint 7"), J(null)));

        Assert.True(pred(In(ActiveId)));
        Assert.True(pred(In(ClosedId)));     // "Sprint 7", by name
        Assert.True(pred(In(null)));         // (Empty)
        Assert.False(pred(In(FutureId)));
    }

    [Fact]
    public async Task A_string_active_is_a_sprint_name_not_a_state()
    {
        var pred = await Pred(Values(FilterOperator.In, J("active")));

        Assert.True(pred(In(NamedActiveId)));
        Assert.False(pred(In(ActiveId)));
    }

    [Fact]
    public async Task No_active_sprint_matches_nothing_rather_than_failing()
    {
        var pred = await Pred(Values(FilterOperator.In, Smart("active")), withActive: false);

        Assert.False(pred(In(FutureId)));
        Assert.False(pred(In(ClosedId)));
        Assert.False(pred(In(null)));
    }

    [Fact]
    public async Task Smart_values_are_refused_off_sprint_unknown_or_malformed()
    {
        Assert.Contains("only available on 'sprint'",
            await Error(new FilterCondition("status", FilterOperator.In, null, new List<JsonElement> { Smart("active") })));
        Assert.Contains("only available on 'sprint'",
            await Error(new FilterCondition("team", FilterOperator.Eq, Smart("active"), null)));
        Assert.Contains("'current'", await Error(Values(FilterOperator.In, Smart("current"))));
        Assert.Contains("{\"smart\"", await Error(Values(FilterOperator.In, J(new { name = "Sprint 7" }))));
    }
}
