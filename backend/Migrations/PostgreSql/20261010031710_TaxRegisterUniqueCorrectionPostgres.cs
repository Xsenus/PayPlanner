using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class TaxRegisterUniqueCorrectionPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaxRegisterEntries_CorrectsEntryId",
                table: "TaxRegisterEntries");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRegisterEntries_CorrectsEntryId",
                table: "TaxRegisterEntries",
                column: "CorrectsEntryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaxRegisterEntries_CorrectsEntryId",
                table: "TaxRegisterEntries");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRegisterEntries_CorrectsEntryId",
                table: "TaxRegisterEntries",
                column: "CorrectsEntryId");
        }
    }
}
