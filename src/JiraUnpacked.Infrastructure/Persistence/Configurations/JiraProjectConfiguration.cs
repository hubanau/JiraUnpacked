using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraProjectConfiguration : IEntityTypeConfiguration<JiraProject>
{
    public void Configure(EntityTypeBuilder<JiraProject> b)
    {
        b.ToTable("JiraProjects");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraProjectKey).IsRequired().HasMaxLength(256);
        b.Property(x => x.JiraProjectId).IsRequired().HasMaxLength(64);
        b.Property(x => x.SyncIntervalMinutes).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.JiraConnectionId, x.JiraProjectKey }).IsUnique();
        b.HasOne<JiraConnection>().WithMany().HasForeignKey(x => x.JiraConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
