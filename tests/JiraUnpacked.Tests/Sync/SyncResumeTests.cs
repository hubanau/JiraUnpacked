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
        /// <summary>Which issues the changelog endpoint was asked about. A resumed ingest proving
        /// it skipped work means proving these issues are NOT in here.</summary>
        public List<string> ChangelogRequests { get; } = new();

        public Task<ChangelogPage> GetIssueChangelogPageAsync(
            JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default)
        {
            ChangelogRequests.Add(jiraIssueId);
            return Task.FromResult(new ChangelogPage(
                new[] { "{\"id\":\"1\",\"created\":\"2026-01-01T00:00:00.000+0000\",\"items\":[]}" },
                Total: 1, IsLast: true));
        }
        /// <summary>The account's zone as Jira reports it. UTC keeps the watermark clause readable.</summary>
        public string? TimeZone { get; set; } = "UTC";
        public Task<string?> GetUserTimeZoneAsync(JiraCredentials creds, CancellationToken ct = default) => Task.FromResult(TimeZone);

        /// <summary>Unanswered by default: reconciliation then skips, which is its documented fallback.</summary>
        public Func<string, IssueIdPage>? IdListing { get; set; }
        public Task<IssueIdPage> SearchIssueIdsPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
            => IdListing is null ? throw new NotSupportedException() : Task.FromResult(IdListing(jql));
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
            Guid? afterSnapshotId = null, int alreadyDone = 0,
            Func<int, int, Guid, CancellationToken, Task>? onBatch = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class NoopDerivation : IDerivationService
    {
        public Task DeriveProjectAsync(
            Guid projectId, string? resumeStage = null, string? resumeCursor = null,
            int alreadyDone = 0, int batchSize = 10_000,
            Func<string, string, int, int, CancellationToken, Task>? onBatch = null,
            DateTime? incrementalSince = null,
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
            // Ids depend on the POSITION only, never on the query text. Real Jira issue ids are
            // stable, so an incremental re-pull returns the same ids and upserts over them; ids
            // derived from the jql string would fake duplicates the moment a watermark clause
            // changed that string, and the test would be measuring its own stub.
            var issues = Enumerable.Range(0, perPage)
                .Select(i => new RawIssue($"ISSUE-{index * perPage + i}", "{}"))
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

    /// <summary>
    /// A payload declaring more history than it carries inline, which is what forces the ingest
    /// to call Jira's dedicated changelog endpoint — the expensive part this stage checkpoints.
    /// </summary>
    private const string TruncatedChangelogPayload = "{\"changelog\":{\"total\":5,\"histories\":[]}}";

    private static async Task<List<Guid>> SeedSnapshotsAsync(
        DbContextOptions<AppDbContext> options, ICurrentTenant tenant, Guid projectId, int count)
    {
        await using var db = new AppDbContext(options, tenant);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.RawIssueSnapshots.Add(new RawIssueSnapshot
            {
                Id = id, JiraProjectId = projectId, JiraIssueId = $"J{i}",
                Payload = TruncatedChangelogPayload, FetchedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task A_resumed_changelog_ingest_skips_the_issues_it_already_wrote()
    {
        // Every issue here costs a Jira round trip, so redoing a completed batch is the single
        // most expensive kind of wasted work in the whole sync.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var ids = await SeedSnapshotsAsync(options, tenant, projectId, count: 600);

        var jira = new RecordingJiraClient((_, _) => new SearchPage(Array.Empty<RawIssue>(), null));

        await using var db = new AppDbContext(options, tenant);
        var ingest = new ChangelogIngestService(db, jira, new FakeClock(DateTime.UtcNow));

        // Ordered by Id, because that is the order the cursor walks.
        var ordered = ids.OrderBy(x => x).ToList();
        var alreadyDone = ordered.Take(250).ToList();

        await ingest.IngestProjectAsync(
            projectId, new JiraCredentials("https://acme.atlassian.net", "ops@acme.com", "t"),
            afterSnapshotId: ordered[249], alreadyDone: 250,
            onBatch: null, ct: default);

        // The issues in the completed batch are never asked about again.
        await using var read = new AppDbContext(options, tenant);
        var skipped = await read.RawIssueSnapshots
            .Where(r => alreadyDone.Contains(r.Id)).Select(r => r.JiraIssueId).ToListAsync();

        Assert.All(skipped, jid => Assert.DoesNotContain(jid, jira.ChangelogRequests));
        Assert.Equal(350, await read.RawChangelogs.CountAsync(r => r.JiraProjectId == projectId));
    }

    /// <summary>A minimal but real issue payload, so derivation has something to model.</summary>
    private static string IssuePayload(int i) =>
        "{\"key\":\"ACME-" + i + "\",\"fields\":{\"summary\":\"s" + i + "\"," +
        "\"created\":\"2026-01-01T00:00:00.000+0000\",\"updated\":\"2026-01-02T00:00:00.000+0000\"}}";

    private static async Task<List<Guid>> SeedIssueSnapshotsAsync(
        DbContextOptions<AppDbContext> options, ICurrentTenant tenant, Guid projectId, int count)
    {
        await using var db = new AppDbContext(options, tenant);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.RawIssueSnapshots.Add(new RawIssueSnapshot
            {
                Id = id, JiraProjectId = projectId, JiraIssueId = $"J{i}",
                Payload = IssuePayload(i), FetchedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task Derivation_never_holds_more_than_one_batch_in_the_change_tracker()
    {
        // The code this replaces loaded every snapshot payload, every changelog payload and every
        // change-history key at once -- 1,477,699 of those keys on the real project. A stage that
        // needs the whole project resident is a stage that cannot survive a laptop, and every long
        // run so far has died inside it.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        await SeedIssueSnapshotsAsync(options, tenant, projectId, count: 20_000);

        // Changelogs too, so the assertion covers the history pass and not only the issue pass.
        // Both loaded their whole table before; only measuring one would leave the other free to
        // regress unnoticed.
        await SeedChangelogsAsync(options, tenant, projectId, count: 5_000, historiesPerIssue: 2);

        await using var db = new AppDbContext(options, tenant);
        var derivation = new DerivationService(db, new FakeClock(DateTime.UtcNow));
        var peak = 0;

        await derivation.DeriveProjectAsync(projectId, null, null, 0, batchSize: 500,
            onBatch: async (stage, cursor, done, total, ct) =>
            {
                await db.SaveChangesAsync(ct);
                peak = Math.Max(peak, db.ChangeTracker.Entries().Count());
            });

        Assert.True(peak < 2_000, $"change tracker peaked at {peak} entries against a batch of 500");
        await using var read = new AppDbContext(options, tenant);
        Assert.Equal(20_000, await read.Issues.CountAsync(i => i.JiraProjectId == projectId));
    }

    [Fact]
    public async Task A_resumed_derivation_starts_after_the_last_committed_snapshot()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var ids = (await SeedIssueSnapshotsAsync(options, tenant, projectId, count: 300))
            .OrderBy(x => x).ToList();

        await using var db = new AppDbContext(options, tenant);
        var derivation = new DerivationService(db, new FakeClock(DateTime.UtcNow));

        await derivation.DeriveProjectAsync(projectId,
            ResumeStage.DeriveIssues, ids[99].ToString(), alreadyDone: 100, batchSize: 100,
            onBatch: async (stage, cursor, done, total, ct) => await db.SaveChangesAsync(ct));

        // Only the remaining 200 were modelled; the first 100 were left to the attempt that did them.
        await using var read = new AppDbContext(options, tenant);
        Assert.Equal(200, await read.Issues.CountAsync(i => i.JiraProjectId == projectId));
    }

    private static async Task SeedChangelogsAsync(
        DbContextOptions<AppDbContext> options, ICurrentTenant tenant, Guid projectId,
        int count, int historiesPerIssue)
    {
        await using var db = new AppDbContext(options, tenant);
        for (var i = 0; i < count; i++)
        {
            var histories = string.Join(",", Enumerable.Range(0, historiesPerIssue).Select(h =>
                "{\"id\":\"" + h + "\",\"created\":\"2026-01-01T00:00:00.000+0000\"," +
                "\"items\":[{\"field\":\"status\",\"fromString\":\"A\",\"toString\":\"B\"}]}"));
            db.RawChangelogs.Add(new RawChangelog
            {
                Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = $"J{i}",
                Payload = "{\"histories\":[" + histories + "]}", FetchedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task History_dedup_is_scoped_per_batch_and_stays_idempotent()
    {
        // The dedup set used to hold every (issue, historyId, field) key in the project — 1,477,699
        // of them on the real one — purely to answer "have I seen this". Scoped per batch it must
        // still answer identically, which means running the whole stage twice adds nothing.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        await SeedChangelogsAsync(options, tenant, projectId, count: 1_000, historiesPerIssue: 5);

        await using var db = new AppDbContext(options, tenant);
        var derivation = new DerivationService(db, new FakeClock(DateTime.UtcNow));

        await derivation.DeriveProjectAsync(projectId, batchSize: 100);
        await using (var read1 = new AppDbContext(options, tenant))
            Assert.Equal(5_000, await read1.IssueChangeHistory.CountAsync(h => h.JiraProjectId == projectId));

        await derivation.DeriveProjectAsync(projectId, batchSize: 100);

        await using var read2 = new AppDbContext(options, tenant);
        Assert.Equal(5_000, await read2.IssueChangeHistory.CountAsync(h => h.JiraProjectId == projectId));
    }

    [Fact]
    public async Task A_rederive_resumes_a_rederive_and_not_a_sync()
    {
        // Kind-matched on purpose: predictable beats clever. A Sync that died in derivation is
        // continued by the next Sync, not by pressing Re-derive — and derivation is the fast
        // stage, so the cost of that choice is small.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var ids = (await SeedIssueSnapshotsAsync(options, tenant, projectId, count: 100))
            .OrderBy(x => x).ToList();

        var jira = new RecordingJiraClient((_, _) => new SearchPage(Array.Empty<RawIssue>(), null));

        // A SYNC checkpoint half way through derivation. A rederive must ignore it.
        await using (var db = new AppDbContext(options, tenant))
        {
            db.SyncRuns.Add(new SyncRun
            {
                Id = Guid.NewGuid(), JiraProjectId = projectId, StartedAt = DateTime.UtcNow.AddHours(-1),
                Status = SyncRunStatus.Failed, Kind = SyncRunKind.Sync, IssuesProcessed = 50,
                ResumeStage = ResumeStage.DeriveIssues, ResumeCursor = ids[49].ToString(), ResumeItemsDone = 50
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, tenant))
        {
            var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
                new FakeClock(DateTime.UtcNow), new NoopIngest(),
                new DerivationService(db, new FakeClock(DateTime.UtcNow)),
                new SyncCancellation(), Options(100), NullLogger<SyncOrchestrator>.Instance);
            await orchestrator.RunRederiveAsync(projectId);
        }

        await using var read = new AppDbContext(options, tenant);
        Assert.Equal(100, await read.Issues.CountAsync(i => i.JiraProjectId == projectId));
    }

    [Fact]
    public async Task A_run_with_no_checkpoint_columns_behaves_exactly_as_before()
    {
        // Every run written before this feature has nulls here. They must simply run in full.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var projectId = SeedProject(options, tenant, "project = A");
        var jira = new RecordingJiraClient((_, _) =>
            new SearchPage(new[] { new RawIssue("10001", "{}") }, null));

        await using (var db = new AppDbContext(options, tenant))
        {
            db.SyncRuns.Add(new SyncRun
            {
                Id = Guid.NewGuid(), JiraProjectId = projectId, StartedAt = DateTime.UtcNow.AddHours(-2),
                Status = SyncRunStatus.Failed, Kind = SyncRunKind.Sync, IssuesProcessed = 7,
                ScopeFingerprint = null, ResumeStage = null, ResumeCursor = null, ResumeItemsDone = null
            });
            await db.SaveChangesAsync();
        }

        await using var db2 = new AppDbContext(options, tenant);
        var result = await NewOrchestrator(db2, jira).RunProjectAsync(projectId);

        Assert.Equal("Succeeded", result.Run!.Status);
        Assert.Contains(jira.Requests, r => r.Token is null);   // started from the beginning
    }

    [Fact]
    public async Task An_interrupted_sync_ends_with_the_same_data_as_an_uninterrupted_one()
    {
        // The end-to-end proof: resumption has to be invisible in the result.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());

        async Task<(int Issues, int Snapshots)> ReadAsync(DbContextOptions<AppDbContext> o, Guid pid)
        {
            await using var read = new AppDbContext(o, tenant);
            return (await read.Issues.CountAsync(i => i.JiraProjectId == pid),
                    await read.RawIssueSnapshots.CountAsync(r => r.JiraProjectId == pid));
        }

        var cleanOptions = InMemoryOptions();
        var cleanProject = SeedProject(cleanOptions, tenant, "project = A");
        var cleanJira = new RecordingJiraClient(Pages(pageCount: 6, perPage: 20));
        await using (var db = new AppDbContext(cleanOptions, tenant))
            await new SyncOrchestrator(db, cleanJira, new PassthroughProtector(),
                new FakeClock(DateTime.UtcNow), new NoopIngest(),
                new DerivationService(db, new FakeClock(DateTime.UtcNow)),
                new SyncCancellation(), Options(20), NullLogger<SyncOrchestrator>.Instance)
                .RunProjectAsync(cleanProject);
        var clean = await ReadAsync(cleanOptions, cleanProject);

        var brokenOptions = InMemoryOptions();
        var brokenProject = SeedProject(brokenOptions, tenant, "project = A");
        var brokenJira = new RecordingJiraClient(Pages(pageCount: 6, perPage: 20)) { ThrowAfterRequests = 2 };

        // Three attempts, each dying part way, then one that is allowed to finish.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = new AppDbContext(brokenOptions, tenant);
            await new SyncOrchestrator(db, brokenJira, new PassthroughProtector(),
                new FakeClock(DateTime.UtcNow), new NoopIngest(),
                new DerivationService(db, new FakeClock(DateTime.UtcNow)),
                new SyncCancellation(), Options(20), NullLogger<SyncOrchestrator>.Instance)
                .RunProjectAsync(brokenProject);
            brokenJira.ThrowAfterRequests = brokenJira.Requests.Count + 2;
        }

        brokenJira.ThrowAfterRequests = null;
        await using (var db = new AppDbContext(brokenOptions, tenant))
            await new SyncOrchestrator(db, brokenJira, new PassthroughProtector(),
                new FakeClock(DateTime.UtcNow), new NoopIngest(),
                new DerivationService(db, new FakeClock(DateTime.UtcNow)),
                new SyncCancellation(), Options(20), NullLogger<SyncOrchestrator>.Instance)
                .RunProjectAsync(brokenProject);

        Assert.Equal(clean, await ReadAsync(brokenOptions, brokenProject));
    }
}
