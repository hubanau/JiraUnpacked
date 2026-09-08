// src/JiraUnpacked.Api/Controllers/MetricsController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

/// <summary>
/// FR-E2 curated metrics. Each metric has a GET (the whole project) and a POST that takes the
/// same filter payload as the issue query. The GET routes are unchanged and remain the
/// unfiltered report; the POST routes compute the same metric over the filtered population.
/// </summary>
[ApiController]
[Authorize]
public sealed class MetricsController : ControllerBase
{
    private readonly IMetricsQueryService _metrics;
    private readonly IFieldChurnQueryService _churn;

    public MetricsController(IMetricsQueryService metrics, IFieldChurnQueryService churn)
    {
        _metrics = metrics;
        _churn = churn;
    }

    /// <summary>Null Report + null Error => 404; non-null Error => 400; otherwise 200.</summary>
    private IActionResult Respond<T>(ReportResult<T> result) where T : class =>
        result.Error is not null
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error)
            : result.Report is null ? NotFound() : Ok(result.Report);

    [HttpGet("api/projects/{projectId:guid}/metrics/story-counts")]
    public async Task<IActionResult> StoryCounts(Guid projectId, CancellationToken ct = default) =>
        Respond(await _metrics.GetStoryCountsAsync(projectId, null, ct));

    [HttpPost("api/projects/{projectId:guid}/metrics/story-counts")]
    public async Task<IActionResult> StoryCountsFiltered(
        Guid projectId, ReportFilterRequest request, CancellationToken ct = default) =>
        Respond(await _metrics.GetStoryCountsAsync(projectId, request.ToFilter(), ct));

    [HttpGet("api/projects/{projectId:guid}/metrics/time-in-progress")]
    public async Task<IActionResult> TimeInProgress(Guid projectId, CancellationToken ct = default) =>
        Respond(await _metrics.GetTimeInProgressAsync(projectId, null, ct));

    [HttpPost("api/projects/{projectId:guid}/metrics/time-in-progress")]
    public async Task<IActionResult> TimeInProgressFiltered(
        Guid projectId, ReportFilterRequest request, CancellationToken ct = default) =>
        Respond(await _metrics.GetTimeInProgressAsync(projectId, request.ToFilter(), ct));

    [HttpGet("api/projects/{projectId:guid}/metrics/throughput")]
    public async Task<IActionResult> Throughput(
        Guid projectId,
        [FromQuery] string? bucket = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (ValidateThroughput(bucket, from, to, out var size, out var error)) return error!;
        return Respond(await _metrics.GetThroughputAsync(projectId, size, from, to, null, ct));
    }

    [HttpPost("api/projects/{projectId:guid}/metrics/throughput")]
    public async Task<IActionResult> ThroughputFiltered(
        Guid projectId, ThroughputFilterRequest request, CancellationToken ct = default)
    {
        if (ValidateThroughput(request.Bucket, request.From, request.To, out var size, out var error))
            return error!;
        return Respond(await _metrics.GetThroughputAsync(
            projectId, size, request.From, request.To, request.ToFilter(), ct));
    }

    /// <summary>
    /// Throughput's own window validation, shared by both routes so they cannot disagree.
    /// Returns true when the request is INVALID, with the response to send. The body shape is
    /// the plain <c>{ error }</c> the GET route has always returned, deliberately kept on the
    /// POST route too: a client parses one shape per concern.
    /// </summary>
    private bool ValidateThroughput(
        string? bucket, DateTime? from, DateTime? to, out ThroughputBucketSize size, out IActionResult? error)
    {
        if (!ThroughputBucketSizes.TryParse(bucket, out size))
        {
            error = BadRequest(new { error = "bucket must be one of: day, week, month" });
            return true;
        }

        if (from is DateTime f && to is DateTime t && f > t)
        {
            error = BadRequest(new { error = "from must not be later than to" });
            return true;
        }

        error = null;
        return false;
    }

    [HttpGet("api/projects/{projectId:guid}/metrics/status-aging")]
    public async Task<IActionResult> StatusAging(
        Guid projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        Respond(await _metrics.GetStatusAgingAsync(projectId, page, pageSize, null, ct));

    [HttpPost("api/projects/{projectId:guid}/metrics/status-aging")]
    public async Task<IActionResult> StatusAgingFiltered(
        Guid projectId, PagedFilterRequest request, CancellationToken ct = default) =>
        Respond(await _metrics.GetStatusAgingAsync(
            projectId, request.Page, request.PageSize, request.ToFilter(), ct));

    /// <summary>
    /// Issues whose chosen changelog field kept changing, worst first — "which stories keep
    /// getting pushed to the next sprint". JQL cannot express this: it has a CHANGED predicate
    /// but no way to count changes, so the answer only exists in the ingested changelog.
    ///
    /// statusClasses repeats (?statusClasses=Active&amp;statusClasses=Other) and defaults to
    /// Active alone.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/metrics/field-churn")]
    public async Task<IActionResult> FieldChurn(
        Guid projectId,
        [FromQuery] string? field = null,
        [FromQuery] int minChanges = 2,
        [FromQuery] string[]? statusClasses = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        Respond(await _churn.GetFieldChurnAsync(
            projectId, field, minChanges, statusClasses, page, pageSize, null, ct));

    [HttpPost("api/projects/{projectId:guid}/metrics/field-churn")]
    public async Task<IActionResult> FieldChurnFiltered(
        Guid projectId, FieldChurnFilterRequest request, CancellationToken ct = default) =>
        Respond(await _churn.GetFieldChurnAsync(
            projectId, request.Field, request.MinChanges, request.StatusClasses,
            request.Page, request.PageSize, request.ToFilter(), ct));
}
