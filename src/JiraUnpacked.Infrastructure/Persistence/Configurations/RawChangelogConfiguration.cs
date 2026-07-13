using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class RawChangelogConfiguration : IEntityTypeConfiguration<RawChangelog>
{
    public void Configure(EntityTypeBuilder<RawChangelog> b)
    {
        b.ToTable("RawChangelog");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Payload).IsRequired().HasColumnType("jsonb");
        b.Property(x => x.FetchedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
