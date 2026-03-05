using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddExtendedCompanyProfileFieldsToLegalEntity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "CharterCapital", table: "LegalEntities", type: "TEXT", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Fss", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "IfnsName", table: "LegalEntities", type: "TEXT", maxLength: 300, nullable: true);
            migrationBuilder.AddColumn<string>(name: "IncomeExpenseTaxRate", table: "LegalEntities", type: "TEXT", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<string>(name: "IncomeTaxRate", table: "LegalEntities", type: "TEXT", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Okato", table: "LegalEntities", type: "TEXT", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Okfs", table: "LegalEntities", type: "TEXT", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Okogu", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Okopf", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Okpo", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Oktmo", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Okved", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Pfr", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TaxOfficeCode", table: "LegalEntities", type: "TEXT", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TaxOfficePhone", table: "LegalEntities", type: "TEXT", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TaxRegion", table: "LegalEntities", type: "TEXT", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TaxRegistrationDate", table: "LegalEntities", type: "TEXT", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TaxSystemMode", table: "LegalEntities", type: "TEXT", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryAccountNumber", table: "LegalEntities", type: "TEXT", maxLength: 34, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryBankBik", table: "LegalEntities", type: "TEXT", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryBankName", table: "LegalEntities", type: "TEXT", maxLength: 300, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryCorrAccountNumber", table: "LegalEntities", type: "TEXT", maxLength: 34, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryInn", table: "LegalEntities", type: "TEXT", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryKbk", table: "LegalEntities", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryKpp", table: "LegalEntities", type: "TEXT", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TreasuryRecipientName", table: "LegalEntities", type: "TEXT", maxLength: 300, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "VatWithoutVat", table: "LegalEntities", type: "INTEGER", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CharterCapital", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Fss", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "IfnsName", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "IncomeExpenseTaxRate", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "IncomeTaxRate", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Okato", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Okfs", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Okogu", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Okopf", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Okpo", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Oktmo", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Okved", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Pfr", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TaxOfficeCode", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TaxOfficePhone", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TaxRegion", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TaxRegistrationDate", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TaxSystemMode", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryAccountNumber", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryBankBik", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryBankName", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryCorrAccountNumber", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryInn", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryKbk", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryKpp", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TreasuryRecipientName", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "VatWithoutVat", table: "LegalEntities");
        }
    }
}
