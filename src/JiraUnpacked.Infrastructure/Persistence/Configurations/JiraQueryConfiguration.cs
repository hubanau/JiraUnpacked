using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraQueryConfiguration : IEntityTypeConfiguration<JiraQuery>
{
    public void Configure(EntityTypeBuilder<JiraQuery> b)
    {
        b.ToTable("JiraQueries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Jql).IsRequired().HasMaxLength(4096);
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => x.JiraProjectId);
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
