using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunProjectStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_JiraProjectId_Status",
                table: "SyncRuns",
                columns: new[] { "JiraProjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncRuns_JiraProjectId_Status",
                table: "SyncRuns");
        }
    }
}
