namespace PayPlanner.Api.Services;

public sealed class DatabaseBackupSchedulerService : BackgroundService
{
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    private readonly DatabaseBackupService _databaseBackupService;
    private readonly ILogger<DatabaseBackupSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;

    public DatabaseBackupSchedulerService(
        DatabaseBackupService databaseBackupService,
        ILogger<DatabaseBackupSchedulerService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _databaseBackupService = databaseBackupService;
        _logger = logger;
        _jobRunState = jobRunState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var startedAt = DateTime.UtcNow;
                var created = await _databaseBackupService.EnsureScheduledBackupsAsync(stoppingToken);
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordSuccessAsync(
                        SystemJobKeys.DatabaseBackup,
                        startedAt,
                        created,
                        null,
                        created > 0
                            ? $"Создано автоматических резервных копий: {created}."
                            : "Расписание проверено, новые резервные копии не требовались.",
                        stoppingToken);
                }

                await Task.Delay(_databaseBackupService.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Database backup scheduler iteration failed.");
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordFailureAsync(
                        SystemJobKeys.DatabaseBackup,
                        DateTime.UtcNow,
                        ex,
                        "Проверка расписания резервного копирования завершилась с ошибкой.",
                        stoppingToken);
                }

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
    }
}
