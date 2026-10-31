using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services;

public sealed class InvoicePaymentStateReconciliationService : BackgroundService
{
    private const string ConfigurationSection = "InvoicePaymentStateReconciliation";
    private const int DefaultBatchSize = 500;
    private const int MaximumRelatedPayments = 5000;
    private readonly SemaphoreSlim _runLock = new(1, 1);
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
        await _runLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var result = new InvoicePaymentStateReconciliationResult();
            var batchSize = GetBatchSize();
            int throughInvoiceId, throughStandaloneId;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                EnsureClean(db);
                throughInvoiceId = await db.Payments.AsNoTracking().Where(payment => payment.DocumentKind == DocumentKind.Invoice && payment.InvoiceId > 0)
                    .Select(payment => payment.InvoiceId).MaxAsync(ct) ?? 0;
                throughStandaloneId = await db.Payments.AsNoTracking().Where(payment => payment.DocumentKind == DocumentKind.Invoice
                        && (!payment.InvoiceId.HasValue || payment.InvoiceId <= 0)).Select(payment => (int?)payment.Id).MaxAsync(ct) ?? 0;
            }
            // Each graph is an independent financial unit. A rejected/busy graph
            // rolls back alone and cannot starve unrelated invoices in this sweep.
            foreach (var standalone in new[] { false, true })
            {
                var lastId = 0;
                var throughId = standalone ? throughStandaloneId : throughInvoiceId;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int[] keys;
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                        EnsureClean(db);
                        var headers = db.Payments.AsNoTracking().Where(payment => payment.DocumentKind == DocumentKind.Invoice);
                        var query = standalone
                            ? headers.Where(payment => !payment.InvoiceId.HasValue || payment.InvoiceId <= 0).Select(payment => payment.Id)
                            : headers.Where(payment => payment.InvoiceId > 0).Select(payment => payment.InvoiceId!.Value).Distinct();
                        keys = await query.Where(id => id > lastId && id <= throughId).OrderBy(id => id).Take(batchSize).ToArrayAsync(ct);
                    }
                    if (keys.Length == 0) break;
                    lastId = keys[^1];
                    foreach (var key in keys) result.Include(await ReconcileGraphAsync(key, standalone, now, ct));
                }
            }
            if (result.Skipped > 0)
                _logger.LogWarning("Invoice reconciliation left {Skipped} aggregate(s) unchanged: {Conflicted} busy, {Oversized} oversized; the rest require history/link review.", result.Skipped, result.Conflicted, result.Oversized);
            return result;
        }
        finally { _runLock.Release(); }
    }

    private async Task<InvoicePaymentStateReconciliationResult> ReconcileGraphAsync(int key, bool standalone, DateTime now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        EnsureClean(db);
        var result = new InvoicePaymentStateReconciliationResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginSystemAsync(db, ct);
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            var graph = standalone
                ? db.Payments.Where(payment => payment.Id == key && payment.DocumentKind == DocumentKind.Invoice
                    && (!payment.InvoiceId.HasValue || payment.InvoiceId <= 0))
                : db.Payments.Where(payment => payment.InvoiceId == key);
            var ids = await graph.AsNoTracking().OrderBy(payment => payment.Id).Select(payment => payment.Id).Take(MaximumRelatedPayments + 1).ToArrayAsync(ct);
            if (ids.Length == 0) return result;
            result.Scanned = 1;
            if (ids.Length > MaximumRelatedPayments) { result.Skipped = 1; result.Oversized = 1; return result; }
            await lease.LockPaymentsAsync(ids, ct);
            var currentIds = await graph.AsNoTracking().OrderBy(payment => payment.Id).Select(payment => payment.Id).Take(MaximumRelatedPayments + 1).ToArrayAsync(ct);
            if (!ids.SequenceEqual(currentIds)) throw new DbUpdateConcurrencyException("Invoice payment graph changed during reconciliation.");
            if (await graph.HasAmbiguousInvoiceLinksAsync(ct)
                || await PaymentCreditInvariantScope.CancellationProtectedPayments(db).AnyAsync(payment => ids.Contains(payment.Id), ct))
            { result.Skipped = 1; return result; }
            var payments = await graph.ToListAsync(ct);
            var headerType = payments.FirstOrDefault(payment => payment.DocumentKind == DocumentKind.Invoice)?.Type;
            if (headerType.HasValue && payments.Any(payment => payment.Type != headerType.Value))
            { result.Skipped = 1; return result; }
            var evaluation = InvoicePaymentStateService.EvaluateInvoiceAggregate(payments, now);
            if (evaluation is null) { result.Scanned = 0; return result; }
            if (evaluation.HasChanges)
            {
                result.Changed = 1;
                using var credit = new PaymentCreditInvariantScope(db);
                credit.IncludePayments(payments);
                var representative = payments.Single(payment => payment.Id == evaluation.RepresentativePaymentId);
                InvoicePaymentStateService.ApplySnapshot(representative, evaluation.Suggested);
                await db.SaveChangesAsync(ct);
                if (!await credit.KeepsAppliedCreditAsync(ct)) { result.Skipped = 1; return result; }
            }
            await lease.CommitAsync(ct);
            result.Applied = result.Changed;
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            result.Scanned = 1; result.Skipped = 1; result.Conflicted = 1;
            return result;
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private static void EnsureClean(PaymentContext db)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Invoice reconciliation requires its own clean context and transaction.");
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
    public int Skipped { get; internal set; }
    public int Conflicted { get; internal set; }
    public int Oversized { get; internal set; }

    internal void Include(InvoicePaymentStateReconciliationResult other)
    {
        Scanned += other.Scanned; Changed += other.Changed; Applied += other.Applied;
        Skipped += other.Skipped; Conflicted += other.Conflicted; Oversized += other.Oversized;
    }
}
