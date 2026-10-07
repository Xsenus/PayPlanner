using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCourtDocumentSourceContentPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CourtDocumentSourceContents",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourtDocumentSourceContents", x => x.AttemptId);
                    table.CheckConstraint("CK_CourtSourceContents_Bytes", "\"SizeBytes\" BETWEEN 1 AND 26214400 AND length(\"Bytes\") = \"SizeBytes\" AND length(\"Sha256\") = 64");
                    table.ForeignKey(
                        name: "FK_CourtDocumentSourceContents_CourtDocumentSourceAttempts_Att~",
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
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CourtDocumentSourceContents") OR EXISTS (SELECT 1 FROM "CourtDocumentSourceAttempts") THEN
                        RAISE EXCEPTION 'Frozen court source content cannot be discarded' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "CourtDocumentSourceContents");
        }
    }
}
