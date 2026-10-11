using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExtendBankConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RefreshTokenExpiresAt",
                table: "BankConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "BankConnectionAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "BankConnectionAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankSyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BankConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    BankConnectionAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PeriodFrom = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeriodTo = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Result = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OperationsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankSyncLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankSyncLogs_Connection_CreatedAt",
                table: "BankSyncLogs",
                columns: new[] { "BankConnectionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankSyncLogs");

            migrationBuilder.DropColumn(
                name: "RefreshTokenExpiresAt",
                table: "BankConnections");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "BankConnectionAccounts");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "BankConnectionAccounts");
        }
    }
}
