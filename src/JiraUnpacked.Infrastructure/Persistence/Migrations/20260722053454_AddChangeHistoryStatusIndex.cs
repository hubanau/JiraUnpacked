using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeHistoryStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraProjectId_Field_ChangedAt",
                table: "IssueChangeHistory",
                columns: new[] { "EndUserId", "JiraProjectId", "Field", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraProjectId_Field_ChangedAt",
                table: "IssueChangeHistory");
        }
    }
}
