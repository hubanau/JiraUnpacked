// tests/JiraUnpacked.Tests/Tenancy/ReaderRoleTests.cs
using JiraUnpacked.Tests.Integration;
using Npgsql;

namespace JiraUnpacked.Tests.Tenancy;

/// <summary>
/// The background-reader role. Two properties make it worth having, and neither is visible
/// from the application layer, so both are asserted against the database directly.
///
/// It must BYPASS RLS — every tenant table is FORCE ROW LEVEL SECURITY, so without it the
/// due-project finder, the retention job, the reaper and the token upgrader all read zero rows
/// and fail silently. And it must be READ-ONLY — that is the whole point of not reusing the
/// owner connection for reads that never needed to write.
/// </summary>
public sealed class ReaderRoleTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public ReaderRoleTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<NpgsqlConnection> ReaderAsync()
    {
        var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestReaderConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<PostgresException> RefusedAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task The_reader_bypasses_row_level_security()
    {
        await using var conn = await ReaderAsync();

        // The property the four background readers depend on. They connect with NO tenant set,
        // so every FORCEd policy evaluates against a null GUC and hides everything — unless the
        // role bypasses RLS. Asserted on the role attribute rather than on a row count, because
        // an empty table would make a row-count assertion pass for the wrong reason.
        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user"));
    }

    [Fact]
    public async Task The_reader_can_actually_read_across_tenants()
    {
        // Two tenants, so the read is genuinely cross-tenant rather than trivially empty.
        var a = await SeedProjectAsync();
        var b = await SeedProjectAsync();

        await using var conn = await ReaderAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT count(*) FROM ""JiraProjects"" WHERE ""Id"" = ANY(@ids)";
        cmd.Parameters.AddWithValue("ids", new[] { a, b });

        // No tenant GUC is set on this connection. Both rows must still be visible — that is
        // what makes DueProjectFinder able to see every tenant's due projects.
        Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("INSERT INTO \"Users\"(\"Id\",\"Email\",\"Username\",\"PasswordHash\",\"Role\",\"Status\",\"CreatedAt\") VALUES (gen_random_uuid(),'x@y.z','x','h','EndUser','Active',now())")]
    [InlineData("UPDATE \"JiraProjects\" SET \"JiraProjectKey\" = 'HACKED'")]
    [InlineData("DELETE FROM \"SyncRuns\"")]
    public async Task The_reader_cannot_write(string sql)
    {
        await using var conn = await ReaderAsync();

        // The reason this role exists instead of reusing the owner connection. Every caller
        // writes through the tenant-scoped context under RLS; a write attempted here is a bug,
        // and the database refuses it rather than letting it succeed outside tenant isolation.
        var ex = await RefusedAsync(conn, sql);
        Assert.Equal("42501", ex.SqlState);   // insufficient_privilege
    }

    [Theory]
    [InlineData("CREATE TABLE reader_should_not_manage (id int)")]
    [InlineData("DROP POLICY tenant_isolation ON \"JiraProjects\"")]
    [InlineData("ALTER TABLE \"Issues\" DISABLE ROW LEVEL SECURITY")]
    public async Task The_reader_cannot_alter_the_schema_or_disarm_isolation(string sql)
    {
        await using var conn = await ReaderAsync();

        // A compromised app process previously held owner credentials, so it could drop the
        // very policies that enforce isolation layer 2. It no longer can.
        var ex = await RefusedAsync(conn, sql);
        Assert.Contains(ex.SqlState, new[] { "42501", "42P01" });   // denied, or cannot even see it
    }

    [Fact]
    public async Task The_app_role_still_does_NOT_bypass_row_level_security()
    {
        // Guards the guard. Granting BYPASSRLS to the wrong role would void isolation layer 2
        // entirely and every other test in the suite would keep passing.
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
        await conn.OpenAsync();

        Assert.False(await ScalarAsync<bool>(conn,
            "SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user"));
    }

    /// <summary>Seeds a tenant with one project via the privileged connection. Returns its id.</summary>
    private static async Task<Guid> SeedProjectAsync()
    {
        var tenant = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        await FilterTestData.Exec(conn,
            @"INSERT INTO ""Users""(""Id"",""Email"",""Username"",""PasswordHash"",""Role"",""Status"",""CreatedAt"")
              VALUES (@id,@e,@u,'not-authenticated','EndUser','Active',now())",
            ("id", tenant), ("e", $"rdr-{tenant:N}@x.com"), ("u", $"rdr-{tenant:N}"));
        await FilterTestData.Exec(conn,
            @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"")
              VALUES (@id,@t,'https://x','e','c',now())",
            ("id", connectionId), ("t", tenant));
        await FilterTestData.Exec(conn,
            @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"")
              VALUES (@id,@t,@c,'ACME','1',60,now())",
            ("id", projectId), ("t", tenant), ("c", connectionId));

        return projectId;
    }
}
