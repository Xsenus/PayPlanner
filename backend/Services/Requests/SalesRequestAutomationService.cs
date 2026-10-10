using PayPlanner.Api.Services.Requests;

namespace PayPlanner.Api.Services;

/// <summary>
/// Раз в минуту переводит заявки по цепочке статусов, когда выполнены все задачи текущего этапа
/// (настройка «автопереход» у статуса). Ошибка одного прохода не останавливает службу.
/// </summary>
public sealed class SalesRequestAutomationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SalesRequestAutomationService> _logger;

    public SalesRequestAutomationService(IServiceScopeFactory scopeFactory, ILogger<SalesRequestAutomationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Sales request automation tick failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISalesRequestService>().AdvanceCompletedAsync(ct);
    }
}
