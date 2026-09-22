using System.Net;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Jira;
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
/// Regressions from the 2026-09-22 audit. Each of the first three reproduced a live defect before
/// it was fixed: a checkpoint resumed forever, a Stop that left the run Running, and a rederive
/// that could not be stopped at all.
/// </summary>
public class SyncCorrectnessTests
{
    private static readonly InMemoryDatabaseRoot Root = new();
    private static readonly DateTime T0 = new(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);

    private static DbContextOptions<AppDbContext> Mem() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString(), Root).Options;

    private static Guid Seed(DbContextOptions<AppDbContext> o, ICurrentTenant t, params string[] jql)
    {
        var projectId = Guid.NewGuid();
        var connId = Guid.NewGuid();
        using var db = new AppDbContext(o, t);
        db.JiraConnections.Add(new JiraConnection { Id = connId, BaseUrl = "https://acme.atlassian.net", Email = "a@b.c", ApiTokenEncrypted = "enc" });
        db.JiraProjects.Add(new JiraProject { Id = projectId, JiraConnectionId = connId, JiraProjectKey = "ACME", JiraProjectId = "1", SyncIntervalMinutes = 60 });
        foreach (var q in jql) db.JiraQueries.Add(new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = q });
        db.SaveChanges();
        return projectId;
    }

    private static SyncOrchestrator Orch(
        AppDbContext db, IJiraApiClient jira, IClock? clock = null,
        ISyncCancellation? cancellation = null, IDerivationService? derivation = null) =>
        new(db, jira, new SyncResumeTests.PassthroughProtector(), clock ?? new FakeClock(DateTime.UtcNow),
            new SyncResumeTests.NoopIngest(), derivation ?? new SyncResumeTests.NoopDerivation(),
            cancellation ?? new SyncCancellation(),
            Options.Create(new SyncSchedulerOptions()), NullLogger<SyncOrchestrator>.Instance);

    private static SearchPage Issues(params string[] ids) =>
        new(ids.Select(id => new RawIssue(id, "{}")).ToArray(), null);

    private static async Task MarkLatestFailedWithCheckpointAsync(
        DbContextOptions<AppDbContext> o, ICurrentTenant t, string stage, SyncRunKind kind = SyncRunKind.Sync)
    {
        await using var db = new AppDbContext(o, t);
        var run = await db.SyncRuns.OrderByDescending(r => r.StartedAt).FirstAsync();
        run.Status = SyncRunStatus.Failed;
        run.Kind = kind;
        run.ResumeStage = stage;
        run.ResumeCursor = Guid.NewGuid().ToString();
        run.ResumeItemsDone = 1;
        await db.SaveChangesAsync();
    }

    // ---- resume ------------------------------------------------------------------------------

    [Fact]
    public async Task A_checkpoint_a_successful_run_already_resumed_is_not_resumed_again()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues("1"));
        var clock = new FakeClock(T0);

        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);
        await MarkLatestFailedWithCheckpointAsync(o, tenant, ResumeStage.Changelog);

        clock.UtcNow = T0.AddHours(1);
        await using (var db = new AppDbContext(o, tenant))
            Assert.Equal("Succeeded", (await Orch(db, jira, clock).RunProjectAsync(projectId)).Run!.Status);

        // Before the fix this run resumed the same checkpoint again, skipped the fetch and never
        // asked Jira for anything - on every later sync, until retention purged the old row.
        jira.Requests.Clear();
        clock.UtcNow = T0.AddHours(2);
        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);
        Assert.NotEmpty(jira.Requests);
    }

    [Fact]
    public async Task A_rederive_does_not_resume_a_checkpoint_older_than_its_last_success()
    {
        // The live case: a failed rederive left derive:issues behind, a later rederive succeeded,
        // and the next one would have skipped every issue before the stale cursor.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues("1"));
        var derivation = new RecordingDerivation();
        var clock = new FakeClock(T0);
        await using (var db = new AppDbContext(o, tenant))
        {
            // A post-fetch checkpoint indexes raw rows; with none it is discarded by design.
            db.RawIssueSnapshots.Add(new RawIssueSnapshot { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "1", Payload = "{}", FetchedAt = T0 });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock, derivation: derivation).RunRederiveAsync(projectId);
        await MarkLatestFailedWithCheckpointAsync(o, tenant, ResumeStage.DeriveIssues, SyncRunKind.Rederive);

        clock.UtcNow = T0.AddHours(1);
        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock, derivation: derivation).RunRederiveAsync(projectId);
        Assert.Equal(ResumeStage.DeriveIssues, derivation.Calls[^1].Stage);   // legitimately resumed once

        clock.UtcNow = T0.AddHours(2);
        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock, derivation: derivation).RunRederiveAsync(projectId);
        Assert.Null(derivation.Calls[^1].Stage);                              // then starts clean
        Assert.Null(derivation.Calls[^1].Since);                              // and in full
    }

    [Fact]
    public async Task A_resumed_run_inherits_the_data_instant_of_the_attempt_that_fetched()
    {
        // A resumed run did not fetch what it inherited. Its own start time as the next watermark
        // skipped every issue updated between the failed attempt and the resume.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues("1"));
        var clock = new FakeClock(T0);

        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);
        await MarkLatestFailedWithCheckpointAsync(o, tenant, ResumeStage.Changelog);

        clock.UtcNow = T0.AddHours(2);
        SyncRunView resumed;
        await using (var db = new AppDbContext(o, tenant)) resumed = (await Orch(db, jira, clock).RunProjectAsync(projectId)).Run!;
        await using (var db = new AppDbContext(o, tenant))
            Assert.Equal(T0, (await db.SyncRuns.SingleAsync(r => r.Id == resumed.Id)).FetchedAsOf);

        jira.Requests.Clear();
        clock.UtcNow = T0.AddHours(3);
        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);
        Assert.Contains(jira.SeenJql, j => j.Contains("updated >= \"2026-03-01 09:25\""));
    }

    // ---- stop / shutdown ---------------------------------------------------------------------

    private sealed class HangingHandler : HttpMessageHandler
    {
        public Action? OnSearch;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/search/jql"))
            {
                OnSearch?.Invoke();                       // Stop is pressed mid-request
                await Task.Delay(Timeout.Infinite, ct);   // a slow page
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"count\":1}") };
        }
    }

    [Fact]
    public async Task Stop_pressed_while_a_Jira_request_is_in_flight_records_Cancelled()
    {
        // Through the REAL client: its retry helper turned the cancelled request into
        // "Jira is unreachable", the failure path saved through the cancelled token, threw, and
        // the run stayed Running until the reaper, blocking the project for two hours.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var cancellation = new SyncCancellation();
        var handler = new HangingHandler();
        var jira = new JiraApiClient(new HttpClient(handler));

        Guid runId;
        await using (var db = new AppDbContext(o, tenant))
            runId = (await Orch(db, jira, cancellation: cancellation).StartProjectAsync(projectId)).Run!.Id;
        handler.OnSearch = () => cancellation.Cancel(runId);

        await using (var db = new AppDbContext(o, tenant))
            await Orch(db, jira, cancellation: cancellation).ExecuteProjectAsync(runId);

        await using var read = new AppDbContext(o, tenant);
        var run = await read.SyncRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal(SyncRunStatus.Cancelled, run.Status);
        Assert.Equal("Stopped on request.", run.Error);
    }

    /// <summary>Hands the test a hook between batches; records each call's resume arguments.</summary>
    private sealed class RecordingDerivation : IDerivationService
    {
        public List<(string? Stage, DateTime? Since)> Calls { get; } = new();
        public Func<Func<string, string, int, int, CancellationToken, Task>, CancellationToken, Task>? Body;

        public async Task DeriveProjectAsync(Guid projectId, string? resumeStage = null, string? resumeCursor = null,
            int alreadyDone = 0, int batchSize = 10_000,
            Func<string, string, int, int, CancellationToken, Task>? onBatch = null,
            DateTime? incrementalSince = null, CancellationToken ct = default)
        {
            Calls.Add((resumeStage, incrementalSince));
            if (Body is not null) await Body(onBatch!, ct);
        }
    }

    [Fact]
    public async Task Stopping_a_rederive_records_Cancelled()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var cancellation = new SyncCancellation();
        var derivation = new RecordingDerivation();
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues());

        Guid runId;
        await using (var db = new AppDbContext(o, tenant))
            runId = (await Orch(db, jira, cancellation: cancellation, derivation: derivation).StartRederiveAsync(projectId)).Run!.Id;
        derivation.Body = async (onBatch, ct) =>
        {
            cancellation.Cancel(runId);
            await onBatch(ResumeStage.DeriveIssues, Guid.NewGuid().ToString(), 1, 2, ct);
        };

        await using (var db = new AppDbContext(o, tenant))
            await Orch(db, jira, cancellation: cancellation, derivation: derivation).ExecuteRederiveAsync(runId);

        await using var read = new AppDbContext(o, tenant);
        var run = await read.SyncRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal(SyncRunStatus.Cancelled, run.Status);
    }

    [Fact]
    public async Task A_shutdown_mid_run_is_recorded_as_interrupted_keeping_its_checkpoint()
    {
        // It used to stay Running for the reaper - hours - before the next process could resume.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var host = new CancellationTokenSource();
        var committedCursor = Guid.NewGuid().ToString();
        var derivation = new RecordingDerivation
        {
            Body = async (onBatch, ct) =>
            {
                await onBatch(ResumeStage.DeriveIssues, committedCursor, 1, 3, ct);   // commits
                host.Cancel();                                                       // host stops
                await onBatch(ResumeStage.DeriveIssues, Guid.NewGuid().ToString(), 2, 3, ct);
            }
        };
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues());
        var clock = new FakeClock(T0);

        Guid runId;
        await using (var db = new AppDbContext(o, tenant))
            runId = (await Orch(db, jira, clock, derivation: derivation).StartRederiveAsync(projectId)).Run!.Id;
        await using (var db = new AppDbContext(o, tenant))
            await Orch(db, jira, clock, derivation: derivation).ExecuteRederiveAsync(runId, host.Token);

        await using var read = new AppDbContext(o, tenant);
        var run = await read.SyncRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.StartsWith("Interrupted", run.Error);
        Assert.Equal(committedCursor, run.ResumeCursor);   // the committed position, not the lost one
        Assert.Equal(T0, run.NextRunAt);                    // due at once
    }

    // ---- scheduling --------------------------------------------------------------------------

    [Fact]
    public async Task Consecutive_failures_back_off()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) =>
            throw new JiraApiException(JiraErrorKind.Upstream, "Jira is unreachable."));
        var clock = new FakeClock(T0);

        await using (var db = new AppDbContext(o, tenant))
            Assert.Equal(T0.AddMinutes(5), (await Orch(db, jira, clock).RunProjectAsync(projectId)).Run!.NextRunAt);

        clock.UtcNow = T0.AddMinutes(6);
        await using (var db = new AppDbContext(o, tenant))
            Assert.Equal(T0.AddMinutes(16), (await Orch(db, jira, clock).RunProjectAsync(projectId)).Run!.NextRunAt);
    }

    [Theory]
    [InlineData(1, 60, 5)]
    [InlineData(2, 60, 10)]
    [InlineData(3, 60, 20)]
    [InlineData(4, 60, 40)]
    [InlineData(5, 60, 60)]    // capped by the interval
    [InlineData(9, 240, 240)]
    public void Backoff_doubles_and_is_capped_by_the_interval(int failures, int interval, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), SyncOrchestrator.Backoff(failures, interval));

    [Fact]
    public void Due_rules()
    {
        var now = T0;
        Assert.True(DueProjectFinder.IsDue(null, null, null, 60, now));                                            // never synced
        Assert.False(DueProjectFinder.IsDue(SyncRunStatus.Succeeded, null, now.AddMinutes(-30), 60, now));
        Assert.True(DueProjectFinder.IsDue(SyncRunStatus.Succeeded, null, now.AddMinutes(-60), 60, now));
        Assert.False(DueProjectFinder.IsDue(SyncRunStatus.Failed, now.AddMinutes(1), null, 60, now));               // backing off
        Assert.True(DueProjectFinder.IsDue(SyncRunStatus.Failed, null, null, 60, now));                             // reaped
        Assert.False(DueProjectFinder.IsDue(SyncRunStatus.Cancelled, now.AddMinutes(59), now.AddDays(-1), 60, now)); // stopped
    }

    [Fact]
    public async Task A_run_whose_owner_was_blocked_does_not_start()
    {
        var userId = Guid.NewGuid();
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(userId);
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        await using (var db = new AppDbContext(o, tenant))
        {
            db.Users.Add(new User
            {
                Id = userId, Email = "b@x.io", Username = "blocked", PasswordHash = "x",
                Role = UserRole.EndUser, Status = UserStatus.Blocked, CreatedAt = T0
            });
            await db.SaveChangesAsync();
        }
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues("1"));

        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira).RunProjectAsync(projectId);

        Assert.Empty(jira.Requests);
        await using var read = new AppDbContext(o, tenant);
        Assert.Equal(SyncRunStatus.Cancelled, (await read.SyncRuns.SingleAsync()).Status);
    }

    [Fact]
    public void Cancelling_a_tenant_stops_only_that_tenants_runs()
    {
        var registry = new SyncCancellation();
        var alice = Guid.NewGuid();
        using var a1 = registry.Register(Guid.NewGuid(), alice, CancellationToken.None, out var t1);
        using var a2 = registry.Register(Guid.NewGuid(), alice, CancellationToken.None, out var t2);
        using var b1 = registry.Register(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None, out var t3);

        Assert.Equal(2, registry.CancelAllFor(alice));
        Assert.True(t1.IsCancellationRequested && t2.IsCancellationRequested);
        Assert.False(t3.IsCancellationRequested);
    }

    // ---- watermark literal -------------------------------------------------------------------

    [Theory]
    [InlineData("2026-03-01T15:00:00Z", "UTC", "2026-03-01 14:55")]
    [InlineData("2026-03-01T15:00:00Z", "America/New_York", "2026-03-01 09:55")]   // UTC-5: was five hours late
    [InlineData("2026-03-01T15:00:00Z", "Europe/Warsaw", "2026-03-01 15:55")]
    [InlineData("2026-03-01T15:00:00Z", null, "2026-03-01 00:55")]                 // unknown: widest margin
    [InlineData("2026-03-01T15:00:00Z", "Not/AZone", "2026-03-01 00:55")]
    [InlineData("2026-11-01T05:30:00Z", "America/New_York", "2026-11-01 00:25")]   // 01:25 happens twice
    public void The_watermark_is_written_in_the_Jira_accounts_zone(string watermark, string? zone, string expected) =>
        Assert.Equal(expected, SyncOrchestrator.JqlSince(
            DateTime.Parse(watermark, null, System.Globalization.DateTimeStyles.AdjustToUniversal), zone));

    // ---- scope reconciliation ----------------------------------------------------------------

    [Fact]
    public async Task A_full_pull_removes_issues_Jira_no_longer_returns()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        string[] returned = { "1", "2" };
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues(returned));
        var clock = new FakeClock(T0);

        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);

        // A changed query is a new scope, so the next run is a full pull.
        await using (var db = new AppDbContext(o, tenant))
        {
            (await db.JiraQueries.SingleAsync()).Jql = "project = A AND type = Story";
            await db.SaveChangesAsync();
        }
        returned = new[] { "1" };
        clock.UtcNow = T0.AddHours(1);
        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);

        await using var read = new AppDbContext(o, tenant);
        Assert.Equal(new[] { "1" }, await read.RawIssueSnapshots.Select(r => r.JiraIssueId).ToListAsync());
    }

    [Fact]
    public async Task An_incremental_sync_removes_issues_missing_from_the_scope_listing()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        string[] returned = { "1", "2", "3" };
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues(returned));
        var clock = new FakeClock(T0);

        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);

        returned = Array.Empty<string>();                              // nothing updated since
        jira.IdListing = _ => new IssueIdPage(new[] { "1", "3" }, null);  // "2" was deleted in Jira
        clock.UtcNow = T0.AddHours(1);
        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);

        await using var read = new AppDbContext(o, tenant);
        Assert.Equal(new[] { "1", "3" }, (await read.RawIssueSnapshots.Select(r => r.JiraIssueId).ToListAsync()).Order());
    }

    [Fact]
    public async Task An_incremental_reconcile_refuses_to_remove_an_implausible_share()
    {
        // A listing missing most of the project is likelier a permissions glitch than a purge.
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant, "project = A");
        var all = Enumerable.Range(1, 100).Select(i => i.ToString()).ToArray();
        var returned = all;
        var jira = new SyncResumeTests.RecordingJiraClient((_, _) => Issues(returned));
        var clock = new FakeClock(T0);

        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);

        returned = Array.Empty<string>();
        jira.IdListing = _ => new IssueIdPage(all.Take(20).ToArray(), null);
        clock.UtcNow = T0.AddHours(1);
        await using (var db = new AppDbContext(o, tenant)) await Orch(db, jira, clock).RunProjectAsync(projectId);

        await using var read = new AppDbContext(o, tenant);
        Assert.Equal(100, await read.RawIssueSnapshots.CountAsync());
    }

    // ---- incremental transform ---------------------------------------------------------------

    private static string Payload(string key, string summary) =>
        $"{{\"key\":\"{key}\",\"fields\":{{\"summary\":\"{summary}\",\"status\":{{\"id\":\"1\",\"name\":\"Open\"}}}}}}";

    [Fact]
    public async Task An_incremental_derive_rebuilds_only_issues_fetched_since_they_were_modelled()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant);
        var clock = new FakeClock(T0);

        await using (var db = new AppDbContext(o, tenant))
        {
            db.RawIssueSnapshots.Add(new RawIssueSnapshot { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "1", Payload = Payload("A-1", "one"), FetchedAt = T0 });
            db.RawIssueSnapshots.Add(new RawIssueSnapshot { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "2", Payload = Payload("A-2", "two"), FetchedAt = T0 });
            await db.SaveChangesAsync();
        }

        clock.UtcNow = T0.AddMinutes(1);
        await using (var db = new AppDbContext(o, tenant))
            await new DerivationService(db, clock).DeriveProjectAsync(projectId);   // full

        // Issue 1 is fetched again, changed; issue 2 is untouched.
        await using (var db = new AppDbContext(o, tenant))
        {
            var snap = await db.RawIssueSnapshots.SingleAsync(r => r.JiraIssueId == "1");
            snap.Payload = Payload("A-1", "one, edited");
            snap.FetchedAt = T0.AddMinutes(2);
            await db.SaveChangesAsync();
        }

        clock.UtcNow = T0.AddMinutes(3);
        await using (var db = new AppDbContext(o, tenant))
            await new DerivationService(db, clock).DeriveProjectAsync(projectId, incrementalSince: T0.AddMinutes(2));

        await using (var read = new AppDbContext(o, tenant))
        {
            var one = await read.Issues.SingleAsync(i => i.JiraIssueId == "1");
            var two = await read.Issues.SingleAsync(i => i.JiraIssueId == "2");
            Assert.Equal("one, edited", one.Summary);
            Assert.Equal(T0.AddMinutes(3), one.LastSyncedAt);
            Assert.Equal(T0.AddMinutes(1), two.LastSyncedAt);   // not re-parsed
        }

        // A rederive is still a rebuild of everything.
        clock.UtcNow = T0.AddMinutes(4);
        await using (var db = new AppDbContext(o, tenant))
            await new DerivationService(db, clock).DeriveProjectAsync(projectId, incrementalSince: null);
        await using (var read = new AppDbContext(o, tenant))
            Assert.All(await read.Issues.ToListAsync(), i => Assert.Equal(T0.AddMinutes(4), i.LastSyncedAt));
    }

    [Fact]
    public async Task Changelog_ingest_skips_snapshots_whose_changelog_is_already_newer()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var o = Mem();
        var projectId = Seed(o, tenant);

        await using (var db = new AppDbContext(o, tenant))
        {
            db.RawIssueSnapshots.Add(new RawIssueSnapshot { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "done", Payload = "{}", FetchedAt = T0 });
            db.RawChangelogs.Add(new RawChangelog { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "done", Payload = "{}", FetchedAt = T0.AddMinutes(1) });
            db.RawIssueSnapshots.Add(new RawIssueSnapshot { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "refetched", Payload = "{}", FetchedAt = T0.AddMinutes(5) });
            db.RawChangelogs.Add(new RawChangelog { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "refetched", Payload = "{}", FetchedAt = T0.AddMinutes(1) });
            db.RawIssueSnapshots.Add(new RawIssueSnapshot { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = "new", Payload = "{}", FetchedAt = T0 });
            await db.SaveChangesAsync();
        }

        await using var read = new AppDbContext(o, tenant);
        var pending = await ChangelogIngestService.Pending(read, projectId).Select(r => r.JiraIssueId).ToListAsync();
        Assert.Equal(new[] { "new", "refetched" }, pending.Order());
    }
}
