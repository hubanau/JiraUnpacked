using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class SemanticLayerModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Model_includes_the_four_semantic_entities()
    {
        using var ctx = Ctx();
        var names = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet();
        Assert.Contains(nameof(Domain.Entities.JiraProject), names);
        Assert.Contains(nameof(Domain.Entities.JiraQuery), names);
        Assert.Contains(nameof(Domain.Entities.FieldMapping), names);
        Assert.Contains(nameof(Domain.Entities.StatusSemantic), names);
    }

    [Fact]
    public void FieldMapping_is_unique_per_project_and_role()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.FieldMapping))!;
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == nameof(Domain.Entities.FieldMapping.JiraProjectId))
            && i.Properties.Any(p => p.Name == nameof(Domain.Entities.FieldMapping.Role)));
    }

    [Fact]
    public void Enums_are_stored_as_strings()
    {
        using var ctx = Ctx();
        var fm = ctx.Model.FindEntityType(typeof(Domain.Entities.FieldMapping))!;
        Assert.Equal(typeof(string), fm.FindProperty(nameof(Domain.Entities.FieldMapping.Role))!.GetProviderClrType());
        var ss = ctx.Model.FindEntityType(typeof(Domain.Entities.StatusSemantic))!;
        Assert.Equal(typeof(string), ss.FindProperty(nameof(Domain.Entities.StatusSemantic.Class))!.GetProviderClrType());
    }
}
