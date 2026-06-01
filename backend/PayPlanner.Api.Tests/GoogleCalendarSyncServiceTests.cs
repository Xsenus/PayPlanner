using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class GoogleCalendarSyncServiceTests
{
    private static readonly IDataProtectionProvider TestDataProtectionProvider =
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pp-google-tests")));

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
            new SystemSetting { Key = "auth.google.clientSecret", Value = "client-secret", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    private static async Task CreateConnectionAsync(PaymentContext db, int userId, string calendarMode = "personal")
    {
        var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1");
        db.GoogleCalendarConnections.Add(new GoogleCalendarConnection
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
            IsEnabled = true,
            SyncEnabled = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TaskJournalItem> CreateTaskAsync(PaymentContext db, int assigneeUserId, string title, string assigneeName = "User")
    {
        var task = new TaskJournalItem
        {
            ObjectName = "Тест",
            Title = title,
            AssigneeUserId = assigneeUserId,
            AssigneeName = assigneeName,
            DueAt = DateTime.UtcNow.AddDays(1),
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
                return Task.FromResult(Json(new { items = Array.Empty<object>(), nextSyncToken = "sync-token" }));
            }

            if (path.Contains("/events", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(new { id = "event-1", etag = "\"etag-1\"" }));
            }

            if (path.Contains("/events", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                return Task.FromResult(Json(new { id = "event-1", etag = "\"etag-2\"" }));
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
            => new(source.Method, source.RequestUri);
    }
}
