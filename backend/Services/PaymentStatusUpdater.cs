using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Каждую минуту переводит неоплаченные платежи со статусом Pending,
    /// дата которых меньше текущего момента (UTC), в статус Overdue.
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

            var baseQuery = db.Payments
                .Where(p => !p.IsPaid
                            && p.Status == PaymentStatus.Pending
                            && p.Date.Date < nowUtc.Date);

            int affected;

            if (overdueStatusId.HasValue)
            {
                affected = await baseQuery.ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(p => p.Status, p => PaymentStatus.Overdue)
                        .SetProperty(p => p.PaymentStatusId, p => overdueStatusId.Value),
                    ct);
            }
            else
            {
                affected = await baseQuery.ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(p => p.Status, p => PaymentStatus.Overdue),
                    ct);
            }

            if (affected > 0)
            {
                _logger.LogInformation("Marked {Count} payment(s) as Overdue.", affected);
            }
        }
    }
}
