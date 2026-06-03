using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class YandexCalendarBackgroundSyncService : BackgroundService
{
    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;
    private readonly ILogger<YandexCalendarBackgroundSyncService> _logger;
    private readonly object _wakeLock = new();
    private TaskCompletionSource _settingsChanged = NewWakeSignal();

    public YandexCalendarBackgroundSyncService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
        ILogger<YandexCalendarBackgroundSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _featureFlags = featureFlags;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            var settings = await LoadCalendarSyncSettingsAsync(stoppingToken);
            if (settings.YandexCalendarSyncEnabled == true)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Yandex Calendar background sync failed.");
                }
            }

            try
            {
                await WaitForNextRunAsync(TimeSpan.FromMinutes(ResolveIntervalMinutes(settings)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public void NotifySettingsChanged()
    {
        lock (_wakeLock)
        {
            _settingsChanged.TrySetResult();
            _settingsChanged = NewWakeSignal();
        }
    }

    public async Task<YandexCalendarSchedulerRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var yandexCalendarSync = scope.ServiceProvider.GetRequiredService<IYandexCalendarSyncService>();
        return await yandexCalendarSync.SyncDueConnectionsAsync(ResolveBatchSize(), cancellationToken);
    }

    private int ResolveIntervalMinutes(CalendarSyncSettingsDto? settings = null)
        => CalendarSyncProviderRegistry.NormalizeProviderInterval(
            settings?.YandexSyncIntervalMinutes
            ?? _configuration.GetValue("YandexCalendarSync:IntervalMinutes", CalendarSyncProviderRegistry.DefaultProviderSyncIntervalMinutes));

    private int ResolveBatchSize()
        => Math.Clamp(_configuration.GetValue("YandexCalendarSync:BatchSize", 100), 1, 500);

    private async Task<CalendarSyncSettingsDto> LoadCalendarSyncSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var setting = await context.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == CalendarSyncSettingsKey, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings(_featureFlags.CurrentValue.Legal.ExternalCalendarSync);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            return CalendarSyncProviderRegistry.NormalizeSettings(settings, _featureFlags.CurrentValue.Legal.ExternalCalendarSync);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Stored calendar sync settings are invalid. Yandex Calendar background sync will use feature flag fallback.");
            return CalendarSyncProviderRegistry.BuildDefaultSettings(_featureFlags.CurrentValue.Legal.ExternalCalendarSync);
        }
    }

    private async Task WaitForNextRunAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task wakeTask;
        lock (_wakeLock)
        {
            wakeTask = _settingsChanged.Task;
        }

        var delayTask = Task.Delay(delay, cancellationToken);
        await Task.WhenAny(delayTask, wakeTask).WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource NewWakeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
