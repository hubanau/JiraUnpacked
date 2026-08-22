using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting;
using Microsoft.Extensions.Caching.Memory;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// A cache over tenant data is the one place a cross-tenant leak would be easy to introduce, so
/// the tenant half of the key is tested first and hardest. The rest of the file pins the
/// property that makes caching a report defensible at all: an answer that could have changed
/// lands on a different key.
/// </summary>
public sealed class ReportCacheTests
{
    private sealed class FakeTenant : ICurrentTenant
    {
        public Guid? EndUserId { get; set; }
    }

    private static (ReportCache Cache, FakeTenant Tenant) Build()
    {
        var tenant = new FakeTenant();
        var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = ReportCache.MaxEntries });
        return (new ReportCache(tenant, memory), tenant);
    }

    private sealed record Payload(string Value);

    [Fact]
    public async Task Two_tenants_asking_the_same_question_never_share_an_answer()
    {
        var (cache, tenant) = Build();
        var projectId = Guid.NewGuid();

        tenant.EndUserId = Guid.NewGuid();
        var a = await cache.GetOrCreateAsync(
            "r", projectId, "same-fingerprint", _ => Task.FromResult(new Payload("tenant-a")), default);

        tenant.EndUserId = Guid.NewGuid();
        var b = await cache.GetOrCreateAsync(
            "r", projectId, "same-fingerprint", _ => Task.FromResult(new Payload("tenant-b")), default);

        // Same region, same project id, same fingerprint — everything a caller controls is
        // identical. Only the tenant differs, and the tenant comes from ICurrentTenant inside
        // the cache, so a caller cannot forget it.
        Assert.Equal("tenant-a", a.Value);
        Assert.Equal("tenant-b", b.Value);
    }

    [Fact]
    public async Task Nothing_is_cached_when_there_is_no_current_tenant()
    {
        var (cache, tenant) = Build();
        tenant.EndUserId = null;
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            await cache.GetOrCreateAsync("r", Guid.Empty, "f", _ =>
            {
                calls++;
                return Task.FromResult(new Payload("x"));
            }, default);
        }

        // Refusing outright is the only way to guarantee every stored key has a tenant.
        // Background jobs and unauthenticated paths simply compute.
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_repeat_question_from_the_same_tenant_is_answered_once()
    {
        var (cache, tenant) = Build();
        tenant.EndUserId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var calls = 0;

        Task<Payload> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(new Payload("v"));
        }

        await cache.GetOrCreateAsync("r", projectId, "f", Factory, default);
        await cache.GetOrCreateAsync("r", projectId, "f", Factory, default);
        await cache.GetOrCreateAsync("r", projectId, "f", Factory, default);

        // This is the whole point: the metrics page asks three times per view.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_changed_fingerprint_is_a_different_answer()
    {
        var (cache, tenant) = Build();
        tenant.EndUserId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var before = await cache.GetOrCreateAsync(
            "r", projectId, "watermark-1", _ => Task.FromResult(new Payload("old")), default);
        var after = await cache.GetOrCreateAsync(
            "r", projectId, "watermark-2", _ => Task.FromResult(new Payload("new")), default);

        // The fingerprint carries the sync watermark and the semantics map, so a sync, a
        // rederive or a semantics edit all land here — as a miss, not as a stale hit.
        Assert.Equal("old", before.Value);
        Assert.Equal("new", after.Value);
    }

    [Fact]
    public async Task Different_projects_and_regions_do_not_collide()
    {
        var (cache, tenant) = Build();
        tenant.EndUserId = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        var a = await cache.GetOrCreateAsync("r", projectA, "f", _ => Task.FromResult(new Payload("a")), default);
        var b = await cache.GetOrCreateAsync("r", projectB, "f", _ => Task.FromResult(new Payload("b")), default);
        var other = await cache.GetOrCreateAsync(
            "other-region", projectA, "f", _ => Task.FromResult(new Payload("c")), default);

        Assert.Equal("a", a.Value);
        Assert.Equal("b", b.Value);
        Assert.Equal("c", other.Value);
    }

    [Fact]
    public void The_cache_is_bounded_so_a_fold_per_project_cannot_grow_without_limit()
    {
        // Entries are whole-project folds. Bounded by count rather than bytes, and kept small
        // because the job is collapsing one page view's fan-out, not holding a working set.
        Assert.Equal(64, ReportCache.MaxEntries);
        Assert.True(ReportCache.Ttl <= TimeSpan.FromMinutes(1));
    }
}
