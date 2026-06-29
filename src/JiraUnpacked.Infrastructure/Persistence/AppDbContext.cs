using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    private readonly ICurrentTenant _tenant;
    private readonly Guid _currentTenantId;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant tenant) : base(options)
    {
        _tenant = tenant;
        _currentTenantId = tenant.EndUserId ?? Guid.Empty;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<EmailVerification> EmailVerifications => Set<EmailVerification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<JiraConnection> JiraConnections => Set<JiraConnection>();
    public DbSet<JiraProject> JiraProjects => Set<JiraProject>();
    public DbSet<JiraQuery> JiraQueries => Set<JiraQuery>();
    public DbSet<FieldMapping> FieldMappings => Set<FieldMapping>();
    public DbSet<StatusSemantic> StatusSemantics => Set<StatusSemantic>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Layer 1 (primary): central tenant filter on every tenant-owned entity.
        // No current tenant => _currentTenantId is Guid.Empty, which matches no real row.
        modelBuilder.Entity<JiraConnection>().HasQueryFilter(c => c.EndUserId == _currentTenantId);
        modelBuilder.Entity<JiraProject>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<JiraQuery>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<FieldMapping>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<StatusSemantic>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
    }

    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    // Auto-stamp EndUserId on new tenant-owned rows so a caller can never create a
    // row owned by another tenant; writing one with no current tenant fails closed.
    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.EndUserId = _tenant.EndUserId
                ?? throw new InvalidOperationException("A current tenant is required to create tenant-owned data.");
        }
    }
}
