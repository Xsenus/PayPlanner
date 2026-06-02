using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
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

    private static ExternalCalendarSyncCoordinator CreateCoordinator(
        IGoogleCalendarSyncService google,
        IYandexCalendarSyncService yandex,
        PaymentContext? db = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(google)
            .AddSingleton(yandex);
        if (db is not null)
        {
            services.AddSingleton(db);
        }

        return new ExternalCalendarSyncCoordinator(
            services.BuildServiceProvider(),
            NullLogger<ExternalCalendarSyncCoordinator>.Instance);
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
        public bool FailDelete { get; set; }

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
            => Task.CompletedTask;

        public Task HandleWebhookAsync(HttpRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeYandexCalendarSyncService : IYandexCalendarSyncService
    {
        public List<int> SyncedTaskIds { get; } = [];
        public List<int> DeletedTaskIds { get; } = [];
        public bool FailDelete { get; set; }

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
            => Task.CompletedTask;
    }
}
