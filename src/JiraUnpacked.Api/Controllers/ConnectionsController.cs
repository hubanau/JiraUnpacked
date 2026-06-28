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
        var view = await _connections.CreateAsync(request.BaseUrl, request.Email, request.ApiToken, ct);
        return CreatedAtAction(nameof(Get), new { id = view.Id }, ToResponse(view));
    }

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

    private static ConnectionResponse ToResponse(ConnectionView v) =>
        new(v.Id, v.BaseUrl, v.Email, v.CreatedAtUtc);
}
