using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJiraConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JiraConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApiTokenEncrypted = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JiraConnections_EndUserId",
                table: "JiraConnections",
                column: "EndUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JiraConnections");
        }
    }
}
