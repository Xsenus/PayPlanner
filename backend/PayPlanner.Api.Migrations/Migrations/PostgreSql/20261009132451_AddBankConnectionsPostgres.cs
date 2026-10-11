using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddBankConnectionsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LegalEntityId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Active"),
                    AccessTokenProtected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastAccountsSyncAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankConnectionAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankConnectionId = table.Column<int>(type: "integer", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountNumber = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    Bik = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Open"),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    StatementsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    StatementWatermark = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastStatementSyncAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankConnectionAccountId = table.Column<int>(type: "integer", nullable: false),
                    ExternalOperationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OperationDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsIncome = table.Column<bool>(type: "boolean", nullable: false),
                    IsBooked = table.Column<bool>(type: "boolean", nullable: false),
                    CounterpartyInn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CounterpartyName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CounterpartyAccount = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankStatementOperations_BankConnectionAccounts_BankConnecti~",
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
