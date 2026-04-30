using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBankStatementImportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BankStatementImportBatchId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankStatementImportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    FileHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RowsRead = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorRows = table.Column<int>(type: "INTEGER", nullable: false),
                    RollbackRows = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    RolledBackAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankStatementImportRows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    RowNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MoneyOperationId = table.Column<int>(type: "INTEGER", nullable: true),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AccountName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CounterpartyName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PaymentOrderNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    RolledBackAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementImportRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankStatementImportRows_BankStatementImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "BankStatementImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BankStatementImportRows_MoneyOperations_MoneyOperationId",
                        column: x => x.MoneyOperationId,
                        principalTable: "MoneyOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_BankStatementImportBatchId",
                table: "MoneyOperations",
                column: "BankStatementImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_CreatedAt",
                table: "BankStatementImportBatches",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_FileHash",
                table: "BankStatementImportBatches",
                column: "FileHash");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_Status",
                table: "BankStatementImportBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportRows_BatchId",
                table: "BankStatementImportRows",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportRows_ImportKey",
                table: "BankStatementImportRows",
                column: "ImportKey");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportRows_MoneyOperationId",
                table: "BankStatementImportRows",
                column: "MoneyOperationId");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyOperations_BankStatementImportBatches_BankStatementImportBatchId",
                table: "MoneyOperations",
                column: "BankStatementImportBatchId",
                principalTable: "BankStatementImportBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MoneyOperations_BankStatementImportBatches_BankStatementImportBatchId",
                table: "MoneyOperations");

            migrationBuilder.DropTable(
                name: "BankStatementImportRows");

            migrationBuilder.DropTable(
                name: "BankStatementImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperations_BankStatementImportBatchId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "BankStatementImportBatchId",
                table: "MoneyOperations");
        }
    }
}
