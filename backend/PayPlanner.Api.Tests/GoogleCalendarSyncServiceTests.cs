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
        Assert.False(await db.GoogleCalendarAuthRequests.AnyAsync());
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
        Assert.False(await db.GoogleCalendarAuthRequests.AnyAsync());
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenStateExpiredMarksRequestExpired()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "expired-google-callback@example.com", "user");
        db.GoogleCalendarAuthRequests.Add(new GoogleCalendarAuthRequest
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
        var authRequest = await db.GoogleCalendarAuthRequests.SingleAsync();
        Assert.Equal("expired", authRequest.Status);
        Assert.NotNull(authRequest.Error);
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenStateCompletedDoesNotRepeatProviderRequests()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "completed-google-callback@example.com", "user");
        db.GoogleCalendarAuthRequests.Add(new GoogleCalendarAuthRequest
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
        Assert.Equal("completed", (await db.GoogleCalendarAuthRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task HandleWebhookAsync_WhenHeadersMissingRejectsRequest()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new FakeHttpClientFactory());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.HandleWebhookAsync(CreateHttpRequest(0), CancellationToken.None));
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
    public async Task SyncNowAsync_ExportsOnlyExplicitlyVisibleTaskComments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "comments-google@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Комментарии Google");
        db.TaskJournalComments.AddRange(
            new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                AuthorUserId = user.Id,
                AuthorName = user.FullName,
                Kind = "comment",
                Text = "Разрешённый комментарий Google",
                IsExternalCalendarVisible = true,
                CreatedAt = DateTime.UtcNow,
            },
            new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                AuthorUserId = user.Id,
                AuthorName = user.FullName,
                Kind = "comment",
                Text = "Внутренний комментарий Google",
                IsExternalCalendarVisible = false,
                CreatedAt = DateTime.UtcNow.AddSeconds(1),
            });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        var request = handler.Requests.Single(item =>
            item.Method == HttpMethod.Post && item.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
        var body = await request.Content!.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(body);
        var description = payload.RootElement.GetProperty("description").GetString();
        Assert.Contains("Разрешённый комментарий Google", description);
        Assert.DoesNotContain("Внутренний комментарий Google", description);
        Assert.Contains("PAYPLANNER-COMMENTS-V1", description);
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
    public async Task SyncNowAsync_TreatsUnspecifiedTaskDueAtAsUtcInstantWhenCreatingGoogleEvent()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-unspecified-google@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await SetUserTimeZoneAsync(db, user.Id, "Europe/Samara");
        await CreateConnectionAsync(db, user.Id);
        var userTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var dueAt = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(7).AddHours(9), DateTimeKind.Unspecified);
        await CreateTaskAsync(
            db,
            user.Id,
            "Дата без Kind не должна сдвигаться",
            dueAt: dueAt);
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
        var expectedStart = DateTime.SpecifyKind(dueAt, DateTimeKind.Utc);
        Assert.Equal(CalendarTimeZoneHelper.FormatOffsetDateTime(expectedStart, userTimeZone), document.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal(CalendarTimeZoneHelper.FormatOffsetDateTime(expectedStart.AddHours(1), userTimeZone), document.RootElement.GetProperty("end").GetProperty("dateTime").GetString());
    }

    [Fact]
    public async Task SyncNowAsync_UsesAssigneeTimeZoneForAdminCalendarNearMidnight()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-google-timezone@example.com", "admin", "Admin");
        var assignee = await CreateUserAsync(db, "assignee-google-timezone@example.com", "user", "Kamchatka User");
        await EnableGoogleSettingsAsync(db);
        await SetUserTimeZoneAsync(db, admin.Id, "Europe/Moscow");
        await SetUserTimeZoneAsync(db, assignee.Id, "Asia/Kamchatka");
        await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var assigneeTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Asia/Kamchatka");
        var localStart = CalendarTimeZoneHelper
            .ToUserLocal(DateTime.UtcNow.AddDays(1), assigneeTimeZone)
            .Date
            .AddHours(23)
            .AddMinutes(30);
        var dueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, assigneeTimeZone);
        await CreateTaskAsync(
            db,
            assignee.Id,
            "Near midnight in assignee timezone",
            assignee.FullName,
            dueAt: dueAt);
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        var createCalendarRequest = handler.Requests.Single(request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars", StringComparison.Ordinal));
        var createCalendarBody = await createCalendarRequest.Content!.ReadAsStringAsync();
        using var calendarDocument = JsonDocument.Parse(createCalendarBody);
        Assert.Equal("Asia/Kamchatka", calendarDocument.RootElement.GetProperty("timeZone").GetString());

        var createEventRequest = handler.Requests.Single(request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
        var body = await createEventRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(CalendarTimeZoneHelper.FormatOffsetDateTime(dueAt, assigneeTimeZone), document.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("Asia/Kamchatka", document.RootElement.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal(CalendarTimeZoneHelper.FormatOffsetDateTime(dueAt.AddHours(1), assigneeTimeZone), document.RootElement.GetProperty("end").GetProperty("dateTime").GetString());
        Assert.Equal("Asia/Kamchatka", document.RootElement.GetProperty("end").GetProperty("timeZone").GetString());
        Assert.Equal(
            "Asia/Kamchatka",
            document.RootElement
                .GetProperty("extendedProperties")
                .GetProperty("private")
                .GetProperty("payPlannerTimeZone")
                .GetString());
    }

    [Fact]
    public async Task SyncNowAsync_UpdatesExistingGoogleCalendarTimeZoneFromProfile()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-existing-timezone@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await SetUserTimeZoneAsync(db, user.Id, "Asia/Krasnoyarsk");
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "google-existing-timezone-calendar", "PayPlanner tasks");
        var dueAt = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1).AddHours(8).AddMinutes(30), DateTimeKind.Utc);
        await CreateTaskAsync(
            db,
            user.Id,
            "Existing calendar should receive profile timezone",
            dueAt: dueAt);
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        var patchCalendarRequest = handler.Requests.Single(request =>
            request.Method == HttpMethod.Patch &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/google-existing-timezone-calendar", StringComparison.Ordinal));
        var patchBody = await patchCalendarRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(patchBody);
        Assert.Equal("Asia/Krasnoyarsk", document.RootElement.GetProperty("timeZone").GetString());
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task SyncNowAsync_UsesGoogleColorForCompletedTasksWhenColorModeEnabled(string status)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-completed-color-google@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Готовая задача", dueAt: DateTime.UtcNow.Date.AddDays(7).AddHours(9));
        task.Status = status;
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        var createEventRequest = handler.Requests.Single(request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
        var body = await createEventRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("8", document.RootElement.GetProperty("colorId").GetString());
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task SyncTaskForConnectedUsersAsync_DeletesCompletedTaskEventWhenHideModeEnabled(string status)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-completed-hide-google@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "hide");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-completed-hide", "PayPlanner задачи");
        var task = await CreateTaskAsync(db, user.Id, "Скрыть готовую задачу", dueAt: new DateTime(2026, 6, 5, 9, 0, 0));
        task.Status = status;
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-completed-hide",
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
            request.RequestUri!.AbsolutePath.Contains("/events/event-completed-hide", StringComparison.Ordinal));
        var link = await db.GoogleCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("deleted", link.SyncStatus);
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        task.Status = "inProgress";
        await db.SaveChangesAsync();
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        Assert.Equal("active", (await db.GoogleCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id)).SyncStatus);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task SyncTaskForConnectedUsersAsync_WhenClosedColorChanges_UpdatesEventOnce(string status)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "closed-color-change@example.local", "user");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Closed color task", dueAt: DateTime.UtcNow.Date.AddDays(7).AddHours(9));
        task.Status = status;
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        var setting = await db.SystemSettings.SingleAsync(item => item.Key == "calendarSync.settings");
        var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value)!;
        settings.CompletedTaskGoogleColorId = "11";
        setting.Value = JsonSerializer.Serialize(settings);
        await db.SaveChangesAsync();

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        var update = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.Contains("/events/", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(await update.Content!.ReadAsStringAsync());
        Assert.Equal("11", document.RootElement.GetProperty("colorId").GetString());
        Assert.Single(await db.GoogleCalendarTaskLinks.ToListAsync());
        Assert.Equal(status, (await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id)).Status);
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
        await EnableGoogleSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: action);
        await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(db, user.Id, "Closed synthetic task", dueAt: DateTime.UtcNow.Date.AddDays(7).AddHours(9));
        task.Status = status;
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(visible, await db.GoogleCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == task.Id));
        Assert.Equal(visible, handler.Requests.Any(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal)));
        Assert.Equal(status, (await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id)).Status);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task SyncNowAsync_WhenClosedTaskDeleteFails_PreservesLinkAndReportsError(string status)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-completed-hide-google@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "hide");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-completed-hide", "PayPlanner задачи");
        var task = await CreateTaskAsync(db, user.Id, "Скрыть готовую задачу", dueAt: DateTime.UtcNow.Date.AddDays(7).AddHours(9));
        task.Status = status;
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-completed-hide",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.True(result.ErrorCount > 0);
        Assert.Equal(0, result.ProcessedTasks);
        Assert.Equal("active", (await db.GoogleCalendarTaskLinks.SingleAsync(item => item.TaskJournalItemId == task.Id)).SyncStatus);
        Assert.Equal(status, (await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id)).Status);
        var logs = await db.GoogleCalendarSyncLogs.Where(item => item.TaskJournalItemId == task.Id).ToListAsync();
        Assert.Contains(logs, item => item.Status == "error");
        Assert.DoesNotContain(logs, item => item.Status == "success" && item.Operation.StartsWith("delete-event", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_ReusesLinkAndDeletesOldEventWhenCalendarChanges()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-google-calendar-move@example.com", "admin", "Admin");
        var assignee = await CreateUserAsync(db, "assignee-google-calendar-move@example.com", "user", "Assignee");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var oldCalendar = await CreateCalendarAsync(db, connection.Id, "old-calendar", "Old calendar", admin.Id);
        var assigneeCalendar = await CreateCalendarAsync(db, connection.Id, "assignee-calendar", "Assignee calendar", assignee.Id);
        var task = await CreateTaskAsync(
            db,
            assignee.Id,
            "Task moved to assignee calendar",
            assignee.FullName ?? "Assignee",
            new DateTime(2026, 6, 12, 3, 20, 0, DateTimeKind.Utc));
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = oldCalendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = oldCalendar.GoogleCalendarId,
            GoogleEventId = "old-event",
            SyncStatus = "active",
            LastCrmHash = "old-crm-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Single(await db.GoogleCalendarTaskLinks.Where(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == task.Id).ToListAsync());
        var link = await db.GoogleCalendarTaskLinks.SingleAsync(item => item.ConnectionId == connection.Id && item.TaskJournalItemId == task.Id);
        Assert.Equal(assigneeCalendar.Id, link.CalendarSyncCalendarId);
        Assert.Equal(assigneeCalendar.GoogleCalendarId, link.GoogleCalendarId);
        Assert.Equal("event-1", link.GoogleEventId);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.Contains($"/calendars/{Uri.EscapeDataString(assigneeCalendar.GoogleCalendarId)}/events", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.Contains($"/calendars/{Uri.EscapeDataString(oldCalendar.GoogleCalendarId)}/events/old-event", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncNowAsync_AppliesGoogleMoveWithoutPushingTaskBackOnNextCycle()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-move@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await SetUserTimeZoneAsync(db, user.Id, "Europe/Samara");
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
                start = new { dateTime = "2026-06-05T16:00:00+04:00" },
                end = new { dateTime = "2026-06-05T17:00:00+04:00" },
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
        var secondResult = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(1, firstResult.ImportedEvents);
        Assert.Equal(0, firstResult.ErrorCount);
        Assert.Equal(0, secondResult.ErrorCount);
        var updatedTask = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc), updatedTask.DueAt);
        var syncLog = await db.GoogleCalendarSyncLogs.SingleAsync(item =>
            item.ConnectionId == connection.Id &&
            item.TaskJournalItemId == task.Id &&
            item.Operation == "update-task" &&
            item.Status == "success");
        Assert.Contains("12:00", syncLog.DetailsJson);
        Assert.Contains("16:00", syncLog.DetailsJson);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Put &&
            request.RequestUri!.AbsolutePath.Contains("/events/event-google-move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncNowAsync_DoesNotReopenCompletedTaskFromStaleGoogleSummary()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-closed-task@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-google-closed-task", "PayPlanner задачи");
        var task = await CreateTaskAsync(db, user.Id, "Закрытая задача", dueAt: new DateTime(2026, 6, 5, 8, 0, 0, DateTimeKind.Utc));
        task.Status = "done";
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-google-closed-task",
            SyncStatus = "active",
            LastGoogleHash = "old-google-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var googleEvents = new object[]
        {
            new
            {
                id = "event-google-closed-task",
                etag = "\"etag-google-closed-task\"",
                status = "confirmed",
                summary = "[Новая] Закрытая задача",
                start = new { dateTime = "2026-06-05T12:00:00Z" },
                end = new { dateTime = "2026-06-05T13:00:00Z" },
                extendedProperties = new
                {
                    @private = new Dictionary<string, string>
                    {
                        ["payPlannerTaskId"] = task.Id.ToString(),
                    },
                },
            },
        };
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler(eventItems: googleEvents)));

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(1, result.ImportedEvents);
        Assert.Equal(0, result.ErrorCount);
        var updatedTask = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("done", updatedTask.Status);
        Assert.Equal(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc), updatedTask.DueAt);
    }

    [Fact]
    public async Task SyncNowAsync_DeletesStaleGoogleDuplicateWithoutMovingTask()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-stale-duplicate@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await SetUserTimeZoneAsync(db, user.Id, "Europe/Samara");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-stale-duplicate", "PayPlanner задачи");
        var taskDueAt = new DateTime(2026, 6, 12, 6, 52, 0, DateTimeKind.Utc);
        var task = await CreateTaskAsync(db, user.Id, "Актуальная задача", dueAt: taskDueAt);
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "current-event",
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
                id = "stale-event",
                etag = "\"etag-stale\"",
                status = "confirmed",
                summary = "[Новая] Устаревший дубль",
                start = new { dateTime = "2026-06-12T10:20:00+04:00" },
                end = new { dateTime = "2026-06-12T11:20:00+04:00" },
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

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(0, result.ErrorCount);
        var unchangedTask = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal(taskDueAt, unchangedTask.DueAt);
        Assert.DoesNotContain(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.TaskJournalItemId == task.Id &&
            item.Operation == "update-task");
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.TaskJournalItemId == task.Id &&
            item.Operation == "delete-stale-event" &&
            item.Status == "success");
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-stale-duplicate/events/stale-event", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncTaskForConnectedUsersAsync_DeletesDuplicateGoogleEventAfterWrite()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-post-write-duplicate@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-google-post-write-duplicate", "PayPlanner tasks");
        var task = await CreateTaskAsync(db, user.Id, "Delete duplicate Google event after write");
        var googleEvents = new object[]
        {
            new
            {
                id = "event-1",
                etag = "\"etag-current\"",
                status = "confirmed",
                summary = "[New] Current event",
                start = new { dateTime = "2026-06-12T10:00:00+04:00" },
                end = new { dateTime = "2026-06-12T11:00:00+04:00" },
                extendedProperties = new
                {
                    @private = new Dictionary<string, string>
                    {
                        ["payPlannerTaskId"] = task.Id.ToString(),
                    },
                },
            },
            new
            {
                id = "duplicate-event",
                etag = "\"etag-duplicate\"",
                status = "confirmed",
                summary = "[New] Duplicate event",
                start = new { dateTime = "2026-06-12T10:00:00+04:00" },
                end = new { dateTime = "2026-06-12T11:00:00+04:00" },
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

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-google-post-write-duplicate/events/duplicate-event", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-google-post-write-duplicate/events/event-1", StringComparison.Ordinal));
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.TaskJournalItemId == task.Id &&
            item.Operation == "delete-duplicate-event" &&
            item.Status == "success");
    }

    [Fact]
    public async Task SyncNowAsync_WhenManualFullSyncCooldownActiveRejectsRepeatedRun()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-cooldown@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        connection.LastFullSyncAt = DateTime.UtcNow.AddMinutes(-5);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 30);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler()));

        var error = await Assert.ThrowsAsync<CalendarManualSyncCooldownException>(() =>
            service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None));

        Assert.Contains("Повторную полную синхронизацию Google Calendar можно запустить после", error.Message);
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
    public async Task SyncNowAsync_RetriesGoogleTemporaryUnavailableWhenListingEvents()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-google-temporary-unavailable@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-temporary-unavailable", "PayPlanner задачи");
        var handler = new RecordingGoogleHandler(eventListTransientFailureCount: 1);
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
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler()));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        Assert.Contains(await db.GoogleCalendarSyncCalendars.ToListAsync(), item =>
            item.OwnerUserId == assignee.Id &&
            item.Name.Contains("Иван Петров", StringComparison.Ordinal));
        var log = Assert.Single(await db.GoogleCalendarSyncLogs.AsNoTracking()
            .Where(item => item.Operation == "create-event").ToListAsync());
        using var details = JsonDocument.Parse(log.DetailsJson!);
        Assert.Equal(admin.FullName, details.RootElement.GetProperty("userName").GetString());
        Assert.Equal(admin.Email, details.RootElement.GetProperty("userEmail").GetString());
        Assert.Equal(assignee.FullName, details.RootElement.GetProperty("calendarOwnerName").GetString());
        Assert.Equal(assignee.Email, details.RootElement.GetProperty("calendarOwnerEmail").GetString());

    }

    [Fact]
    public async Task SyncNowAsync_AdminConnectionCreatesCalendarForActiveApprovedUserWithoutTasks()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-google-empty-calendar@example.com", "admin", "Администратор");
        var assignee = await CreateUserAsync(db, "lawyer-google-empty-calendar@example.com", "user", "Кузьминова Наталья Игоревна");
        await EnableGoogleSettingsAsync(db);
        await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler()));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(0, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Contains(await db.GoogleCalendarSyncCalendars.ToListAsync(), item =>
            item.OwnerUserId == assignee.Id &&
            item.Name.Contains("Кузьминова Наталья Игоревна", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncNowAsync_NonAdminAssigneeModeUsesSpecificCalendarVisibility()
    {
        await using var db = await CreateContextAsync();
        var secretary = await CreateUserAsync(db, "secretary-google-specific@example.com", "secretary", "Секретарь");
        var selected = await CreateUserAsync(db, "selected-google-specific@example.com", "selected-user", "Выбранный сотрудник");
        var hidden = await CreateUserAsync(db, "hidden-google-specific@example.com", "hidden-user", "Скрытый сотрудник");
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

        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, secretary.Id, calendarMode: "adminByAssignee");
        await CreateTaskAsync(db, selected.Id, "Задача выбранного сотрудника", selected.FullName);
        var hiddenTask = await CreateTaskAsync(db, hidden.Id, "Задача скрытого сотрудника", hidden.FullName);
        hiddenTask.Participants.Add(new TaskJournalParticipant
        {
            UserId = selected.Id,
            Role = "observer",
            DisplayName = selected.FullName,
            Email = selected.Email,
        });
        var revokedCalendar = await CreateCalendarAsync(db, connection.Id, "calendar-hidden-specific", "PayPlanner - Hidden employee", hidden.Id);
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = revokedCalendar.Id,
            TaskJournalItemId = hiddenTask.Id,
            GoogleCalendarId = revokedCalendar.GoogleCalendarId,
            GoogleEventId = "event-hidden-specific",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(secretary.Id, CreateHttpRequest(secretary.Id), CancellationToken.None);

        Assert.Equal(1, result.ProcessedTasks);
        var calendars = await db.GoogleCalendarSyncCalendars
            .Where(item => item.OwnerUserId != null)
            .ToListAsync();
        Assert.Contains(calendars, item => item.OwnerUserId == secretary.Id);
        Assert.Contains(calendars, item => item.OwnerUserId == selected.Id);
        Assert.DoesNotContain(calendars, item => item.OwnerUserId == hidden.Id);
        Assert.False(await db.GoogleCalendarTaskLinks.AnyAsync(item => item.TaskJournalItemId == hiddenTask.Id));
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-hidden-specific", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncNowAsync_AdminConnectionRemovesDuplicateAssigneeCalendars()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin-google-duplicate-calendar@example.com", "admin", "Администратор");
        var assignee = await CreateUserAsync(db, "employee-google-duplicate-calendar@example.com", "user", "Елена Кожеваткина");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, admin.Id, calendarMode: "adminByAssignee");
        var primaryCalendar = await CreateCalendarAsync(db, connection.Id, "calendar-elena-primary", "PayPlanner - Елена Кожеваткина", assignee.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-elena-duplicate", "PayPlanner - Елена Кожеваткина", assignee.Id);
        connection.DefaultCalendarId = primaryCalendar.GoogleCalendarId;
        connection.DefaultCalendarName = primaryCalendar.Name;
        await db.SaveChangesAsync();

        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        var result = await service.SyncNowAsync(admin.Id, CreateHttpRequest(admin.Id, "admin"), CancellationToken.None);

        Assert.Equal(0, result.ErrorCount);
        var calendars = await db.GoogleCalendarSyncCalendars
            .Where(item => item.ConnectionId == connection.Id && item.OwnerUserId == assignee.Id)
            .ToListAsync();
        var calendar = Assert.Single(calendars);
        Assert.Equal("calendar-elena-primary", calendar.GoogleCalendarId);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-elena-duplicate", StringComparison.Ordinal));
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.Operation == "delete-duplicate-calendar" &&
            item.Status == "success");
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
    public async Task SyncTaskForConnectedUsersAsync_UpdatesLinkedGoogleEventAndWritesLocalTimeLog()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-update-google@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        await SetUserTimeZoneAsync(db, user.Id, "Asia/Kamchatka");
        var connection = await CreateConnectionAsync(db, user.Id);
        var task = await CreateTaskAsync(
            db,
            user.Id,
            "Update linked Google event",
            dueAt: new DateTime(2026, 6, 4, 11, 30, 0, DateTimeKind.Utc));
        var calendar = await CreateCalendarAsync(db, connection.Id, "calendar-update", "PayPlanner задачи");
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "event-update",
            SyncStatus = "active",
            LastCrmHash = "old-crm-hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingGoogleHandler();
        var service = CreateService(db, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        var putRequest = handler.Requests.Single(request =>
            request.Method == HttpMethod.Put &&
            request.RequestUri!.AbsolutePath.EndsWith("/calendars/calendar-update/events/event-update", StringComparison.Ordinal));
        var body = await putRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("2026-06-04T23:30:00+12:00", document.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        var syncLog = await db.GoogleCalendarSyncLogs.SingleAsync(item =>
            item.ConnectionId == connection.Id &&
            item.TaskJournalItemId == task.Id &&
            item.Operation == "update-event" &&
            item.Status == "success");
        Assert.Contains("23:30", syncLog.DetailsJson);
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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DisconnectAsync_WhenGoogleCleanupFails_KeepsConnection(HttpStatusCode statusCode)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-disconnect-error@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-disconnect-error", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler(statusCode)));

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
    public async Task DisconnectAsync_WhenGoogleCalendarAlreadyMissing_RemovesLocalConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-disconnect-missing@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-disconnect-missing", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler(HttpStatusCode.NotFound)));

        var status = await service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Null(status.Connection);
        Assert.False(await db.GoogleCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.Operation == "delete-calendar-missing" &&
            item.Status == "success");
    }

    [Fact]
    public async Task DisconnectAsync_WhenGoogleCleanupTimesOut_KeepsConnection()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "executor-disconnect-timeout@example.com", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        await CreateCalendarAsync(db, connection.Id, "calendar-disconnect-timeout", "PayPlanner задачи");
        var service = CreateService(db, new FakeHttpClientFactory(new ThrowingGoogleHandler(_ =>
            new TaskCanceledException("Google Calendar timeout."))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DisconnectAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None));

        Assert.Contains("Не удалось отключить Google Calendar", error.Message);
        Assert.True(await db.GoogleCalendarConnections.AnyAsync(item => item.Id == connection.Id));
        Assert.True(await db.GoogleCalendarSyncCalendars.AnyAsync(item => item.GoogleCalendarId == "calendar-disconnect-timeout"));
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item =>
            item.ConnectionId == connection.Id &&
            item.Operation == "cleanup-user-calendars" &&
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

    [Fact]
    public async Task UpdateSettingsAsync_WhenAdminManagedRejectsAdminClaimWithoutDatabaseAdminRole()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "google-stale-admin-claim@example.com", "user");
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
                CreateHttpRequest(user.Id, role: "admin"),
                CancellationToken.None));

        Assert.Contains("администратором", error.Message);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenAdminManagedAllowsDatabaseAdminRoleWithoutAdminClaim()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "google-db-admin@example.com", "admin");
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
            new UpdateGoogleCalendarSettingsRequest { SyncEnabled = false },
            CreateHttpRequest(user.Id, role: "user"),
            CancellationToken.None);

        Assert.NotNull(result.Connection);
        Assert.False(await db.GoogleCalendarConnections
            .Where(item => item.UserId == user.Id)
            .Select(item => item.SyncEnabled)
            .SingleAsync());
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
        var queryProbe = new CalendarBatchQueryProbe("Google");
        await using var db = await CreateContextAsync(queryProbe);
        var user = await CreateUserAsync(db, "bulk-owner@example.local", "user");
        var hidden = await CreateUserAsync(db, "bulk-hidden@example.local", "user");
        await EnableGoogleSettingsAsync(db);
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
            db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = tasks[index].Id,
                GoogleCalendarId = calendar.GoogleCalendarId,
                GoogleEventId = eventId,
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        await CreateTaskAsync(db, hidden.Id, "Not visible", dueAt: dueAt);
        await CreateTaskAsync(db, user.Id, "Outside sync window", dueAt: DateTime.UtcNow.AddDays(366));
        var firstPageIds = tasks.Take(500).Select(item => item.Id).ToArray();
        var inner = new RecordingGoogleHandler();
        var handler = new CalendarBatchDeleteProbe(inner, failOnce, async () =>
        {
            Assert.Equal(500, await db.GoogleCalendarTaskLinks.AsNoTracking()
                .CountAsync(item => firstPageIds.Contains(item.TaskJournalItemId) && item.SyncStatus == "deleted"));
            if (scenario != "normal")
            {
                Assert.Equal(2, db.ChangeTracker.Entries<GoogleCalendarTaskLink>().Count());
                Assert.Empty(db.ChangeTracker.Entries<GoogleCalendarSyncLog>());
                var trackedConnection = Assert.Single(db.ChangeTracker.Entries<GoogleCalendarConnection>()).Entity;
                Assert.Equal(2, trackedConnection.TaskLinks.Count);
                Assert.Equal(2, trackedConnection.Calendars.Sum(item => item.TaskLinks.Count));
            }
            if (insertDuringSync)
            {
                var lateTask = await CreateTaskAsync(db, user.Id, "Created during sync", dueAt: dueAt);
                lateTask.Status = "cancelled";
                db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
                {
                    ConnectionId = connection.Id,
                    CalendarSyncCalendarId = calendar.Id,
                    TaskJournalItemId = lateTask.Id,
                    GoogleCalendarId = calendar.GoogleCalendarId,
                    GoogleEventId = "bulk-event-503",
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
        Console.WriteLine($"CalendarLogQueries provider=Google; scenario=hide; case={scenario}; phase=first; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Assert.Equal(cancelAfterFirstPage ? 501 : 502, handler.DeleteAttempts.Count);
        Assert.All(handler.DeleteAttempts.Values, count => Assert.Equal(1, count));
        Assert.Equal(cancelAfterFirstPage ? 500 : failOnce ? 501 : 502, await db.GoogleCalendarTaskLinks.AsNoTracking().CountAsync(item => item.SyncStatus == "deleted"));
        Assert.Equal(insertDuringSync ? 503 : 502, await db.GoogleCalendarTaskLinks.CountAsync());
        Assert.DoesNotContain(inner.Requests, request => request.Method == HttpMethod.Put);
        var taskIds = tasks.Select(item => item.Id).ToArray();
        Assert.Equal(502, await db.TaskJournalItems.CountAsync(item => taskIds.Contains(item.Id) && (item.Status == "done" || item.Status == "cancelled")));
        db.ChangeTracker.Clear();
        queryProbe.Reset();
        service = CreateService(db, new FakeHttpClientFactory(handler));

        var retry = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(insertDuringSync ? 503 : 502, retry.ProcessedTasks);
        Assert.Equal(0, retry.ErrorCount);
        Assert.Equal(insertDuringSync ? 503 : 502, await db.GoogleCalendarTaskLinks.AsNoTracking().CountAsync(item => item.SyncStatus == "deleted"));
        Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
        Assert.InRange(queryProbe.LinkSelects, 2, 4);
        Console.WriteLine($"CalendarLogQueries provider=Google; scenario=hide; case={scenario}; phase=retry; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Assert.Equal(insertDuringSync ? 503 : 502, handler.DeleteAttempts.Count);
        Assert.All(handler.DeleteAttempts, item => Assert.Equal((failOnce || cancelAfterFirstPage) && item.Key.Contains("bulk-event-501", StringComparison.Ordinal) ? 2 : 1, item.Value));
    }

    [Fact]
    public async Task SyncNowAsync_Colors502EventsInBoundedPagesWithoutRewritingOnRepeat()
    {
        var queryProbe = new CalendarBatchQueryProbe("Google");
        await using var db = await CreateContextAsync(queryProbe);
        var user = await CreateUserAsync(db, "bulk-owner@example.local", "user");
        var hidden = await CreateUserAsync(db, "bulk-hidden@example.local", "user");
        await EnableGoogleSettingsAsync(db);
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
            db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = tasks[index].Id,
                GoogleCalendarId = calendar.GoogleCalendarId,
                GoogleEventId = eventId,
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        await CreateTaskAsync(db, hidden.Id, "Not visible", dueAt: dueAt);
        await CreateTaskAsync(db, user.Id, "Outside sync window", dueAt: DateTime.UtcNow.AddDays(366));
        var firstPageIds = tasks.Take(500).Select(item => item.Id).ToArray();
        var inner = new RecordingGoogleHandler();
        var handler = new CalendarBatchColorProbe(inner, async () =>
        {
            Assert.Equal(500, await db.GoogleCalendarTaskLinks.AsNoTracking()
                .CountAsync(item => firstPageIds.Contains(item.TaskJournalItemId)
                    && item.SyncStatus == "active" && item.LastCrmHash != null));
            Assert.Equal(2, db.ChangeTracker.Entries<GoogleCalendarTaskLink>().Count());
        });
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        queryProbe.Reset();

        var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);

        Assert.Equal(502, result.ProcessedTasks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(new[] { 500, 500 }, queryProbe.TaskPageLimits);
        Assert.InRange(queryProbe.LinkSelects, 2, 4);
        var exportLogs = await db.GoogleCalendarSyncLogs.AsNoTracking()
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
        Console.WriteLine($"CalendarLogQueries provider=Google; scenario=color; case=normal; phase=first; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Assert.Equal(502, handler.PutBodies.Count);
        Assert.All(handler.PutBodies.Values, bodies => Assert.Single(bodies));
        Assert.Equal(0, handler.DeleteCount);
        Assert.Equal(502, await db.GoogleCalendarTaskLinks.AsNoTracking()
            .CountAsync(item => item.SyncStatus == "active" && item.LastCrmHash != null));
        Assert.All(handler.PutBodies.Values, bodies =>
        {
            using var payload = JsonDocument.Parse(Assert.Single(bodies));
            Assert.Equal("8", payload.RootElement.GetProperty("colorId").GetString());
        });
        Assert.Contains(handler.PutBodies.Values, bodies =>
        {
            using var payload = JsonDocument.Parse(bodies[0]);
            return payload.RootElement.GetProperty("summary").GetString()!.StartsWith("[Отменено]", StringComparison.Ordinal);
        });
        Assert.Contains(handler.PutBodies.Values, bodies =>
        {
            using var payload = JsonDocument.Parse(bodies[0]);
            return payload.RootElement.GetProperty("summary").GetString()!.StartsWith("[Готово]", StringComparison.Ordinal);
        });
        Assert.Equal(1, handler.CalendarReadQueries.Count(query => !query.Contains("privateExtendedProperty=", StringComparison.Ordinal)));
        Assert.Equal(502, handler.CalendarReadQueries.Count(query => query.Contains("privateExtendedProperty=", StringComparison.Ordinal)));
        var taskIds = tasks.Select(item => item.Id).ToArray();
        Assert.Equal(502, await db.TaskJournalItems.CountAsync(item => taskIds.Contains(item.Id)
            && (item.Status == "done" || item.Status == "cancelled")));
        db.ChangeTracker.Clear();
        queryProbe.Reset();
        handler.CalendarReadQueries.Clear();
        service = CreateService(db, new FakeHttpClientFactory(handler));

        var retry = await service.SyncDueConnectionsAsync(10, CancellationToken.None);

        Assert.Equal(502, retry.ProcessedTasks);
        Assert.Equal(0, retry.ErrorCount);
        Console.WriteLine($"CalendarLogQueries provider=Google; scenario=color; case=normal; phase=retry; metadata={queryProbe.LogMetadataSelects}; total={queryProbe.TotalSelects}");
        Assert.InRange(queryProbe.LogMetadataSelects, 0, 8);
        Assert.Equal(502, handler.PutBodies.Count);
        Assert.All(handler.PutBodies.Values, bodies => Assert.Single(bodies));
        Assert.Equal(0, handler.DeleteCount);
        Assert.Single(handler.CalendarReadQueries);
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
        await EnableGoogleSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "cleanup-calendar", "Synthetic cleanup calendar");
        var task = await CreateTaskAsync(db, user.Id, "Synthetic cleanup recovery", dueAt: DateTime.UtcNow.AddDays(7));
        if (existingLink)
        {
            db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = task.Id,
                GoogleCalendarId = calendar.GoogleCalendarId,
                GoogleEventId = "event-1",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var duplicatePayload = JsonSerializer.Serialize(new
        {
            items = new[] { new { id = "cleanup-duplicate", status = "confirmed", summary = "Synthetic duplicate" } },
        });
        var handler = new CalendarCleanupFailureProbe(new RecordingGoogleHandler(), google: true, fullSync, failRead, duplicatePayload);
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
        var failedLink = await db.GoogleCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("active", failedLink.SyncStatus);
        Assert.False(string.IsNullOrWhiteSpace(failedLink.LastCrmHash));
        Assert.False(string.IsNullOrWhiteSpace(failedLink.LastError));
        Assert.True(await db.GoogleCalendarSyncLogs.AnyAsync(item => item.TaskJournalItemId == task.Id
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
        var recoveredLink = await db.GoogleCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Null(recoveredLink.LastError);
        Assert.Null((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(item => item.Id == connection.Id)).LastError);
        Assert.True(await db.GoogleCalendarSyncLogs.AnyAsync(item => item.TaskJournalItemId == task.Id
            && item.Operation == "duplicate-cleanup" && item.Status == "success"));
        Assert.Equal(firstHash, recoveredLink.LastCrmHash);
        Assert.Equal("new", (await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).Status);
        Assert.Equal(1, await db.GoogleCalendarTaskLinks.CountAsync(item => item.TaskJournalItemId == task.Id));
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
        await EnableGoogleSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "cleanup-calendar", "Synthetic cleanup calendar");
        var task = await CreateTaskAsync(db, user.Id, "Synthetic cleanup recovery", dueAt: DateTime.UtcNow.AddDays(7));
        var otherTask = await CreateTaskAsync(db, user.Id, "Synthetic other pending cleanup", dueAt: task.DueAt);
        {
            db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
            {
                ConnectionId = connection.Id,
                CalendarSyncCalendarId = calendar.Id,
                TaskJournalItemId = task.Id,
                GoogleCalendarId = calendar.GoogleCalendarId,
                GoogleEventId = "event-1",
                SyncStatus = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var duplicatePayload = JsonSerializer.Serialize(new
        {
            items = new[] { new { id = "cleanup-duplicate", status = "confirmed", summary = "Synthetic duplicate" } },
        });
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = otherTask.Id,
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "other-event",
            SyncStatus = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new CalendarCleanupFailureProbe(new RecordingGoogleHandler(), google: true,
            fullSync: false, failRead: true, duplicatePayload, failureCount: 2);
        db.ChangeTracker.Clear();
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);
        await service.SyncTaskForConnectedUsersAsync(otherTask.Id, CancellationToken.None);
        Assert.Equal(2, handler.EventWrites);
        Assert.Equal(2, await db.GoogleCalendarTaskLinks.AsNoTracking().CountAsync(item => item.LastError != null));
        db.ChangeTracker.Clear();
        await using var retryDb = CreateAdditionalContext(db);
        service = CreateService(retryDb, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(task.Id, CancellationToken.None);

        Assert.Null((await db.GoogleCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id)).LastError);
        Assert.NotNull((await db.GoogleCalendarTaskLinks.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == otherTask.Id)).LastError);
        Assert.NotNull((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(item => item.Id == connection.Id)).LastError);
        Assert.Equal(2, handler.EventWrites);
        await using var finalDb = CreateAdditionalContext(db);
        service = CreateService(finalDb, new FakeHttpClientFactory(handler));

        await service.SyncTaskForConnectedUsersAsync(otherTask.Id, CancellationToken.None);

        Assert.False(await db.GoogleCalendarTaskLinks.AnyAsync(item => item.LastError != null));
        Assert.Null((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(item => item.Id == connection.Id)).LastError);
        Assert.Equal(2, handler.EventWrites);
        Assert.Equal(4, handler.DuplicateReadAttempts);
        Assert.Equal(1, handler.DuplicateDeleteAttempts);
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
        await EnableGoogleSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 10, completedTaskCalendarAction: "color");
        var firstConnection = await CreateConnectionAsync(db, first.Id);
        var secondConnection = await CreateConnectionAsync(db, second.Id);
        var firstCalendar = await CreateCalendarAsync(db, firstConnection.Id, "log-first-calendar", "Synthetic first calendar");
        var secondCalendar = await CreateCalendarAsync(db, secondConnection.Id, "log-second-calendar", "Synthetic second calendar");
        var firstTask = await CreateTaskAsync(db, first.Id, "Synthetic first task", dueAt: DateTime.UtcNow.AddDays(7));
        var secondTask = await CreateTaskAsync(db, second.Id, "Synthetic second task", dueAt: firstTask.DueAt);
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler()));

        await service.SyncTaskForConnectedUsersAsync(firstTask.Id, CancellationToken.None);
        await service.SyncTaskForConnectedUsersAsync(secondTask.Id, CancellationToken.None);

        var firstLog = await db.GoogleCalendarSyncLogs.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == firstConnection.Id && item.Operation == "create-event");
        var secondLog = await db.GoogleCalendarSyncLogs.AsNoTracking()
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

        var updateLog = await db.GoogleCalendarSyncLogs.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == firstConnection.Id && item.Operation == "update-event");
        using var updated = JsonDocument.Parse(updateLog.DetailsJson!);
        Assert.Equal(first.FullName, updated.RootElement.GetProperty("userName").GetString());
        Assert.Equal(first.Email, updated.RootElement.GetProperty("userEmail").GetString());
        Assert.Equal(firstCalendar.Name, updated.RootElement.GetProperty("calendarName").GetString());
        Assert.Equal(firstTask.Title, updated.RootElement.GetProperty("taskTitle").GetString());
        Assert.Equal(firstTask.ObjectName, updated.RootElement.GetProperty("taskObjectName").GetString());
        Assert.Equal(firstTask.AssigneeName, updated.RootElement.GetProperty("taskAssigneeName").GetString());
        Assert.Equal(firstLog.DetailsJson, (await db.GoogleCalendarSyncLogs.AsNoTracking()
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

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string roleName, string fullName = "User")
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

    private static async Task SetCalendarSyncSettingsAsync(
        PaymentContext db,
        int manualFullSyncCooldownMinutes,
        string completedTaskCalendarAction = "color",
        string completedTaskGoogleColorId = "8")
    {
        var setting = await db.SystemSettings.SingleAsync(item => item.Key == "calendarSync.settings");
        setting.Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
        {
            GoogleCalendarSyncEnabled = true,
            YandexCalendarSyncEnabled = false,
            SyncMode = "hybrid",
            ConflictResolutionMode = "latestWins",
            UserControlMode = "userManaged",
            InactiveUserAction = "cleanupExternalData",
            RequireManualConfirmation = true,
            ManualFullSyncCooldownMinutes = manualFullSyncCooldownMinutes,
            CompletedTaskCalendarAction = completedTaskCalendarAction,
            CompletedTaskGoogleColorId = completedTaskGoogleColorId,
        });
        setting.UpdatedAt = DateTime.UtcNow;
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
        private int _eventListTransientFailureCount;

        public RecordingGoogleHandler(
            HttpStatusCode deleteStatusCode = HttpStatusCode.NoContent,
            object[]? eventItems = null,
            int eventListRateLimitCount = 0,
            int eventListTransientFailureCount = 0)
        {
            _deleteStatusCode = deleteStatusCode;
            _eventItems = eventItems ?? [];
            _eventListRateLimitCount = eventListRateLimitCount;
            _eventListTransientFailureCount = eventListTransientFailureCount;
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

            if (request.Method == HttpMethod.Patch && path.Contains("/calendar/v3/calendars", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new { id = path.Split('/').Last(), timeZone = "updated" }));
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

                if (_eventListTransientFailureCount > 0)
                {
                    _eventListTransientFailureCount--;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent(
                            "{\"error\":{\"message\":\"The service is currently unavailable.\"}}",
                            Encoding.UTF8,
                            "application/json"),
                    });
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

    private sealed class ThrowingGoogleHandler(Func<HttpRequestMessage, Exception> resolveException) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(resolveException(request));
    }
}
