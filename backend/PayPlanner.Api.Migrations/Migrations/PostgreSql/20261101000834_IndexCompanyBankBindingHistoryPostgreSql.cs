using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class IndexCompanyBankBindingHistoryPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankBinding_Bank_History",
                table: "CompanyBankAccountBindings",
                column: "BankConnectionAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankBinding_Company_History",
                table: "CompanyBankAccountBindings",
                columns: new[] { "LegalEntityId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CompanyBankBinding_Bank_History",
                table: "CompanyBankAccountBindings");

            migrationBuilder.DropIndex(
                name: "IX_CompanyBankBinding_Company_History",
                table: "CompanyBankAccountBindings");
        }
    }
}
