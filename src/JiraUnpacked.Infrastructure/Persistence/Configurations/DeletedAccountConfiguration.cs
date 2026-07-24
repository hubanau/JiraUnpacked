using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class DeletedAccountConfiguration : IEntityTypeConfiguration<DeletedAccount>
{
    public void Configure(EntityTypeBuilder<DeletedAccount> b)
    {
        b.ToTable("DeletedAccounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeletedUserId).IsRequired();
        b.Property(x => x.RequestedBy).IsRequired();
        b.Property(x => x.DeletedAt).IsRequired();
        b.HasIndex(x => x.DeletedUserId);

        // No foreign keys on purpose: both ids may reference rows that no longer exist.
    }
}
