using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class ExtendBankConnectionsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RefreshTokenExpiresAt",
                table: "BankConnections",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "BankConnectionAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "BankConnectionAccounts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankSyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankConnectionId = table.Column<int>(type: "integer", nullable: false),
                    BankConnectionAccountId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PeriodTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OperationsCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
