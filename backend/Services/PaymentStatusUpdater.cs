using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
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
                    await TickAsync(stoppingToken);
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

        private async Task TickAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();

            var nowUtc = DateTime.UtcNow;

            int? overdueStatusId = await db.PaymentStatuses
                .Where(s => s.Name == "Overdue")
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);

            var candidates = await db.Payments
                .Include(payment => payment.Responsibles)
                .Where(payment =>
                    !payment.IsPaid
                    && payment.Status == PaymentStatus.Pending
                    && payment.Date.Date <= nowUtc.Date)
                .ToListAsync(ct);

            if (candidates.Count == 0)
            {
                return;
            }

            var responsibleUserIds = candidates
                .SelectMany(payment => payment.Responsibles)
                .Select(responsible => responsible.UserId)
                .Where(userId => userId > 0)
                .Distinct()
                .ToArray();

            Dictionary<int, string?> userTimeZoneIds = responsibleUserIds.Length == 0
                ? new Dictionary<int, string?>()
                : await db.UserSettings
                    .AsNoTracking()
                    .Where(setting =>
                        responsibleUserIds.Contains(setting.UserId)
                        && setting.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
                    .Select(setting => new { setting.UserId, Value = (string?)setting.Value })
                    .ToDictionaryAsync(setting => setting.UserId, setting => setting.Value, ct);

            var fallbackTimeZone = CalendarTimeZoneHelper.ResolveTimeZone(null);
            var affectedPayments = candidates
                .Where(payment => payment.Date.Date < ResolvePaymentLocalToday(payment, nowUtc, userTimeZoneIds, fallbackTimeZone))
                .ToList();

            foreach (var payment in affectedPayments)
            {
                payment.Status = PaymentStatus.Overdue;
                if (overdueStatusId.HasValue)
                {
                    payment.PaymentStatusId = overdueStatusId.Value;
                }
            }

            var affectedInvoiceIds = affectedPayments
                .Where(payment => payment.InvoiceId.HasValue)
                .Select(payment => payment.InvoiceId!.Value)
                .Distinct()
                .ToList();

            var affected = affectedPayments.Count;

            if (affected > 0)
            {
                await InvoicePaymentStateService.RefreshInvoiceAggregatesAsync(db, affectedInvoiceIds, nowUtc, ct);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Marked {Count} payment(s) as Overdue.", affected);
            }
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
