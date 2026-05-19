using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseDocumentsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    DocumentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UploadedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseDocuments_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseDocuments_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseDocuments_Case_Document_Version",
                table: "ClientCaseDocuments",
                columns: new[] { "ClientCaseId", "DocumentKey", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseDocuments_ClientCaseId",
                table: "ClientCaseDocuments",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseDocuments_DocumentType",
                table: "ClientCaseDocuments",
                column: "DocumentType");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseDocuments_Status",
                table: "ClientCaseDocuments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseDocuments_UploadedByUserId",
                table: "ClientCaseDocuments",
                column: "UploadedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientCaseDocuments");
        }
    }
}
