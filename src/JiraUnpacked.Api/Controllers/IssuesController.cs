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
    public IssuesController(IIssueQueryService issues) => _issues = issues;

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
}
