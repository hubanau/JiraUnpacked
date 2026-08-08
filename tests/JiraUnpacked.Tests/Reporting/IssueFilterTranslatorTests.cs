using System.Linq.Expressions;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Tests.Reporting;

public sealed class IssueFilterTranslatorTests
{
    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    // A fake dict resolver: maps names to fixed Guids, ignoring the field (tests use one dict field at a time).
    private static readonly Guid InProgressId = Guid.NewGuid();
    private static readonly Guid DoneId = Guid.NewGuid();
    private static Task<IReadOnlyList<Guid>> Resolver(string field, IReadOnlyList<string> names, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            { ["In Progress"] = InProgressId, ["Done"] = DoneId };
        IReadOnlyList<Guid> ids = names.Where(map.ContainsKey).Select(n => map[n]).ToList();
        return Task.FromResult(ids);
    }

    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator();

    // Recursively walks AndAlso nodes to locate a nested ILike call, without asserting an exact tree depth
    // (the top-level AND chain folds in an `i => true` seed, adding a wrapping AndAlso).
    private static MethodCallExpression? FindIlikeCall(Expression e) => e switch
    {
        MethodCallExpression { Method.Name: "ILike" } call => call,
        BinaryExpression { NodeType: ExpressionType.AndAlso } b =>
            FindIlikeCall(b.Left) ?? FindIlikeCall(b.Right),
        _ => null,
    };

    private static async Task<Func<Issue, bool>> CompileAsync(params FilterCondition[] conditions)
    {
        var r = await Sut.BuildAsync(conditions, Resolver, CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static Issue Issue(Action<Issue> set)
    {
        var i = new Issue { Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "ACME-1",
            JiraCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            JiraUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        set(i); return i;
    }

    [Fact]
    public async Task Dict_eq_matches_by_resolved_id()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.Eq, J("In Progress"), null));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = null)));   // unassigned excluded from Eq
    }

    [Fact]
    public async Task Dict_in_matches_any_resolved_id()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.In, null,
            new List<JsonElement> { J("In Progress"), J("Done") }));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = Guid.NewGuid())));
    }

    [Fact]
    public async Task Dict_neq_includes_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.Neq, J("Done"), null));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));  // not Done -> matches
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));       // Done -> excluded
        Assert.True(pred(Issue(i => i.StatusId = null)));          // unassigned -> INCLUDED (the spec rule)
    }

    [Fact]
    public async Task Dict_isnull_matches_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("assignee", FilterOperator.IsNull, null, null));
        Assert.True(pred(Issue(i => i.AssigneeId = null)));
        Assert.False(pred(Issue(i => i.AssigneeId = Guid.NewGuid())));
    }

    [Fact]
    public async Task Decimal_gte_excludes_null()
    {
        var pred = await CompileAsync(new FilterCondition("storyPoints", FilterOperator.Gte, J(3), null));
        Assert.True(pred(Issue(i => i.StoryPoints = 5m)));
        Assert.False(pred(Issue(i => i.StoryPoints = 1m)));
        Assert.False(pred(Issue(i => i.StoryPoints = null)));  // null excluded from a comparison
    }

    [Fact]
    public async Task Date_lt_compares()
    {
        var pred = await CompileAsync(new FilterCondition("updatedAt", FilterOperator.Lt, J("2024-06-01T00:00:00Z"), null));
        Assert.True(pred(Issue(i => i.JiraUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        Assert.False(pred(Issue(i => i.JiraUpdatedAt = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public async Task Text_contains_builds_ilike_call_with_escaped_pattern()
    {
        // NOTE: EF.Functions.ILike has no in-memory implementation and THROWS if compiled and invoked
        // outside a real query, so this test asserts the built expression's node shape rather than
        // compiling and running the predicate. The DB-backed case-insensitivity + wildcard-escaping
        // behavior is proven in the EF3/EF4 integration tests, where ILike actually executes against
        // Postgres. Every other test in this file compiles and evaluates its predicate in memory.
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("summary", FilterOperator.Contains, J("login"), null) },
            Resolver, CancellationToken.None);
        Assert.Null(r.Error);

        var body = Assert.IsAssignableFrom<BinaryExpression>(r.Predicate!.Body);
        Assert.Equal(ExpressionType.AndAlso, body.NodeType);

        // The single-condition AND chain (folded from the Aggregate seed `i => true`) nests this
        // condition's own `notNull && ILike(...)` AndAlso as the right-hand branch. Walk down through
        // AndAlso nodes to find the ILike call, rather than asserting an exact fixed depth.
        var call = FindIlikeCall(body);
        Assert.NotNull(call);
        Assert.Equal("ILike", call!.Method.Name);
        Assert.Equal(4, call.Arguments.Count);

        var pattern = Assert.IsAssignableFrom<ConstantExpression>(call.Arguments[2]);
        Assert.Equal("%login%", pattern.Value);
    }

    [Fact]
    public async Task Parent_isnull_matches_top_level()
    {
        var pred = await CompileAsync(new FilterCondition("parent", FilterOperator.IsNull, null, null));
        Assert.True(pred(Issue(i => i.ParentJiraIssueId = null)));
        Assert.False(pred(Issue(i => i.ParentJiraIssueId = "10001")));
    }

    [Fact]
    public async Task Conditions_are_ANDed()
    {
        var pred = await CompileAsync(
            new FilterCondition("status", FilterOperator.Eq, J("In Progress"), null),
            new FilterCondition("storyPoints", FilterOperator.Gte, J(3), null));
        Assert.True(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 5m; })));
        Assert.False(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 1m; })));  // fails 2nd
    }

    [Fact]
    public async Task Empty_conditions_is_always_true()
    {
        var pred = await CompileAsync();   // no conditions
        Assert.True(pred(Issue(_ => { })));
    }

    // ---- validation -> Error (controller maps to 400) ----

    [Theory]
    [InlineData("nope", FilterOperator.Eq)]          // unknown field
    public async Task Unknown_field_is_an_error(string field, FilterOperator op)
    {
        var r = await Sut.BuildAsync(new[] { new FilterCondition(field, op, J("x"), null) }, Resolver, default);
        Assert.NotNull(r.Error);
        Assert.Null(r.Predicate);
    }

    [Fact]
    public async Task Operator_not_allowed_for_field_is_an_error()
    {
        // createdAt is DateTime -> Contains is not allowed.
        var r = await Sut.BuildAsync(new[] { new FilterCondition("createdAt", FilterOperator.Contains, J("x"), null) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Value_type_mismatch_is_an_error()
    {
        var r = await Sut.BuildAsync(new[] { new FilterCondition("storyPoints", FilterOperator.Gte, J("abc"), null) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Too_many_conditions_is_an_error()
    {
        var many = Enumerable.Range(0, 21)
            .Select(_ => new FilterCondition("storyPoints", FilterOperator.Gte, J(1), null)).ToList();
        var r = await Sut.BuildAsync(many, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Too_many_in_values_is_an_error()
    {
        var vals = Enumerable.Range(0, 101).Select(n => J($"s{n}")).ToList();
        var r = await Sut.BuildAsync(new[] { new FilterCondition("status", FilterOperator.In, null, vals) }, Resolver, default);
        Assert.NotNull(r.Error);
    }
}
