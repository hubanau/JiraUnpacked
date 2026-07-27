using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> b)
    {
        b.ToTable("Issues");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueId).IsRequired().HasMaxLength(64);
        b.Property(x => x.JiraKey).IsRequired().HasMaxLength(64);
        b.Property(x => x.ParentJiraIssueId).HasMaxLength(64);
        b.Property(x => x.StoryPoints).HasPrecision(9, 2);
        b.Property(x => x.Summary); // unlimited text (inline free text)

        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraIssueId }).IsUnique();
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId });
        b.HasIndex(x => new { x.EndUserId, x.ParentJiraIssueId });
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraUpdatedAt });

        // Owning project: cascade. Dictionary FKs: NoAction (a dict delete must not delete issues).
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<IssueType>().WithMany().HasForeignKey(x => x.IssueTypeId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Status>().WithMany().HasForeignKey(x => x.StatusId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Priority>().WithMany().HasForeignKey(x => x.PriorityId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<JiraUser>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Sprint>().WithMany().HasForeignKey(x => x.SprintId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.NoAction);
        // Self parent link: restrict (no cascade cycle).
        b.HasOne<Issue>().WithMany().HasForeignKey(x => x.ParentIssueId).OnDelete(DeleteBehavior.Restrict);
    }
}
