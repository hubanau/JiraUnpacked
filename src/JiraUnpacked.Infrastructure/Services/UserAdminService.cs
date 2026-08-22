using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class UserAdminService : IUserAdminService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;
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

    public async Task<PagedResult<AdminUserListItem>> ListAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        var p = page < 1 ? 1 : page;
        var size = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        // Users carries no tenant query filter by design: it IS the tenant root,
        // and an Admin is a platform operator acting across tenants. The Admin
        // policy on the controller is therefore the ONLY guard on this read.
        var q = _db.Users;
        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id)
            .Skip((p - 1) * size).Take(size)
            // Project explicitly — never select the entity, which carries PasswordHash.
            .Select(u => new AdminUserListItem(
                u.Id, u.Email, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<AdminUserListItem>(items, p, size, total);
    }
}
