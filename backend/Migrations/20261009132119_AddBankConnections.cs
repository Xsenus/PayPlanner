using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBankConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Active"),
                    AccessTokenProtected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastAccountsSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankConnectionAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BankConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AccountNumber = table.Column<string>(type: "TEXT", maxLength: 34, nullable: false),
                    Bik = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Open"),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatementsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatementWatermark = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatementSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnectionAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankConnectionAccounts_BankConnections_BankConnectionId",
                        column: x => x.BankConnectionId,
                        principalTable: "BankConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankStatementOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BankConnectionAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalOperationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OperationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsIncome = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBooked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CounterpartyInn = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    CounterpartyName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CounterpartyAccount = table.Column<string>(type: "TEXT", maxLength: 34, nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankStatementOperations_BankConnectionAccounts_BankConnectionAccountId",
                        column: x => x.BankConnectionAccountId,
                        principalTable: "BankConnectionAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankConnectionAccounts_Connection_External",
                table: "BankConnectionAccounts",
                columns: new[] { "BankConnectionId", "ExternalAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_LegalEntityId_Provider",
                table: "BankConnections",
                columns: new[] { "LegalEntityId", "Provider" });

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementOperations_Account_Date",
                table: "BankStatementOperations",
                columns: new[] { "BankConnectionAccountId", "OperationDate" });

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementOperations_Account_External",
                table: "BankStatementOperations",
                columns: new[] { "BankConnectionAccountId", "ExternalOperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankStatementOperations");

            migrationBuilder.DropTable(
                name: "BankConnectionAccounts");

            migrationBuilder.DropTable(
                name: "BankConnections");
        }
    }
}
