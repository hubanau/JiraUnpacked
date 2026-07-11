using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRawChangelogRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"GRANT SELECT, INSERT, UPDATE, DELETE ON ""RawChangelog"" TO jiraunpacked_app;");
            migrationBuilder.Sql(@"
ALTER TABLE ""RawChangelog"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""RawChangelog"" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ""RawChangelog""
    USING (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation ON ""RawChangelog"";
ALTER TABLE ""RawChangelog"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""RawChangelog"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
