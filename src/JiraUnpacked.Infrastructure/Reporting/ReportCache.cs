// src/JiraUnpacked.Infrastructure/Reporting/ReportCache.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// Memoises the expensive per-project loads the reports share.
///
/// Why this exists: computing any timeline metric materialises every issue AND every status
/// transition in the project. The metrics page fires four sections at once, three of which need
/// that same fold, so one page view did the same unbounded read three times.
///
/// Why it is safe: the cache key is built HERE from <see cref="ICurrentTenant"/>, never from a
/// caller-supplied argument, so a caller cannot forget to scope it — and with no current tenant
/// nothing is cached at all. A cache over tenant data is the one place a cross-tenant leak
/// would be easy to introduce, so the tenant is structural rather than conventional.
///
/// Why it cannot serve stale numbers: the caller's fingerprint carries everything an answer
/// depends on — the sync watermark and the status-semantics map. A sync, a rederive or a
/// semantics edit all change the fingerprint, which is a different key, which is a miss. The
/// short absolute expiry is a backstop for a dependency someone forgets to fingerprint, not the
/// correctness mechanism.
/// </summary>
public interface IReportCache
{
    Task<T> GetOrCreateAsync<T>(
        string region, Guid projectId, string fingerprint,
        Func<CancellationToken, Task<T>> factory, CancellationToken ct) where T : class;
}

public sealed class ReportCache : IReportCache, IDisposable
{
    /// <summary>
    /// Entries are whole-project folds, so this is bounded by COUNT rather than bytes and kept
    /// small deliberately: the cache exists to collapse one page view's fan-out, not to hold a
    /// working set.
    /// </summary>
    public const int MaxEntries = 64;

    /// <summary>
    /// A backstop only — the fingerprint is what makes an answer current. Short enough that a
    /// dependency nobody thought to fingerprint self-corrects within a page refresh.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly IMemoryCache _cache;
    private readonly ICurrentTenant _tenant;
    private readonly bool _ownsCache;

    public ReportCache(ICurrentTenant tenant)
        : this(tenant, new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries }), ownsCache: true)
    {
    }

    /// <summary>Composition overload: lets a caller supply the backing store (used by tests).</summary>
    public ReportCache(ICurrentTenant tenant, IMemoryCache cache, bool ownsCache = false)
    {
        _tenant = tenant;
        _cache = cache;
        _ownsCache = ownsCache;
    }

    public async Task<T> GetOrCreateAsync<T>(
        string region, Guid projectId, string fingerprint,
        Func<CancellationToken, Task<T>> factory, CancellationToken ct) where T : class
    {
        var tenant = _tenant.EndUserId;

        // No tenant, no cache. An entry that is not tenant-keyed is a cross-tenant leak, and
        // refusing outright is the only way to be sure the key always has one — background jobs
        // and unauthenticated paths simply compute.
        if (tenant is null) return await factory(ct);

        var key = (region, tenant.Value, projectId, fingerprint);

        if (_cache.TryGetValue(key, out T? hit) && hit is not null) return hit;

        var value = await factory(ct);

        _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Ttl,
            Size = 1,
        });

        return value;
    }

    public void Dispose()
    {
        if (_ownsCache) (_cache as IDisposable)?.Dispose();
    }
}
