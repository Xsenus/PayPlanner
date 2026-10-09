using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaxRegisterEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: false),
                    BankConnectionAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    BankStatementOperationId = table.Column<int>(type: "INTEGER", nullable: true),
                    OperationDate = table.Column<DateTime>(type: "date", nullable: false),
                    PostingAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SubType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Sign = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentRef = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TaxBaseIncludesVat = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    CorrectsEntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    CorrectedByEntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    CorrectionReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NeedsClarification = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRegisterEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxRegisterEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaxRegisterEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FromStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ToStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRegisterEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxRegisterYearCloses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DataHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRegisterYearCloses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRegisterEntries_BankOperation",
                table: "TaxRegisterEntries",
                column: "BankStatementOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxRegisterEntries_Company_Date_Status_Type",
                table: "TaxRegisterEntries",
                columns: new[] { "LegalEntityId", "OperationDate", "Status", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRegisterEntries_CorrectsEntryId",
                table: "TaxRegisterEntries",
                column: "CorrectsEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRegisterEvents_Entry_CreatedAt",
                table: "TaxRegisterEvents",
                columns: new[] { "TaxRegisterEntryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRegisterYearCloses_Company_Year",
                table: "TaxRegisterYearCloses",
                columns: new[] { "LegalEntityId", "Year" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxRegisterEntries");

            migrationBuilder.DropTable(
                name: "TaxRegisterEvents");

            migrationBuilder.DropTable(
                name: "TaxRegisterYearCloses");
        }
    }
}
