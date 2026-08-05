using System.Security.Claims;
using System.Text.Json;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.UserActivity;

public class UserActivityService : IUserActivityService
{
    private const string LoggingEnabledSettingKey = "user-activity.enabled";
    private const string LoggingRetentionDaysSettingKey = "user-activity.retention-days";
    private static readonly TimeSpan SettingsCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static readonly SemaphoreSlim SettingsLock = new(1, 1);
    private static bool? CachedIsEnabled;
    private static int? CachedRetentionDays;
    private static DateTime? CachedUpdatedAtUtc;
    private static DateTimeOffset SettingsCacheExpiresAtUtc = DateTimeOffset.MinValue;
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "refresh_token", "authorization", "code", "device_id", "hash",
        "otp", "password", "signature", "state", "token",
    };

    private readonly IDbContextFactory<PaymentContext> _contextFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<UserActivityService> _logger;

    public UserActivityService(
        IDbContextFactory<PaymentContext> contextFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<UserActivityService> logger)
    {
        _contextFactory = contextFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<UserActivitySettingsState> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var cached = TryGetCachedSettings();
        if (cached is not null)
        {
            return cached;
        }

        await SettingsLock.WaitAsync(cancellationToken);
        try
        {
            cached = TryGetCachedSettings();
            if (cached is not null)
            {
                return cached;
            }

            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var settings = await context.SystemSettings
                .AsNoTracking()
                .Where(x => x.Key == LoggingEnabledSettingKey || x.Key == LoggingRetentionDaysSettingKey)
                .ToListAsync(cancellationToken);

            var enabledSetting = settings.FirstOrDefault(x => x.Key == LoggingEnabledSettingKey);
            var retentionSetting = settings.FirstOrDefault(x => x.Key == LoggingRetentionDaysSettingKey);

            var state = new UserActivitySettingsState(
                ParseEnabledValue(enabledSetting?.Value),
                ParseRetentionDaysValue(retentionSetting?.Value),
                new[] { enabledSetting?.UpdatedAt, retentionSetting?.UpdatedAt }.Where(x => x.HasValue).Max());
            CacheSettings(state);
            return state;
        }
        finally
        {
            SettingsLock.Release();
        }
    }

    public async Task<UserActivitySettingsState> UpdateSettingsAsync(bool isEnabled, int? retentionDays, CancellationToken cancellationToken = default)
    {
        await SettingsLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var normalizedValue = isEnabled ? "true" : "false";
            var normalizedRetentionDays = retentionDays.GetValueOrDefault() > 0 ? retentionDays.GetValueOrDefault() : 0;

            var settings = await context.SystemSettings
                .Where(x => x.Key == LoggingEnabledSettingKey || x.Key == LoggingRetentionDaysSettingKey)
                .ToListAsync(cancellationToken);

            var enabledSetting = settings.FirstOrDefault(x => x.Key == LoggingEnabledSettingKey);
            if (enabledSetting is null)
            {
                context.SystemSettings.Add(new SystemSetting
                {
                    Key = LoggingEnabledSettingKey,
                    Value = normalizedValue,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                enabledSetting.Value = normalizedValue;
                enabledSetting.UpdatedAt = now;
            }

            var retentionSetting = settings.FirstOrDefault(x => x.Key == LoggingRetentionDaysSettingKey);
            if (retentionSetting is null)
            {
                context.SystemSettings.Add(new SystemSetting
                {
                    Key = LoggingRetentionDaysSettingKey,
                    Value = normalizedRetentionDays.ToString(),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                retentionSetting.Value = normalizedRetentionDays.ToString();
                retentionSetting.UpdatedAt = now;
            }

            await context.SaveChangesAsync(cancellationToken);

            var state = new UserActivitySettingsState(isEnabled, normalizedRetentionDays > 0 ? normalizedRetentionDays : null, now);
            CacheSettings(state, TimeSpan.FromMinutes(10));
            return state;
        }
        finally
        {
            SettingsLock.Release();
        }
    }

    public async Task<UserActivityLog?> TryWriteAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!(await GetSettingsAsync(cancellationToken)).IsEnabled)
            {
                return null;
            }

            var httpContext = _httpContextAccessor.HttpContext;

            var userId = entry.UserId;
            string? userEmail = entry.UserEmail;
            string? userFullName = entry.UserFullName;

            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                if (userId is null && int.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed))
                {
                    userId = parsed;
                }

                userEmail ??= httpContext.User.FindFirstValue(ClaimTypes.Email);
                userFullName ??= httpContext.User.FindFirstValue(ClaimTypes.Name);
            }

            // ForwardedHeadersMiddleware уже заменяет RemoteIpAddress только для доверенного proxy.
            // Прямой X-Forwarded-For здесь читать нельзя: клиент мог бы подменить адрес в аудите.
            var ip = entry.IpAddress ?? httpContext?.Connection?.RemoteIpAddress?.ToString();

            var userAgent = entry.UserAgent ?? httpContext?.Request?.Headers?["User-Agent"].FirstOrDefault();
            var method = entry.HttpMethod ?? httpContext?.Request?.Method;
            var path = entry.Path ?? httpContext?.Request?.Path.ToString();
            var query = SanitizeQueryString(entry.QueryString ?? httpContext?.Request?.QueryString.ToString());
            var statusCode = entry.HttpStatusCode ?? httpContext?.Response?.StatusCode;

            string? metadataJson = null;
            if (entry.Metadata is not null && entry.Metadata.Count > 0)
            {
                metadataJson = JsonSerializer.Serialize(entry.Metadata, JsonOptions);
            }

            var log = new UserActivityLog
            {
                UserId = userId,
                UserEmail = string.IsNullOrWhiteSpace(userEmail) ? null : userEmail,
                UserFullName = string.IsNullOrWhiteSpace(userFullName) ? null : userFullName,
                Category = entry.Category,
                Action = entry.Action,
                Section = entry.Section,
                ObjectType = entry.ObjectType,
                ObjectId = entry.ObjectId,
                Description = entry.Description,
                Status = entry.Status,
                IpAddress = ip,
                UserAgent = userAgent,
                HttpMethod = method,
                Path = path,
                QueryString = query,
                HttpStatusCode = statusCode,
                DurationMs = entry.DurationMs,
                MetadataJson = metadataJson,
                CreatedAt = entry.CreatedAtUtc ?? DateTime.UtcNow
            };

            await SqliteRetryHelper.ExecuteAsync(
                async token =>
                {
                    var lockTaken = false;
                    try
                    {
                        await WriteLock.WaitAsync(token);
                        lockTaken = true;

                        await using var context = await _contextFactory.CreateDbContextAsync(token);
                        context.UserActivityLogs.Add(log);
                        await context.SaveChangesAsync(token);
                        return log;
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            WriteLock.Release();
                        }
                    }
                },
                _logger,
                cancellationToken);

            return log;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить журнал активности пользователя");
            return null;
        }
    }

    /// <summary>
    /// Сохраняет структуру query string для диагностики, но удаляет OAuth-коды,
    /// одноразовые состояния, токены и иные значения, пригодные для повторного входа.
    /// </summary>
    public static string? SanitizeQueryString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var query = value[0] == '?' ? value : $"?{value}";
        var parsed = QueryHelpers.ParseQuery(query);
        var builder = new QueryBuilder();
        foreach (var (key, values) in parsed)
        {
            var normalizedKey = key.Replace('-', '_');
            var sensitive = SensitiveQueryKeys.Contains(normalizedKey)
                || normalizedKey.Contains("token", StringComparison.OrdinalIgnoreCase)
                || normalizedKey.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || normalizedKey.Contains("password", StringComparison.OrdinalIgnoreCase)
                || normalizedKey.Contains("api_key", StringComparison.OrdinalIgnoreCase);
            foreach (var item in values)
            {
                builder.Add(key, sensitive ? "[скрыто]" : item ?? string.Empty);
            }
        }

        var sanitized = builder.ToQueryString().Value;
        return sanitized is { Length: > 512 } ? sanitized[..512] : sanitized;
    }

    private static UserActivitySettingsState? TryGetCachedSettings()
    {
        if (!CachedIsEnabled.HasValue || SettingsCacheExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return new UserActivitySettingsState(CachedIsEnabled.Value, CachedRetentionDays, CachedUpdatedAtUtc);
    }

    private static void CacheSettings(UserActivitySettingsState state, TimeSpan? ttl = null)
    {
        CachedIsEnabled = state.IsEnabled;
        CachedRetentionDays = state.RetentionDays;
        CachedUpdatedAtUtc = state.UpdatedAt;
        SettingsCacheExpiresAtUtc = DateTimeOffset.UtcNow.Add(ttl ?? SettingsCacheLifetime);
    }

    private static bool ParseEnabledValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => true,
        };
    }

    private static int? ParseRetentionDaysValue(string? value)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            return null;
        }

        return parsed;
    }
}
