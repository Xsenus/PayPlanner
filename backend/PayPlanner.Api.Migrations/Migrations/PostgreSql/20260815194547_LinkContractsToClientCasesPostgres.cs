using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class LinkContractsToClientCasesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseContracts",
                columns: table => new
                {
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: false)
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
                INSERT INTO "ClientCaseContracts" ("ClientCaseId", "ContractId")
                SELECT document."ClientCaseId", SUBSTRING(document."DocumentKey" FROM 10)::integer
                FROM "ClientCaseDocuments" AS document
                INNER JOIN "Contracts" AS contract
                    ON contract."Id" = SUBSTRING(document."DocumentKey" FROM 10)::integer
                WHERE document."DocumentKey" ~ '^contract-[0-9]+$'
                ON CONFLICT DO NOTHING;
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
