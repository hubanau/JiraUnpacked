using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueUpdatedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Issues_EndUserId_JiraProjectId_JiraUpdatedAt",
                table: "Issues",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraUpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Issues_EndUserId_JiraProjectId_JiraUpdatedAt",
                table: "Issues");
        }
    }
}
