using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Stages intents in the caller's status transaction; never dispatches or saves it.</summary>
public static class PaymentOverdueNotificationQueue
{
    public const string TokenProperty = "OverdueNotificationToken";

    internal static IQueryable<Payment> CurrentPayments(PaymentContext db, PaymentOverdueNotificationOutbox item)
    {
        var protectedPayments = PaymentCreditInvariantScope.CancellationProtectedPayments(db);
        return db.Payments.AsNoTracking().ApplyUnambiguousInvoiceLinks()
            .Where(payment => payment.Id == item.PaymentId && payment.Status == PaymentStatus.Overdue
                && !payment.IsPaid && payment.Amount > 0m && payment.DocumentKind == item.DocumentKind
                && payment.Date.Date == item.DueDate && EF.Property<string?>(payment, TokenProperty) == item.TransitionToken
                && !protectedPayments.Any(closed => closed.Id == payment.Id));
    }

    internal static IQueryable<int> RecipientIds(PaymentContext db, PaymentOverdueNotificationOutbox item)
    {
        var section = item.DocumentKind == DocumentKind.Invoice ? RolePermissionCatalog.Accounts : RolePermissionCatalog.Payments;
        return db.Users.AsNoTracking().Where(user => user.IsActive && user.IsApproved && user.Role != null
                && (user.Role.Name == "admin" || user.Role.Permissions.Any(permission => permission.Section == section && permission.CanView))
                && db.Set<PaymentResponsible>().Any(responsible => responsible.PaymentId == item.PaymentId && responsible.UserId == user.Id))
            .Select(user => user.Id);
    }

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
