using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> b)
    {
        b.ToTable("PasswordResetTokens");
        b.HasKey(t => t.Id);

        b.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);
        // Unique and the lookup path: a redeem finds the row by hash, so the raw token never
        // reaches a query and there is nothing to compare byte by byte in application code.
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.UserId);

        b.Property(t => t.ExpiresAt).IsRequired();
        b.Property(t => t.CreatedAt).IsRequired();

        // Keyed by UserId, like RefreshTokens and EmailVerifications: this is an identity
        // table, not tenant data, so it carries no EndUserId and needs no RLS policy. The
        // cascade is what keeps account erasure complete.
        b.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
