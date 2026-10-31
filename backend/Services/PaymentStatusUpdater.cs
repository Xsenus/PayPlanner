using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Services.Payments;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Каждую минуту переводит неоплаченные платежи со статусом Pending
    /// в статус Overdue по локальному дню ответственного или системному timezone.
    /// </summary>
    public sealed class PaymentStatusUpdater : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PaymentStatusUpdater> _logger;

        private readonly SemaphoreSlim _runLock = new(1, 1);
        private const int BatchSize = 200;
        private const int MaximumRelatedPayments = 5000;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        public PaymentStatusUpdater(
            IServiceScopeFactory scopeFactory,
            ILogger<PaymentStatusUpdater> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PaymentStatusUpdater started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PaymentStatusUpdater tick failed.");
                }

                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                }
            }

            _logger.LogInformation("PaymentStatusUpdater stopped.");
        }

        public async Task<int> RunOnceAsync(CancellationToken ct = default)
        {
            await _runLock.WaitAsync(ct);
            try
            {
                var nowUtc = DateTime.UtcNow;
                var lastId = 0;
                int? throughId = null;
                var affected = 0;
                while (true)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                    int[] changedIds;
                    await using (var lease = await BalanceApplicationLease.BeginSystemAsync(db, ct))
                    {
                        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
                        var protectedPayments = PaymentCreditInvariantScope.CancellationProtectedPayments(db);
                        var query = db.Payments.ApplyUnambiguousInvoiceLinks().Where(payment => !payment.IsPaid
                            && payment.Status == PaymentStatus.Pending && payment.Amount > 0m && payment.Date.Date <= nowUtc.Date)
                            .Where(payment => !protectedPayments.Any(closed => closed.Id == payment.Id));
                        throughId ??= await query.Select(payment => (int?)payment.Id).MaxAsync(ct) ?? 0;
                        var selected = await query.AsNoTracking().Where(payment => payment.Id > lastId && payment.Id <= throughId.Value)
                            .OrderBy(payment => payment.Id).Select(payment => new { payment.Id, payment.InvoiceId }).Take(BatchSize).ToArrayAsync(ct);
                        if (selected.Length == 0)
                        {
                            await lease.CommitAsync(ct);
                            break;
                        }
                        var ids = selected.Select(payment => payment.Id).ToArray();
                        lastId = ids[^1];
                        var invoiceIds = selected.Where(payment => payment.InvoiceId.HasValue).Select(payment => payment.InvoiceId!.Value).Distinct().ToArray();
                        var graph = db.Payments.Where(payment => ids.Contains(payment.Id)
                            || payment.InvoiceId.HasValue && invoiceIds.Contains(payment.InvoiceId.Value));
                        var graphIds = await graph.AsNoTracking().OrderBy(payment => payment.Id).Select(payment => payment.Id)
                            .Take(MaximumRelatedPayments + 1).ToArrayAsync(ct);
                        if (graphIds.Length > MaximumRelatedPayments)
                            throw new InvalidOperationException("Слишком много связанных платежей для фоновой проверки просрочки. Требуется отдельная проверка счёта.");
                        await lease.LockPaymentsAsync(graphIds, ct);
                        var currentIds = await query.AsNoTracking().Where(payment => ids.Contains(payment.Id)).OrderBy(payment => payment.Id).Select(payment => payment.Id).ToArrayAsync(ct);
                        var currentGraph = await graph.AsNoTracking().OrderBy(payment => payment.Id).Select(payment => payment.Id).Take(MaximumRelatedPayments + 1).ToArrayAsync(ct);
                        if (!ids.SequenceEqual(currentIds) || !graphIds.SequenceEqual(currentGraph)) throw new DbUpdateConcurrencyException("Связанные платежи изменились во время фоновой проверки просрочки.");

                        var payments = await graph.ToListAsync(ct);
                        // Only selected rows need responsible profiles; aggregate
                        // siblings must not multiply the assignment payload.
                        await db.Set<PaymentResponsible>().Where(responsible => ids.Contains(responsible.PaymentId)).LoadAsync(ct);
                        var byId = payments.ToDictionary(payment => payment.Id);
                        var candidates = ids.Select(id => byId[id]).ToArray();
                        var responsibleUserIds = candidates.SelectMany(payment => payment.Responsibles).Select(responsible => responsible.UserId)
                            .Where(id => id > 0).Distinct().ToArray();
                        var userTimeZoneIds = responsibleUserIds.Length == 0 ? new Dictionary<int, string?>()
                            : await db.UserSettings.AsNoTracking().Where(setting => responsibleUserIds.Contains(setting.UserId)
                                && setting.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
                                .Select(setting => new { setting.UserId, Value = (string?)setting.Value })
                                .ToDictionaryAsync(setting => setting.UserId, setting => setting.Value, ct);
                        var fallbackTimeZone = CalendarTimeZoneHelper.ResolveTimeZone(null);
                        var due = candidates.Where(payment => payment.Date.Date < ResolvePaymentLocalToday(payment, nowUtc, userTimeZoneIds, fallbackTimeZone)).ToArray();
                        var originalMoney = payments.ToDictionary(payment => payment.Id, payment => (payment.Amount, payment.PaidAmount));
                        using var credit = new PaymentCreditInvariantScope(db);
                        credit.IncludePayments(payments);
                        var overdueStatusId = await db.PaymentStatuses.Where(status => status.Name == "Overdue").Select(status => (int?)status.Id).FirstOrDefaultAsync(ct);
                        foreach (var payment in due)
                        {
                            payment.Status = PaymentStatus.Overdue;
                            if (overdueStatusId.HasValue) payment.PaymentStatusId = overdueStatusId.Value;
                        }
                        var affectedInvoiceIds = due.Where(payment => payment.InvoiceId.HasValue).Select(payment => payment.InvoiceId!.Value).ToHashSet();
                        foreach (var group in payments.Where(payment => payment.InvoiceId.HasValue && affectedInvoiceIds.Contains(payment.InvoiceId.Value)).GroupBy(payment => payment.InvoiceId!.Value))
                        {
                            var evaluation = InvoicePaymentStateService.EvaluateInvoiceAggregate(group.ToArray(), nowUtc);
                            if (evaluation is { HasChanges: true }) InvoicePaymentStateService.ApplySnapshot(byId[evaluation.RepresentativePaymentId], evaluation.Suggested);
                        }
                        if (payments.Any(payment => originalMoney[payment.Id] != (payment.Amount, payment.PaidAmount)))
                            throw new InvalidOperationException("Фоновая просрочка не изменяет исторические суммы оплаты. Требуется отдельная сверка счёта.");
                        changedIds = due.Where(payment => payment.Status == PaymentStatus.Overdue).Select(payment => payment.Id).ToArray();
                        if (due.Length > 0)
                        {
                            await db.SaveChangesAsync(ct);
                            if (!await credit.KeepsAppliedCreditAsync(ct)) throw new InvalidOperationException(PaymentCreditInvariantScope.RejectionMessage);
                        }
                        await lease.CommitAsync(ct);
                    }

                    affected += changedIds.Length;
                    var notificationService = scope.ServiceProvider.GetService<INotificationService>();
                    if (changedIds.Length > 0 && notificationService is not null)
                    {
                        var messages = await db.Payments.AsNoTracking().Where(payment => changedIds.Contains(payment.Id)
                            && payment.Status == PaymentStatus.Overdue && !payment.IsPaid).Include(payment => payment.Responsibles).ToArrayAsync(ct);
                        var errors = new List<Exception>();
                        var errorCount = 0;
                        foreach (var payment in messages)
                        {
                            var isInvoice = payment.DocumentKind == DocumentKind.Invoice;
                            var recipients = isInvoice ? await ResolveInvoiceNotificationRecipientsAsync(db, payment, ct)
                                : await ResolvePaymentNotificationRecipientsAsync(db, payment, ct);
                            foreach (var recipient in recipients)
                            {
                                ct.ThrowIfCancellationRequested();
                                try
                                {
                                    using var dispatchScope = _scopeFactory.CreateScope();
                                    var dispatcher = dispatchScope.ServiceProvider.GetService<INotificationService>();
                                    if (dispatcher is null) continue;
                                    if (isInvoice)
                                        await dispatcher.NotifyInvoiceChangedAsync(payment, ["Статус оплаты счёта: Просрочен"], "CRM", 0, [recipient], ct);
                                    else
                                        await dispatcher.NotifyPaymentChangedAsync(payment, ["Статус: Ожидается -> Просрочен"], "CRM", 0, [recipient], ct);
                                }
                                catch (Exception error) when (!ct.IsCancellationRequested)
                                {
                                    errorCount++;
                                    if (errors.Count < 32) { errors.Add(error); _logger.LogError(error, "Overdue notification failed for payment {PaymentId}.", payment.Id); }
                                }
                            }
                        }
                        if (errorCount == 1 && errors[0] is not OperationCanceledException) ExceptionDispatchInfo.Capture(errors[0]).Throw();
                        if (errorCount > 0) throw new AggregateException($"Не удалось создать уведомлений о просрочке: {errorCount}.", errors);
                    }
                }
                if (affected > 0) _logger.LogInformation("Marked {Count} payment(s) as Overdue.", affected);
                return affected;
            }
            finally { _runLock.Release(); }
        }

        private static async Task<IReadOnlyCollection<int>> ResolvePaymentNotificationRecipientsAsync(
            PaymentContext db,
            Payment payment,
            CancellationToken ct)
        {
            var candidateUserIds = payment.Responsibles
                .Select(item => item.UserId)
                .Where(userId => userId > 0)
                .Distinct()
                .ToArray();
            if (candidateUserIds.Length == 0)
            {
                return Array.Empty<int>();
            }

            var allowedUserIds = await db.Users
                .AsNoTracking()
                .Where(user => candidateUserIds.Contains(user.Id)
                    && user.IsActive
                    && user.IsApproved
                    && user.Role != null
                    && (user.Role.Name == "admin"
                        || user.Role.Permissions.Any(permission =>
                            permission.Section == RolePermissionCatalog.Payments
                            && permission.CanView)))
                .Select(user => user.Id)
                .ToListAsync(ct);

            // NotificationService centrally revalidates object visibility before persisting notifications.
            return allowedUserIds;
        }

        private static async Task<IReadOnlyCollection<int>> ResolveInvoiceNotificationRecipientsAsync(
            PaymentContext db,
            Payment payment,
            CancellationToken ct)
        {
            var candidateUserIds = payment.Responsibles
                .Select(item => item.UserId)
                .Where(userId => userId > 0)
                .Distinct()
                .ToArray();
            if (candidateUserIds.Length == 0)
            {
                return Array.Empty<int>();
            }

            var allowedUserIds = await db.Users
                .AsNoTracking()
                .Where(user => candidateUserIds.Contains(user.Id)
                    && user.IsActive
                    && user.IsApproved
                    && user.Role != null
                    && (user.Role.Name == "admin"
                        || user.Role.Permissions.Any(permission =>
                            permission.Section == RolePermissionCatalog.Accounts
                            && permission.CanView)))
                .Select(user => user.Id)
                .ToListAsync(ct);

            // NotificationService centrally revalidates object visibility before persisting notifications.
            return allowedUserIds;
        }

        private static DateTime ResolvePaymentLocalToday(
            Payment payment,
            DateTime nowUtc,
            IReadOnlyDictionary<int, string?> userTimeZoneIds,
            TimeZoneInfo fallbackTimeZone)
        {
            var responsibleUserId = payment.Responsibles
                .Select(responsible => responsible.UserId)
                .Where(userId => userId > 0)
                .OrderBy(userId => userId)
                .Cast<int?>()
                .FirstOrDefault();

            var timeZone = responsibleUserId.HasValue && userTimeZoneIds.TryGetValue(responsibleUserId.Value, out var timeZoneId)
                ? CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId)
                : fallbackTimeZone;

            return PaymentDomainService.ResolveLocalToday(nowUtc, timeZone);
        }
    }
}
