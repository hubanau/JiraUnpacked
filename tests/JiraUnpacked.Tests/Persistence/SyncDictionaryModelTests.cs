using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class SyncDictionaryModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Model_includes_the_six_dictionaries()
    {
        using var ctx = Ctx();
        var names = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet();
        foreach (var n in new[] { "Status", "IssueType", "Priority", "JiraUser", "Sprint", "Team" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void Per_connection_dictionaries_are_unique_by_connection_and_jira_id()
    {
        using var ctx = Ctx();
        var status = ctx.Model.FindEntityType(typeof(Domain.Entities.Status))!;
        Assert.Contains(status.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "JiraConnectionId")
            && i.Properties.Any(p => p.Name == "JiraStatusId"));
    }

    [Fact]
    public void Per_project_dictionaries_are_unique_by_project_and_jira_id()
    {
        using var ctx = Ctx();
        var sprint = ctx.Model.FindEntityType(typeof(Domain.Entities.Sprint))!;
        Assert.Contains(sprint.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "JiraProjectId")
            && i.Properties.Any(p => p.Name == "JiraSprintId"));
    }
}
