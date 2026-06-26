using JiraUnpacked.Api.Contracts.Admin;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/admin/invite-codes")]
[Authorize(Policy = "Admin")]
public sealed class InviteCodesController : ControllerBase
{
    private readonly IInviteCodeService _inviteCodes;

    public InviteCodesController(IInviteCodeService inviteCodes) => _inviteCodes = inviteCodes;

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var adminId = Guid.Parse(User.FindFirst("sub")!.Value);
        var result = await _inviteCodes.CreateAsync(adminId, ct);
        return Ok(new InviteCodeResponse(result.Code, result.ExpiresAtUtc));
    }
}
