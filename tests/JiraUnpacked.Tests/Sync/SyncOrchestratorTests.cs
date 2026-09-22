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

namespace JiraUnpacked.Tests.Sync;

public class SyncOrchestratorTests
{
    // Passes the token through unchanged — the orchestrator only needs Unprotect to return something.
    private sealed class PassthroughProtector : ITokenProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    // Jira client whose only exercised member is the issue search; the rest are unused by RunProjectAsync.
    private sealed class StubJiraClient : IJiraApiClient
    {
        private readonly Func<Task<SearchPage>> _search;
        public StubJiraClient(Func<Task<SearchPage>> search) => _search = search;

        /// <summary>Every JQL the orchestrator asked for, in order. Empty means it never called Jira.</summary>
        public List<string> SeenJql { get; } = new();

        /// <summary>What the count endpoint returns. Null models Jira declining to say.</summary>
        public int? ApproximateCount { get; set; }

        /// <summary>Every JQL the orchestrator asked for a count of.</summary>
        public List<string> CountedJql { get; } = new();

        public Task<int?> ApproximateCountAsync(JiraCredentials creds, string jql, CancellationToken ct = default)
        {
            CountedJql.Add(jql);
            return Task.FromResult(ApproximateCount);
        }

        public Task<SearchPage> SearchIssuesPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
        {
            SeenJql.Add(jql);
            return _search();
        }
        public Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JqlValidationResult> ParseJqlAsync(JiraCredentials creds, string jql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChangelogPage> GetIssueChangelogPageAsync(JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>The account's zone as Jira reports it. UTC keeps the watermark clause readable.</summary>
        public string? TimeZone { get; set; } = "UTC";
        public Task<string?> GetUserTimeZoneAsync(JiraCredentials creds, CancellationToken ct = default) => Task.FromResult(TimeZone);

        /// <summary>Unanswered by default: reconciliation then skips, which is its documented fallback.</summary>
        public Func<string, IssueIdPage>? IdListing { get; set; }
        public Task<IssueIdPage> SearchIssueIdsPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
            => IdListing is null ? throw new NotSupportedException() : Task.FromResult(IdListing(jql));
    }

    // These failure tests never reach the transform phase (they throw during extract), so no-op stubs suffice.
    private sealed class NoopIngest : IChangelogIngestService
    {
        public Task IngestProjectAsync(
            Guid projectId, JiraCredentials creds,
            Guid? afterSnapshotId = null, int alreadyDone = 0,
            Func<int, int, Guid, CancellationToken, Task>? onBatch = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Ingest that hands its progress reporter to the test to drive.</summary>
    private sealed class ReportingIngest : IChangelogIngestService
    {
        private readonly Func<Func<int, int, Guid, CancellationToken, Task>, CancellationToken, Task> _run;
        public ReportingIngest(Func<Func<int, int, Guid, CancellationToken, Task>, CancellationToken, Task> run) => _run = run;

        public Task IngestProjectAsync(
            Guid projectId, JiraCredentials creds,
            Guid? afterSnapshotId = null, int alreadyDone = 0,
            Func<int, int, Guid, CancellationToken, Task>? onBatch = null,
            CancellationToken ct = default) => _run(onBatch!, ct);
    }

    /// <summary>Derivation that hands its progress reporter to the test to drive.</summary>
    private sealed class ReportingDerivation : IDerivationService
    {
        private readonly Func<Func<int, int, CancellationToken, Task>, CancellationToken, Task> _run;
        public ReportingDerivation(Func<Func<int, int, CancellationToken, Task>, CancellationToken, Task> run) => _run = run;

        public Task DeriveProjectAsync(
            Guid projectId, string? resumeStage = null, string? resumeCursor = null,
            int alreadyDone = 0, int batchSize = 10_000,
            Func<string, string, int, int, CancellationToken, Task>? onBatch = null,
            DateTime? incrementalSince = null,
            CancellationToken ct = default)
            // Adapts the stage/cursor callback back to the (done, total) shape these older tests
            // drive; they are about progress reporting, not about where a run resumes.
            => _run((d, t, token) => onBatch!(ResumeStage.DeriveIssues, Guid.NewGuid().ToString(), d, t, token), ct);
    }

    private sealed class NoopDerivation : IDerivationService
    {
        public Task DeriveProjectAsync(
            Guid projectId, string? resumeStage = null, string? resumeCursor = null,
            int alreadyDone = 0, int batchSize = 10_000,
            Func<string, string, int, int, CancellationToken, Task>? onBatch = null,
            DateTime? incrementalSince = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    // Simulates an infra failure mid-sync: throws a DbUpdateException (carrying raw SQL detail) on the Nth save.
    private sealed class FailingSaveDbContext : AppDbContext
    {
        private readonly int _throwOnCall;
        private int _calls;
        public FailingSaveDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant tenant, int throwOnCall)
            : base(options, tenant) => _throwOnCall = throwOnCall;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (++_calls == _throwOnCall)
                throw new DbUpdateException(
                    "23505: duplicate key value violates unique constraint \"IX_RawIssueSnapshots_EndUserId_JiraIssueId\"");
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Refuses to save while any RawIssueSnapshot is pending, and succeeds once none is — which
    /// is exactly how Postgres behaves when one payload in the batch is unstorable. The point is
    /// that the SAME failure recurs until the offending changes are discarded, so a failure path
    /// that saves through the same context throws a second time.
    /// </summary>
    private sealed class RejectsSnapshotsDbContext : AppDbContext
    {
        public int RejectedSaves { get; private set; }

        public RejectsSnapshotsDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant tenant)
            : base(options, tenant) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ChangeTracker.Entries<RawIssueSnapshot>().Any())
            {
                RejectedSaves++;
                throw new DbUpdateException(
                    "22P05: unsupported Unicode escape sequence",
                    new Exception("Postgres refuses NUL in jsonb"));
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Default scheduler options. These tests predate checkpointing and none of them
    /// depend on the batch size, so they all take the production default.</summary>
    private static Microsoft.Extensions.Options.IOptions<SyncSchedulerOptions> TestSyncOptions =>
        Microsoft.Extensions.Options.Options.Create(new SyncSchedulerOptions());

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

    private static (Guid projectId, Guid connectionId) Seed(
        DbContextOptions<AppDbContext> options, ICurrentTenant tenant, bool withQuery = true)
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
        if (withQuery)
            db.JiraQueries.Add(new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = "project = ACME" });
        db.SaveChanges();
        return (projectId, connectionId);
    }

    /// <summary>
    /// The reported bug. A project added through the UI has no JiraQuery — there is no way to
    /// create one — so the orchestrator's `foreach (var query in queries)` had nothing to iterate.
    /// It called Jira zero times, processed zero issues, and recorded Succeeded: a green run, no
    /// error, and no data. An empty query list means "nothing to NARROW by", not "nothing to sync".
    /// </summary>
    [Fact]
    public async Task A_project_with_no_queries_syncs_the_whole_project()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        // Jira was actually asked, and asked for THIS project — the assertion that fails before
        // the fix, where SeenJql is empty.
        Assert.Equal(new[] { "project = \"ACME\"" }, jira.SeenJql);
        Assert.Equal("Succeeded", result.Run!.Status);
        Assert.Equal(1, result.Run.IssuesProcessed);
    }

    /// <summary>
    /// The bug leaves wreckage behind it: every silent no-op was recorded Succeeded, and the
    /// watermark is the newest such run. Left alone, the first sync AFTER the fix would ask Jira
    /// only for issues changed since a sync that never fetched anything, and the project would
    /// stay empty — the fix appearing not to work. A project holding no snapshot has no baseline
    /// to be incremental from, so the watermark is discarded and the pull starts clean.
    /// </summary>
    [Fact]
    public async Task A_watermark_left_by_runs_that_stored_nothing_is_discarded()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        // Exactly the state on disk after the bug: a Succeeded Sync run, and no stored issue.
        await using (var seed = new AppDbContext(options, tenant))
        {
            seed.SyncRuns.Add(new SyncRun
            {
                Id = Guid.NewGuid(), JiraProjectId = projectId, Kind = SyncRunKind.Sync,
                Status = SyncRunStatus.Succeeded, StartedAt = DateTime.UtcNow.AddHours(-1),
                FinishedAt = DateTime.UtcNow.AddHours(-1), IssuesProcessed = 0
            });
            await seed.SaveChangesAsync();
        }

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        await orchestrator.RunProjectAsync(projectId);

        // No "AND updated >= ..." clause: the whole project, not the sliver since a phantom sync.
        Assert.Equal(new[] { "project = \"ACME\"" }, jira.SeenJql);
    }

    /// <summary>
    /// The converse, on the whole-project default: once a scope has genuinely been fetched, the
    /// watermark is honoured and the pull stays incremental. Discarding it every time would turn
    /// each sync into a full refetch of the backlog.
    ///
    /// The first watermark is established by RUNNING a sync rather than by hand-seeding a row —
    /// a seeded run cannot say which scope it covered, and a fixture that claims a successful
    /// sync while leaving no trace of one is the same incoherence this rule exists to reject.
    /// </summary>
    [Fact]
    public async Task A_watermark_is_honoured_once_the_scope_has_been_fetched()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        await using (var db1 = new AppDbContext(options, tenant))
        {
            var o1 = new SyncOrchestrator(db1, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o1.RunProjectAsync(projectId);
        }

        jira.SeenJql.Clear();
        await using (var db2 = new AppDbContext(options, tenant))
        {
            var o2 = new SyncOrchestrator(db2, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        Assert.Equal(new[] { "(project = \"ACME\") AND updated >= \"2026-03-01 09:25\"" }, jira.SeenJql);
    }

    /// <summary>
    /// Adding a query CHANGES THE SCOPE, so the incremental watermark from the old scope must not
    /// carry over. Reported from production: a project synced in full, then had a query added, and
    /// the next sync asked Jira for "(that query) AND updated >= yesterday" — returning 57 issues
    /// where the query itself matched 474. Everything older than the previous run was silently
    /// skipped, and would have stayed missing forever, because each sync advanced the watermark
    /// again. A scope that has never been fetched has no baseline to be incremental from.
    /// </summary>
    [Fact]
    public async Task Changing_the_queries_discards_the_previous_scopes_watermark()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        // First sync: no query, so the whole project — and it stores an issue, so the watermark
        // that follows is genuine rather than the no-op kind.
        await using (var db1 = new AppDbContext(options, tenant))
        {
            var o1 = new SyncOrchestrator(db1, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o1.RunProjectAsync(projectId);
        }
        Assert.Equal(new[] { "project = \"ACME\"" }, jira.SeenJql);

        // The user then narrows the project with a query of their own.
        await using (var seed = new AppDbContext(options, tenant))
        {
            seed.JiraQueries.Add(new JiraQuery
            {
                Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = "project = ACME AND created >= -30d"
            });
            await seed.SaveChangesAsync();
        }

        jira.SeenJql.Clear();
        await using (var db2 = new AppDbContext(options, tenant))
        {
            var o2 = new SyncOrchestrator(db2, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        // The new query in full — no "AND updated >= ..." bolted on from the old scope.
        Assert.Equal(new[] { "project = ACME AND created >= -30d" }, jira.SeenJql);
    }

    /// <summary>
    /// The other half: an UNCHANGED scope still syncs incrementally. Discarding the watermark
    /// whenever it was convenient would turn every sync into a full refetch of the backlog.
    /// </summary>
    [Fact]
    public async Task An_unchanged_scope_keeps_syncing_incrementally()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: true);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        await using (var db1 = new AppDbContext(options, tenant))
        {
            var o1 = new SyncOrchestrator(db1, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o1.RunProjectAsync(projectId);
        }

        jira.SeenJql.Clear();
        await using (var db2 = new AppDbContext(options, tenant))
        {
            var o2 = new SyncOrchestrator(db2, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        Assert.Equal(new[] { "(project = ACME) AND updated >= \"2026-03-01 09:25\"" }, jira.SeenJql);
    }

    /// <summary>
    /// Reordering the same queries is not a change of scope. Without sorting, dragging a query up
    /// the list would silently trigger a full refetch of the whole backlog.
    /// </summary>
    [Fact]
    public async Task Reordering_the_same_queries_is_not_a_scope_change()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var a = new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = "project = ACME AND type = Bug" };
        var b = new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = "project = ACME AND type = Story" };
        await using (var seed = new AppDbContext(options, tenant))
        {
            seed.JiraQueries.AddRange(a, b);
            await seed.SaveChangesAsync();
        }

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        await using (var db1 = new AppDbContext(options, tenant))
        {
            var o1 = new SyncOrchestrator(db1, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o1.RunProjectAsync(projectId);
        }

        // Swap the creation order, which is the order the orchestrator reads them in.
        await using (var reorder = new AppDbContext(options, tenant))
        {
            var rows = await reorder.JiraQueries.Where(q => q.JiraProjectId == projectId).ToListAsync();
            reorder.JiraQueries.RemoveRange(rows);
            await reorder.SaveChangesAsync();
            reorder.JiraQueries.AddRange(
                new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = b.Jql },
                new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = a.Jql });
            await reorder.SaveChangesAsync();
        }

        jira.SeenJql.Clear();
        await using (var db2 = new AppDbContext(options, tenant))
        {
            var o2 = new SyncOrchestrator(db2, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        Assert.All(jira.SeenJql, j => Assert.Contains("updated >= \"2026-03-01 09:25\"", j));
    }

    /// <summary>
    /// Progress has to be readable WHILE the run is going, which is the whole point of it. The
    /// counted total and the per-page count are both published before the run ends; a value only
    /// written at the finish would leave a bar at zero for the entire sync and then jump.
    /// </summary>
    [Fact]
    public async Task Progress_is_published_during_the_run_not_only_at_the_end()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        // Two pages of two issues. The observer reads the run row as the orchestrator works.
        var observed = new List<(int Processed, int? Total, string? Phase)>();
        var pages = new Queue<SearchPage>(new[]
        {
            new SearchPage(new[] { new RawIssue("1", "{}"), new RawIssue("2", "{}") }, "tok"),
            new SearchPage(new[] { new RawIssue("3", "{}"), new RawIssue("4", "{}") }, null),
        });

        await using var db = new AppDbContext(options, tenant);

        var jira = new StubJiraClient(() =>
        {
            // Read through a SEPARATE context: the orchestrator's own tracked entity would report
            // in-memory values that were never saved, which would prove nothing about what a
            // polling client can actually see.
            using var probe = new AppDbContext(options, tenant);
            var r = probe.SyncRuns.Single(x => x.JiraProjectId == projectId);
            observed.Add((r.IssuesProcessed, r.TotalToSync, r.Phase));
            return Task.FromResult(pages.Dequeue());
        })
        { ApproximateCount = 4 };

        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        // Before page 1 the total is already known and the phase says what is happening.
        Assert.Equal(2, observed.Count);
        Assert.Equal((0, 4, "Fetching"), observed[0]);
        // Before page 2, page 1's two issues are already visible to a poller.
        Assert.Equal((2, 4, "Fetching"), observed[1]);

        Assert.Equal(4, result.Run!.IssuesProcessed);
        Assert.Equal(4, result.Run.TotalToSync);
        Assert.Null(result.Run.Phase);      // a finished run is doing nothing
    }

    /// <summary>
    /// A run whose SAVE fails must still be able to record that it failed.
    ///
    /// The failure path used to call SaveChanges through the same context that still held the
    /// rejected rows, so it re-attempted them, threw a second time, and that throw escaped the
    /// orchestrator entirely. The run stayed Running — forever, since the partial unique index
    /// permits one Running run per project and the reaper only sweeps after two hours. On screen
    /// it looked exactly like a hung sync; in reality it had died minutes earlier and could not
    /// say so. That is how a NUL in one issue's description presented.
    /// </summary>
    [Fact]
    public async Task A_run_that_cannot_save_still_records_its_own_failure()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        await using var db = new RejectsSnapshotsDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        // Must not throw: the orchestrator owns its failures.
        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Failed", result.Run!.Status);
        Assert.Equal("Sync failed unexpectedly (DbUpdateException).", result.Run.Error);
        Assert.Null(result.Run.Phase);

        // Exactly one save was rejected — the page. The failure write got through because the
        // pending snapshots were discarded first rather than re-sent.
        Assert.Equal(1, db.RejectedSaves);

        // And it is really persisted, not just returned: a Running row left behind is what blocks
        // the project.
        await using var probe = new AppDbContext(options, tenant);
        Assert.Equal(SyncRunStatus.Failed, probe.SyncRuns.Single(r => r.JiraProjectId == projectId).Status);
    }

    /// <summary>
    /// A long sync must not drag every issue it has already stored along with it.
    ///
    /// The context used to keep each RawIssueSnapshot tracked for the whole run — the entity AND
    /// an original-values copy for change detection — so a whole-project pull carried hundreds of
    /// megabytes of issue JSON to the end and re-scanned all of it on every page. Two real
    /// whole-project syncs died at ~31.7k issues, 16 minutes in, with the run marked failed and
    /// nothing wrong at the database.
    /// </summary>
    [Fact]
    public async Task Stored_pages_are_released_rather_than_accumulated()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        // Four pages. Whatever the page count, tracked snapshots must not grow with it.
        var pages = new Queue<SearchPage>(Enumerable.Range(0, 4).Select(p =>
            new SearchPage(
                Enumerable.Range(0, 25).Select(i => new RawIssue($"{p}-{i}", "{}")).ToArray(),
                p < 3 ? $"tok{p}" : null)));

        var trackedPerPage = new List<int>();

        await using var db = new AppDbContext(options, tenant);

        var jira = new StubJiraClient(() =>
        {
            trackedPerPage.Add(db.ChangeTracker.Entries<RawIssueSnapshot>().Count());
            return Task.FromResult(pages.Dequeue());
        });

        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        // Every page starts from a clean tracker, not from the pile left by its predecessors.
        Assert.Equal(new[] { 0, 0, 0, 0 }, trackedPerPage);
        Assert.Empty(db.ChangeTracker.Entries<RawIssueSnapshot>());

        // And detaching cost nothing in correctness: all 100 issues are stored, and the run —
        // which must stay tracked through it — still completed and recorded its own progress.
        Assert.Equal(100, result.Run!.IssuesProcessed);
        Assert.Equal("Succeeded", result.Run.Status);
        Assert.Equal(100, db.RawIssueSnapshots.Count(s => s.JiraProjectId == projectId));
    }

    /// <summary>
    /// Reading change history is a phase of its own, and on a large project it outlasts the fetch
    /// by a long way. Its progress has to be published under its own denominator: reporting the
    /// FETCH numbers through it is what left a real 92,673-issue run behind a bar frozen at 100%
    /// for many minutes, which reads as a hang rather than as work.
    /// </summary>
    [Fact]
    public async Task The_changelog_phase_publishes_its_own_progress()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("1", "{}") }, null)))
        { ApproximateCount = 1 };

        // Reads the run through a SEPARATE context, so what is asserted is what a polling client
        // could actually see rather than values that only ever lived in memory.
        var observed = new List<(string? Phase, int? Done, int? Total)>();
        var ingest = new ReportingIngest(async (report, ct) =>
        {
            await report(0, 40, Guid.NewGuid(), ct);
            Observe();
            await report(20, 40, Guid.NewGuid(), ct);
            Observe();
        });

        void Observe()
        {
            using var probe = new AppDbContext(options, tenant);
            var r = probe.SyncRuns.Single(x => x.JiraProjectId == projectId);
            observed.Add((r.Phase, r.PhaseProcessed, r.PhaseTotal));
        }

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), ingest, new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal(("Changelog", 0, 40), observed[0]);
        Assert.Equal(("Changelog", 20, 40), observed[1]);

        // A finished run is doing nothing, so it reports no phase progress — while the run's own
        // issue figures, which mean something else entirely, are untouched by any of this.
        Assert.Null(result.Run!.Phase);
        Assert.Null(result.Run.PhaseProcessed);
        Assert.Null(result.Run.PhaseTotal);
        Assert.Equal(1, result.Run.IssuesProcessed);
        Assert.Equal(1, result.Run.TotalToSync);
    }

    /// <summary>
    /// Rebuilding the model is the last long phase, and it too has to publish under its own
    /// denominator. Without this the bar showed whatever the previous phase had counted, which on
    /// a 92,673-issue project meant a full bar sitting over work that had only just started.
    /// </summary>
    [Fact]
    public async Task The_derive_phase_publishes_its_own_progress()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("1", "{}") }, null)))
        { ApproximateCount = 1 };

        var observed = new List<(string? Phase, int? Done, int? Total)>();
        var derivation = new ReportingDerivation(async (report, ct) =>
        {
            await report(0, 60, ct);
            Observe();
            await report(30, 60, ct);
            Observe();
        });

        void Observe()
        {
            using var probe = new AppDbContext(options, tenant);
            var r = probe.SyncRuns.Single(x => x.JiraProjectId == projectId);
            observed.Add((r.Phase, r.PhaseProcessed, r.PhaseTotal));
        }

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), derivation, new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal(("Deriving", 0, 60), observed[0]);
        Assert.Equal(("Deriving", 30, 60), observed[1]);
        Assert.Equal("Succeeded", result.Run!.Status);
        Assert.Null(result.Run.PhaseTotal);
    }

    /// <summary>
    /// A run the queue worker did not start must still be stoppable.
    ///
    /// The scheduled tick calls the orchestrator DIRECTLY, so registering for cancellation in the
    /// worker left every scheduled run uncancellable: Stop answered "nothing is running to stop"
    /// while a run was plainly running, because the registry had never heard of it. This calls
    /// RunProjectAsync the way the tick does — no worker anywhere — and stops it mid-flight.
    /// </summary>
    [Fact]
    public async Task A_run_started_outside_the_worker_can_still_be_stopped()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var cancellation = new SyncCancellation();

        // Stops the run from inside the work, which is the only moment the registration exists.
        var ingest = new ReportingIngest(async (report, ct) =>
        {
            var runId = new AppDbContext(options, tenant).SyncRuns.Single(r => r.JiraProjectId == projectId).Id;
            Assert.True(cancellation.Cancel(runId), "the orchestrator did not register the run");
            await report(0, 1, Guid.NewGuid(), ct);
        });

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("1", "{}") }, null)))
        { ApproximateCount = 1 };

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), ingest, new NoopDerivation(), cancellation,
            TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        // Cancelled, not Failed: a deliberate stop is not a fault.
        Assert.Equal("Cancelled", result.Run!.Status);
        Assert.Equal("Stopped on request.", result.Run.Error);
        Assert.Null(result.Run.Phase);
    }

    /// <summary>
    /// The count is drawn against the SAME JQL that is fetched, watermark clause included.
    /// Counting the unnarrowed query would give an incremental sync a denominator from a full
    /// pull, and the bar would stall at a few percent while the run was in fact finished.
    /// </summary>
    [Fact]
    public async Task The_total_counts_the_same_query_that_is_fetched()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)))
        { ApproximateCount = 3 };

        // A real first run establishes the scope, so the second is genuinely incremental.
        await using (var db1 = new AppDbContext(options, tenant))
        {
            var o1 = new SyncOrchestrator(db1, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o1.RunProjectAsync(projectId);
        }

        jira.SeenJql.Clear();
        jira.CountedJql.Clear();

        await using (var db2 = new AppDbContext(options, tenant))
        {
            var o2 = new SyncOrchestrator(db2, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        Assert.Equal(new[] { "(project = \"ACME\") AND updated >= \"2026-03-01 09:25\"" }, jira.CountedJql);
        Assert.Equal(jira.SeenJql, jira.CountedJql);
    }

    /// <summary>
    /// A count Jira will not give must cost the bar its denominator and nothing else. Failing the
    /// sync because the progress display could not be drawn would be the tail wagging the dog.
    /// </summary>
    [Fact]
    public async Task An_unavailable_count_leaves_the_total_unknown_and_the_sync_fine()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: false);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)))
        { ApproximateCount = null };

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Succeeded", result.Run!.Status);
        Assert.Equal(1, result.Run.IssuesProcessed);
        Assert.Null(result.Run.TotalToSync);   // unknown, NOT zero
    }

    /// <summary>
    /// A query, once defined, still narrows: the fallback is a default, not an addition. Otherwise
    /// every explicitly-scoped project would quietly start pulling its whole backlog too.
    /// </summary>
    [Fact]
    public async Task An_explicit_query_replaces_the_whole_project_default()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _unused) = Seed(options, tenant, withQuery: true);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        await orchestrator.RunProjectAsync(projectId);

        Assert.Equal(new[] { "project = ACME" }, jira.SeenJql);
    }

    [Fact]
    public async Task Unexpected_infra_failure_persists_a_generic_error_not_raw_detail()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _) = Seed(options, tenant);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        // throwOnCall: 2 => save #1 inserts the Running run, save #2 (the fetched page) throws the infra error.
        await using var db = new FailingSaveDbContext(options, tenant, throwOnCall: 2);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Failed", result.Run!.Status);
        // The type is named, the message is not: no SQL fragments, no column names, no
        // constraint names — all of which the DbUpdateException message carries.
        Assert.Equal("Sync failed unexpectedly (DbUpdateException).", result.Run.Error);
        Assert.DoesNotContain("23505", result.Run.Error);
        Assert.DoesNotContain("IX_RawIssueSnapshots", result.Run.Error);
    }

    [Fact]
    public async Task Jira_failure_persists_its_own_safe_message()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _) = Seed(options, tenant);

        var jira = new StubJiraClient(() =>
            Task.FromException<SearchPage>(new JiraApiException(JiraErrorKind.Upstream, "Jira upstream error (500).")));

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), new SyncCancellation(), TestSyncOptions, NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Failed", result.Run!.Status);
        Assert.Equal("Jira upstream error (500).", result.Run.Error); // JiraApiException messages stay intact
    }
}
