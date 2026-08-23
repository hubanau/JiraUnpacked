using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A read-only, RLS-bypassing role for the four background readers (due-project discovery,
    /// retention, the stuck-run reaper, the token-envelope upgrader). They read cross-tenant
    /// scheduling metadata and never write through that connection.
    ///
    /// This replaces using the OWNER connection for those reads, which handed the running
    /// container DDL rights it never needed.
    ///
    /// It also removes a silent production failure. Every tenant table is FORCE ROW LEVEL
    /// SECURITY, which subjects even the table owner to its policies, so those reads only ever
    /// worked because the local development owner happens to be a Postgres SUPERUSER — and
    /// superusers bypass RLS unconditionally. On a managed Postgres the master user is not a
    /// superuser, so the same reads would have returned zero rows and scheduled sync would have
    /// stopped without erroring. Granting BYPASSRLS explicitly makes that a property of the
    /// schema instead of an accident of who owns the tables.
    /// </summary>
    public partial class AddReaderRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'jiraunpacked_reader') THEN
        CREATE ROLE jiraunpacked_reader LOGIN PASSWORD 'localreader';
    END IF;
END$$;
");

            // Separate statement so a permissions failure names THIS as the cause. Setting
            // BYPASSRLS requires superuser; on a managed Postgres the master user may not be
            // one, in which case this must be granted out-of-band by someone who is.
            migrationBuilder.Sql(@"ALTER ROLE jiraunpacked_reader BYPASSRLS;");

            // SELECT only, on purpose: every one of the four readers is a reader. Writes go
            // through the tenant-scoped context under RLS, which is what keeps isolation
            // layer 2 load-bearing rather than advisory.
            migrationBuilder.Sql(@"
GRANT USAGE ON SCHEMA public TO jiraunpacked_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO jiraunpacked_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO jiraunpacked_reader;
");

            // Fail the migration rather than leave a role that silently reads nothing. Without
            // BYPASSRLS this role sees zero rows through every FORCEd policy, which is exactly
            // the quiet failure this migration exists to prevent — so it must never be the
            // outcome of a migration that reported success.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT (SELECT rolbypassrls FROM pg_roles WHERE rolname = 'jiraunpacked_reader') THEN
        RAISE EXCEPTION 'jiraunpacked_reader lacks BYPASSRLS. Every tenant table is FORCE ROW '
            'LEVEL SECURITY, so without it this role reads zero rows and scheduled sync stops '
            'silently. Grant it as a superuser: ALTER ROLE jiraunpacked_reader BYPASSRLS;';
    END IF;
END$$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM jiraunpacked_reader;
REVOKE USAGE ON SCHEMA public FROM jiraunpacked_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT ON TABLES FROM jiraunpacked_reader;
");
            // The role itself is left in place: it is cluster-wide and may own grants in another
            // database, so dropping it is not this migration's call to make.
        }
    }
}
