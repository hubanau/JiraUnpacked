using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class SyncController : ControllerBase
{
    private readonly ISyncOrchestrator _orchestrator;
    public SyncController(ISyncOrchestrator orchestrator) => _orchestrator = orchestrator;

    [HttpPost("api/projects/{projectId:guid}/sync")]
    public async Task<IActionResult> Sync(Guid projectId, CancellationToken ct)
    {
        var result = await _orchestrator.RunProjectAsync(projectId, ct);
        return result.Outcome switch
        {
            SyncOutcome.ProjectNotFound => NotFound(),
            SyncOutcome.AlreadyRunning => Conflict(),
            _ => Ok(result.Run)
        };
    }
}
