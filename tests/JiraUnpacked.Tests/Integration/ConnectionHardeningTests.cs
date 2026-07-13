using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using JiraUnpacked.Domain.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class ConnectionHardeningTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ConnectionHardeningTests(JiraUnpackedApiFactory factory) => _factory = factory;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

    private async Task<HttpClient> AuthedClientAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User { Id = Guid.NewGuid(), Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return c;
    }

    [Fact]
    public async Task Create_with_valid_creds_stores_an_encrypted_token()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        var client = await AuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "secret-token" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var view = (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!;

        // Token at rest is ciphertext, never the plaintext, and round-trips via the protector.
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();
        await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""ApiTokenEncrypted"" FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", view.Id);
        var stored = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.NotEqual("secret-token", stored);
        Assert.Equal("secret-token", protector.Unprotect(stored));
    }

    [Fact]
    public async Task Create_with_empty_base_url_is_rejected_with_400()
    {
        // An empty/malformed baseUrl must be rejected by request validation (400),
        // not blow up building a Uri downstream (which surfaces as an unhandled 500).
        var client = await AuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = "", email = "ops@acme.com", apiToken = "secret-token" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_with_bad_creds_is_rejected_and_stores_nothing()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(401));
        var client = await AuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "bad" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var list = await (await client.GetAsync("/api/connections")).Content.ReadFromJsonAsync<List<ConnectionDto>>();
        Assert.Empty(list!);
    }
}
