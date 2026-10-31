using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Recovers committed overdue events in finite batches with expiring claims.</summary>
public sealed class PaymentOverdueNotificationDispatchProcessor(IServiceScopeFactory scopes,
    TimeProvider? timeProvider = null, ILogger<PaymentOverdueNotificationDispatchProcessor>? logger = null)
{
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;
    private ILogger Log => logger ?? NullLogger<PaymentOverdueNotificationDispatchProcessor>.Instance;

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Overdue recovery requires a clean committed context.");
        var now = Clock.GetUtcNow().UtcDateTime;
        var ids = await db.PaymentOverdueNotificationOutboxes.AsNoTracking()
            .Where(row => row.CompletedAt == null && (row.LeaseUntil == null || row.LeaseUntil <= now))
            .OrderBy(row => row.LastAttemptAt).ThenBy(row => row.Id).Select(row => row.Id).Take(50).ToArrayAsync(ct);
        var errors = new List<Exception>();
        var completed = 0;
        foreach (var id in ids)
        {
            try { if (await ProcessAsync(id, ct)) completed++; }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                Log.LogWarning(error, "Overdue event {EventId} remains queued.", id);
                if (errors.Count < 32) errors.Add(error);
            }
        }
        // Retain pending events indefinitely; only bounded, completed history is purged.
        var before = now.AddDays(-30);
        var old = await db.PaymentOverdueNotificationOutboxes.AsNoTracking().Where(row => row.CompletedAt < before)
            .OrderBy(row => row.CompletedAt).ThenBy(row => row.Id).Select(row => row.Id).Take(200).ToArrayAsync(ct);
        if (old.Length > 0) await db.PaymentOverdueNotificationOutboxes.Where(row => old.Contains(row.Id) && row.CompletedAt < before).ExecuteDeleteAsync(ct);
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 0) throw new AggregateException("Не удалось восстановить часть уведомлений о просрочке.", errors);
        return completed;
    }

    public async Task<bool> ProcessAsync(long id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Overdue recovery requires a clean committed context.");
        if (scope.ServiceProvider.GetService<INotificationService>() is not IDurablePaymentOverdueNotificationService) return false;
        var now = Clock.GetUtcNow().UtcDateTime;
        var attempts = await db.PaymentOverdueNotificationOutboxes.AsNoTracking()
            .Where(row => row.Id == id && row.CompletedAt == null && (row.LeaseUntil == null || row.LeaseUntil <= now))
            .Select(row => (int?)row.Attempts).SingleOrDefaultAsync(ct);
        if (!attempts.HasValue) return false;
        var token = Guid.NewGuid().ToString("N");
        var until = now.AddMinutes(Math.Min(60, 2 * (1 << Math.Clamp(attempts.Value, 0, 5))));
        if (await db.PaymentOverdueNotificationOutboxes.Where(row => row.Id == id && row.CompletedAt == null
                && row.Attempts == attempts.Value && (row.LeaseUntil == null || row.LeaseUntil <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseToken, token).SetProperty(row => row.LeaseUntil, (DateTime?)until)
                .SetProperty(row => row.LastAttemptAt, (DateTime?)now).SetProperty(row => row.Attempts, row => row.Attempts < 30 ? row.Attempts + 1 : 30), ct) != 1) return false;
        var item = await db.PaymentOverdueNotificationOutboxes.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id && row.CompletedAt == null && row.LeaseToken == token, ct);
        if (item is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var errors = new List<Exception>();
        if (await PaymentOverdueNotificationQueue.CurrentPayments(db, item).AnyAsync(timeout.Token))
        {
            var query = PaymentOverdueNotificationQueue.RecipientIds(db, item);
            var throughId = await query.Select(userId => (int?)userId).MaxAsync(timeout.Token) ?? 0;
            var lastId = 0;
            while (true)
            {
                var users = await query.Where(userId => userId > lastId && userId <= throughId).OrderBy(userId => userId).Take(50).ToArrayAsync(timeout.Token);
                if (users.Length == 0) break;
                lastId = users[^1];
                foreach (var userId in users)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    try
                    {
                        using var dispatch = scopes.CreateScope();
                        var service = dispatch.ServiceProvider.GetService<INotificationService>() as IDurablePaymentOverdueNotificationService
                            ?? throw new InvalidOperationException("Durable overdue dispatcher is unavailable.");
                        await service.NotifyOverdueForEventAsync(item, token, userId, timeout.Token);
                    }
                    catch (Exception error) when (!timeout.IsCancellationRequested)
                    {
                        if (errors.Count < 32) errors.Add(error);
                    }
                }
            }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 0) throw new AggregateException("Не удалось создать часть уведомлений о просрочке.", errors);
        var finished = Clock.GetUtcNow().UtcDateTime;
        return await db.PaymentOverdueNotificationOutboxes.Where(row => row.Id == id && row.CompletedAt == null
                && row.LeaseToken == token && row.LeaseUntil > finished)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.CompletedAt, (DateTime?)finished)
                .SetProperty(row => row.LeaseToken, (string?)null).SetProperty(row => row.LeaseUntil, (DateTime?)null), ct) == 1;
    }
}
