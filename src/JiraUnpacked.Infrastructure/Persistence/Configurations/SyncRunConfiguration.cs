using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> b)
    {
        b.ToTable("SyncRuns");
        b.HasKey(x => x.Id);
        b.Property(x => x.StartedAt).IsRequired();
        b.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Kind).IsRequired().HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Error); // unlimited text
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.StartedAt });

        // At most one Running run per project — enforces the AlreadyRunning invariant at the DB
        // level, closing the check-then-insert race. Status is stored as a string (HasConversion),
        // so the filter compares the converted value 'Running'.
        b.HasIndex(x => x.JiraProjectId)
            .HasFilter("\"Status\" = 'Running'")
            .IsUnique()
            .HasDatabaseName("IX_SyncRuns_OneRunningPerProject");

        // Restores coverage the FK-convention index provided before the partial index above
        // superseded it: DueProjectFinder.LastSuccess runs privileged (no EndUserId), filtering
        // JiraProjectId + Status='Succeeded', on every scheduler poll.
        b.HasIndex(x => new { x.JiraProjectId, x.Status });

        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
