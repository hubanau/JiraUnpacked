// src/JiraUnpacked.Infrastructure/Security/TokenEnvelopeUpgrader.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JiraUnpacked.Infrastructure.Security;

/// <summary>
/// Rewrites Jira tokens still stored in the pre-envelope format, so the old format stops
/// accumulating rows rather than being carried forever.
///
/// Follows SyncRunRetentionJob's shape exactly: the privileged context is a cross-tenant
/// READER of ids only, and every write happens inside the owning tenant's scope, under RLS.
/// IPrivilegedDbContextFactory documents that it must never write, and that holds here.
///
/// Idempotent and re-entrant: a row that is already current is skipped, and two instances
/// racing on the same row both produce a valid envelope, so a lost race costs nothing.
/// </summary>
public sealed class TokenEnvelopeUpgrader
{
    private readonly IPrivilegedDbContextFactory _privileged;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TokenEnvelopeUpgrader> _logger;

    public TokenEnvelopeUpgrader(
        IPrivilegedDbContextFactory privileged, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, ILogger<TokenEnvelopeUpgrader> logger)
    {
        _privileged = privileged;
        _tenantScope = tenantScope;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Returns how many rows were rewritten.</summary>
    public async Task<int> UpgradeAsync(CancellationToken ct = default)
    {
        // Ids and owners only — never the token itself through the privileged context.
        List<(Guid Id, Guid EndUserId)> stale;
        await using (var db = _privileged.Create())
        {
            var rows = await db.JiraConnections.IgnoreQueryFilters()
                .Where(c => !c.ApiTokenEncrypted.StartsWith(AesGcmTokenProtector.EnvelopePrefix))
                .Select(c => new { c.Id, c.EndUserId })
                .ToListAsync(ct);
            stale = rows.Select(x => (x.Id, x.EndUserId)).ToList();
        }

        if (stale.Count == 0) return 0;

        var upgraded = 0;
        foreach (var (id, endUserId) in stale)
        {
            try
            {
                using (_tenantScope.BeginScope(endUserId))
                using (var scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();

                    var row = await db.JiraConnections.FirstOrDefaultAsync(c => c.Id == id, ct);

                    // Gone, or already rewritten by another instance between the scan and now.
                    if (row is null || !AesGcmTokenProtector.IsLegacy(row.ApiTokenEncrypted)) continue;

                    row.ApiTokenEncrypted = protector.Protect(protector.Unprotect(row.ApiTokenEncrypted));
                    await db.SaveChangesAsync(ct);
                    upgraded++;
                }
            }
            catch (Exception ex)
            {
                // Never the token, never the exception message — a decrypt failure can echo
                // key or ciphertext detail, and this runs at start-up where logs are widest.
                // One bad row must not stop the rest, or block the application from serving.
                _logger.LogError(
                    "Could not upgrade the token envelope for connection {ConnectionId} ({ExceptionType}).",
                    id, ex.GetType().Name);
            }
        }

        if (upgraded > 0)
            _logger.LogInformation("Upgraded {Count} Jira token(s) to the envelope format.", upgraded);

        return upgraded;
    }
}
