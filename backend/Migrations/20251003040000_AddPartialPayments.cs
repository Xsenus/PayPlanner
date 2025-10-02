using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <summary>
    /// ADDITIVE ONLY: Creates payment_schedule table for tracking partial payments
    /// NO CHANGES to existing Payments table
    /// </summary>
    public partial class AddPartialPayments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_schedule",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    case_id = table.Column<int>(type: "INTEGER", nullable: false),
                    client_id = table.Column<int>(type: "INTEGER", nullable: true),
                    related_payment_id = table.Column<int>(type: "INTEGER", nullable: true),
                    scheduled_amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    due_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "Planned"),
                    paid_amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false, defaultValue: 0),
                    paid_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_schedule", x => x.id);
                    table.ForeignKey(
                        name: "FK_payment_schedule_Cases_case_id",
                        column: x => x.case_id,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_payment_schedule_Clients_client_id",
                        column: x => x.client_id,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_payment_schedule_Payments_related_payment_id",
                        column: x => x.related_payment_id,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_schedule_case_id",
                table: "payment_schedule",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "IX_payment_schedule_status_due_date",
                table: "payment_schedule",
                columns: new[] { "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_schedule_related_payment_id",
                table: "payment_schedule",
                column: "related_payment_id");

            // Create view for receivables status summary
            migrationBuilder.Sql(@"
                CREATE VIEW vw_receivables_status AS
                SELECT
                    ps.case_id,
                    c.CaseNumber,
                    SUM(CASE WHEN ps.status = 'Planned' AND ps.due_date >= date('now') THEN ps.scheduled_amount ELSE 0 END) as current_planned,
                    SUM(CASE WHEN ps.status = 'Overdue' THEN ps.scheduled_amount ELSE 0 END) as overdue_amount,
                    SUM(CASE WHEN ps.status = 'Paid' THEN ps.paid_amount ELSE 0 END) as total_paid,
                    COUNT(CASE WHEN ps.status = 'Overdue' THEN 1 END) as overdue_count
                FROM payment_schedule ps
                INNER JOIN Cases c ON ps.case_id = c.Id
                GROUP BY ps.case_id, c.CaseNumber;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS vw_receivables_status;");
            migrationBuilder.DropTable(name: "payment_schedule");
        }
    }
}
