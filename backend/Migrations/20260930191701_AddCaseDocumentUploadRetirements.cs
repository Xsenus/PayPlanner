using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseDocumentUploadRetirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseDocumentUploadRetirements",
                columns: table => new
                {
                    CaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageObjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
