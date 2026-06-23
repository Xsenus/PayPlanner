using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class YandexCalendarSyncService : IYandexCalendarSyncService
{
    private const string YandexEnabledKey = "auth.yandex.enabled";
    private const string YandexClientIdKey = "auth.yandex.clientId";
    private const string YandexClientSecretKey = "auth.yandex.clientSecret";
    private const string YandexScopesKey = "calendar.yandex.scopes";
    private const string YandexAuthEndpoint = "https://oauth.yandex.ru/authorize";
    private const string YandexTokenEndpoint = "https://oauth.yandex.ru/token";
    private const string YandexUserInfoEndpoint = "https://login.yandex.ru/info?format=json";
    private const string CalDavBaseUrl = "https://caldav.yandex.ru";
    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private const int DefaultPollIntervalMs = 2000;

    private static readonly TimeSpan AuthRequestLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _tokenProtector;
    private readonly IExternalCalendarSyncCoordinator? _externalCalendarSyncCoordinator;
    private readonly ILogger<YandexCalendarSyncService> _logger;
    private readonly HashSet<string> _calendarTimeZoneEnsured = [];

    public YandexCalendarSyncService(
        PaymentContext db,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<YandexCalendarSyncService> logger)
        : this(db, configuration, httpClientFactory, dataProtectionProvider, null, logger)
    {
    }

    public YandexCalendarSyncService(
        PaymentContext db,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        IExternalCalendarSyncCoordinator? externalCalendarSyncCoordinator,
        ILogger<YandexCalendarSyncService> logger)
    {
        _db = db;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1");
        _externalCalendarSyncCoordinator = externalCalendarSyncCoordinator;
        _logger = logger;
    }

    public async Task<YandexCalendarStatusDto> GetStatusAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await LoadConnectionQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        return await BuildStatusAsync(userId, request, connection, cancellationToken);
    }

    public async Task<YandexCalendarStartAuthResponseDto> StartAuthAsync(
        int userId,
        StartYandexCalendarAuthRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var settings = await ResolveYandexSettingsAsync(cancellationToken);
        if (!await IsYandexCalendarProviderEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Яндекс Календарь отключен администратором.");
        }

        if (!settings.Ready)
        {
            throw new InvalidOperationException("Настройки Яндекс OAuth не заполнены или отключены.");
        }

        var now = DateTime.UtcNow;
        var state = GenerateToken(32);
        _db.YandexCalendarAuthRequests.Add(new YandexCalendarAuthRequest
        {
            UserId = userId,
            State = state,
            ReturnUrl = NormalizeReturnUrl(request.ReturnUrl),
            Status = "pending",
            CreatedAt = now,
            ExpiresAt = now.Add(AuthRequestLifetime),
        });
        await _db.SaveChangesAsync(cancellationToken);

        var callbackUrl = BuildAbsoluteUrl(httpRequest, "/api/yandex-calendar/auth/callback");
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = callbackUrl,
            ["state"] = state,
            ["force_confirm"] = "yes",
            ["device_id"] = $"payplanner-{userId}",
            ["device_name"] = "PayPlanner CRM",
        };
        if (!string.IsNullOrWhiteSpace(settings.Scopes))
        {
            query["scope"] = settings.Scopes;
        }

        return new YandexCalendarStartAuthResponseDto
        {
            State = state,
            AuthUrl = QueryHelpers.AddQueryString(YandexAuthEndpoint, query!),
            PollIntervalMs = DefaultPollIntervalMs,
        };
    }

    public async Task<YandexCalendarAuthStatusDto> GetAuthStatusAsync(int userId, string state, CancellationToken cancellationToken = default)
    {
        var authRequest = await _db.YandexCalendarAuthRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId && item.State == state, cancellationToken);
        if (authRequest is null)
        {
            throw new KeyNotFoundException("Запрос подключения Яндекс.Календаря не найден.");
        }

        var status = authRequest.Status;
        if (status == "pending" && authRequest.ExpiresAt <= DateTime.UtcNow)
        {
            status = "expired";
        }

        return new YandexCalendarAuthStatusDto
        {
            State = authRequest.State,
            Status = status,
            Completed = status is "completed" or "failed" or "expired",
            Detail = authRequest.Error,
            ReturnUrl = authRequest.ReturnUrl,
        };
    }

    public async Task<string> HandleCallbackAsync(
        string? state,
        string? code,
        string? error,
        string? errorDescription,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedState = NormalizeOptional(state);
        if (string.IsNullOrWhiteSpace(normalizedState))
        {
            return BuildPopupHtml(false, "Яндекс не вернул state для подключения календаря.");
        }

        var authRequest = await _db.YandexCalendarAuthRequests
            .FirstOrDefaultAsync(item => item.State == normalizedState, cancellationToken);
        if (authRequest is null)
        {
            return BuildPopupHtml(false, "Запрос подключения Яндекс.Календаря не найден.");
        }

        if (authRequest.ExpiresAt <= DateTime.UtcNow)
        {
            authRequest.Status = "expired";
            authRequest.Error = "Срок действия ссылки истёк. Запустите подключение заново.";
            await _db.SaveChangesAsync(cancellationToken);
            return BuildPopupHtml(false, authRequest.Error, authRequest.ReturnUrl);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            authRequest.Status = "failed";
            authRequest.Error = NormalizeOptional(errorDescription) ?? error;
            authRequest.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return BuildPopupHtml(false, authRequest.Error, authRequest.ReturnUrl);
        }

        var normalizedCode = NormalizeOptional(code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            authRequest.Status = "failed";
            authRequest.Error = "Яндекс не вернул код авторизации.";
            authRequest.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return BuildPopupHtml(false, authRequest.Error, authRequest.ReturnUrl);
        }

        try
        {
            var settings = await ResolveYandexSettingsAsync(cancellationToken);
            if (!settings.Ready)
            {
                throw new InvalidOperationException("Настройки Яндекс OAuth не заполнены или отключены.");
            }

            var token = await ExchangeCodeAsync(settings, normalizedCode, cancellationToken);
            var profile = await LoadYandexProfileAsync(token.AccessToken, cancellationToken);
            await UpsertConnectionFromTokenAsync(authRequest.UserId, token, profile, cancellationToken);

            authRequest.Status = "completed";
            authRequest.Error = null;
            authRequest.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return BuildPopupHtml(true, "Яндекс.Календарь подключён. Вернитесь в CRM.", authRequest.ReturnUrl);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
        {
            authRequest.Status = "failed";
            authRequest.Error = ex.Message;
            authRequest.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return BuildPopupHtml(false, ex.Message, authRequest.ReturnUrl);
        }
    }

    public async Task<YandexCalendarStatusDto> UpdateSettingsAsync(
        int userId,
        UpdateYandexCalendarSettingsRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        await EnsureUserCanManageCalendarAsync(httpRequest, cancellationToken);
        var connection = await _db.YandexCalendarConnections.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is null)
        {
            throw new KeyNotFoundException("Подключение Яндекс.Календаря не найдено.");
        }

        connection.IsEnabled = request.IsEnabled ?? connection.IsEnabled;
        connection.SyncEnabled = request.SyncEnabled ?? connection.SyncEnabled;
        connection.CalendarMode = NormalizeKnownValue(request.CalendarMode, connection.CalendarMode, ["personal", "adminByAssignee"]);
        connection.ConflictResolutionMode = NormalizeKnownValue(request.ConflictResolutionMode, connection.ConflictResolutionMode, ["crmWins", "yandexWins", "latestWins"]);
        connection.SyncFromDate = NormalizeSyncFromDate(request.SyncFromDate);
        connection.UpdatedAt = DateTime.UtcNow;
        await AddLogAsync(connection.Id, null, null, "system", "settings-updated", "success", "Настройки Яндекс.Календаря сохранены.", BuildConnectionSettingsDetails(connection), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return await GetStatusAsync(userId, httpRequest, cancellationToken);
    }

    public async Task<YandexCalendarStatusDto> DisconnectAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureUserCanManageCalendarAsync(request, cancellationToken);
        var connection = await _db.YandexCalendarConnections
            .Include(item => item.Calendars)
            .Include(item => item.TaskLinks)
            .AsSplitQuery()
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is not null)
        {
            var failures = new List<string>();
            await CleanupConnectionCalendarsAsync(
                connection,
                connection.Calendars.ToList(),
                "Подключение Яндекс.Календаря отключается пользователем.",
                failures,
                cancellationToken);

            if (failures.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException(
                    "Не удалось отключить Яндекс.Календарь: сначала нужно удалить созданные PayPlanner календари и события во внешнем календаре. "
                    + string.Join("; ", failures));
            }

            _db.YandexCalendarConnections.Remove(connection);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await GetStatusAsync(userId, request, cancellationToken);
    }

    public async Task<YandexCalendarSyncNowResultDto> SyncNowAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureUserCanManageCalendarAsync(request, cancellationToken);
        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        await EnsureManualFullSyncAllowedAsync(userId, settings, cancellationToken);
        var result = await SyncConnectionAsync(userId, forceFull: true, cancellationToken);
        return new YandexCalendarSyncNowResultDto
        {
            ProcessedTasks = result.ProcessedTasks,
            ImportedEvents = result.ImportedEvents,
            ErrorCount = result.ErrorCount,
            Message = result.ErrorCount == 0
                ? "Синхронизация Яндекс.Календаря выполнена."
                : "Синхронизация Яндекс.Календаря завершена с ошибками. Проверьте журнал.",
            Status = await GetStatusAsync(userId, request, cancellationToken),
        };
    }

    public async Task<YandexCalendarSchedulerRunResult> SyncDueConnectionsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var userIds = await _db.YandexCalendarConnections
            .AsNoTracking()
            .Where(item => item.IsEnabled && item.SyncEnabled && item.AccessTokenProtected != null)
            .OrderBy(item => item.LastIncrementalSyncAt ?? item.LastFullSyncAt ?? item.CreatedAt)
            .Take(Math.Clamp(batchSize, 1, 500))
            .Select(item => item.UserId)
            .ToListAsync(cancellationToken);

        var processedTasks = 0;
        var importedEvents = 0;
        var errors = 0;
        foreach (var userId in userIds)
        {
            try
            {
                var result = await SyncConnectionAsync(userId, forceFull: false, cancellationToken);
                processedTasks += result.ProcessedTasks;
                importedEvents += result.ImportedEvents;
                errors += result.ErrorCount;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                _logger.LogWarning(ex, "Yandex Calendar scheduled sync failed for user {UserId}", userId);
            }
        }

        return new YandexCalendarSchedulerRunResult(userIds.Count, processedTasks, importedEvents, errors);
    }

    private async Task EnsureManualFullSyncAllowedAsync(
        int userId,
        CalendarSyncSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var lastFullSyncAt = await _db.YandexCalendarConnections
            .AsNoTracking()
            .Where(item => item.UserId == userId && item.IsEnabled && item.SyncEnabled)
            .Select(item => item.LastFullSyncAt)
            .FirstOrDefaultAsync(cancellationToken);
        var availableAt = BuildManualFullSyncAvailableAt(lastFullSyncAt, settings.ManualFullSyncCooldownMinutes);
        var now = DateTime.UtcNow;
        if (availableAt.HasValue && availableAt.Value > now)
        {
            throw new CalendarManualSyncCooldownException(
                $"Повторную полную синхронизацию Яндекс.Календаря можно запустить после {FormatLogDateTime(availableAt.Value)}. До этого времени работает обычная фоновая синхронизация.",
                availableAt.Value);
        }
    }

    public async Task SyncTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _db.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == taskId, cancellationToken);
        if (task is null)
        {
            return;
        }

        var userIds = await _db.YandexCalendarConnections
            .AsNoTracking()
            .Where(item => item.IsEnabled && item.SyncEnabled && item.AccessTokenProtected != null)
            .Select(item => new
            {
                item.UserId,
                IsAdmin = item.User.Role != null && item.User.Role.Name == "admin",
            })
            .ToListAsync(cancellationToken);

        foreach (var item in userIds)
        {
            if (!item.IsAdmin && !TaskBelongsToUser(task, item.UserId))
            {
                continue;
            }

            try
            {
                await SyncSingleTaskAsync(item.UserId, taskId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Yandex Calendar task sync failed for task {TaskId} and user {UserId}", taskId, item.UserId);
            }
        }
    }

    public async Task DeleteTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var links = await _db.YandexCalendarTaskLinks
            .Include(item => item.Connection)
            .Where(item => item.TaskJournalItemId == taskId && item.SyncStatus != "deleted")
            .ToListAsync(cancellationToken);

        foreach (var link in links)
        {
            if (link.Connection is null || string.IsNullOrWhiteSpace(link.YandexEventUrl))
            {
                continue;
            }

            try
            {
                var accessToken = await ResolveAccessTokenAsync(link.Connection, cancellationToken);
                var deleted = await DeleteYandexEventAsync(accessToken, link.YandexEventUrl, cancellationToken);
                link.SyncStatus = "deleted";
                link.LastDirection = "crm-to-yandex";
                link.LastSyncedAt = DateTime.UtcNow;
                link.UpdatedAt = DateTime.UtcNow;
                link.LastError = null;
                await AddLogAsync(
                    link.ConnectionId,
                    link.CalendarSyncCalendarId,
                    link.TaskJournalItemId,
                    "crm-to-yandex",
                    deleted ? "delete-event" : "delete-event-missing",
                    "success",
                    deleted
                        ? "Событие удалено из Яндекс.Календаря после удаления задачи в CRM."
                        : "Событие уже отсутствовало в Яндекс.Календаре при удалении задачи в CRM.",
                    null,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                link.LastError = Trim(ex.Message, 2000);
                link.UpdatedAt = DateTime.UtcNow;
                await AddLogAsync(
                    link.ConnectionId,
                    link.CalendarSyncCalendarId,
                    link.TaskJournalItemId,
                    "crm-to-yandex",
                    "delete-event",
                    "error",
                    "Не удалось удалить событие из Яндекс.Календаря после удаления задачи в CRM.",
                    ex.Message,
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException("YandexCalendarTaskDeleteFailed", ex);
            }
        }

        if (links.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();

        var ownConnection = await LoadConnectionQuery()
            .Include(item => item.TaskLinks)
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (ownConnection is not null)
        {
            await CleanupConnectionCalendarsAsync(
                ownConnection,
                ownConnection.Calendars.ToList(),
                $"Пользователь #{userId} удаляется из CRM администратором #{actorUserId}.",
                failures,
                cancellationToken);
        }

        var adminConnections = await LoadConnectionQuery()
            .Include(item => item.TaskLinks)
            .Where(item => item.UserId != userId && item.Calendars.Any(calendar => calendar.OwnerUserId == userId))
            .ToListAsync(cancellationToken);
        foreach (var connection in adminConnections)
        {
            var calendars = connection.Calendars.Where(calendar => calendar.OwnerUserId == userId).ToList();
            await CleanupConnectionCalendarsAsync(
                connection,
                calendars,
                $"Сотрудник #{userId} удаляется из CRM, календарь сотрудника убирается из администраторского Яндекс.Календаря.",
                failures,
                cancellationToken);
        }

        if (failures.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException($"YandexCalendarCleanupFailed: {string.Join("; ", failures)}");
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ConnectionSyncResult> SyncConnectionAsync(int userId, bool forceFull, CancellationToken cancellationToken)
    {
        var connection = await LoadConnectionQuery()
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is null || !connection.IsEnabled || !connection.SyncEnabled)
        {
            return new ConnectionSyncResult(0, 0, 0);
        }

        var errors = 0;
        var importedEvents = 0;
        try
        {
            await EnsureAdminAssigneeCalendarsAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            errors++;
            connection.LastError = Trim(ex.Message, 2000);
            await AddLogAsync(connection.Id, null, null, "system", "ensure-calendars", "error", "Не удалось подготовить календари сотрудников в Яндекс.Календаре.", ex.Message, cancellationToken);
        }

        try
        {
            importedEvents += await ImportYandexChangesAsync(connection, forceFull, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            errors++;
            connection.LastError = Trim(ex.Message, 2000);
            await AddLogAsync(connection.Id, null, null, "yandex-to-crm", "import", "error", "Не удалось загрузить изменения из Яндекс.Календаря.", ex.Message, cancellationToken);
        }

        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        errors += await DeleteLinksOutsideSyncWindowAsync(connection, cancellationToken);

        var tasks = await LoadTasksForConnectionAsync(connection, cancellationToken);
        var processedTasks = 0;
        foreach (var task in tasks)
        {
            try
            {
                if (ShouldHideCompletedTask(settings, task))
                {
                    await DeleteTaskLinkForCompletedTaskAsync(connection, task.Id, cancellationToken);
                }
                else
                {
                    await UpsertYandexEventForTaskAsync(connection, task, cancellationToken);
                }

                processedTasks++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors++;
                connection.LastError = Trim(ex.Message, 2000);
                await AddLogAsync(connection.Id, null, task.Id, "crm-to-yandex", "upsert", "error", "Не удалось синхронизировать задачу с Яндекс.Календарём.", ex.Message, cancellationToken);
            }
        }

        var now = DateTime.UtcNow;
        connection.LastIncrementalSyncAt = now;
        if (forceFull || connection.LastFullSyncAt is null)
        {
            connection.LastFullSyncAt = now;
        }
        if (errors == 0)
        {
            connection.LastError = null;
        }
        connection.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return new ConnectionSyncResult(processedTasks, importedEvents, errors);
    }

    private async Task SyncSingleTaskAsync(int userId, int taskId, CancellationToken cancellationToken)
    {
        var connection = await LoadConnectionQuery()
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is null || !connection.IsEnabled || !connection.SyncEnabled)
        {
            return;
        }

        var task = await _db.TaskJournalItems
            .Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == taskId, cancellationToken);
        if (task is null)
        {
            return;
        }

        if (IsBeforeSyncFromDate(connection, task))
        {
            await DeleteTaskLinkOutsideSyncWindowAsync(connection, task.Id, cancellationToken);
            connection.LastIncrementalSyncAt = DateTime.UtcNow;
            connection.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var visibility = await ResolveConnectionCalendarVisibilityAsync(connection.UserId, cancellationToken);
        if (!CanSyncTaskForConnection(connection, task, visibility))
        {
            await DeleteTaskLinkForRevokedAccessAsync(connection, task.Id, cancellationToken);
            connection.LastIncrementalSyncAt = DateTime.UtcNow;
            connection.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        if (ShouldHideCompletedTask(settings, task))
        {
            await DeleteTaskLinkForCompletedTaskAsync(connection, task.Id, cancellationToken);
        }
        else
        {
            await UpsertYandexEventForTaskAsync(connection, task, cancellationToken);
        }

        connection.LastIncrementalSyncAt = DateTime.UtcNow;
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task CleanupConnectionCalendarsAsync(
        YandexCalendarConnection connection,
        IReadOnlyList<YandexCalendarSyncCalendar> calendars,
        string reason,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        if (calendars.Count == 0)
        {
            return;
        }

        string? accessToken = null;
        try
        {
            accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            failures.Add($"подключение #{connection.Id}: {ex.Message}");
            await AddLogAsync(connection.Id, null, null, "system", "cleanup-user-calendars", "error", "Не удалось получить токен Яндекс.Календаря для очистки календарей удаляемого пользователя.", ex.Message, cancellationToken);
            return;
        }

        foreach (var calendar in calendars)
        {
            try
            {
                var deleted = await DeleteYandexCalendarAsync(accessToken, calendar.CalendarUrl, cancellationToken);
                await AddLogAsync(
                    connection.Id,
                    calendar.Id,
                    null,
                    "system",
                    deleted ? "delete-calendar" : "delete-calendar-missing",
                    "success",
                    deleted ? $"Календарь «{calendar.Name}» удалён из Яндекс.Календаря." : $"Календарь «{calendar.Name}» уже отсутствовал в Яндекс.Календаре.",
                    reason,
                    cancellationToken);

                foreach (var link in connection.TaskLinks.Where(item => item.CalendarSyncCalendarId == calendar.Id))
                {
                    link.SyncStatus = "deleted";
                    link.LastDirection = "crm-to-yandex";
                    link.LastSyncedAt = DateTime.UtcNow;
                    link.UpdatedAt = DateTime.UtcNow;
                    link.LastError = null;
                }

                _db.YandexCalendarSyncCalendars.Remove(calendar);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failures.Add($"календарь {calendar.Name}: {ex.Message}");
                calendar.LastError = Trim(ex.Message, 2000);
                calendar.UpdatedAt = DateTime.UtcNow;
                await AddLogAsync(connection.Id, calendar.Id, null, "system", "delete-calendar", "error", $"Не удалось удалить календарь «{calendar.Name}» из Яндекс.Календаря.", ex.Message, cancellationToken);
            }
        }
    }

    private async Task<int> ImportYandexChangesAsync(YandexCalendarConnection connection, bool forceFull, CancellationToken cancellationToken)
    {
        var imported = 0;
        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        foreach (var calendar in connection.Calendars.ToList())
        {
            var events = await ListYandexEventsAsync(accessToken, calendar, cancellationToken);
            foreach (var yandexEvent in events)
            {
                if (await ApplyYandexEventAsync(connection, calendar, yandexEvent, accessToken, cancellationToken))
                {
                    imported++;
                }
            }

            calendar.SyncToken = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            calendar.LastSyncedAt = DateTime.UtcNow;
            calendar.LastError = null;
            calendar.UpdatedAt = DateTime.UtcNow;
        }

        return imported;
    }

    private async Task<bool> ApplyYandexEventAsync(
        YandexCalendarConnection connection,
        YandexCalendarSyncCalendar calendar,
        YandexEventItem yandexEvent,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var taskId = yandexEvent.PayPlannerTaskId;
        YandexCalendarTaskLink? link = null;
        if (taskId.HasValue)
        {
            link = await _db.YandexCalendarTaskLinks
                .Include(item => item.TaskJournalItem)
                .FirstOrDefaultAsync(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == taskId.Value, cancellationToken);

            if (link is not null && !IsLinkedYandexEvent(link, calendar.YandexCalendarId, yandexEvent.Id))
            {
                if (yandexEvent.Deleted)
                {
                    return false;
                }

                try
                {
                    var deleted = await DeleteYandexEventAsync(accessToken, yandexEvent.EventUrl, cancellationToken);
                    await AddLogAsync(
                        connection.Id,
                        calendar.Id,
                        link.TaskJournalItemId,
                        "yandex-to-crm",
                        "delete-stale-event",
                        deleted ? "success" : "skip",
                        deleted
                            ? "Лишний дубль события Яндекс.Календаря для задачи CRM удалён; задача CRM оставлена без изменений."
                            : "Лишний дубль события Яндекс.Календаря для задачи CRM уже отсутствовал; задача CRM оставлена без изменений.",
                        yandexEvent.Summary,
                        cancellationToken);
                    return deleted;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await AddLogAsync(
                        connection.Id,
                        calendar.Id,
                        link.TaskJournalItemId,
                        "yandex-to-crm",
                        "delete-stale-event",
                        "error",
                        "Не удалось удалить лишний дубль события Яндекс.Календаря для задачи CRM; задача CRM оставлена без изменений.",
                        ex.Message,
                        cancellationToken);
                    return false;
                }
            }
        }

        link ??= await _db.YandexCalendarTaskLinks
            .Include(item => item.TaskJournalItem)
            .FirstOrDefaultAsync(item =>
                item.YandexCalendarId == calendar.YandexCalendarId &&
                item.YandexEventId == yandexEvent.Id &&
                item.ConnectionId == connection.Id,
                cancellationToken);

        if (link?.TaskJournalItem is null)
        {
            return false;
        }

        if (yandexEvent.Deleted)
        {
            link.SyncStatus = "detached";
            link.LastDirection = "yandex-to-crm";
            link.LastSyncedAt = DateTime.UtcNow;
            link.UpdatedAt = DateTime.UtcNow;
            await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "yandex-to-crm", "detach", "success", "Событие удалено в Яндекс.Календаре, связь с задачей помечена как отключённая.", null, cancellationToken);
            return true;
        }

        var nextHash = BuildYandexHash(yandexEvent);
        if (string.Equals(link.LastYandexHash, nextHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (connection.ConflictResolutionMode == "crmWins")
        {
            link.LastYandexHash = nextHash;
            link.LastDirection = "yandex-to-crm";
            link.LastSyncedAt = DateTime.UtcNow;
            link.UpdatedAt = DateTime.UtcNow;
            await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "yandex-to-crm", "skip", "success", "Изменения в Яндекс.Календаре прочитаны, но правило конфликтов оставляет CRM главным источником.", null, cancellationToken);
            return false;
        }

        var task = link.TaskJournalItem;
        var changed = false;
        var changes = new List<string>();
        var timeZone = await ResolveTaskTimeZoneAsync(connection, calendar, task, cancellationToken);
        var importedStartAt = yandexEvent.StartAt.HasValue ? CalendarTimeZoneHelper.NormalizeUtcInstant(yandexEvent.StartAt.Value) : (DateTime?)null;
        var importedEndAt = yandexEvent.EndAt.HasValue ? CalendarTimeZoneHelper.NormalizeUtcInstant(yandexEvent.EndAt.Value) : (DateTime?)null;
        if (importedStartAt.HasValue && task.DueAt != importedStartAt.Value)
        {
            changes.Add($"Срок: {FormatLogDateTime(task.DueAt, timeZone)} → {FormatLogDateTime(importedStartAt.Value, timeZone)}");
            task.DueAt = importedStartAt.Value;
            changed = true;
        }

        if (importedStartAt.HasValue && importedEndAt.HasValue)
        {
            var minutes = Math.Max(15, (int)Math.Round((importedEndAt.Value - importedStartAt.Value).TotalMinutes));
            if (task.PlannedDurationMinutes != minutes)
            {
                changes.Add($"Длительность: {FormatDuration(task.PlannedDurationMinutes)} → {FormatDuration(minutes)}");
                task.PlannedDurationMinutes = minutes;
                changed = true;
            }
        }

        var importedStatus = ResolveStatusFromSummary(yandexEvent.Summary);
        if (!string.IsNullOrWhiteSpace(importedStatus) && task.Status != importedStatus)
        {
            changes.Add($"Статус: {FormatTaskStatus(task.Status)} → {FormatTaskStatus(importedStatus)}");
            task.Status = importedStatus;
            changed = true;
        }

        var importedTitle = StripStatusPrefix(yandexEvent.Summary);
        if (!string.IsNullOrWhiteSpace(importedTitle) && !string.Equals(task.Title, importedTitle, StringComparison.Ordinal))
        {
            changes.Add($"Название: «{task.Title}» → «{Trim(importedTitle, 500)}»");
            task.Title = Trim(importedTitle, 500);
            changed = true;
        }

        if (!changed)
        {
            link.LastYandexHash = nextHash;
            return false;
        }

        task.UpdatedAt = DateTime.UtcNow;
        link.YandexEtag = yandexEvent.Etag ?? link.YandexEtag;
        link.LastYandexHash = nextHash;
        link.LastCrmHash = BuildCrmHash(task, timeZone);
        link.LastDirection = "yandex-to-crm";
        link.LastSyncedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;
        await AddLogAsync(connection.Id, calendar.Id, task.Id, "yandex-to-crm", "update-task", "success", "Изменения из Яндекс.Календаря применены к задаче.", string.Join("; ", changes), cancellationToken);
        if (_externalCalendarSyncCoordinator is not null)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await _externalCalendarSyncCoordinator.SyncTaskForConnectedUsersAsync(
                task.Id,
                ExternalCalendarSyncCoordinator.YandexProvider,
                cancellationToken);
        }

        return true;
    }

    private async Task UpsertYandexEventForTaskAsync(YandexCalendarConnection connection, TaskJournalItem task, CancellationToken cancellationToken)
    {
        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        var calendar = await EnsureCalendarForTaskAsync(connection, task, accessToken, cancellationToken);
        var timeZone = await ResolveTaskTimeZoneAsync(connection, null, task, cancellationToken);
        var crmHash = BuildCrmHash(task, timeZone);
        var link = await _db.YandexCalendarTaskLinks
            .FirstOrDefaultAsync(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == task.Id, cancellationToken);

        if (link is not null
            && string.Equals(link.LastCrmHash, crmHash, StringComparison.Ordinal)
            && string.Equals(link.SyncStatus, "active", StringComparison.Ordinal))
        {
            return;
        }

        var eventId = link?.YandexEventId;
        if (string.IsNullOrWhiteSpace(eventId) || link?.SyncStatus == "detached")
        {
            eventId = $"payplanner-task-{task.Id}-{GenerateToken(10)}";
        }

        var eventUrl = BuildEventUrl(calendar.CalendarUrl, eventId);
        var ics = BuildIcsEvent(connection, task, eventId, crmHash, timeZone);
        var result = await PutYandexEventAsync(accessToken, eventUrl, ics, cancellationToken);
        var created = link is null || link.SyncStatus == "detached" || string.IsNullOrWhiteSpace(link.YandexEventUrl);
        if (link is null)
        {
            link = new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = task.Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = eventId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.YandexCalendarTaskLinks.Add(link);
        }

        await AddLogAsync(
            connection.Id,
            calendar.Id,
            task.Id,
            "crm-to-yandex",
            created ? "create-event" : "update-event",
            "success",
            created ? "Задача создана в Яндекс.Календаре." : "Задача обновлена в Яндекс.Календаре.",
            BuildTaskSnapshotDetails(task, timeZone),
            cancellationToken);

        link.CalendarSyncCalendarId = calendar.Id;
        link.YandexCalendarId = calendar.YandexCalendarId;
        link.YandexEventId = eventId;
        link.YandexEventUrl = eventUrl;
        link.YandexEtag = result.Etag;
        link.LastCrmHash = crmHash;
        link.LastYandexHash = BuildYandexHashFromTask(task, eventId, timeZone);
        link.LastDirection = "crm-to-yandex";
        link.SyncStatus = "active";
        link.LastError = null;
        link.LastSyncedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;

        await DeleteDuplicateYandexEventsForTaskAsync(
            connection,
            calendar,
            task.Id,
            eventId,
            accessToken,
            cancellationToken);
    }

    private async Task EnsureAdminAssigneeCalendarsAsync(
        YandexCalendarConnection connection,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(connection.CalendarMode, "adminByAssignee", StringComparison.Ordinal))
        {
            return;
        }

        var visibility = await ResolveConnectionCalendarVisibilityAsync(connection.UserId, cancellationToken);
        if (visibility is null)
        {
            return;
        }
        if (!IsAssigneeCalendarMode(connection, visibility))
        {
            return;
        }

        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        var visibleUserIds = visibility.SeesAll
            ? null
            : visibility.VisibleUserIds.Where(id => id > 0).Distinct().ToArray();
        var ownersQuery = _db.Users
            .AsNoTracking()
            .Include(item => item.Role)
            .Where(item =>
                item.IsActive &&
                item.IsApproved &&
                (item.IsEmployee || (item.Role != null && item.Role.Name.ToLower() != "admin")));
        if (visibleUserIds is not null)
        {
            ownersQuery = ownersQuery.Where(item => visibleUserIds.Contains(item.Id));
        }

        var owners = await ownersQuery
            .OrderBy(item => item.FullName)
            .Select(item => new { item.Id, item.FullName })
            .ToListAsync(cancellationToken);

        foreach (var owner in owners)
        {
            await EnsureCalendarForOwnerAsync(connection, owner.Id, owner.FullName, accessToken, cancellationToken);
        }

        if (visibleUserIds is not null)
        {
            var allowed = visibleUserIds.ToHashSet();
            var revokedCalendars = connection.Calendars
                .Where(item => item.OwnerUserId.HasValue && !allowed.Contains(item.OwnerUserId.Value))
                .ToList();
            var failures = new List<string>();
            await CleanupConnectionCalendarsAsync(
                connection,
                revokedCalendars,
                reason: "Доступ к календарю сотрудника отозван в правах роли.",
                failures,
                cancellationToken);
            if (failures.Count > 0)
            {
                throw new InvalidOperationException($"YandexCalendarRevokedCalendarCleanupFailed: {string.Join("; ", failures)}");
            }
        }
    }

    private async Task<YandexCalendarSyncCalendar> EnsureCalendarForTaskAsync(
        YandexCalendarConnection connection,
        TaskJournalItem task,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var visibility = await ResolveConnectionCalendarVisibilityAsync(connection.UserId, cancellationToken);
        var ownerUserId = IsAssigneeCalendarMode(connection, visibility) ? task.AssigneeUserId : null;
        var ownerName = ownerUserId.HasValue ? NormalizeOptional(task.AssigneeName) : null;

        return await EnsureCalendarForOwnerAsync(connection, ownerUserId, ownerName, accessToken, cancellationToken);
    }

    private async Task<YandexCalendarSyncCalendar> EnsureCalendarForOwnerAsync(
        YandexCalendarConnection connection,
        int? ownerUserId,
        string? ownerName,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var name = ownerUserId.HasValue
            ? $"PayPlanner - {NormalizeOptional(ownerName) ?? $"сотрудник #{ownerUserId.Value}"}"
            : "PayPlanner задачи";

        var calendar = connection.Calendars.FirstOrDefault(item => item.OwnerUserId == ownerUserId);
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, ownerUserId ?? connection.UserId, cancellationToken);
        if (calendar is not null)
        {
            await RefreshYandexCalendarUrlAsync(accessToken, connection, calendar, cancellationToken);
            await EnsureYandexCalendarTimeZoneAsync(accessToken, calendar, timeZone, cancellationToken);
            return calendar;
        }

        var created = await CreateYandexCalendarAsync(accessToken, connection, name, ownerUserId, timeZone, cancellationToken);
        calendar = new YandexCalendarSyncCalendar
        {
            ConnectionId = connection.Id,
            OwnerUserId = ownerUserId,
            YandexCalendarId = created.Id,
            CalendarUrl = created.Url,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        connection.Calendars.Add(calendar);
        _db.YandexCalendarSyncCalendars.Add(calendar);
        connection.DefaultCalendarId ??= created.Id;
        connection.DefaultCalendarName ??= name;
        await _db.SaveChangesAsync(cancellationToken);
        await AddLogAsync(connection.Id, calendar.Id, null, "system", "create-calendar", "success", $"В Яндекс.Календаре создан календарь «{name}».", null, cancellationToken);
        return calendar;
    }

    private async Task RefreshYandexCalendarUrlAsync(
        string accessToken,
        YandexCalendarConnection connection,
        YandexCalendarSyncCalendar calendar,
        CancellationToken cancellationToken)
    {
        var email = NormalizeOptional(connection.YandexEmail);
        if (email is null)
        {
            return;
        }

        var homeUrl = $"{CalDavBaseUrl}/calendars/{Uri.EscapeDataString(email)}/";
        var discovered = await ResolveYandexCalendarByNameAsync(accessToken, homeUrl, calendar.Name, cancellationToken);
        if (discovered is null || string.Equals(calendar.CalendarUrl, discovered.Url, StringComparison.Ordinal))
        {
            return;
        }

        calendar.YandexCalendarId = discovered.Id;
        calendar.CalendarUrl = discovered.Url;
        calendar.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<IReadOnlyList<TaskJournalItem>> LoadTasksForConnectionAsync(YandexCalendarConnection connection, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var from = ResolveSyncFromDate(connection) ?? now.AddDays(-30);
        var to = now.AddDays(365);
        var query = _db.TaskJournalItems
            .Include(item => item.Participants)
            .Where(item => item.DueAt >= from && item.DueAt <= to);

        var visibility = await ResolveConnectionCalendarVisibilityAsync(connection.UserId, cancellationToken);
        if (visibility is null)
        {
            return [];
        }

        if (!visibility.SeesAll)
        {
            var visibleUserIds = visibility.VisibleUserIds.Where(id => id > 0).Distinct().ToArray();
            if (IsAssigneeCalendarMode(connection, visibility))
            {
                query = query.Where(item => item.AssigneeUserId.HasValue && visibleUserIds.Contains(item.AssigneeUserId.Value));
            }
            else
            {
                query = query.Where(item =>
                    (item.AssigneeUserId.HasValue && visibleUserIds.Contains(item.AssigneeUserId.Value)) ||
                    item.Participants.Any(participant => participant.UserId.HasValue && visibleUserIds.Contains(participant.UserId.Value)));
            }
        }

        return await query
            .OrderBy(item => item.DueAt)
            .Take(500)
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureYandexCalendarTimeZoneAsync(
        string accessToken,
        YandexCalendarSyncCalendar calendar,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var ianaTimeZoneId = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(timeZone);
        var ensureKey = $"{calendar.CalendarUrl}|{ianaTimeZoneId}";
        if (!_calendarTimeZoneEnsured.Add(ensureKey))
        {
            return;
        }

        var body = BuildYandexCalendarTimeZonePropertyBody(timeZone);
        var request = BuildYandexRequest(new HttpMethod("PROPPATCH"), calendar.CalendarUrl, accessToken, body, "application/xml; charset=utf-8");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or (HttpStatusCode)207)
        {
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс.Календарь не обновил часовой пояс календаря.");
        }
    }

    private async Task<TimeZoneInfo> ResolveTaskTimeZoneAsync(
        YandexCalendarConnection connection,
        YandexCalendarSyncCalendar? calendar,
        TaskJournalItem task,
        CancellationToken cancellationToken)
    {
        var ownerUserId = calendar?.OwnerUserId;
        if (!ownerUserId.HasValue)
        {
            var visibility = await ResolveConnectionCalendarVisibilityAsync(connection.UserId, cancellationToken);
            ownerUserId = IsAssigneeCalendarMode(connection, visibility)
                ? task.AssigneeUserId
                : connection.UserId;
        }

        return await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, ownerUserId ?? connection.UserId, cancellationToken);
    }

    private async Task<int> DeleteLinksOutsideSyncWindowAsync(YandexCalendarConnection connection, CancellationToken cancellationToken)
    {
        var syncFromDate = ResolveSyncFromDate(connection);
        if (!syncFromDate.HasValue)
        {
            return 0;
        }

        var links = await _db.YandexCalendarTaskLinks
            .Include(item => item.TaskJournalItem)
            .Where(item =>
                item.ConnectionId == connection.Id &&
                item.SyncStatus == "active" &&
                item.TaskJournalItem.DueAt < syncFromDate.Value)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return 0;
        }

        var errors = 0;
        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        foreach (var link in links)
        {
            try
            {
                var deleted = string.IsNullOrWhiteSpace(link.YandexEventUrl)
                    ? false
                    : await DeleteYandexEventAsync(accessToken, link.YandexEventUrl, cancellationToken);
                MarkYandexLinkDeleted(link);
                await AddLogAsync(
                    connection.Id,
                    link.CalendarSyncCalendarId,
                    link.TaskJournalItemId,
                    "crm-to-yandex",
                    deleted ? "delete-event" : "delete-event-missing",
                    "success",
                    deleted
                        ? "Событие удалено из Яндекс.Календаря, потому что задача раньше даты начала синхронизации."
                        : "Событие уже отсутствовало в Яндекс.Календаре при очистке задач раньше даты начала синхронизации.",
                    null,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                link.LastError = Trim(ex.Message, 2000);
                link.UpdatedAt = DateTime.UtcNow;
                await AddLogAsync(connection.Id, link.CalendarSyncCalendarId, link.TaskJournalItemId, "crm-to-yandex", "delete-event", "error", "Не удалось удалить событие Яндекс.Календаря для задачи раньше даты начала синхронизации.", ex.Message, cancellationToken);
            }
        }

        return errors;
    }

    private async Task DeleteTaskLinkOutsideSyncWindowAsync(YandexCalendarConnection connection, int taskId, CancellationToken cancellationToken)
    {
        if (!ResolveSyncFromDate(connection).HasValue)
        {
            return;
        }

        var link = await _db.YandexCalendarTaskLinks
            .FirstOrDefaultAsync(item =>
                item.ConnectionId == connection.Id &&
                item.TaskJournalItemId == taskId &&
                item.SyncStatus == "active",
                cancellationToken);
        if (link is null)
        {
            return;
        }

        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        var deleted = string.IsNullOrWhiteSpace(link.YandexEventUrl)
            ? false
            : await DeleteYandexEventAsync(accessToken, link.YandexEventUrl, cancellationToken);
        MarkYandexLinkDeleted(link);
        await AddLogAsync(
            connection.Id,
            link.CalendarSyncCalendarId,
            link.TaskJournalItemId,
            "crm-to-yandex",
            deleted ? "delete-event" : "delete-event-missing",
            "success",
            deleted
                ? "Событие удалено из Яндекс.Календаря, потому что задача раньше даты начала синхронизации."
                : "Событие уже отсутствовало в Яндекс.Календаре при очистке задачи раньше даты начала синхронизации.",
            null,
            cancellationToken);
    }

    private async Task DeleteTaskLinkForCompletedTaskAsync(YandexCalendarConnection connection, int taskId, CancellationToken cancellationToken)
    {
        var link = await _db.YandexCalendarTaskLinks
            .FirstOrDefaultAsync(item =>
                item.ConnectionId == connection.Id &&
                item.TaskJournalItemId == taskId &&
                item.SyncStatus == "active",
                cancellationToken);
        if (link is null)
        {
            return;
        }

        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        var deleted = string.IsNullOrWhiteSpace(link.YandexEventUrl)
            ? false
            : await DeleteYandexEventAsync(accessToken, link.YandexEventUrl, cancellationToken);
        MarkYandexLinkDeleted(link);
        await AddLogAsync(
            connection.Id,
            link.CalendarSyncCalendarId,
            link.TaskJournalItemId,
            "crm-to-yandex",
            deleted ? "delete-event" : "delete-event-missing",
            "success",
            deleted
                ? "Событие удалено из Яндекс Календаря, потому что в настройках включено скрытие выполненных задач."
                : "Событие уже отсутствовало в Яндекс Календаре при скрытии выполненной задачи.",
            null,
            cancellationToken);
    }

    private async Task DeleteTaskLinkForRevokedAccessAsync(YandexCalendarConnection connection, int taskId, CancellationToken cancellationToken)
    {
        var link = await _db.YandexCalendarTaskLinks
            .FirstOrDefaultAsync(item =>
                item.ConnectionId == connection.Id &&
                item.TaskJournalItemId == taskId &&
                item.SyncStatus == "active",
                cancellationToken);
        if (link is null)
        {
            return;
        }

        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        var deleted = string.IsNullOrWhiteSpace(link.YandexEventUrl)
            ? false
            : await DeleteYandexEventAsync(accessToken, link.YandexEventUrl, cancellationToken);
        MarkYandexLinkDeleted(link);
        await AddLogAsync(
            connection.Id,
            link.CalendarSyncCalendarId,
            link.TaskJournalItemId,
            "crm-to-yandex",
            deleted ? "delete-event" : "delete-event-missing",
            "success",
            deleted
                ? "Событие удалено из Яндекс Календаря, потому что доступ к календарю сотрудника отозван в правах роли."
                : "Событие уже отсутствовало в Яндекс Календаре при отзыве доступа к календарю сотрудника.",
            null,
            cancellationToken);
    }

    private IQueryable<YandexCalendarConnection> LoadConnectionQuery()
        => _db.YandexCalendarConnections
            .Include(item => item.User)
            .ThenInclude(item => item.Role)
            .Include(item => item.TaskLinks)
            .Include(item => item.Calendars)
            .ThenInclude(item => item.OwnerUser)
            .AsSplitQuery();

    private async Task<YandexCalendarStatusDto> BuildStatusAsync(
        int userId,
        HttpRequest request,
        YandexCalendarConnection? connection,
        CancellationToken cancellationToken)
    {
        var logs = connection is null
            ? []
            : await _db.YandexCalendarSyncLogs
                .AsNoTracking()
                .Where(item => item.ConnectionId == connection.Id)
                .OrderByDescending(item => item.CreatedAt)
                .Take(20)
                .Select(item => new YandexCalendarSyncLogDto
                {
                    Id = item.Id,
                    TaskJournalItemId = item.TaskJournalItemId,
                    Direction = item.Direction,
                    Operation = item.Operation,
                    Status = item.Status,
                    Message = item.Message,
                    CreatedAt = item.CreatedAt,
                })
                .ToListAsync(cancellationToken);

        var oauthSettings = await ResolveYandexSettingsAsync(cancellationToken);
        var providerEnabled = await IsYandexCalendarProviderEnabledAsync(cancellationToken);
        var calendarSettings = await LoadCalendarSyncSettingsAsync(cancellationToken);

        return new YandexCalendarStatusDto
        {
            IsAvailable = providerEnabled,
            YandexOAuthReady = oauthSettings.Ready,
            CallbackUrl = BuildAbsoluteUrl(request, "/api/yandex-calendar/auth/callback"),
            Connection = connection is null ? null : MapConnection(connection, calendarSettings),
            RecentLogs = logs,
        };
    }

    private static YandexCalendarConnectionDto MapConnection(YandexCalendarConnection connection, CalendarSyncSettingsDto settings)
        => new()
        {
            Id = connection.Id,
            YandexEmail = connection.YandexEmail,
            DisplayName = connection.DisplayName,
            IsEnabled = connection.IsEnabled,
            SyncEnabled = connection.SyncEnabled,
            CalendarMode = connection.CalendarMode,
            ConflictResolutionMode = connection.ConflictResolutionMode,
            DefaultCalendarId = connection.DefaultCalendarId,
            DefaultCalendarName = connection.DefaultCalendarName,
            AccessTokenExpiresAt = connection.AccessTokenExpiresAt,
            LastFullSyncAt = connection.LastFullSyncAt,
            LastIncrementalSyncAt = connection.LastIncrementalSyncAt,
            ManualFullSyncCooldownMinutes = settings.ManualFullSyncCooldownMinutes,
            ManualFullSyncAvailableAt = BuildManualFullSyncAvailableAt(connection.LastFullSyncAt, settings.ManualFullSyncCooldownMinutes),
            SyncFromDate = connection.SyncFromDate,
            LastError = connection.LastError,
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
            Calendars = connection.Calendars
                .OrderBy(item => item.Name)
                .Select(item => new YandexCalendarCalendarDto
                {
                    Id = item.Id,
                    OwnerUserId = item.OwnerUserId,
                    OwnerName = item.OwnerUser?.FullName,
                    YandexCalendarId = item.YandexCalendarId,
                    Name = item.Name,
                    LastSyncedAt = item.LastSyncedAt,
                    LastError = item.LastError,
                })
                .ToList(),
        };

    private async Task UpsertConnectionFromTokenAsync(
        int userId,
        YandexTokenResponse token,
        YandexProfile profile,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var connection = await _db.YandexCalendarConnections.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is null)
        {
            connection = new YandexCalendarConnection
            {
                UserId = userId,
                CalendarMode = await ShouldUseAssigneeCalendarModeByDefaultAsync(userId, cancellationToken) ? "adminByAssignee" : "personal",
                ConflictResolutionMode = "latestWins",
                CreatedAt = now,
            };
            _db.YandexCalendarConnections.Add(connection);
        }

        connection.YandexUserId = profile.Id;
        connection.YandexEmail = profile.Email;
        connection.DisplayName = profile.DisplayName;
        connection.AccessTokenProtected = _tokenProtector.Protect(token.AccessToken);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            connection.RefreshTokenProtected = _tokenProtector.Protect(token.RefreshToken);
        }
        connection.AccessTokenExpiresAt = token.ExpiresInSeconds.HasValue ? now.AddSeconds(token.ExpiresInSeconds.Value) : now.AddHours(1);
        connection.Scope = NormalizeOptional(token.Scope) ?? string.Empty;
        connection.IsEnabled = true;
        connection.SyncEnabled = true;
        connection.LastError = null;
        connection.UpdatedAt = now;

        await AddLogAsync(connection.Id == 0 ? null : connection.Id, null, null, "system", "oauth-connected", "success", "Яндекс.Календарь подключён к профилю.", null, cancellationToken);
    }

    private async Task<string> ResolveAccessTokenAsync(YandexCalendarConnection connection, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(connection.AccessTokenProtected)
            && connection.AccessTokenExpiresAt > DateTime.UtcNow.Add(AccessTokenRefreshSkew))
        {
            return _tokenProtector.Unprotect(connection.AccessTokenProtected);
        }

        if (string.IsNullOrWhiteSpace(connection.RefreshTokenProtected))
        {
            if (!string.IsNullOrWhiteSpace(connection.AccessTokenProtected))
            {
                return _tokenProtector.Unprotect(connection.AccessTokenProtected);
            }

            throw new InvalidOperationException("Нет OAuth-токена для Яндекс.Календаря. Подключите календарь заново.");
        }

        var settings = await ResolveYandexSettingsAsync(cancellationToken);
        if (!settings.Ready)
        {
            throw new InvalidOperationException("Настройки Яндекс OAuth не заполнены или отключены.");
        }

        var refreshToken = _tokenProtector.Unprotect(connection.RefreshTokenProtected);
        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            YandexTokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = settings.ClientSecret!,
            }),
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс не обновил токен календаря.");
        }

        using var document = JsonDocument.Parse(payload);
        var accessToken = ReadString(document.RootElement, "access_token") ?? throw new InvalidOperationException("Яндекс не вернул access token.");
        connection.AccessTokenProtected = _tokenProtector.Protect(accessToken);
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(ReadInt(document.RootElement, "expires_in") ?? 3600);
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return accessToken;
    }

    private async Task<YandexTokenResponse> ExchangeCodeAsync(YandexSettings settings, string code, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            YandexTokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = settings.ClientSecret!,
            }),
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс не выдал токен календаря.");
        }

        using var document = JsonDocument.Parse(payload);
        return new YandexTokenResponse(
            ReadString(document.RootElement, "access_token") ?? throw new InvalidOperationException("Яндекс не вернул access token."),
            ReadString(document.RootElement, "refresh_token"),
            ReadString(document.RootElement, "scope"),
            ReadInt(document.RootElement, "expires_in"));
    }

    private async Task<YandexProfile> LoadYandexProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, YandexUserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс не вернул профиль пользователя.");
        }

        using var document = JsonDocument.Parse(payload);
        var id = ReadString(document.RootElement, "id") ?? ReadString(document.RootElement, "client_id") ?? "yandex";
        var defaultEmail = ReadString(document.RootElement, "default_email");
        var login = ReadString(document.RootElement, "login");
        return new YandexProfile(
            id,
            defaultEmail ?? (login is null ? null : $"{login}@yandex.ru"),
            ReadString(document.RootElement, "real_name") ?? ReadString(document.RootElement, "display_name") ?? login);
    }

    private async Task<YandexCalendarCreateResult> CreateYandexCalendarAsync(
        string accessToken,
        YandexCalendarConnection connection,
        string name,
        int? ownerUserId,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var email = NormalizeOptional(connection.YandexEmail)
            ?? throw new InvalidOperationException("Для Яндекс.Календаря не определён email пользователя.");
        var slug = $"payplanner-{(ownerUserId.HasValue ? $"user-{ownerUserId.Value}" : "tasks")}";
        var homeUrl = $"{CalDavBaseUrl}/calendars/{Uri.EscapeDataString(email)}/";
        var existing = await ResolveYandexCalendarByNameAsync(accessToken, homeUrl, name, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var url = $"{homeUrl}{slug}/";
        var calendarTimeZone = BuildCalendarTimeZoneIcs(timeZone);
        var body = $"""
<?xml version="1.0" encoding="utf-8" ?>
<C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:set>
    <D:prop>
      <D:displayname>{WebUtility.HtmlEncode(name)}</D:displayname>
      <C:calendar-description>{WebUtility.HtmlEncode("PayPlanner CRM")}</C:calendar-description>
      <C:calendar-timezone>{WebUtility.HtmlEncode(calendarTimeZone)}</C:calendar-timezone>
    </D:prop>
  </D:set>
</C:mkcalendar>
""";

        var request = BuildYandexRequest(new HttpMethod("MKCALENDAR"), url, accessToken, body, "application/xml; charset=utf-8");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed))
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс.Календарь не создал календарь.");
            }
        }

        var discovered = await ResolveYandexCalendarByNameAsync(accessToken, homeUrl, name, cancellationToken);
        if (discovered is not null)
        {
            return discovered;
        }

        throw new InvalidOperationException("Яндекс.Календарь создан, но CRM не смогла определить его фактический адрес для синхронизации событий.");
    }

    private async Task<YandexCalendarCreateResult?> ResolveYandexCalendarByNameAsync(
        string accessToken,
        string calendarHomeUrl,
        string name,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }

            var result = await TryResolveYandexCalendarByNameAsync(accessToken, calendarHomeUrl, name, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<YandexCalendarCreateResult?> TryResolveYandexCalendarByNameAsync(
        string accessToken,
        string calendarHomeUrl,
        string name,
        CancellationToken cancellationToken)
    {
        var body = """
<?xml version="1.0" encoding="utf-8" ?>
<D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:prop>
    <D:displayname />
    <D:resourcetype />
    <C:supported-calendar-component-set />
  </D:prop>
</D:propfind>
""";

        using var request = BuildYandexRequest(new HttpMethod("PROPFIND"), calendarHomeUrl, accessToken, body, "application/xml; charset=utf-8");
        request.Headers.Add("Depth", "1");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseYandexCalendarHome(payload, calendarHomeUrl)
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    private async Task<bool> DeleteYandexCalendarAsync(string accessToken, string calendarUrl, CancellationToken cancellationToken)
    {
        using var request = BuildYandexRequest(HttpMethod.Delete, calendarUrl, accessToken);
        return await SendYandexDeleteAsync(request, cancellationToken);
    }

    private async Task<YandexEventWriteResult> PutYandexEventAsync(string accessToken, string eventUrl, string ics, CancellationToken cancellationToken)
    {
        using var request = BuildYandexRequest(HttpMethod.Put, eventUrl, accessToken, ics, "text/calendar; charset=utf-8");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс.Календарь вернул ошибку при сохранении события.");
        }

        return new YandexEventWriteResult(response.Headers.ETag?.Tag);
    }

    private async Task<bool> DeleteYandexEventAsync(string accessToken, string eventUrl, CancellationToken cancellationToken)
    {
        using var request = BuildYandexRequest(HttpMethod.Delete, eventUrl, accessToken);
        return await SendYandexDeleteAsync(request, cancellationToken);
    }

    private async Task DeleteDuplicateYandexEventsForTaskAsync(
        YandexCalendarConnection connection,
        YandexCalendarSyncCalendar calendar,
        int taskId,
        string activeEventId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var events = await ListYandexEventsAsync(accessToken, calendar, cancellationToken);
        foreach (var duplicate in events.Where(item =>
            item.PayPlannerTaskId == taskId &&
            !item.Deleted &&
            !string.Equals(item.Id, activeEventId, StringComparison.Ordinal)))
        {
            try
            {
                var deleted = await DeleteYandexEventAsync(accessToken, duplicate.EventUrl, cancellationToken);
                await AddLogAsync(
                    connection.Id,
                    calendar.Id,
                    taskId,
                    "crm-to-yandex",
                    "delete-duplicate-event",
                    deleted ? "success" : "skip",
                    deleted
                        ? "Лишний дубль события Яндекс.Календаря для задачи CRM удалён."
                        : "Лишний дубль события Яндекс.Календаря для задачи CRM уже отсутствовал.",
                    duplicate.Summary,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await AddLogAsync(
                    connection.Id,
                    calendar.Id,
                    taskId,
                    "crm-to-yandex",
                    "delete-duplicate-event",
                    "error",
                    "Не удалось удалить лишний дубль события Яндекс.Календаря для задачи CRM.",
                    ex.Message,
                    cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<YandexEventItem>> ListYandexEventsAsync(
        string accessToken,
        YandexCalendarSyncCalendar calendar,
        CancellationToken cancellationToken)
    {
        var from = DateTime.UtcNow.AddDays(-30).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(365).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var body = $"""
<?xml version="1.0" encoding="utf-8" ?>
<C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:prop>
    <D:getetag />
    <C:calendar-data />
  </D:prop>
  <C:filter>
    <C:comp-filter name="VCALENDAR">
      <C:comp-filter name="VEVENT">
        <C:time-range start="{from}" end="{to}" />
      </C:comp-filter>
    </C:comp-filter>
  </C:filter>
</C:calendar-query>
""";

        using var request = BuildYandexRequest(new HttpMethod("REPORT"), calendar.CalendarUrl, accessToken, body, "application/xml; charset=utf-8");
        request.Headers.Add("Depth", "1");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс.Календарь не вернул список событий.");
        }

        return ParseYandexEvents(payload, calendar.CalendarUrl);
    }

    private static HttpRequestMessage BuildYandexRequest(
        HttpMethod method,
        string url,
        string accessToken,
        string? body = null,
        string mediaType = "application/xml; charset=utf-8")
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        if (body is not null)
        {
            var contentType = mediaType.Split(';', 2)[0];
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }
        return request;
    }

    private async Task<bool> SendYandexDeleteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return false;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Яндекс.Календарь вернул ошибку при удалении.");
        }

        return true;
    }

    private static IReadOnlyList<YandexEventItem> ParseYandexEvents(string xml, string fallbackCalendarUrl)
    {
        var result = new List<YandexEventItem>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return result;
        }

        var document = XDocument.Parse(xml);
        XNamespace dav = "DAV:";
        XNamespace caldav = "urn:ietf:params:xml:ns:caldav";
        foreach (var response in document.Descendants(dav + "response"))
        {
            var href = NormalizeOptional(response.Element(dav + "href")?.Value);
            var etag = NormalizeOptional(response.Descendants(dav + "getetag").FirstOrDefault()?.Value);
            var data = NormalizeOptional(response.Descendants(caldav + "calendar-data").FirstOrDefault()?.Value);
            if (data is null)
            {
                continue;
            }

            var properties = ParseIcsProperties(data);
            var uid = properties.GetValueOrDefault("UID");
            if (string.IsNullOrWhiteSpace(uid))
            {
                continue;
            }

            var eventUrl = href is null
                ? BuildEventUrl(fallbackCalendarUrl, uid)
                : href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{CalDavBaseUrl}{href}";
            result.Add(new YandexEventItem(
                uid,
                eventUrl,
                etag,
                string.Equals(properties.GetValueOrDefault("STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase),
                properties.GetValueOrDefault("SUMMARY"),
                ReadIcsDate(properties.GetValueOrDefault("DTSTART")),
                ReadIcsDate(properties.GetValueOrDefault("DTEND")),
                ReadPayPlannerTaskId(properties),
                properties.GetValueOrDefault("X-PAYPLANNER-HASH")));
        }

        return result;
    }

    private static IReadOnlyList<YandexCalendarCreateResult> ParseYandexCalendarHome(string xml, string calendarHomeUrl)
    {
        var result = new List<YandexCalendarCreateResult>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return result;
        }

        var document = XDocument.Parse(xml);
        XNamespace dav = "DAV:";
        XNamespace caldav = "urn:ietf:params:xml:ns:caldav";
        foreach (var response in document.Descendants(dav + "response"))
        {
            var href = NormalizeOptional(response.Element(dav + "href")?.Value);
            var displayName = NormalizeOptional(response.Descendants(dav + "displayname").FirstOrDefault()?.Value);
            var isCalendar = response.Descendants(caldav + "calendar").Any();
            if (href is null || displayName is null || !isCalendar)
            {
                continue;
            }

            var absoluteUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : $"{CalDavBaseUrl}{href}";
            var id = absoluteUrl.TrimEnd('/').Split('/').LastOrDefault() ?? displayName;
            result.Add(new YandexCalendarCreateResult(
                id,
                absoluteUrl.EndsWith("/", StringComparison.Ordinal) ? absoluteUrl : absoluteUrl + "/",
                displayName));
        }

        return result;
    }

    private static string BuildIcsEvent(YandexCalendarConnection connection, TaskJournalItem task, string eventId, string crmHash, TimeZoneInfo timeZone)
    {
        var start = CalendarTimeZoneHelper.ToExternalUtc(task.DueAt, timeZone);
        var end = start.AddMinutes(Math.Max(15, task.PlannedDurationMinutes));
        return string.Join("\r\n", [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//PayPlanner//CRM Calendar Sync//RU",
            "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{EscapeIcs(eventId)}",
            $"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}",
            $"DTSTART:{start:yyyyMMdd'T'HHmmss'Z'}",
            $"DTEND:{end:yyyyMMdd'T'HHmmss'Z'}",
            $"SUMMARY:{EscapeIcs($"[{StatusLabel(task.Status)}] {task.Title}")}",
            $"DESCRIPTION:{EscapeIcs(BuildEventDescription(task))}",
            $"X-PAYPLANNER-TASK-ID:{task.Id.ToString(CultureInfo.InvariantCulture)}",
            $"X-PAYPLANNER-CONNECTION-ID:{connection.Id.ToString(CultureInfo.InvariantCulture)}",
            $"X-PAYPLANNER-TIMEZONE:{EscapeIcs(CalendarTimeZoneHelper.ResolveIanaTimeZoneId(timeZone))}",
            $"X-PAYPLANNER-HASH:{crmHash}",
            "END:VEVENT",
            "END:VCALENDAR",
            string.Empty,
        ]);
    }

    private static string BuildYandexCalendarTimeZonePropertyBody(TimeZoneInfo timeZone)
    {
        var calendarTimeZone = BuildCalendarTimeZoneIcs(timeZone);
        return $"""
<?xml version="1.0" encoding="utf-8" ?>
<D:propertyupdate xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:set>
    <D:prop>
      <C:calendar-timezone>{WebUtility.HtmlEncode(calendarTimeZone)}</C:calendar-timezone>
    </D:prop>
  </D:set>
</D:propertyupdate>
""";
    }

    private static string BuildCalendarTimeZoneIcs(TimeZoneInfo timeZone)
    {
        var timeZoneId = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(timeZone);
        var utcNow = DateTime.UtcNow;
        var localNow = CalendarTimeZoneHelper.ToUserLocal(utcNow, timeZone);
        var offset = timeZone.GetUtcOffset(localNow);
        var offsetText = FormatIcsUtcOffset(offset);
        return string.Join("\r\n", [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//PayPlanner//CRM Calendar Sync//RU",
            "BEGIN:VTIMEZONE",
            $"TZID:{EscapeIcs(timeZoneId)}",
            "BEGIN:STANDARD",
            "DTSTART:19700101T000000",
            $"TZOFFSETFROM:{offsetText}",
            $"TZOFFSETTO:{offsetText}",
            $"TZNAME:{EscapeIcs(timeZoneId)}",
            "END:STANDARD",
            "END:VTIMEZONE",
            "END:VCALENDAR",
            string.Empty,
        ]);
    }

    private static string FormatIcsUtcOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{absolute.Hours:00}{absolute.Minutes:00}");
    }

    private static string BuildEventUrl(string calendarUrl, string eventId)
    {
        var normalized = calendarUrl.EndsWith("/", StringComparison.Ordinal) ? calendarUrl : calendarUrl + "/";
        var fileName = eventId.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? eventId : eventId + ".ics";
        return normalized + Uri.EscapeDataString(fileName);
    }

    private static Dictionary<string, string> ParseIcsProperties(string ics)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unfolded = Regex.Replace(ics.Replace("\r\n", "\n").Replace('\r', '\n'), "\n[ \t]", string.Empty);
        foreach (var line in unfolded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Split(';', 2)[0].Trim();
            var value = UnescapeIcs(line[(separator + 1)..].Trim());
            if (!string.IsNullOrWhiteSpace(name))
            {
                properties[name] = value;
            }
        }

        return properties;
    }

    private static DateTime? ReadIcsDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTime.TryParseExact(
            raw.Trim(),
            ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static int? ReadPayPlannerTaskId(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue("X-PAYPLANNER-TASK-ID", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        var description = properties.GetValueOrDefault("DESCRIPTION");
        var match = description is null ? null : Regex.Match(description, @"PayPlanner Task #(?<id>\d+)", RegexOptions.IgnoreCase);
        return match?.Success == true && int.TryParse(match.Groups["id"].Value, out id) ? id : null;
    }

    private static string BuildEventDescription(TaskJournalItem task)
    {
        var lines = new List<string>
        {
            $"PayPlanner Task #{task.Id}",
            $"Объект: {task.ObjectName}",
            $"Статус: {StatusLabel(task.Status)}",
            $"Приоритет: {PriorityLabel(task.Priority)}",
        };
        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            lines.Add($"Исполнитель: {task.AssigneeName}");
        }
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            lines.Add(string.Empty);
            lines.Add(task.Description.Trim());
        }
        if (!string.IsNullOrWhiteSpace(task.Result))
        {
            lines.Add(string.Empty);
            lines.Add($"Результат: {task.Result.Trim()}");
        }
        return string.Join('\n', lines);
    }

    private static string BuildCrmHash(TaskJournalItem task, TimeZoneInfo timeZone)
        => Sha256($"{task.Id}|{task.Title}|{task.Description}|{task.Result}|{task.Status}|{task.Priority}|{CalendarTimeZoneHelper.ToExternalUtc(task.DueAt, timeZone):O}|{task.PlannedDurationMinutes}|{task.AssigneeUserId}|{task.AssigneeName}|{timeZone.Id}");

    private static string BuildYandexHash(YandexEventItem item)
        => Sha256($"{item.Id}|{item.Summary}|{item.Deleted}|{item.StartAt:O}|{item.EndAt:O}|{item.PayPlannerHash}");

    private static string BuildYandexHashFromTask(TaskJournalItem task, string eventId, TimeZoneInfo timeZone)
        => Sha256($"{eventId}|[{StatusLabel(task.Status)}] {task.Title}|False|{CalendarTimeZoneHelper.ToExternalUtc(task.DueAt, timeZone):O}|{CalendarTimeZoneHelper.ToExternalUtc(task.DueAt, timeZone).AddMinutes(Math.Max(15, task.PlannedDurationMinutes)):O}|{BuildCrmHash(task, timeZone)}");

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static bool ShouldHideCompletedTask(CalendarSyncSettingsDto settings, TaskJournalItem task)
        => string.Equals(settings.CompletedTaskCalendarAction, "hide", StringComparison.Ordinal) &&
           string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkedYandexEvent(YandexCalendarTaskLink link, string yandexCalendarId, string yandexEventId)
        => string.Equals(link.YandexCalendarId, yandexCalendarId, StringComparison.Ordinal) &&
           string.Equals(link.YandexEventId, yandexEventId, StringComparison.Ordinal);

    private static DateTime? BuildManualFullSyncAvailableAt(DateTime? lastFullSyncAt, int cooldownMinutes)
        => lastFullSyncAt.HasValue
            ? EnsureUtc(lastFullSyncAt.Value).AddMinutes(Math.Max(0, cooldownMinutes))
            : null;

    private async Task AddLogAsync(
        int? connectionId,
        int? calendarId,
        int? taskId,
        string direction,
        string operation,
        string status,
        string message,
        string? details,
        CancellationToken cancellationToken)
    {
        var detailsJson = await BuildLogDetailsJsonAsync(connectionId, calendarId, taskId, details, cancellationToken);

        _db.YandexCalendarSyncLogs.Add(new YandexCalendarSyncLog
        {
            ConnectionId = connectionId,
            CalendarSyncCalendarId = calendarId,
            TaskJournalItemId = taskId,
            Direction = direction,
            Operation = operation,
            Status = status,
            Message = Trim(message, 1000),
            DetailsJson = detailsJson,
            CreatedAt = DateTime.UtcNow,
        });

        await Task.CompletedTask.WaitAsync(cancellationToken);
    }

    private async Task<string?> BuildLogDetailsJsonAsync(
        int? connectionId,
        int? calendarId,
        int? taskId,
        string? details,
        CancellationToken cancellationToken)
    {
        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(details))
        {
            context["detail"] = Trim(details, 4000);
        }

        if (connectionId.HasValue)
        {
            var connection = await _db.YandexCalendarConnections
                .AsNoTracking()
                .Where(item => item.Id == connectionId.Value)
                .Select(item => new
                {
                    UserName = item.User == null ? string.Empty : item.User.FullName,
                    UserEmail = item.User == null ? string.Empty : item.User.Email,
                })
                .FirstOrDefaultAsync(cancellationToken);

            AddContextValue(context, "userName", connection?.UserName);
            AddContextValue(context, "userEmail", connection?.UserEmail);
        }

        if (calendarId.HasValue)
        {
            var calendar = await _db.YandexCalendarSyncCalendars
                .AsNoTracking()
                .Where(item => item.Id == calendarId.Value)
                .Select(item => new
                {
                    item.Name,
                    OwnerName = item.OwnerUser == null ? string.Empty : item.OwnerUser.FullName,
                    OwnerEmail = item.OwnerUser == null ? string.Empty : item.OwnerUser.Email,
                })
                .FirstOrDefaultAsync(cancellationToken);

            AddContextValue(context, "calendarName", calendar?.Name);
            AddContextValue(context, "calendarOwnerName", calendar?.OwnerName);
            AddContextValue(context, "calendarOwnerEmail", calendar?.OwnerEmail);
        }

        if (taskId.HasValue)
        {
            var task = await _db.TaskJournalItems
                .AsNoTracking()
                .Where(item => item.Id == taskId.Value)
                .Select(item => new
                {
                    item.Title,
                    item.ObjectName,
                    item.AssigneeName,
                })
                .FirstOrDefaultAsync(cancellationToken);

            AddContextValue(context, "taskTitle", task?.Title);
            AddContextValue(context, "taskObjectName", task?.ObjectName);
            AddContextValue(context, "taskAssigneeName", task?.AssigneeName);
        }

        return context.Count == 0 ? null : JsonSerializer.Serialize(context, JsonOptions);
    }

    private static void AddContextValue(Dictionary<string, string> context, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            context[key] = Trim(value, 1000);
        }
    }

    private async Task<bool> ShouldUseAssigneeCalendarModeByDefaultAsync(int userId, CancellationToken cancellationToken)
    {
        var visibility = await ResolveConnectionCalendarVisibilityAsync(userId, cancellationToken);
        return visibility is not null && (visibility.SeesAll || visibility.VisibleUserIds.Count > 1);
    }

    private async Task<DataVisibilityAccess?> ResolveConnectionCalendarVisibilityAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
        ], "calendar-sync"));

        return await _db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Calendar, cancellationToken);
    }

    private static bool IsAssigneeCalendarMode(
        YandexCalendarConnection connection,
        DataVisibilityAccess? visibility)
        => string.Equals(connection.CalendarMode, "adminByAssignee", StringComparison.Ordinal)
            && visibility is not null
            && (visibility.SeesAll || visibility.VisibleUserIds.Count > 1);

    private static bool CanSyncTaskForConnection(
        YandexCalendarConnection connection,
        TaskJournalItem task,
        DataVisibilityAccess? visibility)
    {
        if (visibility is null)
        {
            return false;
        }

        if (visibility.SeesAll)
        {
            return true;
        }

        if (IsAssigneeCalendarMode(connection, visibility))
        {
            return task.AssigneeUserId.HasValue && visibility.VisibleUserIds.Contains(task.AssigneeUserId.Value);
        }

        if (task.AssigneeUserId.HasValue && visibility.VisibleUserIds.Contains(task.AssigneeUserId.Value))
        {
            return true;
        }

        return task.Participants.Any(participant => participant.UserId.HasValue && visibility.VisibleUserIds.Contains(participant.UserId.Value))
            || TaskBelongsToUser(task, connection.UserId);
    }

    private static bool TaskBelongsToUser(TaskJournalItem task, int userId)
        => task.AssigneeUserId == userId || task.Participants.Any(item => item.UserId == userId);

    private static DateTime? NormalizeSyncFromDate(DateTime? value)
        => value?.Date;

    private static DateTime? ResolveSyncFromDate(YandexCalendarConnection connection)
        => connection.SyncFromDate?.Date;

    private static bool IsBeforeSyncFromDate(YandexCalendarConnection connection, TaskJournalItem task)
    {
        var syncFromDate = ResolveSyncFromDate(connection);
        return syncFromDate.HasValue && task.DueAt < syncFromDate.Value;
    }

    private async Task EnsureUserCanManageCalendarAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var user = request.HttpContext.User;
        var userId = user.GetUserId();
        if (!userId.HasValue)
        {
            if (user.IsInRole("admin"))
            {
                return;
            }
        }
        else if (await _db.IsUserAdminAsync(userId.Value, cancellationToken))
        {
            return;
        }

        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        if (string.Equals(settings.UserControlMode, "adminManaged", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Управление синхронизацией календаря ограничено администратором.");
        }
    }

    private async Task<CalendarSyncSettingsDto> LoadCalendarSyncSettingsAsync(CancellationToken cancellationToken)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == CalendarSyncSettingsKey, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            if (settings is not null)
            {
                return CalendarSyncProviderRegistry.NormalizeSettings(settings);
            }
        }
        catch (JsonException)
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings();
        }

        return CalendarSyncProviderRegistry.BuildDefaultSettings();
    }

    private async Task<bool> IsYandexCalendarProviderEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        return settings.YandexCalendarSyncEnabled == true;
    }

    private static void MarkYandexLinkDeleted(YandexCalendarTaskLink link)
    {
        link.SyncStatus = "deleted";
        link.LastDirection = "crm-to-yandex";
        link.LastSyncedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;
        link.LastError = null;
    }

    private async Task<YandexSettings> ResolveYandexSettingsAsync(CancellationToken cancellationToken)
    {
        var enabled = await GetBooleanSettingOrConfigAsync(YandexEnabledKey, "Auth:Yandex:Enabled", false, cancellationToken);
        var clientId = await GetSettingOrConfigAsync(YandexClientIdKey, "Auth:Yandex:ClientId", cancellationToken);
        var clientSecret = await GetSettingOrConfigAsync(YandexClientSecretKey, "Auth:Yandex:ClientSecret", cancellationToken);
        var scopes = await GetSettingOrConfigAsync(YandexScopesKey, "Calendar:Yandex:Scopes", cancellationToken);
        return new YandexSettings(enabled, clientId, clientSecret, scopes);
    }

    private async Task<string?> GetSettingOrConfigAsync(string key, string configPath, CancellationToken cancellationToken)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (!string.IsNullOrWhiteSpace(setting?.Value))
        {
            return setting.Value.Trim();
        }

        var value = _configuration[configPath];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<bool> GetBooleanSettingOrConfigAsync(string key, string configPath, bool fallback, CancellationToken cancellationToken)
    {
        var raw = await GetSettingOrConfigAsync(key, configPath, cancellationToken);
        return raw?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static string BuildAbsoluteUrl(HttpRequest request, string relativePath)
    {
        var normalizedPath = relativePath.StartsWith("/", StringComparison.Ordinal) ? relativePath : "/" + relativePath;
        var scheme = GetProxyHeaderValue(request, "X-Forwarded-Proto") ?? request.Scheme;
        var host = GetProxyHeaderValue(request, "X-Forwarded-Host") ?? request.Host.Value;
        var pathBase = ResolveExternalPathBase(request);
        return $"{scheme}://{host}{pathBase}{normalizedPath}";
    }

    private static string ResolveExternalPathBase(HttpRequest request)
    {
        var requestPathBase = NormalizePathBase(request.PathBase.Value);
        var forwardedPrefix = NormalizePathBase(GetProxyHeaderValue(request, "X-Forwarded-Prefix"));

        if (string.IsNullOrWhiteSpace(forwardedPrefix))
        {
            return requestPathBase ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(requestPathBase))
        {
            return forwardedPrefix;
        }

        return string.Equals(requestPathBase, forwardedPrefix, StringComparison.Ordinal)
            ? requestPathBase
            : $"{requestPathBase}{forwardedPrefix}";
    }

    private static string? GetProxyHeaderValue(HttpRequest request, string headerName)
    {
        if (!request.Headers.TryGetValue(headerName, out var values))
        {
            return null;
        }

        return values.ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }

    private static string? NormalizePathBase(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return null;
        }

        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized.TrimEnd('/')
            : "/" + normalized.TrimEnd('/');
    }

    private static string NormalizeReturnUrl(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "/";
        }

        if (normalized.Contains("://", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return "/";
        }

        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
    }

    private static string BuildPopupHtml(bool success, string? message, string? redirectPath = null)
    {
        var title = success ? "Яндекс.Календарь подключён" : "Ошибка подключения Яндекс.Календаря";
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeMessage = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(message) ? "Вернитесь в CRM." : message.Trim());
        var redirectJson = JsonSerializer.Serialize(redirectPath ?? "/?ppTab=profile");
        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>{{safeTitle}}</title>
  <style>
    body { font-family: Arial, sans-serif; padding: 24px; color: #111827; background: #f8fafc; }
    .card { max-width: 420px; margin: 10vh auto; background: white; border-radius: 16px; padding: 24px; box-shadow: 0 12px 32px rgba(15,23,42,.08); }
    h1 { font-size: 20px; margin: 0 0 12px; }
    p { margin: 0; line-height: 1.5; }
  </style>
</head>
<body>
  <div class="card">
    <h1>{{safeTitle}}</h1>
    <p>{{safeMessage}}</p>
  </div>
  <script>
    setTimeout(function () {
      if (window.opener && !window.opener.closed) {
        window.close();
        return;
      }
      window.location.replace({{redirectJson}});
    }, 1200);
  </script>
</body>
</html>
""";
    }

    private static string GenerateToken(int bytesLength)
        => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytesLength));

    private static string NormalizeKnownValue(string? value, string fallback, IReadOnlyCollection<string> allowed)
    {
        var normalized = NormalizeOptional(value);
        return normalized is not null && allowed.Contains(normalized, StringComparer.Ordinal) ? normalized : fallback;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string? ReadProviderError(string? responseBody, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                return ReadString(document.RootElement, "error_description")
                    ?? ReadString(document.RootElement, "error", "message")
                    ?? ReadString(document.RootElement, "error")
                    ?? ReadString(document.RootElement, "message")
                    ?? NormalizeOptional(fallback);
            }
            catch (JsonException)
            {
            }
        }

        return NormalizeOptional(fallback);
    }

    private static string? ReadString(JsonElement root, params string[] path)
    {
        if (!TryReadPath(root, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeOptional(value.GetString()),
            JsonValueKind.Number => value.GetRawText().Trim(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement root, params string[] path)
    {
        if (!TryReadPath(root, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool TryReadPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static string EscapeIcs(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string UnescapeIcs(string value)
        => value
            .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\;", ";", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string StatusLabel(string? status)
        => status switch
        {
            "new" => "Новая",
            "inProgress" or "in_progress" => "В работе",
            "done" => "Готово",
            "cancelled" => "Отменено",
            "onApproval" or "awaiting_approval" => "На согласовании",
            "approved" => "Согласовано",
            "revision" => "На доработке",
            _ => "Задача",
        };

    private static string PriorityLabel(string? priority)
        => priority switch
        {
            "low" => "Низкий",
            "high" => "Высокий",
            "urgent" => "Срочно",
            _ => "Обычный",
        };

    private static string FormatTaskStatus(string? status) => StatusLabel(status);

    private static string FormatDuration(int? minutes)
        => minutes.HasValue ? $"{minutes.Value} мин." : "не указана";

    private static string FormatLogDateTime(DateTime value)
        => value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string FormatLogDateTime(DateTime value, TimeZoneInfo timeZone)
        => CalendarTimeZoneHelper.FormatUserDateTime(value, timeZone);

    private static string BuildTaskSnapshotDetails(TaskJournalItem task, TimeZoneInfo timeZone)
    {
        var parts = new List<string>
        {
            $"Срок: {FormatLogDateTime(task.DueAt, timeZone)}",
            $"Длительность: {FormatDuration(task.PlannedDurationMinutes)}",
            $"Статус: {FormatTaskStatus(task.Status)}",
        };

        if (!string.IsNullOrWhiteSpace(task.AssigneeName))
        {
            parts.Add($"Исполнитель: {task.AssigneeName}");
        }

        if (!string.IsNullOrWhiteSpace(task.ObjectName))
        {
            parts.Add($"Объект: {task.ObjectName}");
        }

        return string.Join("; ", parts);
    }

    private static string BuildConnectionSettingsDetails(YandexCalendarConnection connection)
    {
        var parts = new List<string>
        {
            $"Подключение: {(connection.IsEnabled ? "включено" : "выключено")}",
            $"Синхронизация: {(connection.SyncEnabled ? "включена" : "выключена")}",
            $"Режим календарей: {FormatCalendarMode(connection.CalendarMode)}",
            $"Правило конфликтов: {FormatYandexConflictMode(connection.ConflictResolutionMode)}",
        };

        parts.Add(connection.SyncFromDate.HasValue
            ? $"Синхронизировать задачи с даты: {FormatLogDateTime(connection.SyncFromDate.Value)}"
            : "Синхронизировать задачи с даты: стандартный диапазон");

        return string.Join("; ", parts);
    }

    private static string FormatCalendarMode(string? mode)
        => mode switch
        {
            "adminByAssignee" => "отдельные календари сотрудников",
            "personal" => "личный календарь пользователя",
            _ => "стандартный режим",
        };

    private static string FormatYandexConflictMode(string? mode)
        => mode switch
        {
            "crmWins" => "CRM главная",
            "yandexWins" => "Яндекс.Календарь главный",
            "latestWins" => "последнее изменение главное",
            _ => "последнее изменение главное",
        };

    private static string? ResolveStatusFromSummary(string? summary)
    {
        var normalized = NormalizeOptional(summary);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.StartsWith("[Готово]", StringComparison.OrdinalIgnoreCase)) return "done";
        if (normalized.StartsWith("[Отменено]", StringComparison.OrdinalIgnoreCase)) return "cancelled";
        if (normalized.StartsWith("[В работе]", StringComparison.OrdinalIgnoreCase)) return "inProgress";
        if (normalized.StartsWith("[Новая]", StringComparison.OrdinalIgnoreCase)) return "new";
        if (normalized.StartsWith("[На согласовании]", StringComparison.OrdinalIgnoreCase)) return "onApproval";
        if (normalized.StartsWith("[Согласовано]", StringComparison.OrdinalIgnoreCase)) return "approved";
        if (normalized.StartsWith("[На доработке]", StringComparison.OrdinalIgnoreCase)) return "revision";
        return null;
    }

    private static string? StripStatusPrefix(string? summary)
    {
        var normalized = NormalizeOptional(summary);
        if (normalized is null || !normalized.StartsWith("[", StringComparison.Ordinal))
        {
            return normalized;
        }

        var closing = normalized.IndexOf(']', StringComparison.Ordinal);
        if (closing < 0 || closing + 1 >= normalized.Length)
        {
            return normalized;
        }

        return NormalizeOptional(normalized[(closing + 1)..]);
    }

    private sealed record YandexSettings(bool Enabled, string? ClientId, string? ClientSecret, string? Scopes)
    {
        public bool Ready => Enabled && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    }

    private sealed record YandexTokenResponse(string AccessToken, string? RefreshToken, string? Scope, int? ExpiresInSeconds);
    private sealed record YandexProfile(string Id, string? Email, string? DisplayName);
    private sealed record YandexCalendarCreateResult(string Id, string Url, string Name = "");
    private sealed record YandexEventWriteResult(string? Etag);
    private sealed record YandexEventItem(string Id, string EventUrl, string? Etag, bool Deleted, string? Summary, DateTime? StartAt, DateTime? EndAt, int? PayPlannerTaskId, string? PayPlannerHash);
    private sealed record ConnectionSyncResult(int ProcessedTasks, int ImportedEvents, int ErrorCount);
}
