using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ExternalCalendarSyncCoordinatorTests
{
    [Fact]
    public async Task SyncTaskForConnectedUsers_FromCrm_SyncsBothProviders()
    {
        var google = new FakeGoogleCalendarSyncService();
        var yandex = new FakeYandexCalendarSyncService();
        var coordinator = CreateCoordinator(google, yandex);

        await coordinator.SyncTaskForConnectedUsersAsync(42);

        Assert.Equal([42], google.SyncedTaskIds);
        Assert.Equal([42], yandex.SyncedTaskIds);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsers_FromGoogle_SkipsGoogleAndPropagatesToYandex()
    {
        var google = new FakeGoogleCalendarSyncService();
        var yandex = new FakeYandexCalendarSyncService();
        var coordinator = CreateCoordinator(google, yandex);

        await coordinator.SyncTaskForConnectedUsersAsync(42, ExternalCalendarSyncCoordinator.GoogleProvider);

        Assert.Empty(google.SyncedTaskIds);
        Assert.Equal([42], yandex.SyncedTaskIds);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsers_FromYandex_SkipsYandexAndPropagatesToGoogle()
    {
        var google = new FakeGoogleCalendarSyncService();
        var yandex = new FakeYandexCalendarSyncService();
        var coordinator = CreateCoordinator(google, yandex);

        await coordinator.SyncTaskForConnectedUsersAsync(42, ExternalCalendarSyncCoordinator.YandexProvider);

        Assert.Equal([42], google.SyncedTaskIds);
        Assert.Empty(yandex.SyncedTaskIds);
    }

    [Fact]
    public async Task DeleteTaskForConnectedUsers_CollectsFailuresAfterTryingBothProviders()
    {
        var google = new FakeGoogleCalendarSyncService { FailDelete = true };
        var yandex = new FakeYandexCalendarSyncService { FailDelete = true };
        var coordinator = CreateCoordinator(google, yandex);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.DeleteTaskForConnectedUsersAsync(42));

        Assert.StartsWith("ExternalCalendarTaskDeleteFailed", error.Message, StringComparison.Ordinal);
        Assert.Equal([42], google.DeletedTaskIds);
        Assert.Equal([42], yandex.DeletedTaskIds);
    }

    [Fact]
    public async Task SyncTaskForConnectedUsers_WhenProviderFails_NotifiesAdminsOnly()
    {
        await using var db = await CreateContextAsync();
        var adminRole = new Role { Name = "admin", Description = "Admin" };
        var userRole = new Role { Name = "user", Description = "User" };
        db.Roles.AddRange(adminRole, userRole);
        await db.SaveChangesAsync();
        var admin = new User
        {
            Email = "external-calendar-admin@example.com",
            FullName = "Администратор",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var employee = new User
        {
            Email = "external-calendar-user@example.com",
            FullName = "Сотрудник",
            PasswordHash = "hash",
            RoleId = userRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(admin, employee);
        await db.SaveChangesAsync();
        var google = new FakeGoogleCalendarSyncService { FailSync = true };
        var yandex = new FakeYandexCalendarSyncService();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var notificationService = CreateNotificationService(db, realtime);
        var coordinator = CreateCoordinator(google, yandex, db, notificationService);

        await coordinator.SyncTaskForConnectedUsersAsync(42);

        Assert.Equal([42], google.SyncedTaskIds);
        Assert.Equal([42], yandex.SyncedTaskIds);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(admin.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.SystemAdminWarning, notification.Type);
        Assert.Equal("system", notification.ObjectType);
        Assert.Contains("Google Calendar", notification.Title);
        Assert.Contains("Не удалось синхронизировать задачу #42", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == employee.Id);
        var published = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, published.Id);
    }

    [Fact]
    public async Task HandleUserAccessRevokedAsync_WhenPolicyDisablesSyncOnly_DisablesConnectionsAndLogs()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "user", Description = "User" };
        var user = new User
        {
            Email = "calendar-access@example.com",
            FullName = "Calendar Access",
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "calendarSync.settings",
            Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
            {
                SyncMode = "hybrid",
                ConflictResolutionMode = "latestWins",
                UserControlMode = "adminManaged",
                InactiveUserAction = "disableSyncOnly",
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.GoogleCalendarConnections.Add(new GoogleCalendarConnection
        {
            UserId = user.Id,
            GoogleUserId = "google-user",
            GoogleEmail = "calendar-access@gmail.com",
            IsEnabled = true,
            SyncEnabled = true,
        });
        db.YandexCalendarConnections.Add(new YandexCalendarConnection
        {
            UserId = user.Id,
            YandexUserId = "yandex-user",
            YandexEmail = "calendar-access@yandex.ru",
            IsEnabled = true,
            SyncEnabled = true,
        });
        await db.SaveChangesAsync();
        var coordinator = CreateCoordinator(new FakeGoogleCalendarSyncService(), new FakeYandexCalendarSyncService(), db);

        await coordinator.HandleUserAccessRevokedAsync(user.Id, actorUserId: 100);

        var googleConnection = await db.GoogleCalendarConnections.SingleAsync();
        var yandexConnection = await db.YandexCalendarConnections.SingleAsync();
        Assert.False(googleConnection.IsEnabled);
        Assert.False(googleConnection.SyncEnabled);
        Assert.False(yandexConnection.IsEnabled);
        Assert.False(yandexConnection.SyncEnabled);
        Assert.Contains(await db.GoogleCalendarSyncLogs.ToListAsync(), item => item.Operation == "disable-sync" && item.Status == "success");
        Assert.Contains(await db.YandexCalendarSyncLogs.ToListAsync(), item => item.Operation == "disable-sync" && item.Status == "success");
    }

    [Fact]
    public async Task HandleUserAccessRevokedAsync_WhenCleanupFails_DisablesSyncAndDoesNotThrow()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "user", Description = "User" };
        var user = new User
        {
            Email = "calendar-cleanup-failure@example.com",
            FullName = "Calendar Cleanup Failure",
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
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
        db.GoogleCalendarConnections.Add(new GoogleCalendarConnection
        {
            UserId = user.Id,
            GoogleUserId = "google-user",
            GoogleEmail = "calendar-cleanup-failure@gmail.com",
            IsEnabled = true,
            SyncEnabled = true,
        });
        db.YandexCalendarConnections.Add(new YandexCalendarConnection
        {
            UserId = user.Id,
            YandexUserId = "yandex-user",
            YandexEmail = "calendar-cleanup-failure@yandex.ru",
            IsEnabled = true,
            SyncEnabled = true,
        });
        await db.SaveChangesAsync();
        var google = new FakeGoogleCalendarSyncService { FailCleanup = true };
        var yandex = new FakeYandexCalendarSyncService { FailCleanup = true };
        var coordinator = CreateCoordinator(google, yandex, db);

        await coordinator.HandleUserAccessRevokedAsync(user.Id, actorUserId: 100);

        Assert.Equal([user.Id], google.CleanedUserIds);
        Assert.Equal([user.Id], yandex.CleanedUserIds);
        var googleConnection = await db.GoogleCalendarConnections.SingleAsync();
        var yandexConnection = await db.YandexCalendarConnections.SingleAsync();
        Assert.False(googleConnection.IsEnabled);
        Assert.False(googleConnection.SyncEnabled);
        Assert.False(yandexConnection.IsEnabled);
        Assert.False(yandexConnection.SyncEnabled);
    }

    private static ExternalCalendarSyncCoordinator CreateCoordinator(
        IGoogleCalendarSyncService google,
        IYandexCalendarSyncService yandex,
        PaymentContext? db = null,
        INotificationService? notificationService = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(google)
            .AddSingleton(yandex);
        if (db is not null)
        {
            services.AddSingleton(db);
        }
        if (notificationService is not null)
        {
            services.AddSingleton(notificationService);
        }

        return new ExternalCalendarSyncCoordinator(
            services.BuildServiceProvider(),
            NullLogger<ExternalCalendarSyncCoordinator>.Instance);
    }

    private static NotificationService CreateNotificationService(
        PaymentContext db,
        IRealtimeNotificationPublisher realtimePublisher)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new NotificationService(
            db,
            configuration,
            new NoOpEmailService(),
            new NoOpTelegramNotificationService(),
            NullLogger<NotificationService>.Instance,
            realtimePublisher);
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

    private sealed class FakeGoogleCalendarSyncService : IGoogleCalendarSyncService
    {
        public List<int> SyncedTaskIds { get; } = [];
        public List<int> DeletedTaskIds { get; } = [];
        public List<int> CleanedUserIds { get; } = [];
        public bool FailSync { get; set; }
        public bool FailDelete { get; set; }
        public bool FailCleanup { get; set; }

        public Task<GoogleCalendarStatusDto> GetStatusAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GoogleCalendarStatusDto());

        public Task<GoogleCalendarStartAuthResponseDto> StartAuthAsync(int userId, StartGoogleCalendarAuthRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default)
            => Task.FromResult(new GoogleCalendarStartAuthResponseDto());

        public Task<GoogleCalendarAuthStatusDto> GetAuthStatusAsync(int userId, string state, CancellationToken cancellationToken = default)
            => Task.FromResult(new GoogleCalendarAuthStatusDto());

        public Task<string> HandleCallbackAsync(string? state, string? code, string? error, string? errorDescription, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<GoogleCalendarStatusDto> UpdateSettingsAsync(int userId, UpdateGoogleCalendarSettingsRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default)
            => Task.FromResult(new GoogleCalendarStatusDto());

        public Task<GoogleCalendarStatusDto> DisconnectAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GoogleCalendarStatusDto());

        public Task<GoogleCalendarSyncNowResultDto> SyncNowAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GoogleCalendarSyncNowResultDto());

        public Task<GoogleCalendarSchedulerRunResult> SyncDueConnectionsAsync(int batchSize, CancellationToken cancellationToken = default)
            => Task.FromResult(new GoogleCalendarSchedulerRunResult(0, 0, 0, 0));

        public Task SyncTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default)
        {
            SyncedTaskIds.Add(taskId);
            if (FailSync)
            {
                throw new InvalidOperationException("GoogleCalendarSyncFailed");
            }

            return Task.CompletedTask;
        }

        public Task DeleteTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default)
        {
            DeletedTaskIds.Add(taskId);
            if (FailDelete)
            {
                throw new InvalidOperationException("GoogleCalendarTaskDeleteFailed");
            }

            return Task.CompletedTask;
        }

        public Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
        {
            CleanedUserIds.Add(userId);
            if (FailCleanup)
            {
                throw new InvalidOperationException("GoogleCalendarCleanupFailed");
            }

            return Task.CompletedTask;
        }

        public Task HandleWebhookAsync(HttpRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeYandexCalendarSyncService : IYandexCalendarSyncService
    {
        public List<int> SyncedTaskIds { get; } = [];
        public List<int> DeletedTaskIds { get; } = [];
        public List<int> CleanedUserIds { get; } = [];
        public bool FailSync { get; set; }
        public bool FailDelete { get; set; }
        public bool FailCleanup { get; set; }

        public Task<YandexCalendarStatusDto> GetStatusAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexCalendarStatusDto());

        public Task<YandexCalendarStartAuthResponseDto> StartAuthAsync(int userId, StartYandexCalendarAuthRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexCalendarStartAuthResponseDto());

        public Task<YandexCalendarAuthStatusDto> GetAuthStatusAsync(int userId, string state, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexCalendarAuthStatusDto());

        public Task<string> HandleCallbackAsync(string? state, string? code, string? error, string? errorDescription, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<YandexCalendarStatusDto> UpdateSettingsAsync(int userId, UpdateYandexCalendarSettingsRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexCalendarStatusDto());

        public Task<YandexCalendarStatusDto> DisconnectAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexCalendarStatusDto());

        public Task<YandexCalendarSyncNowResultDto> SyncNowAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexCalendarSyncNowResultDto());

        public Task<YandexCalendarSchedulerRunResult> SyncDueConnectionsAsync(int batchSize, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexCalendarSchedulerRunResult(0, 0, 0, 0));

        public Task SyncTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default)
        {
            SyncedTaskIds.Add(taskId);
            if (FailSync)
            {
                throw new InvalidOperationException("YandexCalendarSyncFailed");
            }

            return Task.CompletedTask;
        }

        public Task DeleteTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default)
        {
            DeletedTaskIds.Add(taskId);
            if (FailDelete)
            {
                throw new InvalidOperationException("YandexCalendarTaskDeleteFailed");
            }

            return Task.CompletedTask;
        }

        public Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
        {
            CleanedUserIds.Add(userId);
            if (FailCleanup)
            {
                throw new InvalidOperationException("YandexCalendarCleanupFailed");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRealtimeNotificationPublisher : IRealtimeNotificationPublisher
    {
        public List<UserNotification> Notifications { get; } = [];

        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
            => Task.CompletedTask;

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string body,
            string attachmentFileName,
            byte[] attachmentBytes,
            string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoOpTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());

        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(long chatId, string text, string botToken, CancellationToken cancellationToken = default, TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));
    }
}
