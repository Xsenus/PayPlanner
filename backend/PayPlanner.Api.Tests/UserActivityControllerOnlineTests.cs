using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task AdminFilter_ReturnsForbid_ForUserWithoutAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "user");
        var controller = CreateController(db);
        var context = CreateAdminActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.False(nextCalled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task AdminFilter_AllowsUserWithAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin");
        var controller = CreateController(db);
        var context = CreateAdminActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task GetOnlineAsync_ReturnsUsersWithOnlineStateAndLastLogin()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
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
        interceptor.Commands.Clear();
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
        Assert.NotNull(loggedOut.LastLogoutAt);

        var inactive = Assert.Single(summary.Users, user => user.UserId == inactiveUser.Id);
        Assert.False(inactive.IsOnline);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"UserActivityLogs\"", StringComparison.Ordinal));
        Assert.DoesNotContain(interceptor.Commands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal) &&
            command.Contains("UserActivityLogs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOnlineAsync_DoesNotReadLogs_WhenPresenceFieldsAreComplete()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var user = await CreateUserAsync(db, "admin");
        var now = DateTime.UtcNow;
        user.LastActivityAt = now.AddMinutes(-1);
        user.LastLoginAt = now.AddMinutes(-5);
        user.LastLogoutAt = now.AddDays(-1);
        user.LastActivityAction = "heartbeat";
        user.LastActivitySection = "session";
        user.LastActivityIpAddress = "127.0.0.1";
        user.LastActivityUserAgent = "test-browser";
        await db.SaveChangesAsync();

        interceptor.Commands.Clear();
        var summary = AssertOk<UserOnlineSummaryDto>(
            await CreateController(db).GetOnlineAsync(CancellationToken.None));

        var current = Assert.Single(summary.Users);
        Assert.Equal(user.Id, current.UserId);
        Assert.True(current.IsOnline);
        Assert.Equal("heartbeat", current.LastAction);
        Assert.Single(interceptor.Commands);
        Assert.DoesNotContain(interceptor.Commands, command =>
            command.Contains("UserActivityLogs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HeartbeatAsync_UpdatesUserPresenceWithoutActivityLog()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "admin", Description = "Administrator" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "current@example.com",
            FullName = "Current User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
                    "test"))
            }
        };
        controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "test-browser";
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

        var heartbeatResult = await controller.HeartbeatAsync(CancellationToken.None);

        Assert.IsType<NoContentResult>(heartbeatResult);

        var updatedUser = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.NotNull(updatedUser.LastActivityAt);
        Assert.Equal("heartbeat", updatedUser.LastActivityAction);
        Assert.Equal("session", updatedUser.LastActivitySection);
        Assert.Equal("127.0.0.1", updatedUser.LastActivityIpAddress);
        Assert.Equal("test-browser", updatedUser.LastActivityUserAgent);
        Assert.Empty(await db.UserActivityLogs.AsNoTracking().ToListAsync());

        var summary = AssertOk<UserOnlineSummaryDto>(await controller.GetOnlineAsync(CancellationToken.None));
        var current = Assert.Single(summary.Users, item => item.UserId == user.Id);
        Assert.True(current.IsOnline);
        Assert.Equal("heartbeat", current.LastAction);
    }

    [Fact]
    public async Task GetMaintenanceAsync_LoadsLogStatisticsWithSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var controller = CreateController(db);

        interceptor.Commands.Clear();
        var empty = AssertOk<UserActivityMaintenanceDto>(
            await controller.GetMaintenanceAsync(CancellationToken.None));

        Assert.Equal(0, empty.LogEntries);
        Assert.Null(empty.OldestLogAt);
        Assert.Null(empty.NewestLogAt);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"UserActivityLogs\"", StringComparison.Ordinal));

        var oldest = DateTime.UtcNow.AddDays(-2);
        var newest = DateTime.UtcNow.AddMinutes(-5);
        db.UserActivityLogs.AddRange(
            new UserActivityLog { Category = "test", Action = "oldest", Status = UserActivityStatus.Info, CreatedAt = oldest },
            new UserActivityLog { Category = "test", Action = "newest", Status = UserActivityStatus.Success, CreatedAt = newest });
        await db.SaveChangesAsync();

        interceptor.Commands.Clear();
        var populated = AssertOk<UserActivityMaintenanceDto>(
            await controller.GetMaintenanceAsync(CancellationToken.None));

        Assert.Equal(2, populated.LogEntries);
        Assert.Equal(oldest, populated.OldestLogAt);
        Assert.Equal(newest, populated.NewestLogAt);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"UserActivityLogs\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAsync_PrefersExplicitUtcBoundaries()
    {
        await using var db = await CreateContextAsync();
        db.UserActivityLogs.AddRange(
            new UserActivityLog
            {
                Category = "test",
                Action = "before",
                CreatedAt = new DateTime(2026, 8, 15, 16, 59, 59, DateTimeKind.Utc),
            },
            new UserActivityLog
            {
                Category = "test",
                Action = "inside",
                CreatedAt = new DateTime(2026, 8, 15, 17, 0, 0, DateTimeKind.Utc),
            },
            new UserActivityLog
            {
                Category = "test",
                Action = "after",
                CreatedAt = new DateTime(2026, 8, 16, 17, 0, 1, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();

        var result = await CreateController(db).GetAsync(
            from: new DateTime(2030, 1, 1),
            to: new DateTime(2030, 1, 2),
            fromUtc: new DateTimeOffset(2026, 8, 15, 17, 0, 0, TimeSpan.Zero),
            toUtc: new DateTimeOffset(2026, 8, 16, 16, 59, 0, TimeSpan.Zero),
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        var value = ok.Value!;
        var items = Assert.IsAssignableFrom<IEnumerable<UserActivityLogDto>>(
            value.GetType().GetProperty("items")?.GetValue(value));
        var item = Assert.Single(items);
        Assert.Equal("inside", item.Action);
    }

    [Fact]
    public async Task GetAsync_UsesVisibleLabelAliasesForSearch()
    {
        await using var db = await CreateContextAsync();
        db.UserActivityLogs.AddRange(
            new UserActivityLog { Category = "Clients", Action = "GetAll", Description = "Clients.GetAll | HTTP 200" },
            new UserActivityLog { Category = "NotificationInbox", Action = "GetNotifications", Description = "NotificationInbox.GetNotifications | HTTP 200" });
        await db.SaveChangesAsync();

        var result = await CreateController(db).GetAsync(
            search: "клиенты",
            searchAliases: ["Clients"],
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        var value = ok.Value!;
        var items = Assert.IsAssignableFrom<IEnumerable<UserActivityLogDto>>(
            value.GetType().GetProperty("items")?.GetValue(value));
        var item = Assert.Single(items);
        Assert.Equal("Clients", item.Category);
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

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var db = new PaymentContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string roleName)
    {
        var role = new Role
        {
            Name = roleName,
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"{roleName}-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = roleName,
            IsActive = true,
            IsApproved = true,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ActionExecutingContext CreateAdminActionExecutingContext(
        UserActivityController controller,
        int userId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, $"User {userId}"),
                    new Claim(ClaimTypes.Role, "admin"),
                ],
                "TestAuth")),
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor
            {
                EndpointMetadata = [new AuthorizeAttribute { Policy = "Admin" }],
            });
        controller.ControllerContext = new ControllerContext(actionContext);

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller);
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
