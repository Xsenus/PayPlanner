using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyBankAccountBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyBankAccountBindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileAccountId = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    ProfileAccountKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BankConnectionAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AccountNumber = table.Column<string>(type: "TEXT", maxLength: 34, nullable: false),
                    Bik = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ConfirmedMatchVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RevokedByUserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevocationMatchVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyBankAccountBindings", x => x.Id);
                    table.CheckConstraint("CK_CompanyBankBinding_RevocationTime", "(\"RevokedAtUtc\" IS NULL AND \"RevocationMatchVersion\" IS NULL AND \"RevokedByUserId\" IS NULL AND \"RevokedByUserName\" IS NULL) OR (\"RevokedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" >= \"CreatedAtUtc\" AND \"RevocationMatchVersion\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CompanyBankAccountBindings_BankConnectionAccounts_BankConnectionAccountId",
                        column: x => x.BankConnectionAccountId,
                        principalTable: "BankConnectionAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyBankAccountBindings_LegalEntities_LegalEntityId",
                        column: x => x.LegalEntityId,
                        principalTable: "LegalEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyBankAccountBindings_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompanyBankAccountBindings_Users_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankAccountBindings_CreatedByUserId",
                table: "CompanyBankAccountBindings",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankAccountBindings_RevokedByUserId",
                table: "CompanyBankAccountBindings",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankBinding_ActiveBank",
                table: "CompanyBankAccountBindings",
                column: "BankConnectionAccountId",
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankBinding_ActiveProfile",
                table: "CompanyBankAccountBindings",
                columns: new[] { "LegalEntityId", "ProfileAccountKey" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyBankAccountBindings");
        }
    }
}
