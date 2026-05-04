using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddLegalEntityDocumentOwnersPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpenseLegalEntityId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LegalEntityId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ClientId",
                table: "ClientCases",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "LegalEntityId",
                table: "ClientCases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LegalEntityId",
                table: "Acts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ExpenseLegalEntityId",
                table: "Payments",
                column: "ExpenseLegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LegalEntityId",
                table: "Payments",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_LegalEntityId_CreatedAt",
                table: "ClientCases",
                columns: new[] { "LegalEntityId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_LegalEntityId_Status",
                table: "ClientCases",
                columns: new[] { "LegalEntityId", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClientCases_Owner",
                table: "ClientCases",
                sql: "(ClientId IS NOT NULL AND LegalEntityId IS NULL) OR (ClientId IS NULL AND LegalEntityId IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_LegalEntityId",
                table: "Acts",
                column: "LegalEntityId");

            migrationBuilder.AddForeignKey(
                name: "FK_Acts_LegalEntities_LegalEntityId",
                table: "Acts",
                column: "LegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCases_LegalEntities_LegalEntityId",
                table: "ClientCases",
                column: "LegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_LegalEntities_ExpenseLegalEntityId",
                table: "Payments",
                column: "ExpenseLegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_LegalEntities_LegalEntityId",
                table: "Payments",
                column: "LegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Acts_LegalEntities_LegalEntityId",
                table: "Acts");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientCases_LegalEntities_LegalEntityId",
                table: "ClientCases");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_LegalEntities_ExpenseLegalEntityId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_LegalEntities_LegalEntityId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ExpenseLegalEntityId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_LegalEntityId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_LegalEntityId_CreatedAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_LegalEntityId_Status",
                table: "ClientCases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClientCases_Owner",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_Acts_LegalEntityId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "ExpenseLegalEntityId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "LegalEntityId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "LegalEntityId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "LegalEntityId",
                table: "Acts");

            migrationBuilder.AlterColumn<int>(
                name: "ClientId",
                table: "ClientCases",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
