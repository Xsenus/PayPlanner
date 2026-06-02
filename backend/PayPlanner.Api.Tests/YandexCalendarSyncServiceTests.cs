using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class YandexCalendarSyncServiceTests
{
    private static readonly IDataProtectionProvider TestDataProtectionProvider =
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pp-yandex-calendar-tests")));

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

    private static async Task<YandexCalendarConnection> CreateConnectionAsync(PaymentContext db, int userId, DateTime? syncFromDate = null)
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
            CalendarMode = "personal",
            ConflictResolutionMode = "latestWins",
            SyncFromDate = syncFromDate,
            IsEnabled = true,
            SyncEnabled = true,
        };
        db.YandexCalendarConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
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

        public RecordingYandexHandler(HttpStatusCode deleteStatusCode = HttpStatusCode.NoContent)
        {
            _deleteStatusCode = deleteStatusCode;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));

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
    }
}
