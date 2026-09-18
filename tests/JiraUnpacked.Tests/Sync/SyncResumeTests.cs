using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using JiraUnpacked.Tests.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Tests.Sync;

/// <summary>
/// A sync that is interrupted must resume where it stopped rather than start again.
///
/// The assertion that matters in most of these is what is ABSENT from the Jira request log. A
/// test that only checks the final data is correct passes just as well against a run that redoes
/// every page harmlessly — which is precisely the defect being fixed, so such a test would go
/// green while the project still burned two hours re-fetching what it already had.
/// </summary>
public class SyncResumeTests
{
    /// <summary>
    /// Records what was asked of Jira and in what order. Unlike the stub in SyncOrchestratorTests
    /// this keeps the page token too, because "did it re-request page one" is the whole question.
    /// </summary>
    internal sealed class RecordingJiraClient : IJiraApiClient
    {
        private readonly Func<string, string?, SearchPage> _search;
        public RecordingJiraClient(Func<string, string?, SearchPage> search) => _search = search;

        /// <summary>Every (jql, pageToken) pair requested, in order.</summary>
        public List<(string Jql, string? Token)> Requests { get; } = new();

        public List<string> SeenJql => Requests.Select(r => r.Jql).ToList();

        public int? ApproximateCount { get; set; }

        public Task<int?> ApproximateCountAsync(JiraCredentials creds, string jql, CancellationToken ct = default)
            => Task.FromResult(ApproximateCount);

        public Task<SearchPage> SearchIssuesPageAsync(
            JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
        {
            Requests.Add((jql, nextPageToken));
            return Task.FromResult(_search(jql, nextPageToken));
        }

        public Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JqlValidationResult> ParseJqlAsync(JiraCredentials creds, string jql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChangelogPage> GetIssueChangelogPageAsync(JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default) => throw new NotSupportedException();
    }

    internal sealed class PassthroughProtector : ITokenProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    internal sealed class NoopIngest : IChangelogIngestService
    {
        public Task IngestProjectAsync(
            Guid projectId, JiraCredentials creds,
            Func<int, int, CancellationToken, Task>? onProgress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class NoopDerivation : IDerivationService
    {
        public Task DeriveProjectAsync(
            Guid projectId,
            Func<int, int, CancellationToken, Task>? onProgress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// ONE root for the whole class. Each call still gets a unique database NAME, so tests stay
    /// isolated — but a fresh InMemoryDatabaseRoot per call also builds a fresh EF service
    /// provider, and EF throws once twenty exist. Passing a new root each time made the suite
    /// fail as a whole while every test passed alone.
    /// </summary>
    private static readonly InMemoryDatabaseRoot Root = new();

    private static DbContextOptions<AppDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), Root)
            .Options;

    private static IOptions<SyncSchedulerOptions> Options(int batchSize = 10_000) =>
        Microsoft.Extensions.Options.Options.Create(new SyncSchedulerOptions { CheckpointBatchSize = batchSize });

    private static Guid SeedProject(DbContextOptions<AppDbContext> options, ICurrentTenant tenant, params string[] jql)
    {
        var projectId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        using var db = new AppDbContext(options, tenant);
        db.JiraConnections.Add(new JiraConnection
        {
            Id = connectionId, BaseUrl = "https://acme.atlassian.net", Email = "ops@acme.com", ApiTokenEncrypted = "enc"
        });
        db.JiraProjects.Add(new JiraProject
        {
            Id = projectId, JiraConnectionId = connectionId,
            JiraProjectKey = "ACME", JiraProjectId = "1", SyncIntervalMinutes = 60
        });
        foreach (var q in jql)
            db.JiraQueries.Add(new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = q });
        db.SaveChanges();
        return projectId;
    }

    [Fact]
    public async Task The_queries_are_fetched_in_a_deterministic_sorted_order()
    {
        // A fetch cursor says "resume at query 2". That is only meaningful if query 2 is the same
        // query every time. Postgres promises no ordering without ORDER BY, so unsorted, a resumed
        // run can re-enter the wrong query and silently skip every issue in another.
        //
        // ScopeFingerprint already sorts before hashing, so the fingerprint never cared. The fetch
        // loop is what did not, and it is the fetch loop the cursor indexes into.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = Z", "project = A", "project = M");

        var jira = new RecordingJiraClient((_, _) => new SearchPage(Array.Empty<RawIssue>(), null));

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(),
            new SyncCancellation(), Options(), NullLogger<SyncOrchestrator>.Instance);

        await orchestrator.RunProjectAsync(projectId);

        Assert.Equal(new[] { "project = A", "project = M", "project = Z" }, jira.SeenJql);
    }
}
