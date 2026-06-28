using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'jiraunpacked_app') THEN
        CREATE ROLE jiraunpacked_app LOGIN PASSWORD 'localapp' NOBYPASSRLS;
    END IF;
END$$;

GRANT USAGE ON SCHEMA public TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO jiraunpacked_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO jiraunpacked_app;

ALTER TABLE ""JiraConnections"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""JiraConnections"" FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON ""JiraConnections""
    USING (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation ON ""JiraConnections"";
ALTER TABLE ""JiraConnections"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""JiraConnections"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
