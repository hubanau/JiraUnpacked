using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Admin;

public sealed class AdminUserListTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public AdminUserListTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    private const string Password = "Correct-Horse-9!";

    private async Task<(Guid id, string email)> SeedUserAsync(UserRole role)
    {
        var unique = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"user-{unique}@example.com",
            Username = $"user-{unique}",
            PasswordHash = hasher.Hash(Password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, user.Email);
    }

    private async Task<TokenResponseDto> LoginAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password = Password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<TokenResponseDto>())!;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, email) = await SeedUserAsync(UserRole.Admin);
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact] // The Admin policy is the ONLY thing between an End User and every account.
    public async Task End_user_cannot_list_users()
    {
        var (_, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, endUserEmail);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Response_never_contains_a_password_hash()
    {
        await SeedUserAsync(UserRole.EndUser);
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        // Asserted on the RAW JSON, not a typed DTO: a typed assertion cannot
        // catch an extra serialised property.
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$2a$", raw); // a bcrypt hash prefix, in case the name changes
    }

    [Fact]
    public async Task Admin_sees_users_paged()
    {
        await SeedUserAsync(UserRole.EndUser);
        var admin = await AdminClientAsync(); // seeds a second user (the admin itself)

        var response = await admin.GetAsync("/api/admin/users?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminUserListItem>>();
        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.True(result.TotalCount >= 2);
        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.PageSize);
    }
}
