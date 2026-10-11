using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations;

[DbContext(typeof(PaymentContext))]
[Migration("20260925113000_ReleaseTasksFromCancelledInvoices")]
public sealed class ReleaseTasksFromCancelledInvoices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "TaskJournalComments" ("TaskJournalItemId", "AuthorName", "Kind", "Text", "CreatedAt")
            SELECT task."Id", 'Система', 'history',
                   'Счёт №' || COALESCE(NULLIF(invoice."Number", ''), NULLIF(payment."Account", ''), CAST(payment."Id" AS TEXT))
                   || ' был аннулирован ранее. Платная задача снова ожидает выставления счёта.',
                   CURRENT_TIMESTAMP
            FROM "TaskJournalItems" AS task
            JOIN "Payments" AS payment ON payment."Id" = task."BillingInvoiceId"
            LEFT JOIN "Invoices" AS invoice ON invoice."Id" = payment."InvoiceId"
            WHERE payment."DocumentKind" = 'Invoice'
              AND (payment."Status" = 'Cancelled' OR payment."InvoiceWorkflowStatus" = 'Cancelled');

            UPDATE "TaskJournalItems"
            SET "BillingInvoiceId" = NULL, "UpdatedAt" = CURRENT_TIMESTAMP
            WHERE "BillingInvoiceId" IN (
                SELECT "Id" FROM "Payments"
                WHERE "DocumentKind" = 'Invoice'
                  AND ("Status" = 'Cancelled' OR "InvoiceWorkflowStatus" = 'Cancelled')
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restoring an obsolete link could hide work from billing again.
    }
}
