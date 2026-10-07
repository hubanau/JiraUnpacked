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

    // ---- value matching ----

    [Theory]
    [InlineData("PLAT 101", true)]                       // equal
    [InlineData("PLAT 101, PLAT 102", true)]             // first element
    [InlineData("PLAT 100, PLAT 101", true)]             // last element
    [InlineData("PLAT 100, PLAT 101, PLAT 102", true)]   // middle element
    [InlineData("PLAT 1010", false)]                     // never a partial word
    [InlineData("PLAT 10, PLAT 1011", false)]
    [InlineData("XPLAT 101", false)]
    [InlineData(null, false)]
    public async Task A_value_matches_the_whole_stored_value_or_one_element_of_its_list(string? stored, bool matches)
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Labels", values = new[] { "PLAT 101" } }),
            new[] { Row("1", "Labels", null, stored) });
        Assert.Equal(matches, was(I("1")));
    }

    [Fact]
    public async Task A_shorter_name_never_matches_a_longer_one()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Labels", values = new[] { "PLAT 10" } }),
            new[] { Row("1", "Labels", "PLAT 101", "PLAT 101, PLAT 102") });
        Assert.False(was(I("1")));
    }

    [Fact]
    public async Task Was_looks_at_both_sides_of_every_change()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Team", values = new[] { "Red" } }), new[]
        {
            Row("1", "Team", "Red", "Blue"),    // left Red
            Row("2", "Team", "Blue", "Red"),    // arrived at Red
            Row("3", "Team", "Blue", "Green"),
        });
        Assert.True(was(I("1")));
        Assert.True(was(I("2")));
        Assert.False(was(I("3")));
    }

    [Fact]
    public async Task Empty_matches_a_null_or_empty_stored_value()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Team", values = new string?[] { null } }), new[]
        {
            Row("1", "Team", null, "Red"),
            Row("2", "Team", "", "Red"),
            Row("3", "Team", "Blue", "Red"),
        });
        Assert.True(was(I("1")));
        Assert.True(was(I("2")));
        Assert.False(was(I("3")));
    }

    [Theory]
    [InlineData("status", "status", nameof(Issue.StatusId))]
    [InlineData("assignee", "assignee", nameof(Issue.AssigneeId))]
    [InlineData("priority", "priority", nameof(Issue.PriorityId))]
    [InlineData("issuetype", "issueType", nameof(Issue.IssueTypeId))]
    [InlineData("Sprint", "sprint", nameof(Issue.SprintId))]
    public async Task Was_also_matches_the_current_value_of_the_five_fields_the_issue_holds(
        string historyField, string dictField, string fk)
    {
        var id = Guid.NewGuid();
        string? asked = null;
        DictNameResolver resolve = (field, names, ct) =>
        {
            asked = field;
            return Task.FromResult<IReadOnlyList<Guid>>(names.Contains("Blocked") ? new[] { id } : Array.Empty<Guid>());
        };
        var was = await Pred(History(FilterOperator.Was, new { historyField, values = new[] { "Blocked" } }),
            Array.Empty<IssueChangeHistory>(), resolve);

        Assert.Equal(dictField, asked);                 // resolved through the dictionary filters' own path
        var now = I("1");
        typeof(Issue).GetProperty(fk)!.SetValue(now, (Guid?)id);
        Assert.True(was(now));                          // never changed, but is Blocked now
        Assert.False(was(I("2")));
    }

    [Fact]
    public async Task Was_empty_on_a_current_value_field_matches_an_issue_with_no_value_now()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "status", values = new string?[] { null } }),
            Array.Empty<IssueChangeHistory>());
        var withStatus = I("2");
        withStatus.StatusId = Guid.NewGuid();

        Assert.True(was(I("1")));     // no status now
        Assert.False(was(withStatus));
    }

    [Fact]
    public async Task Other_fields_rely_on_history_alone()
    {
        var asked = false;
        DictNameResolver resolve = (_, _, _) => { asked = true; return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()); };
        await Pred(History(FilterOperator.Was, new { historyField = "Story Points", values = new[] { "3" } }),
            Array.Empty<IssueChangeHistory>(), resolve);
        Assert.False(asked);
    }

    // ---- Changed: from / to / by ----

    [Fact]
    public async Task Changed_requires_every_part_given_on_the_same_row()
    {
        var rows = new[]
        {
            Row("1", "status", "To Do", "Blocked", author: "Bob"),
            Row("2", "status", "To Do", "Done", author: "Bob"),
            Row("2", "status", "Done", "Blocked", author: "Alice"),   // to Blocked — but from Done, by Alice
            Row("3", "status", "To Do", "Blocked", author: null),
        };
        var changed = await Pred(History(FilterOperator.Changed, new
        {
            historyField = "status", from = new[] { "To Do" }, to = new[] { "Blocked" }, by = new[] { "Bob" },
        }), rows);

        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));   // the parts hold, but on different rows
        Assert.False(changed(I("3")));   // no author matches no name
    }

    [Fact]
    public async Task Changed_from_empty_finds_the_first_assignment()
    {
        var changed = await Pred(History(FilterOperator.Changed, new { historyField = "Team", from = new string?[] { null } }),
            new[] { Row("1", "Team", null, "Red"), Row("2", "Team", "", "Blue"), Row("3", "Team", "Red", "Blue") });
        Assert.True(changed(I("1")));
        Assert.True(changed(I("2")));
        Assert.False(changed(I("3")));
    }

    [Fact]
    public async Task Value_lists_are_validated()
    {
        FilterCondition Was(object value) => History(FilterOperator.Was, value);
        FilterCondition Changed(object value) => History(FilterOperator.Changed, value);

        Assert.Contains("'Was' needs 'values'", await Error(Was(new { historyField = "status" })));
        Assert.Contains("at least one value", await Error(Was(new { historyField = "status", values = Array.Empty<string>() })));
        Assert.Contains("at least one value", await Error(Changed(new { historyField = "status", from = Array.Empty<string>() })));
        Assert.Contains("at most 100 values", await Error(Was(new
        {
            historyField = "status", values = Enumerable.Range(0, 101).Select(i => $"v{i}").ToArray(),
        })));
        Assert.Contains("at most 255 characters", await Error(Was(new { historyField = "status", values = new[] { new string('x', 256) } })));
        Assert.Contains("(Empty) is not one", await Error(Changed(new { historyField = "status", by = new string?[] { null } })));
        Assert.Contains("must be a list", await Error(Changed(new { historyField = "status", to = "Done" })));
        Assert.Contains("must be a list", await Error(Was(new { historyField = "status", values = new object[] { 7 } })));
        Assert.StartsWith("'during' is not part of 'Was'.", await Error(Was(new
        {
            historyField = "status", values = new[] { "x" }, during = new { kind = "inLast", amount = 1, unit = "days" },
        })));
    }
}
