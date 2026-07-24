using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class AccountDeletionService : IAccountDeletionService
{
    private readonly AppDbContext _db;
    private readonly ITenantScope _tenantScope;
    private readonly IClock _clock;

    public AccountDeletionService(AppDbContext db, ITenantScope tenantScope, IClock clock)
    {
        _db = db;
        _tenantScope = tenantScope;
        _clock = clock;
    }

    public async Task<AccountDeletionResult> DeleteAsync(
        Guid userId, Guid requestedBy, CancellationToken ct = default)
    {
        // The tenant scope must be established BEFORE the first database access:
        // TenantConnectionInterceptor sets app.current_tenant when the connection OPENS, so a
        // scope entered later would leave RLS denying the very rows being erased — including
        // when an Admin deletes someone else's account.
        using var _ = _tenantScope.BeginScope(userId);

        // Users is not tenant-owned, so this lookup is unaffected by the scope.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new AccountDeletionResult(AccountDeletionOutcome.NotFound);

        // Admin accounts are config-seeded; the startup seeder would recreate them, so
        // deleting one is meaningless.
        if (user.Role != UserRole.EndUser)
            return new AccountDeletionResult(AccountDeletionOutcome.NotAnEndUser);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        _db.Users.Remove(user);   // ON DELETE CASCADE removes all tenant-owned rows
        _db.DeletedAccounts.Add(new DeletedAccount
        {
            Id = Guid.NewGuid(),
            DeletedUserId = userId,
            RequestedBy = requestedBy,
            DeletedAt = _clock.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new AccountDeletionResult(AccountDeletionOutcome.Deleted);
    }
}
