// tests/JiraUnpacked.Tests/Integration/SyncCancelTestData.cs
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

internal static class SyncCancelTestData
{
    /// <summary>
    /// A Running row with no worker behind it — what a previous process leaves when it dies.
    /// Seeded by SQL precisely because no code path in this process can produce one.
    /// </summary>
    internal static async Task SeedRunningRunAsync(Guid tenant, Guid projectId)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"")
              VALUES (@i,@t,@p,now(),'Running','Sync',0)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId));
    }
}
