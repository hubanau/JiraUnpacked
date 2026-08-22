using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class SavedFilterConfiguration : IEntityTypeConfiguration<SavedFilter>
{
    public void Configure(EntityTypeBuilder<SavedFilter> b)
    {
        b.ToTable("SavedFilters");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(100);
        // jsonb, not text: Postgres validates the document on write, so a row this service
        // wrote can always be deserialised back.
        b.Property(x => x.DefinitionJson).IsRequired().HasColumnType("jsonb");
        b.HasIndex(x => x.EndUserId);
        // Names are unique per project, so a client can address a filter by what the user typed.
        b.HasIndex(x => new { x.JiraProjectId, x.Name }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
