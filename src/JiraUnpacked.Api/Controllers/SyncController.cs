using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class SyncController : ControllerBase
{
    private readonly ISyncOrchestrator _orchestrator;
    private readonly ISyncRunQuery _runs;
    private readonly ISyncQueue _queue;
    private readonly ISyncCancellation _cancellation;
    private readonly ICurrentTenant _tenant;

    /// <summary>
    /// Stops the project's running sync.
    ///
    /// The run is resolved from the PROJECT rather than named by the caller: the run query is
    /// tenant-scoped, so this cannot reach a run the caller could not already see, whereas
    /// accepting a run id would let one tenant name another's.
    ///
    /// 202, not 200. Cancellation is a request the run notices at its next checkpoint, and the
    /// row does not read Cancelled until it unwinds. 404 when there is nothing running — which
    /// includes a run left behind by a previous process: that one is orphaned rather than
    /// cancellable, and belongs to the reaper.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/sync/cancel")]
    public async Task<IActionResult> Cancel(Guid projectId, CancellationToken ct)
    {
        var running = (await _runs.ListAsync(projectId, ct))
            .FirstOrDefault(r => r.Status == nameof(SyncRunStatus.Running));

        if (running is null) return NotFound();
        return _cancellation.Cancel(running.Id) ? Accepted() : NotFound();
    }

    public SyncController(ISyncOrchestrator orchestrator, ISyncRunQuery runs,
        ISyncQueue queue, ISyncCancellation cancellation, ICurrentTenant tenant)
    {
        _orchestrator = orchestrator;
        _runs = runs;
        _queue = queue;
        _cancellation = cancellation;
        _tenant = tenant;
    }

    [HttpPost("api/projects/{projectId:guid}/sync")]
    [EnableRateLimiting("sync")]
    public Task<IActionResult> Sync(Guid projectId, CancellationToken ct) =>
        StartAsync(projectId, isRederive: false, ct);

    [HttpGet("api/projects/{projectId:guid}/sync-runs")]
    public async Task<IActionResult> Runs(Guid projectId, CancellationToken ct) =>
        Ok(await _runs.ListAsync(projectId, ct));

    [HttpPost("api/projects/{projectId:guid}/rederive")]
    [EnableRateLimiting("sync")]
    public Task<IActionResult> Rederive(Guid projectId, CancellationToken ct) =>
        StartAsync(projectId, isRederive: true, ct);

    /// <summary>
    /// Claims the run, hands it to the background worker, and answers 202 with the Running row
    /// to poll — rather than holding the request open for the minutes a large project takes.
    ///
    /// The claim stays synchronous so 404 and 409 keep their meaning: a queue-and-shrug endpoint
    /// could only ever answer "accepted", including for a project that does not exist.
    ///
    /// `ct` belongs to the REQUEST and is passed only to the claim. The work itself must not
    /// receive it — that was the bug: a closed tab cancelled the sync it had just started.
    /// </summary>
    private async Task<IActionResult> StartAsync(Guid projectId, bool isRederive, CancellationToken ct)
    {
        var result = isRederive
            ? await _orchestrator.StartRederiveAsync(projectId, ct)
            : await _orchestrator.StartProjectAsync(projectId, ct);

        switch (result.Outcome)
        {
            case SyncOutcome.ProjectNotFound:
                return NotFound();
            case SyncOutcome.AlreadyRunning:
                return Conflict();
        }

        // Authorized + the project resolved under the tenant filter, so a tenant is present.
        _queue.Enqueue(new SyncWorkItem(_tenant.EndUserId!.Value, result.Run!.Id, isRederive));
        return Accepted(result.Run);
    }
}
