using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class SyncCoreModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Model_includes_the_core_tables()
    {
        using var ctx = Ctx();
        var names = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet();
        foreach (var n in new[] { "RawIssueSnapshot", "Issue", "IssueChangeHistory", "SyncRun" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void RawIssueSnapshot_payload_is_jsonb_and_unique_per_issue()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.RawIssueSnapshot))!;
        Assert.Equal("jsonb", e.FindProperty(nameof(Domain.Entities.RawIssueSnapshot.Payload))!.GetColumnType());
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "EndUserId")
            && i.Properties.Any(p => p.Name == "JiraIssueId"));
    }

    [Fact]
    public void Issue_is_unique_per_issue_and_has_a_self_parent_fk()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.Issue))!;
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "EndUserId")
            && i.Properties.Any(p => p.Name == "JiraIssueId"));
        Assert.Contains(e.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Domain.Entities.Issue));
    }

    [Fact]
    public void SyncRun_status_is_stored_as_string()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.SyncRun))!;
        Assert.Equal(typeof(string), e.FindProperty(nameof(Domain.Entities.SyncRun.Status))!.GetProviderClrType());
    }

    [Fact]
    public void ChangeHistory_is_unique_by_issue_history_and_field()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.IssueChangeHistory))!;
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "JiraHistoryId")
            && i.Properties.Any(p => p.Name == "Field"));
    }
}
