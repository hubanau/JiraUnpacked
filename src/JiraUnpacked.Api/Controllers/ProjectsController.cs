using JiraUnpacked.Api.Contracts.Projects;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class ProjectsController : ControllerBase
{
    private readonly IJiraProjectService _projects;
    public ProjectsController(IJiraProjectService projects) => _projects = projects;

    [HttpPost("api/connections/{connectionId:guid}/projects")]
    public async Task<IActionResult> Add(Guid connectionId, AddProjectRequest request, CancellationToken ct)
    {
        var result = await _projects.AddAsync(connectionId, request.ProjectKey, request.SyncIntervalMinutes, ct);
        return result.Status switch
        {
            AddProjectStatus.Created => CreatedAtAction(nameof(Get), new { projectId = result.View!.Id }, result.View),
            AddProjectStatus.ConnectionNotFound => NotFound(),
            AddProjectStatus.UnknownProjectKey => Problem(statusCode: 400, title: "Unknown Jira project key."),
            AddProjectStatus.InvalidInterval => Problem(statusCode: 400, title: "Sync interval must be at least 60 minutes."),
            AddProjectStatus.Duplicate => Conflict(),
            // Upstream Jira failures propagate as JiraApiException → 502 via JiraApiExceptionFilter.
            _ => throw new InvalidOperationException($"Unexpected {nameof(AddProjectStatus)}: {result.Status}.")
        };
    }

    [HttpGet("api/connections/{connectionId:guid}/projects")]
    public async Task<IActionResult> List(Guid connectionId, CancellationToken ct) =>
        Ok(await _projects.ListAsync(connectionId, ct));

    [HttpGet("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken ct)
    {
        var view = await _projects.GetAsync(projectId, ct);
        return view is null ? NotFound() : Ok(view);
    }

    [HttpPatch("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Update(Guid projectId, UpdateProjectRequest request, CancellationToken ct)
    {
        var result = await _projects.UpdateIntervalAsync(projectId, request.SyncIntervalMinutes, ct);
        return result switch
        {
            null => NotFound(),
            false => Problem(statusCode: 400, title: "Sync interval must be at least 60 minutes."),
            _ => NoContent()
        };
    }
}
