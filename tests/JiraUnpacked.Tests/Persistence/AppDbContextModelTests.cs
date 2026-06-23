using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

/// <summary>
/// Validates the EF Core model (keys, indexes, conversions, relationships)
/// without a database connection — accessing <c>Model</c> forces model
/// building. DB-backed integration tests arrive with the Phase 3 tenancy gate.
/// </summary>
public class AppDbContextModelTests
{
    private static AppDbContext CreateContext()
    {
        // A valid-looking connection string is enough; nothing connects here.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p")
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void Model_Includes_All_Identity_Entities()
    {
        using var ctx = CreateContext();

        var entities = ctx.Model.GetEntityTypes()
            .Select(e => e.ClrType.Name)
            .ToHashSet();

        Assert.Contains(nameof(Domain.Entities.User), entities);
        Assert.Contains(nameof(Domain.Entities.InviteCode), entities);
        Assert.Contains(nameof(Domain.Entities.EmailVerification), entities);
        Assert.Contains(nameof(Domain.Entities.RefreshToken), entities);
    }

    [Fact]
    public void User_Email_And_Username_Are_Unique()
    {
        using var ctx = CreateContext();
        var user = ctx.Model.FindEntityType(typeof(Domain.Entities.User))!;

        Assert.Contains(user.GetIndexes(),
            i => i.IsUnique && i.Properties.Any(p => p.Name == nameof(Domain.Entities.User.Email)));
        Assert.Contains(user.GetIndexes(),
            i => i.IsUnique && i.Properties.Any(p => p.Name == nameof(Domain.Entities.User.Username)));
    }

    [Fact]
    public void User_Role_And_Status_Are_Stored_As_Strings()
    {
        using var ctx = CreateContext();
        var user = ctx.Model.FindEntityType(typeof(Domain.Entities.User))!;

        Assert.Equal(typeof(string),
            user.FindProperty(nameof(Domain.Entities.User.Role))!.GetProviderClrType());
        Assert.Equal(typeof(string),
            user.FindProperty(nameof(Domain.Entities.User.Status))!.GetProviderClrType());
    }

    [Fact]
    public void Model_Includes_RefreshToken_With_Unique_TokenHash()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(Domain.Entities.RefreshToken))!;

        Assert.NotNull(entity);
        Assert.Contains(entity.GetIndexes(),
            i => i.IsUnique && i.Properties.Any(p => p.Name == nameof(Domain.Entities.RefreshToken.TokenHash)));
    }
}
