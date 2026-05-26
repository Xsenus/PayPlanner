using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class SystemJobKeys
{
    public const string DatabaseBackup = "database-backup";
    public const string TaskReminders = "task-reminders";
    public const string NotificationRetry = "notification-retry";
    public const string NotificationDeliveryRetention = "notification-delivery-retention";
    public const string ExternalCalendarSync = "external-calendar-sync";
    public const string CaseInactivityControl = "case-inactivity-control";
    public const string CourtSync = "court-sync";
    public const string MoneyBalanceReconciliation = "money-balance-reconciliation";
    public const string UserActivityRetention = "user-activity-retention";
}

public sealed class SystemJobRunState
{
    public string JobKey { get; init; } = string.Empty;
    public string Status { get; init; } = "unknown";
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public int? ProcessedCount { get; init; }
    public int? SkippedCount { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

public interface ISystemJobRunStateService
{
    Task RecordSuccessAsync(
        string jobKey,
        DateTime startedAt,
        int? processedCount,
        int? skippedCount,
        string summary,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        string jobKey,
        DateTime startedAt,
        Exception exception,
        string? summary = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, SystemJobRunState>> GetLastRunsAsync(
        IEnumerable<string> jobKeys,
        CancellationToken cancellationToken = default);
}

public sealed class SystemJobRunStateService : ISystemJobRunStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<PaymentContext> _contextFactory;
    private readonly ILogger<SystemJobRunStateService> _logger;

    public SystemJobRunStateService(
        IDbContextFactory<PaymentContext> contextFactory,
        ILogger<SystemJobRunStateService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public Task RecordSuccessAsync(
        string jobKey,
        DateTime startedAt,
        int? processedCount,
        int? skippedCount,
        string summary,
        CancellationToken cancellationToken = default)
        => SaveStateAsync(new SystemJobRunState
        {
            JobKey = NormalizeJobKey(jobKey),
            Status = "success",
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            ProcessedCount = processedCount,
            SkippedCount = skippedCount,
            Summary = Truncate(summary, 1000),
        }, cancellationToken);

    public Task RecordFailureAsync(
        string jobKey,
        DateTime startedAt,
        Exception exception,
        string? summary = null,
        CancellationToken cancellationToken = default)
        => SaveStateAsync(new SystemJobRunState
        {
            JobKey = NormalizeJobKey(jobKey),
            Status = "error",
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            Summary = Truncate(summary ?? "Фоновый сервис завершился с ошибкой.", 1000),
            ErrorMessage = Truncate(exception.Message, 1000),
        }, cancellationToken);

    public async Task<IReadOnlyDictionary<string, SystemJobRunState>> GetLastRunsAsync(
        IEnumerable<string> jobKeys,
        CancellationToken cancellationToken = default)
    {
        var normalizedKeys = jobKeys
            .Select(NormalizeJobKey)
            .Where(static key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var settingKeys = normalizedKeys.Select(BuildSettingKey).ToArray();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.SystemSettings
            .AsNoTracking()
            .Where(item => settingKeys.Contains(item.Key))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, SystemJobRunState>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                var state = JsonSerializer.Deserialize<SystemJobRunState>(row.Value, JsonOptions);
                if (state is not null && !string.IsNullOrWhiteSpace(state.JobKey))
                {
                    result[state.JobKey] = state;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize system job run state {Key}.", row.Key);
            }
        }

        return result;
    }

    private async Task SaveStateAsync(SystemJobRunState state, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var key = BuildSettingKey(state.JobKey);
            var now = DateTime.UtcNow;
            var value = JsonSerializer.Serialize(state, JsonOptions);
            var setting = await context.SystemSettings.FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
            if (setting is null)
            {
                context.SystemSettings.Add(new SystemSetting
                {
                    Key = key,
                    Value = value,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                setting.Value = value;
                setting.UpdatedAt = now;
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save system job run state {JobKey}.", state.JobKey);
        }
    }

    private static string BuildSettingKey(string jobKey) => $"system.jobs.{NormalizeJobKey(jobKey)}.lastRun";

    private static string NormalizeJobKey(string jobKey) => (jobKey ?? string.Empty).Trim();

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
