using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Stages intents in the caller's status transaction; never dispatches or saves it.</summary>
public static class PaymentOverdueNotificationQueue
{
    public const string TokenProperty = "OverdueNotificationToken";

    public static IReadOnlyList<PaymentOverdueNotificationOutbox> Stage(
        PaymentContext db, IEnumerable<Payment> payments, DateTime nowUtc)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Overdue events require the payment status transaction.");
        db.ChangeTracker.DetectChanges();
        var selected = payments.ToArray();
        if (selected.Select(payment => payment.Id).Distinct().Count() != selected.Length)
            throw new InvalidOperationException("A payment transition may only be staged once.");
        foreach (var payment in selected)
        {
            var entry = db.Entry(payment);
            if (payment.Id <= 0 || entry.State != EntityState.Modified || payment.IsPaid || payment.Amount <= 0m
                || payment.Status != PaymentStatus.Overdue || entry.Property(item => item.Status).OriginalValue != PaymentStatus.Pending
                || entry.Property<string?>(TokenProperty).IsModified)
                throw new InvalidOperationException("Only the current tracked Pending to Overdue transition can create an event.");
        }
        var rows = new List<PaymentOverdueNotificationOutbox>(selected.Length);
        foreach (var payment in selected)
        {
            var token = Guid.NewGuid().ToString("N");
            db.Entry(payment).Property<string?>(TokenProperty).CurrentValue = token;
            rows.Add(new()
            {
                PaymentId = payment.Id,
                TransitionToken = token,
                EventKey = $"payment-overdue:{payment.Id}:{token}",
                DocumentKind = payment.DocumentKind,
                DueDate = DateTime.SpecifyKind(payment.Date.Date, DateTimeKind.Utc),
                CreatedAt = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            });
        }
        db.PaymentOverdueNotificationOutboxes.AddRange(rows);
        return rows;
    }
}
