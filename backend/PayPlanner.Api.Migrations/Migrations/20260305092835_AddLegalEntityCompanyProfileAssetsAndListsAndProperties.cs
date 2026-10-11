using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalEntityCompanyProfileAssetsAndListsAndProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BankAccountsJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardsJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashRegistersJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CharterCapital",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChiefAccountantJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmailsJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPhonesJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectorSignatureDataUrl",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 12000000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentDirectorJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fss",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IfnsName",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncomeExpenseTaxRate",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncomeTaxRate",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoDataUrl",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 12000000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Okato",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Okfs",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Okogu",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Okopf",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Okpo",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Oktmo",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Okved",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Pfr",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequisitesEnglishJson",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleUserId",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealDataUrl",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 12000000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxOfficeCode",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxOfficePhone",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegion",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegistrationDate",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxSystemMode",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryAccountNumber",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryBankBik",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryBankName",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryCorrAccountNumber",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryInn",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryKbk",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryKpp",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreasuryRecipientName",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VatWithoutVat",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BaseCurrencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BaseCurrencies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Countries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationForms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationForms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceLanguages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLanguages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxSystemModes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSystemModes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxSystems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSystems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Timezones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Timezones", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_ResponsibleUserId",
                table: "LegalEntities",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseCurrencyDictionaryItems_IsActive",
                table: "BaseCurrencies",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BaseCurrencyDictionaryItems_IsActive_Name",
                table: "BaseCurrencies",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseCurrencyDictionaryItems_Name",
                table: "BaseCurrencies",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_CountryDictionaryItems_IsActive",
                table: "Countries",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CountryDictionaryItems_IsActive_Name",
                table: "Countries",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CountryDictionaryItems_Name",
                table: "Countries",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFormDictionaryItems_IsActive",
                table: "OrganizationForms",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFormDictionaryItems_IsActive_Name",
                table: "OrganizationForms",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFormDictionaryItems_Name",
                table: "OrganizationForms",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLanguageDictionaryItems_IsActive",
                table: "ServiceLanguages",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLanguageDictionaryItems_IsActive_Name",
                table: "ServiceLanguages",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLanguageDictionaryItems_Name",
                table: "ServiceLanguages",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemModeDictionaryItems_IsActive",
                table: "TaxSystemModes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemModeDictionaryItems_IsActive_Name",
                table: "TaxSystemModes",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemModeDictionaryItems_Name",
                table: "TaxSystemModes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemDictionaryItems_IsActive",
                table: "TaxSystems",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemDictionaryItems_IsActive_Name",
                table: "TaxSystems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemDictionaryItems_Name",
                table: "TaxSystems",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TimezoneDictionaryItems_IsActive",
                table: "Timezones",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TimezoneDictionaryItems_IsActive_Name",
                table: "Timezones",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TimezoneDictionaryItems_Name",
                table: "Timezones",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_LegalEntities_Users_ResponsibleUserId",
                table: "LegalEntities",
                column: "ResponsibleUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LegalEntities_Users_ResponsibleUserId",
                table: "LegalEntities");

            migrationBuilder.DropTable(
                name: "BaseCurrencies");

            migrationBuilder.DropTable(
                name: "Countries");

            migrationBuilder.DropTable(
                name: "OrganizationForms");

            migrationBuilder.DropTable(
                name: "ServiceLanguages");

            migrationBuilder.DropTable(
                name: "TaxSystemModes");

            migrationBuilder.DropTable(
                name: "TaxSystems");

            migrationBuilder.DropTable(
                name: "Timezones");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntities_ResponsibleUserId",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "BankAccountsJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "CardsJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "CashRegistersJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "CharterCapital",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "ChiefAccountantJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "ContactEmailsJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "ContactPhonesJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "DirectorSignatureDataUrl",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "DocumentDirectorJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Fss",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "IfnsName",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "IncomeExpenseTaxRate",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "IncomeTaxRate",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "LogoDataUrl",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Okato",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Okfs",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Okogu",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Okopf",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Okpo",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Oktmo",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Okved",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "Pfr",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "RequisitesEnglishJson",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "SealDataUrl",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TaxOfficeCode",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TaxOfficePhone",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TaxRegion",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TaxRegistrationDate",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TaxSystemMode",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryAccountNumber",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryBankBik",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryBankName",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryCorrAccountNumber",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryInn",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryKbk",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryKpp",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "TreasuryRecipientName",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "VatWithoutVat",
                table: "LegalEntities");
        }
    }
}
