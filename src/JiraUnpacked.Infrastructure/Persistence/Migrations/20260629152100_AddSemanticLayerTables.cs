using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticLayerTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JiraProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    JiraProjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SyncIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraProjects_JiraConnections_JiraConnectionId",
                        column: x => x.JiraConnectionId,
                        principalTable: "JiraConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FieldMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    JiraFieldId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldMappings_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JiraQueries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Jql = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraQueries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraQueries_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusSemantics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraStatusId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StatusName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Class = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusSemantics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusSemantics_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldMappings_EndUserId",
                table: "FieldMappings",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldMappings_JiraProjectId_Role",
                table: "FieldMappings",
                columns: new[] { "JiraProjectId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraProjects_EndUserId",
                table: "JiraProjects",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JiraProjects_JiraConnectionId_JiraProjectKey",
                table: "JiraProjects",
                columns: new[] { "JiraConnectionId", "JiraProjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraQueries_EndUserId",
                table: "JiraQueries",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JiraQueries_JiraProjectId",
                table: "JiraQueries",
                column: "JiraProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusSemantics_EndUserId",
                table: "StatusSemantics",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusSemantics_JiraProjectId_JiraStatusId",
                table: "StatusSemantics",
                columns: new[] { "JiraProjectId", "JiraStatusId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldMappings");

            migrationBuilder.DropTable(
                name: "JiraQueries");

            migrationBuilder.DropTable(
                name: "StatusSemantics");

            migrationBuilder.DropTable(
                name: "JiraProjects");
        }
    }
}
