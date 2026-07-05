using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;

    public SyncOrchestrator(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock)
    {
        _db = db; _jira = jira; _protector = protector; _clock = clock;
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
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        try
        {
            var watermark = await _db.SyncRuns
                .Where(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Succeeded)
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
                            .FirstOrDefaultAsync(x => x.JiraIssueId == issue.JiraIssueId, ct);
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

            run.Status = SyncRunStatus.Succeeded;
            run.IssuesProcessed = processed;
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.Error = ex.Message; // JiraApiException/domain messages are safe; the token never appears in them
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }

        return new SyncRunResult(SyncOutcome.Completed, ToView(run));
    }

    private static SyncRunView ToView(SyncRun r) =>
        new(r.Id, r.JiraProjectId, r.Status.ToString(), r.StartedAt, r.FinishedAt, r.IssuesProcessed, r.Error, r.NextRunAt);
}
