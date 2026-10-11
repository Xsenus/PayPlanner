using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddContractRelationsAndClientContractComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Contracts_CreatedAt",
                table: "Contracts");

            migrationBuilder.AddColumn<string>(
                name: "Comment",
                table: "ClientContracts",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContractActs",
                columns: table => new
                {
                    ContractId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractActs", x => new { x.ContractId, x.ActId });
                    table.ForeignKey(
                        name: "FK_ContractActs_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractActs_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractInvoices",
                columns: table => new
                {
                    ContractId = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractInvoices", x => new { x.ContractId, x.InvoiceId });
                    table.ForeignKey(
                        name: "FK_ContractInvoices_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractInvoices_Payments_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractPayments",
                columns: table => new
                {
                    ContractId = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractPayments", x => new { x.ContractId, x.PaymentId });
                    table.ForeignKey(
                        name: "FK_ContractPayments_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractPayments_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractActs_ActId",
                table: "ContractActs",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractInvoices_InvoiceId",
                table: "ContractInvoices",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractPayments_PaymentId",
                table: "ContractPayments",
                column: "PaymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractActs");

            migrationBuilder.DropTable(
                name: "ContractInvoices");

            migrationBuilder.DropTable(
                name: "ContractPayments");

            migrationBuilder.DropColumn(
                name: "Comment",
                table: "ClientContracts");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_CreatedAt",
                table: "Contracts",
                column: "CreatedAt");
        }
    }
}
