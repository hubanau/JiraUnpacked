using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiraUnpacked.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletedAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeletedAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeletedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletedAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeletedAccounts_DeletedUserId",
                table: "DeletedAccounts",
                column: "DeletedUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletedAccounts");
        }
    }
}
