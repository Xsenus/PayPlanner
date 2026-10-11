using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddSpreadsheetImportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpreadsheetImportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    CreatedRows = table.Column<int>(type: "integer", nullable: false),
                    UpdatedRows = table.Column<int>(type: "integer", nullable: false),
                    SkippedRows = table.Column<int>(type: "integer", nullable: false),
                    ErrorRows = table.Column<int>(type: "integer", nullable: false),
                    RollbackRows = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    AppliedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RolledBackAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SummaryJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpreadsheetImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpreadsheetImportBatchFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    FileHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DetectedKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SheetName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpreadsheetImportBatchFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpreadsheetImportBatchFiles_SpreadsheetImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "SpreadsheetImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpreadsheetImportBatchOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<int>(type: "integer", nullable: false),
                    SectionKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    SourceFileHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceRowId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceRowNumber = table.Column<int>(type: "integer", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<int>(type: "integer", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BeforeJson = table.Column<string>(type: "text", nullable: true),
                    AfterJson = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    RollbackAction = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RollbackMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    RolledBackAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpreadsheetImportBatchOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpreadsheetImportBatchOperations_SpreadsheetImportBatches_B~",
                        column: x => x.BatchId,
                        principalTable: "SpreadsheetImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatches_CreatedAt",
                table: "SpreadsheetImportBatches",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatches_Status",
                table: "SpreadsheetImportBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatchFiles_BatchId",
                table: "SpreadsheetImportBatchFiles",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatchFiles_FileHash",
                table: "SpreadsheetImportBatchFiles",
                column: "FileHash");

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatchOperations_BatchId",
                table: "SpreadsheetImportBatchOperations",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatchOperations_BatchId_SectionKey",
                table: "SpreadsheetImportBatchOperations",
                columns: new[] { "BatchId", "SectionKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatchOperations_BatchId_SourceRowId",
                table: "SpreadsheetImportBatchOperations",
                columns: new[] { "BatchId", "SourceRowId" });

            migrationBuilder.CreateIndex(
                name: "IX_SpreadsheetImportBatchOperations_EntityId",
                table: "SpreadsheetImportBatchOperations",
                column: "EntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpreadsheetImportBatchFiles");

            migrationBuilder.DropTable(
                name: "SpreadsheetImportBatchOperations");

            migrationBuilder.DropTable(
                name: "SpreadsheetImportBatches");
        }
    }
}
