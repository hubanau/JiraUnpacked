// src/JiraUnpacked.Api/Controllers/MetricsController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class MetricsController : ControllerBase
{
    private readonly IMetricsQueryService _metrics;
    public MetricsController(IMetricsQueryService metrics) => _metrics = metrics;

    [HttpGet("api/projects/{projectId:guid}/metrics/story-counts")]
    public async Task<IActionResult> StoryCounts(Guid projectId, CancellationToken ct = default)
    {
        var report = await _metrics.GetStoryCountsAsync(projectId, ct);
        return report is null ? NotFound() : Ok(report);
    }
}
