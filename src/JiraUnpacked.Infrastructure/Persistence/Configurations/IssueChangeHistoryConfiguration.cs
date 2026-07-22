using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class IssueChangeHistoryConfiguration : IEntityTypeConfiguration<IssueChangeHistory>
{
    public void Configure(EntityTypeBuilder<IssueChangeHistory> b)
    {
        b.ToTable("IssueChangeHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueId).IsRequired().HasMaxLength(64);
        b.Property(x => x.JiraHistoryId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Field).IsRequired().HasMaxLength(256);
        b.Property(x => x.Author).HasMaxLength(256);
        b.Property(x => x.ChangedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId, x.JiraHistoryId, x.Field }).IsUnique();
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraIssueId });
        b.HasIndex(x => new { x.JiraIssueId, x.Field, x.ChangedAt });
        // Serves the FR-E2 project-scoped status-history scan (Field == "status", time-ordered)
        // so metrics reads never table-scan.
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.Field, x.ChangedAt });
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
