using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourtDocumentAcceptedControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptedControlsJson",
                table: "CourtDocumentSources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__CourtControlProofDowngradeGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__CourtControlProofDowngradeGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (SELECT 1 FROM "CourtDocumentSources");
                DROP TABLE "__CourtControlProofDowngradeGuard";
                """);
            migrationBuilder.DropColumn(
                name: "AcceptedControlsJson",
                table: "CourtDocumentSources");
        }
    }
}
