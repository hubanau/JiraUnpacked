using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// History conditions through the translator, run in memory: the history source is a list, the
/// predicate is compiled, and the correlated Any/Count run as LINQ to Objects. (The SQL they
/// become is pinned by HistorySqlShapeTests; the database path by HistoryConditionTests.)
/// </summary>
public sealed class HistoryTranslationTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Project = Guid.NewGuid();

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> NoDictionary(string field, IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    private static IssueChangeHistory Row(
        string issueId, string field, string? from, string? to,
        string? author = "Alice", DateTime? at = null, Guid? project = null) => new()
    {
        Id = Guid.NewGuid(), JiraProjectId = project ?? Project, JiraIssueId = issueId,
        JiraHistoryId = Guid.NewGuid().ToString("N"), Field = field, FromValue = from, ToValue = to,
        Author = author, ChangedAt = at ?? Now.AddDays(-1),
    };

    /// <summary>An issue of the test project, by Jira issue id. (Not named Issue: that would hide the type in nameof/expressions.)</summary>
    private static Issue I(string id) => new()
    {
        Id = Guid.NewGuid(), JiraProjectId = Project, JiraIssueId = id, JiraKey = $"A-{id}",
        JiraCreatedAt = Now, JiraUpdatedAt = Now,
    };

    private static FilterCondition History(FilterOperator op, object value, bool? disabled = null) =>
        new("history", op, J(value), null, disabled);

    private static Task<FilterBuildResult> BuildAsync(
        IEnumerable<FilterCondition> conditions, IEnumerable<IssueChangeHistory> rows, DictNameResolver? resolve = null) =>
        new IssueFilterTranslator(new FakeClock(Now))
            .BuildAsync(conditions.ToList(), resolve ?? NoDictionary, CancellationToken.None, rows.ToList().AsQueryable());

    private static async Task<Func<Issue, bool>> Pred(
        FilterCondition c, IEnumerable<IssueChangeHistory> rows, DictNameResolver? resolve = null)
    {
        var r = await BuildAsync(new[] { c }, rows, resolve);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static async Task<string> Error(FilterCondition c)
    {
        var r = await BuildAsync(new[] { c }, Array.Empty<IssueChangeHistory>());
        Assert.Null(r.Predicate);
        return Assert.IsType<string>(r.Error);
    }

    // ---- Changed, the field, the correlation ----

    [Fact]
    public async Task Changed_matches_an_issue_with_a_row_for_exactly_that_field()
    {
        var changed = await Pred(History(FilterOperator.Changed, new { historyField = "Sprint" }), new[]
        {
            Row("1", "Sprint", null, "S1"),
            Row("2", "sprint", null, "S1"),                          // another field: names are exact
            Row("3", "Sprint", null, "S1", project: Guid.NewGuid()), // same issue id, another project
        });

        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));
        Assert.False(changed(I("3")));
        Assert.False(changed(I("4")));   // no rows at all
    }

    [Fact]
    public async Task A_field_no_row_uses_simply_matches_nothing()
    {
        var changed = await Pred(History(FilterOperator.Changed, new { historyField = "Nope" }),
            new[] { Row("1", "Sprint", null, "S1") });
        Assert.False(changed(I("1")));
    }

    [Fact]
    public async Task The_history_value_and_its_field_are_validated()
    {
        Assert.Contains("historyField", await Error(new FilterCondition("history", FilterOperator.Changed, null, null)));
        Assert.Contains("historyField", await Error(History(FilterOperator.Changed, new { })));
        Assert.Contains("historyField", await Error(History(FilterOperator.Changed, new { historyField = "  " })));
        Assert.Contains("historyField", await Error(History(FilterOperator.Changed, new { historyField = 7 })));
        Assert.Contains("at most 100 characters",
            await Error(History(FilterOperator.Changed, new { historyField = new string('x', 101) })));
        Assert.StartsWith("'colour' is not part of 'Changed'.",
            await Error(History(FilterOperator.Changed, new { historyField = "Sprint", colour = "red" })));
        Assert.Contains("not allowed", await Error(new FilterCondition("history", FilterOperator.Eq, J("x"), null)));
    }

    [Fact]
    public async Task An_explicit_null_key_is_the_same_as_leaving_it_out()
    {
        var r = await BuildAsync(
            new[] { History(FilterOperator.Changed, new { historyField = "Sprint", colour = (string?)null }) },
            Array.Empty<IssueChangeHistory>());
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Without_a_history_source_a_history_condition_is_refused_not_ignored()
    {
        var r = await new IssueFilterTranslator(new FakeClock(Now)).BuildAsync(
            new[] { History(FilterOperator.Changed, new { historyField = "Sprint" }) }, NoDictionary, CancellationToken.None);
        Assert.Equal("History conditions are not available here.", r.Error);
    }

    [Fact]
    public async Task At_most_five_enabled_history_conditions_counted_over_the_whole_tree()
    {
        var c = History(FilterOperator.Changed, new { historyField = "Sprint" });
        var five = Enumerable.Repeat(c, 5).ToList();
        Assert.Null((await BuildAsync(five, Array.Empty<IssueChangeHistory>())).Error);

        // A disabled sixth does not count.
        var withDisabled = five.Append(History(FilterOperator.Changed, new { historyField = "x" }, disabled: true)).ToList();
        Assert.Null((await BuildAsync(withDisabled, Array.Empty<IssueChangeHistory>())).Error);

        // A sixth in a nested group does.
        var tree = new FilterGroup(FilterLogic.And, five,
            new List<FilterGroup> { new(FilterLogic.Or, new List<FilterCondition> { c }, null) });
        var r = await new IssueFilterTranslator(new FakeClock(Now)).BuildAsync(
            tree, NoDictionary, CancellationToken.None, Array.Empty<IssueChangeHistory>().AsQueryable());
        Assert.Equal("A filter may have at most 5 history conditions.", r.Error);
    }
}
