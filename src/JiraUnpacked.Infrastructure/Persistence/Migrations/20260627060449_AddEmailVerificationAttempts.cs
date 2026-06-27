using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "EmailVerifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "EmailVerifications");
        }
    }
}
