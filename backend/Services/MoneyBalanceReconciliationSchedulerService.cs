using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class MoneyBalanceReconciliationSchedulerService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MoneyBalanceReconciliationSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private DateTime? _lastRunDate;

    public MoneyBalanceReconciliationSchedulerService(
        IServiceScopeFactory scopeFactory,
        ILogger<MoneyBalanceReconciliationSchedulerService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jobRunState = jobRunState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MoneyBalanceReconciliationSchedulerService started.");

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var targetDate = ResolveReconciliationTargetDate(DateTime.UtcNow);
                if (_lastRunDate != targetDate)
                {
                    var startedAt = DateTime.UtcNow;
                    var result = await RunForDateAsync(targetDate, stoppingToken);
                    _logger.LogInformation(
                        "Daily money balance reconciliation finished for {Date}: created {Created}, updated {Updated}, issues {IssueCount}.",
                        targetDate,
                        result.Created,
                        result.Updated,
                        result.IssueCount);
                    if (_jobRunState is not null)
                    {
                        await _jobRunState.RecordSuccessAsync(
                            SystemJobKeys.MoneyBalanceReconciliation,
                            startedAt,
                            result.Created + result.Updated,
                            null,
                            $"Сверка за {targetDate:dd.MM.yyyy}: создано {result.Created}, обновлено {result.Updated}, расхождений {result.IssueCount}.",
                            stoppingToken);
                    }
                    _lastRunDate = targetDate;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily money balance reconciliation failed.");
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordFailureAsync(
                        SystemJobKeys.MoneyBalanceReconciliation,
                        DateTime.UtcNow,
                        ex,
                        "Ежедневная сверка денежных остатков завершилась с ошибкой.",
                        stoppingToken);
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }

        _logger.LogInformation("MoneyBalanceReconciliationSchedulerService stopped.");
    }

    public async Task<MoneyBalanceReconciliationRunResultDto> RunForDateAsync(DateTime date, CancellationToken cancellationToken,
        Func<PaymentContext, CancellationToken, Task<bool>>? verifyCaller = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var authority = await MoneyReconciliationScope.ForSystemAsync(db, cancellationToken);
        var service = new MoneyBalanceReconciliationService(db, loggerFactory.CreateLogger<MoneyBalanceReconciliationService>());
        return await service.RunAsync(authority, date.Date, date.Date, null, null,
            async (authorityDb, token) => authority.SameAuthority(await MoneyReconciliationScope.ForSystemAsync(authorityDb, token))
                && (verifyCaller is null || await verifyCaller(authorityDb, token)),
            cancellationToken);
    }

    public static DateTime ResolveReconciliationTargetDate(DateTime nowUtc, TimeZoneInfo? timeZone = null)
    {
        var effectiveTimeZone = timeZone ?? CalendarTimeZoneHelper.ResolveTimeZone(null);
        return CalendarTimeZoneHelper.ToUserLocal(nowUtc, effectiveTimeZone).Date.AddDays(-1);
    }
}
