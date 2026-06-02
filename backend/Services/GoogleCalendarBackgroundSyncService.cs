using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class GoogleCalendarBackgroundSyncService : BackgroundService
{
    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;
    private readonly ILogger<GoogleCalendarBackgroundSyncService> _logger;

    public GoogleCalendarBackgroundSyncService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
        ILogger<GoogleCalendarBackgroundSyncService> logger)
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
            if (await IsExternalCalendarSyncEnabledAsync(stoppingToken))
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Google Calendar background sync failed.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(ResolveIntervalMinutes()), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<GoogleCalendarSchedulerRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var googleCalendarSync = scope.ServiceProvider.GetRequiredService<IGoogleCalendarSyncService>();
        return await googleCalendarSync.SyncDueConnectionsAsync(ResolveBatchSize(), cancellationToken);
    }

    private int ResolveIntervalMinutes()
        => Math.Clamp(_configuration.GetValue("GoogleCalendarSync:IntervalMinutes", 10), 2, 1440);

    private int ResolveBatchSize()
        => Math.Clamp(_configuration.GetValue("GoogleCalendarSync:BatchSize", 100), 1, 500);

    private async Task<bool> IsExternalCalendarSyncEnabledAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var setting = await context.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == CalendarSyncSettingsKey, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return _featureFlags.CurrentValue.Legal.ExternalCalendarSync;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            return CalendarSyncProviderRegistry
                .NormalizeSettings(settings, _featureFlags.CurrentValue.Legal.ExternalCalendarSync)
                .ExternalCalendarSyncEnabled == true;
        }
        catch (JsonException)
        {
            _logger.LogWarning("Stored calendar sync settings are invalid. Google Calendar background sync will use feature flag fallback.");
            return _featureFlags.CurrentValue.Legal.ExternalCalendarSync;
        }
    }
}
