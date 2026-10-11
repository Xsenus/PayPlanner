using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddContractSellerSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SellerBranchId",
                table: "Contracts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerCompanyId",
                table: "Contracts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerSnapshotJson",
                table: "Contracts",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_SellerBranchId",
                table: "Contracts",
                column: "SellerBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_SellerCompanyId",
                table: "Contracts",
                column: "SellerCompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Contracts_SellerBranchId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_SellerCompanyId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SellerBranchId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SellerCompanyId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SellerSnapshotJson",
                table: "Contracts");
        }
    }
}
