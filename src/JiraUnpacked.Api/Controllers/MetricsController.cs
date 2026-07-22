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

    [HttpGet("api/projects/{projectId:guid}/metrics/time-in-progress")]
    public async Task<IActionResult> TimeInProgress(Guid projectId, CancellationToken ct = default)
    {
        var report = await _metrics.GetTimeInProgressAsync(projectId, ct);
        return report is null ? NotFound() : Ok(report);
    }

    [HttpGet("api/projects/{projectId:guid}/metrics/throughput")]
    public async Task<IActionResult> Throughput(
        Guid projectId,
        [FromQuery] string? bucket = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (!ThroughputBucketSizes.TryParse(bucket, out var size))
            return BadRequest(new { error = "bucket must be one of: day, week, month" });

        if (from is DateTime f && to is DateTime t && f > t)
            return BadRequest(new { error = "from must not be later than to" });

        var report = await _metrics.GetThroughputAsync(projectId, size, from, to, ct);
        return report is null ? NotFound() : Ok(report);
    }

    [HttpGet("api/projects/{projectId:guid}/metrics/status-aging")]
    public async Task<IActionResult> StatusAging(
        Guid projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var report = await _metrics.GetStatusAgingAsync(projectId, page, pageSize, ct);
        return report is null ? NotFound() : Ok(report);
    }
}
