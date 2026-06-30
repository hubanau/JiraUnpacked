using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Tenancy;

public sealed class TenantIsolationGateTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();

    public TenantIsolationGateTests(JiraUnpackedApiFactory factory)
    {
        _factory = factory;
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
    }

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

    private const string Password = "Correct-Horse-9!";

    private async Task<HttpClient> NewTenantClientAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = $"tenant-{unique}@example.com",
                Username = $"tenant-{unique}",
                PasswordHash = hasher.Hash(Password),
                Role = UserRole.EndUser,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"tenant-{unique}@example.com", password = Password });
        var tokens = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private async Task<ConnectionDto> CreateConnectionAsync(HttpClient client, string baseUrl)
    {
        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl, email = "ops@acme.com", apiToken = "secret-token" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!;
    }

    [Fact]
    public async Task Tenant_CannotReadAnotherTenantsConnections_ThroughAnyReadPath()
    {
        var a = await NewTenantClientAsync();
        var b = await NewTenantClientAsync();

        var aConn = await CreateConnectionAsync(a, _jira.Url!);
        var bConn = await CreateConnectionAsync(b, _jira.Url!);

        // List (both layers): B sees only B's connection.
        var bList = await (await b.GetAsync("/api/connections"))
            .Content.ReadFromJsonAsync<List<ConnectionDto>>();
        Assert.Single(bList!);
        Assert.Equal(bConn.Id, bList![0].Id);
        Assert.DoesNotContain(bList, c => c.Id == aConn.Id);

        // Get-by-id (both layers): B cannot fetch A's connection => 404 (no existence leak).
        var bGetA = await b.GetAsync($"/api/connections/{aConn.Id}");
        Assert.Equal(HttpStatusCode.NotFound, bGetA.StatusCode);

        // RLS proven independently of EF: connect as the app role, set the GUC to B,
        // and query A's row directly => RLS must hide it even with no EF filter involved.
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
        await conn.OpenAsync();
        var bTenantId = await TenantOf(bConn.Id);
        await SetTenantAsync(conn, bTenantId);

        Assert.Equal(0L, await CountConnectionById(conn, aConn.Id)); // A hidden from B at the DB
        Assert.Equal(1L, await CountConnectionById(conn, bConn.Id)); // B sees its own row (sanity)
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant', @t, false)";
        cmd.Parameters.AddWithValue("t", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    // Reads the owning tenant of a connection via the privileged superuser (bypasses RLS for setup).
    private async Task<Guid> TenantOf(Guid connectionId)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""EndUserId"" FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", connectionId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountConnectionById(NpgsqlConnection conn, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT count(*) FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_projects_via_rls()
    {
        // Two tenants with one project row each (seed via privileged conn; capture ids).
        var (tenantA, projA) = await SeedProjectAsync();
        var (tenantB, projB) = await SeedProjectAsync();

        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
        await conn.OpenAsync();
        await SetTenantAsync(conn, tenantB);

        Assert.Equal(0L, await CountProjectById(conn, projA)); // A hidden from B
        Assert.Equal(1L, await CountProjectById(conn, projB)); // B sees its own
    }

    // Inserts a JiraProject for a fresh tenant via the privileged (RLS-bypassing) connection.
    private async Task<(Guid tenant, Guid project)> SeedProjectAsync()
    {
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using (var c1 = conn.CreateCommand())
        {
            c1.CommandText = @"INSERT INTO ""JiraConnections""
                (""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"")
                VALUES (@id,@t,'https://x','e','c',now())";
            c1.Parameters.AddWithValue("id", connectionId);
            c1.Parameters.AddWithValue("t", tenant);
            await c1.ExecuteNonQueryAsync();
        }
        await using (var c2 = conn.CreateCommand())
        {
            c2.CommandText = @"INSERT INTO ""JiraProjects""
                (""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"")
                VALUES (@id,@t,@conn,'ACME','10001',60,now())";
            c2.Parameters.AddWithValue("id", project);
            c2.Parameters.AddWithValue("t", tenant);
            c2.Parameters.AddWithValue("conn", connectionId);
            await c2.ExecuteNonQueryAsync();
        }
        return (tenant, project);
    }

    private static async Task<long> CountProjectById(NpgsqlConnection conn, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT count(*) FROM ""JiraProjects"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_issues_via_rls()
    {
        var (tenantA, issueA) = await SeedIssueAsync();
        var (tenantB, issueB) = await SeedIssueAsync();

        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
        await conn.OpenAsync();
        await SetTenantAsync(conn, tenantB);

        Assert.Equal(0L, await CountIssueById(conn, issueA)); // A hidden from B
        Assert.Equal(1L, await CountIssueById(conn, issueB)); // B sees its own
    }

    // Inserts a JiraConnection -> JiraProject -> Issue chain for a fresh tenant via the
    // privileged (RLS-bypassing) connection. Returns (tenantId, issueId).
    private async Task<(Guid tenant, Guid issue)> SeedIssueAsync()
    {
        var tenant = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var issueId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        await using (var c1 = conn.CreateCommand())
        {
            c1.CommandText = @"INSERT INTO ""JiraConnections""
                (""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"")
                VALUES (@id,@t,'https://x','e','c',now())";
            c1.Parameters.AddWithValue("id", connectionId);
            c1.Parameters.AddWithValue("t", tenant);
            await c1.ExecuteNonQueryAsync();
        }
        await using (var c2 = conn.CreateCommand())
        {
            c2.CommandText = @"INSERT INTO ""JiraProjects""
                (""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"")
                VALUES (@id,@t,@conn,'ACME','10001',60,now())";
            c2.Parameters.AddWithValue("id", projectId);
            c2.Parameters.AddWithValue("t", tenant);
            c2.Parameters.AddWithValue("conn", connectionId);
            await c2.ExecuteNonQueryAsync();
        }
        await using (var c3 = conn.CreateCommand())
        {
            c3.CommandText = @"INSERT INTO ""Issues""
                (""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",
                 ""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"")
                VALUES (@id,@t,@proj,@jid,'ACME-1',now(),now(),now())";
            c3.Parameters.AddWithValue("id", issueId);
            c3.Parameters.AddWithValue("t", tenant);
            c3.Parameters.AddWithValue("proj", projectId);
            c3.Parameters.AddWithValue("jid", Guid.NewGuid().ToString("N"));
            await c3.ExecuteNonQueryAsync();
        }
        return (tenant, issueId);
    }

    private static async Task<long> CountIssueById(NpgsqlConnection conn, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT count(*) FROM ""Issues"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
