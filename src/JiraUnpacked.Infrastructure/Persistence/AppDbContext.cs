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
    public DbSet<Status> Statuses => Set<Status>();
    public DbSet<IssueType> IssueTypes => Set<IssueType>();
    public DbSet<Priority> Priorities => Set<Priority>();
    public DbSet<JiraUser> JiraUsers => Set<JiraUser>();
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<RawIssueSnapshot> RawIssueSnapshots => Set<RawIssueSnapshot>();
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<IssueChangeHistory> IssueChangeHistory => Set<IssueChangeHistory>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<RawChangelog> RawChangelogs => Set<RawChangelog>();
    public DbSet<DeletedAccount> DeletedAccounts => Set<DeletedAccount>();

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
        modelBuilder.Entity<Status>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<IssueType>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<Priority>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<JiraUser>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<Sprint>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<Team>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<RawIssueSnapshot>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<Issue>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<IssueChangeHistory>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<SyncRun>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
        modelBuilder.Entity<RawChangelog>().HasQueryFilter(x => x.EndUserId == _currentTenantId);

        // Layer 3 (erasure): every tenant-owned row cascades from its owning User, so account
        // deletion is complete by construction. Applied centrally on purpose — a 17th
        // tenant-owned entity inherits this automatically instead of relying on memory.
        // This complements the Layer 1 filters above; it does not replace them.
        foreach (var entity in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            modelBuilder.Entity(entity.ClrType)
                .HasOne(typeof(User))
                .WithMany()
                .HasForeignKey(nameof(ITenantOwned.EndUserId))
                .OnDelete(DeleteBehavior.Cascade);
        }
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

    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.EndUserId = _tenant.EndUserId
                ?? throw new InvalidOperationException("A current tenant is required to create tenant-owned data.");
        }
    }
}
