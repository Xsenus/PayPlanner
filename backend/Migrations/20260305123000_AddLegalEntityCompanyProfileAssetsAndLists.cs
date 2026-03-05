using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddLegalEntityCompanyProfileAssetsAndLists : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "BankAccountsJson", table: "LegalEntities", type: "TEXT", maxLength: 16000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "CashRegistersJson", table: "LegalEntities", type: "TEXT", maxLength: 8000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "CardsJson", table: "LegalEntities", type: "TEXT", maxLength: 8000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "ChiefAccountantJson", table: "LegalEntities", type: "TEXT", maxLength: 8000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "DirectorSignatureDataUrl", table: "LegalEntities", type: "TEXT", maxLength: 4000000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "DocumentDirectorJson", table: "LegalEntities", type: "TEXT", maxLength: 8000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "LogoDataUrl", table: "LegalEntities", type: "TEXT", maxLength: 4000000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "RequisitesEnglishJson", table: "LegalEntities", type: "TEXT", maxLength: 8000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "SealDataUrl", table: "LegalEntities", type: "TEXT", maxLength: 4000000, nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BankAccountsJson", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "CashRegistersJson", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "CardsJson", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "ChiefAccountantJson", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "DirectorSignatureDataUrl", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "DocumentDirectorJson", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "LogoDataUrl", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "RequisitesEnglishJson", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "SealDataUrl", table: "LegalEntities");
        }
    }
}
