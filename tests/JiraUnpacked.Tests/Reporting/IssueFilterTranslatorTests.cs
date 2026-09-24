using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

public sealed class IssueFilterTranslatorTests
{
    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    private static readonly JsonElement JNull = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>A `values` list; a C# null becomes a JSON null, which is how (Empty) travels.</summary>
    private static List<JsonElement> Vals(params object?[] vs) =>
        vs.Select(v => v is null ? JNull : J(v)).ToList();

    /// <summary>Finds `NOT ILike(...)` anywhere under AndAlso/OrElse nodes.</summary>
    private static MethodCallExpression? FindNegatedIlike(Expression e) => e switch
    {
        UnaryExpression { NodeType: ExpressionType.Not, Operand: MethodCallExpression { Method.Name: "ILike" } call } => call,
        BinaryExpression b => FindNegatedIlike(b.Left) ?? FindNegatedIlike(b.Right),
        _ => null,
    };

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

    // None of these conditions is relative, so the time is irrelevant — but there is always a clock.
    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(DateTime.UtcNow));

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
    public async Task Dict_neq_excludes_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.Neq, J("Done"), null));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        // Empty matches only when asked for: "is not Done" says nothing about issues with no status.
        Assert.False(pred(Issue(i => i.StatusId = null)));
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

    // ---- folded-in fix (FR-E4 EF4): escaping must be backslash-first, or a literal backslash in the
    // ---- value would itself become a live escape char and corrupt the following escaped metacharacter.

    [Fact]
    public async Task Text_contains_escapes_backslash_percent_and_underscore_backslash_first()
    {
        // NOTE: like Text_contains_builds_ilike_call_with_escaped_pattern above, ILike has no in-memory
        // implementation, so this asserts the built ILike node's pattern/escape-char constants directly.
        //
        // Hand-verified escape order (translator: Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")):
        //   raw:                              a % b _ c \ d
        //   step 1 (\ -> \\, backslash FIRST): a % b _ c \\ d   =>  "a%b_c\\d"
        //   step 2 (% -> \%):                  a \% b _ c \\ d  =>  "a\%b_c\\d"
        //   step 3 (_ -> \_):                  a \% b \_ c \\ d =>  "a\%b\_c\\d"
        //   wrapped "%" + escaped + "%":       "%a\%b\_c\\d%"
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("summary", FilterOperator.Contains, J(@"a%b_c\d"), null) },
            Resolver, CancellationToken.None);
        Assert.Null(r.Error);

        var body = Assert.IsAssignableFrom<BinaryExpression>(r.Predicate!.Body);
        Assert.Equal(ExpressionType.AndAlso, body.NodeType);

        var call = FindIlikeCall(body);
        Assert.NotNull(call);
        Assert.Equal("ILike", call!.Method.Name);
        Assert.Equal(4, call.Arguments.Count);

        var pattern = Assert.IsAssignableFrom<ConstantExpression>(call.Arguments[2]);
        Assert.Equal(@"%a\%b\_c\\d%", pattern.Value);

        var escapeChar = Assert.IsAssignableFrom<ConstantExpression>(call.Arguments[3]);
        Assert.Equal("\\", escapeChar.Value);
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

    [Fact]
    public void A_json_null_inside_values_arrives_as_a_Null_element()
    {
        // The wire representation of (Empty) rests on this; pin it rather than assume it.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var c = JsonSerializer.Deserialize<FilterCondition>(
            """{"field":"sprint","op":"In","values":["S1",null]}""", options)!;

        Assert.Equal(2, c.Values!.Count);
        Assert.Equal(JsonValueKind.String, c.Values[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, c.Values[1].ValueKind);
    }

    [Fact]
    public async Task Dict_notin_excludes_the_listed_values_and_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.NotIn, null, Vals("Done")));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = null)));
    }

    [Fact]
    public async Task Dict_in_with_a_null_value_also_matches_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.In, null, Vals("Done", null)));
        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
        Assert.True(pred(Issue(i => i.StatusId = null)));
        Assert.False(pred(Issue(i => i.StatusId = InProgressId)));
    }

    [Fact]
    public async Task Dict_in_with_only_null_is_empty()
    {
        var pred = await CompileAsync(new FilterCondition("sprint", FilterOperator.In, null, Vals((object?)null)));
        Assert.True(pred(Issue(i => i.SprintId = null)));
        Assert.False(pred(Issue(i => i.SprintId = Guid.NewGuid())));
    }

    [Fact]
    public async Task Dict_notin_accepts_a_null_value_as_redundant()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.NotIn, null, Vals("Done", null)));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = null)));
    }

    [Fact]
    public async Task Dict_in_rejects_a_value_that_is_neither_string_nor_null()
    {
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("status", FilterOperator.In, null, Vals("Done", 5)) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Dict_isnotnull_matches_only_assigned()
    {
        var pred = await CompileAsync(new FilterCondition("assignee", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.AssigneeId = Guid.NewGuid())));
        Assert.False(pred(Issue(i => i.AssigneeId = null)));
    }

    [Fact]
    public async Task Decimal_neq_excludes_null()
    {
        // C# null semantics (which EF reproduces in SQL) make a bare `StoryPoints != 5` TRUE for NULL.
        var pred = await CompileAsync(new FilterCondition("storyPoints", FilterOperator.Neq, J(5), null));
        Assert.True(pred(Issue(i => i.StoryPoints = 3m)));
        Assert.False(pred(Issue(i => i.StoryPoints = 5m)));
        Assert.False(pred(Issue(i => i.StoryPoints = null)));
    }

    [Fact]
    public async Task Decimal_isnotnull_matches_only_estimated()
    {
        var pred = await CompileAsync(new FilterCondition("storyPoints", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.StoryPoints = 0m)));
        Assert.False(pred(Issue(i => i.StoryPoints = null)));
    }

    [Fact]
    public async Task Summary_isnotnull_matches_only_issues_with_a_summary()
    {
        var pred = await CompileAsync(new FilterCondition("summary", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.Summary = "x")));
        Assert.False(pred(Issue(i => i.Summary = null)));
    }

    [Fact]
    public async Task Parent_isnotnull_matches_only_children()
    {
        var pred = await CompileAsync(new FilterCondition("parent", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.ParentJiraIssueId = "10001")));
        Assert.False(pred(Issue(i => i.ParentJiraIssueId = null)));
    }

    [Fact]
    public async Task JiraKey_in_and_notin_match_exact_keys()
    {
        var inList = await CompileAsync(new FilterCondition("jiraKey", FilterOperator.In, null, Vals("ACME-1", "ACME-3")));
        Assert.True(inList(Issue(i => i.JiraKey = "ACME-1")));
        Assert.False(inList(Issue(i => i.JiraKey = "ACME-2")));

        var notIn = await CompileAsync(new FilterCondition("jiraKey", FilterOperator.NotIn, null, Vals("ACME-1", "ACME-3")));
        Assert.True(notIn(Issue(i => i.JiraKey = "ACME-2")));
        Assert.False(notIn(Issue(i => i.JiraKey = "ACME-3")));
    }

    [Fact]
    public async Task JiraKey_in_rejects_null_because_a_key_is_never_empty()
    {
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("jiraKey", FilterOperator.In, null, Vals("ACME-1", null)) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Summary_notcontains_guards_null_and_negates_an_escaped_ilike()
    {
        // ILike has no in-memory implementation (see Text_contains_builds_ilike_call_with_escaped_pattern),
        // so this asserts the tree: `Summary != null && !ILike(Summary, "%a\%b%", "\")`.
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("summary", FilterOperator.NotContains, J("a%b"), null) }, Resolver, default);
        Assert.Null(r.Error);

        var body = Assert.IsAssignableFrom<BinaryExpression>(r.Predicate!.Body);
        Assert.Equal(ExpressionType.AndAlso, body.NodeType);
        var guard = Assert.IsAssignableFrom<BinaryExpression>(body.Left);
        Assert.Equal(ExpressionType.NotEqual, guard.NodeType);
        Assert.Null(Assert.IsAssignableFrom<ConstantExpression>(guard.Right).Value);

        var call = FindNegatedIlike(body);
        Assert.NotNull(call);
        Assert.Equal(@"%a\%b%", Assert.IsAssignableFrom<ConstantExpression>(call!.Arguments[2]).Value);
    }

    [Fact]
    public async Task NotIn_with_more_than_the_limit_is_an_error()
    {
        var vals = Enumerable.Range(0, 101).Select(n => J($"s{n}")).ToList();
        var r = await Sut.BuildAsync(new[] { new FilterCondition("status", FilterOperator.NotIn, null, vals) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Eq_with_a_json_null_value_is_still_an_error()
    {
        // The editor turns "is (Empty)" into IsNull before sending; a null value on the wire is a client bug.
        var r = await Sut.BuildAsync(new[] { new FilterCondition("status", FilterOperator.Eq, JNull, null) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Theory]
    [InlineData("createdAt", FilterOperator.IsNotNull)]
    [InlineData("jiraKey", FilterOperator.IsNull)]
    [InlineData("jiraKey", FilterOperator.IsNotNull)]
    [InlineData("jiraKey", FilterOperator.NotContains)]
    [InlineData("parent", FilterOperator.Neq)]
    [InlineData("parent", FilterOperator.In)]
    [InlineData("storyPoints", FilterOperator.NotIn)]
    [InlineData("summary", FilterOperator.NotIn)]
    [InlineData("status", FilterOperator.NotContains)]
    public async Task Operators_outside_a_fields_set_are_rejected(string field, FilterOperator op)
    {
        var r = await Sut.BuildAsync(new[] { new FilterCondition(field, op, J("x"), Vals("x")) }, Resolver, default);
        Assert.Equal($"Operator '{op}' is not allowed for field '{field}'.", r.Error);
    }
}
