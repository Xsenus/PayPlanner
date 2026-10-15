using System.Security.Claims;
using System.Diagnostics;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.Sqlite;
using PayPlanner.Api.Data;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Services.UserActivity;

public class UserActivityService : IUserActivityService
{
    private const string LoggingEnabledSettingKey = "user-activity.enabled";
    private const string LoggingRetentionDaysSettingKey = "user-activity.retention-days";
    private static readonly TimeSpan SettingsCacheLifetime = TimeSpan.FromSeconds(30);
    private const int SqliteAuditAttemptTimeoutSeconds = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    // AddDbContextFactory is singleton: all SQLite audit services share this gate.
    // Weak keys bound its lifetime to the factory without retaining database names.
    private static readonly ConditionalWeakTable<IDbContextFactory<PaymentContext>, SemaphoreSlim> SqliteWriterLocks = new();
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
        var elapsed = Stopwatch.StartNew();
        var stage = "settings";
        long lockWaitMilliseconds = 0;
        long contextMilliseconds = 0;
        long connectionMilliseconds = 0;
        long admissionMilliseconds = 0;
        long saveMilliseconds = 0;
        long commitMilliseconds = 0;
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
                    stage = "context";
                    var interval = Stopwatch.StartNew();
                    PaymentContext context;
                    try { context = await _contextFactory.CreateDbContextAsync(token); }
                    finally { contextMilliseconds += interval.ElapsedMilliseconds; }
                    await using var ownedContext = context;
                    // A busy commit is rolled back with its owned transaction;
                    // a later attempt must not reuse a now-unreserved generated ID.
                    log.Id = 0;
                    context.UserActivityLogs.Add(log);

                    // Only the insert needs serialization. Preparing a context or
                    // opening its connection must not block unrelated audit writes.
                    var connectionOpened = false;
                    var connectionStarted = false;
                    SemaphoreSlim? sqliteWriterLock = null;
                    var sqliteWriterLockTaken = false;
                    try
                    {
                        if (context.Database.IsSqlite())
                        {
                            // Queue asynchronously before SQLite's synchronous writer admission.
                            // Release between retry attempts; another factory stays independent.
                            sqliteWriterLock = SqliteWriterLocks.GetValue(_contextFactory, static _ => new SemaphoreSlim(1, 1));
                            stage = "write-lock";
                            interval.Restart();
                            try { await sqliteWriterLock.WaitAsync(token); }
                            finally { lockWaitMilliseconds += interval.ElapsedMilliseconds; }
                            sqliteWriterLockTaken = true;
                        }
                        stage = "connection";
                        interval.Restart();
                        connectionStarted = true;
                        return await DatabaseConnectionOperation.RunAsync(context, async () =>
                        {
                            connectionMilliseconds += interval.ElapsedMilliseconds;
                            connectionOpened = true;
                            var sqlite = context.Database.IsSqlite()
                                ? context.Database.GetDbConnection() as SqliteConnection : null;
                            var nativeTimeout = sqlite?.DefaultTimeout;
                            var commandTimeout = sqlite is null ? null : context.Database.GetCommandTimeout();
                            try
                            {
                                // SQLite's async commands still wait synchronously for a writer.
                                // Admit the owned transaction before the shared audit lock, with
                                // short attempts that the existing bounded retry can release.
                                if (sqlite is not null)
                                {
                                    sqlite.DefaultTimeout = SqliteAuditAttemptTimeoutSeconds;
                                    context.Database.SetCommandTimeout(SqliteAuditAttemptTimeoutSeconds);
                                }
                                stage = "write-admission";
                                interval.Restart();
                                IDbContextTransaction? admission;
                                try
                                {
                                    admission = sqlite is not null && context.Database.CurrentTransaction is null
                                        ? await context.Database.BeginTransactionAsync(token) : null;
                                }
                                finally { admissionMilliseconds += interval.ElapsedMilliseconds; }
                                await using var ownedAdmission = admission;
                                var lockTaken = false;
                                try
                                {
                                    stage = "write-lock";
                                    interval.Restart();
                                    try { await WriteLock.WaitAsync(token); }
                                    finally { lockWaitMilliseconds += interval.ElapsedMilliseconds; }
                                    lockTaken = true;
                                    stage = "save";
                                    interval.Restart();
                                    try { await context.SaveChangesAsync(token); }
                                    finally { saveMilliseconds += interval.ElapsedMilliseconds; }
                                    if (admission is not null)
                                    {
                                        stage = "commit";
                                        interval.Restart();
                                        try { await admission.CommitAsync(token); }
                                        finally { commitMilliseconds += interval.ElapsedMilliseconds; }
                                    }
                                    stage = "completed";
                                    return log;
                                }
                                finally
                                {
                                    if (lockTaken) WriteLock.Release();
                                }
                            }
                            finally
                            {
                                if (sqlite is not null)
                                {
                                    sqlite.DefaultTimeout = nativeTimeout!.Value;
                                    context.Database.SetCommandTimeout(commandTimeout);
                                }
                            }
                        }, token);
                    }
                    finally
                    {
                        if (connectionStarted && !connectionOpened) connectionMilliseconds += interval.ElapsedMilliseconds;
                        if (sqliteWriterLockTaken) sqliteWriterLock!.Release();
                    }
                },
                _logger,
                cancellationToken,
                // This replay creates its own context/transaction before writing.
                // Other helper callers retain the existing BUSY-only policy.
                retrySharedCacheLock: true);

            return log;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Не удалось сохранить журнал активности пользователя. Этап={AuditStage}; всего={AuditTotalMs} мс; ожидание={AuditLockWaitMs} мс; контекст={AuditContextMs} мс; подключение={AuditConnectionMs} мс; допуск={AuditAdmissionMs} мс; сохранение={AuditSaveMs} мс; фиксация={AuditCommitMs} мс",
                stage, elapsed.ElapsedMilliseconds, lockWaitMilliseconds, contextMilliseconds, connectionMilliseconds, admissionMilliseconds, saveMilliseconds, commitMilliseconds);
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
