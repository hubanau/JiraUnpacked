// tests/JiraUnpacked.Tests/Integration/TokenEnvelopeUpgradeTests.cs
using System.Security.Cryptography;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The background rewrite of pre-envelope tokens. What matters here is that it upgrades real
/// rows without ever changing the token, leaves current rows alone, and does its writing
/// inside each tenant's scope — IPrivilegedDbContextFactory is a cross-tenant READER only.
/// </summary>
public sealed class TokenEnvelopeUpgradeTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public TokenEnvelopeUpgradeTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The pre-envelope layout: token sealed under the master key, raw base64.</summary>
    private static string LegacyProtect(byte[] masterKey, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(masterKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        var combined = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(tag, 0, combined, 12, 16);
        Buffer.BlockCopy(cipher, 0, combined, 12 + 16, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    /// <summary>The master key the running host was configured with.</summary>
    private byte[] MasterKey()
    {
        using var scope = _factory.Services.CreateScope();
        var configuration = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        return Convert.FromBase64String(configuration["TokenProtection:Key"]!);
    }

    private static async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await FilterTestData.Exec(conn,
            @"INSERT INTO ""Users""(""Id"",""Email"",""Username"",""PasswordHash"",""Role"",""Status"",""CreatedAt"")
              VALUES (@id,@e,@u,'not-authenticated','EndUser','Active',now())",
            ("id", tenant), ("e", $"tok-{tenant:N}@x.com"), ("u", $"tok-{tenant:N}"));
        return tenant;
    }

    private static async Task<Guid> SeedConnectionAsync(Guid tenant, string storedToken)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await FilterTestData.Exec(conn,
            @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"")
              VALUES (@id,@t,'https://x','e@x.com',@tok,now())",
            ("id", id), ("t", tenant), ("tok", storedToken));
        return id;
    }

    private static async Task<string> ReadStoredTokenAsync(Guid connectionId)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""ApiTokenEncrypted"" FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", connectionId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task RunUpgradeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TokenEnvelopeUpgrader>().UpgradeAsync();
    }

    [Fact]
    public async Task A_legacy_token_is_rewritten_without_changing_the_token()
    {
        var key = MasterKey();
        var tenant = await SeedTenantAsync();
        var connectionId = await SeedConnectionAsync(tenant, LegacyProtect(key, "jira-token-abc"));

        await RunUpgradeAsync();

        var stored = await ReadStoredTokenAsync(connectionId);
        Assert.StartsWith("v1:", stored);

        // The whole point: the format changed, the secret did not. A rewrite that lost the
        // token would break the connection silently — nothing reads it until the next sync.
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();
        Assert.Equal("jira-token-abc", protector.Unprotect(stored));
    }

    [Fact]
    public async Task An_already_upgraded_row_is_left_untouched()
    {
        var tenant = await SeedTenantAsync();

        string current;
        using (var scope = _factory.Services.CreateScope())
        {
            current = scope.ServiceProvider.GetRequiredService<ITokenProtector>().Protect("already-current");
        }
        var connectionId = await SeedConnectionAsync(tenant, current);

        await RunUpgradeAsync();

        // Byte-identical, not merely still-decryptable: re-wrapping a current row would churn
        // ciphertext on every boot for no benefit.
        Assert.Equal(current, await ReadStoredTokenAsync(connectionId));
    }

    [Fact]
    public async Task Running_twice_changes_nothing_the_second_time()
    {
        var key = MasterKey();
        var tenant = await SeedTenantAsync();
        var connectionId = await SeedConnectionAsync(tenant, LegacyProtect(key, "idempotent"));

        await RunUpgradeAsync();
        var afterFirst = await ReadStoredTokenAsync(connectionId);

        await RunUpgradeAsync();

        // It runs on every start-up, so a second pass must be a no-op.
        Assert.Equal(afterFirst, await ReadStoredTokenAsync(connectionId));
    }

    [Fact]
    public async Task Legacy_rows_belonging_to_different_tenants_are_all_upgraded()
    {
        var key = MasterKey();
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var a = await SeedConnectionAsync(tenantA, LegacyProtect(key, "token-a"));
        var b = await SeedConnectionAsync(tenantB, LegacyProtect(key, "token-b"));

        await RunUpgradeAsync();

        // Each write happens inside its OWN tenant's scope, under RLS — the privileged context
        // only supplied the ids. If the scoping were wrong, one of these would still be legacy
        // because the row would have been invisible to the write.
        var storedA = await ReadStoredTokenAsync(a);
        var storedB = await ReadStoredTokenAsync(b);
        Assert.StartsWith("v1:", storedA);
        Assert.StartsWith("v1:", storedB);

        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();
        Assert.Equal("token-a", protector.Unprotect(storedA));
        Assert.Equal("token-b", protector.Unprotect(storedB));
    }
}
