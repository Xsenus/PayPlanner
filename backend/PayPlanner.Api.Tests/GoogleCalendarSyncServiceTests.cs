using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class GoogleCalendarSyncServiceTests
{
    private static readonly IDataProtectionProvider TestDataProtectionProvider =
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pp-google-tests")));

    [Fact]
    public async Task ServiceProvider_ResolvesGoogleCalendarSyncService()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(TestDataProtectionProvider);
        services.AddLogging();
        services.AddHttpClient();
        services.AddScoped<PaymentContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .Options;
            return new PaymentContext(options, useObjectAuditModel: false);
        });
        services.AddScoped<IGoogleCalendarSyncService, GoogleCalendarSyncService>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IGoogleCalendarSyncService>();

        Assert.IsType<GoogleCalendarSyncService>(service);
    }

    [Fact]
    public async Task StartAuthAsync_CreatesOfflineGoogleCalendarAuthorizationUrl()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "user@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var service = CreateService(db, new FakeHttpClientFactory());

        var result = await service.StartAuthAsync(
            user.Id,
            new StartGoogleCalendarAuthRequest { ReturnUrl = "/?ppTab=profile" },
            CreateHttpRequest(user.Id),
            CancellationToken.None);

        Assert.Contains("https://accounts.google.com/o/oauth2/v2/auth", result.AuthUrl);
        Assert.Contains("access_type=offline", result.AuthUrl);
        Assert.Contains("prompt=consent%20select_account", result.AuthUrl);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/calendar"), result.AuthUrl);
        Assert.True(await db.GoogleCalendarAuthRequests.AnyAsync(item => item.UserId == user.Id && item.State == result.State));
    }

    [Fact]
    public async Task StartAuthAsync_WhenProviderDisabledRejectsConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "disabled-google@example.com", "user");
        var now = DateTime.UtcNow;
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "auth.google.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.google.clientId", Value = "client-id", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.google.clientSecret", Value = "client-secret", CreatedAt = now, UpdatedAt = now },
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
                new StartGoogleCalendarAuthRequest { ReturnUrl = "/?ppTab=profile" },
                CreateHttpRequest(user.Id),
                CancellationToken.None));

        Assert.Contains("Google Calendar отключен администратором", error.Message);
        var status = await service.GetStatusAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);
        Assert.False(status.IsAvailable);
        Assert.True(status.GoogleOAuthReady);
    }

    [Fact]
    public async Task SyncNowAsync_CreatesCalendarEventAndTaskLink()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Подготовить документы");
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Contains(await db.GoogleCalendarSyncCalendars.ToListAsync(), item => item.Name == "PayPlanner задачи");
        Assert.Contains(await db.GoogleCalendarTaskLinks.ToListAsync(), item => item.TaskJournalItemId == task.Id && item.GoogleEventId == "event-1");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/calendars"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/events"));
    }

    [Fact]
    public async Task SyncNowAsync_UsesSyncFromDateToSkipOlderTasks()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-sync-from@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var syncFromDate = DateTime.UtcNow.Date.AddDays(1);
        await CreateConnectionAsync(db, user.Id, syncFromDate: syncFromDate);
        var oldTask = await CreateTaskAsync(db, user.Id, "Старая задача", dueAt: syncFromDate.AddDays(-1).AddHours(10));
        var newTask = await CreateTaskAsync(db, user.Id, "Новая задача", dueAt: syncFromDate.AddDays(1).AddHours(10));
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.False(await db.GoogleCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == oldTask.Id));
        Assert.True(await db.GoogleCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == newTask.Id));
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncNowAsync_TreatsUnspecifiedTaskDueAtAsUtcWhenCreatingGoogleEvent()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-unspecified-google@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, user.Id);
        await CreateTaskAsync(
            db,
            user.Id,
            "Дата без Kind не должна сдвигаться",
            dueAt: new DateTime(2026, 6, 5, 9, 0, 0, DateTimeKind.Unspecified));
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        var createEventRequest = handler.Requests.Single(request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
        var body = await createEventRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("2026-06-05T09:00:00.0000000Z", document.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("2026-06-05T10:00:00.0000000Z", document.RootElement.GetProperty("end").GetProperty("dateTime").GetString());
    }

    [Fact]
    public async Task SyncNowAsync_AppliesGoogleMoveWithoutPushingTaskBackOnNextCycle()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-move@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-google-move", "PayPlanner задачи");
        var task = await CreateTaskAsync(db, user.Id, "Перенесенная задача", dueAt: new DateTime(2026, 6, 5, 8, 0, 0, DateTimeKind.Utc));
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-google-move",
            SyncStatus = "active",
            LastCrmHash = "old-crm-hash",
            LastGoogleHash = "old-google-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var googleEvents = new object[]
        {
            new
            {
                id = "event-google-move",
                etag = "\"etag-google-move\"",
                status = "confirmed",
                summary = "[Новая] Перенесенная задача",
                start = new { dateTime = "2026-06-05T16:00:00+07:00" },
                end = new { dateTime = "2026-06-05T17:00:00+07:00" },
                extendedProperties = new
                {
                    @private = new Dictionary<string, string>
                    {
                        ["payPlannerTaskId"] = task.Id.ToString(),
                    },
                },
            },
        };
        var handler = new RecordingGoogleHandler(eventItems: googleEvents);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var firstResult = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);
        db.ChangeTracker.Clear();
        var secondResult = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, firstResult.ImportedEvents);
        Assert.Equal(0, firstResult.ErrorCount);
        Assert.Equal(0, secondResult.ErrorCount);
        var updatedTask = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal(new DateTime(2026, 6, 5, 9, 0, 0, DateTimeKind.Utc), updatedTask.DueAt);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Put &&
            request.RequestUri!.AbsolutePath.Contains("/events/event-google-move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncNowAsync_RetriesGoogleRateLimitWhenListingEvents()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-rate-limit@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-rate-limit", "PayPlanner задачи");
        var handler = new RecordingGoogleHandler(eventListRateLimitCount: 1);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal)));
        Assert.DoesNotContain(await db.GoogleCalendarSyncLogs.ToListAsync(), log => log.Status == "error");
    }

    [Fact]
    public async Task SyncNowAsync_AdminConnectionCreatesCalendarByAssignee()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin@example.com", "admin");
        var assignee = await CreateUserAsync(db, "employee@example.com", "user", "Иван Петров");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        await CreateTaskAsync(db, assignee.Id, "Проверить задачу", "Иван Петров");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler()));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Contains(await db.GoogleCalendarSyncCalendars.ToListAsync(), item =>
            item.OwnerUserId == assignee.Id &&
            item.Name.Contains("Иван Петров", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteTaskForConnectedUsersAsync_RemovesLinkedGoogleEventAndWritesLog()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-delete@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Удалить из календаря");
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-delete", "PayPlanner задачи");
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-delete",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.DeleteTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-delete/events/event-delete", StringComparison.Ordinal));
        var link = await db.GoogleCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("deleted", link.SyncStatus);
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.ConnectionId == connection.Id &&
            item.TaskJournalItemId == task.Id &&
            item.Operation == "delete-event" &&
            item.Status == "success");
    }

    [Fact]
    public async Task DeleteTaskForConnectedUsersAsync_WhenGoogleFails_WritesErrorLog()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-delete-error@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Удалить с ошибкой Google");
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-delete-error", "PayPlanner задачи");
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-delete-error",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler(HttpStatusCode.BadGateway)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteTaskForConnectedUsersAsync(task.Id, CancellationToken.None));

        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.ConnectionId == connection.Id &&
            item.TaskJournalItemId == task.Id &&
            item.Operation == "delete-event" &&
            item.Status == "error");
        var link = await db.GoogleCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.False(string.IsNullOrWhiteSpace(link.LastError));
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_WhenTaskBeforeSyncFromDateDeletesExistingGoogleEvent()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-sync-from-delete@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var syncFromDate = DateTime.UtcNow.Date.AddDays(1);
        var connection = await CreateConnectionAsync(db, user.Id, syncFromDate: syncFromDate);
        var task = await CreateTaskAsync(db, user.Id, "Убрать из календаря", dueAt: syncFromDate.AddDays(-1).AddHours(10));
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-sync-from-delete", "PayPlanner задачи");
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-sync-from-delete",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-sync-from-delete/events/event-sync-from-delete", StringComparison.Ordinal));
        var link = await db.GoogleCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("deleted", link.SyncStatus);
    }

    [Fact]
    public async Task DisconnectAsync_RemovesExternalGoogleCalendarsBeforeConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-disconnect@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Отключить Google Calendar");
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-disconnect", "PayPlanner задачи");
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-disconnect",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var status = await service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Null(status.Connection);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-disconnect", StringComparison.Ordinal));
        Assert.False(await db.GoogleCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.False(await db.GoogleCalendarSyncCalendars.AnyAsync(item => item.GoogleCalendarId == "calendar-disconnect"));
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.Operation == "delete-calendar" &&
            item.Status == "success");
    }

    [Fact]
    public async Task DisconnectAsync_WhenGoogleCleanupFails_KeepsConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-disconnect-error@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-disconnect-error", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler(HttpStatusCode.BadGateway)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None));

        Assert.Contains("Не удалось отключить Google Calendar", error.Message);
        Assert.True(await db.GoogleCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.True(await db.GoogleCalendarSyncCalendars.AnyAsync(item => item.GoogleCalendarId == "calendar-disconnect-error"));
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.ConnectionId == connection.Id &&
            item.Operation == "delete-calendar" &&
            item.Status == "error");
    }

    [Fact]
    public async Task CleanupDeletedUserAsync_RemovesOwnAndAdminCalendars()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-cleanup@example.com", "admin");
        var user = await CreateUserAsync(db, "employee-cleanup@example.com", "user", "Иван Петров");
        await EnableGoogleSettingsAsync(db);
        var userConnection = await CreateConnectionAsync(db, user.Id);
        var adminConnection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        await CreateCalendarAsync(db, userConnection.Id, "calendar-user", "PayPlanner задачи");
        await CreateCalendarAsync(db, adminConnection.Id, "calendar-admin-user", "PayPlanner - Иван Петров", user.Id);
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.CleanupDeletedUserAsync(user.Id, admin.Id, CancellationToken.None);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-user", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-admin-user", StringComparison.Ordinal));
        Assert.False(await db.GoogleCalendarSyncCalendars.AnyAsync(item => item.GoogleCalendarId == "calendar-user"));
        Assert.False(await db.GoogleCalendarSyncCalendars.AnyAsync(item => item.GoogleCalendarId == "calendar-admin-user"));
        Assert.Equal(2, await db.GoogleCalendarSyncLogs.CountAsync(item => item.Operation == "delete-calendar" && item.Status == "success"));
    }

    [Fact]
    public async Task AdminTelyatnikovScenario_SyncsTasksDeletesEventAndCleansEmployeeCalendar()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "telyatnikov@example.com", "admin", "Телятников Илья Александрович");
        var employee = await CreateUserAsync(db, "employee-scenario@example.com", "user", "Иван Петров");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var employeeTask = await CreateTaskAsync(db, employee.Id, "Проверить документы сотрудника", employee.FullName);
        await CreateTaskAsync(db, admin.Id, "Проверить общую задачу администратора", admin.FullName);
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);
        await service.DeleteTaskForConnectedUsersAsync(employeeTask.Id, CancellationToken.None);
        await service.CleanupDeletedUserAsync(employee.Id, admin.Id, CancellationToken.None);

        Assert.Equal(2, result.ProcessedTasks);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/calendars", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath.Contains("/events/", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete && !request.RequestUri!.AbsolutePath.Contains("/events/", StringComparison.Ordinal));
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item => item.Operation == "create-event" && item.Status == "success");
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item => item.Operation == "delete-event" && item.Status == "success");
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item => item.Operation == "delete-calendar" && item.Status == "success");
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenAdminManagedRejectsRegularUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "google-admin-policy@example.com", "user");
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
                new UpdateGoogleCalendarSettingsRequest { SyncEnabled = false },
                CreateHttpRequest(user.Id),
                CancellationToken.None));

        Assert.Contains("администратором", error.Message);
    }

    private static GoogleCalendarSyncService CreateService(PaymentContext db, IHttpClientFactory httpClientFactory)
    {
        return new GoogleCalendarSyncService(
            db,
            new ConfigurationBuilder().Build(),
            httpClientFactory,
            TestDataProtectionProvider,
            NullLogger<GoogleCalendarSyncService>.Instance);
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

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string roleName, string fullName = "User")
    {
        var role = new Role { Name = roleName, Description = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User
        {
            Email = email,
            FullName = fullName,
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task EnableGoogleSettingsAsync(PaymentContext db)
    {
        var now = DateTime.UtcNow;
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "auth.google.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.google.clientId", Value = "client-id", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "auth.google.clientSecret", Value = "client-secret", CreatedAt = now, UpdatedAt = now },
            new SystemSetting
            {
                Key = "calendarSync.settings",
                Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
                {
                    GoogleCalendarSyncEnabled = true,
                    YandexCalendarSyncEnabled = false,
                    SyncMode = "hybrid",
                    ConflictResolutionMode = "latestWins",
                    UserControlMode = "userManaged",
                    InactiveUserAction = "cleanupExternalData",
                    RequireManualConfirmation = true,
                }),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<GoogleCalendarConnection> CreateConnectionAsync(PaymentContext db, int userId, string calendarMode = "personal", DateTime? syncFromDate = null)
    {
        var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1");
        var connection = new GoogleCalendarConnection
        {
            UserId = userId,
            GoogleUserId = "google-user",
            GoogleEmail = "google@example.com",
            AccessTokenProtected = protector.Protect("expired-token"),
            RefreshTokenProtected = protector.Protect("refresh-token"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-10),
            Scope = "openid email profile https://www.googleapis.com/auth/calendar",
            CalendarMode = calendarMode,
            ConflictResolutionMode = "latestWins",
            SyncFromDate = syncFromDate,
            IsEnabled = true,
            SyncEnabled = true,
        };
        db.GoogleCalendarConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task<GoogleCalendarSyncCalendar> CreateCalendarAsync(
        PaymentContext db,
        int connectionId,
        string googleCalendarId,
        string name,
        int? ownerUserId = null)
    {
        var calendar = new GoogleCalendarSyncCalendar
        {
            ConnectionId = connectionId,
            OwnerUserId = ownerUserId,
            GoogleCalendarId = googleCalendarId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.GoogleCalendarSyncCalendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar;
    }

    private static async Task<TaskJournalItem> CreateTaskAsync(PaymentContext db, int assigneeUserId, string title, string assigneeName = "User", DateTime? dueAt = null)
    {
        var task = new TaskJournalItem
        {
            ObjectName = "Тест",
            Title = title,
            AssigneeUserId = assigneeUserId,
            AssigneeName = assigneeName,
            DueAt = dueAt ?? DateTime.UtcNow.AddDays(1),
            PlannedDurationMinutes = 60,
            Status = "new",
            Priority = "normal",
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        return task;
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
            _handler = handler ?? new RecordingGoogleHandler();
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class RecordingGoogleHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _deleteStatusCode;
        private readonly object[] _eventItems;
        private int _eventListRateLimitCount;

        public RecordingGoogleHandler(
            HttpStatusCode deleteStatusCode = HttpStatusCode.NoContent,
            object[]? eventItems = null,
            int eventListRateLimitCount = 0)
        {
            _deleteStatusCode = deleteStatusCode;
            _eventItems = eventItems ?? [];
            _eventListRateLimitCount = eventListRateLimitCount;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new { access_token = "fresh-token", expires_in = 3600 }));
            }

            if (path.EndsWith("/calendars", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(new { id = $"calendar-{Requests.Count}" }));
            }

            if (path.Contains("/events", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                if (_eventListRateLimitCount > 0)
                {
                    _eventListRateLimitCount--;
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new StringContent(
                            "{\"error\":{\"message\":\"Rate Limit Exceeded\",\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}",
                            Encoding.UTF8,
                            "application/json"),
                    };
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(response);
                }

                return Task.FromResult(Json(new { items = _eventItems, nextSyncToken = "sync-token" }));
            }

            if (path.Contains("/events", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(new { id = "event-1", etag = "\"etag-1\"" }));
            }

            if (path.Contains("/events", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                return Task.FromResult(Json(new { id = "event-1", etag = "\"etag-2\"" }));
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/calendar/v3/calendars", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(_deleteStatusCode)
                {
                    Content = new StringContent("{\"error\":\"delete failed\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json"),
            });
        }

        private static HttpResponseMessage Json(object value)
            => new()
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };

        private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri);
            if (source.Content is not null)
            {
                var body = source.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                clone.Content = new StringContent(body, Encoding.UTF8, source.Content.Headers.ContentType?.MediaType ?? "application/json");
            }

            return clone;
        }
    }
}
