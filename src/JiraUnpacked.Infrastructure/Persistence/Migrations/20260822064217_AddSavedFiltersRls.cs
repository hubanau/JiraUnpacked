using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Layer 2 for the SavedFilters table. The EF global query filter (layer 1) is the primary
    /// guard; this policy is the independent one that holds even if a query ever escapes EF.
    /// FORCE ROW LEVEL SECURITY so the table owner is not exempt either.
    /// </summary>
    public partial class AddSavedFiltersRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"GRANT SELECT, INSERT, UPDATE, DELETE ON ""SavedFilters"" TO jiraunpacked_app;");
            migrationBuilder.Sql(@"
ALTER TABLE ""SavedFilters"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""SavedFilters"" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ""SavedFilters""
    USING (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation ON ""SavedFilters"";
ALTER TABLE ""SavedFilters"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""SavedFilters"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
