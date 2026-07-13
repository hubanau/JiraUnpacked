using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class RawChangelogModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void RawChangelog_is_jsonb_and_unique_per_issue()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.RawChangelog))!;
        Assert.Equal("jsonb", e.FindProperty(nameof(Domain.Entities.RawChangelog.Payload))!.GetColumnType());
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "EndUserId") && i.Properties.Any(p => p.Name == "JiraIssueId"));
    }

    [Fact]
    public void SyncRun_Kind_is_stored_as_string()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.SyncRun))!;
        Assert.Equal(typeof(string), e.FindProperty(nameof(Domain.Entities.SyncRun.Kind))!.GetProviderClrType());
    }
}
