using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed class CourtSyncMetricsRollupService : BackgroundService
{
    public const string ArchiveSettingKey = "court.sync.metrics.daily";
    public const int DefaultRetentionDays = 90;
    public const int DefaultIntervalHours = 1;
    public const int MaxRetentionDays = 365;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly CourtSyncMetrics _metrics;
    private readonly ILogger<CourtSyncMetricsRollupService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public CourtSyncMetricsRollupService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        CourtSyncMetrics metrics,
        ILogger<CourtSyncMetricsRollupService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _metrics = metrics;
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
                await RunOnceAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Court sync metrics rollup failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(ResolveIntervalHours()), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<CourtSyncMetricsRetainedSnapshot> RunOnceAsync(
        DateTime? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        var delta = _metrics.DrainPending();
        try
        {
            var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
            var retentionDays = ResolveRetentionDays();
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var setting = await context.SystemSettings
                .SingleOrDefaultAsync(item => item.Key == ArchiveSettingKey, cancellationToken);
            var archive = ParseArchive(setting?.Value);
            var changed = archive.Prune(now.Date.AddDays(-(retentionDays - 1)));

            if (delta.HasValues)
            {
                archive.Add(now.Date, delta);
                changed = true;
            }

            if (changed)
            {
                setting ??= new SystemSetting
                {
                    Key = ArchiveSettingKey,
                    CreatedAt = now,
                };
                if (setting.Id == 0)
                {
                    context.SystemSettings.Add(setting);
                }

                setting.Value = JsonSerializer.Serialize(archive, JsonOptions);
                setting.UpdatedAt = now;
                await context.SaveChangesAsync(cancellationToken);
            }

            return archive.ToSnapshot(retentionDays, now);
        }
        catch
        {
            _metrics.RestorePending(delta);
            throw;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public static CourtSyncMetricsRetainedSnapshot ReadSnapshot(
        string? value,
        int retentionDays = DefaultRetentionDays,
        DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var normalizedRetentionDays = NormalizeRetentionDays(retentionDays);
        var archive = ParseArchive(value);
        archive.Prune(now.Date.AddDays(-(normalizedRetentionDays - 1)));
        return archive.ToSnapshot(normalizedRetentionDays, now);
    }

    public static int NormalizeRetentionDays(int value)
        => Math.Clamp(value, 7, MaxRetentionDays);

    private int ResolveRetentionDays()
        => NormalizeRetentionDays(_configuration.GetValue(
            "LegalIntegrations:CourtSyncMetrics:RetentionDays",
            DefaultRetentionDays));

    private int ResolveIntervalHours()
        => Math.Clamp(
            _configuration.GetValue(
                "LegalIntegrations:CourtSyncMetrics:IntervalHours",
                DefaultIntervalHours),
            1,
            168);

    private static CourtSyncMetricsArchive ParseArchive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new CourtSyncMetricsArchive();
        }

        try
        {
            return JsonSerializer.Deserialize<CourtSyncMetricsArchive>(value, JsonOptions)
                ?? new CourtSyncMetricsArchive();
        }
        catch (JsonException)
        {
            return new CourtSyncMetricsArchive();
        }
    }
}

public sealed class CourtSyncMetricsArchive
{
    public int Version { get; init; } = 1;
    public List<CourtSyncMetricsDailyBucket> Days { get; init; } = [];

    public void Add(DateTime dayUtc, CourtSyncMetricsDelta delta)
    {
        var day = dayUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var bucket = Days.SingleOrDefault(item => item.Day == day);
        if (bucket is null)
        {
            bucket = new CourtSyncMetricsDailyBucket { Day = day };
            Days.Add(bucket);
        }

        bucket.RequestCount += delta.RequestCount;
        bucket.FailedRequestCount += delta.FailedRequestCount;
        bucket.TotalLatencyTicks += delta.TotalLatencyTicks;
        bucket.MaxLatencyTicks = Math.Max(bucket.MaxLatencyTicks, delta.MaxLatencyTicks);
        bucket.DuplicateSkipCount += delta.DuplicateSkipCount;
        bucket.DocumentCount += delta.DocumentCount;
        bucket.DocumentBytes += delta.DocumentBytes;
        Days.Sort((left, right) => string.CompareOrdinal(left.Day, right.Day));
    }

    public bool Prune(DateTime cutoffUtc)
    {
        var cutoff = cutoffUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return Days.RemoveAll(item => string.CompareOrdinal(item.Day, cutoff) < 0) > 0;
    }

    public CourtSyncMetricsRetainedSnapshot ToSnapshot(int retentionDays, DateTime nowUtc)
    {
        var requests = Days.Sum(item => item.RequestCount);
        var totalLatencyTicks = Days.Sum(item => item.TotalLatencyTicks);
        return new CourtSyncMetricsRetainedSnapshot(
            retentionDays,
            Days.Count == 0 ? null : Days[0].Day,
            Days.Count == 0 ? null : Days[^1].Day,
            nowUtc,
            requests,
            Days.Sum(item => item.FailedRequestCount),
            requests == 0 ? 0 : TimeSpan.FromTicks(totalLatencyTicks).TotalMilliseconds / requests,
            TimeSpan.FromTicks(Days.Count == 0 ? 0 : Days.Max(item => item.MaxLatencyTicks)).TotalMilliseconds,
            Days.Sum(item => item.DuplicateSkipCount),
            Days.Sum(item => item.DocumentCount),
            Days.Sum(item => item.DocumentBytes));
    }
}

public sealed class CourtSyncMetricsDailyBucket
{
    public string Day { get; init; } = string.Empty;
    public long RequestCount { get; set; }
    public long FailedRequestCount { get; set; }
    public long TotalLatencyTicks { get; set; }
    public long MaxLatencyTicks { get; set; }
    public long DuplicateSkipCount { get; set; }
    public long DocumentCount { get; set; }
    public long DocumentBytes { get; set; }
}

public sealed record CourtSyncMetricsRetainedSnapshot(
    int RetentionDays,
    string? PeriodStartedOn,
    string? PeriodEndedOn,
    DateTime GeneratedAt,
    long RequestCount,
    long FailedRequestCount,
    double AverageLatencyMs,
    double MaxLatencyMs,
    long DuplicateSkipCount,
    long DocumentCount,
    long DocumentBytes);
