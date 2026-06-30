using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncEngineTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IssueChangeHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraIssueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JiraHistoryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Field = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FromValue = table.Column<string>(type: "text", nullable: true),
                    ToValue = table.Column<string>(type: "text", nullable: true),
                    Author = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueChangeHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueChangeHistory_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraIssueTypeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueTypes_JiraConnections_JiraConnectionId",
                        column: x => x.JiraConnectionId,
                        principalTable: "JiraConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JiraUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraUsers_JiraConnections_JiraConnectionId",
                        column: x => x.JiraConnectionId,
                        principalTable: "JiraConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Priorities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraPriorityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Priorities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Priorities_JiraConnections_JiraConnectionId",
                        column: x => x.JiraConnectionId,
                        principalTable: "JiraConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RawIssueSnapshots",
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
                    table.PrimaryKey("PK_RawIssueSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RawIssueSnapshots_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sprints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraSprintId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sprints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sprints_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Statuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraStatusId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StatusCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Statuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Statuses_JiraConnections_JiraConnectionId",
                        column: x => x.JiraConnectionId,
                        principalTable: "JiraConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IssuesProcessed = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuns_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraTeamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Teams_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Issues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraIssueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JiraKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssueTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    StatusId = table.Column<Guid>(type: "uuid", nullable: true),
                    PriorityId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SprintId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoryPoints = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    ParentJiraIssueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ParentIssueId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    JiraCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    JiraUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Issues_IssueTypes_IssueTypeId",
                        column: x => x.IssueTypeId,
                        principalTable: "IssueTypes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Issues_Issues_ParentIssueId",
                        column: x => x.ParentIssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Issues_JiraProjects_JiraProjectId",
                        column: x => x.JiraProjectId,
                        principalTable: "JiraProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Issues_JiraUsers_AssigneeId",
                        column: x => x.AssigneeId,
                        principalTable: "JiraUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Issues_Priorities_PriorityId",
                        column: x => x.PriorityId,
                        principalTable: "Priorities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Issues_Sprints_SprintId",
                        column: x => x.SprintId,
                        principalTable: "Sprints",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Issues_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Issues_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_EndUserId",
                table: "IssueChangeHistory",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraIssueId_JiraHistoryId_Field",
                table: "IssueChangeHistory",
                columns: new[] { "EndUserId", "JiraIssueId", "JiraHistoryId", "Field" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_EndUserId_JiraProjectId_JiraIssueId",
                table: "IssueChangeHistory",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraIssueId" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_JiraIssueId_Field_ChangedAt",
                table: "IssueChangeHistory",
                columns: new[] { "JiraIssueId", "Field", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueChangeHistory_JiraProjectId",
                table: "IssueChangeHistory",
                column: "JiraProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_AssigneeId",
                table: "Issues",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_EndUserId",
                table: "Issues",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_EndUserId_JiraIssueId",
                table: "Issues",
                columns: new[] { "EndUserId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_EndUserId_JiraProjectId",
                table: "Issues",
                columns: new[] { "EndUserId", "JiraProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_EndUserId_ParentJiraIssueId",
                table: "Issues",
                columns: new[] { "EndUserId", "ParentJiraIssueId" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_IssueTypeId",
                table: "Issues",
                column: "IssueTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_JiraProjectId",
                table: "Issues",
                column: "JiraProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ParentIssueId",
                table: "Issues",
                column: "ParentIssueId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_PriorityId",
                table: "Issues",
                column: "PriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_SprintId",
                table: "Issues",
                column: "SprintId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_StatusId",
                table: "Issues",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_TeamId",
                table: "Issues",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTypes_EndUserId",
                table: "IssueTypes",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTypes_EndUserId_JiraConnectionId_JiraIssueTypeId",
                table: "IssueTypes",
                columns: new[] { "EndUserId", "JiraConnectionId", "JiraIssueTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueTypes_JiraConnectionId",
                table: "IssueTypes",
                column: "JiraConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_JiraUsers_EndUserId",
                table: "JiraUsers",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JiraUsers_EndUserId_JiraConnectionId_JiraAccountId",
                table: "JiraUsers",
                columns: new[] { "EndUserId", "JiraConnectionId", "JiraAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraUsers_JiraConnectionId",
                table: "JiraUsers",
                column: "JiraConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Priorities_EndUserId",
                table: "Priorities",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Priorities_EndUserId_JiraConnectionId_JiraPriorityId",
                table: "Priorities",
                columns: new[] { "EndUserId", "JiraConnectionId", "JiraPriorityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Priorities_JiraConnectionId",
                table: "Priorities",
                column: "JiraConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_RawIssueSnapshots_EndUserId",
                table: "RawIssueSnapshots",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RawIssueSnapshots_EndUserId_JiraIssueId",
                table: "RawIssueSnapshots",
                columns: new[] { "EndUserId", "JiraIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawIssueSnapshots_JiraProjectId",
                table: "RawIssueSnapshots",
                column: "JiraProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_EndUserId",
                table: "Sprints",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_EndUserId_JiraProjectId_JiraSprintId",
                table: "Sprints",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraSprintId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_JiraProjectId",
                table: "Sprints",
                column: "JiraProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Statuses_EndUserId",
                table: "Statuses",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Statuses_EndUserId_JiraConnectionId_JiraStatusId",
                table: "Statuses",
                columns: new[] { "EndUserId", "JiraConnectionId", "JiraStatusId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Statuses_JiraConnectionId",
                table: "Statuses",
                column: "JiraConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_EndUserId",
                table: "SyncRuns",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_EndUserId_JiraProjectId_StartedAt",
                table: "SyncRuns",
                columns: new[] { "EndUserId", "JiraProjectId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_JiraProjectId",
                table: "SyncRuns",
                column: "JiraProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_EndUserId",
                table: "Teams",
                column: "EndUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_EndUserId_JiraProjectId_JiraTeamId",
                table: "Teams",
                columns: new[] { "EndUserId", "JiraProjectId", "JiraTeamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_JiraProjectId",
                table: "Teams",
                column: "JiraProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueChangeHistory");

            migrationBuilder.DropTable(
                name: "Issues");

            migrationBuilder.DropTable(
                name: "RawIssueSnapshots");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "IssueTypes");

            migrationBuilder.DropTable(
                name: "JiraUsers");

            migrationBuilder.DropTable(
                name: "Priorities");

            migrationBuilder.DropTable(
                name: "Sprints");

            migrationBuilder.DropTable(
                name: "Statuses");

            migrationBuilder.DropTable(
                name: "Teams");
        }
    }
}
