// src/JiraUnpacked.Api/Controllers/IssuesController.cs
using JiraUnpacked.Domain.Abstractions;
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
}
