// tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Seeding and query helpers for the richer-filter-value tests. Seeding uses the superuser
/// connection (bypasses RLS) purely to build fixtures; every assertion reads through the API as
/// the owning tenant, and every call works in a fresh project so parallel classes never collide.
/// </summary>
internal static class RicherFilterTestData
{
    /// <summary>
    /// The API with the FILTER TRANSLATOR's clock pinned — only the translator's: the token
    /// service keeps the real clock, or every token this host issued would already have expired.
    /// Registered after the app's own registration, so it is the one resolved. The caller must
    /// have migrated the database first (the app seeds an admin while the host is built).
    /// </summary>
    internal static WebApplicationFactory<Program> WithFixedClock(JiraUnpackedApiFactory factory, DateTime nowUtc) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddScoped<IIssueFilterTranslator>(_ => new IssueFilterTranslator(new FakeClock(nowUtc)))));

    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    /// <summary>One issue; created = updated = <paramref name="updatedAt"/> (default Jan 1 2026).</summary>
    internal static async Task SeedIssueAsync(
        SeedContext ctx, string key, DateTime? updatedAt = null, Guid? statusId = null, Guid? sprintId = null)
    {
        var at = updatedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""SprintId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"")
              VALUES (@i,@t,@p,@jid,@k,@k,@st,@sp,@at,@at,now())",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", key), ("k", key),
            ("st", statusId), ("sp", sprintId), ("at", at));
    }

    private static Task<HttpResponseMessage> QueryAsync(HttpClient client, Guid projectId, object[] conditions) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/issues/query",
            new { filter = new { logic = "And", conditions } });

    /// <summary>The matching keys, sorted ordinally, from one And group of the given conditions.</summary>
    internal static async Task<string[]> KeysAsync(HttpClient client, Guid projectId, params object[] conditions)
    {
        var resp = await QueryAsync(client, projectId, conditions);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>();
        return page!.Items.Select(i => i.JiraKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Asserts a 400 and returns its ProblemDetails title.</summary>
    internal static async Task<string> RejectionAsync(HttpClient client, Guid projectId, params object[] conditions)
    {
        var resp = await QueryAsync(client, projectId, conditions);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
    }
}
