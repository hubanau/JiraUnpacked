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
    }

    // These failure tests never reach the transform phase (they throw during extract), so no-op stubs suffice.
    private sealed class NoopIngest : IChangelogIngestService
    {
        public Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopDerivation : IDerivationService
    {
        public Task DeriveProjectAsync(Guid projectId, CancellationToken ct = default) => Task.CompletedTask;
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

    private static DbContextOptions<AppDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
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
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);

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
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);

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
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
            await o1.RunProjectAsync(projectId);
        }

        jira.SeenJql.Clear();
        await using (var db2 = new AppDbContext(options, tenant))
        {
            var o2 = new SyncOrchestrator(db2, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        Assert.Equal(new[] { "(project = \"ACME\") AND updated >= \"2026-03-01 09:30\"" }, jira.SeenJql);
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
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
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
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
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
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
            await o1.RunProjectAsync(projectId);
        }

        jira.SeenJql.Clear();
        await using (var db2 = new AppDbContext(options, tenant))
        {
            var o2 = new SyncOrchestrator(db2, jira, new PassthroughProtector(),
                new FakeClock(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        Assert.Equal(new[] { "(project = ACME) AND updated >= \"2026-03-01 09:30\"" }, jira.SeenJql);
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
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
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
                new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);
            await o2.RunProjectAsync(projectId);
        }

        Assert.All(jira.SeenJql, j => Assert.Contains("updated >= \"2026-03-01 09:30\"", j));
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
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);

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
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Failed", result.Run!.Status);
        Assert.Equal("Sync failed unexpectedly.", result.Run.Error); // no SQL fragments / column names leaked
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
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Failed", result.Run!.Status);
        Assert.Equal("Jira upstream error (500).", result.Run.Error); // JiraApiException messages stay intact
    }
}
