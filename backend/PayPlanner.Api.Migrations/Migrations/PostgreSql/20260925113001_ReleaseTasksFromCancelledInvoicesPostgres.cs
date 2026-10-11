using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql;

[DbContext(typeof(PostgreSqlPaymentContext))]
[Migration("20260925113001_ReleaseTasksFromCancelledInvoicesPostgres")]
public sealed class ReleaseTasksFromCancelledInvoicesPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "TaskJournalComments" ("TaskJournalItemId", "AuthorName", "Kind", "Text", "CreatedAt")
            SELECT task."Id", 'Система', 'history',
                   'Счёт №' || COALESCE(NULLIF(invoice."Number", ''), NULLIF(payment."Account", ''), payment."Id"::text)
                   || ' был аннулирован ранее. Платная задача снова ожидает выставления счёта.',
                   CURRENT_TIMESTAMP
            FROM "TaskJournalItems" AS task
            JOIN "Payments" AS payment ON payment."Id" = task."BillingInvoiceId"
            LEFT JOIN "Invoices" AS invoice ON invoice."Id" = payment."InvoiceId"
            WHERE payment."DocumentKind" = 'Invoice'
              AND (payment."Status" = 'Cancelled' OR payment."InvoiceWorkflowStatus" = 'Cancelled');

            UPDATE "TaskJournalItems" AS task
            SET "BillingInvoiceId" = NULL, "UpdatedAt" = CURRENT_TIMESTAMP
            FROM "Payments" AS payment
            WHERE payment."Id" = task."BillingInvoiceId"
              AND payment."DocumentKind" = 'Invoice'
              AND (payment."Status" = 'Cancelled' OR payment."InvoiceWorkflowStatus" = 'Cancelled');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restoring an obsolete link could hide work from billing again.
    }
}
