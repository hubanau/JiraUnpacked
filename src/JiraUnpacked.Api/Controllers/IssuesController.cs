// src/JiraUnpacked.Api/Controllers/IssuesController.cs
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class IssuesController : ControllerBase
{
    private readonly IIssueQueryService _issues;
    private readonly IFilterValueService _filterValues;

    public IssuesController(IIssueQueryService issues, IFilterValueService filterValues)
    {
        _issues = issues;
        _filterValues = filterValues;
    }

    [HttpGet("api/projects/{projectId:guid}/issues")]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var result = await _issues.ListAsync(projectId, page, pageSize, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("api/projects/{projectId:guid}/issues/{jiraIssueId}")]
    public async Task<IActionResult> Detail(Guid projectId, string jiraIssueId, CancellationToken ct = default)
    {
        var detail = await _issues.GetDetailAsync(projectId, jiraIssueId, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("api/projects/{projectId:guid}/issues/query")]
    public async Task<IActionResult> Query(Guid projectId, IssueFilterRequest request, CancellationToken ct = default)
    {
        var result = await _issues.QueryAsync(projectId, request, ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        return result.Result is null ? NotFound() : Ok(result.Result);
    }

    /// <summary>
    /// The values a dictionary field actually takes in this project, for the filter's value
    /// picker. A field with no value list answers 400 naming it rather than an empty array:
    /// "this field has no picker" and "this project has no values" are different answers, and a
    /// client that cannot tell them apart shows the wrong empty state.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/filter-values")]
    public async Task<IActionResult> FilterValues(
        Guid projectId, [FromQuery] string? field, CancellationToken ct = default)
    {
        var result = await _filterValues.GetAsync(projectId, field ?? "", ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        return result.Values is null ? NotFound() : Ok(new { field, values = result.Values });
    }

    /// <summary>Matches the SPA's own filter encoding, including its string enum names.</summary>
    private static readonly JsonSerializerOptions FilterJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The filtered, sorted list as CSV. A GET rather than a POST because a browser can only
    /// download from a navigation, and the whole view already lives in the query string.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/issues/export.csv")]
    public async Task<IActionResult> Export(
        Guid projectId,
        [FromQuery] string? filter, [FromQuery] string? logic,
        [FromQuery] string? sortBy, [FromQuery] bool sortDescending = true,
        [FromQuery] string? cols = null,
        CancellationToken ct = default)
    {
        List<FilterCondition>? conditions;
        try
        {
            conditions = string.IsNullOrWhiteSpace(filter)
                ? null
                : JsonSerializer.Deserialize<List<FilterCondition>>(filter, FilterJson);
        }
        catch (JsonException)
        {
            // A URL is user-editable and chat clients truncate them. Say so rather than 500.
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "The filter in the link could not be read.");
        }

        var group = conditions is { Count: > 0 }
            ? new FilterGroup(logic == "Or" ? FilterLogic.Or : FilterLogic.And, conditions, null)
            : null;
        var request = new IssueFilterRequest(null, group, 1, 1, sortBy, sortDescending);

        // Checked BEFORE a single byte is written: once the response has started there is no way
        // left to report a problem.
        var info = await _issues.PrepareExportAsync(projectId, request, ct);
        if (info.ProjectKey is null) return NotFound();
        if (info.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: info.Error);

        var columns = ExportColumns.Resolve(cols);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{info.ProjectKey}-issues-{DateTime.UtcNow:yyyyMMdd}.csv\"";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false));
        await writer.WriteAsync(CsvWriter.Row(columns.Select(c => c.Header)));

        // Written as they arrive. Buffering the set to build a string is the mistake this project
        // removed from derivation, and it is worse here: the response is held for the download.
        await foreach (var item in _issues.StreamExportAsync(projectId, request, ct))
            await writer.WriteAsync(CsvWriter.Row(columns.Select(c => c.Value(item))));

        return new EmptyResult();
    }
}
