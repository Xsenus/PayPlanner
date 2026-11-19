using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxServiceTests
{
    [Theory]
    [InlineData("selection")]
    [InlineData("acl")]
    [InlineData("source")]
    [InlineData("stage")]
    public async Task ContractExpiration_QueuedMaxRechecksCurrentSourceSelectionAndStage(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var user = await f.Db.Users.Include(item => item.Role).ThenInclude(role => role!.Permissions)
            .SingleAsync(item => item.Id == f.UserId);
        user.Role!.Permissions.Add(new RolePermission
        { Section = RolePermissionCatalog.Contracts, CanView = true, ViewScope = DataVisibilityScope.All });
        var rolePreferences = RoleNotificationPreferenceDefaults.Resolve(user.Role.Name).Preferences;
        var disabledChannels = rolePreferences.DisabledChannels.ToDictionary(
            item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        disabledChannels[UserNotificationTypes.ContractExpiring] = UserNotificationTypes.Find(UserNotificationTypes.ContractExpiring)!
            .PreferenceChannels.Where(channel => channel != NotificationChannels.InApp && channel != NotificationChannels.Max).ToArray();
        var expiryPreferences = new UserNotificationPreferenceSnapshot(
            rolePreferences.DisabledTypes.Where(type => type != UserNotificationTypes.ContractExpiring).ToArray(), disabledChannels);
        Assert.True(UserNotificationPreferenceSettings.IsChannelEnabled(expiryPreferences, UserNotificationTypes.ContractExpiring, NotificationChannels.InApp));
        Assert.True(UserNotificationPreferenceSettings.IsChannelEnabled(expiryPreferences, UserNotificationTypes.ContractExpiring, NotificationChannels.Max));
        f.Db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(expiryPreferences)
        });
        f.Db.SystemSettings.Add(new SystemSetting
        {
            Key = ContractExpirationRecipientSettings.SettingKey,
            Value = ContractExpirationRecipientSettings.Serialize(new() { UserIds = [user.Id] })
        });
        var contract = new Contract { Number = "MAX-EXPIRY", Date = f.Now.Date, ValidUntil = f.Now.Date.AddDays(20), Status = ContractStatus.Concluded };
        f.Db.Contracts.Add(contract);
        await f.Db.SaveChangesAsync();
        var key = ContractExpirationNotificationPolicy.EventKey(contract.Id, user.Id, contract.ValidUntil!.Value, 30);
        var request = new MaxQueueRequest(user.Id, MaxMessengerScenarios.Notifications, key, "", "Открыть CRM",
            "https://crm.example.test", UserNotificationTypes.ContractExpiring);
        var queued = await f.Outbox.QueueAsync(request, default);
        if (change == "selection") (await f.Db.SystemSettings.SingleAsync(item => item.Key == ContractExpirationRecipientSettings.SettingKey)).Value
            = ContractExpirationRecipientSettings.Serialize(new());
        if (change == "acl") (await f.Db.RolePermissions.SingleAsync(item => item.RoleId == user.RoleId && item.Section == RolePermissionCatalog.Contracts)).CanView = false;
        if (change == "source") contract.ValidUntil = contract.ValidUntil!.Value.AddDays(1);
        if (change == "stage") f.MoveTimeTo(new DateTimeOffset(f.Now.AddDays(6), TimeSpan.Zero));
        await f.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await f.Processor(api).ProcessBatchAsync(default);

        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal("contract-expiration-no-longer-deliverable", queued.LastErrorCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Outbox.QueueAsync(request, default));
        Assert.Single(await f.Db.MaxOutboxMessages.ToArrayAsync());
    }
}
