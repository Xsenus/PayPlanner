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

    [Fact]
    public async Task DisconnectAsync_WhenYandexCleanupFails_KeepsConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-disconnect-error@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-yandex-disconnect-error", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler(HttpStatusCode.BadGateway)));

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
    public async Task SyncNowAsync_UsesAssigneeTimeZoneForAdminCalendarNearMidnight()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-yandex-timezone@example.com", "admin");
        var assignee = await CreateUserAsync(db, "assignee-yandex-timezone@example.com", "user");
        await SetUserTimeZoneAsync(db, admin.Id, "Europe/Moscow");
        await SetUserTimeZoneAsync(db, assignee.Id, "Asia/Kamchatka");
        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var task = await CreateTaskAsync(
            db,
            assignee.Id,
            "Near midnight in assignee timezone",
            new DateTime(2026, 6, 4, 11, 30, 0, DateTimeKind.Utc));
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
        Assert.Contains("DTSTART:20260604T113000Z", body);
        Assert.Contains("DTEND:20260604T123000Z", body);
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
    public async Task SyncTaskForConnectedUsersAsync_RefreshesStoredYandexCalendarUrl()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-refresh-url@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "payplanner-tasks", "PayPlanner Р·Р°РґР°С‡Рё");
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
    public async Task SyncTaskForConnectedUsersAsync_DeletesCompletedTaskEventWhenHideModeEnabled()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-yandex-completed-hide@example.com", "user");
        var connection = await CreateConnectionAsync(db, user.Id);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "hide");
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-yandex-completed-hide", "PayPlanner Р·Р°РґР°С‡Рё");
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "РЎРєСЂС‹С‚СЊ РіРѕС‚РѕРІСѓСЋ Р·Р°РґР°С‡Сѓ",
            new DateTime(2026, 6, 5, 9, 0, 0));
        task.Status = "done";
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

    private static YandexCalendarSyncService CreateService(PaymentContext db, IHttpClientFactory httpClientFactory)
    {
        return new YandexCalendarSyncService(
            db,
            new ConfigurationBuilder().Build(),
            httpClientFactory,
            TestDataProtectionProvider,
            NullLogger<YandexCalendarSyncService>.Instance);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string roleName)
    {
        var role = new Role { Name = roleName, Description = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
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
        string name)
    {
        var calendar = new YandexCalendarSyncCalendar
        {
            ConnectionId = connectionId,
            YandexCalendarId = yandexCalendarId,
            Name = name,
            CalendarUrl = "https://caldav.yandex.ru/calendars/user/payplanner/",
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
        var href = $"{calendarUrl}{eventId}.ics";
        return $"""
<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:response>
    <D:href>{href}</D:href>
    <D:propstat>
      <D:prop>
        <D:getetag>"etag-{eventId}"</D:getetag>
        <C:calendar-data><![CDATA[{ics}]]></C:calendar-data>
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
</D:multistatus>
""";
    }

    private static string BuildYandexCalendarHomeResponse(string calendarHref, string displayName)
    {
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
        <C:supported-calendar-component-set>
          <C:comp name="VEVENT" />
        </C:supported-calendar-component-set>
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
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
                return "PayPlanner Р·Р°РґР°С‡Рё";
            }

            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            try
            {
                var document = XDocument.Parse(body);
                XNamespace dav = "DAV:";
                return document.Descendants(dav + "displayname").FirstOrDefault()?.Value ?? "PayPlanner Р·Р°РґР°С‡Рё";
            }
            catch
            {
                return "PayPlanner Р·Р°РґР°С‡Рё";
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
}
