using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class UserAdminService : IUserAdminService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public UserAdminService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<BlockResult> BlockAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return BlockResult.Fail(BlockError.NotFound);
        }
        if (user.Role != UserRole.EndUser)
        {
            return BlockResult.Fail(BlockError.NotAnEndUser);
        }

        var now = _clock.UtcNow;
        user.Status = UserStatus.Blocked;

        // Revoke active refresh tokens so existing sessions can't be refreshed.
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        await _db.SaveChangesAsync(ct);
        return BlockResult.Ok();
    }

    public async Task<BlockResult> UnblockAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return BlockResult.Fail(BlockError.NotFound);
        }
        if (user.Role != UserRole.EndUser)
        {
            return BlockResult.Fail(BlockError.NotAnEndUser);
        }

        // Restore access; previously revoked refresh tokens stay revoked (the user logs in again).
        user.Status = UserStatus.Active;
        await _db.SaveChangesAsync(ct);
        return BlockResult.Ok();
    }
}
