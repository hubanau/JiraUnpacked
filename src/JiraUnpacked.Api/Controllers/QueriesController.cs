using JiraUnpacked.Api.Contracts.Queries;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class QueriesController : ControllerBase
{
    private readonly IJiraQueryService _queries;
    public QueriesController(IJiraQueryService queries) => _queries = queries;

    [HttpPost("api/projects/{projectId:guid}/queries")]
    public async Task<IActionResult> Add(Guid projectId, AddQueryRequest request, CancellationToken ct)
    {
        var result = await _queries.AddAsync(projectId, request.Jql, ct);
        return result.Status switch
        {
            AddQueryStatus.Created => CreatedAtAction(nameof(List), new { projectId }, result.View),
            AddQueryStatus.ProjectNotFound => NotFound(),
            AddQueryStatus.InvalidJql => Problem(statusCode: 400, title: "Invalid JQL.", detail: string.Join("; ", result.Errors)),
            _ => Problem(statusCode: 502, title: "Jira could not be reached.")
        };
    }

    [HttpGet("api/projects/{projectId:guid}/queries")]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct) =>
        Ok(await _queries.ListAsync(projectId, ct));

    [HttpDelete("api/queries/{queryId:guid}")]
    public async Task<IActionResult> Delete(Guid queryId, CancellationToken ct) =>
        await _queries.DeleteAsync(queryId, ct) ? NoContent() : NotFound();
}
