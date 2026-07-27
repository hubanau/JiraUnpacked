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

    public async Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new SyncRunResult(SyncOutcome.ProjectNotFound, null);

        if (await _db.SyncRuns.AnyAsync(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Running, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);

        var startedAt = _clock.UtcNow;
        var run = new SyncRun
        {
            Id = Guid.NewGuid(), JiraProjectId = projectId,
            StartedAt = startedAt, Status = SyncRunStatus.Running, IssuesProcessed = 0
        };
        if (!await TryStartRunAsync(run, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);

        try
        {
            var watermark = await _db.SyncRuns
                .Where(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Succeeded && r.Kind == SyncRunKind.Sync)
                .MaxAsync(r => (DateTime?)r.StartedAt, ct);

            var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct)
                       ?? throw new InvalidOperationException("Connection not found for project.");
            var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));

            var queries = await _db.JiraQueries.Where(q => q.JiraProjectId == projectId).ToListAsync(ct);

            var processed = 0;
            foreach (var query in queries)
            {
                var jql = watermark is null
                    ? query.Jql
                    : $"({query.Jql}) AND updated >= \"{watermark.Value:yyyy-MM-dd HH:mm}\"";

                string? token = null;
                do
                {
                    var page = await _jira.SearchIssuesPageAsync(creds, jql, token, ct);
                    foreach (var issue in page.Issues)
                    {
                        var existing = await _db.RawIssueSnapshots
                            .FirstOrDefaultAsync(x => x.JiraProjectId == projectId && x.JiraIssueId == issue.JiraIssueId, ct);
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
            }

            // Transform: ingest the full changelog to raw, then derive the modeled tables (Jira-free).
            await _ingest.IngestProjectAsync(projectId, creds, ct);
            await _derivation.DeriveProjectAsync(projectId, ct);

            run.Status = SyncRunStatus.Succeeded;
            run.IssuesProcessed = processed;
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            // JiraApiException messages are curated + safe (the token never appears in them), so keep them.
            // Anything else (DbUpdateException, Npgsql, JSON parse, ...) can carry raw infra detail — SQL
            // fragments, column names — which must not land in this tenant-readable field. Log it server-side
            // and persist a generic message instead.
            if (ex is JiraApiException)
            {
                run.Error = ex.Message;
            }
            else
            {
                _logger.LogError(ex, "Unexpected failure syncing project {ProjectId}", projectId);
                run.Error = "Sync failed unexpectedly.";
            }
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }

        return new SyncRunResult(SyncOutcome.Completed, ToView(run));
    }

    public async Task<SyncRunResult> RunRederiveAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new SyncRunResult(SyncOutcome.ProjectNotFound, null);
        if (await _db.SyncRuns.AnyAsync(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Running, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);

        var run = new SyncRun
        {
            Id = Guid.NewGuid(), JiraProjectId = projectId, StartedAt = _clock.UtcNow,
            Status = SyncRunStatus.Running, Kind = SyncRunKind.Rederive, IssuesProcessed = 0
        };
        if (!await TryStartRunAsync(run, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);
        try
        {
            await _derivation.DeriveProjectAsync(projectId, ct);
            run.Status = SyncRunStatus.Succeeded; run.FinishedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed; run.Error = ex.Message; run.FinishedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return new SyncRunResult(SyncOutcome.Completed, ToView(run));
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
