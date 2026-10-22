using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class MaxOutboxQuotaPostgreSqlTests(PostgreSqlMigratedTemplate databases)
    : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongKeyPersonalOptOutSurvivesContextReloadOnPostgreSql(bool legacy)
    {
        await using var lease = await databases.CreateDatabaseAsync("pp_max_long_key_");
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        var settings = new FixedMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        int messageId;
        await using (var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false))
        {
            var user = new User
            {
                Email = "max-long-key@example.test",
                FullName = "Тест длинного ключа",
                PasswordHash = "hash",
                Role = new Role { Name = "max-long-key-test", Description = "Тест" },
                IsActive = true,
                IsApproved = true,
            };
            db.MaxRecipientLinks.Add(new MaxRecipientLink
            {
                User = user,
                MaxUserId = 7001,
                Status = MaxRecipientLinkStatuses.Verified,
                VerifiedAt = clock.Now.UtcDateTime,
                LinkTokenHash = Guid.NewGuid().ToString("N"),
            });
            await db.SaveChangesAsync();
            var eventKey = new string('я', 240);
            var message = await new MaxOutboxService(db, settings, protection, clock).QueueAsync(new MaxQueueRequest(
                user.Id, MaxMessengerScenarios.Notifications, eventKey, string.Empty,
                "Открыть CRM", "https://crm.example.test/", UserNotificationTypes.TaskCompleted), CancellationToken.None);
            messageId = message.Id;
            Assert.StartsWith("sha256:", message.EventKey);
            if (legacy)
                message.ProtectedText = protection.CreateProtector("PayPlanner.Notifications.Max.Outbox.v1")
                    .Protect(MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.TaskCompleted));
            db.UserNotifications.Add(new UserNotification
            {
                RecipientUserId = user.Id,
                EventKey = eventKey,
                Type = UserNotificationTypes.TaskCompleted,
                Title = "Задача выполнена",
                ObjectType = "system",
            });
            db.UserSettings.Add(new UserSetting
            {
                UserId = user.Id,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                    [UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>())),
            });
            await db.SaveChangesAsync();
        }
        await using var workerDb = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var api = new BlockingMaxApi();
        api.Release.TrySetResult();

        Assert.Equal(1, await new MaxOutboxProcessor(workerDb, settings, api, protection, clock,
            NullLogger<MaxOutboxProcessor>.Instance).ProcessBatchAsync(CancellationToken.None));

        var stored = await workerDb.MaxOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == messageId);
        Assert.Equal(MaxOutboxStatuses.Cancelled, stored.Status);
        Assert.Equal("recipient-preference-disabled", stored.LastErrorCode);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(0, api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightDeliveryReservesDailySlotForOtherWorkerAndDeferredMessageSurvives(bool crossesMidnight)
    {
        await using var lease = await databases.CreateDatabaseAsync("pp_max_quota_");
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var firstDb = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        await using var secondDb = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var clock = new MutableTimeProvider(crossesMidnight
            ? new DateTimeOffset(2026, 10, 22, 16, 59, 59, TimeSpan.Zero)
            : new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        var settings = new FixedMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var user = new User
        {
            Email = "max-quota@example.test",
            FullName = "Тест MAX",
            PasswordHash = "hash",
            Role = new Role { Name = "max-quota-test", Description = "Тест лимита" },
            IsActive = true,
            IsApproved = true,
        };
        var link = new MaxRecipientLink
        {
            User = user,
            MaxUserId = 7001,
            Status = MaxRecipientLinkStatuses.Verified,
            VerifiedAt = clock.GetUtcNow().UtcDateTime,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
        };
        firstDb.MaxRecipientLinks.Add(link);
        await firstDb.SaveChangesAsync();
        var request = new MaxQueueRequest(user.Id, MaxMessengerScenarios.Notifications, "pg:quota:1", string.Empty,
            "Открыть CRM", "https://crm.example.test/", UserNotificationTypes.TaskCompleted);
        await new MaxOutboxService(firstDb, settings, protection, clock).QueueAsync(request, CancellationToken.None);
        var api = new BlockingMaxApi();
        var firstWorker = new MaxOutboxProcessor(firstDb, settings, api, protection, clock, NullLogger<MaxOutboxProcessor>.Instance);
        var secondWorker = new MaxOutboxProcessor(secondDb, settings, api, protection, clock, NullLogger<MaxOutboxProcessor>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var firstRun = firstWorker.ProcessBatchAsync(timeout.Token);
        MaxOutboxMessage deferred;
        try
        {
            await api.Started.Task.WaitAsync(timeout.Token);
            if (crossesMidnight)
                clock.Now = clock.Now.AddSeconds(2);
            var expected = new DateTime(2026, 10, crossesMidnight ? 23 : 22, 17, 0, 0, DateTimeKind.Utc);
            deferred = await new MaxOutboxService(secondDb, settings, protection, clock).QueueAsync(
                request with { EventKey = "pg:quota:2" }, timeout.Token);
            Assert.Equal(expected, deferred.NextAttemptAt);
            // Старый worker, изменение тихих часов или ручной retry могут оставить
            // просроченную запись: даже тогда активная отправка уже занимает лимит.
            deferred.NextAttemptAt = clock.GetUtcNow().UtcDateTime;
            await secondDb.SaveChangesAsync(timeout.Token);

            Assert.Equal(1, await secondWorker.ProcessBatchAsync(timeout.Token));
            Assert.Equal(1, api.Calls);
            Assert.Equal(MaxOutboxStatuses.Queued, deferred.Status);
            Assert.Equal(0, deferred.AttemptCount);
            Assert.Equal(expected, deferred.NextAttemptAt);
        }
        finally
        {
            api.Release.TrySetResult();
            await firstRun;
        }

        clock.Now = new DateTimeOffset(deferred.NextAttemptAt);
        Assert.Equal(1, await secondWorker.ProcessBatchAsync(timeout.Token));
        Assert.Equal(2, api.Calls);
        Assert.Equal(2, await secondDb.MaxOutboxMessages.CountAsync(item => item.Status == MaxOutboxStatuses.Sent));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedMaxSettings : IMaxNotificationService
    {
        public Task<MaxRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MaxRuntimeSettings(true, false, "test-token", "test-secret", true, false, false, false,
                false, new TimeOnly(20, 0), new TimeOnly(9, 0), "Asia/Novosibirsk", 1, 30));
        public Task<MaxNotificationDiagnosticsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<MaxNotificationDiagnosticsDto> UpdateSettingsAsync(UpdateMaxNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class BlockingMaxApi : IMaxApiClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MaxApiResult> SendTextAsync(long userId, string text, string botToken, string? actionText = null,
            Uri? actionUrl = null, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new MaxApiResult(true, $"test-message-{call}");
        }
        public Task<MaxApiResult> CheckHealthAsync(string botToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MaxUploadSlotResult> CreateUploadSlotAsync(string mediaType, string botToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
