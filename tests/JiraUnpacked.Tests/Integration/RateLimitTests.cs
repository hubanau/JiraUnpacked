// tests/JiraUnpacked.Tests/Integration/RateLimitTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// A factory that keeps the REAL rate limiters (the default JiraUnpackedApiFactory no-ops them
/// so the broad suite isn't throttled). Points at the test DB the same way.
/// </summary>
public sealed class RateLimitedApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Sync:SchedulerEnabled", "false");
        builder.UseSetting("ConnectionStrings:Default", JiraUnpackedApiFactory.TestConnectionString);
        builder.ConfigureTestServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddScoped<JiraUnpacked.Infrastructure.Tenancy.TenantConnectionInterceptor>();
            services.AddDbContext<AppDbContext>((sp, o) =>
                o.UseNpgsql(JiraUnpackedApiFactory.TestAppConnectionString)
                 .AddInterceptors(sp.GetRequiredService<JiraUnpacked.Infrastructure.Tenancy.TenantConnectionInterceptor>()));
        });
    }
}

public sealed class RateLimitTests : IClassFixture<RateLimitedApiFactory>, IAsyncLifetime
{
    private readonly RateLimitedApiFactory _factory;
    public RateLimitTests(RateLimitedApiFactory f) => _factory = f;
    public Task InitializeAsync() => new JiraUnpackedApiFactory().EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    private async Task<(HttpClient client, Guid projectId)> AuthedProjectAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User { Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        // A project id that doesn't exist is fine — the limiter runs before the 404, and the point
        // is the 429, not the sync result.
        return (c, Guid.NewGuid());
    }

    [Fact]
    public async Task Sync_endpoint_returns_429_after_the_per_user_limit()
    {
        var (client, projectId) = await AuthedProjectAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++) // limit is 5/min per user
            statuses.Add((await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { })).StatusCode);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Auth_endpoint_returns_429_after_the_per_ip_limit()
    {
        var client = _factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++) // limit is 10/min per IP
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login",
                new { emailOrUsername = "nobody@x.com", password = "wrong" })).StatusCode);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
