// src/JiraUnpacked.Api/Controllers/SavedFiltersController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

/// <summary>
/// Named per-project filter definitions. Storage only — no route here runs a filter. A client
/// reads a definition and posts it to /issues/query or a report endpoint exactly as it posts an
/// ad-hoc one, so there is still exactly one path from filter input to predicate.
/// </summary>
[ApiController]
[Authorize]
public sealed class SavedFiltersController : ControllerBase
{
    private readonly ISavedFilterService _filters;
    public SavedFiltersController(ISavedFilterService filters) => _filters = filters;

    [HttpGet("api/projects/{projectId:guid}/filters")]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct = default)
    {
        var result = await _filters.ListAsync(projectId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("api/projects/{projectId:guid}/filters/{filterId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid filterId, CancellationToken ct = default)
    {
        var result = await _filters.GetAsync(projectId, filterId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("api/projects/{projectId:guid}/filters")]
    public async Task<IActionResult> Create(
        Guid projectId, SaveFilterRequest request, CancellationToken ct = default)
    {
        var result = await _filters.CreateAsync(projectId, request, ct);
        return result.Error switch
        {
            SaveFilterError.None => CreatedAtAction(
                nameof(Get), new { projectId, filterId = result.Filter!.Id }, result.Filter),
            _ => Respond(result),
        };
    }

    [HttpPut("api/projects/{projectId:guid}/filters/{filterId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId, Guid filterId, SaveFilterRequest request, CancellationToken ct = default)
    {
        var result = await _filters.UpdateAsync(projectId, filterId, request, ct);
        return result.Error == SaveFilterError.None ? Ok(result.Filter) : Respond(result);
    }

    [HttpDelete("api/projects/{projectId:guid}/filters/{filterId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid filterId, CancellationToken ct = default)
    {
        var deleted = await _filters.DeleteAsync(projectId, filterId, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// NotFound covers both an unknown project and an unknown filter id: distinguishing them
    /// would leak which ids exist in another tenant.
    /// </summary>
    private IActionResult Respond(SaveFilterResult result) => result.Error switch
    {
        SaveFilterError.NotFound => NotFound(),
        SaveFilterError.Invalid => Problem(
            statusCode: StatusCodes.Status400BadRequest, title: result.Message),
        SaveFilterError.Conflict => Problem(
            statusCode: StatusCodes.Status409Conflict, title: result.Message),
        _ => Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}
