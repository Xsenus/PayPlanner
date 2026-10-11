using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddDocumentSellerSelectionPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SellerBranchId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerCompanyId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerSnapshotJson",
                table: "Payments",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerBranchId",
                table: "Acts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerCompanyId",
                table: "Acts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerSnapshotJson",
                table: "Acts",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SellerBranchId",
                table: "Payments",
                column: "SellerBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SellerCompanyId",
                table: "Payments",
                column: "SellerCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_SellerBranchId",
                table: "Acts",
                column: "SellerBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_SellerCompanyId",
                table: "Acts",
                column: "SellerCompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_SellerBranchId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_SellerCompanyId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Acts_SellerBranchId",
                table: "Acts");

            migrationBuilder.DropIndex(
                name: "IX_Acts_SellerCompanyId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "SellerBranchId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "SellerCompanyId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "SellerSnapshotJson",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "SellerBranchId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "SellerCompanyId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "SellerSnapshotJson",
                table: "Acts");
        }
    }
}
