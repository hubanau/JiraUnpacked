// src/JiraUnpacked.Api/Controllers/IssueTypesController.cs
using JiraUnpacked.Api.Contracts.Mappings;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

/// <summary>
/// Issue-type classification is CONNECTION-scoped, unlike the project-scoped mapping routes:
/// IssueTypes are keyed by JiraConnectionId and shared by every project on that connection,
/// so a project-scoped route would misrepresent the blast radius.
/// </summary>
[ApiController]
[Authorize]
public sealed class IssueTypesController : ControllerBase
{
    private readonly IIssueTypeClassService _issueTypes;
    public IssueTypesController(IIssueTypeClassService issueTypes) => _issueTypes = issueTypes;

    [HttpGet("api/connections/{connectionId:guid}/issue-types")]
    public async Task<IActionResult> Get(Guid connectionId, CancellationToken ct = default)
    {
        var items = await _issueTypes.GetAsync(connectionId, ct);
        return items is null ? NotFound() : Ok(items);
    }

    [HttpPut("api/connections/{connectionId:guid}/issue-types")]
    public async Task<IActionResult> Put(
        Guid connectionId, IssueTypeClassesRequest request, CancellationToken ct = default)
    {
        var items = request.IssueTypes
            .Select(i => new IssueTypeClassInput(i.JiraIssueTypeId, i.Class)).ToList();

        var result = await _issueTypes.SetAsync(connectionId, items, ct);
        return result.Outcome switch
        {
            SetIssueTypeClassesOutcome.ConnectionNotFound => NotFound(),
            SetIssueTypeClassesOutcome.UnknownIssueTypeId =>
                BadRequest(new { error = $"Unknown jiraIssueTypeId: {result.UnknownJiraIssueTypeId}" }),
            _ => NoContent(),
        };
    }
}
