using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public class UserActivityControllerOnlineTests
{
    [Fact]
    public async Task GetOnlineAsync_ReturnsUsersWithOnlineStateAndLastLogin()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "admin", Description = "Administrator" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var onlineUser = new User
        {
            Email = "online@example.com",
            FullName = "Online User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var logoutUser = new User
        {
            Email = "logout@example.com",
            FullName = "Logout User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var inactiveUser = new User
        {
            Email = "inactive@example.com",
            FullName = "Inactive User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = false,
            IsApproved = true,
        };

        db.Users.AddRange(onlineUser, logoutUser, inactiveUser);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        db.UserActivityLogs.AddRange(
            new UserActivityLog
            {
                UserId = onlineUser.Id,
                UserEmail = onlineUser.Email,
                UserFullName = onlineUser.FullName,
                Category = "auth",
                Action = "login",
                Section = "auth",
                Status = UserActivityStatus.Success,
                CreatedAt = now.AddMinutes(-4),
            },
            new UserActivityLog
            {
                UserId = onlineUser.Id,
                UserEmail = onlineUser.Email,
                UserFullName = onlineUser.FullName,
                Category = "session",
                Action = "heartbeat",
                Section = "session",
                Status = UserActivityStatus.Info,
                IpAddress = "127.0.0.1",
                CreatedAt = now.AddMinutes(-1),
            },
            new UserActivityLog
            {
                UserId = logoutUser.Id,
                UserEmail = logoutUser.Email,
                UserFullName = logoutUser.FullName,
                Category = "auth",
                Action = "logout",
                Section = "auth",
                Status = UserActivityStatus.Info,
                CreatedAt = now.AddMinutes(-1),
            },
            new UserActivityLog
            {
                UserId = inactiveUser.Id,
                UserEmail = inactiveUser.Email,
                UserFullName = inactiveUser.FullName,
                Category = "session",
                Action = "heartbeat",
                Section = "session",
                Status = UserActivityStatus.Info,
                CreatedAt = now.AddMinutes(-1),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.GetOnlineAsync(CancellationToken.None);
        var summary = AssertOk<UserOnlineSummaryDto>(result);

        Assert.Equal(3, summary.TotalUsers);
        Assert.Equal(1, summary.OnlineUsers);

        var online = Assert.Single(summary.Users, user => user.UserId == onlineUser.Id);
        Assert.True(online.IsOnline);
        Assert.Equal("heartbeat", online.LastAction);
        Assert.Equal("127.0.0.1", online.LastIpAddress);
        Assert.NotNull(online.LastLoginAt);

        var loggedOut = Assert.Single(summary.Users, user => user.UserId == logoutUser.Id);
        Assert.False(loggedOut.IsOnline);
        Assert.Equal("logout", loggedOut.LastAction);

        var inactive = Assert.Single(summary.Users, user => user.UserId == inactiveUser.Id);
        Assert.False(inactive.IsOnline);
    }

    private static UserActivityController CreateController(PaymentContext db)
        => new(
            db,
            new FakeUserActivityService(),
            new FakeSqliteMaintenanceService(),
            NullLogger<UserActivityController>.Instance);

    private static T AssertOk<T>(ActionResult<T> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<T>(ok.Value);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FakeUserActivityService : IUserActivityService
    {
        public Task<UserActivitySettingsState> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserActivitySettingsState(true, null, DateTime.UtcNow));

        public Task<UserActivitySettingsState> UpdateSettingsAsync(bool isEnabled, int? retentionDays, CancellationToken cancellationToken = default)
            => Task.FromResult(new UserActivitySettingsState(isEnabled, retentionDays, DateTime.UtcNow));

        public Task<UserActivityLog?> TryWriteAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default)
            => Task.FromResult<UserActivityLog?>(null);
    }

    private sealed class FakeSqliteMaintenanceService : ISqliteMaintenanceService
    {
        private static readonly SqliteStorageStats EmptyStats = new(0, 0, 0, 0, 0);

        public Task<SqliteStorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyStats);

        public Task<SqliteStorageStats> CheckpointAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyStats);

        public Task<(SqliteStorageStats Before, SqliteStorageStats After)> CompactAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((EmptyStats, EmptyStats));
    }
}
