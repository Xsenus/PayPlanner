using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCaseDocumentUploadRetirementsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseDocumentUploadRetirements",
                columns: table => new
                {
                    CaseId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    UploadKey = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseDocumentUploadRetirements", x => new { x.CaseId, x.ActorId, x.UploadKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseDocumentUploadRetirements_StorageObjectId",
                table: "CaseDocumentUploadRetirements",
                column: "StorageObjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseDocumentUploadRetirements");
        }
    }
}
