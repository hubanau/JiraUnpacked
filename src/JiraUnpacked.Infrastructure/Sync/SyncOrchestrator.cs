using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;
    private readonly IChangelogIngestService _ingest;
    private readonly IDerivationService _derivation;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock,
        IChangelogIngestService ingest, IDerivationService derivation, ILogger<SyncOrchestrator> logger)
    {
        _db = db; _jira = jira; _protector = protector; _clock = clock;
        _ingest = ingest; _derivation = derivation; _logger = logger;
    }

    /// <summary>Both halves in one call — the scheduled tick, which is already off the request path.</summary>
    public async Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var started = await StartProjectAsync(projectId, ct);
        return started.Outcome == SyncOutcome.Started
            ? await ExecuteProjectAsync(started.Run!.Id, ct)
            : started;
    }

    /// <summary>Both halves in one call — see <see cref="RunProjectAsync"/>.</summary>
    public async Task<SyncRunResult> RunRederiveAsync(Guid projectId, CancellationToken ct = default)
    {
        var started = await StartRederiveAsync(projectId, ct);
        return started.Outcome == SyncOutcome.Started
            ? await ExecuteRederiveAsync(started.Run!.Id, ct)
            : started;
    }

    public Task<SyncRunResult> StartProjectAsync(Guid projectId, CancellationToken ct = default) =>
        StartAsync(projectId, SyncRunKind.Sync, ct);

    public Task<SyncRunResult> StartRederiveAsync(Guid projectId, CancellationToken ct = default) =>
        StartAsync(projectId, SyncRunKind.Rederive, ct);

    /// <summary>
    /// Claims the project's single Running slot. Cheap on purpose: everything here is answerable
    /// in milliseconds, so a caller can reject a bad project or a collision synchronously and
    /// still hand the work off.
    /// </summary>
    private async Task<SyncRunResult> StartAsync(Guid projectId, SyncRunKind kind, CancellationToken ct)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new SyncRunResult(SyncOutcome.ProjectNotFound, null);

        if (await _db.SyncRuns.AnyAsync(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Running, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);

        var run = new SyncRun
        {
            Id = Guid.NewGuid(), JiraProjectId = projectId, Kind = kind,
            StartedAt = _clock.UtcNow, Status = SyncRunStatus.Running, IssuesProcessed = 0
        };
        if (!await TryStartRunAsync(run, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);

        return new SyncRunResult(SyncOutcome.Started, ToView(run));
    }

    /// <summary>
    /// Loads a run that START created and is still Running. Anything else — a missing row, a run
    /// some other worker already finished, a project deleted in between — is not ours to execute,
    /// and must never resurrect a finished run.
    /// </summary>
    private async Task<(SyncRun Run, JiraProject Project)?> ClaimAsync(Guid runId, CancellationToken ct)
    {
        var run = await _db.SyncRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != SyncRunStatus.Running) return null;

        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == run.JiraProjectId, ct);
        return project is null ? null : (run, project);
    }

    public async Task<SyncRunResult> ExecuteProjectAsync(Guid runId, CancellationToken ct = default)
    {
        // Nothing to execute: no such run, one another worker already finished, or a project
        // deleted in between. ProjectNotFound is the "there is no work here" answer.
        var claim = await ClaimAsync(runId, ct);
        if (claim is null) return new SyncRunResult(SyncOutcome.ProjectNotFound, null);
        var (run, project) = claim.Value;

        var projectId = project.Id;
        try
        {
            var watermark = await _db.SyncRuns
                .Where(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Succeeded && r.Kind == SyncRunKind.Sync)
                .MaxAsync(r => (DateTime?)r.StartedAt, ct);

            // An incremental pull is only meaningful when there is a baseline to be incremental
            // FROM. If the project holds no snapshot at all, every "successful" run behind that
            // watermark stored nothing — the state this whole-project default exists to escape —
            // and honouring it would ask Jira only for issues changed since a sync that never
            // happened, leaving the project permanently empty. Start over instead.
            if (watermark is not null &&
                !await _db.RawIssueSnapshots.AnyAsync(r => r.JiraProjectId == projectId, ct))
            {
                _logger.LogWarning(
                    "Sync {ProjectKey} ({ProjectId}): ignoring watermark {Watermark} because the project holds no "
                    + "stored issues — there is nothing to be incremental from. Falling back to a full pull.",
                    project.JiraProjectKey, projectId, watermark.Value.ToString("o"));
                watermark = null;
            }

            var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct)
                       ?? throw new InvalidOperationException("Connection not found for project.");
            var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));

            var queries = await _db.JiraQueries
                .Where(q => q.JiraProjectId == projectId)
                .Select(q => q.Jql)
                .ToListAsync(ct);

            // A project with no query of its own syncs in full. Queries NARROW what is pulled, so an
            // empty list means "nothing to narrow by", not "nothing to sync" — reading it as the
            // latter is what let a freshly added project report Succeeded having never called Jira.
            var wholeProject = queries.Count == 0;
            if (wholeProject) queries.Add(WholeProjectJql(project.JiraProjectKey));

            _logger.LogInformation(
                "Sync starting for {ProjectKey} ({ProjectId}): {QueryCount} query(ies), wholeProjectDefault={WholeProject}, watermark={Watermark}.",
                project.JiraProjectKey, projectId, queries.Count, wholeProject,
                watermark?.ToString("o") ?? "none (full pull)");

            var processed = 0;
            foreach (var query in queries)
            {
                var jql = watermark is null
                    ? query
                    : $"({query}) AND updated >= \"{watermark.Value:yyyy-MM-dd HH:mm}\"";

                var fetched = 0;
                var pages = 0;
                string? token = null;
                do
                {
                    var page = await _jira.SearchIssuesPageAsync(creds, jql, token, ct);
                    fetched += page.Issues.Count;
                    pages++;
                    var pageIds = page.Issues.Select(i => i.JiraIssueId).ToList();
                    var existingByJiraId = await _db.RawIssueSnapshots
                        .Where(x => x.JiraProjectId == projectId && pageIds.Contains(x.JiraIssueId))
                        .ToDictionaryAsync(x => x.JiraIssueId, ct);
                    foreach (var issue in page.Issues)
                    {
                        existingByJiraId.TryGetValue(issue.JiraIssueId, out var existing);
                        if (existing is null)
                        {
                            _db.RawIssueSnapshots.Add(new RawIssueSnapshot
                            {
                                Id = Guid.NewGuid(), JiraProjectId = projectId,
                                JiraIssueId = issue.JiraIssueId, Payload = issue.RawJson, FetchedAt = _clock.UtcNow
                            });
                        }
                        else
                        {
                            existing.Payload = issue.RawJson;
                            existing.FetchedAt = _clock.UtcNow;
                        }
                        processed++;
                    }
                    await _db.SaveChangesAsync(ct);
                    token = page.NextPageToken;
                } while (token is not null);

                // The JQL is logged with its result because "which query returned nothing" is the
                // first question asked when a sync lands no data, and it cannot be reconstructed
                // afterwards — the watermark clause makes it differ from the stored text.
                _logger.LogInformation(
                    "Sync {ProjectKey}: JQL [{Jql}] returned {Fetched} issue(s) over {Pages} page(s).",
                    project.JiraProjectKey, jql, fetched, pages);
            }

            // Transform: ingest the full changelog to raw, then derive the modeled tables (Jira-free).
            await _ingest.IngestProjectAsync(projectId, creds, ct);
            await _derivation.DeriveProjectAsync(projectId, ct);

            // Counted AFTER derivation, because "raw arrived but nothing was modelled" is a second,
            // separate way for this screen to show zeroes, and the two need telling apart.
            var modeled = await _db.Issues.CountAsync(i => i.JiraProjectId == projectId, ct);
            if (processed == 0)
            {
                _logger.LogWarning(
                    "Sync {ProjectKey} ({ProjectId}) fetched NO issues. The JQL above matched nothing in Jira — "
                    + "check the project has issues visible to this connection's account. Modelled issues: {Modeled}.",
                    project.JiraProjectKey, projectId, modeled);
            }
            else
            {
                _logger.LogInformation(
                    "Sync {ProjectKey} ({ProjectId}) fetched {Processed} issue(s); {Modeled} modelled issue(s) in total.",
                    project.JiraProjectKey, projectId, processed, modeled);
            }

            run.Status = SyncRunStatus.Succeeded;
            run.IssuesProcessed = processed;
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            SetFailure(run, ex, projectId);
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }

        return new SyncRunResult(SyncOutcome.Completed, ToView(run));
    }

    public async Task<SyncRunResult> ExecuteRederiveAsync(Guid runId, CancellationToken ct = default)
    {
        // Nothing to execute: no such run, one another worker already finished, or a project
        // deleted in between. ProjectNotFound is the "there is no work here" answer.
        var claim = await ClaimAsync(runId, ct);
        if (claim is null) return new SyncRunResult(SyncOutcome.ProjectNotFound, null);
        var (run, project) = claim.Value;

        var projectId = project.Id;
        try
        {
            await _derivation.DeriveProjectAsync(projectId, ct);
            run.Status = SyncRunStatus.Succeeded; run.FinishedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);

            var modeled = await _db.Issues.CountAsync(i => i.JiraProjectId == projectId, ct);
            _logger.LogInformation(
                "Rederive {ProjectKey} ({ProjectId}) finished: {Modeled} modelled issue(s).",
                project.JiraProjectKey, projectId, modeled);
        }
        catch (Exception ex)
        {
            SetFailure(run, ex, projectId);
            await _db.SaveChangesAsync(ct);
        }
        return new SyncRunResult(SyncOutcome.Completed, ToView(run));
    }

    /// <summary>
    /// The default scope for a project that has no query of its own: everything in it.
    /// The key is quoted and escaped even though Jira restricts project keys to [A-Z][A-Z0-9_]* —
    /// the value is being pasted into JQL text, and quoting costs nothing next to trusting a
    /// format rule enforced somewhere else.
    /// </summary>
    private static string WholeProjectJql(string projectKey) =>
        $"project = \"{projectKey.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    // Sets the run's failure fields, keeping raw infrastructure detail out of the tenant-readable
    // Error. JiraApiException messages are curated + token-free; anything else is logged server-side
    // and replaced with a generic message.
    private void SetFailure(SyncRun run, Exception ex, Guid projectId)
    {
        run.Status = SyncRunStatus.Failed;
        run.FinishedAt = _clock.UtcNow;
        if (ex is JiraApiException)
        {
            run.Error = ex.Message;
        }
        else
        {
            _logger.LogError(ex, "Unexpected failure syncing project {ProjectId}", projectId);
            run.Error = "Sync failed unexpectedly.";
        }
    }

    // Inserts the Running SyncRun, translating the partial-unique-index violation (lost race)
    // into AlreadyRunning. Only THIS constraint is swallowed; any other error propagates.
    private async Task<bool> TryStartRunAsync(SyncRun run, CancellationToken ct)
    {
        _db.SyncRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == "23505"
            && pg.ConstraintName == "IX_SyncRuns_OneRunningPerProject")
        {
            return false;
        }
    }

    private static SyncRunView ToView(SyncRun r) =>
        new(r.Id, r.JiraProjectId, r.Status.ToString(), r.StartedAt, r.FinishedAt, r.IssuesProcessed, r.Error, r.NextRunAt, r.Kind.ToString());
}
