using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// statusCategory filters through the status: category names → the statuses in them →
/// StatusId in set. (Empty) is no status, or a status with no category.
/// </summary>
public sealed class StatusCategoryFilterTests
{
    private static readonly Guid ToDoId = Guid.NewGuid();    // a status in "To Do"
    private static readonly Guid DoingId = Guid.NewGuid();   // a status in "In Progress"
    private static readonly Guid DoneId = Guid.NewGuid();    // a status in "Done"
    private static readonly Guid LimboId = Guid.NewGuid();   // a status with no category

    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(DateTime.UtcNow));

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> Resolver(string lookup, IReadOnlyList<string> names, CancellationToken ct)
    {
        var byCategory = new Dictionary<string, Guid> { ["To Do"] = ToDoId, ["In Progress"] = DoingId, ["Done"] = DoneId };
        IReadOnlyList<Guid> ids = lookup switch
        {
            FilterLookups.StatusCategory => names.Where(byCategory.ContainsKey).Select(n => byCategory[n]).ToList(),
            FilterLookups.UncategorisedStatus => new[] { LimboId },
            _ => Array.Empty<Guid>(),
        };
        return Task.FromResult(ids);
    }

    private static async Task<Func<Issue, bool>> Pred(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, Resolver, CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static Issue With(Guid? statusId) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", StatusId = statusId,
        JiraCreatedAt = DateTime.UtcNow, JiraUpdatedAt = DateTime.UtcNow,
    };

    private static FilterCondition Is(FilterOperator op, string name) => new("statusCategory", op, J(name), null);
    private static FilterCondition Any(FilterOperator op, params string?[] names) =>
        new("statusCategory", op, null, names.Select(n => J(n)).ToList());

    [Fact]
    public async Task Eq_matches_the_statuses_in_that_category_only()
    {
        var pred = await Pred(Is(FilterOperator.Eq, "Done"));

        Assert.True(pred(With(DoneId)));
        Assert.False(pred(With(ToDoId)));
        Assert.False(pred(With(LimboId)));
        Assert.False(pred(With(null)));
    }

    [Fact]
    public async Task Neq_and_NotIn_never_match_empty()
    {
        var neq = await Pred(Is(FilterOperator.Neq, "Done"));
        Assert.True(neq(With(ToDoId)));
        Assert.True(neq(With(DoingId)));
        Assert.False(neq(With(DoneId)));
        Assert.False(neq(With(LimboId)));   // a status with no category is (Empty), not "not Done"
        Assert.False(neq(With(null)));

        var notIn = await Pred(Any(FilterOperator.NotIn, "Done"));
        Assert.True(notIn(With(ToDoId)));
        Assert.False(notIn(With(DoneId)));
        Assert.False(notIn(With(LimboId)));
        Assert.False(notIn(With(null)));
    }

    [Fact]
    public async Task In_matches_empty_only_when_asked_for()
    {
        var plain = await Pred(Any(FilterOperator.In, "To Do", "Done"));
        Assert.True(plain(With(ToDoId)));
        Assert.True(plain(With(DoneId)));
        Assert.False(plain(With(DoingId)));
        Assert.False(plain(With(LimboId)));
        Assert.False(plain(With(null)));

        var withEmpty = await Pred(Any(FilterOperator.In, "Done", null));
        Assert.True(withEmpty(With(DoneId)));
        Assert.True(withEmpty(With(LimboId)));
        Assert.True(withEmpty(With(null)));
        Assert.False(withEmpty(With(ToDoId)));
    }

    [Fact]
    public async Task IsNull_is_no_status_or_no_category_and_IsNotNull_is_the_rest()
    {
        var isNull = await Pred(new FilterCondition("statusCategory", FilterOperator.IsNull, null, null));
        Assert.True(isNull(With(null)));
        Assert.True(isNull(With(LimboId)));
        Assert.False(isNull(With(DoneId)));

        var isNotNull = await Pred(new FilterCondition("statusCategory", FilterOperator.IsNotNull, null, null));
        Assert.True(isNotNull(With(DoneId)));
        Assert.False(isNotNull(With(LimboId)));
        Assert.False(isNotNull(With(null)));
    }

    [Fact]
    public async Task Smart_values_and_other_operators_are_refused()
    {
        var smart = await Sut.BuildAsync(new[] { new FilterCondition("statusCategory", FilterOperator.In, null,
            new List<JsonElement> { J(new { smart = "active" }) }) }, Resolver, CancellationToken.None);
        Assert.Contains("only available on 'sprint'", smart.Error);

        var contains = await Sut.BuildAsync(new[] { new FilterCondition("statusCategory", FilterOperator.Contains, J("Do"), null) },
            Resolver, CancellationToken.None);
        Assert.Equal("Operator 'Contains' is not allowed for field 'statusCategory'.", contains.Error);
    }
}
