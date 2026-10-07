using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Infrastructure.Tenancy;
using JiraUnpacked.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// The SQL the history operators become, without a database: EF + Npgsql must turn the
/// expression trees into correlated subqueries over the tenant-filtered IssueChangeHistory —
/// never a client-side evaluation, never a user string in the statement.
/// </summary>
public sealed class HistorySqlShapeTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    internal static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> NoDictionary(string field, IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    /// <summary>The issue query's SQL for one history condition, composed as IssueQueryService composes it.</summary>
    internal static async Task<string> SqlAsync(FilterCondition c, DateTime? now = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=unused").Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        var built = await new IssueFilterTranslator(new FakeClock(now ?? Now))
            .BuildAsync(new[] { c }, NoDictionary, CancellationToken.None, db.IssueChangeHistory);
        Assert.Null(built.Error);
        return db.Issues.Where(i => i.JiraProjectId == Guid.Empty).Where(built.Predicate!).ToQueryString();
    }

    /// <summary>The statement without EF's parameter header (the `-- @p='…'` lines).</summary>
    internal static string Statement(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public async Task Changed_is_a_correlated_exists_over_the_tenant_filtered_history()
    {
        var sql = Statement(await SqlAsync(new FilterCondition(
            "history", FilterOperator.Changed, J(new { historyField = "Sprint" }), null)));

        Assert.Contains("EXISTS (", sql);
        Assert.Contains("FROM \"IssueChangeHistory\"", sql);
        // Correlated on the issue: same project, same Jira issue id.
        Assert.Matches(@"\w+\.""JiraProjectId"" = \w+\.""JiraProjectId""", sql);
        Assert.Matches(@"\w+\.""JiraIssueId"" = \w+\.""JiraIssueId""", sql);
        // The tenant query filter applies INSIDE the subquery, not only to Issues.
        Assert.Matches(@"EXISTS \([\s\S]*""EndUserId"" = @", sql);
        // The field name is a parameter, never SQL text. (Not a bare "Sprint": the projection
        // reads Issues."SprintId", so look for the literal and for the parameter instead.)
        Assert.DoesNotContain("'Sprint'", sql);
        Assert.Matches(@"""Field"" = @\w+", sql);
    }

    [Fact]
    public async Task Values_travel_as_parameters_so_the_statement_is_the_same_for_any_value()
    {
        static FilterCondition Was(string v) => new("history", FilterOperator.Was,
            J(new { historyField = "Labels", values = new[] { v } }), null);
        var first = Statement(await SqlAsync(Was("PLAT 101")));
        var second = Statement(await SqlAsync(Was("100%_done")));

        Assert.DoesNotContain("PLAT", first);
        Assert.DoesNotContain("100%", second);
        Assert.Equal(first, second);   // one shape, one compiled-query cache entry
    }
}
