using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed class ClientCaseCourtSyncSchedulerService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    public const string EnabledSettingKey = "legalIntegrations.courtSyncScheduler.enabled";
    public const string IntervalMinutesSettingKey = "legalIntegrations.courtSyncScheduler.intervalMinutes";
    public const string BatchSizeSettingKey = "legalIntegrations.courtSyncScheduler.batchSize";
    public const int DefaultIntervalMinutes = 360;
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 1440;
    public const int DefaultBatchSize = 25;
    public const int MinBatchSize = 1;
    public const int MaxBatchSize = 100;

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(DefaultIntervalMinutes);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientCaseCourtSyncSchedulerService> _logger;

    public ClientCaseCourtSyncSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
        IConfiguration configuration,
        ILogger<ClientCaseCourtSyncSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _featureFlags = featureFlags;
        _configuration = configuration;
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
            var nextDelay = DefaultInterval;
            try
            {
                var settings = await ResolveSettingsAsync(stoppingToken);
                nextDelay = TimeSpan.FromMinutes(settings.IntervalMinutes);
                if (settings.CanRun)
                {
                    var processed = await RunOnceAsync(settings.BatchSize, stoppingToken);
                    _logger.LogInformation("Automatic court source sync finished. Processed cases: {Count}.", processed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic court source sync failed.");
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        return await RunOnceAsync(settings.BatchSize, cancellationToken);
    }

    private async Task<int> RunOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var admin = await db.Users
            .AsNoTracking()
            .Where(item => item.IsActive && item.IsApproved)
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (admin is null)
        {
            return 0;
        }

        var caseIds = await db.ClientCases
            .AsNoTracking()
            .Where(item => item.Status == ClientCaseStatus.Open)
            .Where(item => item.CourtUrl.Contains(".sudrf.ru"))
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .Take(NormalizeBatchSize(batchSize))
            .ToListAsync(cancellationToken);

        if (caseIds.Count == 0)
        {
            return 0;
        }

        var environment = scope.ServiceProvider.GetService<IWebHostEnvironment>();
        var storage = scope.ServiceProvider.GetRequiredService<ICaseDocumentStorage>();
        var sudrf = scope.ServiceProvider.GetRequiredService<ISudrfCourtSyncService>();
        var controller = new CasesV2Controller(db, environment, storage, sudrf)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                        new Claim(ClaimTypes.Name, "Система"),
                        new Claim(ClaimTypes.Email, admin.Email),
                        new Claim(ClaimTypes.Role, "admin"),
                    ], "CourtSyncScheduler")),
                },
            },
        };

        var processed = 0;
        foreach (var caseId in caseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await controller.RunCourtSync(caseId, cancellationToken);
            processed++;
        }

        return processed;
    }

    public static int NormalizeIntervalMinutes(int value)
        => Math.Clamp(value, MinIntervalMinutes, MaxIntervalMinutes);

    public static int NormalizeBatchSize(int value)
        => Math.Clamp(value, MinBatchSize, MaxBatchSize);

    private async Task<CourtSyncSchedulerSettings> ResolveSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var settings = await db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EnabledSettingKey ||
                item.Key == IntervalMinutesSettingKey ||
                item.Key == BatchSizeSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        var configEnabled = _configuration.GetValue("LegalIntegrations:CourtSyncScheduler:Enabled", false);
        var configInterval = _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:IntervalMinutes") ?? (int)DefaultInterval.TotalMinutes;
        var configBatchSize = _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:BatchSize") ?? DefaultBatchSize;

        var isEnabled = ParseBoolSetting(settings.GetValueOrDefault(EnabledSettingKey), configEnabled);
        var intervalMinutes = NormalizeIntervalMinutes(ParseIntSetting(settings.GetValueOrDefault(IntervalMinutesSettingKey), configInterval));
        var batchSize = NormalizeBatchSize(ParseIntSetting(settings.GetValueOrDefault(BatchSizeSettingKey), configBatchSize));

        return new CourtSyncSchedulerSettings(
            isEnabled,
            _featureFlags.CurrentValue.Legal.ExternalCourtSync,
            intervalMinutes,
            batchSize);
    }

    private static bool ParseBoolSetting(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseIntSetting(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private readonly record struct CourtSyncSchedulerSettings(
        bool IsEnabled,
        bool FeatureFlagEnabled,
        int IntervalMinutes,
        int BatchSize)
    {
        public bool CanRun => IsEnabled && FeatureFlagEnabled;
    }
}
