using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncReliabilityUniqueScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RawIssueSnapshots_EndUserId_JiraIssueId",
                table: "RawIssueSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RawChangelog_EndUserId_JiraIssueId",
                table: "RawChangelog");

            migrationBuilder.DropIndex(
                name: "IX_Issues_EndUserId_JiraIssueId",
                table: "Issues");

            migrationBuilder.DropIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraIssueId_JiraHistoryId_Field",
                table: "IssueChangeHistory");

            migrationBuilder.CreateIndex(
                name: "IX_RawIssueSnapshots_EndUserId_JiraProjectId_JiraIssueId",
                table: "RawIssueSnapshots",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawChangelog_EndUserId_JiraProjectId_JiraIssueId",
                table: "RawChangelog",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_EndUserId_JiraProjectId_JiraIssueId",
                table: "Issues",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraProjectId_JiraIssueId_Jira~",
                table: "IssueChangeHistory",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraIssueId", "JiraHistoryId", "Field" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RawIssueSnapshots_EndUserId_JiraProjectId_JiraIssueId",
                table: "RawIssueSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RawChangelog_EndUserId_JiraProjectId_JiraIssueId",
                table: "RawChangelog");

            migrationBuilder.DropIndex(
                name: "IX_Issues_EndUserId_JiraProjectId_JiraIssueId",
                table: "Issues");

            migrationBuilder.DropIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraProjectId_JiraIssueId_Jira~",
                table: "IssueChangeHistory");

            migrationBuilder.CreateIndex(
                name: "IX_RawIssueSnapshots_EndUserId_JiraIssueId",
                table: "RawIssueSnapshots",
                columns: new[] { "EndUserId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawChangelog_EndUserId_JiraIssueId",
                table: "RawChangelog",
                columns: new[] { "EndUserId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_EndUserId_JiraIssueId",
                table: "Issues",
                columns: new[] { "EndUserId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraIssueId_JiraHistoryId_Field",
                table: "IssueChangeHistory",
                columns: new[] { "EndUserId", "JiraIssueId", "JiraHistoryId", "Field" },
                unique: true);
        }
    }
}
