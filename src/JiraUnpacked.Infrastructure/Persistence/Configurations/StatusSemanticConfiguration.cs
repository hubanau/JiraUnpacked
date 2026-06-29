using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class StatusSemanticConfiguration : IEntityTypeConfiguration<StatusSemantic>
{
    public void Configure(EntityTypeBuilder<StatusSemantic> b)
    {
        b.ToTable("StatusSemantics");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraStatusId).IsRequired().HasMaxLength(64);
        b.Property(x => x.StatusName).IsRequired().HasMaxLength(256);
        b.Property(x => x.Class).IsRequired().HasConversion<string>().HasMaxLength(16);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.JiraProjectId, x.JiraStatusId }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
