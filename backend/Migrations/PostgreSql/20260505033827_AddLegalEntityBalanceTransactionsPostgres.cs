using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddLegalEntityBalanceTransactionsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ClientId",
                table: "ClientBalanceTransactions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "LegalEntityId",
                table: "ClientBalanceTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientBalanceTransactions_LegalEntityId_CreatedAt",
                table: "ClientBalanceTransactions",
                columns: new[] { "LegalEntityId", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClientBalanceTransactions_Owner",
                table: "ClientBalanceTransactions",
                sql: "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientBalanceTransactions_LegalEntities_LegalEntityId",
                table: "ClientBalanceTransactions",
                column: "LegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientBalanceTransactions_LegalEntities_LegalEntityId",
                table: "ClientBalanceTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ClientBalanceTransactions_LegalEntityId_CreatedAt",
                table: "ClientBalanceTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClientBalanceTransactions_Owner",
                table: "ClientBalanceTransactions");

            migrationBuilder.DropColumn(
                name: "LegalEntityId",
                table: "ClientBalanceTransactions");

            migrationBuilder.AlterColumn<int>(
                name: "ClientId",
                table: "ClientBalanceTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
