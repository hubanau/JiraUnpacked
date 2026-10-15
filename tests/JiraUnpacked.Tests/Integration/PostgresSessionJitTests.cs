using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The app's sessions run with JIT off — the runtime (RLS) context and the background readers'
/// privileged one alike, both as the host registers them, and both on the test database. See
/// PostgresSessionOptions for why.
/// </summary>
public sealed class PostgresSessionJitTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public PostgresSessionJitTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The session's jit setting — after checking the session is on the test database, not the dev one.</summary>
    private static async Task<string?> ShowJitAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT current_database()";
            Assert.Equal(JiraUnpackedApiFactory.TestDatabase, (string?)await cmd.ExecuteScalarAsync());
            cmd.CommandText = "SHOW jit";
            return (string?)await cmd.ExecuteScalarAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task The_app_context_runs_with_jit_off()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.Equal("off", await ShowJitAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>()));
    }

    [Fact]
    public async Task The_privileged_reader_context_runs_with_jit_off()
    {
        await using var db = _factory.Services.GetRequiredService<IPrivilegedDbContextFactory>().Create();
        Assert.Equal("off", await ShowJitAsync(db));
    }
}
