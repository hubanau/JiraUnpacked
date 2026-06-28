using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Tenancy;

public sealed class CurrentTenantStampTests
{
    private sealed class FixedTenant : ICurrentTenant
    {
        public FixedTenant(Guid? id) => EndUserId = id;
        public Guid? EndUserId { get; }
    }

    private static AppDbContext InMemory(ICurrentTenant tenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"stamp-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options, tenant);
    }

    [Fact]
    public async Task SaveChanges_StampsEndUserId_FromCurrentTenant()
    {
        var tenantId = Guid.NewGuid();
        await using var db = InMemory(new FixedTenant(tenantId));

        db.JiraConnections.Add(new JiraConnection
        {
            Id = Guid.NewGuid(), BaseUrl = "https://x.atlassian.net", Email = "a@b.com",
            ApiTokenEncrypted = "t", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var saved = await db.JiraConnections.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(tenantId, saved.EndUserId);
    }

    [Fact]
    public async Task SaveChanges_WithoutTenant_Throws()
    {
        await using var db = InMemory(new FixedTenant(null));
        db.JiraConnections.Add(new JiraConnection
        {
            Id = Guid.NewGuid(), BaseUrl = "https://x.atlassian.net", Email = "a@b.com",
            ApiTokenEncrypted = "t", CreatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
