using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
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

    private static SyncOrchestrator NewOrchestrator(
        AppDbContext db, IJiraApiClient jira, int batchSize = 10_000) =>
        new(db, jira, new PassthroughProtector(), new FakeClock(DateTime.UtcNow),
            new NoopIngest(), new NoopDerivation(), new SyncCancellation(),
            Options(batchSize), NullLogger<SyncOrchestrator>.Instance);

    /// <summary>
    /// Runs one sync, then rewrites that run as a failure carrying a checkpoint.
    ///
    /// Going through a real run is what makes the ScopeFingerprint right: it is computed by a
    /// private method from the project's own queries, so writing one by hand would test a
    /// fingerprint the orchestrator never produces — the test would pass while real runs never
    /// matched anything.
    /// </summary>
    private static async Task SeedFailedCheckpointAsync(
        DbContextOptions<AppDbContext> options, ICurrentTenant tenant, Guid projectId,
        RecordingJiraClient jira, string stage, string? cursor, int itemsDone,
        SyncRunKind kind = SyncRunKind.Sync)
    {
        await using (var db = new AppDbContext(options, tenant))
            await NewOrchestrator(db, jira).RunProjectAsync(projectId);

        await using (var db = new AppDbContext(options, tenant))
        {
            var run = await db.SyncRuns.Where(r => r.JiraProjectId == projectId)
                .OrderByDescending(r => r.StartedAt).FirstAsync();
            run.Status = SyncRunStatus.Failed;
            run.Kind = kind;
            run.ResumeStage = stage;
            run.ResumeCursor = cursor;
            run.ResumeItemsDone = itemsDone;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task A_checkpoint_skips_the_stages_it_has_already_passed()
    {
        // The positive case the rest are measured against: a run that already finished fetching
        // must not ask Jira for a single page on the way back in.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var jira = new RecordingJiraClient((_, _) =>
            new SearchPage(new[] { new RawIssue("10001", "{}") }, null));

        await SeedFailedCheckpointAsync(options, tenant, projectId, jira,
            ResumeStage.Changelog, cursor: Guid.NewGuid().ToString(), itemsDone: 1);
        jira.Requests.Clear();

        await using var db = new AppDbContext(options, tenant);
        await NewOrchestrator(db, jira).RunProjectAsync(projectId);

        Assert.Empty(jira.Requests);
    }

    [Fact]
    public async Task A_checkpoint_is_only_inherited_within_the_same_scope()
    {
        // A checkpoint, like a watermark, says "I got this far through THIS set of queries".
        // After the queries change it describes a scope that was never fetched, and honouring it
        // would skip every issue the new query covers but the old one did not.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var jira = new RecordingJiraClient((_, _) =>
            new SearchPage(new[] { new RawIssue("10001", "{}") }, null));

        await SeedFailedCheckpointAsync(options, tenant, projectId, jira,
            ResumeStage.Changelog, cursor: Guid.NewGuid().ToString(), itemsDone: 1);

        await using (var db = new AppDbContext(options, tenant))
        {
            var q = await db.JiraQueries.FirstAsync(x => x.JiraProjectId == projectId);
            q.Jql = "project = B";                      // the scope changes
            await db.SaveChangesAsync();
        }
        jira.Requests.Clear();

        await using var db2 = new AppDbContext(options, tenant);
        await NewOrchestrator(db2, jira).RunProjectAsync(projectId);

        Assert.Contains(jira.Requests, r => r.Jql == "project = B");
    }

    [Fact]
    public async Task A_post_fetch_checkpoint_is_discarded_when_the_raw_rows_are_gone()
    {
        // Retention, an erasure or a manual delete. The cursor indexes into rows that no longer
        // exist, so resuming would start part-way through a project holding nothing at all.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var jira = new RecordingJiraClient((_, _) =>
            new SearchPage(new[] { new RawIssue("10001", "{}") }, null));

        await SeedFailedCheckpointAsync(options, tenant, projectId, jira,
            ResumeStage.DeriveIssues, cursor: Guid.NewGuid().ToString(), itemsDone: 1);

        await using (var db = new AppDbContext(options, tenant))
        {
            db.RawIssueSnapshots.RemoveRange(db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId));
            await db.SaveChangesAsync();
        }
        jira.Requests.Clear();

        await using var db2 = new AppDbContext(options, tenant);
        await NewOrchestrator(db2, jira).RunProjectAsync(projectId);

        Assert.NotEmpty(jira.Requests);   // fell back to a full pull
    }

    [Fact]
    public async Task A_succeeded_run_leaves_no_checkpoint_behind()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var jira = new RecordingJiraClient((_, _) =>
            new SearchPage(new[] { new RawIssue("10001", "{}") }, null));

        await using var db = new AppDbContext(options, tenant);
        var result = await NewOrchestrator(db, jira).RunProjectAsync(projectId);

        Assert.Equal("Succeeded", result.Run!.Status);

        await using var read = new AppDbContext(options, tenant);
        var row = await read.SyncRuns.FirstAsync(r => r.Id == result.Run.Id);
        Assert.Null(row.ResumeStage);
        Assert.Null(row.ResumeCursor);
        Assert.Null(row.ResumeItemsDone);
    }
}
