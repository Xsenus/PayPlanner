using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentOverdueDeliveryChannelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaxFailure_RetainsStableKey_AndRetryHonoursANewPersonalBan(bool disable)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db);
        var user = await db.Users.SingleAsync(); db.MaxRecipientLinks.Add(new() { UserId = user.Id, MaxUserId = 101, Status = MaxRecipientLinkStatuses.Verified, LinkTokenHash = Guid.NewGuid().ToString("N") }); await db.SaveChangesAsync();
        var max = new ProbeMaxQueue { Failures = 1 }; var realtime = new PaymentOverdueDeliveryTests.ProbeRealtime(); await using var provider = Provider(db, realtime, max, null); var processor = PaymentOverdueDeliveryTests.Processor(provider);
        await Assert.ThrowsAsync<IOException>(() => processor.ProcessBatchAsync()); var first = await db.UserNotifications.AsNoTracking().SingleAsync(); Assert.Equal(0, realtime.Successes);
        if (disable) { db.UserSettings.Add(new() { UserId = user.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = JsonSerializer.Serialize(new { disabledChannels = new Dictionary<string, string[]> { [UserNotificationTypes.PaymentChanged] = [NotificationChannels.Max] } }) }); await db.SaveChangesAsync(); }
        await PaymentOverdueDeliveryTests.ExpireAsync(db); Assert.Equal(1, await processor.ProcessBatchAsync()); Assert.Single(await db.UserNotifications.ToArrayAsync()); Assert.Equal(disable ? 1 : 2, max.Keys.Count); Assert.All(max.Keys, key => Assert.Equal(first.EventKey, key)); Assert.Equal(1, realtime.Successes);
    }

    [Fact]
    public async Task PreferencesChangedDuringRealtime_PreventTheFollowingPush()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db); var userId = await db.Users.Select(u => u.Id).SingleAsync();
        var realtime = new PaymentOverdueDeliveryTests.ProbeRealtime { Before = async _ => { db.UserSettings.Add(new() { UserId = userId, Key = UserNotificationPreferenceSettings.SettingKey, Value = JsonSerializer.Serialize(new { disabledChannels = new Dictionary<string, string[]> { [UserNotificationTypes.PaymentChanged] = [NotificationChannels.WebPush] } }) }); await db.SaveChangesAsync(); } };
        var push = new ProbePush(); await using var provider = Provider(db, realtime, null, push); Assert.Equal(1, await PaymentOverdueDeliveryTests.Processor(provider).ProcessBatchAsync()); Assert.Equal(0, push.Calls); Assert.Single(await db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("archive")]
    [InlineData("deleted")]
    public async Task RetryDoesNotResurrectADismissedNotification(string dismissal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db); var realtime = new PaymentOverdueDeliveryTests.ProbeRealtime { Failures = 1 }; await using var provider = PaymentOverdueDeliveryTests.SqliteProvider(db, realtime); var processor = PaymentOverdueDeliveryTests.Processor(provider);
        await Assert.ThrowsAsync<IOException>(() => processor.ProcessBatchAsync()); var n = await db.UserNotifications.SingleAsync();
        if (dismissal == "read") n.IsRead = true; if (dismissal == "archive") n.IsArchived = true; if (dismissal == "deleted") n.DeletedAt = DateTime.UtcNow; await db.SaveChangesAsync();
        await PaymentOverdueDeliveryTests.ExpireAsync(db); Assert.Equal(1, await processor.ProcessBatchAsync()); Assert.Single(await db.UserNotifications.ToArrayAsync()); Assert.Equal(0, realtime.Successes);
    }

    private static ServiceProvider Provider(PaymentContext db, PaymentOverdueDeliveryTests.ProbeRealtime realtime, ProbeMaxQueue? max, ProbePush? push)
    {
        var services = new ServiceCollection(); services.AddScoped(_ => new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options));
        services.AddScoped<INotificationService>(s => new NotificationService(s.GetRequiredService<PaymentContext>(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test/" }).Build(),
            PaymentOverdueDeliveryTests.RejectExternal.Create<IEmailService>(), PaymentOverdueDeliveryTests.RejectExternal.Create<ITelegramNotificationService>(), NullLogger<NotificationService>.Instance, realtime, push,
            maxService: max is null ? null : new ReadyMax(), maxOutboxService: max)); return services.BuildServiceProvider();
    }
    private sealed class ReadyMax : IMaxNotificationService
    {
        public Task<MaxRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MaxRuntimeSettings(true, false, "synthetic", "synthetic", true, false, false, false, false, new(20, 0), new(9, 0), "UTC", 20, 30));
        public Task<MaxNotificationDiagnosticsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<MaxNotificationDiagnosticsDto> UpdateSettingsAsync(UpdateMaxNotificationSettingsRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class ProbeMaxQueue : IMaxOutboxService
    {
        public int Failures { get; set; }
        public List<string> Keys { get; } = [];
        public Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct)
        { Keys.Add(request.EventKey); if (Failures-- > 0) throw new IOException("Synthetic MAX enqueue failure"); return Task.FromResult(new MaxOutboxMessage()); }
        public Task<bool> RetryAsync(Guid publicId, int? actorUserId, string actorName, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class ProbePush : IWebPushNotificationService
    {
        public int Calls { get; private set; }
        public WebPushPublicKeyStatus GetPublicKeyStatus() => throw new InvalidOperationException();
        public Task SendUserNotificationAsync(UserNotification notification, CancellationToken cancellationToken = default) { Calls++; return Task.CompletedTask; }
        public Task<NotificationDelivery?> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
