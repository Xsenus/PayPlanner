using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class LinkContractsToClientCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseContracts",
                columns: table => new
                {
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContractId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseContracts", x => new { x.ClientCaseId, x.ContractId });
                    table.ForeignKey(
                        name: "FK_ClientCaseContracts_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseContracts_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseContracts_ContractId",
                table: "ClientCaseContracts",
                column: "ContractId");

            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO "ClientCaseContracts" ("ClientCaseId", "ContractId")
                SELECT document."ClientCaseId", CAST(SUBSTR(document."DocumentKey", 10) AS INTEGER)
                FROM "ClientCaseDocuments" AS document
                INNER JOIN "Contracts" AS contract
                    ON contract."Id" = CAST(SUBSTR(document."DocumentKey", 10) AS INTEGER)
                WHERE document."DocumentKey" LIKE 'contract-%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientCaseContracts");
        }
    }
}
