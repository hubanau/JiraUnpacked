using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class InviteCodeConfiguration : IEntityTypeConfiguration<InviteCode>
{
    public void Configure(EntityTypeBuilder<InviteCode> builder)
    {
        builder.ToTable("InviteCodes");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Code).IsRequired().HasMaxLength(64);
        builder.HasIndex(i => i.Code).IsUnique();

        builder.Property(i => i.ExpiresAt).IsRequired();

        // Creator admin — restrict delete so codes never orphan silently.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(i => i.CreatedByAdminId)
            .OnDelete(DeleteBehavior.Restrict);

        // Redeemer (nullable) — SetNull so an erasure request can proceed. The code row
        // survives as proof it was issued and redeemed; the link to the erased person is cut.
        // (Restrict here made every End User who registered via a code undeletable.)
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(i => i.UsedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
