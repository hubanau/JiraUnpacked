// src/JiraUnpacked.Api/Controllers/ChecksController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class ChecksController : ControllerBase
{
    private readonly IGapCheckService _checks;
    public ChecksController(IGapCheckService checks) => _checks = checks;

    [HttpGet("api/projects/{projectId:guid}/checks")]
    public async Task<IActionResult> Catalog(Guid projectId, CancellationToken ct = default)
    {
        var catalog = await _checks.GetCatalogAsync(projectId, ct);
        return catalog is null ? NotFound() : Ok(catalog);
    }

    [HttpGet("api/projects/{projectId:guid}/checks/{checkId}")]
    public async Task<IActionResult> Run(
        Guid projectId, string checkId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _checks.RunAsync(projectId, checkId, page, pageSize, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
