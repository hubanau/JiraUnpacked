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
        CancellationToken ct = default) =>
        Respond(await _checks.RunAsync(projectId, checkId, page, pageSize, null, ct));

    /// <summary>
    /// The same check, restricted to the issues the filter selects. The filter chooses which
    /// issues are EXAMINED — it never narrows the evidence, so an epic reported here still
    /// carries every one of its children, filtered or not.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/checks/{checkId}")]
    public async Task<IActionResult> RunFiltered(
        Guid projectId, string checkId, PagedFilterRequest request, CancellationToken ct = default) =>
        Respond(await _checks.RunAsync(
            projectId, checkId, request.Page, request.PageSize, request.ToFilter(), ct));

    /// <summary>Null Report + null Error => 404; non-null Error => 400; otherwise 200.</summary>
    private IActionResult Respond<T>(ReportResult<T> result) where T : class =>
        result.Error is not null
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error)
            : result.Report is null ? NotFound() : Ok(result.Report);
}
