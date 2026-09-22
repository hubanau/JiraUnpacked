using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSyncPaused : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SyncPaused",
                table: "JiraProjects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncPaused",
                table: "JiraProjects");
        }
    }
}
