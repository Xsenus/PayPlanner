using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PushSubscriptionsControllerTests
{
    [Fact]
    public async Task UpsertSubscription_ListsAndRevokesCurrentUserSubscription()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "push-user@example.com", "user");
        var controller = CreateController(db, user.Id);

        var upsert = await controller.UpsertSubscription(new UpsertPushSubscriptionRequest
        {
            Endpoint = " https://push.example/subscription ",
            Keys = new PushSubscriptionKeysRequest
            {
                P256dh = "p256dh",
                Auth = "auth",
            },
            Browser = "Chrome",
            Platform = "Windows",
            DeviceName = "Workstation",
            UserAgent = new string('a', 600),
        }, CancellationToken.None);

        var created = AssertOk<UserPushSubscriptionDto>(upsert);
        Assert.Equal(user.Id, await db.UserPushSubscriptions.Select(item => item.UserId).SingleAsync());
        Assert.Equal("https://push.example/subscription", created.Endpoint);
        Assert.True(created.IsActive);

        var list = AssertOk<IReadOnlyList<UserPushSubscriptionDto>>(
            await controller.GetSubscriptions(CancellationToken.None));
        var listed = Assert.Single(list);
        Assert.Equal(created.Id, listed.Id);

        var revoke = await controller.RevokeSubscriptionById(created.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(revoke);

        var subscription = await db.UserPushSubscriptions.SingleAsync();
        Assert.False(subscription.IsActive);
        Assert.NotNull(subscription.RevokedAt);
        Assert.Equal(512, subscription.UserAgent.Length);
    }

    [Fact]
    public async Task GetDiagnostics_ReturnsAggregatedWebPushStateForNotificationAdmins()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var admin = await CreateUserAsync(db, "admin@example.com", "admin");
        var user = await CreateUserAsync(db, "push-user@example.com", "user");
        var now = DateTime.UtcNow;
        db.UserPushSubscriptions.AddRange(
            new UserPushSubscription
            {
                UserId = user.Id,
                Endpoint = "https://push.example/active",
                P256dh = "p256dh",
                Auth = "auth",
                IsActive = true,
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now.AddMinutes(-5),
                LastSeenAt = now.AddMinutes(-5),
            },
            new UserPushSubscription
            {
                UserId = user.Id,
                Endpoint = "https://push.example/revoked",
                P256dh = "p256dh",
                Auth = "auth",
                IsActive = false,
                CreatedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-15),
                RevokedAt = now.AddMinutes(-15),
                LastErrorAt = now.AddMinutes(-12),
                LastError = "Gone",
            });
        db.NotificationDeliveries.AddRange(
            new NotificationDelivery
            {
                EventKey = "push:sent",
                Channel = NotificationChannels.WebPush,
                SenderName = "Web Push",
                RecipientUserId = user.Id,
                RecipientAddress = "https://push.example/active",
                Subject = "Sent",
                Status = NotificationDeliveryStatus.Sent,
                CreatedAt = now.AddMinutes(-4),
                SentAt = now.AddMinutes(-3),
            },
            new NotificationDelivery
            {
                EventKey = "push:failed",
                Channel = NotificationChannels.WebPush,
                SenderName = "Web Push",
                RecipientUserId = user.Id,
                RecipientAddress = "https://push.example/revoked",
                Subject = "Failed",
                Status = NotificationDeliveryStatus.Failed,
                Error = "Network error",
                CreatedAt = now.AddMinutes(-2),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, admin.Id, new RecordingWebPushNotificationService(
            new WebPushPublicKeyStatus(true, false, string.Empty, true, false, true)));
        interceptor.Commands.Clear();

        var diagnostics = AssertOk<WebPushDiagnosticsDto>(
            await controller.GetDiagnostics(CancellationToken.None));

        Assert.True(diagnostics.Enabled);
        Assert.False(diagnostics.Ready);
        Assert.True(diagnostics.PublicKeyConfigured);
        Assert.False(diagnostics.PrivateKeyConfigured);
        Assert.True(diagnostics.SubjectConfigured);
        Assert.Equal(2, diagnostics.TotalSubscriptionsCount);
        Assert.Equal(1, diagnostics.ActiveSubscriptionsCount);
        Assert.Equal(1, diagnostics.RevokedSubscriptionsCount);
        Assert.Equal(1, diagnostics.FailedSubscriptionsCount);
        Assert.Equal(1, diagnostics.UsersWithActiveSubscriptionsCount);
        Assert.NotNull(diagnostics.LastSuccessfulDeliveryAt);
        Assert.NotNull(diagnostics.LastFailedDeliveryAt);
        Assert.Equal("Network error", diagnostics.LastDeliveryError);
        Assert.Equal(3, interceptor.Commands.Count(command =>
            command.Contains("FROM \"UserPushSubscriptions\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetSubscriptions_DeactivatesStoredVapidMismatchSubscription()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "push-stale@example.com", "user");
        var now = DateTime.UtcNow;
        db.UserPushSubscriptions.Add(new UserPushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://push.example/stale-vapid",
            P256dh = "p256dh",
            Auth = "auth",
            Browser = "Chrome",
            Platform = "Windows",
            IsActive = true,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-1),
            LastSeenAt = now.AddDays(-1),
            LastErrorAt = now.AddHours(-2),
            LastError = "Received unexpected response code: 403. Details: the VAPID credentials in the authorization header do not correspond to the credentials used to create the subscriptions.",
        });
        await db.SaveChangesAsync();

        var subscriptions = AssertOk<IReadOnlyList<UserPushSubscriptionDto>>(
            await CreateController(db, user.Id).GetSubscriptions(CancellationToken.None));

        var listed = Assert.Single(subscriptions);
        Assert.False(listed.IsActive);
        Assert.NotNull(listed.RevokedAt);

        var stored = await db.UserPushSubscriptions.SingleAsync();
        Assert.False(stored.IsActive);
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task GetDiagnostics_ReplacesTechnicalWebPushErrorWithUserMessage()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "push-admin-error@example.com", "admin");
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "push:failed:payload",
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = admin.Id,
            RecipientAddress = "https://push.example/payload-error",
            Subject = "Failed",
            Status = NotificationDeliveryStatus.Failed,
            Error = "Bad Request. Details: binary data passed in the request must be less than 4096 bytes.",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, admin.Id, new RecordingWebPushNotificationService(
            new WebPushPublicKeyStatus(true, true, "public-key", true, true, true)));
        var diagnostics = AssertOk<WebPushDiagnosticsDto>(
            await controller.GetDiagnostics(CancellationToken.None));

        Assert.Equal(
            "Размер данных Push-уведомления превышает допустимый лимит.",
            diagnostics.LastDeliveryError);
        Assert.DoesNotContain("4096", diagnostics.LastDeliveryError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiagnostics_DeactivatesStoredExpiredSubscriptionBeforeCounting()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "push-admin@example.com", "admin");
        var user = await CreateUserAsync(db, "push-expired@example.com", "user");
        var now = DateTime.UtcNow;
        db.UserPushSubscriptions.Add(new UserPushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://push.example/expired",
            P256dh = "p256dh",
            Auth = "auth",
            IsActive = true,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-1),
            LastErrorAt = now.AddHours(-2),
            LastError = "Subscription no longer valid: push subscription has unsubscribed or expired.",
        });
        await db.SaveChangesAsync();

        var diagnostics = AssertOk<WebPushDiagnosticsDto>(
            await CreateController(db, admin.Id).GetDiagnostics(CancellationToken.None));

        Assert.Equal(1, diagnostics.TotalSubscriptionsCount);
        Assert.Equal(0, diagnostics.ActiveSubscriptionsCount);
        Assert.Equal(1, diagnostics.RevokedSubscriptionsCount);

        var stored = await db.UserPushSubscriptions.SingleAsync();
        Assert.False(stored.IsActive);
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task GetDiagnostics_HidesStaleInvalidDeliveryErrorAfterRevokingSubscription()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "push-admin-stale-delivery@example.com", "admin");
        var user = await CreateUserAsync(db, "push-user-stale-delivery@example.com", "user");
        var now = DateTime.UtcNow;
        db.UserPushSubscriptions.Add(new UserPushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://push.example/stale-delivery",
            P256dh = "p256dh",
            Auth = "auth",
            IsActive = true,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddHours(-3),
            LastSeenAt = now.AddHours(-3),
        });
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "push:stale-vapid",
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = user.Id,
            RecipientAddress = "https://push.example/stale-delivery",
            Subject = "Failed",
            Status = NotificationDeliveryStatus.Failed,
            Error = "Received unexpected response code: 403. Details: the VAPID credentials in the authorization header do not correspond to the credentials used to create the subscriptions.",
            CreatedAt = now.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var diagnostics = AssertOk<WebPushDiagnosticsDto>(
            await CreateController(db, admin.Id).GetDiagnostics(CancellationToken.None));

        Assert.Equal(1, diagnostics.TotalSubscriptionsCount);
        Assert.Equal(0, diagnostics.ActiveSubscriptionsCount);
        Assert.Equal(1, diagnostics.RevokedSubscriptionsCount);
        Assert.Null(diagnostics.LastFailedDeliveryAt);
        Assert.Null(diagnostics.LastDeliveryError);

        var stored = await db.UserPushSubscriptions.SingleAsync();
        Assert.False(stored.IsActive);
        Assert.NotNull(stored.RevokedAt);
        Assert.NotNull(stored.LastErrorAt);
        Assert.Contains("VAPID credentials", stored.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiagnostics_UsesNotificationViewPermission()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "notification-viewer@example.com", "notifications-viewer");
        var blocked = await CreateUserAsync(db, "notification-blocked@example.com", "notifications-blocked");
        await SetNotificationPermissionAsync(db, viewer, canView: true);
        await SetNotificationPermissionAsync(db, blocked, canView: false);

        var diagnostics = await CreateController(db, viewer.Id).GetDiagnostics(CancellationToken.None);
        var blockedResult = await CreateController(db, blocked.Id).GetDiagnostics(CancellationToken.None);

        Assert.IsType<OkObjectResult>(diagnostics.Result);
        Assert.IsType<ForbidResult>(blockedResult.Result);
    }

    private static PushSubscriptionsController CreateController(
        PaymentContext db,
        int userId,
        IWebPushNotificationService? webPushNotificationService = null)
        => new(db, webPushNotificationService ?? new RecordingWebPushNotificationService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                            new Claim(ClaimTypes.Name, "Push Test User"),
                        ],
                        authenticationType: "Test")),
                },
            },
        };

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string roleName)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == roleName);
        if (role is null)
        {
            role = new Role
            {
                Name = roleName,
                Description = roleName,
                CreatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task SetNotificationPermissionAsync(PaymentContext db, User user, bool canView)
    {
        var permission = await db.RolePermissions
            .SingleOrDefaultAsync(item => item.RoleId == user.RoleId && item.Section == RolePermissionCatalog.Notifications);
        if (permission is null)
        {
            permission = new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Notifications,
            };
            db.RolePermissions.Add(permission);
        }

        permission.CanView = canView;
        permission.CanCreate = false;
        permission.CanEdit = false;
        permission.CanDelete = false;
        permission.CanExport = false;
        permission.CanViewAnalytics = false;
        permission.ViewScope = DataVisibilityScope.All;
        await db.SaveChangesAsync();
    }

    private static T AssertOk<T>(ActionResult<T> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<T>(ok.Value);
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

    private sealed class RecordingWebPushNotificationService : IWebPushNotificationService
    {
        private readonly WebPushPublicKeyStatus _status;

        public RecordingWebPushNotificationService(WebPushPublicKeyStatus? status = null)
        {
            _status = status ?? new WebPushPublicKeyStatus(true, true, "public-key", true, true, true);
        }

        public WebPushPublicKeyStatus GetPublicKeyStatus() => _status;

        public Task SendUserNotificationAsync(UserNotification notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<NotificationDelivery?> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult<NotificationDelivery?>(null);
    }
}
