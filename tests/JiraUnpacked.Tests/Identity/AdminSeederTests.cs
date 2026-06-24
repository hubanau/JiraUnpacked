using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Identity;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Tests.Identity;

public sealed class AdminSeederTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public AdminSeederTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task RunSeederAsync(SeedOptions options)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var seeder = new AdminSeeder(db, hasher, clock, Options.Create(options));
        await seeder.SeedAsync();
    }

    private static SeedOptions OneAdmin(string email, string username, string password) =>
        new() { Admins = { new SeedAdmin { Email = email, Username = username, Password = password } } };

    [Fact]
    public async Task Seed_CreatesActiveAdmin()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"admin-{unique}@example.com";

        await RunSeederAsync(OneAdmin(email, $"admin-{unique}", "Seeded-Admin-9!"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.NotEqual("Seeded-Admin-9!", user.PasswordHash);
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        var unique = Guid.NewGuid().ToString("N");
        var options = OneAdmin($"admin-{unique}@example.com", $"admin-{unique}", "Seeded-Admin-9!");

        await RunSeederAsync(options);
        await RunSeederAsync(options);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Users.CountAsync(u => u.Email == $"admin-{unique}@example.com");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SeededAdmin_CanLogIn()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"admin-{unique}@example.com";
        const string password = "Seeded-Admin-9!";

        await RunSeederAsync(OneAdmin(email, $"admin-{unique}", password));

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
