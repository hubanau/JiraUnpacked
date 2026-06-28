using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraConnectionConfiguration : IEntityTypeConfiguration<JiraConnection>
{
    public void Configure(EntityTypeBuilder<JiraConnection> builder)
    {
        builder.ToTable("JiraConnections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.BaseUrl).IsRequired().HasMaxLength(2048);
        builder.Property(c => c.Email).IsRequired().HasMaxLength(256);
        builder.Property(c => c.ApiTokenEncrypted).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();

        builder.HasIndex(c => c.EndUserId);
    }
}
