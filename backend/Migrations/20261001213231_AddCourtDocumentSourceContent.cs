using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourtDocumentSourceContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CourtDocumentSourceContents",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourtDocumentSourceContents", x => x.AttemptId);
                    table.CheckConstraint("CK_CourtSourceContents_Bytes", "\"SizeBytes\" BETWEEN 1 AND 26214400 AND length(\"Bytes\") = \"SizeBytes\" AND length(\"Sha256\") = 64");
                    table.ForeignKey(
                        name: "FK_CourtDocumentSourceContents_CourtDocumentSourceAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "CourtDocumentSourceAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__CourtContentDowngradeGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__CourtContentDowngradeGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (SELECT 1 FROM "CourtDocumentSourceContents")
                    OR EXISTS (SELECT 1 FROM "CourtDocumentSourceAttempts");
                DROP TABLE "__CourtContentDowngradeGuard";
                """);
            migrationBuilder.DropTable(
                name: "CourtDocumentSourceContents");
        }
    }
}
