using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSellerSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SellerBranchId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerCompanyId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerSnapshotJson",
                table: "Payments",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerBranchId",
                table: "Acts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerCompanyId",
                table: "Acts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerSnapshotJson",
                table: "Acts",
                type: "TEXT",
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
