using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// Boolean grouping over the FR-E4 translator. Pure: the dict resolver is a fake and no
/// database is involved, so every case here is a statement about the Expression tree alone.
/// </summary>
public sealed class IssueFilterGroupTests
{
    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    private static readonly Guid InProgressId = Guid.NewGuid();
    private static readonly Guid DoneId = Guid.NewGuid();

    private static Task<IReadOnlyList<Guid>> Resolver(string field, IReadOnlyList<string> names, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            { ["In Progress"] = InProgressId, ["Done"] = DoneId };
        IReadOnlyList<Guid> ids = names.Where(map.ContainsKey).Select(n => map[n]).ToList();
        return Task.FromResult(ids);
    }

    // None of these conditions is relative, so the time is irrelevant — but there is always a clock.
    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(DateTime.UtcNow));

    private static FilterCondition Status(string name) =>
        new("status", FilterOperator.Eq, J(name), null);

    private static FilterCondition Points(FilterOperator op, decimal v) =>
        new("storyPoints", op, J(v), null);

    private static FilterGroup Group(FilterLogic logic, params FilterCondition[] conditions) =>
        new(logic, conditions.ToList(), null);

    private static FilterGroup Nest(FilterLogic logic, params FilterGroup[] groups) =>
        new(logic, null, groups.ToList());

    private static Issue Issue(Action<Issue> set)
    {
        var i = new Issue
        {
            Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "ACME-1",
            JiraCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            JiraUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        set(i);
        return i;
    }

    private static async Task<Func<Issue, bool>> CompileAsync(FilterGroup group)
    {
        var r = await Sut.BuildAsync(group, Resolver, CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static async Task<string> ErrorAsync(FilterGroup group)
    {
        var r = await Sut.BuildAsync(group, Resolver, CancellationToken.None);
        Assert.Null(r.Predicate);
        Assert.NotNull(r.Error);
        return r.Error!;
    }

    [Fact]
    public async Task Or_matches_either_arm_and_still_excludes_what_matches_neither()
    {
        var pred = await CompileAsync(new FilterGroup(
            FilterLogic.Or,
            new List<FilterCondition> { Status("In Progress"), Points(FilterOperator.Gt, 3m) },
            null));

        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));                           // left arm only
        Assert.True(pred(Issue(i => { i.StatusId = DoneId; i.StoryPoints = 5m; })));        // right arm only
        Assert.True(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 5m; })));  // both

        // The load-bearing case. Combining with a seeded Aggregate would fold in the old
        // `i => true` seed, making this `true || p` — every Or group would match every issue.
        Assert.False(pred(Issue(i => { i.StatusId = DoneId; i.StoryPoints = 1m; })));
    }

    [Fact]
    public async Task Single_condition_or_group_still_excludes_non_matches()
    {
        var pred = await CompileAsync(Group(FilterLogic.Or, Status("In Progress")));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
    }

    [Fact]
    public async Task And_of_two_or_groups_intersects_them()
    {
        // (status = In Progress OR Done) AND (points > 3 OR points is null)
        var pred = await CompileAsync(Nest(
            FilterLogic.And,
            Group(FilterLogic.Or, Status("In Progress"), Status("Done")),
            new FilterGroup(FilterLogic.Or,
                new List<FilterCondition>
                {
                    Points(FilterOperator.Gt, 3m),
                    new("storyPoints", FilterOperator.IsNull, null, null),
                },
                null)));

        Assert.True(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 5m; })));
        Assert.True(pred(Issue(i => { i.StatusId = DoneId; i.StoryPoints = null; })));
        Assert.False(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 1m; })));   // right arm fails
        Assert.False(pred(Issue(i => { i.StatusId = Guid.NewGuid(); i.StoryPoints = 5m; }))); // left arm fails
    }

    [Fact]
    public async Task Or_of_and_groups_unions_them()
    {
        var pred = await CompileAsync(Nest(
            FilterLogic.Or,
            Group(FilterLogic.And, Status("In Progress"), Points(FilterOperator.Gt, 3m)),
            Group(FilterLogic.And, Status("Done"), Points(FilterOperator.Lt, 2m))));

        Assert.True(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 5m; })));
        Assert.True(pred(Issue(i => { i.StatusId = DoneId; i.StoryPoints = 1m; })));
        Assert.False(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 1m; })));  // crosses the arms
        Assert.False(pred(Issue(i => { i.StatusId = DoneId; i.StoryPoints = 5m; })));
    }

    [Fact]
    public async Task Group_mixing_conditions_and_nested_groups_joins_all_parts_with_its_own_logic()
    {
        // Or { points > 100, And { status = Done, points < 2 } } — the leaf and the subgroup are
        // both parts of the same Or, so either alone suffices.
        var pred = await CompileAsync(new FilterGroup(
            FilterLogic.Or,
            new List<FilterCondition> { Points(FilterOperator.Gt, 100m) },
            new List<FilterGroup> { Group(FilterLogic.And, Status("Done"), Points(FilterOperator.Lt, 2m)) }));

        Assert.True(pred(Issue(i => i.StoryPoints = 200m)));                            // leaf alone
        Assert.True(pred(Issue(i => { i.StatusId = DoneId; i.StoryPoints = 1m; })));    // subgroup alone
        Assert.False(pred(Issue(i => { i.StatusId = DoneId; i.StoryPoints = 50m; })));  // neither
    }

    [Fact]
    public async Task Empty_root_group_is_rejected_under_both_logics()
    {
        // An empty And is vacuously true and an empty Or vacuously false — opposite wrong
        // answers from the same client bug, so neither may be allowed through.
        Assert.Equal("A filter group must contain at least one condition or nested group.",
            await ErrorAsync(new FilterGroup(FilterLogic.And, null, null)));

        Assert.Equal("A filter group must contain at least one condition or nested group.",
            await ErrorAsync(new FilterGroup(FilterLogic.Or, new List<FilterCondition>(), new List<FilterGroup>())));
    }

    [Fact]
    public async Task Empty_nested_group_is_rejected_even_when_the_root_is_populated()
    {
        var error = await ErrorAsync(new FilterGroup(
            FilterLogic.And,
            new List<FilterCondition> { Status("Done") },
            new List<FilterGroup> { new(FilterLogic.Or, null, null) }));

        Assert.Equal("A filter group must contain at least one condition or nested group.", error);
    }

    // A chain of `depth` nested groups with a single condition at the bottom.
    private static FilterGroup Chain(int depth)
    {
        var g = Group(FilterLogic.And, Status("Done"));
        for (var i = 1; i < depth; i++) g = Nest(FilterLogic.And, g);
        return g;
    }

    [Fact]
    public async Task Depth_is_allowed_at_the_limit_and_refused_one_past_it()
    {
        Assert.Equal(3, IssueFilterTranslator.MaxDepth);

        var atLimit = await Sut.BuildAsync(Chain(IssueFilterTranslator.MaxDepth), Resolver, CancellationToken.None);
        Assert.Null(atLimit.Error);

        Assert.Equal($"A filter may nest at most {IssueFilterTranslator.MaxDepth} levels deep.",
            await ErrorAsync(Chain(IssueFilterTranslator.MaxDepth + 1)));
    }

    // A root holding `siblings` sibling subgroups — root included that is siblings + 1 groups,
    // and only two levels deep, so this isolates the group-count guard from the depth guard.
    private static FilterGroup Siblings(int siblings) =>
        new(FilterLogic.Or, null,
            Enumerable.Range(0, siblings).Select(_ => Group(FilterLogic.And, Status("Done"))).ToList());

    [Fact]
    public async Task Group_count_is_allowed_at_the_limit_and_refused_one_past_it()
    {
        Assert.Equal(10, IssueFilterTranslator.MaxGroups);

        var atLimit = await Sut.BuildAsync(
            Siblings(IssueFilterTranslator.MaxGroups - 1), Resolver, CancellationToken.None);
        Assert.Null(atLimit.Error);

        Assert.Equal($"A filter may contain at most {IssueFilterTranslator.MaxGroups} groups.",
            await ErrorAsync(Siblings(IssueFilterTranslator.MaxGroups)));
    }

    // Spreads `total` conditions across two sibling subgroups, so the cap is only reached when
    // tallied across the WHOLE tree — a per-group limit would let this through.
    private static FilterGroup Spread(int total)
    {
        var left = Enumerable.Range(0, total / 2).Select(_ => Status("Done")).ToArray();
        var right = Enumerable.Range(0, total - total / 2).Select(_ => Status("Done")).ToArray();
        return Nest(FilterLogic.Or, Group(FilterLogic.And, left), Group(FilterLogic.And, right));
    }

    [Fact]
    public async Task Condition_count_is_tallied_across_the_whole_tree_not_per_group()
    {
        Assert.Equal(20, IssueFilterTranslator.MaxConditions);

        var atLimit = await Sut.BuildAsync(
            Spread(IssueFilterTranslator.MaxConditions), Resolver, CancellationToken.None);
        Assert.Null(atLimit.Error);

        // 21 split as 10 + 11: neither subgroup alone exceeds the limit.
        Assert.Equal($"A filter may have at most {IssueFilterTranslator.MaxConditions} conditions.",
            await ErrorAsync(Spread(IssueFilterTranslator.MaxConditions + 1)));
    }

    [Fact]
    public async Task Unknown_field_inside_a_nested_group_is_still_rejected()
    {
        var error = await ErrorAsync(Nest(
            FilterLogic.Or,
            Group(FilterLogic.And, Status("Done")),
            Group(FilterLogic.And, new FilterCondition("secret", FilterOperator.Eq, J("x"), null))));

        Assert.Equal("Unknown field 'secret'.", error);
    }

    [Fact]
    public async Task Legacy_flat_conditions_path_is_unchanged()
    {
        var empty = await Sut.BuildAsync(Array.Empty<FilterCondition>(), Resolver, CancellationToken.None);
        Assert.Null(empty.Error);
        Assert.True(empty.Predicate!.Compile()(Issue(_ => { })));   // empty = unfiltered, not an empty group

        var flat = await Sut.BuildAsync(
            new[] { Status("In Progress"), Points(FilterOperator.Gt, 3m) }, Resolver, CancellationToken.None);
        Assert.Null(flat.Error);
        var pred = flat.Predicate!.Compile();
        Assert.True(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 5m; })));
        Assert.False(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 1m; })));  // still AND
    }

    private static FilterCondition Off(FilterCondition c) => c with { Disabled = true };
    private static FilterGroup Off(FilterGroup g) => g with { Disabled = true };

    [Fact]
    public async Task A_disabled_condition_is_skipped_without_being_validated()
    {
        // Unknown field AND a value of the wrong type: either is a 400 if the row is looked at.
        // A row switched off because it is broken must not break the filter.
        var pred = await CompileAsync(new FilterGroup(FilterLogic.And, new List<FilterCondition>
        {
            Status("Done"),
            Off(new FilterCondition("secret", FilterOperator.Eq, J(42), null)),
        }, null));

        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = InProgressId)));
    }

    [Fact]
    public async Task Disabled_conditions_do_not_count_toward_the_limit()
    {
        var conditions = Enumerable.Range(0, IssueFilterTranslator.MaxConditions).Select(_ => Status("Done")).ToList();
        conditions.Add(Off(Status("Done")));   // the 21st, switched off

        var r = await Sut.BuildAsync(new FilterGroup(FilterLogic.And, conditions, null), Resolver, CancellationToken.None);

        Assert.Null(r.Error);
    }

    [Fact]
    public async Task A_disabled_group_is_skipped_whole_even_past_the_depth_limit()
    {
        var pred = await CompileAsync(new FilterGroup(FilterLogic.And,
            new List<FilterCondition> { Status("Done") },
            new List<FilterGroup> { Off(Chain(IssueFilterTranslator.MaxDepth + 1)) }));

        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
    }

    [Fact]
    public async Task Disabled_groups_do_not_count_toward_the_group_limit()
    {
        var root = Siblings(IssueFilterTranslator.MaxGroups - 1);   // root + 9 = at the limit
        root.Groups!.Add(Off(Group(FilterLogic.And, Status("Done"))));

        var r = await Sut.BuildAsync(root, Resolver, CancellationToken.None);

        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData(FilterLogic.And)]
    [InlineData(FilterLogic.Or)]
    public async Task A_group_whose_parts_are_all_disabled_is_treated_as_absent(FilterLogic inner)
    {
        // Kept as an empty group it would be vacuously true under And (an outer Or would then
        // match everything) or vacuously false under Or (an outer And would match nothing).
        // Absent, the outer group means exactly its other parts.
        var allOff = new FilterGroup(inner, new List<FilterCondition> { Off(Status("In Progress")) }, null);

        var underOr = await CompileAsync(new FilterGroup(FilterLogic.Or,
            new List<FilterCondition> { Status("Done") }, new List<FilterGroup> { allOff }));
        Assert.True(underOr(Issue(i => i.StatusId = DoneId)));
        Assert.False(underOr(Issue(i => i.StatusId = InProgressId)));

        var underAnd = await CompileAsync(new FilterGroup(FilterLogic.And,
            new List<FilterCondition> { Status("Done") }, new List<FilterGroup> { allOff }));
        Assert.True(underAnd(Issue(i => i.StatusId = DoneId)));
        Assert.False(underAnd(Issue(i => i.StatusId = InProgressId)));
    }

    [Theory]
    [InlineData(FilterLogic.And)]
    [InlineData(FilterLogic.Or)]
    public async Task A_root_with_nothing_enabled_matches_every_issue(FilterLogic logic)
    {
        var pred = await CompileAsync(new FilterGroup(logic,
            new List<FilterCondition> { Off(Status("Done")) },
            new List<FilterGroup> { Off(Group(FilterLogic.And, Status("Done"))) }));

        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.True(pred(Issue(i => i.StatusId = null)));
    }

    [Fact]
    public async Task A_disabled_root_matches_every_issue()
    {
        var pred = await CompileAsync(Off(Group(FilterLogic.And, Status("Done"))));

        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
    }

    [Fact]
    public async Task A_nested_group_written_with_no_parts_is_still_rejected()
    {
        // Switched-off parts are a choice; a group with nothing in it at all is a client bug.
        var error = await ErrorAsync(new FilterGroup(FilterLogic.And,
            new List<FilterCondition> { Off(Status("Done")) },
            new List<FilterGroup> { new(FilterLogic.Or, null, null) }));

        Assert.Equal("A filter group must contain at least one condition or nested group.", error);
    }

    [Fact]
    public async Task The_flat_conditions_path_skips_disabled_rows_too()
    {
        var r = await Sut.BuildAsync(new[] { Off(Status("Done")) }, Resolver, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.True(r.Predicate!.Compile()(Issue(i => i.StatusId = InProgressId)));
    }
}
