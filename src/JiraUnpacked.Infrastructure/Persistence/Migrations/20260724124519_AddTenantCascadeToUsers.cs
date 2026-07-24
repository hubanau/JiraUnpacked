using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCascadeToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InviteCodes_Users_UsedByUserId",
                table: "InviteCodes");

            migrationBuilder.AddForeignKey(
                name: "FK_FieldMappings_Users_EndUserId",
                table: "FieldMappings",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_InviteCodes_Users_UsedByUserId",
                table: "InviteCodes",
                column: "UsedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_IssueChangeHistory_Users_EndUserId",
                table: "IssueChangeHistory",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Issues_Users_EndUserId",
                table: "Issues",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_IssueTypes_Users_EndUserId",
                table: "IssueTypes",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JiraConnections_Users_EndUserId",
                table: "JiraConnections",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JiraProjects_Users_EndUserId",
                table: "JiraProjects",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JiraQueries_Users_EndUserId",
                table: "JiraQueries",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JiraUsers_Users_EndUserId",
                table: "JiraUsers",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Priorities_Users_EndUserId",
                table: "Priorities",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RawChangelog_Users_EndUserId",
                table: "RawChangelog",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RawIssueSnapshots_Users_EndUserId",
                table: "RawIssueSnapshots",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Sprints_Users_EndUserId",
                table: "Sprints",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Statuses_Users_EndUserId",
                table: "Statuses",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StatusSemantics_Users_EndUserId",
                table: "StatusSemantics",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuns_Users_EndUserId",
                table: "SyncRuns",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Teams_Users_EndUserId",
                table: "Teams",
                column: "EndUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FieldMappings_Users_EndUserId",
                table: "FieldMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_InviteCodes_Users_UsedByUserId",
                table: "InviteCodes");

            migrationBuilder.DropForeignKey(
                name: "FK_IssueChangeHistory_Users_EndUserId",
                table: "IssueChangeHistory");

            migrationBuilder.DropForeignKey(
                name: "FK_Issues_Users_EndUserId",
                table: "Issues");

            migrationBuilder.DropForeignKey(
                name: "FK_IssueTypes_Users_EndUserId",
                table: "IssueTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_JiraConnections_Users_EndUserId",
                table: "JiraConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_JiraProjects_Users_EndUserId",
                table: "JiraProjects");

            migrationBuilder.DropForeignKey(
                name: "FK_JiraQueries_Users_EndUserId",
                table: "JiraQueries");

            migrationBuilder.DropForeignKey(
                name: "FK_JiraUsers_Users_EndUserId",
                table: "JiraUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_Priorities_Users_EndUserId",
                table: "Priorities");

            migrationBuilder.DropForeignKey(
                name: "FK_RawChangelog_Users_EndUserId",
                table: "RawChangelog");

            migrationBuilder.DropForeignKey(
                name: "FK_RawIssueSnapshots_Users_EndUserId",
                table: "RawIssueSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_Sprints_Users_EndUserId",
                table: "Sprints");

            migrationBuilder.DropForeignKey(
                name: "FK_Statuses_Users_EndUserId",
                table: "Statuses");

            migrationBuilder.DropForeignKey(
                name: "FK_StatusSemantics_Users_EndUserId",
                table: "StatusSemantics");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuns_Users_EndUserId",
                table: "SyncRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_Teams_Users_EndUserId",
                table: "Teams");

            migrationBuilder.AddForeignKey(
                name: "FK_InviteCodes_Users_UsedByUserId",
                table: "InviteCodes",
                column: "UsedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
