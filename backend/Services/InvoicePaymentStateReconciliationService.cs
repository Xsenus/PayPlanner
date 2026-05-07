using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed class InvoicePaymentStateReconciliationService : BackgroundService
{
    private const string ConfigurationSection = "InvoicePaymentStateReconciliation";
    private const int DefaultBatchSize = 500;
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InvoicePaymentStateReconciliationService> _logger;

    public InvoicePaymentStateReconciliationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InvoicePaymentStateReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            _logger.LogInformation("Invoice payment state reconciliation is disabled.");
            return;
        }

        _logger.LogInformation("Invoice payment state reconciliation started.");

        try
        {
            await Task.Delay(GetStartupDelay(), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunOnceAsync(stoppingToken);
                if (result.Applied > 0)
                {
                    _logger.LogInformation(
                        "Invoice payment state reconciliation fixed {Applied} of {Changed} changed invoice aggregate(s), scanned {Scanned}.",
                        result.Applied,
                        result.Changed,
                        result.Scanned);
                }
                else
                {
                    _logger.LogDebug(
                        "Invoice payment state reconciliation finished without fixes, scanned {Scanned}.",
                        result.Scanned);
                }

                await Task.Delay(GetInterval(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invoice payment state reconciliation failed.");

                try
                {
                    await Task.Delay(ErrorDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Invoice payment state reconciliation stopped.");
    }

    public async Task<InvoicePaymentStateReconciliationResult> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var now = DateTime.UtcNow;
        var batchSize = GetBatchSize();
        var result = new InvoicePaymentStateReconciliationResult();

        var lastInvoiceId = 0;
        while (!ct.IsCancellationRequested)
        {
            var invoiceIds = await db.Payments
                .Where(payment =>
                    payment.DocumentKind == DocumentKind.Invoice
                    && payment.InvoiceId.HasValue
                    && payment.InvoiceId.Value > lastInvoiceId)
                .Select(payment => payment.InvoiceId!.Value)
                .Distinct()
                .OrderBy(id => id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (invoiceIds.Count == 0)
            {
                break;
            }

            lastInvoiceId = invoiceIds[^1];
            var payments = await db.Payments
                .Where(payment => payment.InvoiceId.HasValue && invoiceIds.Contains(payment.InvoiceId.Value))
                .ToListAsync(ct);

            foreach (var group in payments.GroupBy(payment => payment.InvoiceId!.Value))
            {
                ApplyEvaluation(group.ToList(), now, result);
            }

            if (result.PendingSaves > 0)
            {
                await db.SaveChangesAsync(ct);
                result.ClearPendingSaves();
            }
        }

        var lastStandalonePaymentId = 0;
        while (!ct.IsCancellationRequested)
        {
            var standalonePayments = await db.Payments
                .Where(payment =>
                    payment.DocumentKind == DocumentKind.Invoice
                    && (!payment.InvoiceId.HasValue || payment.InvoiceId.Value <= 0)
                    && payment.Id > lastStandalonePaymentId)
                .OrderBy(payment => payment.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (standalonePayments.Count == 0)
            {
                break;
            }

            lastStandalonePaymentId = standalonePayments[^1].Id;
            foreach (var payment in standalonePayments)
            {
                ApplyEvaluation(new List<Payment> { payment }, now, result);
            }

            if (result.PendingSaves > 0)
            {
                await db.SaveChangesAsync(ct);
                result.ClearPendingSaves();
            }
        }

        return result;
    }

    private static void ApplyEvaluation(
        List<Payment> payments,
        DateTime now,
        InvoicePaymentStateReconciliationResult result)
    {
        var evaluation = InvoicePaymentStateService.EvaluateInvoiceAggregate(payments, now);
        if (evaluation is null)
        {
            return;
        }

        result.Scanned++;
        if (!evaluation.HasChanges)
        {
            return;
        }

        result.Changed++;
        var representative = payments.First(payment => payment.Id == evaluation.RepresentativePaymentId);
        if (InvoicePaymentStateService.ApplySnapshot(representative, evaluation.Suggested))
        {
            result.Applied++;
            result.MarkPendingSave();
        }
    }

    private bool IsEnabled()
        => _configuration.GetValue<bool?>($"{ConfigurationSection}:Enabled") ?? true;

    private TimeSpan GetStartupDelay()
        => GetConfiguredMinutes("StartupDelayMinutes", DefaultStartupDelay, allowZero: true);

    private TimeSpan GetInterval()
        => GetConfiguredMinutes("IntervalMinutes", DefaultInterval, allowZero: false);

    private int GetBatchSize()
        => Math.Clamp(
            _configuration.GetValue<int?>($"{ConfigurationSection}:BatchSize") ?? DefaultBatchSize,
            1,
            5_000);

    private TimeSpan GetConfiguredMinutes(string key, TimeSpan fallback, bool allowZero)
    {
        var value = _configuration.GetValue<double?>($"{ConfigurationSection}:{key}");
        if (!value.HasValue)
        {
            return fallback;
        }

        var minValue = allowZero ? 0d : 0.1d;
        return value.Value < minValue ? fallback : TimeSpan.FromMinutes(value.Value);
    }
}

public sealed class InvoicePaymentStateReconciliationResult
{
    public int Scanned { get; internal set; }
    public int Changed { get; internal set; }
    public int Applied { get; internal set; }
    internal int PendingSaves { get; private set; }

    internal void MarkPendingSave() => PendingSaves++;

    internal void ClearPendingSaves() => PendingSaves = 0;
}
