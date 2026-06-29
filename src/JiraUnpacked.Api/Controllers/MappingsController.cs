using JiraUnpacked.Api.Contracts.Mappings;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class MappingsController : ControllerBase
{
    private readonly IProjectMappingService _mappings;
    public MappingsController(IProjectMappingService mappings) => _mappings = mappings;

    [HttpGet("api/projects/{projectId:guid}/jira-statuses")]
    public async Task<IActionResult> Statuses(Guid projectId, CancellationToken ct)
    {
        var statuses = await _mappings.GetStatusesAsync(projectId, ct);
        return statuses is null ? NotFound() : Ok(statuses);
    }

    [HttpPut("api/projects/{projectId:guid}/field-mappings")]
    public async Task<IActionResult> PutFieldMappings(Guid projectId, FieldMappingsRequest request, CancellationToken ct)
    {
        var items = request.Mappings.Select(m => new FieldMappingInput(m.Role, m.JiraFieldId)).ToList();
        var result = await _mappings.ReplaceFieldMappingsAsync(projectId, items, ct);
        return result is null ? NotFound() : NoContent();
    }

    [HttpGet("api/projects/{projectId:guid}/field-mappings")]
    public async Task<IActionResult> GetFieldMappings(Guid projectId, CancellationToken ct)
    {
        var items = await _mappings.GetFieldMappingsAsync(projectId, ct);
        return items is null ? NotFound() : Ok(items);
    }

    [HttpPut("api/projects/{projectId:guid}/status-semantics")]
    public async Task<IActionResult> PutStatusSemantics(Guid projectId, StatusSemanticsRequest request, CancellationToken ct)
    {
        var items = request.Statuses.Select(s => new StatusSemanticInput(s.JiraStatusId, s.StatusName, s.Class)).ToList();
        var result = await _mappings.ReplaceStatusSemanticsAsync(projectId, items, ct);
        return result is null ? NotFound() : NoContent();
    }

    [HttpGet("api/projects/{projectId:guid}/status-semantics")]
    public async Task<IActionResult> GetStatusSemantics(Guid projectId, CancellationToken ct)
    {
        var items = await _mappings.GetStatusSemanticsAsync(projectId, ct);
        return items is null ? NotFound() : Ok(items);
    }
}
