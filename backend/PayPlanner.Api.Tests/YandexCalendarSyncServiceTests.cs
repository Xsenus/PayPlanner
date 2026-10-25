using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class YandexCalendarSyncServiceTests
{
    // Token fixtures and their services share this process; parallel workers must not share a disk key ring.
    private static readonly IDataProtectionProvider TestDataProtectionProvider = new EphemeralDataProtectionProvider();

    [Fact]
    public async Task HandleCallbackAsync_WhenStateMissingDoesNotCreateAuthRequest()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var html = await service.HandleCallbackAsync(
            state: null,
            code: "code",
            error: null,
            errorDescription: null,
            CreateHttpRequest(0),
            CancellationToken.None);

        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.YandexCalendarAuthRequests.AnyAsync());
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenStateUnknownDoesNotCreateAuthRequest()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var html = await service.HandleCallbackAsync(
            state: "unknown-state",
            code: "code",
            error: null,
            errorDescription: null,
            CreateHttpRequest(0),
            CancellationToken.None);

        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.YandexCalendarAuthRequests.AnyAsync());
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenStateExpiredMarksRequestExpired()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "expired-yandex-callback@example.com", "user");
        db.YandexCalendarAuthRequests.Add(new YandexCalendarAuthRequest
        {
            UserId = user.Id,
            State = "expired-state",
            ReturnUrl = "/?ppTab=profile",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var html = await service.HandleCallbackAsync(
            state: "expired-state",
            code: "code",
            error: null,
            errorDescription: null,
            CreateHttpRequest(user.Id),
            CancellationToken.None);

        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        var authRequest = await db.YandexCalendarAuthRequests.SingleAsync();
        Assert.Equal("expired", authRequest.Status);
        Assert.NotNull(authRequest.Error);
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenStateCompletedDoesNotRepeatProviderRequests()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "completed-yandex-callback@example.com", "user");
        db.YandexCalendarAuthRequests.Add(new YandexCalendarAuthRequest
        {
            UserId = user.Id,
            State = "completed-state",
            Status = "completed",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var html = await service.HandleCallbackAsync(
            "completed-state", "replayed-code", null, null, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Contains("уже подключён", html);
        Assert.Equal("completed", (await db.YandexCalendarAuthRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task StartAuthAsync_WhenProviderDisabledRejectsConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "disabled-yandex@example.com", "user");
        var now = DateTime.UtcNow;
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "auth.yandex.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.yandex.clientId", Value = "client-id", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.yandex.clientSecret", Value = "client-secret", CreatedAt = now, UpdatedAt = now },
            new SystemSetting
            {
                Key = "calendarSync.settings",
                Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
                {
                    GoogleCalendarSyncEnabled = false,
                    YandexCalendarSyncEnabled = false,
                }),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAuthAsync(
                user.Id,
                new StartYandexCalendarAuthRequest { ReturnUrl = "/?ppTab=profile" },
                CreateHttpRequest(user.Id),
                CancellationToken.None));

        Assert.Contains("Яндекс Календарь отключен администратором", error.Message);
        var status = await service.GetStatusAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);
        Assert.False(status.IsAvailable);
        Assert.True(status.YandexOAuthReady);
    }

    [Fact]
    public async Task DisconnectAsync_RemovesExternalYandexCalendarsBeforeConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-disconnect@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Отключить Яндекс.Календарь");
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-disconnect", "PayPlanner задачи");
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "event-yandex-disconnect",
            YandexEventUrl = $"{calendar.CalendarUrl}event-yandex-disconnect.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var status = await service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Null(status.Connection);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsoluteUri == "https://caldav.yandex.ru/calendars/user/payplanner/");
        Assert.False(await db.YandexCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.False(await db.YandexCalendarSyncCalendars.AnyAsync(item => item.YandexCalendarId == "calendar-yandex-disconnect"));
        Assert.Contains(await db.YandexCalendarSyncLogs.ToListAsync(), item =>
            item.Operation == "delete-calendar" &&
            item.Status == "success");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DisconnectAsync_WhenYandexCleanupFails_KeepsConnection(HttpStatusCode statusCode)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-disconnect-error@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-yandex-disconnect-error", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler(statusCode)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None));

        Assert.Contains("Не удалось отключить Яндекс.Календарь", error.Message);
        Assert.True(await db.YandexCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.True(await db.YandexCalendarSyncCalendars.AnyAsync(item => item.YandexCalendarId == "calendar-yandex-disconnect-error"));
        Assert.Contains(await db.YandexCalendarSyncLogs.ToListAsync(), item =>
            item.ConnectionId == connection.Id &&
            item.Operation == "delete-calendar" &&
            item.Status == "error");
    }

    [Fact]
    public async Task DisconnectAsync_WhenYandexCalendarAlreadyMissing_RemovesLocalConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-disconnect-missing@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-yandex-disconnect-missing", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler(HttpStatusCode.NotFound)));

        var status = await service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Null(status.Connection);
        Assert.False(await db.YandexCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.Contains(await db.YandexCalendarSyncLogs.ToListAsync(), item =>
            item.Operation == "delete-calendar-missing" &&
            item.Status == "success");
    }

    [Fact]
    public async Task DisconnectAsync_WhenYandexCleanupTimesOut_KeepsConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-disconnect-timeout@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-yandex-disconnect-timeout", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new ThrowingYandexHandler(_ =>
            new TaskCanceledException("Yandex Calendar timeout."))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None));

        Assert.Contains("Не удалось отключить Яндекс.Календарь", error.Message);
        Assert.True(await db.YandexCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.True(await db.YandexCalendarSyncCalendars.AnyAsync(item => item.YandexCalendarId == "calendar-yandex-disconnect-timeout"));
        Assert.Contains(await db.YandexCalendarSyncLogs.ToListAsync(), item =>
            item.ConnectionId == connection.Id &&
            item.Operation == "delete-calendar" &&
            item.Status == "error");
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_WhenTaskBeforeSyncFromDateDeletesExistingYandexEvent()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-sync-from-delete@example.com", "user");
        var syncFromDate = DateTime.UtcNow.Date.AddDays(1);
        var connection = await CreateConnectionAsync(db, user.Id, syncFromDate);
        var task = await CreateTaskAsync(db, user.Id, "Убрать из Яндекс.Календаря", syncFromDate.AddDays(-1).AddHours(10));
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-sync-from-delete", "PayPlanner задачи");
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "event-yandex-sync-from-delete",
            YandexEventUrl = $"{calendar.CalendarUrl}event-yandex-sync-from-delete.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsoluteUri == "https://caldav.yandex.ru/calendars/user/payplanner/event-yandex-sync-from-delete.ics");
        var link = await db.YandexCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("deleted", link.SyncStatus);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_TreatsUnspecifiedTaskDueAtAsUtcInstantWhenWritingYandexEvent()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-unspecified@example.com", "user");
        await SetUserTimeZoneAsync(db, user.Id, "Europe/Samara");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-yandex-unspecified", "PayPlanner задачи");
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "Дата без Kind не должна сдвигаться",
            new DateTime(2026, 6, 5, 9, 0, 0, DateTimeKind.Unspecified));
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        var putRequest = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        var body = await putRequest.Content!.ReadAsStringAsync();
        Assert.Contains("DTSTART:20260605T090000Z", body);
        Assert.Contains("DTEND:20260605T100000Z", body);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_ExportsOnlyExplicitlyVisibleTaskComments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "comments-yandex@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-yandex-comments", "PayPlanner задачи");
        var task = await CreateTaskAsync(db, user.Id, "Комментарии Яндекс", DateTime.UtcNow.AddDays(1));
        db.TaskJournalComments.AddRange(
            new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                AuthorUserId = user.Id,
                AuthorName = user.FullName,
                Kind = "comment",
                Text = "Разрешённый комментарий Яндекс",
                IsExternalCalendarVisible = true,
                CreatedAt = DateTime.UtcNow,
            },
            new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                AuthorUserId = user.Id,
                AuthorName = user.FullName,
                Kind = "comment",
                Text = "Внутренний комментарий Яндекс",
                CreatedAt = DateTime.UtcNow.AddSeconds(1),
            });
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        var request = handler.Requests.Single(item => item.Method == HttpMethod.Put);
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("Разрешённый комментарий Яндекс", body);
        Assert.DoesNotContain("Внутренний комментарий Яндекс", body);
        Assert.Contains("PAYPLANNER-COMMENTS-V1", body);
    }

    [Fact]
    public async Task SyncNowAsync_UsesAssigneeTimeZoneForAdminCalendarNearMidnight()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-yandex-timezone@example.com", "admin");
        var assignee = await CreateUserAsync(db, "assignee-yandex-timezone@example.com", "user");
        await SetUserTimeZoneAsync(db, admin.Id, "Europe/Moscow");
        await SetUserTimeZoneAsync(db, assignee.Id, "Asia/Kamchatka");
        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var assigneeTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Asia/Kamchatka");
        var localStart = CalendarTimeZoneHelper
            .ToUserLocal(DateTime.UtcNow.AddDays(1), assigneeTimeZone)
            .Date
            .AddHours(23)
            .AddMinutes(30);
        var dueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, assigneeTimeZone);
        var task = await CreateTaskAsync(
            db,
            assignee.Id,
            "Near midnight in assignee timezone",
            dueAt);
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        var calendar = await db.YandexCalendarSyncCalendars.SingleAsync(item => item.ConnectionId == connection.Id);
        Assert.Equal(assignee.Id, calendar.OwnerUserId);
        var createCalendarRequest = handler.Requests.Single(request =>
            string.Equals(request.Method.Method, "MKCALENDAR", StringComparison.Ordinal));
        var createCalendarBody = await createCalendarRequest.Content!.ReadAsStringAsync();
        Assert.Contains("<C:calendar-timezone>", createCalendarBody);
        Assert.Contains("TZID:Asia/Kamchatka", WebUtility.HtmlDecode(createCalendarBody));
        var putRequest = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        var body = await putRequest.Content!.ReadAsStringAsync();
        Assert.Contains($"DTSTART:{CalendarTimeZoneHelper.FormatUtcIcsDateTime(dueAt, assigneeTimeZone)}", body);
        Assert.Contains($"DTEND:{CalendarTimeZoneHelper.FormatUtcIcsDateTime(dueAt.AddHours(1), assigneeTimeZone)}", body);
        Assert.Contains("X-PAYPLANNER-TIMEZONE:Asia/Kamchatka", body);
    }

    [Fact]
    public async Task SyncNowAsync_UsesDiscoveredYandexCalendarUrlAfterCreate()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin-yandex-discovered-url@example.com", "admin");
        var assignee = await CreateUserAsync(db, "assignee-yandex-discovered-url@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id, calendarMode: "adminByAssignee");
        var task = await CreateTaskAsync(db, assignee.Id, "Use discovered Yandex calendar URL");
        var handler = new RecordingYandexHandler(calendarHomeResponse: BuildYandexCalendarHomeResponse(
            "/calendars/yandex%40example.com/events-123456/",
            "PayPlanner - User"));
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id, "admin"), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        var calendar = await db.YandexCalendarSyncCalendars.SingleAsync(item => item.ConnectionId == connection.Id);
        Assert.Equal("events-123456", calendar.YandexCalendarId);
        Assert.Equal("https://caldav.yandex.ru/calendars/yandex%40example.com/events-123456/", calendar.CalendarUrl);
        var putRequest = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        Assert.StartsWith(
            "https://caldav.yandex.ru/calendars/yandex%40example.com/events-123456/payplanner-task-",
            putRequest.RequestUri!.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncDueConnectionsAsync_WhenYandexReportTemporarilyTimesOut_RetriesWithoutSyncError()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "yandex-transient-report@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-yandex-transient", "PayPlanner задачи");
        var handler = new TransientReportYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(1, result.ProcessedConnections);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(2, handler.ReportRequestCount);
        Assert.Null((await db.YandexCalendarConnections.SingleAsync()).LastError);
        Assert.DoesNotContain(await db.YandexCalendarSyncLogs.ToListAsync(), item => item.Status == "error");
    }

    [Fact]
    public async Task SyncDueConnectionsAsync_WhenCalendarListTemporarilyTimesOut_DiscoversExistingCalendarWithoutDuplicate()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "yandex-transient-calendar-list@example.com", "user");
        await CreateConnectionAsync(db, user.Id);
        await CreateTaskAsync(db, user.Id, "Не создавать дубль календаря");
        var handler = new TransientCalendarHomeYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(2, handler.CalendarListRequestCount);
        Assert.Equal(0, handler.CreateCalendarRequestCount);
        Assert.Equal("existing-calendar", (await db.YandexCalendarSyncCalendars.SingleAsync()).YandexCalendarId);
    }

    [Fact]
    public async Task SyncNowAsync_AdminConnectionCreatesCalendarForActiveApprovedUserWithoutTasks()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-yandex-empty-calendar@example.com", "admin");
        admin.FullName = "Администратор";
        var assignee = await CreateUserAsync(db, "lawyer-yandex-empty-calendar@example.com", "user");
        assignee.FullName = "Кузьминова Наталья Игоревна";
        await db.SaveChangesAsync();
        await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler()));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(0, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Contains(await db.YandexCalendarSyncCalendars.ToListAsync(), item =>
            item.OwnerUserId == assignee.Id &&
            item.Name.Contains("Кузьминова Наталья Игоревна", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncNowAsync_NonAdminAssigneeModeUsesSpecificCalendarVisibility()
    {
        await using var db = await CreateContextAsync();
        var secretary = await CreateUserAsync(db, "secretary-yandex-specific@example.com", "secretary");
        secretary.FullName = "Секретарь";
        var selected = await CreateUserAsync(db, "selected-yandex-specific@example.com", "selected-user");
        selected.FullName = "Выбранный сотрудник";
        var hidden = await CreateUserAsync(db, "hidden-yandex-specific@example.com", "hidden-user");
        hidden.FullName = "Скрытый сотрудник";
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = secretary.RoleId,
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            ViewScope = DataVisibilityScope.Specific,
        });
        db.RoleCalendarVisibleUsers.Add(new RoleCalendarVisibleUser
        {
            RoleId = secretary.RoleId,
            UserId = selected.Id,
        });
        await db.SaveChangesAsync();

        var connection = await CreateConnectionAsync(db, secretary.Id, calendarMode: "adminByAssignee");
        await CreateTaskAsync(db, selected.Id, "Задача выбранного сотрудника");
        var hiddenTask = await CreateTaskAsync(db, hidden.Id, "Задача скрытого сотрудника");
        hiddenTask.Participants.Add(new TaskJournalParticipant
        {
            UserId = selected.Id,
            Role = "observer",
            DisplayName = selected.FullName,
            Email = selected.Email,
        });
        var revokedCalendar = await CreateCalendarAsync(
            db,
            connection.Id,
            "calendar-yandex-hidden-specific",
            "PayPlanner - Hidden employee",
            hidden.Id,
            "https://caldav.yandex.ru/calendars/user/calendar-yandex-hidden-specific/");
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = revokedCalendar.Id,
            TaskJournalItemId = hiddenTask.Id,
            YandexCalendarId = revokedCalendar.YandexCalendarId,
            YandexEventId = "event-yandex-hidden-specific",
            YandexEventUrl = $"{revokedCalendar.CalendarUrl}event-yandex-hidden-specific.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(secretary.Id, CreateHttpRequest(secretary.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        var calendars = await db.YandexCalendarSyncCalendars
            .Where(item => item.OwnerUserId != null)
            .ToListAsync();
        Assert.Contains(calendars, item => item.OwnerUserId == secretary.Id);
        Assert.Contains(calendars, item => item.OwnerUserId == selected.Id);
        Assert.DoesNotContain(calendars, item => item.OwnerUserId == hidden.Id);
        Assert.False(await db.YandexCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == hiddenTask.Id));
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsoluteUri == "https://caldav.yandex.ru/calendars/user/calendar-yandex-hidden-specific/");
    }

    [Fact]
    public async Task SyncNowAsync_ReusesExistingYandexCalendarByName()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin-yandex-reuse-calendar@example.com", "admin");
        var assignee = await CreateUserAsync(db, "assignee-yandex-reuse-calendar@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id, calendarMode: "adminByAssignee");
        await CreateTaskAsync(db, assignee.Id, "Reuse existing Yandex calendar");
        var handler = new RecordingYandexHandler(calendarHomeResponse: BuildYandexCalendarHomeResponse(
            "/calendars/yandex%40example.com/events-654321/",
            "PayPlanner - User"));
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id, "admin"), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, request =>
            string.Equals(request.Method.Method, "MKCALENDAR", StringComparison.Ordinal));
        var calendar = await db.YandexCalendarSyncCalendars.SingleAsync(item => item.ConnectionId == connection.Id);
        Assert.Equal("events-654321", calendar.YandexCalendarId);
        Assert.Equal("https://caldav.yandex.ru/calendars/yandex%40example.com/events-654321/", calendar.CalendarUrl);
    }

    [Fact]
    public async Task SyncNowAsync_ReusesExistingYandexCalendarWhenComponentSetIsMissing()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin-yandex-calendar-no-components@example.com", "admin");
        var assignee = await CreateUserAsync(db, "assignee-yandex-calendar-no-components@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id, calendarMode: "adminByAssignee");
        await CreateTaskAsync(db, assignee.Id, "Reuse existing Yandex calendar without component set");
        var handler = new RecordingYandexHandler(calendarHomeResponse: BuildYandexCalendarHomeResponse(
            "/calendars/yandex%40example.com/events-987654/",
            "PayPlanner - User",
            includeSupportedComponents: false));
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id, "admin"), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.DoesNotContain(handler.Requests, request =>
            string.Equals(request.Method.Method, "MKCALENDAR", StringComparison.Ordinal));
        var calendar = await db.YandexCalendarSyncCalendars.SingleAsync(item => item.ConnectionId == connection.Id);
        Assert.Equal("events-987654", calendar.YandexCalendarId);
        Assert.Equal("https://caldav.yandex.ru/calendars/yandex%40example.com/events-987654/", calendar.CalendarUrl);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_RefreshesStoredYandexCalendarUrl()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-refresh-url@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "payplanner-tasks", "PayPlanner задачи");
        var task = await CreateTaskAsync(db, user.Id, "Refresh stored Yandex calendar URL");
        var handler = new RecordingYandexHandler(calendarHomeResponse: BuildYandexCalendarHomeResponse(
            "/calendars/yandex%40example.com/events-777/",
            calendar.Name));
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Equal("events-777", calendar.YandexCalendarId);
        Assert.Equal("https://caldav.yandex.ru/calendars/yandex%40example.com/events-777/", calendar.CalendarUrl);
        var putRequest = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        Assert.StartsWith(
            "https://caldav.yandex.ru/calendars/yandex%40example.com/events-777/payplanner-task-",
            putRequest.RequestUri!.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncNowAsync_RefreshesSharedYandexCalendarUrlOnlyOncePerRun()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-refresh-once@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "payplanner-tasks", "PayPlanner задачи");
        await CreateTaskAsync(db, user.Id, "Первая задача общего календаря");
        await CreateTaskAsync(db, user.Id, "Вторая задача общего календаря");
        var handler = new RecordingYandexHandler(calendarHomeResponse: BuildYandexCalendarHomeResponse(
            "/calendars/yandex%40example.com/events-shared/",
            calendar.Name));
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(2, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Single(handler.Requests, request =>
            string.Equals(request.Method.Method, "PROPFIND", StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Put));
    }

    [Fact]
    public async Task SyncNowAsync_LoadsCalendarHomeOnlyOnceForMultipleExistingCalendars()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-yandex-home-snapshot@example.com", "admin");
        var firstAssignee = await CreateUserAsync(db, "first-yandex-home-snapshot@example.com", "first-user");
        var secondAssignee = await CreateUserAsync(db, "second-yandex-home-snapshot@example.com", "second-user");
        firstAssignee.FullName = "Первый сотрудник";
        secondAssignee.FullName = "Второй сотрудник";
        await db.SaveChangesAsync();
        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        await CreateCalendarAsync(
            db,
            connection.Id,
            "events-first",
            "PayPlanner - Первый сотрудник",
            firstAssignee.Id,
            "https://caldav.yandex.ru/calendars/user/events-first/");
        await CreateCalendarAsync(
            db,
            connection.Id,
            "events-second",
            "PayPlanner - Второй сотрудник",
            secondAssignee.Id,
            "https://caldav.yandex.ru/calendars/user/events-second/");
        await CreateTaskAsync(db, firstAssignee.Id, "Задача первого сотрудника");
        await CreateTaskAsync(db, secondAssignee.Id, "Задача второго сотрудника");
        var handler = new RecordingYandexHandler(calendarHomeResponse: BuildYandexCalendarHomeResponseForCalendars(
            ("/calendars/yandex%40example.com/events-first/", "PayPlanner - Первый сотрудник"),
            ("/calendars/yandex%40example.com/events-second/", "PayPlanner - Второй сотрудник")));
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(2, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Single(handler.Requests, request =>
            string.Equals(request.Method.Method, "PROPFIND", StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Put));
    }

    [Fact]
    public async Task SyncNowAsync_AppliesYandexMoveAndWritesLocalTimeLog()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-move@example.com", "user");
        await SetUserTimeZoneAsync(db, user.Id, "Asia/Kamchatka");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-move", "PayPlanner задачи");
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "Near midnight moved",
            new DateTime(2026, 6, 4, 8, 30, 0, DateTimeKind.Utc));
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "event-yandex-move",
            YandexEventUrl = $"{calendar.CalendarUrl}event-yandex-move.ics",
            SyncStatus = "active",
            LastYandexHash = "old-yandex-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var reportResponse = BuildYandexReportResponse(
            calendar.CalendarUrl,
            "event-yandex-move",
            $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:event-yandex-move
DTSTART:20260604T113000Z
DTEND:20260604T123000Z
SUMMARY:Near midnight moved
X-PAYPLANNER-TASK-ID:{task.Id}
X-PAYPLANNER-HASH:yandex-move-hash
END:VEVENT
END:VCALENDAR
""");
        var handler = new RecordingYandexHandler(reportResponse: reportResponse);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ImportedEvents);
        Assert.Equal(0, result.ErrorCount);
        var updatedTask = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal(new DateTime(2026, 6, 4, 11, 30, 0, DateTimeKind.Utc), updatedTask.DueAt);
        var syncLog = await db.YandexCalendarSyncLogs.SingleAsync(item =>
            item.ConnectionId == connection.Id &&
            item.TaskJournalItemId == task.Id &&
            item.Operation == "update-task" &&
            item.Status == "success");
        Assert.Contains("20:30", syncLog.DetailsJson);
        Assert.Contains("23:30", syncLog.DetailsJson);
    }

    [Fact]
    public async Task SyncNowAsync_DoesNotReopenCompletedTaskFromStaleYandexSummary()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-closed-task@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-closed-task", "PayPlanner задачи");
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "Закрытая задача",
            new DateTime(2026, 6, 5, 8, 0, 0, DateTimeKind.Utc));
        task.Status = "done";
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "event-yandex-closed-task",
            YandexEventUrl = $"{calendar.CalendarUrl}event-yandex-closed-task.ics",
            SyncStatus = "active",
            LastYandexHash = "old-yandex-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var reportResponse = BuildYandexReportResponse(
            calendar.CalendarUrl,
            "event-yandex-closed-task",
            $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:event-yandex-closed-task
DTSTART:20260605T120000Z
DTEND:20260605T130000Z
SUMMARY:[Новая] Закрытая задача
X-PAYPLANNER-TASK-ID:{task.Id}
X-PAYPLANNER-HASH:yandex-closed-task-hash
END:VEVENT
END:VCALENDAR
""");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler(reportResponse: reportResponse)));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ImportedEvents);
        Assert.Equal(0, result.ErrorCount);
        var updatedTask = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("done", updatedTask.Status);
        Assert.Equal(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc), updatedTask.DueAt);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_UpdatesLinkedYandexEventAndWritesLocalTimeLog()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-update-yandex@example.com", "user");
        await SetUserTimeZoneAsync(db, user.Id, "Asia/Kamchatka");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-update", "PayPlanner задачи");
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "Update linked Yandex event",
            new DateTime(2026, 6, 4, 11, 30, 0, DateTimeKind.Utc));
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "event-yandex-update",
            YandexEventUrl = $"{calendar.CalendarUrl}event-yandex-update.ics",
            SyncStatus = "active",
            LastCrmHash = "old-crm-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        var putRequest = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        var body = await putRequest.Content!.ReadAsStringAsync();
        Assert.Contains("DTSTART:20260604T113000Z", body);
        Assert.Contains("X-PAYPLANNER-TIMEZONE:Asia/Kamchatka", body);
        var syncLog = await db.YandexCalendarSyncLogs.SingleAsync(item =>
            item.ConnectionId == connection.Id &&
            item.TaskJournalItemId == task.Id &&
            item.Operation == "update-event" &&
            item.Status == "success");
        Assert.Contains("23:30", syncLog.DetailsJson);
    }

    [Fact]
    public async Task SyncNowAsync_DeletesStaleYandexDuplicateWithoutMovingTask()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-stale-duplicate@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-stale-duplicate", "PayPlanner tasks");
        var taskDueAt = new DateTime(2026, 6, 12, 6, 52, 0, DateTimeKind.Utc);
        var task = await CreateTaskAsync(db, user.Id, "Current Yandex task", taskDueAt);
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "current-yandex-event",
            YandexEventUrl = $"{calendar.CalendarUrl}current-yandex-event.ics",
            SyncStatus = "active",
            LastCrmHash = "old-crm-hash",
            LastYandexHash = "old-yandex-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var reportResponse = BuildYandexReportResponse(
            calendar.CalendarUrl,
            "stale-yandex-event",
            $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:stale-yandex-event
DTSTART:20260612T102000Z
DTEND:20260612T112000Z
SUMMARY:[New] Stale duplicate
X-PAYPLANNER-TASK-ID:{task.Id}
X-PAYPLANNER-HASH:stale-hash
END:VEVENT
END:VCALENDAR
""");
        var handler = new RecordingYandexHandler(reportResponse: reportResponse);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(0, result.ErrorCount);
        var unchangedTask = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal(taskDueAt, unchangedTask.DueAt);
        Assert.DoesNotContain(await db.YandexCalendarSyncLogs.ToListAsync(), item =>
            item.TaskJournalItemId == task.Id &&
            item.Operation == "update-task");
        Assert.Contains(await db.YandexCalendarSyncLogs.ToListAsync(), item =>
            item.TaskJournalItemId == task.Id &&
            item.Operation == "delete-stale-event" &&
            item.Status == "success");
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsoluteUri == $"{calendar.CalendarUrl}stale-yandex-event.ics");
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_DeletesDuplicateYandexEventAfterWrite()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-post-write-duplicate@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-post-write-duplicate", "PayPlanner tasks");
        var task = await CreateTaskAsync(db, user.Id, "Delete duplicate Yandex event after write");
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "current-yandex-event",
            YandexEventUrl = $"{calendar.CalendarUrl}current-yandex-event.ics",
            SyncStatus = "active",
            LastCrmHash = "old-crm-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var reportResponse = BuildYandexReportResponseForEvents(
            (calendar.CalendarUrl, "current-yandex-event", $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:current-yandex-event
DTSTART:20260612T100000Z
DTEND:20260612T110000Z
SUMMARY:[New] Current event
X-PAYPLANNER-TASK-ID:{task.Id}
X-PAYPLANNER-HASH:current-hash
END:VEVENT
END:VCALENDAR
"""),
            (calendar.CalendarUrl, "duplicate-yandex-event", $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:duplicate-yandex-event
DTSTART:20260612T100000Z
DTEND:20260612T110000Z
SUMMARY:[New] Duplicate event
X-PAYPLANNER-TASK-ID:{task.Id}
X-PAYPLANNER-HASH:duplicate-hash
END:VEVENT
END:VCALENDAR
"""));
        var handler = new RecordingYandexHandler(reportResponse: reportResponse);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsoluteUri == $"{calendar.CalendarUrl}duplicate-yandex-event.ics");
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsoluteUri == $"{calendar.CalendarUrl}current-yandex-event.ics");
        Assert.Contains(await db.YandexCalendarSyncLogs.ToListAsync(), item =>
            item.TaskJournalItemId == task.Id &&
            item.Operation == "delete-duplicate-event" &&
            item.Status == "success");
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task SyncTaskForConnectedUsersAsync_DeletesCompletedTaskEventWhenHideModeEnabled(string status)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-completed-hide@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "hide");
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-completed-hide", "PayPlanner задачи");
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "Скрыть готовую задачу",
            new DateTime(2026, 6, 5, 9, 0, 0));
        task.Status = status;
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "event-yandex-completed-hide",
            YandexEventUrl = $"{calendar.CalendarUrl}event-yandex-completed-hide.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsoluteUri == "https://caldav.yandex.ru/calendars/user/payplanner/event-yandex-completed-hide.ics");
        var link = await db.YandexCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("deleted", link.SyncStatus);
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        task.Status = "inProgress";
        await db.SaveChangesAsync();
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        Assert.Equal("active", (await db.YandexCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id)).SyncStatus);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Theory]
    [InlineData("done", "hide", false)]
    [InlineData("cancelled", "hide", false)]
    [InlineData("done", "color", true)]
    [InlineData("cancelled", "color", true)]
    public async Task SyncNowAsync_AppliesClosedTaskPolicyBeforeCreatingEvent(string status, string action, bool visible)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "closed-initial@example.local", "user");
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: action);
        await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Closed synthetic task", DateTime.UtcNow.Date.AddDays(7).AddHours(9));
        task.Status = status;
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(visible, await db.YandexCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == task.Id));
        Assert.Equal(visible, handler.Requests.Any(request => request.Method == HttpMethod.Put));
        Assert.Equal(status, (await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id)).Status);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task SyncNowAsync_WhenClosedTaskDeleteFails_PreservesLinkAndReportsError(string status)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-completed-hide@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "hide");
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-completed-hide", "PayPlanner задачи");
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "Скрыть готовую задачу",
            DateTime.UtcNow.Date.AddDays(7).AddHours(9));
        task.Status = status;
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "event-yandex-completed-hide",
            YandexEventUrl = $"{calendar.CalendarUrl}event-yandex-completed-hide.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingYandexHandler(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.True(result.ErrorCount > 0);
        Assert.Equal(0, result.ProcessedTasks);
        Assert.Equal("active", (await db.YandexCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id)).SyncStatus);
        Assert.Equal(status, (await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id)).Status);
        var logs = await db.YandexCalendarSyncLogs.Where(item => item.TaskJournalItemId == task.Id).ToListAsync();
        Assert.Contains(logs, item => item.Status == "error");
        Assert.DoesNotContain(logs, item => item.Status == "success" && item.Operation.StartsWith("delete-event", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task SyncNowAsync_WhenManualFullSyncCooldownActiveRejectsRepeatedRun()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-cooldown@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        connection.LastFullSyncAt = DateTime.UtcNow.AddMinutes(-5);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 30);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler()));

        var error = await Assert.ThrowsAsync<CalendarManualSyncCooldownException>(() =>
            service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None));

        Assert.Contains("Повторную полную синхронизацию Яндекс.Календаря можно запустить после", error.Message);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenAdminManagedRejectsRegularUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "yandex-admin-policy@example.com", "user");
        await CreateConnectionAsync(db, user.Id);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "calendarSync.settings",
            Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
            {
                SyncMode = "hybrid",
                ConflictResolutionMode = "latestWins",
                UserControlMode = "adminManaged",
                InactiveUserAction = "cleanupExternalData",
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateSettingsAsync(
                user.Id,
                new UpdateYandexCalendarSettingsRequest { SyncEnabled = false },
                CreateHttpRequest(user.Id),
                CancellationToken.None));

        Assert.Contains("администратором", error.Message);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenAdminManagedRejectsAdminClaimWithoutDatabaseAdminRole()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "yandex-stale-admin-claim@example.com", "user");
        await CreateConnectionAsync(db, user.Id);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "calendarSync.settings",
            Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
            {
                SyncMode = "hybrid",
                ConflictResolutionMode = "latestWins",
                UserControlMode = "adminManaged",
                InactiveUserAction = "cleanupExternalData",
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateSettingsAsync(
                user.Id,
                new UpdateYandexCalendarSettingsRequest { SyncEnabled = false },
                CreateHttpRequest(user.Id, role: "admin"),
                CancellationToken.None));

        Assert.Contains("администратором", error.Message);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenAdminManagedAllowsDatabaseAdminRoleWithoutAdminClaim()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "yandex-db-admin@example.com", "admin");
        await CreateConnectionAsync(db, user.Id);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "calendarSync.settings",
            Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
            {
                SyncMode = "hybrid",
                ConflictResolutionMode = "latestWins",
                UserControlMode = "adminManaged",
                InactiveUserAction = "cleanupExternalData",
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        var result = await service.UpdateSettingsAsync(
            user.Id,
            new UpdateYandexCalendarSettingsRequest { SyncEnabled = false },
            CreateHttpRequest(user.Id, role: "user"),
            CancellationToken.None);

        Assert.NotNull(result.Connection);
        Assert.False(await db.YandexCalendarConnections
            .Where(item => item.UserId == user.Id)
            .Select(item => item.SyncEnabled)
            .SingleAsync());
    }


    [Theory]
    [InlineData(false, "deny")]
    [InlineData(true, "deny")]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    [InlineData(false, "role")]
    [InlineData(true, "role")]
    [InlineData(false, "specific")]
    [InlineData(true, "specific")]
    [InlineData(false, "grant")]
    [InlineData(true, "grant")]
    public async Task Synchronization_RefreshesCalendarPermissionOnSameService(bool singleTask, string change)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "permission-actor@example.local", "permission-actor");
        var selected = await CreateUserAsync(db, "permission-selected@example.local", "permission-selected");
        var other = await CreateUserAsync(db, "permission-other@example.local", "permission-other");
        actor.FullName = "Permission actor";
        selected.FullName = "Permission selected";
        other.FullName = "Permission other";
        var specific = change == "specific";
        var permission = new RolePermission
        {
            RoleId = actor.RoleId,
            Section = RolePermissionCatalog.Calendar,
            CanView = change != "grant",
            ViewScope = specific ? DataVisibilityScope.Specific : DataVisibilityScope.Own,
        };
        db.RolePermissions.Add(permission);
        if (specific)
        {
            db.RoleCalendarVisibleUsers.AddRange(
                new RoleCalendarVisibleUser { RoleId = actor.RoleId, UserId = selected.Id },
                new RoleCalendarVisibleUser { RoleId = actor.RoleId, UserId = other.Id });
        }
        await db.SaveChangesAsync();
        var connection = await CreateConnectionAsync(db, actor.Id, calendarMode: specific ? "adminByAssignee" : "personal");
        var task = await CreateTaskAsync(db, specific ? selected.Id : actor.Id, "Permission before change");
        if (specific)
        {
            task.Participants.Add(new TaskJournalParticipant { UserId = actor.Id, Role = "observer", DisplayName = "Actor" });
            await db.SaveChangesAsync();
        }
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        db.ChangeTracker.Clear();

        async Task RunAsync()
        {
            if (singleTask)
            {
                await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
            }
            else
            {
                var result = await service.SyncDueConnectionsAsync(1, CancellationToken.None);
                Assert.Equal(1, result.ProcessedConnections);
                Assert.Equal(0, result.ErrorCount);
            }
        }

        await RunAsync();
        var initialWrites = handler.Requests.Count(request => request.Method == HttpMethod.Put);
        Assert.Equal(change == "grant" ? 0 : 1, initialWrites);
        switch (change)
        {
            case "deny":
                await db.RolePermissions.Where(item => item.Id == permission.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanView, false));
                break;
            case "grant":
                await db.RolePermissions.Where(item => item.Id == permission.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanView, true));
                break;
            case "inactive":
                await db.Users.Where(item => item.Id == actor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
                break;
            case "role":
                var deniedRole = new Role { Name = "permission-denied", Description = "Denied" };
                db.Roles.Add(deniedRole);
                await db.SaveChangesAsync();
                db.RolePermissions.Add(new RolePermission { RoleId = deniedRole.Id, Section = RolePermissionCatalog.Calendar, CanView = false });
                await db.SaveChangesAsync();
                await db.Users.Where(item => item.Id == actor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RoleId, deniedRole.Id));
                break;
            case "specific":
                await db.RoleCalendarVisibleUsers.Where(item => item.RoleId == actor.RoleId && item.UserId == selected.Id)
                    .ExecuteDeleteAsync();
                break;
        }
        await db.TaskJournalItems.Where(item => item.Id == task.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Title, "Protected after permission change"));
        db.ChangeTracker.Clear();
        await RunAsync();
        var newWrites = handler.Requests.Count(request => request.Method == HttpMethod.Put) - initialWrites;
        Console.WriteLine($"CalendarPermissionRefresh provider=Yandex; single={singleTask}; change={change}; writes={newWrites}");
        Assert.Equal(change == "grant" ? 1 : 0, newWrites);
        Assert.Equal("Protected after permission change", await db.TaskJournalItems.AsNoTracking()
            .Where(item => item.Id == task.Id).Select(item => item.Title).SingleAsync());
        await RunAsync();
        Assert.Equal(initialWrites + (change == "grant" ? 1 : 0), handler.Requests.Count(request => request.Method == HttpMethod.Put));
    }


    [Theory]
    [InlineData(false, "deny")]
    [InlineData(true, "deny")]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    [InlineData(false, "role")]
    [InlineData(true, "role")]
    [InlineData(false, "specific")]
    [InlineData(true, "specific")]
    [InlineData(false, "moved")]
    [InlineData(true, "moved")]
    [InlineData(false, "participant")]
    [InlineData(true, "participant")]
    [InlineData(false, "missingId")]
    [InlineData(true, "missingId")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "stale")]
    [InlineData(true, "stale")]
    [InlineData(false, "deletedMissingId")]
    [InlineData(true, "deletedMissingId")]
    public async Task Import_DoesNotChangeTaskAfterCalendarAccessRevoked(bool newService, string change)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "import-permission-actor@example.local", "import-permission-actor");
        var selected = await CreateUserAsync(db, "import-permission-selected@example.local", "import-permission-selected");
        var other = await CreateUserAsync(db, "import-permission-other@example.local", "import-permission-other");
        actor.FullName = "Import actor";
        selected.FullName = "Import selected";
        other.FullName = "Import other";
        var missingTaskId = change is "missingId" or "deletedMissingId";
        var specific = change == "specific";
        var permission = new RolePermission
        {
            RoleId = actor.RoleId,
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            ViewScope = specific ? DataVisibilityScope.Specific : DataVisibilityScope.Own,
        };
        db.RolePermissions.Add(permission);
        if (specific)
        {
            db.RoleCalendarVisibleUsers.AddRange(
                new RoleCalendarVisibleUser { RoleId = actor.RoleId, UserId = selected.Id },
                new RoleCalendarVisibleUser { RoleId = actor.RoleId, UserId = other.Id });
        }
        await db.SaveChangesAsync();
        var connection = await CreateConnectionAsync(db, actor.Id, calendarMode: specific ? "adminByAssignee" : "personal");
        var calendar = await CreateCalendarAsync(db, connection.Id, "permission-calendar", "Permission calendar",
            ownerUserId: specific ? selected.Id : null);
        var dueAt = DateTime.UtcNow.Date.AddDays(7).AddHours(12);
        var task = await CreateTaskAsync(db, specific || change == "participant" ? selected.Id : actor.Id, "External allowed", dueAt: dueAt);
        if (change == "participant")
        {
            task.Participants.Add(new TaskJournalParticipant { UserId = actor.Id, Role = "observer", DisplayName = actor.FullName });
            task.Participants.Add(new TaskJournalParticipant { UserId = selected.Id, Role = "observer", DisplayName = selected.FullName });
        }
        var unrelated = await CreateTaskAsync(db, other.Id, "Unrelated protected task", dueAt: dueAt);
        var eventId = "event-1";
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = eventId,
            YandexEventUrl = $"{calendar.CalendarUrl}{eventId}.ics",
            SyncStatus = "active",
            LastYandexHash = "before-first-import",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        string BuildPermissionReport(bool revoked)
        {
            var externalId = revoked && change == "stale" ? "stale-event" : eventId;
            return BuildYandexReportResponse(calendar.CalendarUrl, externalId, $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:{externalId}
{(revoked && change is "deleted" or "deletedMissingId" ? "STATUS:CANCELLED" : string.Empty)}
DTSTART:{dueAt.AddHours(1):yyyyMMdd'T'HHmmss'Z'}
DTEND:{dueAt.AddHours(2):yyyyMMdd'T'HHmmss'Z'}
SUMMARY:External allowed
{(missingTaskId ? string.Empty : $"X-PAYPLANNER-TASK-ID:{task.Id}")}
X-PAYPLANNER-HASH:external-permission-change
END:VEVENT
END:VCALENDAR
""");
        }
        var handler = new RecordingYandexHandler(reportResponse: BuildPermissionReport(false), reportCalendarPath: new Uri(calendar.CalendarUrl).AbsolutePath);
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        db.ChangeTracker.Clear();
        var first = await service.SyncDueConnectionsAsync(1, CancellationToken.None);
        Assert.Equal(0, first.ErrorCount);
        Assert.Equal(1, first.ImportedEvents);
        Assert.Equal(dueAt.AddHours(1), await db.TaskJournalItems.AsNoTracking()
            .Where(item => item.Id == task.Id).Select(item => item.DueAt).SingleAsync());

        switch (change)
        {
            case "deny":
                await db.RolePermissions.Where(item => item.Id == permission.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanView, false));
                break;
            case "inactive":
                await db.Users.Where(item => item.Id == actor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
                break;
            case "role":
                var deniedRole = new Role { Name = "import-permission-denied", Description = "Denied" };
                db.Roles.Add(deniedRole);
                await db.SaveChangesAsync();
                db.RolePermissions.Add(new RolePermission { RoleId = deniedRole.Id, Section = RolePermissionCatalog.Calendar, CanView = false });
                await db.SaveChangesAsync();
                await db.Users.Where(item => item.Id == actor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RoleId, deniedRole.Id));
                break;
            case "specific":
                await db.RoleCalendarVisibleUsers.Where(item => item.RoleId == actor.RoleId && item.UserId == selected.Id)
                    .ExecuteDeleteAsync();
                break;
            case "moved":
            case "missingId":
            case "deleted":
            case "stale":
            case "deletedMissingId":
                await db.TaskJournalItems.Where(item => item.Id == task.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.AssigneeUserId, other.Id));
                await db.TaskJournalParticipants.Where(item => item.TaskJournalItemId == task.Id).ExecuteDeleteAsync();
                break;
            case "participant":
                await db.TaskJournalParticipants.Where(item => item.TaskJournalItemId == task.Id && item.UserId == actor.Id).ExecuteDeleteAsync();
                break;
        }
        await db.TaskJournalItems.Where(item => item.Id == task.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Title, "Protected after access revoked")
            .SetProperty(item => item.DueAt, dueAt));
        await db.YandexCalendarTaskLinks.Where(item => item.ConnectionId == connection.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastYandexHash, "before-revoked-import"));
        handler.ReportResponse = BuildPermissionReport(true);
        var protectedLink = await db.YandexCalendarTaskLinks.AsNoTracking()
            .Where(item => item.TaskJournalItemId == task.Id).Select(item => new
            {
                item.Id,
                item.ConnectionId,
                item.CalendarSyncCalendarId,
                item.TaskJournalItemId,
                item.YandexCalendarId,
                item.YandexEventId,
                item.YandexEventUrl,
                item.YandexEtag,
                item.LastYandexHash,
                item.LastCrmHash,
                item.LastSyncedAt,
                item.LastDirection,
                item.SyncStatus,
                item.LastError,
                item.CreatedAt,
                item.UpdatedAt
            }).SingleAsync();
        var requestsBeforeRevokedImport = handler.Requests.Count;
        db.ChangeTracker.Clear();
        if (newService) service = CreateService(db, new FakeHttpClientFactory(handler));
        var second = await service.SyncDueConnectionsAsync(1, CancellationToken.None);
        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Console.WriteLine($"CalendarPermissionImport provider=Yandex; fresh={newService}; change={change}; imported={second.ImportedEvents}; title={stored.Title}");
        Assert.Equal("Protected after access revoked", stored.Title);
        Assert.Equal(dueAt, stored.DueAt);
        Assert.Equal("new", stored.Status);
        Assert.Equal(0, second.ImportedEvents);
        Assert.Equal(0, second.ErrorCount);
        var linkAfterImport = await db.YandexCalendarTaskLinks.AsNoTracking()
            .Where(item => item.TaskJournalItemId == task.Id).Select(item => new
            {
                item.Id,
                item.ConnectionId,
                item.CalendarSyncCalendarId,
                item.TaskJournalItemId,
                item.YandexCalendarId,
                item.YandexEventId,
                item.YandexEventUrl,
                item.YandexEtag,
                item.LastYandexHash,
                item.LastCrmHash,
                item.LastSyncedAt,
                item.LastDirection,
                item.SyncStatus,
                item.LastError,
                item.CreatedAt,
                item.UpdatedAt
            }).SingleOrDefaultAsync();
        if (specific)
        {
            Assert.Null(linkAfterImport);
            Assert.False(await db.YandexCalendarSyncCalendars.AnyAsync(item => item.Id == calendar.Id));
        }
        else Assert.Equal(protectedLink, linkAfterImport);
        Assert.DoesNotContain(handler.Requests.Skip(requestsBeforeRevokedImport), request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath.Contains("stale-event", StringComparison.Ordinal));
        Assert.Equal("Unrelated protected task", await db.TaskJournalItems.AsNoTracking()
            .Where(item => item.Id == unrelated.Id).Select(item => item.Title).SingleAsync());
    }

    private static YandexCalendarSyncService CreateService(PaymentContext db, IHttpClientFactory httpClientFactory)
    {
        return new YandexCalendarSyncService(
            db,
            new ConfigurationBuilder().Build(),
            httpClientFactory,
            TestDataProtectionProvider,
            NullLogger<YandexCalendarSyncService>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronization_RestoresRemovedAssigneeCalendarWithFreshProviderState(bool newService)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "calendar-recovery-actor@example.local", "calendar-recovery-actor");
        var selected = await CreateUserAsync(db, "calendar-recovery-selected@example.local", "calendar-recovery-selected");
        var other = await CreateUserAsync(db, "calendar-recovery-other@example.local", "calendar-recovery-other");
        var hidden = await CreateUserAsync(db, "calendar-recovery-hidden@example.local", "calendar-recovery-hidden");
        actor.FullName = "Recovery actor";
        selected.FullName = "Recovery selected";
        other.FullName = "Recovery other";
        hidden.FullName = "Recovery hidden";
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = actor.RoleId,
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            ViewScope = DataVisibilityScope.Specific,
        });
        db.RoleCalendarVisibleUsers.AddRange(
            new RoleCalendarVisibleUser { RoleId = actor.RoleId, UserId = selected.Id },
            new RoleCalendarVisibleUser { RoleId = actor.RoleId, UserId = other.Id });
        await db.SaveChangesAsync();
        var connection = await CreateConnectionAsync(db, actor.Id, calendarMode: "adminByAssignee");
        var task = await CreateTaskAsync(db, selected.Id, "Recover allowed calendar");
        var hiddenTask = await CreateTaskAsync(db, hidden.Id, "Preserve hidden calendar");
        using var handler = new CalendarRecoveryYandexHandler(
            ("recovery-actor", $"PayPlanner - {actor.FullName}"),
            ("recovery-selected", $"PayPlanner - {selected.FullName}"),
            ("recovery-other", $"PayPlanner - {other.FullName}"),
            ("recovery-hidden", $"PayPlanner - {hidden.FullName}"));
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        db.ChangeTracker.Clear();
        var initial = await service.SyncDueConnectionsAsync(1, CancellationToken.None);
        Assert.Equal(0, initial.ErrorCount);
        Assert.Equal(1, initial.ProcessedTasks);
        Assert.Single(handler.SuccessfulWrites);
        var oldCalendar = await db.YandexCalendarSyncCalendars.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == connection.Id && item.OwnerUserId == selected.Id);
        var oldPath = new Uri(oldCalendar.CalendarUrl).AbsolutePath;

        await db.RoleCalendarVisibleUsers.Where(item => item.RoleId == actor.RoleId && item.UserId == selected.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        var revoked = await service.SyncDueConnectionsAsync(1, CancellationToken.None);
        Assert.False(handler.ActiveCalendars.ContainsKey(oldPath));
        Assert.False(await db.YandexCalendarSyncCalendars.AnyAsync(item => item.ConnectionId == connection.Id && item.OwnerUserId == selected.Id));
        Assert.False(await db.YandexCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == task.Id));
        Assert.Single(handler.SuccessfulWrites);

        db.RoleCalendarVisibleUsers.Add(new RoleCalendarVisibleUser { RoleId = actor.RoleId, UserId = selected.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        if (newService) service = CreateService(db, new FakeHttpClientFactory(handler));
        var restored = await service.SyncDueConnectionsAsync(1, CancellationToken.None);
        Console.WriteLine($"CalendarPermissionRecovery fresh={newService}; revokedErrors={revoked.ErrorCount}; restoredErrors={restored.ErrorCount}; created={handler.CreatedCalendars}; successfulWrites={handler.SuccessfulWrites.Count}; missingReads={handler.MissingReads}");
        Assert.Equal(0, revoked.ErrorCount);
        Assert.Equal(0, restored.ErrorCount);
        Assert.Equal(1, restored.ProcessedTasks);
        Assert.Equal(2, handler.SuccessfulWrites.Count);
        Assert.Equal(1, handler.CreatedCalendars);
        var newCalendar = await db.YandexCalendarSyncCalendars.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == connection.Id && item.OwnerUserId == selected.Id);
        Assert.NotEqual(oldPath, new Uri(newCalendar.CalendarUrl).AbsolutePath);
        Assert.True(handler.ActiveCalendars.ContainsKey(new Uri(newCalendar.CalendarUrl).AbsolutePath));
        var link = await db.YandexCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("active", link.SyncStatus);
        Assert.Null(link.LastError);
        Assert.Equal("Recover allowed calendar", await db.TaskJournalItems.AsNoTracking().Where(item => item.Id == task.Id).Select(item => item.Title).SingleAsync());
        Assert.True(handler.ActiveCalendars.ContainsKey(CalendarRecoveryYandexHandler.PathFor("recovery-hidden")));
        Assert.False(await db.YandexCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == hiddenTask.Id));
        Assert.Equal("Preserve hidden calendar", await db.TaskJournalItems.AsNoTracking().Where(item => item.Id == hiddenTask.Id).Select(item => item.Title).SingleAsync());
    }

    private sealed class CalendarRecoveryYandexHandler : HttpMessageHandler
    {
        public Dictionary<string, string> ActiveCalendars { get; } = new(StringComparer.Ordinal);
        public List<string> SuccessfulWrites { get; } = [];
        public int CreatedCalendars { get; private set; }
        public int MissingReads { get; private set; }
        public static string PathFor(string id) => $"/calendars/yandex%40example.com/{id}/";

        public CalendarRecoveryYandexHandler(params (string Id, string Name)[] calendars)
        {
            foreach (var item in calendars) ActiveCalendars.Add(PathFor(item.Id), item.Name);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method.Method;
            if (method == "PROPFIND")
            {
                return Reply((HttpStatusCode)207, BuildYandexCalendarHomeResponseForCalendars(
                    ActiveCalendars.Select(item => (item.Key, item.Value)).ToArray()));
            }
            if (method == "MKCALENDAR")
            {
                var document = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                XNamespace dav = "DAV:";
                var name = document.Descendants(dav + "displayname").Single().Value;
                if (!ActiveCalendars.TryAdd(path, name)) return Reply(HttpStatusCode.Conflict);
                CreatedCalendars++;
                return Reply(HttpStatusCode.Created);
            }
            if (method == "DELETE")
            {
                return Reply(ActiveCalendars.Remove(path) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
            }
            if (method == "REPORT")
            {
                if (ActiveCalendars.ContainsKey(path)) return Reply((HttpStatusCode)207);
                MissingReads++;
                return Reply(HttpStatusCode.NotFound);
            }
            if (method == "PROPPATCH")
            {
                return Reply(ActiveCalendars.ContainsKey(path) ? (HttpStatusCode)207 : HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Put)
            {
                var calendarPath = path[..(path.LastIndexOf('/') + 1)];
                if (!ActiveCalendars.ContainsKey(calendarPath)) return Reply(HttpStatusCode.NotFound);
                SuccessfulWrites.Add(path);
                return Reply(HttpStatusCode.Created);
            }
            return Reply(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string payload = "")
            => new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/xml") };
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("insert")]
    public async Task SyncNowAsync_Processes502TasksInBoundedPagesAndRetriesOnlyFailedEvent(string scenario)
    {
        var failOnce = scenario == "failure";
        var cancelAfterFirstPage = scenario == "cancel";
        var insertDuringSync = scenario == "insert";
        using var cancellation = new CancellationTokenSource();
        var queryProbe = new CalendarBatchQueryProbe("Yandex");
        await using var db = await CreateContextAsync(queryProbe);
        var user = await CreateUserAsync(db, "bulk-owner@example.local", "user");
        var hidden = await CreateUserAsync(db, "bulk-hidden@example.local", "user");
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "hide");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "bulk-calendar", "PayPlanner задачи");
        var dueAt = DateTime.UtcNow.Date.AddDays(7).AddHours(9);
        var tasks = Enumerable.Range(1, 502).Select(index => new TaskJournalItem
        {
            ObjectName = "Synthetic bulk",
            Title = $"Bulk task {index}",
            AssigneeUserId = user.Id,
            AssigneeName = "User",
            DueAt = dueAt,
            PlannedDurationMinutes = 60,
            Priority = "normal",
            Status = index % 2 == 0 ? "done" : "cancelled",
        }).ToArray();
        db.TaskJournalItems.AddRange(tasks);
        await db.SaveChangesAsync();
        for (var index = 0; index < tasks.Length; index++)
        {
            var eventId = $"bulk-event-{index + 1}";
            db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = tasks[index].Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = eventId,
                YandexEventUrl = $"{calendar.CalendarUrl}{eventId}.ics",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        await CreateTaskAsync(db, hidden.Id, "Not visible", dueAt: dueAt);
        await CreateTaskAsync(db, user.Id, "Outside sync window", dueAt: DateTime.UtcNow.AddDays(366));
        var firstPageIds = tasks.Take(500).Select(item => item.Id).ToArray();
        YandexCalendarTaskLink? lateCallerLink = null;
        var inner = new RecordingYandexHandler();
        var handler = new CalendarBatchDeleteProbe(inner, failOnce, async () =>
        {
            Assert.Equal(500, await db.YandexCalendarTaskLinks.AsNoTracking()
                .CountAsync(item => firstPageIds.Contains(item.TaskJournalItemId) && item.SyncStatus == "deleted"));
            if (scenario != "normal")
            {
                Assert.Equal(2, db.ChangeTracker.Entries<YandexCalendarTaskLink>().Count());
                Assert.Empty(db.ChangeTracker.Entries<YandexCalendarSyncLog>());
                var trackedConnection = Assert.Single(db.ChangeTracker.Entries<YandexCalendarConnection>()).Entity;
                Assert.Equal(2, trackedConnection.TaskLinks.Count);
                Assert.Equal(2, trackedConnection.Calendars.Sum(item => item.TaskLinks.Count));
            }
            if (insertDuringSync)
            {
                var lateTask = await CreateTaskAsync(db, user.Id, "Created during sync", dueAt: dueAt);
                lateTask.Status = "cancelled";
                lateCallerLink = new YandexCalendarTaskLink
                {
                    ConnectionId = connection.Id,
                    CalendarSyncCalendarId = calendar.Id,
                    TaskJournalItemId = lateTask.Id,
                    YandexCalendarId = calendar.YandexCalendarId,
                    YandexEventId = "bulk-event-503",
                    YandexEventUrl = $"{calendar.CalendarUrl}bulk-event-503.ics",
                    SyncStatus = "active",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                db.YandexCalendarTaskLinks.Add(lateCallerLink);
                await db.SaveChangesAsync();
            }
            if (cancelAfterFirstPage)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        });
        if (scenario != "normal")
        {
            db.ChangeTracker.Clear();
        }
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        queryProbe.Reset();

        if (cancelAfterFirstPage)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), cancellation.Token));
        }
        else
        {
            var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), cancellation.Token);
            Assert.Equal(failOnce ? 501 : 502, result.ProcessedTasks);
            Assert.Equal(failOnce ? 1 : 0, result.ErrorCount);
        }
        Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
        Assert.InRange(queryProbe.LinkSelects, 2, 4);
        Console.WriteLine($"CalendarLogQueries provider=Yandex; scenario=hide; case={scenario}; phase=first; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Assert.Equal(cancelAfterFirstPage ? 501 : 502, handler.DeleteAttempts.Count);
        Assert.All(handler.DeleteAttempts.Values, count => Assert.Equal(1, count));
        Assert.Equal(cancelAfterFirstPage ? 500 : failOnce ? 501 : 502, await db.YandexCalendarTaskLinks.AsNoTracking().CountAsync(item => item.SyncStatus == "deleted"));
        Assert.Equal(insertDuringSync ? 503 : 502, await db.YandexCalendarTaskLinks.CountAsync());
        Assert.DoesNotContain(inner.Requests, request => request.Method == HttpMethod.Put);
        var taskIds = tasks.Select(item => item.Id).ToArray();
        Assert.Equal(502, await db.TaskJournalItems.CountAsync(item => taskIds.Contains(item.Id) && (item.Status == "done" || item.Status == "cancelled")));
        if (lateCallerLink is not null)
        {
            Assert.Equal(EntityState.Unchanged, db.Entry(lateCallerLink).State);
        }
        db.ChangeTracker.Clear();
        queryProbe.Reset();
        service = CreateService(db, new FakeHttpClientFactory(handler));

        var retry = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(insertDuringSync ? 503 : 502, retry.ProcessedTasks);
        Assert.Equal(0, retry.ErrorCount);
        Assert.Equal(insertDuringSync ? 503 : 502, await db.YandexCalendarTaskLinks.AsNoTracking().CountAsync(item => item.SyncStatus == "deleted"));
        Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
        Assert.InRange(queryProbe.LinkSelects, 2, 4);
        Console.WriteLine($"CalendarLogQueries provider=Yandex; scenario=hide; case={scenario}; phase=retry; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Assert.Equal(insertDuringSync ? 503 : 502, handler.DeleteAttempts.Count);
        Assert.All(handler.DeleteAttempts, item => Assert.Equal((failOnce || cancelAfterFirstPage) && item.Key.Contains("bulk-event-501", StringComparison.Ordinal) ? 2 : 1, item.Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SyncNowAsync_Colors502EventsInBoundedPagesWithoutRewritingOnRepeat(bool withDuplicates, bool failBatchRead)
    {
        var queryProbe = new CalendarBatchQueryProbe("Yandex");
        await using var db = await CreateContextAsync(queryProbe);
        var user = await CreateUserAsync(db, "bulk-owner@example.local", "user");
        var hidden = await CreateUserAsync(db, "bulk-hidden@example.local", "user");
        await SetUserTimeZoneAsync(db, user.Id, "Europe/Samara");
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "bulk-calendar", "PayPlanner задачи");
        var dueAt = DateTime.UtcNow.Date.AddDays(7).AddHours(9);
        var tasks = Enumerable.Range(1, 502).Select(index => new TaskJournalItem
        {
            ObjectName = "Synthetic bulk",
            Title = $"Bulk task {index}",
            AssigneeUserId = user.Id,
            AssigneeName = "User",
            DueAt = dueAt,
            PlannedDurationMinutes = 60,
            Priority = "normal",
            Status = index % 2 == 0 ? "done" : "cancelled",
        }).ToArray();
        db.TaskJournalItems.AddRange(tasks);
        await db.SaveChangesAsync();
        for (var index = 0; index < tasks.Length; index++)
        {
            var eventId = $"bulk-event-{index + 1}";
            db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = tasks[index].Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = eventId,
                YandexEventUrl = $"{calendar.CalendarUrl}{eventId}.ics",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        var foreignTask = await CreateTaskAsync(db, hidden.Id, "Not visible", dueAt: dueAt);
        await CreateTaskAsync(db, user.Id, "Outside sync window", dueAt: DateTime.UtcNow.AddDays(366));
        var firstPageIds = tasks.Take(500).Select(item => item.Id).ToArray();
        var inner = new RecordingYandexHandler();
        string BuildDuplicateIcs(int taskId, string eventId) => $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:{eventId}
DTSTART:{dueAt:yyyyMMdd'T'HHmmss'Z'}
DTEND:{dueAt.AddHours(1):yyyyMMdd'T'HHmmss'Z'}
SUMMARY:Synthetic duplicate
X-PAYPLANNER-TASK-ID:{taskId}
END:VEVENT
END:VCALENDAR
""";
        var duplicateReport = withDuplicates ? BuildYandexReportResponseForEvents(
            (calendar.CalendarUrl, "duplicate-first", BuildDuplicateIcs(tasks[0].Id, "duplicate-first")),
            (calendar.CalendarUrl, "duplicate-second", BuildDuplicateIcs(tasks[500].Id, "duplicate-second")),
            (calendar.CalendarUrl, "bulk-event-1", BuildDuplicateIcs(tasks[0].Id, "bulk-event-1")),
            (calendar.CalendarUrl, "foreign-duplicate", BuildDuplicateIcs(foreignTask.Id, "foreign-duplicate"))) : null;
        var handler = new CalendarBatchColorProbe(inner, async () =>
        {
            Assert.Equal(500, await db.YandexCalendarTaskLinks.AsNoTracking()
                .CountAsync(item => firstPageIds.Contains(item.TaskJournalItemId)
                    && item.SyncStatus == "active" && item.LastCrmHash != null));
            Assert.Equal(2, db.ChangeTracker.Entries<YandexCalendarTaskLink>().Count());
        }, yandexDuplicateReport: duplicateReport, failFirstYandexDuplicateRead: failBatchRead);
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        queryProbe.Reset();

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(failBatchRead ? 2 : 502, result.ProcessedTasks);
        Assert.Equal(failBatchRead ? 500 : 0, result.ErrorCount);
        Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
        Assert.InRange(queryProbe.LinkSelects, 2, 4);
        var exportLogs = await db.YandexCalendarSyncLogs.AsNoTracking()
            .Where(item => item.ConnectionId == connection.Id && item.Operation == "update-event").ToListAsync();
        Assert.Equal(502, exportLogs.Count);
        var logTasksById = tasks.ToDictionary(item => item.Id);
        foreach (var exportLog in exportLogs)
        {
            using var details = JsonDocument.Parse(exportLog.DetailsJson!);
            var value = details.RootElement;
            var logTask = logTasksById[exportLog.TaskJournalItemId!.Value];
            Assert.Equal(user.FullName, value.GetProperty("userName").GetString());
            Assert.Equal(user.Email, value.GetProperty("userEmail").GetString());
            Assert.Equal(calendar.Name, value.GetProperty("calendarName").GetString());
            Assert.False(value.TryGetProperty("calendarOwnerName", out _));
            Assert.False(value.TryGetProperty("calendarOwnerEmail", out _));
            Assert.Equal(logTask.Title, value.GetProperty("taskTitle").GetString());
            Assert.Equal(logTask.ObjectName, value.GetProperty("taskObjectName").GetString());
            Assert.Equal(logTask.AssigneeName, value.GetProperty("taskAssigneeName").GetString());
            Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("detail").GetString()));
        }
        Console.WriteLine($"CalendarLogQueries provider=Yandex; scenario=color; case={withDuplicates}-{failBatchRead}; phase=first; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Console.WriteLine($"CalendarTimezoneQueries provider=Yandex; phase=first; settings={queryProbe.TimeZoneSelects}");
        Assert.InRange(queryProbe.TimeZoneSelects, 1, 2);
        Assert.Equal(502, handler.PutBodies.Count);
        Assert.All(handler.PutBodies.Values, bodies => Assert.Single(bodies));
        Assert.Equal(withDuplicates ? 2 : 0, handler.DeleteCount);
        Assert.Equal(502, await db.YandexCalendarTaskLinks.AsNoTracking()
            .CountAsync(item => item.SyncStatus == "active" && item.LastCrmHash != null));
        Assert.Contains(handler.PutBodies.Values, bodies => bodies[0].Contains("SUMMARY:[Отменено]", StringComparison.Ordinal));
        Assert.Contains(handler.PutBodies.Values, bodies => bodies[0].Contains("SUMMARY:[Готово]", StringComparison.Ordinal));
        Assert.Equal(3, handler.CalendarReadQueries.Count); // import + one duplicate snapshot per page
        Assert.Equal(failBatchRead ? 500 : 0, await db.YandexCalendarTaskLinks.AsNoTracking()
            .CountAsync(item => item.LastError != null));
        if (withDuplicates)
        {
            Assert.Contains(handler.DeletedPaths, path => path.EndsWith("duplicate-first.ics", StringComparison.Ordinal));
            Assert.Contains(handler.DeletedPaths, path => path.EndsWith("duplicate-second.ics", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.DeletedPaths, path => path.EndsWith("bulk-event-1.ics", StringComparison.Ordinal)
                || path.EndsWith("foreign-duplicate.ics", StringComparison.Ordinal));
        }
        var taskIds = tasks.Select(item => item.Id).ToArray();
        Assert.Equal(502, await db.TaskJournalItems.CountAsync(item => taskIds.Contains(item.Id)
            && (item.Status == "done" || item.Status == "cancelled")));
        db.ChangeTracker.Clear();
        queryProbe.Reset();
        handler.CalendarReadQueries.Clear();
        await using var retryDb = CreateAdditionalContext(db, queryProbe);
        service = CreateService(retryDb, new FakeHttpClientFactory(handler));

        var retry = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(502, retry.ProcessedTasks);
        Assert.Equal(0, retry.ErrorCount);
        Console.WriteLine($"CalendarLogQueries provider=Yandex; scenario=color; case={withDuplicates}-{failBatchRead}; phase=retry; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Console.WriteLine($"CalendarTimezoneQueries provider=Yandex; phase=retry; settings={queryProbe.TimeZoneSelects}");
        Assert.InRange(queryProbe.TimeZoneSelects, 1, 2);
        Assert.Equal(502, handler.PutBodies.Count);
        Assert.All(handler.PutBodies.Values, bodies => Assert.Single(bodies));
        Assert.Equal(withDuplicates ? 2 : 0, handler.DeleteCount);
        Assert.Equal(failBatchRead ? 2 : 1, handler.CalendarReadQueries.Count);
        Assert.False(await db.YandexCalendarTaskLinks.AnyAsync(item => item.LastError != null));
        Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
        Assert.InRange(queryProbe.LinkSelects, 2, 4);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Synchronization_RetriesIncompleteDuplicateCleanupWithoutRewritingEvent(bool failRead, bool fullSync, bool existingLink)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "cleanup-owner@example.local", "user");
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "cleanup-calendar", "Synthetic cleanup calendar");
        var task = await CreateTaskAsync(db, user.Id, "Synthetic cleanup recovery", dueAt: DateTime.UtcNow.AddDays(7));
        if (existingLink)
        {
            db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = task.Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = "current-yandex-event",
                YandexEventUrl = $"{calendar.CalendarUrl}current-yandex-event.ics",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var duplicatePayload = BuildYandexReportResponse(calendar.CalendarUrl, "cleanup-duplicate", $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:cleanup-duplicate
DTSTART:{task.DueAt:yyyyMMdd'T'HHmmss'Z'}
DTEND:{task.DueAt.AddHours(1):yyyyMMdd'T'HHmmss'Z'}
SUMMARY:Synthetic duplicate
X-PAYPLANNER-TASK-ID:{task.Id}
END:VEVENT
END:VCALENDAR
""");
        var handler = new CalendarCleanupFailureProbe(new RecordingYandexHandler(), google: false, fullSync, failRead, duplicatePayload);
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        handler.BeginRun();
        if (fullSync)
        {
            var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);
            Assert.Equal(1, result.ErrorCount);
            Assert.Equal(0, result.ProcessedTasks);
        }
        else
        {
            await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        }
        Assert.Equal(1, handler.EventWrites);
        var failedLink = await db.YandexCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("active", failedLink.SyncStatus);
        Assert.False(string.IsNullOrWhiteSpace(failedLink.LastCrmHash));
        Assert.False(string.IsNullOrWhiteSpace(failedLink.LastError));
        Assert.True(await db.YandexCalendarSyncLogs.AnyAsync(item => item.TaskJournalItemId == task.Id
            && item.Operation == "duplicate-cleanup" && item.Status == "error"));
        var firstHash = failedLink.LastCrmHash;
        db.ChangeTracker.Clear();
        await using var retryDb = CreateAdditionalContext(db);
        service = CreateService(retryDb, new FakeHttpClientFactory(handler));
        handler.BeginRun();

        if (fullSync)
        {
            var retry = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
            Assert.Equal(0, retry.ErrorCount);
            Assert.Equal(1, retry.ProcessedTasks);
        }
        else
        {
            await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        }
        Assert.Equal(1, handler.EventWrites);
        Assert.True(handler.DuplicateDeleted);
        Assert.Equal(failRead ? 1 : 2, handler.DuplicateDeleteAttempts);
        Assert.Equal(2, handler.DuplicateReadAttempts);
        var recoveredLink = await db.YandexCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Null(recoveredLink.LastError);
        Assert.Null((await db.YandexCalendarConnections.AsNoTracking().SingleAsync(item => item.Id == connection.Id)).LastError);
        Assert.True(await db.YandexCalendarSyncLogs.AnyAsync(item => item.TaskJournalItemId == task.Id
            && item.Operation == "duplicate-cleanup" && item.Status == "success"));
        Assert.Equal(firstHash, recoveredLink.LastCrmHash);
        Assert.Equal("new", (await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).Status);
        Assert.Equal(1, await db.YandexCalendarTaskLinks.CountAsync(item => item.TaskJournalItemId == task.Id));
        db.ChangeTracker.Clear();
        await using var repeatDb = CreateAdditionalContext(db);
        service = CreateService(repeatDb, new FakeHttpClientFactory(handler));
        handler.BeginRun();
        if (fullSync)
        {
            var repeated = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
            Assert.Equal(0, repeated.ErrorCount);
        }
        else
        {
            await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        }
        Assert.Equal(1, handler.EventWrites);
        Assert.Equal(failRead ? 1 : 2, handler.DuplicateDeleteAttempts);
        Assert.Equal(2, handler.DuplicateReadAttempts);
    }

    [Fact]
    public async Task Synchronization_SingleRetryPreservesOtherTaskCleanupError()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "cleanup-owner@example.local", "user");
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "cleanup-calendar", "Synthetic cleanup calendar");
        var task = await CreateTaskAsync(db, user.Id, "Synthetic cleanup recovery", dueAt: DateTime.UtcNow.AddDays(7));
        var otherTask = await CreateTaskAsync(db, user.Id, "Synthetic other pending cleanup", dueAt: task.DueAt);
        {
            db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = task.Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = "current-yandex-event",
                YandexEventUrl = $"{calendar.CalendarUrl}current-yandex-event.ics",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var duplicatePayload = BuildYandexReportResponse(calendar.CalendarUrl, "cleanup-duplicate", $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:cleanup-duplicate
DTSTART:{task.DueAt:yyyyMMdd'T'HHmmss'Z'}
DTEND:{task.DueAt.AddHours(1):yyyyMMdd'T'HHmmss'Z'}
SUMMARY:Synthetic duplicate
X-PAYPLANNER-TASK-ID:{task.Id}
END:VEVENT
END:VCALENDAR
""");
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = otherTask.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "other-event",
            YandexEventUrl = $"{calendar.CalendarUrl}other-event.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new CalendarCleanupFailureProbe(new RecordingYandexHandler(), google: false,
            fullSync: false, failRead: true, duplicatePayload, failureCount: 2);
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        await service.SyncTaskForConnectedUsersAsync(otherTask.Id, CancellationToken.None);
        Assert.Equal(2, handler.EventWrites);
        Assert.Equal(2, await db.YandexCalendarTaskLinks.AsNoTracking().CountAsync(item => item.LastError != null));
        db.ChangeTracker.Clear();
        await using var retryDb = CreateAdditionalContext(db);
        service = CreateService(retryDb, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Null((await db.YandexCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id)).LastError);
        Assert.NotNull((await db.YandexCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == otherTask.Id)).LastError);
        Assert.NotNull((await db.YandexCalendarConnections.AsNoTracking().SingleAsync(item => item.Id == connection.Id)).LastError);
        Assert.Equal(2, handler.EventWrites);
        await using var finalDb = CreateAdditionalContext(db);
        service = CreateService(finalDb, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(otherTask.Id, CancellationToken.None);

        Assert.False(await db.YandexCalendarTaskLinks.AnyAsync(item => item.LastError != null));
        Assert.Null((await db.YandexCalendarConnections.AsNoTracking().SingleAsync(item => item.Id == connection.Id)).LastError);
        Assert.Equal(2, handler.EventWrites);
        Assert.Equal(4, handler.DuplicateReadAttempts);
        Assert.Equal(1, handler.DuplicateDeleteAttempts);
    }

    [Fact]
    public async Task SyncNowAsync_AssigneeCalendarOwnerLabelsArePreservedInJournal()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "log-admin@example.local", "admin");
        var assignee = await CreateUserAsync(db, "log-assignee@example.local", "user");
        admin.FullName = "Synthetic administrator";
        assignee.FullName = "Synthetic assignee";
        await db.SaveChangesAsync();
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var task = await CreateTaskAsync(db, assignee.Id, "Synthetic assigned task", dueAt: DateTime.UtcNow.AddDays(7));
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler()));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        var log = await db.YandexCalendarSyncLogs.AsNoTracking().SingleAsync(item =>
            item.ConnectionId == connection.Id && item.TaskJournalItemId == task.Id && item.Operation == "create-event");
        using var details = JsonDocument.Parse(log.DetailsJson!);
        Assert.Equal(admin.FullName, details.RootElement.GetProperty("userName").GetString());
        Assert.Equal(admin.Email, details.RootElement.GetProperty("userEmail").GetString());
        Assert.Equal(assignee.FullName, details.RootElement.GetProperty("calendarOwnerName").GetString());
        Assert.Equal(assignee.Email, details.RootElement.GetProperty("calendarOwnerEmail").GetString());
        Assert.Equal(task.Title, details.RootElement.GetProperty("taskTitle").GetString());
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_LogContextRefreshesAcrossUsersAndCalls()
    {
        await using var db = await CreateContextAsync();
        var first = await CreateUserAsync(db, "log-first@example.local", "user");
        var second = await CreateUserAsync(db, "log-second@example.local", "user");
        first.FullName = "Synthetic first user";
        second.FullName = "Synthetic second user";
        await db.SaveChangesAsync();
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var firstConnection = await CreateConnectionAsync(db, first.Id);
        var secondConnection = await CreateConnectionAsync(db, second.Id);
        var firstCalendar = await CreateCalendarAsync(db, firstConnection.Id, "log-first-calendar", "Synthetic first calendar");
        var secondCalendar = await CreateCalendarAsync(db, secondConnection.Id, "log-second-calendar", "Synthetic second calendar");
        var firstTask = await CreateTaskAsync(db, first.Id, "Synthetic first task", dueAt: DateTime.UtcNow.AddDays(7));
        var secondTask = await CreateTaskAsync(db, second.Id, "Synthetic second task", dueAt: firstTask.DueAt);
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler()));

        await service.SyncTaskForConnectedUsersAsync(firstTask.Id, CancellationToken.None);
        await service.SyncTaskForConnectedUsersAsync(secondTask.Id, CancellationToken.None);

        var firstLog = await db.YandexCalendarSyncLogs.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == firstConnection.Id && item.Operation == "create-event");
        var secondLog = await db.YandexCalendarSyncLogs.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == secondConnection.Id && item.Operation == "create-event");
        using var firstDetails = JsonDocument.Parse(firstLog.DetailsJson!);
        using var secondDetails = JsonDocument.Parse(secondLog.DetailsJson!);
        Assert.Equal(first.FullName, firstDetails.RootElement.GetProperty("userName").GetString());
        Assert.Equal(first.Email, firstDetails.RootElement.GetProperty("userEmail").GetString());
        Assert.Equal(firstCalendar.Name, firstDetails.RootElement.GetProperty("calendarName").GetString());
        Assert.Equal(firstTask.Title, firstDetails.RootElement.GetProperty("taskTitle").GetString());
        Assert.Equal(second.FullName, secondDetails.RootElement.GetProperty("userName").GetString());
        Assert.Equal(second.Email, secondDetails.RootElement.GetProperty("userEmail").GetString());
        Assert.Equal(secondCalendar.Name, secondDetails.RootElement.GetProperty("calendarName").GetString());
        Assert.Equal(secondTask.Title, secondDetails.RootElement.GetProperty("taskTitle").GetString());

        first.FullName = "Synthetic renamed user";
        first.Email = "log-renamed@example.local";
        firstCalendar.Name = "Synthetic renamed calendar";
        firstTask.Title = "Synthetic changed task";
        firstTask.ObjectName = "Synthetic changed object";
        firstTask.AssigneeName = "Synthetic changed assignee";
        await db.SaveChangesAsync();

        await service.SyncTaskForConnectedUsersAsync(firstTask.Id, CancellationToken.None);

        var updateLog = await db.YandexCalendarSyncLogs.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == firstConnection.Id && item.Operation == "update-event");
        using var updated = JsonDocument.Parse(updateLog.DetailsJson!);
        Assert.Equal(first.FullName, updated.RootElement.GetProperty("userName").GetString());
        Assert.Equal(first.Email, updated.RootElement.GetProperty("userEmail").GetString());
        Assert.Equal(firstCalendar.Name, updated.RootElement.GetProperty("calendarName").GetString());
        Assert.Equal(firstTask.Title, updated.RootElement.GetProperty("taskTitle").GetString());
        Assert.Equal(firstTask.ObjectName, updated.RootElement.GetProperty("taskObjectName").GetString());
        Assert.Equal(firstTask.AssigneeName, updated.RootElement.GetProperty("taskAssigneeName").GetString());
        Assert.Equal(firstLog.DetailsJson, (await db.YandexCalendarSyncLogs.AsNoTracking()
            .SingleAsync(item => item.Id == firstLog.Id)).DetailsJson);
    }

    private static PaymentContext CreateAdditionalContext(PaymentContext original, CalendarBatchQueryProbe? queryProbe = null)
    {
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(original.Database.GetDbConnection());
        if (queryProbe is not null)
        {
            options.AddInterceptors(queryProbe);
        }
        return new PaymentContext(options.Options, useObjectAuditModel: false);
    }

    private static async Task<PaymentContext> CreateContextAsync(CalendarBatchQueryProbe? queryProbe = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection);
        if (queryProbe is not null)
        {
            optionsBuilder.AddInterceptors(queryProbe);
        }
        var options = optionsBuilder.Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string roleName)
    {
        var role = await db.Roles.SingleOrDefaultAsync(item => item.Name == roleName);
        if (role is null)
        {
            role = new Role { Name = roleName, Description = roleName };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }
        var user = new User
        {
            Email = email,
            FullName = "User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }


    [Theory]
    [InlineData(false, "America/New_York")]
    [InlineData(true, "America/New_York")]
    [InlineData(false, "Unknown/Timezone")]
    [InlineData(true, "Unknown/Timezone")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task Synchronization_RefreshesProfileTimeZoneOnSameService(bool singleTask, string? newTimeZone)
    {
        var queryProbe = new CalendarBatchQueryProbe("Yandex");
        await using var db = await CreateContextAsync(queryProbe);
        var user = await CreateUserAsync(db, "timezone-refresh@example.local", "user");
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10);
        await SetUserTimeZoneAsync(db, user.Id, "Europe/Samara");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "timezone-calendar", "Synthetic timezone calendar");
        var dueAt = DateTime.UtcNow.Date.AddDays(7).AddHours(23).AddMinutes(30);
        var task = await CreateTaskAsync(db, user.Id, "Synthetic timezone refresh", dueAt: dueAt);
        var handler = new RecordingYandexHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        db.ChangeTracker.Clear();

        async Task RunAsync()
        {
            if (singleTask)
            {
                await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
            }
            else
            {
                await db.YandexCalendarConnections.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastFullSyncAt, (DateTime?)null)
                    .SetProperty(item => item.LastIncrementalSyncAt, (DateTime?)null));
                db.ChangeTracker.Clear();
                var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);
                Assert.Equal(1, result.ProcessedTasks);
                Assert.Equal(0, result.ErrorCount);
            }
        }

        await RunAsync();
        var oldHash = await db.YandexCalendarTaskLinks.AsNoTracking().Select(item => item.LastCrmHash).SingleAsync();
        var settingQuery = db.UserSettings.Where(item => item.UserId == user.Id
            && item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey);
        if (newTimeZone is null)
        {
            await settingQuery.ExecuteDeleteAsync();
        }
        else
        {
            await settingQuery.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Value, newTimeZone));
        }
        queryProbe.Reset();
        await RunAsync();
        var writes = handler.Requests.Where(request => request.Method == HttpMethod.Put).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.NotEqual(oldHash, await db.YandexCalendarTaskLinks.AsNoTracking().Select(item => item.LastCrmHash).SingleAsync());
        var expectedZone = CalendarTimeZoneHelper.ResolveTimeZone(newTimeZone);
        var expectedId = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(expectedZone);
        var payload = await writes[1].Content!.ReadAsStringAsync();
        Assert.Contains($"X-PAYPLANNER-TIMEZONE:{expectedId}", payload);
        Assert.Contains($"DTSTART:{CalendarTimeZoneHelper.FormatUtcIcsDateTime(dueAt, expectedZone)}", payload);
        Assert.Contains($"DTEND:{CalendarTimeZoneHelper.FormatUtcIcsDateTime(dueAt.AddHours(1), expectedZone)}", payload);
        Assert.Contains(handler.Requests, request => request.Method.Method == "PROPPATCH"
            && WebUtility.HtmlDecode(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Contains($"TZID:{expectedId}", StringComparison.Ordinal));
        Console.WriteLine($"CalendarTimezoneRefresh provider=Yandex; single={singleTask}; setting={newTimeZone ?? "missing"}; settings={queryProbe.TimeZoneSelects}");
        Assert.InRange(queryProbe.TimeZoneSelects, 1, 2);
        await RunAsync();
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Put));
        Assert.False(await db.YandexCalendarTaskLinks.AnyAsync(item => item.LastError != null));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncNowAsync_BatchesTimeZonesFor502TasksAndPreservesCalendarOwner(bool assigneeMode)
    {
        var queryProbe = new CalendarBatchQueryProbe("Yandex");
        await using var db = await CreateContextAsync(queryProbe);
        var admin = await CreateUserAsync(db, "timezone-batch-admin@example.local", "admin");
        var owners = new[]
        {
            await CreateUserAsync(db, "timezone-batch-first@example.local", "user"),
            await CreateUserAsync(db, "timezone-batch-invalid@example.local", "user"),
            await CreateUserAsync(db, "timezone-batch-missing@example.local", "user"),
        };
        admin.FullName = "Synthetic Admin";
        for (var index = 0; index < owners.Length; index++)
        {
            owners[index].FullName = $"Synthetic Owner {index + 1}";
        }
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10);
        await SetUserTimeZoneAsync(db, admin.Id, "Europe/Samara");
        await SetUserTimeZoneAsync(db, owners[0].Id, "America/New_York");
        await SetUserTimeZoneAsync(db, owners[1].Id, "Unknown/Timezone");
        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: assigneeMode ? "adminByAssignee" : "personal");
        var personalCalendar = await CreateCalendarAsync(db, connection.Id, "personal-calendar", "Synthetic personal calendar");
        var calendars = new Dictionary<int, YandexCalendarSyncCalendar>();
        foreach (var owner in owners)
        {
            calendars.Add(owner.Id, await CreateCalendarAsync(db, connection.Id, $"owner-calendar-{owner.Id}", owner.FullName, owner.Id, $"https://caldav.yandex.ru/calendars/user/owner-{owner.Id}/"));
        }
        var firstDate = DateTime.UtcNow.Date.AddDays(7).AddHours(23).AddMinutes(30);
        var tasks = Enumerable.Range(0, 502).Select(index => new TaskJournalItem
        {
            ObjectName = "Synthetic timezone batch",
            Title = $"Timezone task {index + 1}",
            AssigneeUserId = owners[index % owners.Length].Id,
            AssigneeName = owners[index % owners.Length].FullName,
            DueAt = index % 2 == 0 ? firstDate : firstDate.AddDays(180),
            PlannedDurationMinutes = 60,
            Priority = "normal",
            Status = "new",
        }).ToArray();
        db.TaskJournalItems.AddRange(tasks);
        await db.SaveChangesAsync();
        for (var index = 0; index < tasks.Length; index++)
        {
            var calendar = assigneeMode ? calendars[tasks[index].AssigneeUserId!.Value] : personalCalendar;
            db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = tasks[index].Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = $"bulk-event-{index + 1}",
                YandexEventUrl = $"{calendar.CalendarUrl}bulk-event-{index + 1}.ics",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        var handler = new CalendarBatchColorProbe(new RecordingYandexHandler(), () => Task.CompletedTask);
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        db.ChangeTracker.Clear();

        async Task RunAsync()
        {
            await db.YandexCalendarConnections.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LastFullSyncAt, (DateTime?)null)
                .SetProperty(item => item.LastIncrementalSyncAt, (DateTime?)null));
            db.ChangeTracker.Clear();
            queryProbe.Reset();
            var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);
            Assert.Equal(502, result.ProcessedTasks);
            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
            Console.WriteLine($"CalendarTimezoneOwners provider=Yandex; assignee={assigneeMode}; settings={queryProbe.TimeZoneSelects}");
            Assert.InRange(queryProbe.TimeZoneSelects, 2, assigneeMode ? 3 : 2);
            if (!assigneeMode)
            {
                Assert.Equal(2, queryProbe.TimeZoneUserIds.Count);
                Assert.All(queryProbe.TimeZoneUserIds, ids => Assert.Equal(new[] { admin.Id }, ids));
            }
        }

        await RunAsync();
        Assert.Equal(502, handler.PutBodies.Count);
        for (var index = 0; index < tasks.Length; index++)
        {
            var task = tasks[index];
            var body = Assert.Single(handler.PutBodies.Single(pair => pair.Key.EndsWith($"/bulk-event-{index + 1}.ics", StringComparison.Ordinal)).Value);
            var zone = CalendarTimeZoneHelper.ResolveTimeZone(assigneeMode
                ? (task.AssigneeUserId == owners[0].Id ? "America/New_York" : null) : "Europe/Samara");
            Assert.Contains($"X-PAYPLANNER-TIMEZONE:{CalendarTimeZoneHelper.ResolveIanaTimeZoneId(zone)}", body);
            Assert.Contains($"DTSTART:{CalendarTimeZoneHelper.FormatUtcIcsDateTime(task.DueAt, zone)}", body);
            Assert.Contains($"DTEND:{CalendarTimeZoneHelper.FormatUtcIcsDateTime(task.DueAt.AddHours(1), zone)}", body);
        }
        await RunAsync();
        Assert.All(handler.PutBodies.Values, bodies => Assert.Single(bodies));
        await db.UserSettings.Where(item => item.UserId == owners[0].Id && item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Value, "Asia/Kamchatka"));
        await RunAsync();
        Assert.Equal(assigneeMode ? 168 : 0, handler.PutBodies.Values.Count(bodies => bodies.Count == 2));
        Assert.All(handler.PutBodies.Values, bodies => Assert.InRange(bodies.Count, 1, 2));
        await RunAsync();
        Assert.Equal(assigneeMode ? 168 : 0, handler.PutBodies.Values.Count(bodies => bodies.Count == 2));
        Assert.Equal(0, handler.DeleteCount);
        Assert.Equal(502, await db.YandexCalendarTaskLinks.CountAsync(item => item.SyncStatus == "active" && item.LastError == null));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncDueConnectionsAsync_BoundsTimezoneReadsFor502ImportsAndRefreshesOwnerProfile(bool assigneeCalendar)
    {
        var queryProbe = new CalendarBatchQueryProbe("Yandex");
        await using var db = await CreateContextAsync(queryProbe);
        var owner = await CreateUserAsync(db, "timezone-import-owner@example.local", "user");
        var actor = assigneeCalendar ? await CreateUserAsync(db, "timezone-import-admin@example.local", "admin") : owner;
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10);
        await SetUserTimeZoneAsync(db, owner.Id, "Europe/Samara");
        if (assigneeCalendar)
        {
            await SetUserTimeZoneAsync(db, actor.Id, "Asia/Kamchatka");
        }
        var connection = await CreateConnectionAsync(db, actor.Id, calendarMode: assigneeCalendar ? "adminByAssignee" : "personal");
        var calendar = await CreateCalendarAsync(db, connection.Id, "timezone-import", "Synthetic import calendar",
            assigneeCalendar ? owner.Id : null);
        calendar.SyncToken = "synthetic-existing-sync";
        // Import deliberately covers linked tasks beyond the export window, so
        // these incremental changes are legal without full-sync date filters,
        // and setting reads cannot be attributed to export upserts.
        var dueAt = DateTime.UtcNow.Date.AddDays(400).AddHours(9);
        var tasks = Enumerable.Range(1, 502).Select(index => new TaskJournalItem
        {
            ObjectName = "Synthetic timezone import",
            Title = $"Before import {index}",
            AssigneeUserId = owner.Id,
            AssigneeName = owner.FullName,
            DueAt = dueAt,
            PlannedDurationMinutes = 60,
            Priority = "normal",
            Status = "new",
        }).ToArray();
        db.TaskJournalItems.AddRange(tasks);
        await db.SaveChangesAsync();
        for (var index = 0; index < tasks.Length; index++)
        {
            db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = tasks[index].Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = $"import-event-{index + 1}",
                YandexEventUrl = $"{calendar.CalendarUrl}import-event-{index + 1}.ics",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        var handler = new CalendarTimezoneImportProbe(new RecordingYandexHandler(), google: false);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        void PrepareResponse(int round)
        {
            handler.YandexReport = BuildYandexReportResponseForEvents(tasks.Select((task, index) =>
                (calendar.CalendarUrl, $"import-event-{index + 1}", $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:import-event-{index + 1}
DTSTART:{task.DueAt.AddDays(round + 1):yyyyMMdd'T'HHmmss'Z'}
DTEND:{task.DueAt.AddDays(round + 1).AddHours(1):yyyyMMdd'T'HHmmss'Z'}
SUMMARY:Imported {round}-{index + 1}
X-PAYPLANNER-TASK-ID:{task.Id}
X-PAYPLANNER-HASH:synthetic-import-{round}-{index + 1}
END:VEVENT
END:VCALENDAR
""")).ToArray());
        }

        async Task RunAsync(int round, bool unchanged)
        {
            await db.YandexCalendarConnections.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LastFullSyncAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(item => item.LastIncrementalSyncAt, (DateTime?)DateTime.UtcNow.AddHours(-1)));
            db.ChangeTracker.Clear();
            if (!unchanged)
            {
                PrepareResponse(round);
            }
            queryProbe.Reset();
            handler.ResetReads();
            var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(0, result.ProcessedTasks);
            Assert.Equal(unchanged ? 0 : 502, result.ImportedEvents);
            Assert.Equal(1, handler.EventListReads);
            var updatedTasks = await db.TaskJournalItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
            Assert.Equal(502, updatedTasks.Count);
            Assert.All(updatedTasks, task => Assert.Equal(dueAt.AddDays(round + 1), task.DueAt));
            Assert.All(updatedTasks, task => Assert.Equal(60, task.PlannedDurationMinutes));
            Assert.All(updatedTasks, task => Assert.Equal("new", task.Status));
            Assert.All(updatedTasks, task => Assert.Equal(owner.Id, task.AssigneeUserId));
            if (!unchanged)
            {
                var logs = await db.YandexCalendarSyncLogs.AsNoTracking()
                    .Where(item => item.Direction == "yandex-to-crm" && item.Operation == "update-task")
                    .OrderByDescending(item => item.Id).Take(502).ToListAsync();
                Assert.Equal(502, logs.Count);
                var zone = CalendarTimeZoneHelper.ResolveTimeZone(round == 0 ? "Europe/Samara" : "America/New_York");
                var expectedTime = CalendarTimeZoneHelper.FormatUserDateTime(dueAt.AddDays(round + 1), zone);
                Assert.All(logs, log =>
                {
                    using var details = JsonDocument.Parse(log.DetailsJson!);
                    Assert.Contains(expectedTime, details.RootElement.GetProperty("detail").GetString());
                });
            }
            Console.WriteLine($"CalendarTimezoneImport provider=Yandex; assignee={assigneeCalendar}; round={round}; unchanged={unchanged}; settings={queryProbe.TimeZoneSelects}");
            Assert.Equal((assigneeCalendar ? 1 : 0) + (unchanged ? 0 : 2), queryProbe.TimeZoneSelects);
        }

        await RunAsync(0, unchanged: false);
        await db.UserSettings.Where(item => item.UserId == owner.Id && item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Value, "America/New_York"));
        await RunAsync(1, unchanged: false);
        await RunAsync(1, unchanged: true);
    }

    private static async Task SetUserTimeZoneAsync(PaymentContext db, int userId, string timeZoneId)
    {
        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncDueConnectionsAsync_PersistsImportedTaskBeforeReadingExportPage(bool movesOutsideWindow)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "import-export-owner@example.local", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "timezone-import", "Synthetic import/export calendar");
        var dueAt = DateTime.UtcNow.Date.AddDays(14).AddHours(9);
        var task = await CreateTaskAsync(db, user.Id, "Before import", dueAt: dueAt);
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "import-event-1",
            YandexEventUrl = $"{calendar.CalendarUrl}import-event-1.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var inner = new RecordingYandexHandler();
        var handler = new CalendarTimezoneImportProbe(inner, google: false);
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        var initialWrites = inner.Requests.Count(request => request.Method == HttpMethod.Put);
        var importedDueAt = movesOutsideWindow ? dueAt.AddDays(400) : dueAt.AddDays(1);
        handler.YandexReport = BuildYandexReportResponse(calendar.CalendarUrl, "import-event-1", $"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:import-event-1
DTSTART:{importedDueAt:yyyyMMdd'T'HHmmss'Z'}
DTEND:{importedDueAt.AddHours(1):yyyyMMdd'T'HHmmss'Z'}
SUMMARY:After import
X-PAYPLANNER-TASK-ID:{task.Id}
X-PAYPLANNER-HASH:synthetic-import-export-hash
END:VEVENT
END:VCALENDAR
""");
        db.ChangeTracker.Clear();

        var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.ImportedEvents);
        Assert.Equal(movesOutsideWindow ? 0 : 1, result.ProcessedTasks);
        var saved = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(importedDueAt, saved.DueAt);
        Assert.Equal("After import", saved.Title);
        Assert.Equal(initialWrites, inner.Requests.Count(request => request.Method == HttpMethod.Put));
        db.ChangeTracker.Clear();
        var repeat = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
        Assert.Equal(0, repeat.ErrorCount);
        Assert.Equal(0, repeat.ImportedEvents);
        Assert.Equal(initialWrites, inner.Requests.Count(request => request.Method == HttpMethod.Put));
        Assert.Equal(1, await db.YandexCalendarSyncLogs.CountAsync(item => item.Direction == "yandex-to-crm" && item.Operation == "update-task"));
    }


    [Theory]
    [InlineData("normal")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("insert")]
    public async Task SyncDueConnectionsAsync_Cleans502OldLinksInBoundedPagesAndPreservesUnrelatedState(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new CalendarBatchQueryProbe("Yandex");
        await using var db = await CreateContextAsync(probe);
        var user = await CreateUserAsync(db, "old-events-owner@example.local", "user");
        var hidden = await CreateUserAsync(db, "old-events-other@example.local", "user");

        var cutoff = DateTime.UtcNow.Date;
        var connection = await CreateConnectionAsync(db, user.Id, syncFromDate: cutoff);
        var calendar = await CreateCalendarAsync(db, connection.Id, "bulk-old-calendar", "Synthetic old events");
        var other = await CreateConnectionAsync(db, hidden.Id, syncFromDate: cutoff);
        other.SyncEnabled = false;
        var otherCalendar = await CreateCalendarAsync(db, other.Id, "other-calendar", "Synthetic other connection");
        var tasks = Enumerable.Range(1, 502).Select(index => new TaskJournalItem
        {
            Title = $"Old task {index}",
            ObjectName = "Synthetic cleanup",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = cutoff.AddDays(-2),
            PlannedDurationMinutes = 60,
            Priority = "normal",
            Status = index % 2 == 0 ? "new" : "cancelled",
        }).ToArray();
        db.TaskJournalItems.AddRange(tasks);
        await db.SaveChangesAsync();
        for (var index = 0; index < tasks.Length; index++)
        {
            db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = tasks[index].Id,
                YandexCalendarId = calendar.YandexCalendarId,
                YandexEventId = $"bulk-event-{index + 1}",
                YandexEventUrl = $"{calendar.CalendarUrl}bulk-event-{index + 1}.ics",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        var protectedTask = await CreateTaskAsync(db, hidden.Id, "Protected task at cutoff", dueAt: cutoff);
        var protectedLink = new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = protectedTask.Id,
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "protected-event",
            YandexEventUrl = $"{calendar.CalendarUrl}protected-event.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.YandexCalendarTaskLinks.Add(protectedLink);
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = other.Id,
            CalendarSyncCalendarId = otherCalendar.Id,
            TaskJournalItemId = tasks[0].Id,
            YandexCalendarId = otherCalendar.YandexCalendarId,
            YandexEventId = "other-event",
            YandexEventUrl = $"{otherCalendar.CalendarUrl}other-event.ics",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var firstIds = tasks.Take(500).Select(task => task.Id).ToArray();
        db.ChangeTracker.Clear();
        var callerLink = await db.YandexCalendarTaskLinks.SingleAsync(link => link.Id == protectedLink.Id);
        var callerTask = await db.TaskJournalItems.SingleAsync(task => task.Id == protectedTask.Id);
        YandexCalendarTaskLink? lateCallerLink = null;
        var inner = new RecordingYandexHandler();
        var handler = new CalendarBatchDeleteProbe(inner, scenario == "failure", async () =>
        {
            var savedFirstPage = await db.YandexCalendarTaskLinks.AsNoTracking()
                .CountAsync(link => link.ConnectionId == connection.Id && firstIds.Contains(link.TaskJournalItemId) && link.SyncStatus == "deleted");
            Console.WriteLine($"CalendarWindowBoundary provider=Yandex; scenario={scenario}; saved={savedFirstPage}; tasks={db.ChangeTracker.Entries<TaskJournalItem>().Count()}; links={db.ChangeTracker.Entries<YandexCalendarTaskLink>().Count()}; metadata={probe.LogMetadataSelects}");
            Assert.Equal(500, savedFirstPage);
            Assert.Equal(3, db.ChangeTracker.Entries<YandexCalendarTaskLink>().Count());
            Assert.Empty(db.ChangeTracker.Entries<YandexCalendarSyncLog>());
            Assert.Same(callerTask, Assert.Single(db.ChangeTracker.Entries<TaskJournalItem>()).Entity);
            Assert.Equal(EntityState.Unchanged, db.Entry(callerLink).State);
            if (scenario == "insert")
            {
                var late = await CreateTaskAsync(db, user.Id, "Added during cleanup", dueAt: cutoff.AddDays(-1));
                lateCallerLink = new YandexCalendarTaskLink
                {
                    ConnectionId = connection.Id,
                    CalendarSyncCalendarId = calendar.Id,
                    TaskJournalItemId = late.Id,
                    YandexCalendarId = calendar.YandexCalendarId,
                    YandexEventId = "bulk-event-503",
                    YandexEventUrl = $"{calendar.CalendarUrl}bulk-event-503.ics",
                    SyncStatus = "active",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                db.YandexCalendarTaskLinks.Add(lateCallerLink);
                await db.SaveChangesAsync();
            }
            if (scenario == "cancel")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        });
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        probe.Reset();
        if (scenario == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncDueConnectionsAsync(10, cancellation.Token));
        }
        else
        {
            var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
            Assert.Equal(0, result.ProcessedTasks);
            Assert.Equal(scenario == "failure" ? 1 : 0, result.ErrorCount);
        }
        Assert.Equal(new[] { 500, 500 }, probe.CleanupPageLimits);
        Assert.InRange(probe.LogMetadataSelects, 0, 4);
        Assert.Equal(scenario == "cancel" ? 500 : scenario == "failure" ? 501 : 502,
            await db.YandexCalendarTaskLinks.CountAsync(link => link.ConnectionId == connection.Id && link.SyncStatus == "deleted"));
        Assert.Equal(EntityState.Unchanged, db.Entry(callerLink).State);
        Assert.Equal(EntityState.Unchanged, db.Entry(callerTask).State);
        if (lateCallerLink is not null)
        {
            Assert.Equal(EntityState.Unchanged, db.Entry(lateCallerLink).State);
        }
        Assert.Equal(502, await db.TaskJournalItems.CountAsync(task => task.Title.StartsWith("Old task ")
            && task.DueAt == cutoff.AddDays(-2) && (task.Status == "new" || task.Status == "cancelled")));
        Assert.Equal(2, await db.YandexCalendarTaskLinks.CountAsync(link => link.YandexEventId == "protected-event"
            || link.YandexEventId == "other-event"));
        Assert.Equal(2, await db.YandexCalendarTaskLinks.CountAsync(link => (link.YandexEventId == "protected-event"
            || link.YandexEventId == "other-event") && link.SyncStatus == "active"));
        Assert.DoesNotContain(inner.Requests, request => request.Method == HttpMethod.Put);
        Console.WriteLine($"CalendarWindow provider=Yandex; scenario={scenario}; phase=first; pages={string.Join(',', probe.CleanupPageLimits)}; metadata={probe.LogMetadataSelects}");
        db.ChangeTracker.Clear();
        probe.Reset();
        var retry = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
        Assert.Equal(0, retry.ErrorCount);
        Assert.Equal(scenario == "insert" ? 503 : 502,
            await db.YandexCalendarTaskLinks.CountAsync(link => link.ConnectionId == connection.Id && link.SyncStatus == "deleted"));
        Assert.Equal(scenario == "normal" ? Array.Empty<int>() : new[] { 500 }, probe.CleanupPageLimits);
        Assert.InRange(probe.LogMetadataSelects, 0, 4);
        Assert.All(handler.DeleteAttempts, pair => Assert.Equal((scenario is "cancel" or "failure")
            && pair.Key.Contains("bulk-event-501", StringComparison.Ordinal) ? 2 : 1, pair.Value));
        Assert.DoesNotContain(handler.DeleteAttempts.Keys, path => path.Contains("protected-event") || path.Contains("other-event"));
        Assert.Equal(scenario == "insert" ? 503 : 502, handler.DeleteAttempts.Count);
        Console.WriteLine($"CalendarWindow provider=Yandex; scenario={scenario}; phase=retry; pages={string.Join(',', probe.CleanupPageLimits)}; metadata={probe.LogMetadataSelects}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncDueConnectionsAsync_Reads502CalendarOwnersInBoundedPagesAndDefersNewOwner(bool insertDuringRun)
    {
        var probe = new CalendarBatchQueryProbe("Yandex");
        await using var db = await CreateContextAsync(probe);
        var admin = await CreateUserAsync(db, "owners-admin@example.local", "admin");
        admin.IsEmployee = false;
        var regular = await CreateUserAsync(db, "owners-role@example.local", "user");
        regular.IsActive = false;

        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var employees = Enumerable.Range(1, 502).Select(index => new User
        {
            Email = $"owner-{index}@example.local",
            FullName = $"Owner {503 - index:D4}",
            PasswordHash = "hash",
            RoleId = index == 502 ? admin.RoleId : regular.RoleId,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true,
        }).ToArray();
        var rejected = new[]
        {
            new User { Email = "inactive-owner@example.local", FullName = "Inactive owner", PasswordHash = "hash", RoleId = regular.RoleId, IsActive = false, IsApproved = true, IsEmployee = true },
            new User { Email = "unapproved-owner@example.local", FullName = "Unapproved owner", PasswordHash = "hash", RoleId = regular.RoleId, IsActive = true, IsApproved = false, IsEmployee = true },
            new User { Email = "nonemployee-admin@example.local", FullName = "Other admin", PasswordHash = "hash", RoleId = admin.RoleId, IsActive = true, IsApproved = true, IsEmployee = false },
        };
        db.Users.AddRange(employees);
        db.Users.AddRange(rejected);
        await db.SaveChangesAsync();
        for (var index = 0; index < employees.Length; index++)
        {
            db.YandexCalendarSyncCalendars.Add(new YandexCalendarSyncCalendar
            {
                ConnectionId = connection.Id,
                OwnerUserId = employees[index].Id,
                YandexCalendarId = $"owner-calendar-{index + 1}",
                Name = $"PayPlanner - {employees[index].FullName}",
                CalendarUrl = $"https://caldav.yandex.ru/calendars/user/owner-calendar-{index + 1}/",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        User? late = null;
        var handler = new CalendarOwnerPageProbe(new RecordingYandexHandler(), async () =>
        {
            if (insertDuringRun)
            {
                late = new User { Email = "late-owner@example.local", FullName = "Late owner", PasswordHash = "hash", RoleId = regular.RoleId, IsActive = true, IsApproved = true, IsEmployee = true };
                db.Users.Add(late);
                await db.SaveChangesAsync();
            }
        });
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        probe.Reset();
        var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.ProcessedTasks);
        Assert.Equal(new[] { 500, 500 }, probe.OwnerPageLimits);
        Assert.Equal(2, probe.TimeZoneSelects);
        var ownerIds = employees.Select(user => user.Id).ToArray();
        Assert.Equal(502, await db.YandexCalendarSyncCalendars.CountAsync(calendar => calendar.ConnectionId == connection.Id
            && calendar.OwnerUserId.HasValue && ownerIds.Contains(calendar.OwnerUserId.Value)));
        Assert.Equal(502, await db.YandexCalendarSyncCalendars.CountAsync());
        Assert.All(await db.YandexCalendarSyncCalendars.AsNoTracking().ToListAsync(), calendar =>
            Assert.Equal($"PayPlanner - {employees.Single(owner => owner.Id == calendar.OwnerUserId).FullName}", calendar.Name));
        if (late is not null)
        {
            Assert.False(await db.YandexCalendarSyncCalendars.AnyAsync(calendar => calendar.OwnerUserId == late.Id));
        }
        Console.WriteLine($"CalendarOwners provider=Yandex; insert={insertDuringRun}; phase=first; pages={string.Join(',', probe.OwnerPageLimits)}; settings={probe.TimeZoneSelects}");
        db.ChangeTracker.Clear();
        probe.Reset();
        var repeat = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
        Assert.Equal(0, repeat.ErrorCount);
        Assert.Equal(new[] { 500, 500 }, probe.OwnerPageLimits);
        Assert.Equal(2, probe.TimeZoneSelects);
        Assert.Equal(insertDuringRun ? 503 : 502, await db.YandexCalendarSyncCalendars.CountAsync());
        if (late is not null)
        {
            Assert.Equal("PayPlanner - Late owner", (await db.YandexCalendarSyncCalendars.AsNoTracking().SingleAsync(calendar => calendar.OwnerUserId == late.Id)).Name);
        }
        var rejectedIds = rejected.Select(user => user.Id).Append(admin.Id).Append(regular.Id).ToArray();
        Assert.False(await db.YandexCalendarSyncCalendars.AnyAsync(calendar => calendar.OwnerUserId.HasValue && rejectedIds.Contains(calendar.OwnerUserId.Value)));
        Console.WriteLine($"CalendarOwners provider=Yandex; insert={insertDuringRun}; phase=repeat; pages={string.Join(',', probe.OwnerPageLimits)}; settings={probe.TimeZoneSelects}");
    }

    private static async Task<YandexCalendarConnection> CreateConnectionAsync(
        PaymentContext db,
        int userId,
        DateTime? syncFromDate = null,
        string calendarMode = "personal")
    {
        var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1");
        var connection = new YandexCalendarConnection
        {
            UserId = userId,
            YandexUserId = "yandex-user",
            YandexEmail = "yandex@example.com",
            AccessTokenProtected = protector.Protect("access-token"),
            RefreshTokenProtected = protector.Protect("refresh-token"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1),
            Scope = "login:email calendar:all",
            CalendarMode = calendarMode,
            ConflictResolutionMode = "latestWins",
            SyncFromDate = syncFromDate,
            IsEnabled = true,
            SyncEnabled = true,
        };
        db.YandexCalendarConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task SetCalendarSyncSettingsAsync(
        PaymentContext db,
        int manualFullSyncCooldownMinutes,
        string completedTaskCalendarAction = "color")
    {
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "calendarSync.settings",
            Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
            {
                GoogleCalendarSyncEnabled = false,
                YandexCalendarSyncEnabled = true,
                SyncMode = "hybrid",
                ConflictResolutionMode = "latestWins",
                UserControlMode = "userManaged",
                InactiveUserAction = "cleanupExternalData",
                RequireManualConfirmation = true,
                ManualFullSyncCooldownMinutes = manualFullSyncCooldownMinutes,
                CompletedTaskCalendarAction = completedTaskCalendarAction,
                CompletedTaskGoogleColorId = "8",
            }),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<YandexCalendarSyncCalendar> CreateCalendarAsync(
        PaymentContext db,
        int connectionId,
        string yandexCalendarId,
        string name,
        int? ownerUserId = null,
        string? calendarUrl = null)
    {
        var calendar = new YandexCalendarSyncCalendar
        {
            ConnectionId = connectionId,
            OwnerUserId = ownerUserId,
            YandexCalendarId = yandexCalendarId,
            Name = name,
            CalendarUrl = calendarUrl ?? "https://caldav.yandex.ru/calendars/user/payplanner/",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.YandexCalendarSyncCalendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar;
    }

    private static async Task<TaskJournalItem> CreateTaskAsync(PaymentContext db, int assigneeUserId, string title, DateTime? dueAt = null)
    {
        var task = new TaskJournalItem
        {
            ObjectName = "Тест",
            Title = title,
            AssigneeUserId = assigneeUserId,
            AssigneeName = "User",
            DueAt = dueAt ?? DateTime.UtcNow.AddDays(1),
            PlannedDurationMinutes = 60,
            Status = "new",
            Priority = "normal",
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private static string BuildYandexReportResponse(string calendarUrl, string eventId, string ics)
    {
        return BuildYandexReportResponseForEvents((calendarUrl, eventId, ics));
    }

    private static string BuildYandexReportResponseForEvents(params (string CalendarUrl, string EventId, string Ics)[] events)
    {
        var responses = string.Join(
            Environment.NewLine,
            events.Select(item =>
            {
                var href = $"{item.CalendarUrl}{item.EventId}.ics";
                return $"""
  <D:response>
    <D:href>{href}</D:href>
    <D:propstat>
      <D:prop>
        <D:getetag>"etag-{item.EventId}"</D:getetag>
        <C:calendar-data><![CDATA[{item.Ics}]]></C:calendar-data>
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
""";
            }));

        return $"""
<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
{responses}
</D:multistatus>
""";
    }

    private static string BuildYandexCalendarHomeResponse(string calendarHref, string displayName, bool includeSupportedComponents = true)
    {
        var componentSet = includeSupportedComponents
            ? """
        <C:supported-calendar-component-set>
          <C:comp name="VEVENT" />
        </C:supported-calendar-component-set>
"""
            : string.Empty;
        return $"""
<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:response>
    <D:href>{calendarHref}</D:href>
    <D:propstat>
      <D:prop>
        <D:displayname>{WebUtility.HtmlEncode(displayName)}</D:displayname>
        <D:resourcetype>
          <D:collection />
          <C:calendar />
        </D:resourcetype>
{componentSet}
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
</D:multistatus>
""";
    }

    private static string BuildYandexCalendarHomeResponseForCalendars(
        params (string CalendarHref, string DisplayName)[] calendars)
    {
        var responses = string.Join(
            Environment.NewLine,
            calendars.Select(calendar => $"""
  <D:response>
    <D:href>{calendar.CalendarHref}</D:href>
    <D:propstat>
      <D:prop>
        <D:displayname>{WebUtility.HtmlEncode(calendar.DisplayName)}</D:displayname>
        <D:resourcetype>
          <D:collection />
          <C:calendar />
        </D:resourcetype>
        <C:supported-calendar-component-set>
          <C:comp name="VEVENT" />
        </C:supported-calendar-component-set>
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
"""));
        return $"""
<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
{responses}
</D:multistatus>
""";
    }

    private static HttpRequest CreateHttpRequest(int userId, string role = "user")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("crm.example.com");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        }, "Test"));
        return context.Request;
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler? handler = null)
        {
            _handler = handler ?? new RecordingYandexHandler();
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class RecordingYandexHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _deleteStatusCode;
        public string? ReportResponse { get; set; }
        private readonly string? _reportCalendarPath;
        private readonly string? _calendarHomeResponse;
        private readonly List<(string Href, string DisplayName)> _createdCalendars = [];

        public RecordingYandexHandler(
            HttpStatusCode deleteStatusCode = HttpStatusCode.NoContent,
            string? reportResponse = null,
            string? calendarHomeResponse = null,
            string? reportCalendarPath = null)
        {
            _deleteStatusCode = deleteStatusCode;
            ReportResponse = reportResponse;
            _reportCalendarPath = reportCalendarPath;
            _calendarHomeResponse = calendarHomeResponse;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));

            if (string.Equals(request.Method.Method, "MKCALENDAR", StringComparison.Ordinal))
            {
                var displayName = ReadMkCalendarDisplayName(request);
                var href = request.RequestUri!.AbsolutePath;
                _createdCalendars.Add((href, displayName));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
                });
            }

            if (string.Equals(request.Method.Method, "PROPPATCH", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "application/xml"),
                });
            }

            if (string.Equals(request.Method.Method, "PROPFIND", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent(_calendarHomeResponse ?? BuildYandexCalendarHomeResponse(_createdCalendars), Encoding.UTF8, "application/xml"),
                });
            }

            if (string.Equals(request.Method.Method, "REPORT", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent(_reportCalendarPath is null || request.RequestUri?.AbsolutePath == _reportCalendarPath
                        ? ReportResponse ?? string.Empty : string.Empty, Encoding.UTF8, "application/xml"),
                });
            }

            if (request.Method == HttpMethod.Put)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
                });
            }

            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(_deleteStatusCode)
                {
                    Content = new StringContent("delete failed", Encoding.UTF8, "text/plain"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found", Encoding.UTF8, "text/plain"),
            });
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri);
            if (source.Content is not null)
            {
                var body = source.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                clone.Content = new StringContent(body, Encoding.UTF8, source.Content.Headers.ContentType?.MediaType ?? "text/plain");
            }

            return clone;
        }

        private static string ReadMkCalendarDisplayName(HttpRequestMessage request)
        {
            if (request.Content is null)
            {
                return "PayPlanner задачи";
            }

            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            try
            {
                var document = XDocument.Parse(body);
                XNamespace dav = "DAV:";
                return document.Descendants(dav + "displayname").FirstOrDefault()?.Value ?? "PayPlanner задачи";
            }
            catch
            {
                return "PayPlanner задачи";
            }
        }

        private static string BuildYandexCalendarHomeResponse(IReadOnlyList<(string Href, string DisplayName)> calendars)
        {
            var items = calendars.Select(item => $"""
  <D:response>
    <D:href>{item.Href}</D:href>
    <D:propstat>
      <D:prop>
        <D:displayname>{WebUtility.HtmlEncode(item.DisplayName)}</D:displayname>
        <D:resourcetype>
          <D:collection />
          <C:calendar />
        </D:resourcetype>
        <C:supported-calendar-component-set>
          <C:comp name="VEVENT" />
        </C:supported-calendar-component-set>
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
""");
            return $"""
<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
{string.Join(Environment.NewLine, items)}
</D:multistatus>
""";
        }
    }

    private sealed class ThrowingYandexHandler(Func<HttpRequestMessage, Exception> resolveException) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(resolveException(request));
    }

    private sealed class TransientReportYandexHandler : HttpMessageHandler
    {
        public int ReportRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Method.Method, "REPORT", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
                });
            }

            ReportRequestCount++;
            var statusCode = ReportRequestCount == 1
                ? HttpStatusCode.GatewayTimeout
                : (HttpStatusCode)207;
            var payload = ReportRequestCount == 1 ? "Gateway time out" : string.Empty;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class TransientCalendarHomeYandexHandler : HttpMessageHandler
    {
        public int CalendarListRequestCount { get; private set; }
        public int CreateCalendarRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.Method.Method, "PROPFIND", StringComparison.Ordinal))
            {
                CalendarListRequestCount++;
                if (CalendarListRequestCount == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                    {
                        Content = new StringContent("Gateway time out", Encoding.UTF8, "text/plain"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent(
                        BuildYandexCalendarHomeResponse(
                            "/calendars/yandex%40example.com/existing-calendar/",
                            "PayPlanner задачи"),
                        Encoding.UTF8,
                        "application/xml"),
                });
            }

            if (string.Equals(request.Method.Method, "MKCALENDAR", StringComparison.Ordinal))
            {
                CreateCalendarRequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
            }

            var statusCode = request.Method == HttpMethod.Put
                ? HttpStatusCode.Created
                : (HttpStatusCode)207;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            });
        }
    }
}
