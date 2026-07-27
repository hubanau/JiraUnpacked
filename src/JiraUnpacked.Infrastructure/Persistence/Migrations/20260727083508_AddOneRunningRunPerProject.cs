using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOneRunningRunPerProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncRuns_JiraProjectId",
                table: "SyncRuns");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_OneRunningPerProject",
                table: "SyncRuns",
                column: "JiraProjectId",
                unique: true,
                filter: "\"Status\" = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncRuns_OneRunningPerProject",
                table: "SyncRuns");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_JiraProjectId",
                table: "SyncRuns",
                column: "JiraProjectId");
        }
    }
}
