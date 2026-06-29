using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class FieldMappingConfiguration : IEntityTypeConfiguration<FieldMapping>
{
    public void Configure(EntityTypeBuilder<FieldMapping> b)
    {
        b.ToTable("FieldMappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.JiraFieldId).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.JiraProjectId, x.Role }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
