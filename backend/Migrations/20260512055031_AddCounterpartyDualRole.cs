using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCounterpartyDualRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientStatusId",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsClientAndSupplier",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsClientAndSupplier",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_ClientStatusId",
                table: "LegalEntities",
                column: "ClientStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_IsClientAndSupplier",
                table: "LegalEntities",
                column: "IsClientAndSupplier");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_IsClientAndSupplier",
                table: "Clients",
                column: "IsClientAndSupplier");

            migrationBuilder.AddForeignKey(
                name: "FK_LegalEntities_ClientStatuses_ClientStatusId",
                table: "LegalEntities",
                column: "ClientStatusId",
                principalTable: "ClientStatuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LegalEntities_ClientStatuses_ClientStatusId",
                table: "LegalEntities");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntities_ClientStatusId",
                table: "LegalEntities");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntities_IsClientAndSupplier",
                table: "LegalEntities");

            migrationBuilder.DropIndex(
                name: "IX_Clients_IsClientAndSupplier",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ClientStatusId",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "IsClientAndSupplier",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "IsClientAndSupplier",
                table: "Clients");
        }
    }
}
