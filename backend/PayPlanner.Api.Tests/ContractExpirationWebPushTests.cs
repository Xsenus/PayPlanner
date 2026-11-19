using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ContractExpirationWebPushTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractExpiration_PushConsumerRechecksSelectionForEverySubscription(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var notification = await PrepareAsync(scope, 2);
        var push = new GuardedPush(scope.Db, async () =>
        {
            (await scope.Db.SystemSettings.SingleAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey)).Value
                = ContractExpirationRecipientSettings.Serialize(new());
            await scope.Db.SaveChangesAsync();
        });

        await push.SendUserNotificationAsync(notification);

        Assert.Equal(1, push.Sends);
        var deliveries = await scope.Db.NotificationDeliveries.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, deliveries.Length);
        Assert.Single(deliveries, item => item.Status == NotificationDeliveryStatus.Sent);
        Assert.Single(deliveries, item => item.Status == NotificationDeliveryStatus.Skipped);
        Assert.Equal(notification.Id, (await scope.Db.UserNotifications.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(false, "selection")]
    [InlineData(true, "selection")]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    public async Task ContractExpiration_PushRetryRejectsStaleSourceWithoutCallingProvider(bool postgres, string change)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var notification = await PrepareAsync(scope, 1);
        var push = new GuardedPush(scope.Db, fail: true);
        await push.SendUserNotificationAsync(notification);
        var original = await scope.Db.NotificationDeliveries.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, original.Status);
        Assert.Equal(1, push.Sends);
        if (change == "selection") (await scope.Db.SystemSettings.SingleAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey)).Value
            = ContractExpirationRecipientSettings.Serialize(new());
        else (await scope.Db.Contracts.SingleAsync(item => item.Id == notification.ObjectId)).Status = ContractStatus.Terminated;
        await scope.Db.SaveChangesAsync();

        var retry = await push.RetryDeliveryAsync(original.Id);

        Assert.NotNull(retry);
        Assert.Equal(NotificationDeliveryStatus.Skipped, retry!.Status);
        Assert.Equal(1, push.Sends);
        Assert.Equal(NotificationDeliveryStatus.Failed, original.Status);
        Assert.Equal(2, await scope.Db.NotificationDeliveries.CountAsync());
        Assert.Equal(notification.Id, (await scope.Db.UserNotifications.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractExpiration_PushRejectsAnExpirationWithoutCanonicalSource(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var notification = await PrepareAsync(scope, 1);
        notification.EventKey = "opaque-expiration";
        await scope.Db.SaveChangesAsync();
        var push = new GuardedPush(scope.Db);

        await push.SendUserNotificationAsync(notification);

        Assert.Equal(0, push.Sends);
        Assert.Equal(NotificationDeliveryStatus.Skipped, (await scope.Db.NotificationDeliveries.SingleAsync()).Status);
        Assert.Equal(notification.Id, (await scope.Db.UserNotifications.SingleAsync()).Id);
    }

    private static async Task<UserNotification> PrepareAsync(ActAnnualTestScope scope, int subscriptions)
    {
        scope.Source.Other.Role!.Permissions.Add(new RolePermission
        { Section = RolePermissionCatalog.Contracts, CanView = true, ViewScope = DataVisibilityScope.All });
        var contract = new Contract
        {
            Number = "PUSH-EXPIRY",
            Date = DateTime.UtcNow.Date,
            ValidUntil = DateTime.UtcNow.Date.AddDays(20),
            Status = ContractStatus.Concluded,
            ClientContracts = { new ClientContract { ClientId = scope.Source.OwnClient.Id } }
        };
        scope.Db.Contracts.Add(contract);
        scope.Db.SystemSettings.Add(new SystemSetting
        {
            Key = ContractExpirationRecipientSettings.SettingKey,
            Value = ContractExpirationRecipientSettings.Serialize(new() { UserIds = [scope.Source.Other.Id] })
        });
        for (var index = 0; index < subscriptions; index++) scope.Db.UserPushSubscriptions.Add(new UserPushSubscription
        { UserId = scope.Source.Other.Id, Endpoint = "https://push.example.test/" + index, Auth = "auth", P256dh = "key", IsActive = true });
        await scope.Db.SaveChangesAsync();
        var notification = new UserNotification
        {
            RecipientUserId = scope.Source.Other.Id,
            Type = UserNotificationTypes.ContractExpiring,
            ObjectType = "contract",
            ObjectId = contract.Id,
            EventKey = ContractExpirationNotificationPolicy.EventKey(contract.Id, scope.Source.Other.Id, contract.ValidUntil!.Value, 30),
            Title = "Договор",
            BodyPreview = "Срок подходит к концу"
        };
        scope.Db.UserNotifications.Add(notification);
        await scope.Db.SaveChangesAsync();
        return notification;
    }

    private sealed class GuardedPush(PaymentContext db, Func<Task>? afterSend = null, bool fail = false)
        : WebPushNotificationService(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebPush:Enabled"] = "true",
            ["WebPush:PublicKey"] = "public-key",
            ["WebPush:PrivateKey"] = "private-key",
            ["WebPush:Subject"] = "mailto:synthetic@example.test",
            ["App:FrontendUrl"] = "https://crm.example.test/"
        }).Build(), NullLogger<WebPushNotificationService>.Instance)
    {
        public int Sends { get; private set; }
        protected override async Task SendSubscriptionAsync(UserPushSubscription subscription, string payload, string subject,
            string publicKey, string privateKey, CancellationToken cancellationToken)
        {
            Sends++;
            if (fail) throw new InvalidOperationException("Synthetic push failure");
            if (afterSend != null) await afterSend();
        }
    }
}
