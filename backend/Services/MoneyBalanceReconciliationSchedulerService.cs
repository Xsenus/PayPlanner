using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class MoneyBalanceReconciliationSchedulerService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MoneyBalanceReconciliationSchedulerService> _logger;
    private DateTime? _lastRunDate;

    public MoneyBalanceReconciliationSchedulerService(
        IServiceScopeFactory scopeFactory,
        ILogger<MoneyBalanceReconciliationSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
                var targetDate = DateTime.UtcNow.Date.AddDays(-1);
                if (_lastRunDate != targetDate)
                {
                    var result = await RunForDateAsync(targetDate, stoppingToken);
                    _lastRunDate = targetDate;
                    _logger.LogInformation(
                        "Daily money balance reconciliation finished for {Date}: created {Created}, updated {Updated}, issues {IssueCount}.",
                        targetDate,
                        result?.Created ?? 0,
                        result?.Updated ?? 0,
                        result?.IssueCount ?? 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily money balance reconciliation failed.");
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

    public async Task<MoneyBalanceReconciliationRunResultDto?> RunForDateAsync(DateTime date, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var controller = new MoneyOperationsController(
            db,
            loggerFactory.CreateLogger<MoneyOperationsController>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        var action = await controller.RunBalanceReconciliation(
            from: date.Date,
            to: date.Date,
            source: null,
            accountName: null,
            ct: cancellationToken);

        return action.Result is OkObjectResult { Value: MoneyBalanceReconciliationRunResultDto result }
            ? result
            : null;
    }
}
