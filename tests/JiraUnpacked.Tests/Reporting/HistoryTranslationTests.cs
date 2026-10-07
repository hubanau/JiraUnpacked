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

    // ---- windows ----

    [Fact]
    public async Task During_in_the_last_counts_from_now_minus_the_amount()
    {
        var changed = await Pred(History(FilterOperator.Changed, new
        {
            historyField = "Sprint", during = new { kind = "inLast", amount = 30, unit = "days" },
        }), new[]
        {
            Row("1", "Sprint", null, "S1", at: Now.AddDays(-30)),                  // the boundary itself is inside
            Row("2", "Sprint", null, "S1", at: Now.AddDays(-30).AddSeconds(-1)),
        });
        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));
    }

    [Fact]
    public async Task During_in_period_uses_the_zones_calendar()
    {
        // Now is Wed 2026-10-07 12:00 UTC. Last month in Warsaw (CEST) = [Aug 31 22:00Z, Sep 30 22:00Z).
        var changed = await Pred(History(FilterOperator.Changed, new
        {
            historyField = "Sprint", during = new { kind = "inPeriod", period = "lastMonth", timeZone = "Europe/Warsaw" },
        }), new[]
        {
            Row("1", "Sprint", null, "S1", at: new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc)),
            Row("2", "Sprint", null, "S1", at: new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc)),
            Row("3", "Sprint", null, "S1", at: new DateTime(2026, 9, 30, 21, 59, 59, DateTimeKind.Utc)),
        });
        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));
        Assert.True(changed(I("3")));
    }

    [Fact]
    public async Task During_between_includes_the_whole_last_day_and_either_bound_may_be_open()
    {
        var rows = new[]
        {
            Row("1", "Sprint", null, "S1", at: new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc)),
            Row("2", "Sprint", null, "S1", at: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            Row("3", "Sprint", null, "S1", at: new DateTime(2026, 8, 31, 23, 0, 0, DateTimeKind.Utc)),
        };
        FilterCondition Between(object during) => History(FilterOperator.Changed, new { historyField = "Sprint", during });

        var closed = await Pred(Between(new { kind = "between", from = "2026-09-01", to = "2026-09-30" }), rows);
        Assert.True(closed(I("1")));
        Assert.False(closed(I("2")));
        Assert.False(closed(I("3")));

        var openStart = await Pred(Between(new { kind = "between", from = (string?)null, to = "2026-09-30" }), rows);
        Assert.True(openStart(I("3")));
        Assert.False(openStart(I("2")));

        var openEnd = await Pred(Between(new { kind = "between", from = "2026-09-01" }), rows);
        Assert.True(openEnd(I("2")));
        Assert.False(openEnd(I("3")));
    }

    // ---- ChangeCount ----

    [Theory]
    [InlineData("Eq", 2, new[] { false, false, true, false })]
    [InlineData("Neq", 2, new[] { true, true, false, true })]
    [InlineData("Gt", 1, new[] { false, false, true, true })]
    [InlineData("Gte", 3, new[] { false, false, false, true })]
    [InlineData("Lt", 1, new[] { true, false, false, false })]
    [InlineData("Lte", 1, new[] { true, true, false, false })]
    [InlineData("Eq", 0, new[] { true, false, false, false })]
    public async Task ChangeCount_compares_the_number_of_rows_and_never_changed_counts_as_zero(
        string compare, int count, bool[] expected)
    {
        // Issue 0 never changed the field (only another one); issues 1, 2 and 3 changed it that many times.
        var rows = new[] { "1", "2", "2", "3", "3", "3" }.Select(id => Row(id, "Sprint", null, "S"))
            .Append(Row("0", "status", null, "Done"));
        var counted = await Pred(History(FilterOperator.ChangeCount, new { historyField = "Sprint", compare, count }), rows);
        Assert.Equal(expected, new[] { "0", "1", "2", "3" }.Select(id => counted(I(id))).ToArray());
    }

    [Fact]
    public async Task ChangeCount_counts_only_the_rows_inside_the_window()
    {
        var rows = new[]
        {
            Row("1", "Sprint", null, "S", at: Now.AddDays(-3)),
            Row("1", "Sprint", null, "S", at: Now.AddDays(-40)),
            Row("1", "Sprint", null, "S", at: Now.AddDays(-41)),
        };
        var recent = await Pred(History(FilterOperator.ChangeCount, new
        {
            historyField = "Sprint", compare = "Eq", count = 1, during = new { kind = "inLast", amount = 30, unit = "days" },
        }), rows);
        Assert.True(recent(I("1")));
        Assert.False(recent(I("2")));   // no rows: 0, not 1
    }

    [Fact]
    public async Task ChangeCount_of_a_field_no_row_uses_is_zero_for_everyone()
    {
        var zero = await Pred(History(FilterOperator.ChangeCount, new { historyField = "Nope", compare = "Eq", count = 0 }),
            new[] { Row("1", "Sprint", null, "S") });
        Assert.True(zero(I("1")));
        Assert.True(zero(I("2")));
    }

    [Fact]
    public async Task Counts_and_windows_are_validated()
    {
        FilterCondition Count(object value) => History(FilterOperator.ChangeCount, value);
        Assert.Contains("'compare' must be one of: Eq, Neq, Gt, Gte, Lt, Lte", await Error(Count(new { historyField = "S", count = 2 })));
        Assert.Contains("'compare' must be one of", await Error(Count(new { historyField = "S", compare = "gte", count = 2 })));
        foreach (var bad in new object[] { -1, 10001, 2.5, "3" })
            Assert.Contains("from 0 to 10000", await Error(Count(new { historyField = "S", compare = "Gte", count = bad })));
        Assert.Contains("from 0 to 10000", await Error(Count(new { historyField = "S", compare = "Gte" })));

        FilterCondition During(object during) => History(FilterOperator.Changed, new { historyField = "S", during });
        Assert.StartsWith("'during' must look like", await Error(During("last week")));
        Assert.Contains("Unknown 'during' kind 'fortnight'", await Error(During(new { kind = "fortnight" })));
        Assert.Contains("'amount' must be a whole number from 1 to 3650", await Error(During(new { kind = "inLast", amount = 0, unit = "days" })));
        Assert.Contains("Unknown unit 'decades'", await Error(During(new { kind = "inLast", amount = 1, unit = "decades" })));
        Assert.Contains("Unknown period 'fortnight'", await Error(During(new { kind = "inPeriod", period = "fortnight" })));
        Assert.Contains("Unknown time zone '../../etc/passwd'", await Error(During(new { kind = "inPeriod", period = "today", timeZone = "../../etc/passwd" })));
        Assert.Contains("at least one bound", await Error(During(new { kind = "between" })));
        Assert.Contains("requires ISO-8601 dates", await Error(During(new { kind = "between", from = "soon" })));
        Assert.Contains("'from' is after 'to'", await Error(During(new { kind = "between", from = "2026-09-30", to = "2026-09-01" })));
    }

    // ---- gaps left open by Task 2's review ----

    [Fact]
    public async Task Was_blocked_or_empty_on_a_current_value_field_is_current_empty_or_a_history_match()
    {
        var blocked = Guid.NewGuid();
        DictNameResolver resolve = (_, names, _) =>
            Task.FromResult<IReadOnlyList<Guid>>(names.Contains("Blocked") ? new[] { blocked } : Array.Empty<Guid>());
        var was = await Pred(History(FilterOperator.Was, new { historyField = "status", values = new string?[] { "Blocked", null } }),
            new[] { Row("3", "status", "To Do", "Blocked") }, resolve);

        var noStatus = I("1");                                                 // no value now: (Empty)
        var blockedNow = I("2"); blockedNow.StatusId = blocked;                // Blocked now
        var blockedBefore = I("3"); blockedBefore.StatusId = Guid.NewGuid();   // elsewhere now, but was Blocked
        var never = I("4"); never.StatusId = Guid.NewGuid();                   // some other status, never Blocked

        Assert.True(was(noStatus));
        Assert.True(was(blockedNow));
        Assert.True(was(blockedBefore));
        Assert.False(was(never));
    }

    [Fact]
    public async Task Changed_to_empty_finds_the_changes_that_cleared_the_field()
    {
        var changed = await Pred(History(FilterOperator.Changed, new { historyField = "Team", to = new string?[] { null } }), new[]
        {
            Row("1", "Team", "Red", null),
            Row("2", "Team", "Red", ""),
            Row("3", "Team", null, "Red"),
        });
        Assert.True(changed(I("1")));
        Assert.True(changed(I("2")));
        Assert.False(changed(I("3")));
    }

    // ---- labels are space-separated; every other field is ", "-separated ----

    [Theory]
    [InlineData("ansible", true)]                    // equal
    [InlineData("DataOps ansible tested", true)]     // middle
    [InlineData("ansible tested", true)]             // first
    [InlineData("DataOps ansible", true)]            // last
    [InlineData("DataOps ansible2 tested", false)]
    [InlineData("DataOps xansible tested", false)]
    [InlineData("DataOps, ansible, tested", false)]  // not how labels are written
    public async Task Labels_are_split_on_spaces(string stored, bool matches)
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "labels", values = new[] { "ansible" } }),
            new[] { Row("1", "labels", null, stored) });
        Assert.Equal(matches, was(I("1")));
    }

    [Fact]
    public async Task A_label_is_never_matched_by_a_prefix_of_it_in_Was_or_Changed()
    {
        var rows = new[] { Row("1", "labels", "DataOps ansible tested", "DataOps ansible tested") };
        var was = await Pred(History(FilterOperator.Was, new { historyField = "labels", values = new[] { "ans" } }), rows);
        Assert.False(was(I("1")));
        var changed = await Pred(History(FilterOperator.Changed, new
        {
            historyField = "labels", from = new[] { "tested" }, to = new[] { "DataOps" },
        }), rows);
        Assert.True(changed(I("1")));
        var prefix = await Pred(History(FilterOperator.Changed, new { historyField = "labels", from = new[] { "test" } }), rows);
        Assert.False(prefix(I("1")));
    }

    [Fact]
    public async Task Other_fields_keep_the_comma_separator_even_when_a_name_has_spaces()
    {
        var rows = new[] { Row("1", "Sprint", null, "PLAT 100, PLAT 101") };
        var element = await Pred(History(FilterOperator.Was, new { historyField = "Sprint", values = new[] { "PLAT 101" } }), rows);
        Assert.True(element(I("1")));
        var word = await Pred(History(FilterOperator.Was, new { historyField = "Sprint", values = new[] { "101" } }), rows);
        Assert.False(word(I("1")));
        // Field names are exact: "Labels" is not the labels field, so it splits on ", " like the rest.
        var other = await Pred(History(FilterOperator.Was, new { historyField = "Labels", values = new[] { "ansible" } }),
            new[] { Row("1", "Labels", null, "DataOps ansible tested") });
        Assert.False(other(I("1")));
    }
}
