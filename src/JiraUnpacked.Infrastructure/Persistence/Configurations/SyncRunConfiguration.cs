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
        b.Property(x => x.Error); // unlimited text
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.StartedAt });
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
