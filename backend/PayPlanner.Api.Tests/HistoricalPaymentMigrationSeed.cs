using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

// These records precede the migrations under test. Do not route their inserts
// through the current EF model or later SaveChanges hooks.
internal static class HistoricalPaymentMigrationSeed
{
    internal static async Task InsertPaymentAsync(PaymentContext db, Payment payment)
    {
        var paymentCreated = Timestamp(db, "paymentCreatedAt", payment.CreatedAt);
        DateOnly? accountDate = payment.AccountDate.HasValue ? DateOnly.FromDateTime(payment.AccountDate.Value) : null;
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Payments"
                ("Id", "InvoiceId", "ClientId", "LegalEntityId", "Account", "AccountDate", "Amount", "CreatedAt", "Date", "PlannedDate",
                 "Status", "Type", "DocumentKind", "InvoiceWorkflowStatus", "Description", "Notes", "SystemNotes", "PaidAmount", "IsPaid", "CounterpartyName")
            VALUES ({payment.Id}, {payment.InvoiceId}, {payment.ClientId}, {payment.LegalEntityId}, {payment.Account}, {accountDate}, {payment.Amount}, {paymentCreated},
                    {DateOnly.FromDateTime(payment.Date)}, {DateOnly.FromDateTime(payment.PlannedDate)}, {payment.Status.ToString()}, {payment.Type.ToString()},
                    {payment.DocumentKind.ToString()}, {payment.InvoiceWorkflowStatus}, {payment.Description}, {payment.Notes}, {payment.SystemNotes},
                    {payment.PaidAmount}, {payment.IsPaid}, {payment.CounterpartyName})
            """));
    }

    internal static async Task InsertLedgerAsync(PaymentContext db, ClientBalanceTransaction row)
    {
        var created = Timestamp(db, "ledgerCreatedAt", row.CreatedAt);
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ClientBalanceTransactions"
                ("Id", "ClientId", "LegalEntityId", "PaymentId", "Amount", "Type", "Direction", "Description", "ExternalKey", "CreatedAt")
            VALUES ({row.Id}, {row.ClientId}, {row.LegalEntityId}, {row.PaymentId}, {row.Amount}, {row.Type.ToString()},
                    {row.Direction.ToString()}, {row.Description}, {row.ExternalKey}, {created})
            """));
    }

    private static object Timestamp(PaymentContext db, string name, DateTime value)
        => db.Database.IsNpgsql()
            ? new NpgsqlParameter(name, NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified) }
            : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
