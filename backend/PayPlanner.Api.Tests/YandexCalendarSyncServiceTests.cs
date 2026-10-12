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

public class YandexCalendarSyncServiceTests
{
    private static readonly IDataProtectionProvider TestDataProtectionProvider =
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pp-yandex-calendar-tests")));

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
                db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
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
                });
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
        Assert.Equal(cancelAfterFirstPage ? 501 : 502, handler.DeleteAttempts.Count);
        Assert.All(handler.DeleteAttempts.Values, count => Assert.Equal(1, count));
        Assert.Equal(cancelAfterFirstPage ? 500 : failOnce ? 501 : 502, await db.YandexCalendarTaskLinks.AsNoTracking().CountAsync(item => item.SyncStatus == "deleted"));
        Assert.Equal(insertDuringSync ? 503 : 502, await db.YandexCalendarTaskLinks.CountAsync());
        Assert.DoesNotContain(inner.Requests, request => request.Method == HttpMethod.Put);
        var taskIds = tasks.Select(item => item.Id).ToArray();
        Assert.Equal(502, await db.TaskJournalItems.CountAsync(item => taskIds.Contains(item.Id) && (item.Status == "done" || item.Status == "cancelled")));
        db.ChangeTracker.Clear();
        queryProbe.Reset();
        service = CreateService(db, new FakeHttpClientFactory(handler));

        var retry = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(insertDuringSync ? 503 : 502, retry.ProcessedTasks);
        Assert.Equal(0, retry.ErrorCount);
        Assert.Equal(insertDuringSync ? 503 : 502, await db.YandexCalendarTaskLinks.AsNoTracking().CountAsync(item => item.SyncStatus == "deleted"));
        Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
        Assert.InRange(queryProbe.LinkSelects, 2, 4);
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
        private readonly string? _reportResponse;
        private readonly string? _calendarHomeResponse;
        private readonly List<(string Href, string DisplayName)> _createdCalendars = [];

        public RecordingYandexHandler(
            HttpStatusCode deleteStatusCode = HttpStatusCode.NoContent,
            string? reportResponse = null,
            string? calendarHomeResponse = null)
        {
            _deleteStatusCode = deleteStatusCode;
            _reportResponse = reportResponse;
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
                    Content = new StringContent(_reportResponse ?? string.Empty, Encoding.UTF8, "application/xml"),
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
