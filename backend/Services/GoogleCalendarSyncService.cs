using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class GoogleCalendarSyncService : IGoogleCalendarSyncService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ConnectionSyncLocks = new();

    private const string GoogleEnabledKey = "auth.google.enabled";
    private const string GoogleClientIdKey = "auth.google.clientId";
    private const string GoogleClientSecretKey = "auth.google.clientSecret";
    private const string CalendarScope = "https://www.googleapis.com/auth/calendar";
    private const string GoogleAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GoogleUserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    private const string GoogleApiBaseUrl = "https://www.googleapis.com/calendar/v3";
    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private const int DefaultPollIntervalMs = 2000;
    private const int GoogleRateLimitRetryCount = 2;
    private static readonly TimeSpan AuthRequestLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _tokenProtector;
    private readonly IExternalCalendarSyncCoordinator? _externalCalendarSyncCoordinator;
    private readonly ILogger<GoogleCalendarSyncService> _logger;
    private readonly HashSet<string> _calendarTimeZoneEnsured = [];

    public GoogleCalendarSyncService(
        PaymentContext db,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<GoogleCalendarSyncService> logger)
        : this(db, configuration, httpClientFactory, dataProtectionProvider, null, logger)
    {
    }

    public GoogleCalendarSyncService(
        PaymentContext db,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        IExternalCalendarSyncCoordinator? externalCalendarSyncCoordinator,
        ILogger<GoogleCalendarSyncService> logger)
    {
        _db = db;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1");
        _externalCalendarSyncCoordinator = externalCalendarSyncCoordinator;
        _logger = logger;
    }

    public async Task<GoogleCalendarStatusDto> GetStatusAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await LoadConnectionQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        return await BuildStatusAsync(userId, request, connection, cancellationToken);
    }

    public async Task<GoogleCalendarStartAuthResponseDto> StartAuthAsync(
        int userId,
        StartGoogleCalendarAuthRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var settings = await ResolveGoogleSettingsAsync(cancellationToken);
        if (!await IsGoogleCalendarProviderEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Google Calendar отключен администратором.");
        }

        if (!settings.Ready)
        {
            throw new InvalidOperationException("Настройки Google OAuth не заполнены или отключены.");
        }

        var now = DateTime.UtcNow;
        var state = GenerateToken(32);
        _db.GoogleCalendarAuthRequests.Add(new GoogleCalendarAuthRequest
        {
            UserId = userId,
            State = state,
            ReturnUrl = NormalizeReturnUrl(request.ReturnUrl),
            Status = "pending",
            CreatedAt = now,
            ExpiresAt = now.Add(AuthRequestLifetime),
        });
        await _db.SaveChangesAsync(cancellationToken);

        var callbackUrl = BuildAbsoluteUrl(httpRequest, "/api/google-calendar/auth/callback");
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = callbackUrl,
            ["response_type"] = "code",
            ["state"] = state,
            ["scope"] = $"openid email profile {CalendarScope}",
            ["access_type"] = "offline",
            ["include_granted_scopes"] = "true",
            ["prompt"] = "consent select_account",
        };

        return new GoogleCalendarStartAuthResponseDto
        {
            State = state,
            AuthUrl = QueryHelpers.AddQueryString(GoogleAuthEndpoint, query!),
            PollIntervalMs = DefaultPollIntervalMs,
        };
    }

    public async Task<GoogleCalendarAuthStatusDto> GetAuthStatusAsync(int userId, string state, CancellationToken cancellationToken = default)
    {
        var authRequest = await _db.GoogleCalendarAuthRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId && item.State == state, cancellationToken);
        if (authRequest is null)
        {
            throw new KeyNotFoundException("Запрос подключения Google Calendar не найден.");
        }

        var status = authRequest.Status;
        if (status == "pending" && authRequest.ExpiresAt <= DateTime.UtcNow)
        {
            status = "expired";
        }

        return new GoogleCalendarAuthStatusDto
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
            return BuildPopupHtml(false, "Google не вернул state для подключения календаря.");
        }

        var authRequest = await _db.GoogleCalendarAuthRequests
            .FirstOrDefaultAsync(item => item.State == normalizedState, cancellationToken);
        if (authRequest is null)
        {
            return BuildPopupHtml(false, "Запрос подключения Google Calendar не найден.");
        }

        if (!string.Equals(authRequest.Status, "pending", StringComparison.Ordinal))
        {
            return BuildExistingCallbackHtml(authRequest);
        }

        // Один state может прийти повторно от браузера или провайдера. Условное обновление
        // не допускает двух параллельных обменов одного authorization code на токены.
        var claimed = await _db.GoogleCalendarAuthRequests
            .Where(item => item.Id == authRequest.Id && item.Status == "pending")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "processing"), cancellationToken);
        if (claimed == 0)
        {
            await _db.Entry(authRequest).ReloadAsync(cancellationToken);
            return BuildExistingCallbackHtml(authRequest);
        }
        authRequest.Status = "processing";

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
            authRequest.Error = Trim(NormalizeOptional(errorDescription) ?? error, 2000);
            authRequest.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return BuildPopupHtml(false, authRequest.Error, authRequest.ReturnUrl);
        }

        var normalizedCode = NormalizeOptional(code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            authRequest.Status = "failed";
            authRequest.Error = "Google не вернул код авторизации.";
            authRequest.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return BuildPopupHtml(false, authRequest.Error, authRequest.ReturnUrl);
        }

        try
        {
            var settings = await ResolveGoogleSettingsAsync(cancellationToken);
            if (!settings.Ready)
            {
                throw new InvalidOperationException("Настройки Google OAuth не заполнены или отключены.");
            }

            var callbackUrl = BuildAbsoluteUrl(request, "/api/google-calendar/auth/callback");
            var token = await ExchangeCodeAsync(settings, normalizedCode, callbackUrl, cancellationToken);
            var profile = await LoadGoogleProfileAsync(token.AccessToken, cancellationToken);
            await UpsertConnectionFromTokenAsync(authRequest.UserId, token, profile, cancellationToken);

            authRequest.Status = "completed";
            authRequest.Error = null;
            authRequest.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return BuildPopupHtml(true, "Google Calendar подключён. Вернитесь в CRM.", authRequest.ReturnUrl);
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

    private static string BuildExistingCallbackHtml(GoogleCalendarAuthRequest authRequest)
        => authRequest.Status switch
        {
            "completed" => BuildPopupHtml(true, "Google Calendar уже подключён. Вернитесь в CRM.", authRequest.ReturnUrl),
            "failed" or "expired" => BuildPopupHtml(false, authRequest.Error ?? "Подключение Google Calendar не завершено.", authRequest.ReturnUrl),
            _ => BuildPopupHtml(false, "Запрос подключения Google Calendar уже обрабатывается.", authRequest.ReturnUrl),
        };

    public async Task<GoogleCalendarStatusDto> UpdateSettingsAsync(
        int userId,
        UpdateGoogleCalendarSettingsRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        await EnsureUserCanManageCalendarAsync(httpRequest, cancellationToken);
        var connection = await _db.GoogleCalendarConnections.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is null)
        {
            throw new KeyNotFoundException("Подключение Google Calendar не найдено.");
        }

        connection.IsEnabled = request.IsEnabled ?? connection.IsEnabled;
        connection.SyncEnabled = request.SyncEnabled ?? connection.SyncEnabled;
        connection.CalendarMode = NormalizeKnownValue(request.CalendarMode, connection.CalendarMode, ["personal", "adminByAssignee"]);
        connection.ConflictResolutionMode = NormalizeKnownValue(request.ConflictResolutionMode, connection.ConflictResolutionMode, ["crmWins", "googleWins", "latestWins"]);
        connection.SyncFromDate = NormalizeSyncFromDate(request.SyncFromDate);
        connection.UpdatedAt = DateTime.UtcNow;
        await AddLogAsync(connection.Id, null, null, "system", "settings-updated", "success", "Настройки Google Calendar сохранены.", BuildConnectionSettingsDetails(connection), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return await GetStatusAsync(userId, httpRequest, cancellationToken);
    }

    public async Task<GoogleCalendarStatusDto> DisconnectAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureUserCanManageCalendarAsync(request, cancellationToken);
        var connection = await _db.GoogleCalendarConnections
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
                removeConnectionAfterCleanup: false,
                "Подключение Google Calendar отключается пользователем.",
                failures,
                cancellationToken);

            if (failures.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException(
                    "Не удалось отключить Google Calendar: сначала нужно удалить созданные PayPlanner календари и события во внешнем календаре. "
                    + string.Join("; ", failures));
            }

            _db.GoogleCalendarConnections.Remove(connection);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await GetStatusAsync(userId, request, cancellationToken);
    }

    public async Task<GoogleCalendarSyncNowResultDto> SyncNowAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureUserCanManageCalendarAsync(request, cancellationToken);
        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        await EnsureManualFullSyncAllowedAsync(userId, settings, cancellationToken);
        var result = await SyncConnectionAsync(userId, forceFull: true, cancellationToken);
        return new GoogleCalendarSyncNowResultDto
        {
            ProcessedTasks = result.ProcessedTasks,
            ImportedEvents = result.ImportedEvents,
            ErrorCount = result.ErrorCount,
            Message = result.ErrorCount == 0
                ? "Синхронизация Google Calendar выполнена."
                : "Синхронизация Google Calendar завершена с ошибками. Проверьте журнал.",
            Status = await GetStatusAsync(userId, request, cancellationToken),
        };
    }

    public async Task<GoogleCalendarSchedulerRunResult> SyncDueConnectionsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var userIds = await _db.GoogleCalendarConnections
            .AsNoTracking()
            .Where(item => item.IsEnabled && item.SyncEnabled && item.RefreshTokenProtected != null)
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
                _logger.LogWarning(ex, "Google Calendar scheduled sync failed for user {UserId}", userId);
            }
        }

        return new GoogleCalendarSchedulerRunResult(userIds.Count, processedTasks, importedEvents, errors);
    }

    private async Task EnsureManualFullSyncAllowedAsync(
        int userId,
        CalendarSyncSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var lastFullSyncAt = await _db.GoogleCalendarConnections
            .AsNoTracking()
            .Where(item => item.UserId == userId && item.IsEnabled && item.SyncEnabled)
            .Select(item => item.LastFullSyncAt)
            .FirstOrDefaultAsync(cancellationToken);
        var availableAt = BuildManualFullSyncAvailableAt(lastFullSyncAt, settings.ManualFullSyncCooldownMinutes);
        var now = DateTime.UtcNow;
        if (availableAt.HasValue && availableAt.Value > now)
        {
            throw new CalendarManualSyncCooldownException(
                $"Повторную полную синхронизацию Google Calendar можно запустить после {FormatLogDateTime(availableAt.Value)}. До этого времени работает обычная фоновая синхронизация.",
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

        var userIds = await _db.GoogleCalendarConnections
            .AsNoTracking()
            .Where(item => item.IsEnabled && item.SyncEnabled && item.RefreshTokenProtected != null)
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
                _logger.LogWarning(ex, "Google Calendar task sync failed for task {TaskId} and user {UserId}", taskId, item.UserId);
            }
        }
    }

    public async Task DeleteTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var links = await _db.GoogleCalendarTaskLinks
            .Include(item => item.Connection)
            .Include(item => item.CalendarSyncCalendar)
            .Where(item => item.TaskJournalItemId == taskId && item.SyncStatus != "deleted")
            .ToListAsync(cancellationToken);

        foreach (var link in links)
        {
            if (link.Connection is null || string.IsNullOrWhiteSpace(link.GoogleCalendarId) || string.IsNullOrWhiteSpace(link.GoogleEventId))
            {
                continue;
            }

            try
            {
                var accessToken = await ResolveAccessTokenAsync(link.Connection, cancellationToken);
                var deleted = await DeleteGoogleEventAsync(accessToken, link.GoogleCalendarId, link.GoogleEventId, cancellationToken);
                link.SyncStatus = "deleted";
                link.LastDirection = "crm-to-google";
                link.LastSyncedAt = DateTime.UtcNow;
                link.UpdatedAt = DateTime.UtcNow;
                link.LastError = null;
                await AddLogAsync(
                    link.ConnectionId,
                    link.CalendarSyncCalendarId,
                    link.TaskJournalItemId,
                    "crm-to-google",
                    deleted ? "delete-event" : "delete-event-missing",
                    "success",
                    deleted
                        ? "Событие удалено из Google Calendar после удаления задачи в CRM."
                        : "Событие уже отсутствовало в Google Calendar при удалении задачи в CRM.",
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
                    "crm-to-google",
                    "delete-event",
                    "error",
                    "Не удалось удалить событие из Google Calendar после удаления задачи в CRM.",
                    ex.Message,
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException("GoogleCalendarTaskDeleteFailed", ex);
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
                removeConnectionAfterCleanup: false,
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
            var calendars = connection.Calendars
                .Where(calendar => calendar.OwnerUserId == userId)
                .ToList();
            await CleanupConnectionCalendarsAsync(
                connection,
                calendars,
                removeConnectionAfterCleanup: false,
                $"Сотрудник #{userId} удаляется из CRM, календарь сотрудника убирается из админского Google Calendar.",
                failures,
                cancellationToken);
        }

        if (failures.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException($"GoogleCalendarCleanupFailed: {string.Join("; ", failures)}");
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleWebhookAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        var channelId = request.Headers.TryGetValue("X-Goog-Channel-ID", out StringValues channelHeader)
            ? NormalizeOptional(channelHeader.ToString())
            : null;
        var token = request.Headers.TryGetValue("X-Goog-Channel-Token", out StringValues tokenHeader)
            ? NormalizeOptional(tokenHeader.ToString())
            : null;

        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(token))
        {
            throw new UnauthorizedAccessException("Google Calendar webhook не содержит channel id или token.");
        }

        var calendar = await _db.GoogleCalendarSyncCalendars
            .AsNoTracking()
            .Include(item => item.Connection)
            .FirstOrDefaultAsync(item => item.WatchChannelId == channelId && item.WatchToken == token, cancellationToken);
        if (calendar?.Connection is null)
        {
            throw new UnauthorizedAccessException("Google Calendar webhook не прошёл проверку канала.");
        }

        await SyncConnectionAsync(calendar.Connection.UserId, forceFull: false, cancellationToken);
    }

    private async Task<ConnectionSyncResult> SyncConnectionAsync(int userId, bool forceFull, CancellationToken cancellationToken)
    {
        var syncLock = ConnectionSyncLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(cancellationToken);
        try
        {
            return await SyncConnectionCoreAsync(userId, forceFull, cancellationToken);
        }
        finally
        {
            syncLock.Release();
        }
    }

    private async Task<ConnectionSyncResult> SyncConnectionCoreAsync(int userId, bool forceFull, CancellationToken cancellationToken)
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
            var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
            await CleanupDuplicateCalendarsAsync(connection, accessToken, cancellationToken);
            await EnsureAdminAssigneeCalendarsAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            errors++;
            connection.LastError = Trim(ex.Message, 2000);
            await AddLogAsync(connection.Id, null, null, "system", "ensure-calendars", "error", "Не удалось подготовить календари сотрудников в Google Calendar.", ex.Message, cancellationToken);
        }

        try
        {
            importedEvents += await ImportGoogleChangesAsync(connection, forceFull, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            errors++;
            connection.LastError = Trim(ex.Message, 2000);
            await AddLogAsync(connection.Id, null, null, "google-to-crm", "import", "error", "Не удалось загрузить изменения из Google Calendar.", ex.Message, cancellationToken);
        }

        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        errors += await DeleteLinksOutsideSyncWindowAsync(connection, cancellationToken);

        var tasks = await LoadTasksForConnectionAsync(connection, cancellationToken);
        var externalCommentsByTask = await LoadExternalCalendarCommentsAsync(tasks.Select(item => item.Id), cancellationToken);
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
                    await UpsertGoogleEventForTaskAsync(
                        connection,
                        task,
                        settings,
                        externalCommentsByTask.GetValueOrDefault(task.Id) ?? [],
                        cancellationToken);
                }

                processedTasks++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors++;
                connection.LastError = Trim(ex.Message, 2000);
                await AddLogAsync(connection.Id, null, task.Id, "crm-to-google", "upsert", "error", "Не удалось синхронизировать задачу с Google Calendar.", ex.Message, cancellationToken);
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
        var syncLock = ConnectionSyncLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(cancellationToken);
        try
        {
            await SyncSingleTaskCoreAsync(userId, taskId, cancellationToken);
        }
        finally
        {
            syncLock.Release();
        }
    }

    private async Task SyncSingleTaskCoreAsync(int userId, int taskId, CancellationToken cancellationToken)
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
            var externalComments = await LoadExternalCalendarCommentsAsync([task.Id], cancellationToken);
            await UpsertGoogleEventForTaskAsync(
                connection,
                task,
                settings,
                externalComments.GetValueOrDefault(task.Id) ?? [],
                cancellationToken);
        }

        connection.LastIncrementalSyncAt = DateTime.UtcNow;
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task CleanupDuplicateCalendarsAsync(
        GoogleCalendarConnection connection,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var duplicateGroups = connection.Calendars
            .GroupBy(item => item.OwnerUserId)
            .Where(group => group.Count() > 1)
            .ToList();

        if (duplicateGroups.Count == 0)
        {
            return;
        }

        foreach (var group in duplicateGroups)
        {
            var calendars = group
                .OrderBy(item => item.Id)
                .ToList();
            var canonical = calendars.FirstOrDefault(item => string.Equals(item.GoogleCalendarId, connection.DefaultCalendarId, StringComparison.Ordinal))
                ?? calendars[0];

            if (connection.DefaultCalendarId is null || calendars.Any(item => string.Equals(item.GoogleCalendarId, connection.DefaultCalendarId, StringComparison.Ordinal)))
            {
                connection.DefaultCalendarId = canonical.GoogleCalendarId;
                connection.DefaultCalendarName = canonical.Name;
            }

            foreach (var duplicate in calendars.Where(item => item.Id != canonical.Id))
            {
                try
                {
                    var deleted = await DeleteGoogleCalendarAsync(accessToken, duplicate.GoogleCalendarId, cancellationToken);
                    await AddLogAsync(
                        connection.Id,
                        duplicate.Id,
                        null,
                        "system",
                        deleted ? "delete-duplicate-calendar" : "delete-duplicate-calendar-missing",
                        "success",
                        deleted
                            ? $"Дублирующий календарь «{duplicate.Name}» удален из Google Calendar."
                            : $"Дублирующий календарь «{duplicate.Name}» уже отсутствовал в Google Calendar.",
                        $"Оставлен основной календарь «{canonical.Name}».",
                        cancellationToken);

                    _db.GoogleCalendarSyncCalendars.Remove(duplicate);
                    connection.Calendars.Remove(duplicate);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    duplicate.LastError = Trim(ex.Message, 2000);
                    duplicate.UpdatedAt = DateTime.UtcNow;
                    await AddLogAsync(
                        connection.Id,
                        duplicate.Id,
                        null,
                        "system",
                        "delete-duplicate-calendar",
                        "error",
                        $"Не удалось удалить дублирующий календарь «{duplicate.Name}» из Google Calendar.",
                        ex.Message,
                        cancellationToken);
                }
            }
        }

        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task CleanupConnectionCalendarsAsync(
        GoogleCalendarConnection connection,
        IReadOnlyList<GoogleCalendarSyncCalendar> calendars,
        bool removeConnectionAfterCleanup,
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
            await AddLogAsync(
                connection.Id,
                null,
                null,
                "system",
                "cleanup-user-calendars",
                "error",
                "Не удалось получить токен Google Calendar для очистки календарей удаляемого пользователя.",
                ex.Message,
                cancellationToken);
            return;
        }

        foreach (var calendar in calendars)
        {
            try
            {
                var deleted = await DeleteGoogleCalendarAsync(accessToken, calendar.GoogleCalendarId, cancellationToken);
                await AddLogAsync(
                    connection.Id,
                    calendar.Id,
                    null,
                    "system",
                    deleted ? "delete-calendar" : "delete-calendar-missing",
                    "success",
                    deleted
                        ? $"Календарь «{calendar.Name}» удален из Google Calendar."
                        : $"Календарь «{calendar.Name}» уже отсутствовал в Google Calendar.",
                    reason,
                    cancellationToken);

                foreach (var link in connection.TaskLinks.Where(item => item.CalendarSyncCalendarId == calendar.Id))
                {
                    link.SyncStatus = "deleted";
                    link.LastDirection = "crm-to-google";
                    link.LastSyncedAt = DateTime.UtcNow;
                    link.UpdatedAt = DateTime.UtcNow;
                    link.LastError = null;
                }

                _db.GoogleCalendarSyncCalendars.Remove(calendar);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failures.Add($"календарь {calendar.Name}: {ex.Message}");
                calendar.LastError = Trim(ex.Message, 2000);
                calendar.UpdatedAt = DateTime.UtcNow;
                await AddLogAsync(
                    connection.Id,
                    calendar.Id,
                    null,
                    "system",
                    "delete-calendar",
                    "error",
                    $"Не удалось удалить календарь «{calendar.Name}» из Google Calendar.",
                    ex.Message,
                    cancellationToken);
            }
        }

        if (removeConnectionAfterCleanup)
        {
            _db.GoogleCalendarConnections.Remove(connection);
        }
    }

    private async Task<int> ImportGoogleChangesAsync(GoogleCalendarConnection connection, bool forceFull, CancellationToken cancellationToken)
    {
        var imported = 0;
        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        foreach (var calendar in connection.Calendars.ToList())
        {
            var response = await ListEventsAsync(accessToken, calendar, forceFull, cancellationToken);
            if (response.ResetRequired)
            {
                calendar.SyncToken = null;
                response = await ListEventsAsync(accessToken, calendar, forceFull: true, cancellationToken);
            }

            foreach (var googleEvent in response.Events)
            {
                if (await ApplyGoogleEventAsync(connection, calendar, googleEvent, accessToken, cancellationToken))
                {
                    imported++;
                }
            }

            calendar.SyncToken = response.NextSyncToken ?? calendar.SyncToken;
            calendar.LastSyncedAt = DateTime.UtcNow;
            calendar.LastError = null;
            calendar.UpdatedAt = DateTime.UtcNow;
        }

        return imported;
    }

    private async Task<bool> ApplyGoogleEventAsync(
        GoogleCalendarConnection connection,
        GoogleCalendarSyncCalendar calendar,
        GoogleEventItem googleEvent,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var taskId = googleEvent.PayPlannerTaskId;
        GoogleCalendarTaskLink? link = null;
        if (taskId.HasValue)
        {
            link = await _db.GoogleCalendarTaskLinks
                .Include(item => item.TaskJournalItem)
                .FirstOrDefaultAsync(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == taskId.Value, cancellationToken);

            if (link is not null && !IsLinkedGoogleEvent(link, calendar.GoogleCalendarId, googleEvent.Id))
            {
                if (string.Equals(googleEvent.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                try
                {
                    var deleted = await DeleteGoogleEventAsync(accessToken, calendar.GoogleCalendarId, googleEvent.Id, cancellationToken);
                    await AddLogAsync(
                        connection.Id,
                        calendar.Id,
                        link.TaskJournalItemId,
                        "google-to-crm",
                        "delete-stale-event",
                        deleted ? "success" : "skip",
                        deleted
                            ? "Устаревший дубль события Google Calendar удален, задача CRM оставлена без изменений."
                            : "Устаревший дубль события Google Calendar уже отсутствует, задача CRM оставлена без изменений.",
                        googleEvent.Summary,
                        cancellationToken);
                    return deleted;
                }
                catch (Exception ex)
                {
                    await AddLogAsync(
                        connection.Id,
                        calendar.Id,
                        link.TaskJournalItemId,
                        "google-to-crm",
                        "delete-stale-event",
                        "error",
                        "Не удалось удалить устаревший дубль события Google Calendar, задача CRM оставлена без изменений.",
                        ex.Message,
                        cancellationToken);
                    return false;
                }
            }
        }

        link ??= await _db.GoogleCalendarTaskLinks
            .Include(item => item.TaskJournalItem)
            .FirstOrDefaultAsync(item =>
                item.GoogleCalendarId == calendar.GoogleCalendarId &&
                item.GoogleEventId == googleEvent.Id &&
                item.ConnectionId == connection.Id,
                cancellationToken);

        if (link?.TaskJournalItem is null)
        {
            return false;
        }

        if (string.Equals(googleEvent.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            link.SyncStatus = "detached";
            link.LastDirection = "google-to-crm";
            link.LastSyncedAt = DateTime.UtcNow;
            link.UpdatedAt = DateTime.UtcNow;
            await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "google-to-crm", "detach", "success", "Событие удалено в Google Calendar, связь с задачей помечена как отключённая.", null, cancellationToken);
            return true;
        }

        var nextHash = BuildGoogleHash(googleEvent);
        if (string.Equals(link.LastGoogleHash, nextHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (connection.ConflictResolutionMode == "crmWins")
        {
            link.LastGoogleHash = nextHash;
            link.LastDirection = "google-to-crm";
            link.LastSyncedAt = DateTime.UtcNow;
            link.UpdatedAt = DateTime.UtcNow;
            await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "google-to-crm", "skip", "success", "Изменения в Google Calendar прочитаны, но правило конфликтов оставляет CRM главным источником.", null, cancellationToken);
            return false;
        }

        var task = link.TaskJournalItem;
        var changed = false;
        var changes = new List<string>();
        var timeZone = await ResolveTaskTimeZoneAsync(connection, calendar, task, cancellationToken);
        var importedStartAt = googleEvent.StartAt.HasValue ? CalendarTimeZoneHelper.NormalizeUtcInstant(googleEvent.StartAt.Value) : (DateTime?)null;
        var importedEndAt = googleEvent.EndAt.HasValue ? CalendarTimeZoneHelper.NormalizeUtcInstant(googleEvent.EndAt.Value) : (DateTime?)null;
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

        var importedStatus = ResolveStatusFromGoogleSummary(googleEvent.Summary);
        if (!string.IsNullOrWhiteSpace(importedStatus) && task.Status != importedStatus)
        {
            changes.Add($"Статус: {FormatTaskStatus(task.Status)} → {FormatTaskStatus(importedStatus)}");
            task.Status = importedStatus;
            changed = true;
        }

        var importedTitle = StripStatusPrefix(googleEvent.Summary);
        if (!string.IsNullOrWhiteSpace(importedTitle) && !string.Equals(task.Title, importedTitle, StringComparison.Ordinal))
        {
            changes.Add($"Название: «{task.Title}» → «{Trim(importedTitle, 500)}»");
            task.Title = Trim(importedTitle, 500);
            changed = true;
        }

        if (!changed)
        {
            link.LastGoogleHash = nextHash;
            return false;
        }

        task.UpdatedAt = DateTime.UtcNow;
        link.GoogleEtag = googleEvent.Etag ?? link.GoogleEtag;
        link.LastGoogleHash = nextHash;
        link.LastCrmHash = BuildCrmHash(task, timeZone);
        link.LastDirection = "google-to-crm";
        link.LastSyncedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;
        await AddLogAsync(connection.Id, calendar.Id, task.Id, "google-to-crm", "update-task", "success", "Изменения из Google Calendar применены к задаче.", string.Join("; ", changes), cancellationToken);
        if (_externalCalendarSyncCoordinator is not null)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await _externalCalendarSyncCoordinator.SyncTaskForConnectedUsersAsync(
                task.Id,
                ExternalCalendarSyncCoordinator.GoogleProvider,
                cancellationToken);
        }

        return true;
    }

    private async Task UpsertGoogleEventForTaskAsync(
        GoogleCalendarConnection connection,
        TaskJournalItem task,
        CalendarSyncSettingsDto settings,
        IReadOnlyCollection<TaskJournalComment> externalComments,
        CancellationToken cancellationToken)
    {
        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        var calendar = await EnsureCalendarForTaskAsync(connection, task, accessToken, cancellationToken);
        var timeZone = await ResolveTaskTimeZoneAsync(connection, null, task, cancellationToken);
        var crmHash = BuildCrmHash(task, timeZone, externalComments);
        var link = await _db.GoogleCalendarTaskLinks
            .FirstOrDefaultAsync(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == task.Id, cancellationToken);

        if (link is not null
            && string.Equals(link.LastCrmHash, crmHash, StringComparison.Ordinal)
            && string.Equals(link.SyncStatus, "active", StringComparison.Ordinal))
        {
            return;
        }

        var payload = BuildGoogleEventPayload(connection, task, settings, externalComments, crmHash, timeZone);
        var previousGoogleCalendarId = link?.GoogleCalendarId;
        var previousGoogleEventId = link?.GoogleEventId;
        var createNewEvent = link is null ||
            string.IsNullOrWhiteSpace(link.GoogleEventId) ||
            link.GoogleCalendarId != calendar.GoogleCalendarId ||
            link.SyncStatus == "detached";

        GoogleEventWriteResult result;
        if (createNewEvent)
        {
            result = await CreateGoogleEventAsync(accessToken, calendar.GoogleCalendarId, payload, cancellationToken);
            if (link is null)
            {
                link = new GoogleCalendarTaskLink
                {
                    ConnectionId = connection.Id,
                    CalendarSyncCalendarId = calendar.Id,
                    TaskJournalItemId = task.Id,
                    GoogleCalendarId = calendar.GoogleCalendarId,
                    GoogleEventId = result.Id,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.GoogleCalendarTaskLinks.Add(link);
            }
            else if (!string.IsNullOrWhiteSpace(previousGoogleCalendarId) &&
                     !string.IsNullOrWhiteSpace(previousGoogleEventId) &&
                     !string.Equals(previousGoogleCalendarId, calendar.GoogleCalendarId, StringComparison.Ordinal) &&
                     !string.Equals(link.SyncStatus, "detached", StringComparison.Ordinal))
            {
                try
                {
                    await DeleteGoogleEventAsync(accessToken, previousGoogleCalendarId, previousGoogleEventId, cancellationToken);
                    await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "delete-event", "success", "Старое событие Google Calendar удалено после переноса задачи в другой календарь.", BuildTaskSnapshotDetails(task, timeZone), cancellationToken);
                }
                catch (Exception ex)
                {
                    await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "delete-event", "error", "Не удалось удалить старое событие Google Calendar после переноса задачи в другой календарь.", ex.Message, cancellationToken);
                }
            }
            await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "create-event", "success", "Задача создана в Google Calendar.", BuildTaskSnapshotDetails(task, timeZone), cancellationToken);
        }
        else
        {
            if (link is null)
            {
                throw new InvalidOperationException("GoogleCalendarTaskLinkMissing");
            }

            result = await UpdateGoogleEventAsync(accessToken, link.GoogleCalendarId, link.GoogleEventId, payload, cancellationToken);
            await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "update-event", "success", "Задача обновлена в Google Calendar.", BuildTaskSnapshotDetails(task, timeZone), cancellationToken);
        }

        link.CalendarSyncCalendarId = calendar.Id;
        link.GoogleCalendarId = calendar.GoogleCalendarId;
        link.GoogleEventId = result.Id;
        link.GoogleEtag = result.Etag;
        link.LastCrmHash = crmHash;
        link.LastGoogleHash = BuildGoogleHashFromPayload(payload, result.Id);
        link.LastDirection = "crm-to-google";
        link.SyncStatus = "active";
        link.LastError = null;
        link.LastSyncedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;

        await DeleteDuplicateGoogleEventsForTaskAsync(
            connection,
            calendar,
            task.Id,
            result.Id,
            accessToken,
            cancellationToken);
    }

    private async Task EnsureAdminAssigneeCalendarsAsync(
        GoogleCalendarConnection connection,
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
                removeConnectionAfterCleanup: false,
                reason: "Доступ к календарю сотрудника отозван в правах роли.",
                failures,
                cancellationToken);
            if (failures.Count > 0)
            {
                throw new InvalidOperationException($"GoogleCalendarRevokedCalendarCleanupFailed: {string.Join("; ", failures)}");
            }
        }
    }

    private async Task<GoogleCalendarSyncCalendar> EnsureCalendarForTaskAsync(
        GoogleCalendarConnection connection,
        TaskJournalItem task,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var visibility = await ResolveConnectionCalendarVisibilityAsync(connection.UserId, cancellationToken);
        var ownerUserId = IsAssigneeCalendarMode(connection, visibility) ? task.AssigneeUserId : null;
        var ownerName = ownerUserId.HasValue ? NormalizeOptional(task.AssigneeName) : null;

        return await EnsureCalendarForOwnerAsync(connection, ownerUserId, ownerName, accessToken, cancellationToken);
    }

    private async Task<GoogleCalendarSyncCalendar> EnsureCalendarForOwnerAsync(
        GoogleCalendarConnection connection,
        int? ownerUserId,
        string? ownerName,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var name = ownerUserId.HasValue
            ? $"PayPlanner - {NormalizeOptional(ownerName) ?? $"сотрудник #{ownerUserId.Value}"}"
            : "PayPlanner задачи";

        var calendar = await _db.GoogleCalendarSyncCalendars
            .Where(item => item.ConnectionId == connection.Id && item.OwnerUserId == ownerUserId)
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, ownerUserId ?? connection.UserId, cancellationToken);
        if (calendar is not null)
        {
            await EnsureGoogleCalendarTimeZoneAsync(accessToken, calendar, timeZone, cancellationToken);
            return calendar;
        }

        var created = await CreateGoogleCalendarAsync(accessToken, name, timeZone, cancellationToken);
        calendar = new GoogleCalendarSyncCalendar
        {
            ConnectionId = connection.Id,
            OwnerUserId = ownerUserId,
            GoogleCalendarId = created.Id,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        connection.Calendars.Add(calendar);
        _db.GoogleCalendarSyncCalendars.Add(calendar);
        connection.DefaultCalendarId ??= created.Id;
        connection.DefaultCalendarName ??= name;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            _db.Entry(calendar).State = EntityState.Detached;
            connection.Calendars.Remove(calendar);
            try
            {
                await DeleteGoogleCalendarAsync(accessToken, created.Id, cancellationToken);
            }
            catch (Exception deleteEx) when (!cancellationToken.IsCancellationRequested)
            {
                await AddLogAsync(connection.Id, null, null, "system", "delete-race-calendar", "error", $"Не удалось удалить календарь «{name}», созданный повторным параллельным запуском.", deleteEx.Message, cancellationToken);
            }

            var existing = await _db.GoogleCalendarSyncCalendars
                .Where(item => item.ConnectionId == connection.Id && item.OwnerUserId == ownerUserId)
                .OrderBy(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }

        await AddLogAsync(connection.Id, calendar.Id, null, "system", "create-calendar", "success", $"В Google Calendar создан календарь «{name}».", null, cancellationToken);
        return calendar;
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyCollection<TaskJournalComment>>> LoadExternalCalendarCommentsAsync(
        IEnumerable<int> taskIds,
        CancellationToken cancellationToken)
    {
        var ids = taskIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, IReadOnlyCollection<TaskJournalComment>>();
        }

        var comments = await _db.TaskJournalComments
            .AsNoTracking()
            .Where(item =>
                ids.Contains(item.TaskJournalItemId)
                && item.IsExternalCalendarVisible
                && item.ExternalDeletedAt == null
                && item.Kind == "comment")
            .Select(item => new TaskJournalComment
            {
                Id = item.Id,
                TaskJournalItemId = item.TaskJournalItemId,
                AuthorName = item.AuthorName,
                Kind = item.Kind,
                Text = item.Text,
                IsExternalCalendarVisible = true,
                CreatedAt = item.CreatedAt,
            })
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        return comments
            .GroupBy(item => item.TaskJournalItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<TaskJournalComment>)group.ToArray());
    }

    private async Task<IReadOnlyList<TaskJournalItem>> LoadTasksForConnectionAsync(GoogleCalendarConnection connection, CancellationToken cancellationToken)
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

    private async Task EnsureGoogleCalendarTimeZoneAsync(
        string accessToken,
        GoogleCalendarSyncCalendar calendar,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var ianaTimeZoneId = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(timeZone);
        var ensureKey = $"{calendar.GoogleCalendarId}|{ianaTimeZoneId}";
        if (!_calendarTimeZoneEnsured.Add(ensureKey))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { timeZone = ianaTimeZoneId }, JsonOptions);
        using var request = BuildGoogleRequest(
            HttpMethod.Patch,
            $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendar.GoogleCalendarId)}",
            accessToken,
            payload);
        await SendGoogleJsonAsync(request, cancellationToken);
    }

    private async Task<TimeZoneInfo> ResolveTaskTimeZoneAsync(
        GoogleCalendarConnection connection,
        GoogleCalendarSyncCalendar? calendar,
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

    private async Task<int> DeleteLinksOutsideSyncWindowAsync(GoogleCalendarConnection connection, CancellationToken cancellationToken)
    {
        var syncFromDate = ResolveSyncFromDate(connection);
        if (!syncFromDate.HasValue)
        {
            return 0;
        }

        var links = await _db.GoogleCalendarTaskLinks
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
                var deleted = await DeleteGoogleEventAsync(accessToken, link.GoogleCalendarId, link.GoogleEventId, cancellationToken);
                MarkGoogleLinkDeleted(link);
                await AddLogAsync(
                    connection.Id,
                    link.CalendarSyncCalendarId,
                    link.TaskJournalItemId,
                    "crm-to-google",
                    deleted ? "delete-event" : "delete-event-missing",
                    "success",
                    deleted
                        ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации."
                        : "Событие уже отсутствовало в Google Calendar при очистке задач раньше даты начала синхронизации.",
                    null,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                link.LastError = Trim(ex.Message, 2000);
                link.UpdatedAt = DateTime.UtcNow;
                await AddLogAsync(connection.Id, link.CalendarSyncCalendarId, link.TaskJournalItemId, "crm-to-google", "delete-event", "error", "Не удалось удалить событие Google Calendar для задачи раньше даты начала синхронизации.", ex.Message, cancellationToken);
            }
        }

        return errors;
    }

    private async Task DeleteTaskLinkOutsideSyncWindowAsync(GoogleCalendarConnection connection, int taskId, CancellationToken cancellationToken)
    {
        if (!ResolveSyncFromDate(connection).HasValue)
        {
            return;
        }

        var link = await _db.GoogleCalendarTaskLinks
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
        var deleted = await DeleteGoogleEventAsync(accessToken, link.GoogleCalendarId, link.GoogleEventId, cancellationToken);
        MarkGoogleLinkDeleted(link);
        await AddLogAsync(
            connection.Id,
            link.CalendarSyncCalendarId,
            link.TaskJournalItemId,
            "crm-to-google",
            deleted ? "delete-event" : "delete-event-missing",
            "success",
            deleted
                ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации."
                : "Событие уже отсутствовало в Google Calendar при очистке задачи раньше даты начала синхронизации.",
            null,
            cancellationToken);
    }

    private async Task DeleteTaskLinkForCompletedTaskAsync(GoogleCalendarConnection connection, int taskId, CancellationToken cancellationToken)
    {
        var link = await _db.GoogleCalendarTaskLinks
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
        var deleted = await DeleteGoogleEventAsync(accessToken, link.GoogleCalendarId, link.GoogleEventId, cancellationToken);
        MarkGoogleLinkDeleted(link);
        await AddLogAsync(
            connection.Id,
            link.CalendarSyncCalendarId,
            link.TaskJournalItemId,
            "crm-to-google",
            deleted ? "delete-event" : "delete-event-missing",
            "success",
            deleted
                ? "Событие удалено из Google Calendar, потому что в настройках включено скрытие выполненных задач."
                : "Событие уже отсутствовало в Google Calendar при скрытии выполненной задачи.",
            null,
            cancellationToken);
    }

    private async Task DeleteTaskLinkForRevokedAccessAsync(GoogleCalendarConnection connection, int taskId, CancellationToken cancellationToken)
    {
        var link = await _db.GoogleCalendarTaskLinks
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
        var deleted = await DeleteGoogleEventAsync(accessToken, link.GoogleCalendarId, link.GoogleEventId, cancellationToken);
        MarkGoogleLinkDeleted(link);
        await AddLogAsync(
            connection.Id,
            link.CalendarSyncCalendarId,
            link.TaskJournalItemId,
            "crm-to-google",
            deleted ? "delete-event" : "delete-event-missing",
            "success",
            deleted
                ? "Событие удалено из Google Calendar, потому что доступ к календарю сотрудника отозван в правах роли."
                : "Событие уже отсутствовало в Google Calendar при отзыве доступа к календарю сотрудника.",
            null,
            cancellationToken);
    }

    private IQueryable<GoogleCalendarConnection> LoadConnectionQuery()
        => _db.GoogleCalendarConnections
            .Include(item => item.User)
            .ThenInclude(item => item.Role)
            .Include(item => item.TaskLinks)
            .Include(item => item.Calendars)
            .ThenInclude(item => item.OwnerUser)
            .AsSplitQuery();

    private async Task<GoogleCalendarStatusDto> BuildStatusAsync(
        int userId,
        HttpRequest request,
        GoogleCalendarConnection? connection,
        CancellationToken cancellationToken)
    {
        var logs = connection is null
            ? []
            : await _db.GoogleCalendarSyncLogs
                .AsNoTracking()
                .Where(item => item.ConnectionId == connection.Id)
                .OrderByDescending(item => item.CreatedAt)
                .Take(20)
                .Select(item => new GoogleCalendarSyncLogDto
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

        var oauthSettings = await ResolveGoogleSettingsAsync(cancellationToken);
        var providerEnabled = await IsGoogleCalendarProviderEnabledAsync(cancellationToken);
        var calendarSettings = await LoadCalendarSyncSettingsAsync(cancellationToken);

        return new GoogleCalendarStatusDto
        {
            IsAvailable = providerEnabled,
            GoogleOAuthReady = oauthSettings.Ready,
            CallbackUrl = BuildAbsoluteUrl(request, "/api/google-calendar/auth/callback"),
            Connection = connection is null ? null : MapConnection(connection, calendarSettings),
            RecentLogs = logs,
        };
    }

    private static GoogleCalendarConnectionDto MapConnection(GoogleCalendarConnection connection, CalendarSyncSettingsDto settings)
        => new()
        {
            Id = connection.Id,
            GoogleEmail = connection.GoogleEmail,
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
                .Select(item => new GoogleCalendarCalendarDto
                {
                    Id = item.Id,
                    OwnerUserId = item.OwnerUserId,
                    OwnerName = item.OwnerUser?.FullName,
                    GoogleCalendarId = item.GoogleCalendarId,
                    Name = item.Name,
                    LastSyncedAt = item.LastSyncedAt,
                    WatchExpiresAt = item.WatchExpiresAt,
                    LastError = item.LastError,
                })
                .ToList(),
        };

    private async Task UpsertConnectionFromTokenAsync(
        int userId,
        GoogleTokenResponse token,
        GoogleProfile profile,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var connection = await _db.GoogleCalendarConnections.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is null)
        {
            connection = new GoogleCalendarConnection
            {
                UserId = userId,
                CalendarMode = await ShouldUseAssigneeCalendarModeByDefaultAsync(userId, cancellationToken) ? "adminByAssignee" : "personal",
                ConflictResolutionMode = "latestWins",
                CreatedAt = now,
            };
            _db.GoogleCalendarConnections.Add(connection);
        }

        connection.GoogleUserId = profile.Sub;
        connection.GoogleEmail = profile.Email;
        connection.DisplayName = profile.Name;
        connection.AccessTokenProtected = _tokenProtector.Protect(token.AccessToken);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            connection.RefreshTokenProtected = _tokenProtector.Protect(token.RefreshToken);
        }
        connection.AccessTokenExpiresAt = token.ExpiresInSeconds.HasValue ? now.AddSeconds(token.ExpiresInSeconds.Value) : now.AddHours(1);
        connection.Scope = NormalizeOptional(token.Scope) ?? $"openid email profile {CalendarScope}";
        connection.IsEnabled = true;
        connection.SyncEnabled = true;
        connection.LastError = null;
        connection.UpdatedAt = now;

        await AddLogAsync(connection.Id == 0 ? null : connection.Id, null, null, "system", "oauth-connected", "success", "Google Calendar подключён к профилю.", null, cancellationToken);
    }

    private async Task<string> ResolveAccessTokenAsync(GoogleCalendarConnection connection, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(connection.AccessTokenProtected)
            && connection.AccessTokenExpiresAt > DateTime.UtcNow.Add(AccessTokenRefreshSkew))
        {
            return _tokenProtector.Unprotect(connection.AccessTokenProtected);
        }

        if (string.IsNullOrWhiteSpace(connection.RefreshTokenProtected))
        {
            throw new InvalidOperationException("Нет refresh token для Google Calendar. Подключите календарь заново.");
        }

        var settings = await ResolveGoogleSettingsAsync(cancellationToken);
        if (!settings.Ready)
        {
            throw new InvalidOperationException("Настройки Google OAuth не заполнены или отключены.");
        }

        var refreshToken = _tokenProtector.Unprotect(connection.RefreshTokenProtected);
        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            GoogleTokenEndpoint,
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
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Google не обновил токен Calendar API.");
        }

        using var document = JsonDocument.Parse(payload);
        var accessToken = ReadString(document.RootElement, "access_token") ?? throw new InvalidOperationException("Google не вернул access token.");
        connection.AccessTokenProtected = _tokenProtector.Protect(accessToken);
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(ReadInt(document.RootElement, "expires_in") ?? 3600);
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return accessToken;
    }

    private async Task<GoogleTokenResponse> ExchangeCodeAsync(GoogleSettings settings, string code, string callbackUrl, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            GoogleTokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = settings.ClientSecret!,
                ["redirect_uri"] = callbackUrl,
            }),
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Google не выдал токен Calendar API.");
        }

        using var document = JsonDocument.Parse(payload);
        return new GoogleTokenResponse(
            ReadString(document.RootElement, "access_token") ?? throw new InvalidOperationException("Google не вернул access token."),
            ReadString(document.RootElement, "refresh_token"),
            ReadString(document.RootElement, "scope"),
            ReadInt(document.RootElement, "expires_in"));
    }

    private async Task<GoogleProfile> LoadGoogleProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, GoogleUserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Google не вернул профиль пользователя.");
        }

        using var document = JsonDocument.Parse(payload);
        return new GoogleProfile(
            ReadString(document.RootElement, "sub") ?? throw new InvalidOperationException("Google не вернул id пользователя."),
            ReadString(document.RootElement, "email"),
            ReadString(document.RootElement, "name"));
    }

    private async Task<GoogleCalendarCreateResult> CreateGoogleCalendarAsync(string accessToken, string name, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { summary = name, timeZone = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(timeZone) }, JsonOptions);
        using var request = BuildGoogleRequest(HttpMethod.Post, $"{GoogleApiBaseUrl}/calendars", accessToken, payload);
        var document = await SendGoogleJsonAsync(request, cancellationToken);
        return new GoogleCalendarCreateResult(ReadString(document.RootElement, "id") ?? throw new InvalidOperationException("Google не вернул id календаря."));
    }

    private async Task<bool> DeleteGoogleCalendarAsync(string accessToken, string calendarId, CancellationToken cancellationToken)
    {
        using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}", accessToken);
        return await SendGoogleDeleteAsync(request, cancellationToken);
    }

    private async Task<GoogleEventWriteResult> CreateGoogleEventAsync(string accessToken, string calendarId, GoogleEventPayload payload, CancellationToken cancellationToken)
    {
        using var request = BuildGoogleRequest(HttpMethod.Post, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events", accessToken, JsonSerializer.Serialize(payload, JsonOptions));
        var document = await SendGoogleJsonAsync(request, cancellationToken);
        return new GoogleEventWriteResult(
            ReadString(document.RootElement, "id") ?? throw new InvalidOperationException("Google не вернул id события."),
            ReadString(document.RootElement, "etag"));
    }

    private async Task<GoogleEventWriteResult> UpdateGoogleEventAsync(string accessToken, string calendarId, string eventId, GoogleEventPayload payload, CancellationToken cancellationToken)
    {
        using var request = BuildGoogleRequest(HttpMethod.Put, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken, JsonSerializer.Serialize(payload, JsonOptions));
        var document = await SendGoogleJsonAsync(request, cancellationToken);
        return new GoogleEventWriteResult(
            ReadString(document.RootElement, "id") ?? eventId,
            ReadString(document.RootElement, "etag"));
    }

    private async Task<bool> DeleteGoogleEventAsync(string accessToken, string calendarId, string eventId, CancellationToken cancellationToken)
    {
        using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken);
        return await SendGoogleDeleteAsync(request, cancellationToken);
    }

    private async Task DeleteDuplicateGoogleEventsForTaskAsync(
        GoogleCalendarConnection connection,
        GoogleCalendarSyncCalendar calendar,
        int taskId,
        string activeEventId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var duplicates = await ListGoogleEventsForTaskAsync(accessToken, calendar.GoogleCalendarId, taskId, cancellationToken);
        foreach (var duplicate in duplicates.Where(item =>
            !string.Equals(item.Id, activeEventId, StringComparison.Ordinal) &&
            !string.Equals(item.Status, "cancelled", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var deleted = await DeleteGoogleEventAsync(accessToken, calendar.GoogleCalendarId, duplicate.Id, cancellationToken);
                await AddLogAsync(
                    connection.Id,
                    calendar.Id,
                    taskId,
                    "crm-to-google",
                    "delete-duplicate-event",
                    deleted ? "success" : "skip",
                    deleted
                        ? "Лишний дубль события Google Calendar для задачи CRM удалён."
                        : "Лишний дубль события Google Calendar для задачи CRM уже отсутствовал.",
                    duplicate.Summary,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await AddLogAsync(
                    connection.Id,
                    calendar.Id,
                    taskId,
                    "crm-to-google",
                    "delete-duplicate-event",
                    "error",
                    "Не удалось удалить лишний дубль события Google Calendar для задачи CRM.",
                    ex.Message,
                    cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<GoogleEventItem>> ListGoogleEventsForTaskAsync(
        string accessToken,
        string calendarId,
        int taskId,
        CancellationToken cancellationToken)
    {
        var events = new List<GoogleEventItem>();
        string? pageToken = null;
        do
        {
            var query = new Dictionary<string, string?>
            {
                ["singleEvents"] = "true",
                ["showDeleted"] = "true",
                ["maxResults"] = "2500",
                ["privateExtendedProperty"] = $"payPlannerTaskId={taskId.ToString(CultureInfo.InvariantCulture)}",
            };
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                query["pageToken"] = pageToken;
            }

            var url = QueryHelpers.AddQueryString($"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events", query!);
            using var request = BuildGoogleRequest(HttpMethod.Get, url, accessToken);
            var response = await SendGoogleWithRetryAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google не вернул события задачи.");
            }

            using var document = JsonDocument.Parse(response.Payload);
            if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var eventId = ReadString(item, "id");
                    if (string.IsNullOrWhiteSpace(eventId))
                    {
                        continue;
                    }

                    events.Add(new GoogleEventItem(
                        eventId,
                        ReadString(item, "etag"),
                        ReadString(item, "status"),
                        ReadString(item, "summary"),
                        ReadGoogleDate(item, "start"),
                        ReadGoogleDate(item, "end"),
                        ReadPayPlannerTaskId(item)));
                }
            }

            pageToken = ReadString(document.RootElement, "nextPageToken");
        } while (!string.IsNullOrWhiteSpace(pageToken));

        return events;
    }

    private async Task<GoogleEventsResponse> ListEventsAsync(
        string accessToken,
        GoogleCalendarSyncCalendar calendar,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["singleEvents"] = "true",
            ["showDeleted"] = "true",
            ["maxResults"] = "2500",
        };

        if (!forceFull && !string.IsNullOrWhiteSpace(calendar.SyncToken))
        {
            query["syncToken"] = calendar.SyncToken;
        }
        else
        {
            query["timeMin"] = DateTime.UtcNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture);
            query["timeMax"] = DateTime.UtcNow.AddDays(365).ToString("O", CultureInfo.InvariantCulture);
        }

        var url = QueryHelpers.AddQueryString($"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendar.GoogleCalendarId)}/events", query!);
        using var request = BuildGoogleRequest(HttpMethod.Get, url, accessToken);
        var response = await SendGoogleWithRetryAsync(request, cancellationToken);
        if ((int)response.StatusCode == 410)
        {
            return new GoogleEventsResponse([], null, ResetRequired: true);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google не вернул список событий.");
        }

        using var document = JsonDocument.Parse(response.Payload);
        var events = new List<GoogleEventItem>();
        if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var eventId = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(eventId))
                {
                    continue;
                }

                events.Add(new GoogleEventItem(
                    eventId,
                    ReadString(item, "etag"),
                    ReadString(item, "status"),
                    ReadString(item, "summary"),
                    ReadGoogleDate(item, "start"),
                    ReadGoogleDate(item, "end"),
                    ReadPayPlannerTaskId(item)));
            }
        }

        return new GoogleEventsResponse(events, ReadString(document.RootElement, "nextSyncToken"), ResetRequired: false);
    }

    private async Task<JsonDocument> SendGoogleJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await SendGoogleWithRetryAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку.");
        }

        return JsonDocument.Parse(response.Payload);
    }

    private async Task<bool> SendGoogleDeleteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await SendGoogleWithRetryAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку при удалении.");
        }

        return true;
    }

    private async Task<GoogleHttpResponse> SendGoogleWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var snapshot = await GoogleRequestSnapshot.FromAsync(request, cancellationToken);
        GoogleHttpResponse? lastResponse = null;

        for (var attempt = 0; attempt <= GoogleRateLimitRetryCount; attempt++)
        {
            using var retryRequest = snapshot.CreateRequest();
            using var response = await client.SendAsync(retryRequest, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = new GoogleHttpResponse(response.StatusCode, response.ReasonPhrase, payload);
            if (!IsGoogleRetryableResponse(result) || attempt == GoogleRateLimitRetryCount)
            {
                return result;
            }

            lastResponse = result;
            var delay = ResolveGoogleRetryDelay(response, attempt);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        return lastResponse ?? new GoogleHttpResponse(HttpStatusCode.TooManyRequests, null, string.Empty);
    }

    private static HttpRequestMessage BuildGoogleRequest(HttpMethod method, string url, string accessToken, string? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static GoogleEventPayload BuildGoogleEventPayload(
        GoogleCalendarConnection connection,
        TaskJournalItem task,
        CalendarSyncSettingsDto settings,
        IReadOnlyCollection<TaskJournalComment> externalComments,
        string crmHash,
        TimeZoneInfo timeZone)
    {
        var start = CalendarTimeZoneHelper.NormalizeUtcInstant(task.DueAt);
        var end = start.AddMinutes(Math.Max(15, task.PlannedDurationMinutes));
        var timeZoneId = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(timeZone);
        return new GoogleEventPayload
        {
            Summary = $"[{StatusLabel(task.Status)}] {task.Title}",
            Description = BuildEventDescription(task, externalComments),
            Start = new GoogleEventDateTime { DateTime = CalendarTimeZoneHelper.FormatOffsetDateTime(start, timeZone), TimeZone = timeZoneId },
            End = new GoogleEventDateTime { DateTime = CalendarTimeZoneHelper.FormatOffsetDateTime(end, timeZone), TimeZone = timeZoneId },
            ColorId = ResolveCompletedTaskGoogleColorId(task, settings),
            ExtendedProperties = new GoogleEventExtendedProperties
            {
                Private = new Dictionary<string, string>
                {
                    ["payPlannerTaskId"] = task.Id.ToString(CultureInfo.InvariantCulture),
                    ["payPlannerConnectionId"] = connection.Id.ToString(CultureInfo.InvariantCulture),
                    ["payPlannerHash"] = crmHash,
                    ["payPlannerTimeZone"] = timeZoneId,
                },
            },
        };
    }

    private static string BuildEventDescription(
        TaskJournalItem task,
        IReadOnlyCollection<TaskJournalComment> externalComments)
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
        return ExternalCalendarCommentBlockService
            .AppendToDescription(string.Join('\n', lines), externalComments)
            .Description;
    }

    private static string BuildCrmHash(
        TaskJournalItem task,
        TimeZoneInfo timeZone,
        IReadOnlyCollection<TaskJournalComment>? externalComments = null)
    {
        var commentsHash = Sha256(BuildEventDescription(task, externalComments ?? []));
        return Sha256($"{task.Id}|{task.Title}|{task.Description}|{task.Result}|{task.Status}|{task.Priority}|{CalendarTimeZoneHelper.ToExternalUtc(task.DueAt, timeZone):O}|{task.PlannedDurationMinutes}|{task.AssigneeUserId}|{task.AssigneeName}|{timeZone.Id}|{commentsHash}");
    }

    private static string BuildGoogleHash(GoogleEventItem item)
        => Sha256($"{item.Id}|{item.Summary}|{item.Status}|{item.StartAt:O}|{item.EndAt:O}");

    private static string BuildGoogleHashFromPayload(GoogleEventPayload payload, string eventId)
        => Sha256($"{eventId}|{payload.Summary}|confirmed|{payload.Start.DateTime}|{payload.End.DateTime}|{payload.ColorId}");

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

    private static bool IsLinkedGoogleEvent(GoogleCalendarTaskLink link, string googleCalendarId, string googleEventId)
        => string.Equals(link.GoogleCalendarId, googleCalendarId, StringComparison.Ordinal) &&
           string.Equals(link.GoogleEventId, googleEventId, StringComparison.Ordinal);

    private static string? ResolveCompletedTaskGoogleColorId(TaskJournalItem task, CalendarSyncSettingsDto settings)
        => string.Equals(settings.CompletedTaskCalendarAction, "color", StringComparison.Ordinal) &&
           string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase)
            ? CalendarSyncProviderRegistry.NormalizeGoogleColorId(settings.CompletedTaskGoogleColorId)
            : null;

    private static DateTime? BuildManualFullSyncAvailableAt(DateTime? lastFullSyncAt, int cooldownMinutes)
        => lastFullSyncAt.HasValue
            ? EnsureUtc(lastFullSyncAt.Value).AddMinutes(Math.Max(0, cooldownMinutes))
            : null;

    private static bool IsGoogleRetryableResponse(GoogleHttpResponse response)
    {
        if (response.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden || string.IsNullOrWhiteSpace(response.Payload))
        {
            return false;
        }

        return response.Payload.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || response.Payload.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ResolveGoogleRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay <= TimeSpan.Zero
                ? TimeSpan.Zero
                : delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay;
        }

        return TimeSpan.FromMilliseconds(250 * (attempt + 1));
    }

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

        _db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog
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
            var connection = await _db.GoogleCalendarConnections
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
            var calendar = await _db.GoogleCalendarSyncCalendars
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
        GoogleCalendarConnection connection,
        DataVisibilityAccess? visibility)
        => string.Equals(connection.CalendarMode, "adminByAssignee", StringComparison.Ordinal)
            && visibility is not null
            && (visibility.SeesAll || visibility.VisibleUserIds.Count > 1);

    private static bool CanSyncTaskForConnection(
        GoogleCalendarConnection connection,
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

    private static DateTime? ResolveSyncFromDate(GoogleCalendarConnection connection)
        => connection.SyncFromDate?.Date;

    private static bool IsBeforeSyncFromDate(GoogleCalendarConnection connection, TaskJournalItem task)
    {
        var syncFromDate = ResolveSyncFromDate(connection);
        return syncFromDate.HasValue && task.DueAt < syncFromDate.Value;
    }

    private async Task EnsureUserCanManageCalendarAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (await _db.IsUserAdminOrLegacyFallbackAsync(request.HttpContext.User, cancellationToken))
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

    private async Task<bool> IsGoogleCalendarProviderEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        return settings.GoogleCalendarSyncEnabled == true;
    }

    private static void MarkGoogleLinkDeleted(GoogleCalendarTaskLink link)
    {
        link.SyncStatus = "deleted";
        link.LastDirection = "crm-to-google";
        link.LastSyncedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;
        link.LastError = null;
    }

    private async Task<GoogleSettings> ResolveGoogleSettingsAsync(CancellationToken cancellationToken)
    {
        var enabled = await GetBooleanSettingOrConfigAsync(GoogleEnabledKey, "Auth:Google:Enabled", false, cancellationToken);
        var clientId = await GetSettingOrConfigAsync(GoogleClientIdKey, "Auth:Google:ClientId", cancellationToken);
        var clientSecret = await GetSettingOrConfigAsync(GoogleClientSecretKey, "Auth:Google:ClientSecret", cancellationToken);
        return new GoogleSettings(enabled, clientId, clientSecret);
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
        if (!request.Headers.TryGetValue(headerName, out StringValues values))
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
        var title = success ? "Google Calendar подключён" : "Ошибка подключения Google Calendar";
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

    private static DateTime? ReadGoogleDate(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var dateRoot) || dateRoot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var raw = ReadString(dateRoot, "dateTime") ?? ReadString(dateRoot, "date");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTime.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static int? ReadPayPlannerTaskId(JsonElement root)
    {
        var raw = ReadString(root, "extendedProperties", "private", "payPlannerTaskId");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
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

    private static string BuildConnectionSettingsDetails(GoogleCalendarConnection connection)
    {
        var parts = new List<string>
        {
            $"Подключение: {(connection.IsEnabled ? "включено" : "выключено")}",
            $"Синхронизация: {(connection.SyncEnabled ? "включена" : "выключена")}",
            $"Режим календарей: {FormatCalendarMode(connection.CalendarMode)}",
            $"Правило конфликтов: {FormatGoogleConflictMode(connection.ConflictResolutionMode)}",
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

    private static string FormatGoogleConflictMode(string? mode)
        => mode switch
        {
            "crmWins" => "CRM главная",
            "googleWins" => "Google Calendar главный",
            "latestWins" => "последнее изменение главное",
            _ => "последнее изменение главное",
        };

    private static string? ResolveStatusFromGoogleSummary(string? summary)
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

    private sealed record GoogleSettings(bool Enabled, string? ClientId, string? ClientSecret)
    {
        public bool Ready => Enabled && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    }

    private sealed record GoogleTokenResponse(string AccessToken, string? RefreshToken, string? Scope, int? ExpiresInSeconds);
    private sealed record GoogleProfile(string Sub, string? Email, string? Name);
    private sealed record GoogleCalendarCreateResult(string Id);
    private sealed record GoogleEventWriteResult(string Id, string? Etag);
    private sealed record GoogleHttpResponse(HttpStatusCode StatusCode, string? ReasonPhrase, string Payload)
    {
        public bool IsSuccessStatusCode => (int)StatusCode >= 200 && (int)StatusCode <= 299;
    }

    private sealed record GoogleRequestSnapshot(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        string? Body,
        string? MediaType)
    {
        public static async Task<GoogleRequestSnapshot> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new GoogleRequestSnapshot(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                body,
                request.Content?.Headers.ContentType?.MediaType);
        }

        public HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(Method, RequestUri);
            request.Headers.Authorization = Authorization;
            if (Body is not null)
            {
                request.Content = new StringContent(Body, Encoding.UTF8, MediaType ?? "application/json");
            }

            return request;
        }
    }

    private sealed record GoogleEventsResponse(IReadOnlyList<GoogleEventItem> Events, string? NextSyncToken, bool ResetRequired);
    private sealed record GoogleEventItem(string Id, string? Etag, string? Status, string? Summary, DateTime? StartAt, DateTime? EndAt, int? PayPlannerTaskId);
    private sealed record ConnectionSyncResult(int ProcessedTasks, int ImportedEvents, int ErrorCount);

    private sealed class GoogleEventPayload
    {
        public string Summary { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public GoogleEventDateTime Start { get; set; } = new();
        public GoogleEventDateTime End { get; set; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ColorId { get; set; }
        public GoogleEventExtendedProperties ExtendedProperties { get; set; } = new();
    }

    private sealed class GoogleEventDateTime
    {
        public string DateTime { get; set; } = string.Empty;
        public string TimeZone { get; set; } = "Asia/Novosibirsk";
    }

    private sealed class GoogleEventExtendedProperties
    {
        public Dictionary<string, string> Private { get; set; } = new(StringComparer.Ordinal);
    }
}
