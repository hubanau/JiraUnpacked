using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncEngineRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var tables = new[]
            {
                "RawIssueSnapshots", "Statuses", "IssueTypes", "Priorities", "JiraUsers",
                "Sprints", "Teams", "Issues", "IssueChangeHistory", "SyncRuns"
            };
            foreach (var table in tables)
            {
                migrationBuilder.Sql($@"GRANT SELECT, INSERT, UPDATE, DELETE ON ""{table}"" TO jiraunpacked_app;");
                migrationBuilder.Sql($@"
ALTER TABLE ""{table}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{table}"" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ""{table}""
    USING (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var tables = new[]
            {
                "RawIssueSnapshots", "Statuses", "IssueTypes", "Priorities", "JiraUsers",
                "Sprints", "Teams", "Issues", "IssueChangeHistory", "SyncRuns"
            };
            foreach (var table in tables)
            {
                migrationBuilder.Sql($@"
DROP POLICY IF EXISTS tenant_isolation ON ""{table}"";
ALTER TABLE ""{table}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{table}"" DISABLE ROW LEVEL SECURITY;
");
            }
        }
    }
}
