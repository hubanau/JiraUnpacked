using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "Admin")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly IUserAdminService _users;
    private readonly IAccountDeletionService _deletion;

    public AdminUsersController(IUserAdminService users, IAccountDeletionService deletion)
    {
        _users = users;
        _deletion = deletion;
    }

    [HttpPost("{id:guid}/block")]
    public async Task<IActionResult> Block(Guid id, CancellationToken ct)
        => Map(await _users.BlockAsync(id, ct));

    [HttpPost("{id:guid}/unblock")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
        => Map(await _users.UnblockAsync(id, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var actor = Guid.TryParse(User.FindFirst("sub")?.Value, out var s) ? s : Guid.Empty;
        var result = await _deletion.DeleteAsync(id, actor, ct);
        return result.Outcome switch
        {
            AccountDeletionOutcome.Deleted => NoContent(),
            AccountDeletionOutcome.NotFound => Problem(
                statusCode: StatusCodes.Status404NotFound, title: "User not found."),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Only End Users can be deleted."),
        };
    }

    private IActionResult Map(BlockResult result) => result.Error switch
    {
        BlockError.None => NoContent(),
        BlockError.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "User not found."),
        BlockError.NotAnEndUser => Problem(
            statusCode: StatusCodes.Status400BadRequest, title: "Only End Users can be blocked or unblocked."),
        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
}
