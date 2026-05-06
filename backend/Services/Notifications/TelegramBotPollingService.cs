namespace PayPlanner.Api.Services.Notifications;

public sealed class TelegramBotPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramBotPollingService> _logger;

    public TelegramBotPollingService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TelegramBotPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue("Notifications:TelegramPolling:PollSeconds", 10),
            5,
            300);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var updateService = scope.ServiceProvider.GetRequiredService<ITelegramBotUpdateService>();
                await updateService.PollEnabledSendersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram bot polling cycle failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
