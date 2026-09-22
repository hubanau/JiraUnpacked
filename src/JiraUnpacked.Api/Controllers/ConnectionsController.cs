using JiraUnpacked.Api.Contracts.Connections;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/connections")]
[Authorize]
public sealed class ConnectionsController : ControllerBase
{
    private readonly IJiraConnectionService _connections;

    public ConnectionsController(IJiraConnectionService connections) => _connections = connections;

    [HttpPost]
    public async Task<IActionResult> Create(CreateConnectionRequest request, CancellationToken ct)
    {
        var result = await _connections.CreateAsync(request.BaseUrl, request.Email, request.ApiToken, ct);
        return result.Status switch
        {
            CreateConnectionStatus.Created =>
                CreatedAtAction(nameof(Get), new { id = result.View!.Id }, ToResponse(result.View)),
            CreateConnectionStatus.InvalidBaseUrl =>
                Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error),
            CreateConnectionStatus.InvalidCredentials => InvalidCredentials(),
            _ => JiraUnreachable()
        };
    }

    /// <summary>
    /// Rotates the API token (and optionally the email). The new credentials are checked against
    /// Jira before anything is stored, with the same answers as create: 400 for credentials Jira
    /// refuses, 502 when Jira cannot be reached. The token is never echoed back.
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateCredentials(Guid id, UpdateConnectionRequest request, CancellationToken ct)
    {
        var result = await _connections.UpdateCredentialsAsync(id, request.Email, request.ApiToken, ct);
        return result.Status switch
        {
            UpdateConnectionStatus.Updated => Ok(ToResponse(result.View!)),
            UpdateConnectionStatus.NotFound => NotFound(),
            UpdateConnectionStatus.InvalidBaseUrl =>
                Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error),
            UpdateConnectionStatus.InvalidCredentials => InvalidCredentials(),
            _ => JiraUnreachable()
        };
    }

    /// <summary>
    /// Deletes the connection with every project under it and all of their data. 409 while any of
    /// those projects is syncing — stop the run first.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await _connections.DeleteAsync(id, ct) switch
        {
            DeleteOutcome.Deleted => NoContent(),
            DeleteOutcome.SyncRunning => Problem(statusCode: StatusCodes.Status409Conflict,
                title: "A sync is running for one of this connection's projects. Stop it or wait for it to finish, then try again."),
            _ => NotFound()
        };

    private ObjectResult InvalidCredentials() =>
        Problem(statusCode: StatusCodes.Status400BadRequest, title: "Jira credentials could not be validated.");

    private ObjectResult JiraUnreachable() =>
        Problem(statusCode: StatusCodes.Status502BadGateway, title: "Jira could not be reached.");

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var views = await _connections.ListAsync(ct);
        return Ok(views.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var view = await _connections.GetAsync(id, ct);
        return view is null ? NotFound() : Ok(ToResponse(view));
    }

    [HttpGet("{id:guid}/fields")]
    public async Task<IActionResult> Fields(Guid id, CancellationToken ct)
    {
        var fields = await _connections.GetFieldsAsync(id, ct);
        return fields is null ? NotFound() : Ok(fields);
    }

    [HttpGet("{id:guid}/projects-available")]
    public async Task<IActionResult> AvailableProjects(Guid id, CancellationToken ct)
    {
        var projects = await _connections.GetAvailableProjectsAsync(id, ct);
        return projects is null ? NotFound() : Ok(projects);
    }

    private static ConnectionResponse ToResponse(ConnectionView v) =>
        new(v.Id, v.BaseUrl, v.Email, v.CreatedAtUtc);
}
