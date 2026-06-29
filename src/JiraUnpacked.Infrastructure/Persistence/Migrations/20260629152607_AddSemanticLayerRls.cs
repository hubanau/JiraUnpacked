using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticLayerRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE, DELETE ON ""JiraProjects""   TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ""JiraQueries""    TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ""FieldMappings""  TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ""StatusSemantics"" TO jiraunpacked_app;
");
            foreach (var table in new[] { "JiraProjects", "JiraQueries", "FieldMappings", "StatusSemantics" })
            {
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
            foreach (var table in new[] { "JiraProjects", "JiraQueries", "FieldMappings", "StatusSemantics" })
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
