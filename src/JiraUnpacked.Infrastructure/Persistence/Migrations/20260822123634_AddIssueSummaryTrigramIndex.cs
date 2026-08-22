using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-E4's `summary Contains` compiles to ILIKE '%term%', which no btree index can serve —
    /// the existing (EndUserId, JiraProjectId, ...) index narrows to the project and every row
    /// in it is then matched one by one. A trigram GIN index makes the match itself indexed.
    ///
    /// Deliberately fails loudly if pg_trgm is unavailable rather than skipping quietly: an
    /// index that silently did not get created would show up much later as a slow report with
    /// no explanation. pg_trgm ships with Postgres and is enabled by default on RDS, Aurora,
    /// Cloud SQL and Azure Database.
    /// </summary>
    public partial class AddIssueSummaryTrigramIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Issues_Summary_trgm""
                  ON ""Issues"" USING gin (""Summary"" gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Issues_Summary_trgm"";");
            // The extension is intentionally left in place: something else may depend on it,
            // and dropping a shared extension is not this migration's call to make.
        }
    }
}
