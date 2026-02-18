using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAddressRecordsAndClientReferenceEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AddressRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    UnrestrictedValue = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    PostalCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Country = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    RegionWithType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CityWithType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StreetWithType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    House = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Flat = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    FiasId = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    KladrId = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    GeoLat = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    GeoLon = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressRecords", x => x.Id);
                    table.CheckConstraint("CK_AddressRecords_Owner", "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AddressRecords_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AddressRecords_LegalEntities_LegalEntityId",
                        column: x => x.LegalEntityId,
                        principalTable: "LegalEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPassports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    Series = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    IssuedBy = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DepartmentCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPassports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPassports_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OkinDictionaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OkinDictionaryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OkpdtrDictionaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OkpdtrDictionaryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OksoDictionaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OksoDictionaryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PowersOfAttorney",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PowersOfAttorney", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PowersOfAttorney_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_ClientId",
                table: "AddressRecords",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_ClientId_Type_IsPrimary",
                table: "AddressRecords",
                columns: new[] { "ClientId", "Type", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_FiasId",
                table: "AddressRecords",
                column: "FiasId");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_LegalEntityId",
                table: "AddressRecords",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_LegalEntityId_Type_IsPrimary",
                table: "AddressRecords",
                columns: new[] { "LegalEntityId", "Type", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId",
                table: "ClientPassports",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OkinDictionaryItems_Code",
                table: "OkinDictionaryItems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OkinDictionaryItems_IsActive_Name",
                table: "OkinDictionaryItems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OkpdtrDictionaryItems_Code",
                table: "OkpdtrDictionaryItems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OkpdtrDictionaryItems_IsActive_Name",
                table: "OkpdtrDictionaryItems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OksoDictionaryItems_Code",
                table: "OksoDictionaryItems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OksoDictionaryItems_IsActive_Name",
                table: "OksoDictionaryItems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PowersOfAttorney_ClientId_IsActive",
                table: "PowersOfAttorney",
                columns: new[] { "ClientId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PowersOfAttorney_Number",
                table: "PowersOfAttorney",
                column: "Number");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AddressRecords");

            migrationBuilder.DropTable(
                name: "ClientPassports");

            migrationBuilder.DropTable(
                name: "OkinDictionaryItems");

            migrationBuilder.DropTable(
                name: "OkpdtrDictionaryItems");

            migrationBuilder.DropTable(
                name: "OksoDictionaryItems");

            migrationBuilder.DropTable(
                name: "PowersOfAttorney");
        }
    }
}
