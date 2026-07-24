// tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Tenancy;

/// <summary>
/// The deletion-completeness gate, sibling to the FR-B2 isolation gate. Erasure is a legal
/// obligation, so completeness cannot rest on someone remembering to wire a new table.
/// </summary>
public sealed class TenantDeletionGateTests
{
    /// <summary>Builds the model without touching a database — Model is constructed lazily from configuration.</summary>
    private static AppDbContext Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Every_tenant_owned_entity_cascades_from_user()
    {
        using var db = Model();

        var offenders = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            var fk = entity.GetForeignKeys().FirstOrDefault(f =>
                f.PrincipalEntityType.ClrType == typeof(User)
                && f.Properties.Count == 1
                && f.Properties[0].Name == nameof(ITenantOwned.EndUserId));

            if (fk is null)
                offenders.Add($"{entity.ClrType.Name}: no FK from EndUserId to User");
            else if (fk.DeleteBehavior != DeleteBehavior.Cascade)
                offenders.Add($"{entity.ClrType.Name}: delete behaviour is {fk.DeleteBehavior}, expected Cascade");
        }

        // A new tenant-owned table added without cascade wiring fails HERE, in CI,
        // instead of silently leaving personal data behind after an erasure request.
        Assert.Empty(offenders);
    }

    [Fact]
    public void Gate_actually_examines_every_tenant_owned_entity()
    {
        // Guards the guard: if the reflection predicate ever stops matching, the test above
        // would pass vacuously over an empty set.
        using var db = Model();

        var count = db.Model.GetEntityTypes()
            .Count(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType));

        Assert.Equal(16, count);
    }

    [Fact]
    public void Invite_code_redeemer_link_does_not_block_deletion()
    {
        using var db = Model();

        var fk = db.Model.FindEntityType(typeof(InviteCode))!
            .GetForeignKeys()
            .Single(f => f.Properties.Count == 1
                         && f.Properties[0].Name == nameof(InviteCode.UsedByUserId));

        // Restrict here would make every End User who registered via a code undeletable.
        Assert.Equal(DeleteBehavior.SetNull, fk.DeleteBehavior);
    }
}
