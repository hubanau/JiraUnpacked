using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRawChangelog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SyncRuns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Sync");

            migrationBuilder.CreateTable(
                name: "RawChangelog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraIssueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawChangelog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RawChangelog_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawChangelog_EndUserId",
                table: "RawChangelog",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RawChangelog_EndUserId_JiraIssueId",
                table: "RawChangelog",
                columns: new[] { "EndUserId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawChangelog_JiraProjectId",
                table: "RawChangelog",
                column: "JiraProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawChangelog");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SyncRuns");
        }
    }
}
