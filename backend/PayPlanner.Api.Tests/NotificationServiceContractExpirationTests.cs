using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationServiceTests
{
    [Fact]
    public async Task ContractExpiration_LegacySevenDayReceiptPreventsAnotherImmediateDelivery()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "legacy-expiry@example.test");
        var clock = new ContractExpiryClock();
        var contract = await NewExpiryContractAsync(db, recipient, clock.GetUtcNow().UtcDateTime.Date.AddDays(3));
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, recipient.Id, contract.ValidUntil!.Value, 7);
        db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = recipient.Id,
            Type = UserNotificationTypes.ContractExpiring,
            EventKey = key,
            ObjectType = "contract",
            ObjectId = contract.Id,
            Title = "Прежнее напоминание",
            BodyPreview = "Ранее отправлено"
        });
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime, push, timeProvider: clock);

        await service.NotifyContractExpiringAsync(contract, 3, [recipient.Id]);

        Assert.Equal("Ранее отправлено", (await db.UserNotifications.SingleAsync()).BodyPreview);
        Assert.Empty(realtime.Notifications);
        Assert.Empty(push.Notifications);
    }

    [Fact]
    public async Task ContractExpiration_ThreeWindowsCreateThreeDistinctReceiptsAndNoTickDuplicates()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "three-expiry@example.test");
        recipient.Role!.Name = "admin";
        var clock = new ContractExpiryClock();
        var contract = await NewExpiryContractAsync(db, recipient, new DateTime(2030, 1, 31));
        var realtime = new RecordingRealtimeNotificationPublisher();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime, push, timeProvider: clock);
        foreach (var days in new[] { 30, 14, 7 })
        {
            clock.Set(new DateTimeOffset(contract.ValidUntil!.Value.AddDays(-days).AddHours(12), TimeSpan.Zero));
            await service.NotifyContractExpiringAsync(contract, days, [recipient.Id]);
            await service.NotifyContractExpiringAsync(contract, days, [recipient.Id]);
        }
        var notifications = await db.UserNotifications.AsNoTracking().ToArrayAsync();
        Assert.Equal(3, notifications.Length);
        Assert.Equal(3, notifications.Select(item => item.EventKey).Distinct().Count());
        Assert.Contains(notifications, item => item.EventKey.EndsWith(":stage:30", StringComparison.Ordinal));
        Assert.Contains(notifications, item => item.EventKey.EndsWith(":stage:14", StringComparison.Ordinal));
        Assert.Contains(notifications, item => item.EventKey == $"contract:{contract.Id}:expiring:{recipient.Id}:2030-01-31");
        Assert.Equal(3, realtime.Notifications.Count);
        Assert.Equal(3, push.Notifications.Count);
    }

    [Fact]
    public async Task ContractExpiration_RemovedManagerDoesNotCreateOrDispatchAnotherNotice()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "removed-expiry@example.test");
        recipient.Role!.Name = "admin";
        var clock = new ContractExpiryClock();
        var contract = new Contract { Number = "REMOVED", Date = new DateTime(2030, 1, 1), ValidUntil = new DateTime(2030, 1, 21), Status = ContractStatus.Concluded };
        db.Contracts.Add(contract);
        db.SystemSettings.Add(new SystemSetting
        { Key = ContractExpirationRecipientSettings.SettingKey, Value = ContractExpirationRecipientSettings.Serialize(new()) });
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime, push, timeProvider: clock);

        await service.NotifyContractExpiringAsync(contract, 20, [recipient.Id]);

        Assert.Empty(await db.UserNotifications.ToArrayAsync());
        Assert.Empty(realtime.Notifications);
        Assert.Empty(push.Notifications);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("object")]
    public async Task ContractExpiration_ConflictingReceiptCannotDispatchAnotherNotification(string conflict)
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "poison-expiry@example.test");
        recipient.Role!.Name = "admin";
        var clock = new ContractExpiryClock();
        var contract = await NewExpiryContractAsync(db, recipient, new DateTime(2030, 1, 21));
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, recipient.Id, contract.ValidUntil!.Value, 30);
        var original = new UserNotification
        {
            RecipientUserId = recipient.Id,
            EventKey = key,
            Type = conflict == "type" ? UserNotificationTypes.TaskChanged : UserNotificationTypes.ContractExpiring,
            ObjectType = conflict == "type" ? "task" : "contract",
            ObjectId = contract.Id + 999,
            Title = "SECRET",
            BodyPreview = "PRIVATE"
        };
        db.UserNotifications.Add(original);
        db.MaxRecipientLinks.Add(new MaxRecipientLink { UserId = recipient.Id, MaxUserId = 7001, Status = MaxRecipientLinkStatuses.Verified, LinkTokenHash = Guid.NewGuid().ToString("N"), VerifiedAt = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync();
        var outbox = new RecordingMaxOutboxService();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime, push,
            timeProvider: clock, maxService: new ReadyMaxNotificationService(), maxOutboxService: outbox,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test/" });

        await service.NotifyContractExpiringAsync(contract, 20, [recipient.Id]);

        Assert.Equal(original.Id, (await db.UserNotifications.SingleAsync()).Id);
        Assert.Equal("SECRET", original.Title);
        Assert.Equal("PRIVATE", original.BodyPreview);
        Assert.Empty(outbox.Requests);
        Assert.Empty(realtime.Notifications);
        Assert.Empty(push.Notifications);
    }

    [Fact]
    public async Task ContractExpiration_ReceiptForAnotherRecipientCannotSuppressTheCanonicalRecipient()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "canonical-expiry@example.test");
        var another = new User
        {
            Email = "unrelated-expiry@example.test",
            FullName = "Unrelated expiry recipient",
            PasswordHash = "hash",
            RoleId = recipient.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(another);
        recipient.Role!.Permissions.Add(new RolePermission
        { Section = RolePermissionCatalog.Contracts, CanView = true, ViewScope = DataVisibilityScope.All });
        var clock = new ContractExpiryClock();
        var contract = await NewExpiryContractAsync(db, recipient, new DateTime(2030, 1, 21));
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, recipient.Id, contract.ValidUntil!.Value, 30);
        var original = new UserNotification
        {
            RecipientUserId = another.Id,
            EventKey = key,
            Type = UserNotificationTypes.ContractExpiring,
            ObjectType = "contract",
            ObjectId = contract.Id,
            Title = "OLD",
            BodyPreview = "PRIVATE"
        };
        db.UserNotifications.Add(original);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime, push, timeProvider: clock);

        await service.NotifyContractExpiringAsync(contract, 20, [recipient.Id, another.Id]);

        var notifications = await db.UserNotifications.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, notifications.Length);
        Assert.Single(notifications, item => item.RecipientUserId == recipient.Id && item.EventKey == key);
        Assert.Equal("PRIVATE", notifications.Single(item => item.Id == original.Id).BodyPreview);
        Assert.Single(realtime.Notifications);
        Assert.Single(push.Notifications);
    }

    private static async Task<Contract> NewExpiryContractAsync(PaymentContext db, User recipient, DateTime end)
    {
        var contract = new Contract
        {
            Number = "NEW-EXPIRY",
            Date = end.AddMonths(-1),
            ValidUntil = end,
            Status = ContractStatus.Concluded,
            ClientContracts = { new ClientContract { Client = new Client { Name = "Expiry owner", ResponsibleUserId = recipient.Id, IsActive = true } } }
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract;
    }

    private sealed class ContractExpiryClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset value) => _now = value;
    }
}
