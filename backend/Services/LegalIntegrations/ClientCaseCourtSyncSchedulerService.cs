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
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);
    private const int DefaultBatchSize = 25;

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
            try
            {
                if (IsEnabled())
                {
                    var processed = await RunOnceAsync(stoppingToken);
                    _logger.LogInformation("Automatic court source sync finished. Processed cases: {Count}.", processed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic court source sync failed.");
            }

            try
            {
                await Task.Delay(ResolveInterval(), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
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

        var batchSize = Math.Clamp(_configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:BatchSize") ?? DefaultBatchSize, 1, 100);
        var caseIds = await db.ClientCases
            .AsNoTracking()
            .Where(item => item.Status == ClientCaseStatus.Open)
            .Where(item => item.CourtUrl.Contains(".sudrf.ru"))
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .Take(batchSize)
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

    private bool IsEnabled()
        => _featureFlags.CurrentValue.Legal.ExternalCourtSync
           && _configuration.GetValue("LegalIntegrations:CourtSyncScheduler:Enabled", false);

    private TimeSpan ResolveInterval()
    {
        var minutes = _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:IntervalMinutes");
        return minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : DefaultInterval;
    }
}
