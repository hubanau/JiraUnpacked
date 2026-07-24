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

/// <summary>
/// Data-level half of the gate: proves the cascade actually fires end-to-end, and — just as
/// important — that it does not over-reach into another tenant.
/// </summary>
public sealed class TenantDeletionDataGateTests : IClassFixture<Integration.JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly Integration.JiraUnpackedApiFactory _factory;
    public TenantDeletionDataGateTests(Integration.JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Integration.JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public async Task Deleting_an_account_erases_every_seeded_tenant_row()
    {
        var tenant = await TenantDeletionTestData.SeedUserAsync(_factory);
        var seed = await TenantDeletionTestData.SeedFullGraphAsync(tenant);

        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenant, tenant);

        await using var db = Privileged();
        foreach (var (table, count) in await TenantDeletionTestData.CountsForTenantAsync(db, tenant))
            Assert.True(count == 0, $"{table} still holds {count} row(s) after erasure");
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == tenant));
    }

    [Fact]
    public async Task Deleting_one_account_leaves_another_tenants_rows_untouched()
    {
        var tenantA = await TenantDeletionTestData.SeedUserAsync(_factory);
        await TenantDeletionTestData.SeedFullGraphAsync(tenantA);
        var tenantB = await TenantDeletionTestData.SeedUserAsync(_factory);
        await TenantDeletionTestData.SeedFullGraphAsync(tenantB);

        await using (var before = Privileged())
        {
            var b = await TenantDeletionTestData.CountsForTenantAsync(before, tenantB);
            Assert.All(b, x => Assert.True(x.Count > 0));   // B genuinely has data to lose
        }

        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenantA, tenantA);

        await using var after = Privileged();
        foreach (var (table, count) in await TenantDeletionTestData.CountsForTenantAsync(after, tenantB))
            Assert.True(count > 0, $"tenant B lost rows in {table} — the cascade over-reached");
        Assert.True(await after.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == tenantB));
    }

    [Fact]
    public async Task A_user_who_redeemed_an_invite_code_is_deletable_and_the_code_survives()
    {
        var tenant = await TenantDeletionTestData.SeedUserAsync(_factory);
        var codeId = await TenantDeletionTestData.SeedRedeemedInviteCodeAsync(tenant);

        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenant, tenant);

        await using var db = Privileged();
        var code = await db.InviteCodes.IgnoreQueryFilters().SingleAsync(c => c.Id == codeId);
        Assert.Null(code.UsedByUserId);   // audit fact retained, personal link severed
    }

    [Fact]
    public async Task Tombstone_records_the_erasure_without_personal_data()
    {
        var tenant = await TenantDeletionTestData.SeedUserAsync(_factory);
        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenant, tenant);

        await using var db = Privileged();
        var tomb = await db.DeletedAccounts.SingleAsync(t => t.DeletedUserId == tenant);
        Assert.Equal(tenant, tomb.RequestedBy);          // self-service
        Assert.NotEqual(default, tomb.DeletedAt);
        // The entity carries only ids and a timestamp — there is no field that could hold
        // an email or username, which is the property being asserted.
        Assert.Equal(4, typeof(DeletedAccount).GetProperties().Length);
    }
}
