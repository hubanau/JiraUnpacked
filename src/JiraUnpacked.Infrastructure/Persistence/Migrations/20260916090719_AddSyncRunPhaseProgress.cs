using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunPhaseProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PhaseProcessed",
                table: "SyncRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhaseTotal",
                table: "SyncRuns",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhaseProcessed",
                table: "SyncRuns");

            migrationBuilder.DropColumn(
                name: "PhaseTotal",
                table: "SyncRuns");
        }
    }
}
