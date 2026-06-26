using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Admin;

public sealed class InviteCodesEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public InviteCodesEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record InviteCodeBody(string Code, DateTime ExpiresAtUtc);

    private async Task<(string login, string password)> SeedUserAsync(UserRole role)
    {
        const string password = "Correct-Horse-9!";
        var unique = Guid.NewGuid().ToString("N");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"user-{unique}@example.com",
            Username = $"user-{unique}",
            PasswordHash = hasher.Hash(password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Email, password);
    }

    private async Task<HttpClient> LoggedInClientAsync(UserRole role)
    {
        var (login, password) = await SeedUserAsync(role);
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        var tokens = await loginResponse.Content.ReadFromJsonAsync<TokenResponseDto>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Admin_CanCreateInviteCode()
    {
        var client = await LoggedInClientAsync(UserRole.Admin);

        var response = await client.PostAsync("/api/admin/invite-codes", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InviteCodeBody>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Code));
        Assert.True(body.ExpiresAtUtc > DateTime.UtcNow);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.InviteCodes.SingleAsync(c => c.Code == body.Code);
        Assert.Null(row.UsedByUserId);
        Assert.NotEqual(Guid.Empty, row.CreatedByAdminId);
    }

    [Fact]
    public async Task EndUser_CannotCreateInviteCode_Returns403()
    {
        var client = await LoggedInClientAsync(UserRole.EndUser);
        var response = await client.PostAsync("/api/admin/invite-codes", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_CannotCreateInviteCode_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/admin/invite-codes", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EachCall_GeneratesUniqueCode()
    {
        var client = await LoggedInClientAsync(UserRole.Admin);

        var first = await (await client.PostAsync("/api/admin/invite-codes", content: null))
            .Content.ReadFromJsonAsync<InviteCodeBody>();
        var second = await (await client.PostAsync("/api/admin/invite-codes", content: null))
            .Content.ReadFromJsonAsync<InviteCodeBody>();

        Assert.NotEqual(first!.Code, second!.Code);
    }
}
