using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class GoogleCalendarSyncService : IGoogleCalendarSyncService
{
    private const string GoogleEnabledKey = "auth.google.enabled";
    private const string GoogleClientIdKey = "auth.google.clientId";
    private const string GoogleClientSecretKey = "auth.google.clientSecret";
    private const string CalendarScope = "https://www.googleapis.com/auth/calendar";
    private const string GoogleAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GoogleUserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    private const string GoogleApiBaseUrl = "https://www.googleapis.com/calendar/v3";
    private const int DefaultPollIntervalMs = 2000;
    private static readonly TimeSpan AuthRequestLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _tokenProtector;
    private readonly ILogger<GoogleCalendarSyncService> _logger;

    public GoogleCalendarSyncService(
        PaymentContext db,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<GoogleCalendarSyncService> logger)
    {
        _db = db;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1");
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

    public async Task<GoogleCalendarStatusDto> UpdateSettingsAsync(
        int userId,
        UpdateGoogleCalendarSettingsRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var connection = await _db.GoogleCalendarConnections.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is null)
        {
            throw new KeyNotFoundException("Подключение Google Calendar не найдено.");
        }

        connection.IsEnabled = request.IsEnabled ?? connection.IsEnabled;
        connection.SyncEnabled = request.SyncEnabled ?? connection.SyncEnabled;
        connection.CalendarMode = NormalizeKnownValue(request.CalendarMode, connection.CalendarMode, ["personal", "adminByAssignee"]);
        connection.ConflictResolutionMode = NormalizeKnownValue(request.ConflictResolutionMode, connection.ConflictResolutionMode, ["crmWins", "googleWins", "latestWins"]);
        connection.UpdatedAt = DateTime.UtcNow;
        await AddLogAsync(connection.Id, null, null, "system", "settings-updated", "success", "Настройки Google Calendar сохранены.", null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return await GetStatusAsync(userId, httpRequest, cancellationToken);
    }

    public async Task<GoogleCalendarStatusDto> DisconnectAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await _db.GoogleCalendarConnections
            .Include(item => item.Calendars)
            .Include(item => item.TaskLinks)
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (connection is not null)
        {
            _db.GoogleCalendarConnections.Remove(connection);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await GetStatusAsync(userId, request, cancellationToken);
    }

    public async Task<GoogleCalendarSyncNowResultDto> SyncNowAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
    {
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
            importedEvents += await ImportGoogleChangesAsync(connection, forceFull, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors++;
            connection.LastError = Trim(ex.Message, 2000);
            await AddLogAsync(connection.Id, null, null, "google-to-crm", "import", "error", "Не удалось загрузить изменения из Google Calendar.", ex.Message, cancellationToken);
        }

        var tasks = await LoadTasksForConnectionAsync(connection, cancellationToken);
        var processedTasks = 0;
        foreach (var task in tasks)
        {
            try
            {
                await UpsertGoogleEventForTaskAsync(connection, task, cancellationToken);
                processedTasks++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

        await UpsertGoogleEventForTaskAsync(connection, task, cancellationToken);
        connection.LastIncrementalSyncAt = DateTime.UtcNow;
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
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
                if (await ApplyGoogleEventAsync(connection, calendar, googleEvent, cancellationToken))
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
        CancellationToken cancellationToken)
    {
        var taskId = googleEvent.PayPlannerTaskId;
        GoogleCalendarTaskLink? link = null;
        if (taskId.HasValue)
        {
            link = await _db.GoogleCalendarTaskLinks
                .Include(item => item.TaskJournalItem)
                .FirstOrDefaultAsync(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == taskId.Value, cancellationToken);
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
        if (googleEvent.StartAt.HasValue && task.DueAt != googleEvent.StartAt.Value)
        {
            task.DueAt = googleEvent.StartAt.Value;
            changed = true;
        }

        if (googleEvent.StartAt.HasValue && googleEvent.EndAt.HasValue)
        {
            var minutes = Math.Max(15, (int)Math.Round((googleEvent.EndAt.Value - googleEvent.StartAt.Value).TotalMinutes));
            if (task.PlannedDurationMinutes != minutes)
            {
                task.PlannedDurationMinutes = minutes;
                changed = true;
            }
        }

        var importedStatus = ResolveStatusFromGoogleSummary(googleEvent.Summary);
        if (!string.IsNullOrWhiteSpace(importedStatus) && task.Status != importedStatus)
        {
            task.Status = importedStatus;
            changed = true;
        }

        var importedTitle = StripStatusPrefix(googleEvent.Summary);
        if (!string.IsNullOrWhiteSpace(importedTitle) && !string.Equals(task.Title, importedTitle, StringComparison.Ordinal))
        {
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
        link.LastCrmHash = BuildCrmHash(task);
        link.LastDirection = "google-to-crm";
        link.LastSyncedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;
        await AddLogAsync(connection.Id, calendar.Id, task.Id, "google-to-crm", "update-task", "success", "Изменения из Google Calendar применены к задаче.", null, cancellationToken);
        return true;
    }

    private async Task UpsertGoogleEventForTaskAsync(GoogleCalendarConnection connection, TaskJournalItem task, CancellationToken cancellationToken)
    {
        var accessToken = await ResolveAccessTokenAsync(connection, cancellationToken);
        var calendar = await EnsureCalendarForTaskAsync(connection, task, accessToken, cancellationToken);
        var crmHash = BuildCrmHash(task);
        var link = await _db.GoogleCalendarTaskLinks
            .FirstOrDefaultAsync(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == task.Id, cancellationToken);

        if (link is not null
            && string.Equals(link.LastCrmHash, crmHash, StringComparison.Ordinal)
            && string.Equals(link.SyncStatus, "active", StringComparison.Ordinal))
        {
            return;
        }

        var payload = BuildGoogleEventPayload(connection, task, crmHash);
        GoogleEventWriteResult result;
        if (link is null || string.IsNullOrWhiteSpace(link.GoogleEventId) || link.GoogleCalendarId != calendar.GoogleCalendarId || link.SyncStatus == "detached")
        {
            result = await CreateGoogleEventAsync(accessToken, calendar.GoogleCalendarId, payload, cancellationToken);
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
            await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "create-event", "success", "Задача создана в Google Calendar.", null, cancellationToken);
        }
        else
        {
            result = await UpdateGoogleEventAsync(accessToken, link.GoogleCalendarId, link.GoogleEventId, payload, cancellationToken);
            await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "update-event", "success", "Задача обновлена в Google Calendar.", null, cancellationToken);
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
    }

    private async Task<GoogleCalendarSyncCalendar> EnsureCalendarForTaskAsync(
        GoogleCalendarConnection connection,
        TaskJournalItem task,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var isAdmin = await IsAdminConnectionAsync(connection.UserId, cancellationToken);
        var ownerUserId = isAdmin && connection.CalendarMode == "adminByAssignee" ? task.AssigneeUserId : null;
        var name = ownerUserId.HasValue
            ? $"PayPlanner - {NormalizeOptional(task.AssigneeName) ?? $"сотрудник #{ownerUserId.Value}"}"
            : "PayPlanner задачи";

        var calendar = connection.Calendars.FirstOrDefault(item => item.OwnerUserId == ownerUserId);
        if (calendar is not null)
        {
            return calendar;
        }

        var created = await CreateGoogleCalendarAsync(accessToken, name, cancellationToken);
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
        await _db.SaveChangesAsync(cancellationToken);
        await AddLogAsync(connection.Id, calendar.Id, null, "system", "create-calendar", "success", $"В Google Calendar создан календарь «{name}».", null, cancellationToken);
        return calendar;
    }

    private async Task<IReadOnlyList<TaskJournalItem>> LoadTasksForConnectionAsync(GoogleCalendarConnection connection, CancellationToken cancellationToken)
    {
        var isAdmin = await IsAdminConnectionAsync(connection.UserId, cancellationToken);
        var now = DateTime.UtcNow;
        var from = now.AddDays(-30);
        var to = now.AddDays(365);
        var query = _db.TaskJournalItems
            .Include(item => item.Participants)
            .Where(item => item.DueAt >= from && item.DueAt <= to);

        if (!isAdmin)
        {
            query = query.Where(item =>
                item.AssigneeUserId == connection.UserId ||
                item.Participants.Any(participant => participant.UserId == connection.UserId));
        }

        return await query
            .OrderBy(item => item.DueAt)
            .Take(500)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<GoogleCalendarConnection> LoadConnectionQuery()
        => _db.GoogleCalendarConnections
            .Include(item => item.User)
            .ThenInclude(item => item.Role)
            .Include(item => item.Calendars)
            .ThenInclude(item => item.OwnerUser);

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

        return new GoogleCalendarStatusDto
        {
            GoogleOAuthReady = (await ResolveGoogleSettingsAsync(cancellationToken)).Ready,
            CallbackUrl = BuildAbsoluteUrl(request, "/api/google-calendar/auth/callback"),
            Connection = connection is null ? null : MapConnection(connection),
            RecentLogs = logs,
        };
    }

    private static GoogleCalendarConnectionDto MapConnection(GoogleCalendarConnection connection)
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
                CalendarMode = await IsAdminConnectionAsync(userId, cancellationToken) ? "adminByAssignee" : "personal",
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

    private async Task<GoogleCalendarCreateResult> CreateGoogleCalendarAsync(string accessToken, string name, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { summary = name, timeZone = "Asia/Novosibirsk" }, JsonOptions);
        using var request = BuildGoogleRequest(HttpMethod.Post, $"{GoogleApiBaseUrl}/calendars", accessToken, payload);
        var document = await SendGoogleJsonAsync(request, cancellationToken);
        return new GoogleCalendarCreateResult(ReadString(document.RootElement, "id") ?? throw new InvalidOperationException("Google не вернул id календаря."));
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
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode == 410)
        {
            return new GoogleEventsResponse([], null, ResetRequired: true);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Google не вернул список событий.");
        }

        using var document = JsonDocument.Parse(payload);
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
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadProviderError(payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку.");
        }

        return JsonDocument.Parse(payload);
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

    private static GoogleEventPayload BuildGoogleEventPayload(GoogleCalendarConnection connection, TaskJournalItem task, string crmHash)
    {
        var start = EnsureUtc(task.DueAt);
        var end = start.AddMinutes(Math.Max(15, task.PlannedDurationMinutes));
        return new GoogleEventPayload
        {
            Summary = $"[{StatusLabel(task.Status)}] {task.Title}",
            Description = BuildEventDescription(task),
            Start = new GoogleEventDateTime { DateTime = start.ToString("O", CultureInfo.InvariantCulture), TimeZone = "Asia/Novosibirsk" },
            End = new GoogleEventDateTime { DateTime = end.ToString("O", CultureInfo.InvariantCulture), TimeZone = "Asia/Novosibirsk" },
            ExtendedProperties = new GoogleEventExtendedProperties
            {
                Private = new Dictionary<string, string>
                {
                    ["payPlannerTaskId"] = task.Id.ToString(CultureInfo.InvariantCulture),
                    ["payPlannerConnectionId"] = connection.Id.ToString(CultureInfo.InvariantCulture),
                    ["payPlannerHash"] = crmHash,
                },
            },
        };
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

    private static string BuildCrmHash(TaskJournalItem task)
        => Sha256($"{task.Id}|{task.Title}|{task.Description}|{task.Result}|{task.Status}|{task.Priority}|{EnsureUtc(task.DueAt):O}|{task.PlannedDurationMinutes}|{task.AssigneeUserId}|{task.AssigneeName}");

    private static string BuildGoogleHash(GoogleEventItem item)
        => Sha256($"{item.Id}|{item.Summary}|{item.Status}|{item.StartAt:O}|{item.EndAt:O}");

    private static string BuildGoogleHashFromPayload(GoogleEventPayload payload, string eventId)
        => Sha256($"{eventId}|{payload.Summary}|confirmed|{payload.Start.DateTime}|{payload.End.DateTime}");

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateTime EnsureUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
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
        _db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog
        {
            ConnectionId = connectionId,
            CalendarSyncCalendarId = calendarId,
            TaskJournalItemId = taskId,
            Direction = direction,
            Operation = operation,
            Status = status,
            Message = Trim(message, 1000),
            DetailsJson = string.IsNullOrWhiteSpace(details) ? null : JsonSerializer.Serialize(new { detail = Trim(details, 4000) }, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        });

        await Task.CompletedTask.WaitAsync(cancellationToken);
    }

    private async Task<bool> IsAdminConnectionAsync(int userId, CancellationToken cancellationToken)
        => await _db.Users
            .AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => item.Role != null && item.Role.Name == "admin")
            .FirstOrDefaultAsync(cancellationToken);

    private static bool TaskBelongsToUser(TaskJournalItem task, int userId)
        => task.AssigneeUserId == userId || task.Participants.Any(item => item.UserId == userId);

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
    private sealed record GoogleEventsResponse(IReadOnlyList<GoogleEventItem> Events, string? NextSyncToken, bool ResetRequired);
    private sealed record GoogleEventItem(string Id, string? Etag, string? Status, string? Summary, DateTime? StartAt, DateTime? EndAt, int? PayPlannerTaskId);
    private sealed record ConnectionSyncResult(int ProcessedTasks, int ImportedEvents, int ErrorCount);

    private sealed class GoogleEventPayload
    {
        public string Summary { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public GoogleEventDateTime Start { get; set; } = new();
        public GoogleEventDateTime End { get; set; } = new();
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
