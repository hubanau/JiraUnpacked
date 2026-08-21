// src/JiraUnpacked.Api/Controllers/FilterCatalogController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

/// <summary>
/// The filter-field catalog is static and tenant-independent: every signed-in
/// user builds filters over the same allow-list, so this route carries no
/// project id and is not admin-only — unlike the project- or
/// connection-scoped controllers alongside it.
/// </summary>
[ApiController]
[Authorize]
public sealed class FilterCatalogController : ControllerBase
{
    private readonly IFilterCatalogService _catalog;
    public FilterCatalogController(IFilterCatalogService catalog) => _catalog = catalog;

    [HttpGet("api/filter-catalog")]
    public IActionResult Get() => Ok(_catalog.Get());
}
