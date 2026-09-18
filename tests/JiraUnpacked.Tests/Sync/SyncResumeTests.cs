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

        /// <summary>Simulates an infrastructure failure: throws once this many requests have been served.</summary>
        public int? ThrowAfterRequests { get; set; }

        /// <summary>Page tokens Jira no longer accepts — what an opaque token looks like after it expires.</summary>
        public HashSet<string> RejectedTokens { get; } = new();

        public Task<SearchPage> SearchIssuesPageAsync(
            JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
        {
            Requests.Add((jql, nextPageToken));

            if (nextPageToken is not null && RejectedTokens.Contains(nextPageToken))
                throw new JiraApiException(JiraErrorKind.Upstream, "Page token is no longer valid.");

            if (ThrowAfterRequests is int n && Requests.Count > n)
                throw new InvalidOperationException("simulated infrastructure failure");

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

    /// <summary>
    /// Serves <paramref name="pageCount"/> pages of <paramref name="perPage"/> issues, chained by
    /// the tokens p1, p2, ... The token IS the position, so a resumed run asking for "p2" gets
    /// page three and nothing earlier.
    /// </summary>
    private static Func<string, string?, SearchPage> Pages(int pageCount, int perPage) =>
        (jql, token) =>
        {
            var index = token is null ? 0 : int.Parse(token[1..]);
            var issues = Enumerable.Range(0, perPage)
                .Select(i => new RawIssue($"{jql.GetHashCode():X}-{index * perPage + i}", "{}"))
                .ToArray();
            var next = index + 1 < pageCount ? $"p{index + 1}" : null;
            return new SearchPage(issues, next);
        };

    [Fact]
    public async Task A_resumed_fetch_does_not_ask_Jira_for_pages_it_already_stored()
    {
        // THE test. A resumption that merely redid the work harmlessly would satisfy a weaker
        // assertion — "all the data is there" — while fixing nothing, so this asserts on what is
        // ABSENT from the request log: the query is never restarted from its first page.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");

        var jira = new RecordingJiraClient(Pages(pageCount: 3, perPage: 2)) { ThrowAfterRequests = 2 };

        await using (var db = new AppDbContext(options, tenant))
            await NewOrchestrator(db, jira, batchSize: 2).RunProjectAsync(projectId);

        jira.ThrowAfterRequests = null;
        jira.Requests.Clear();

        await using (var db = new AppDbContext(options, tenant))
            await NewOrchestrator(db, jira, batchSize: 2).RunProjectAsync(projectId);

        Assert.NotEmpty(jira.Requests);
        Assert.DoesNotContain(jira.Requests, r => r.Token is null);   // never restarted the query

        await using var read = new AppDbContext(options, tenant);
        Assert.Equal(6, await read.RawIssueSnapshots.CountAsync(r => r.JiraProjectId == projectId));
    }

    [Fact]
    public async Task An_expired_page_token_restarts_only_its_own_query()
    {
        // nextPageToken is opaque and can expire across a long outage. Restarting that one query
        // costs time but not correctness, because snapshots upsert. Restarting the whole run is
        // what this design exists to prevent.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A", "project = B");

        var jira = new RecordingJiraClient(Pages(pageCount: 2, perPage: 2));
        await SeedFailedCheckpointAsync(options, tenant, projectId, jira,
            ResumeStage.Fetch, cursor: "1|stale", itemsDone: 4);

        jira.RejectedTokens.Add("stale");
        jira.Requests.Clear();

        await using (var db = new AppDbContext(options, tenant))
            await NewOrchestrator(db, jira, batchSize: 2).RunProjectAsync(projectId);

        Assert.DoesNotContain(jira.Requests, r => r.Jql == "project = A");          // query 0 untouched
        Assert.Contains(jira.Requests, r => r.Jql == "project = B" && r.Token is null);
    }

    /// <summary>
    /// Refuses any save carrying a snapshot — how Postgres behaves when one payload in the batch
    /// is unstorable, which this project has hit for real with a NUL in an issue description.
    /// </summary>
    private sealed class RejectsSnapshotsDbContext : AppDbContext
    {
        public RejectsSnapshotsDbContext(DbContextOptions<AppDbContext> o, ICurrentTenant t) : base(o, t) { }

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            if (ChangeTracker.Entries<RawIssueSnapshot>().Any())
                throw new DbUpdateException("22P05: unsupported Unicode escape sequence");
            return base.SaveChangesAsync(ct);
        }
    }

    [Fact]
    public async Task A_batch_that_fails_to_save_does_not_advance_the_cursor()
    {
        // The invariant the whole design rests on. SaveCheckpointAsync sets the cursor and saves
        // in one call, but the failure handler deliberately keeps the RUN's pending changes while
        // discarding the data — so without care it would persist a cursor pointing past issues
        // that were never stored, and every later run would skip them silently and for good.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");

        var jira = new RecordingJiraClient(Pages(pageCount: 3, perPage: 2));

        await using (var db = new RejectsSnapshotsDbContext(options, tenant))
            await NewOrchestrator(db, jira, batchSize: 2).RunProjectAsync(projectId);

        await using var read = new AppDbContext(options, tenant);
        var run = await read.SyncRuns.Where(r => r.JiraProjectId == projectId)
            .OrderByDescending(r => r.StartedAt).FirstAsync();

        Assert.Equal("Failed", run.Status.ToString());
        Assert.Equal(0, await read.RawIssueSnapshots.CountAsync(r => r.JiraProjectId == projectId));
        Assert.Null(run.ResumeCursor);   // nothing committed, so nothing to resume past
    }
}
