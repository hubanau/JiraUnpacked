using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Jira;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly ISyncCancellation _cancellation;
    private readonly SyncSchedulerOptions _options;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock,
        IChangelogIngestService ingest, IDerivationService derivation, ISyncCancellation cancellation,
        IOptions<SyncSchedulerOptions> options, ILogger<SyncOrchestrator> logger)
    {
        _db = db; _jira = jira; _protector = protector; _clock = clock;
        _ingest = ingest; _derivation = derivation; _cancellation = cancellation;
        _options = options.Value; _logger = logger;
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

        // Registered HERE, not in the worker that usually calls this. The scheduled tick invokes
        // the orchestrator directly, so registering one level up left every scheduled run
        // uncancellable — Stop answered "nothing is running to stop" while a run was plainly
        // running. This is the one place every path goes through.
        //
        // Reassigning ct is deliberate: everything below then uses the cancellable token, with no
        // chance of one stray call still holding the original and ignoring a stop.
        using var registration = _cancellation.Register(run.Id, ct, out var runToken);
        ct = runToken;

        var projectId = project.Id;
        try
        {
            var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct)
                       ?? throw new InvalidOperationException("Connection not found for project.");
            var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));

            // Sorted here, once, and this same list feeds BOTH the fingerprint and the fetch loop.
            // ScopeFingerprint sorts internally so the hash never cared about order, but a fetch
            // cursor addresses a query by INDEX, and Postgres promises no order without ORDER BY.
            // Unsorted, a resumed run could re-enter the wrong query and skip every issue in another.
            var queries = await _db.JiraQueries
                .Where(q => q.JiraProjectId == projectId)
                .Select(q => q.Jql)
                .ToListAsync(ct);
            queries.Sort(StringComparer.Ordinal);

            // A project with no query of its own syncs in full. Queries NARROW what is pulled, so an
            // empty list means "nothing to narrow by", not "nothing to sync" — reading it as the
            // latter is what let a freshly added project report Succeeded having never called Jira.
            var wholeProject = queries.Count == 0;
            if (wholeProject) queries.Add(WholeProjectJql(project.JiraProjectKey));

            // The scope must be known BEFORE the watermark is chosen, because the watermark is
            // only valid for the scope that produced it.
            var fingerprint = ScopeFingerprint(queries);

            // Only a run over THIS scope may seed the incremental clause. A run says "everything in
            // the scope I fetched is current as of then" — it says nothing about a scope that has
            // never been fetched, so after the queries change the pull must start clean. Runs
            // predating the column carry a null fingerprint and match nothing, which is intended:
            // among them are the silent no-ops that fetched nothing at all.
            var watermark = await _db.SyncRuns
                .Where(r => r.JiraProjectId == projectId
                            && r.Status == SyncRunStatus.Succeeded
                            && r.Kind == SyncRunKind.Sync
                            && r.ScopeFingerprint == fingerprint)
                .MaxAsync(r => (DateTime?)r.StartedAt, ct);

            // Belt and braces for a scope whose data went away underneath it — retention, an
            // erasure, a manual delete. There is then no baseline to be incremental from, whatever
            // the run history claims.
            if (watermark is not null &&
                !await _db.RawIssueSnapshots.AnyAsync(r => r.JiraProjectId == projectId, ct))
            {
                _logger.LogWarning(
                    "Sync {ProjectKey} ({ProjectId}): ignoring watermark {Watermark} because the project holds no "
                    + "stored issues — there is nothing to be incremental from. Falling back to a full pull.",
                    project.JiraProjectKey, projectId, watermark.Value.ToString("o"));
                watermark = null;
            }

            run.ScopeFingerprint = fingerprint;

            // Where the last attempt over THIS scope stopped, if there was one.
            var checkpoint = await FindResumePointAsync(projectId, SyncRunKind.Sync, fingerprint, ct);
            var startStage = ResumeStage.IndexOf(checkpoint?.Stage);
            if (checkpoint is not null)
                _logger.LogInformation(
                    "Sync {ProjectKey}: resuming at stage {Stage} with {Done} item(s) already done.",
                    project.JiraProjectKey, checkpoint.Value.Stage, checkpoint.Value.ItemsDone);

            _logger.LogInformation(
                "Sync starting for {ProjectKey} ({ProjectId}): {QueryCount} query(ies), wholeProjectDefault={WholeProject}, "
                + "scope={Fingerprint}, watermark={Watermark}.",
                project.JiraProjectKey, projectId, queries.Count, wholeProject, fingerprint,
                watermark?.ToString("o") ?? "none (full pull of this scope)");

            // The JQL actually sent, resolved once so the count and the fetch cannot disagree.
            var effective = queries
                .Select(q => watermark is null
                    ? q
                    : $"({q}) AND updated >= \"{watermark.Value:yyyy-MM-dd HH:mm}\"")
                .ToList();

            // A checkpoint past the fetch means a previous attempt already pulled this scope in
            // full. Counting and fetching it again would cost the better part of an hour and
            // change nothing, which is the waste this whole design exists to remove.
            var skipFetch = startStage > ResumeStage.IndexOf(ResumeStage.Fetch);

            if (!skipFetch)
            {
                await SetPhaseAsync(run, SyncPhase.Counting, ct);
                run.TotalToSync = await CountScopeAsync(creds, effective, ct);
                await SetPhaseAsync(run, SyncPhase.Fetching, ct);
            }

            // A resumed run reports the issues the ATTEMPT has stored rather than the zero it
            // fetched itself, because the number on screen means "how big is this project", and
            // dropping it to zero mid-way would read as data loss.
            var processed = skipFetch
                ? await _db.RawIssueSnapshots.CountAsync(r => r.JiraProjectId == projectId, ct)
                : 0;

            foreach (var jql in skipFetch ? Enumerable.Empty<string>() : effective)
            {

                var fetched = 0;
                var pages = 0;
                string? token = null;
                do
                {
                    var page = await _jira.SearchIssuesPageAsync(creds, jql, token, ct);
                    fetched += page.Issues.Count;
                    pages++;
                    // Postgres rejects NUL in jsonb, and Jira serves back whatever someone pasted
                    // into a text field. One such issue used to fail its page's INSERT and, with
                    // it, the whole run. Stripped here, at the boundary where the payload stops
                    // being an HTTP response and becomes a database row.
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
                                JiraIssueId = issue.JiraIssueId,
                                Payload = JiraJson.SanitiseForStorage(issue.RawJson), FetchedAt = _clock.UtcNow
                            });
                        }
                        else
                        {
                            existing.Payload = JiraJson.SanitiseForStorage(issue.RawJson);
                            existing.FetchedAt = _clock.UtcNow;
                        }
                        processed++;
                    }
                    // Progress rides along on the save this loop already does, so a visible bar
                    // costs no extra round trip. IssuesProcessed is therefore live during the run
                    // rather than only true once it ends.
                    run.IssuesProcessed = processed;
                    run.PhaseProcessed = processed;
                    run.PhaseTotal = run.TotalToSync;
                    run.LastProgressAt = _clock.UtcNow;
                    await _db.SaveChangesAsync(ct);

                    // Let the page go. Without this the context accumulates every snapshot for
                    // the whole run — EF keeps each entity AND an original-values copy for change
                    // detection — so a large project drags hundreds of megabytes of issue JSON
                    // through to the end and change detection re-scans all of it on every page.
                    // Two whole-project syncs died at ~31.7k issues before this was bounded.
                    // Only the snapshots are detached: the run itself must stay tracked, or the
                    // progress and completion writes below would silently do nothing.
                    DetachAll<RawIssueSnapshot>();

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
            // Both are phases of their own because they can outlast the fetch on a large project,
            // and a bar that sat at 100% through them would read as a hang.
            if (startStage <= ResumeStage.IndexOf(ResumeStage.Changelog))
            {
                await SetPhaseAsync(run, SyncPhase.Changelog, ct);
                await _ingest.IngestProjectAsync(projectId, creds, async (done, total, token) =>
                {
                    run.PhaseProcessed = done;
                    run.PhaseTotal = total;
                    run.LastProgressAt = _clock.UtcNow;
                    await _db.SaveChangesAsync(token);
                }, ct);
            }

            await SetPhaseAsync(run, SyncPhase.Deriving, ct);
            await _derivation.DeriveProjectAsync(projectId, async (d, t, token) =>
            {
                run.PhaseProcessed = d;
                run.PhaseTotal = t;
                run.LastProgressAt = _clock.UtcNow;
                await _db.SaveChangesAsync(token);
            }, ct);

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
            ClearCheckpoint(run);          // nothing left to resume
            run.Phase = null;              // over: a finished run is doing nothing
            run.PhaseProcessed = null;
            run.PhaseTotal = null;
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (_cancellation.WasCancelled(run.Id))
        {
            // Someone pressed stop. Recorded as Cancelled rather than Failed: a deliberate stop is
            // not a fault, and colouring it red would bury the real failures among decisions.
            DiscardPendingChangesExcept(run);
            run.Status = SyncRunStatus.Cancelled;
            run.Error = "Stopped on request.";
            run.Phase = null;
            run.PhaseProcessed = null;
            run.PhaseTotal = null;
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);

            // CancellationToken.None, not ct: ct is the cancelled one, and recording WHY the run
            // ended is the single write that still has to happen after a cancellation.
            await _db.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation(
                "Sync {ProjectKey} ({ProjectId}) stopped on request after {Processed} issue(s).",
                project.JiraProjectKey, projectId, run.IssuesProcessed);
        }
        catch (Exception ex)
        {
            // The pending changes are WHY we are here. They have to go before the failure can be
            // recorded: saving the run through a context that still holds the rejected rows
            // re-attempts them, throws again, and the second throw escapes past this catch —
            // leaving the run Running forever and blocking the project until the reaper. That is
            // exactly how a NUL in one issue's description looked like a hung sync.
            DiscardPendingChangesExcept(run);
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

        // Registered HERE, not in the worker that usually calls this. The scheduled tick invokes
        // the orchestrator directly, so registering one level up left every scheduled run
        // uncancellable — Stop answered "nothing is running to stop" while a run was plainly
        // running. This is the one place every path goes through.
        //
        // Reassigning ct is deliberate: everything below then uses the cancellable token, with no
        // chance of one stray call still holding the original and ignoring a stop.
        using var registration = _cancellation.Register(run.Id, ct, out var runToken);
        ct = runToken;

        var projectId = project.Id;
        try
        {
            // No TotalToSync: a rederive fetches nothing, so there is no denominator to report and
            // null is the honest answer rather than a zero that would render as an empty bar.
            await SetPhaseAsync(run, SyncPhase.Deriving, ct);
            await _derivation.DeriveProjectAsync(projectId, async (d, t, token) =>
            {
                run.PhaseProcessed = d;
                run.PhaseTotal = t;
                run.LastProgressAt = _clock.UtcNow;
                await _db.SaveChangesAsync(token);
            }, ct);
            run.Status = SyncRunStatus.Succeeded; ClearCheckpoint(run); run.Phase = null; run.FinishedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);

            var modeled = await _db.Issues.CountAsync(i => i.JiraProjectId == projectId, ct);
            _logger.LogInformation(
                "Rederive {ProjectKey} ({ProjectId}) finished: {Modeled} modelled issue(s).",
                project.JiraProjectKey, projectId, modeled);
        }
        catch (Exception ex)
        {
            DiscardPendingChangesExcept(run);   // see ExecuteProjectAsync
            SetFailure(run, ex, projectId);
            await _db.SaveChangesAsync(ct);
        }
        return new SyncRunResult(SyncOutcome.Completed, ToView(run));
    }

    /// <summary>
    /// Identifies the set of JQL a run extracted, so a later run can tell whether its own scope
    /// has ever been fetched before.
    ///
    /// Sorted, so merely reordering the queries is not a change of scope and does not force a
    /// refetch. Truncated to 32 hex characters: this only ever decides "same scope or not", and
    /// collisions between two JQL sets a user actually wrote are not a realistic concern.
    /// </summary>
    /// <summary>Where a previous attempt stopped: a stage, a position within it, and how much of
    /// that stage was already done.</summary>
    private readonly record struct SyncCheckpoint(string Stage, string? Cursor, int ItemsDone);

    /// <summary>
    /// Commits a batch: whatever entities the caller has already added or modified, the cursor,
    /// and the progress fields — in ONE SaveChangesAsync.
    ///
    /// The single save is the whole point. Two saves can be interrupted between them, and a
    /// cursor that survives while its data does not claims work that was never committed, which
    /// skips those issues for good. Callers must NOT save their data separately.
    /// </summary>
    private async Task SaveCheckpointAsync(
        SyncRun run, string stage, string? cursor, int itemsDone, int? phaseTotal, CancellationToken ct)
    {
        run.ResumeStage = stage;
        run.ResumeCursor = cursor;
        run.ResumeItemsDone = itemsDone;
        run.PhaseProcessed = itemsDone;
        run.PhaseTotal = phaseTotal;
        run.LastProgressAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The checkpoint this run may continue from, or null to start clean.
    ///
    /// Matched on scope, because a checkpoint is only valid for the queries that produced it —
    /// the same rule the incremental watermark follows, and for the same reason. Matched on Kind
    /// so a Rederive continues a Rederive and nothing else: predictable beats clever here.
    /// </summary>
    private async Task<SyncCheckpoint?> FindResumePointAsync(
        Guid projectId, SyncRunKind kind, string fingerprint, CancellationToken ct)
    {
        var prior = await _db.SyncRuns
            .Where(r => r.JiraProjectId == projectId
                     && r.Kind == kind
                     && r.ScopeFingerprint == fingerprint
                     && r.ResumeStage != null
                     && (r.Status == SyncRunStatus.Failed || r.Status == SyncRunStatus.Cancelled))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.ResumeStage, r.ResumeCursor, r.ResumeItemsDone })
            .FirstOrDefaultAsync(ct);

        if (prior?.ResumeStage is null) return null;

        // Belt and braces, mirroring the watermark guard: a cursor past the fetch indexes into raw
        // rows, and if those went away there is nothing to be part-way through.
        if (prior.ResumeStage != ResumeStage.Fetch &&
            !await _db.RawIssueSnapshots.AnyAsync(r => r.JiraProjectId == projectId, ct))
        {
            _logger.LogWarning(
                "Sync {ProjectId}: discarding a {Stage} checkpoint because the project holds no stored "
                + "issues — there is nothing to resume over. Starting clean.", projectId, prior.ResumeStage);
            return null;
        }

        return new SyncCheckpoint(prior.ResumeStage, prior.ResumeCursor, prior.ResumeItemsDone ?? 0);
    }

    /// <summary>A finished run is not part-way through anything.</summary>
    private static void ClearCheckpoint(SyncRun run)
    {
        run.ResumeStage = null;
        run.ResumeCursor = null;
        run.ResumeItemsDone = null;
    }

    private static string ScopeFingerprint(IEnumerable<string> effectiveJql)
    {
        var canonical = string.Join("\n", effectiveJql.OrderBy(j => j, StringComparer.Ordinal));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..32];
    }

    /// <summary>
    /// Detaches everything except the run, abandoning whatever was pending. Used only on the
    /// failure path, where the pending changes are the thing that failed and the single write
    /// that still MUST succeed is the one recording that failure.
    /// </summary>
    private void DiscardPendingChangesExcept(SyncRun run)
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            if (entry.Entity is not SyncRun)
                entry.State = EntityState.Detached;

        // The run must still be tracked AND still carry its edits, or the failure write below is
        // a no-op and the run stays Running — the very thing this method exists to prevent.
        _db.Entry(run).State = EntityState.Modified;
    }

    /// <summary>
    /// Drops tracked entities of one type, leaving everything else — notably the SyncRun — tracked.
    /// ChangeTracker.Clear() would be simpler and wrong: it would detach the run too.
    /// </summary>
    private void DetachAll<T>() where T : class
    {
        foreach (var entry in _db.ChangeTracker.Entries<T>().ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>
    /// Publishes the phase immediately, so a poll during a long step sees what is happening
    /// rather than the previous step's label.
    /// </summary>
    private async Task SetPhaseAsync(SyncRun run, string phase, CancellationToken ct)
    {
        run.Phase = phase;
        run.LastProgressAt = _clock.UtcNow;

        // Each phase counts something different, so entering one clears the previous one's
        // numbers. Leaving them would show the last phase's progress under this phase's label —
        // a bar that reads 100% while the work has only just begun.
        run.PhaseProcessed = null;
        run.PhaseTotal = null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Jira's approximate count for the whole scope — the denominator of the progress bar.
    ///
    /// /search/jql is token-paginated and reports no total, so this is the only way to size a
    /// sync in advance. A null from any one query makes the whole total null rather than an
    /// undercount: a bar reading "300 of 100" is worse than one that admits it does not know.
    /// </summary>
    private async Task<int?> CountScopeAsync(
        JiraCredentials creds, IReadOnlyList<string> effectiveJql, CancellationToken ct)
    {
        var total = 0;
        foreach (var jql in effectiveJql)
        {
            var count = await _jira.ApproximateCountAsync(creds, jql, ct);
            if (count is null) return null;
            total += count.Value;
        }
        return total;
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
        run.Phase = null;
        run.PhaseProcessed = null;
        run.PhaseTotal = null;
        run.FinishedAt = _clock.UtcNow;
        if (ex is JiraApiException)
        {
            run.Error = ex.Message;
        }
        else
        {
            _logger.LogError(ex, "Unexpected failure syncing project {ProjectId}", projectId);

            // The TYPE, not the message. A type name carries no tenant data, no SQL and no
            // connection detail, so it is safe in a column a user reads — and it is the
            // difference between "something went wrong" and a diagnosis. The message stays
            // server-side. This exists because the only record of one such failure was a
            // container log that was gone by the time anyone asked why.
            run.Error = $"Sync failed unexpectedly ({ex.GetType().Name}).";
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
        new(r.Id, r.JiraProjectId, r.Status.ToString(), r.StartedAt, r.FinishedAt, r.IssuesProcessed, r.Error, r.NextRunAt, r.Kind.ToString(),
            r.TotalToSync, r.Phase, r.PhaseProcessed, r.PhaseTotal);
}
