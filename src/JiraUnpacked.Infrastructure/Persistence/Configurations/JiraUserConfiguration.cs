using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraUserConfiguration : IEntityTypeConfiguration<JiraUser>
{
    public void Configure(EntityTypeBuilder<JiraUser> b)
    {
        b.ToTable("JiraUsers");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraAccountId).IsRequired().HasMaxLength(128);
        b.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraConnectionId, x.JiraAccountId }).IsUnique();
        b.HasOne<JiraConnection>().WithMany().HasForeignKey(x => x.JiraConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
