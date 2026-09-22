using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunResumeCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResumeCursor",
                table: "SyncRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResumeItemsDone",
                table: "SyncRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResumeStage",
                table: "SyncRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResumeCursor",
                table: "SyncRuns");

            migrationBuilder.DropColumn(
                name: "ResumeItemsDone",
                table: "SyncRuns");

            migrationBuilder.DropColumn(
                name: "ResumeStage",
                table: "SyncRuns");
        }
    }
}
