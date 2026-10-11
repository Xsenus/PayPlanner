using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceClientCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceClientCases",
                columns: table => new
                {
                    PaymentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceClientCases", x => new { x.PaymentId, x.ClientCaseId });
                    table.ForeignKey(
                        name: "FK_InvoiceClientCases_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceClientCases_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceClientCases_ClientCaseId",
                table: "InvoiceClientCases",
                column: "ClientCaseId");

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO InvoiceClientCases (PaymentId, ClientCaseId)
                SELECT Id, ClientCaseId
                FROM Payments
                WHERE DocumentKind = 'Invoice' AND ClientCaseId IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                DELETE FROM FormFieldRequirements
                WHERE Section = 'contract-invoices' AND FieldKey = 'CounterpartyName';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceClientCases");
        }
    }
}
