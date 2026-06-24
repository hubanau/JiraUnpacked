using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class EmailVerificationConfiguration : IEntityTypeConfiguration<EmailVerification>
{
    public void Configure(EntityTypeBuilder<EmailVerification> builder)
    {
        builder.ToTable("EmailVerifications");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.CodeHash).IsRequired();
        builder.Property(e => e.ExpiresAt).IsRequired();

        builder.HasIndex(e => e.UserId);

        // Verifications belong to a user — cascade delete with the user.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
