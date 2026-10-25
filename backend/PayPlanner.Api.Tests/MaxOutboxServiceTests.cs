using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxServiceTests
{
    [Fact]
    public async Task QueueRequiresVerifiedRecipientAndEnabledScenario()
    {
        await using var fixture = await Fixture.CreateAsync(linked: false);
        var request = fixture.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(request, CancellationToken.None));
        Assert.Empty(await fixture.Db.MaxOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task NotificationHonorsPersonalMaxPreference()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                [],
                new Dictionary<string, IReadOnlyList<string>>
                {
                    [UserNotificationTypes.SystemAdminWarning] = [NotificationChannels.Max],
                })),
        });
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None));
        Assert.Empty(await fixture.Db.MaxOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task LongEventKeyRechecksPersonalMaxOptOutBeforeDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var eventKey = "task:completed:" + new string('я', 100);
        var request = fixture.Request() with { EventKey = eventKey, NotificationType = UserNotificationTypes.TaskCompleted };
        var queued = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            EventKey = eventKey,
            Type = UserNotificationTypes.TaskCompleted,
            Title = "Задача выполнена",
            ObjectType = "system",
        });
        fixture.Db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                [], new Dictionary<string, IReadOnlyList<string>>
                {
                    [UserNotificationTypes.TaskCompleted] = [NotificationChannels.Max],
                })),
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal("recipient-preference-disabled", queued.LastErrorCode);
        Assert.Equal(0, queued.AttemptCount);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task QueueIsIdempotentAndProtectsTextAndUrl()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        var second = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.DoesNotContain("Критическое уведомление", first.ProtectedText);
        Assert.DoesNotContain("opaque-link", first.ProtectedActionUrl);
        Assert.Equal("Содержимое скрыто: сообщение MAX.", first.BodyPreview);
        Assert.Single(await fixture.Db.MaxOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DocumentsRequireHttpsLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request() with { Scenario = MaxMessengerScenarios.Documents, ActionUrl = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(request, CancellationToken.None));
        request = request with { ActionUrl = "http://crm.example.test/document" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task NotificationsWaitForQuietHoursToEndAndDailyLimitStillApplies()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: true, dailyLimit: 20);
        var deferred = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(new DateTime(2026, 8, 1, 2, 0, 0, DateTimeKind.Utc), deferred.NextAttemptAt);
        var api = new RecordingApiClient();
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        fixture.MoveTimeTo(new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Single(api.Messages);

        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);
        Assert.Equal(deferred.Id, (await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None)).Id);
        var overflow = await fixture.Outbox.QueueAsync(
            fixture.Request() with { EventKey = "critical:event:2" }, CancellationToken.None);
        Assert.Equal(new DateTime(2026, 8, 1, 17, 0, 0, DateTimeKind.Utc), overflow.NextAttemptAt);
        Assert.Equal(MaxOutboxStatuses.Queued, overflow.Status);
        Assert.Equal(2, await fixture.Db.MaxOutboxMessages.CountAsync());
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Single(api.Messages);
    }

    [Theory]
    [InlineData(MaxOutboxStatuses.Queued)]
    [InlineData(MaxOutboxStatuses.Failed)]
    [InlineData(MaxOutboxStatuses.Processing)]
    public async Task ProcessorSpreadsBacklogAcrossDaysAfterLimitIsReduced(string status)
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 3; i++)
            await fixture.Outbox.QueueAsync(fixture.Request() with { EventKey = $"backlog:{i}" }, CancellationToken.None);
        var priorAttempts = status == MaxOutboxStatuses.Queued ? 0 : 1;
        foreach (var message in await fixture.Db.MaxOutboxMessages.ToListAsync())
        {
            message.Status = status;
            message.AttemptCount = priorAttempts;
            message.UpdatedAt = fixture.Now.AddMinutes(-6);
        }
        await fixture.Db.SaveChangesAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);
        var api = new RecordingApiClient();

        Assert.Equal(3, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Single(api.Messages);
        var deferred = await fixture.Db.MaxOutboxMessages.Where(item => item.Status == MaxOutboxStatuses.Queued).ToListAsync();
        Assert.Equal(2, deferred.Count);
        Assert.All(deferred, item =>
        {
            Assert.Equal(new DateTime(2026, 7, 31, 17, 0, 0, DateTimeKind.Utc), item.NextAttemptAt);
            Assert.Equal(priorAttempts, item.AttemptCount);
        });
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        fixture.MoveTimeTo(new DateTimeOffset(2026, 7, 31, 17, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(2, api.Messages.Count);
        fixture.MoveTimeTo(new DateTimeOffset(2026, 8, 1, 17, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(3, api.Messages.Count);
        Assert.Equal(3, await fixture.Db.MaxOutboxMessages.CountAsync(item => item.Status == MaxOutboxStatuses.Sent));
    }

    [Fact]
    public async Task DailyQuotaUsesActualDeliveryDayAndLeavesManualDocumentsLimited()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        sent.Status = MaxOutboxStatuses.Sent;
        sent.NextAttemptAt = fixture.Now.AddDays(-1);
        sent.SentAt = fixture.Now;
        await fixture.Db.SaveChangesAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);

        var deferred = await fixture.Outbox.QueueAsync(fixture.Request() with { EventKey = "quota:actual-day" }, CancellationToken.None);

        Assert.Equal(new DateTime(2026, 7, 31, 17, 0, 0, DateTimeKind.Utc), deferred.NextAttemptAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(
            fixture.Request() with { EventKey = "document:limited", Scenario = MaxMessengerScenarios.Documents }, CancellationToken.None));
        Assert.Equal(2, await fixture.Db.MaxOutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData("Asia/Novosibirsk", "2026-10-25T02:00:00Z")]
    [InlineData("Europe/Berlin", "2026-10-25T08:00:00Z")]
    public async Task QuotaDeferralRespectsQuietHoursAndDaylightSavingTime(string timeZone, string expectedUtc)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MoveTimeTo(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: true, dailyLimit: 1, timeZone: timeZone);
        var api = new RecordingApiClient();
        await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        var request = fixture.Request() with { EventKey = "quota:quiet-hours", NotificationType = UserNotificationTypes.TaskCompleted };
        var deferred = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc).UtcDateTime, deferred.NextAttemptAt);
        Assert.Equal(deferred.Id, (await fixture.Outbox.QueueAsync(request, CancellationToken.None)).Id);
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        fixture.MoveTimeTo(new DateTimeOffset(deferred.NextAttemptAt));
        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(2, api.Messages.Count);
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProcessorAppliesDailyLimitAfterQuietHoursMoveBacklogIntoSameMorning()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 3; i++)
            await fixture.Outbox.QueueAsync(fixture.Request() with { EventKey = $"quiet-backlog:{i}" }, CancellationToken.None);
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: true, dailyLimit: 1);
        var api = new RecordingApiClient();
        Assert.Equal(3, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Empty(api.Messages);
        fixture.MoveTimeTo(new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        Assert.Single(api.Messages);
        var pending = await fixture.Db.MaxOutboxMessages.Where(item => item.Status == MaxOutboxStatuses.Queued).ToListAsync();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, item => Assert.Equal(new DateTime(2026, 8, 2, 2, 0, 0, DateTimeKind.Utc), item.NextAttemptAt));
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("unlinked", "recipient-unlinked")]
    [InlineData("inactive", "recipient-preference-disabled")]
    [InlineData("unapproved", "recipient-preference-disabled")]
    public async Task DeferredNotificationRechecksRecipientAccess(string reason, string errorCode)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);
        await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        await fixture.Processor(new RecordingApiClient()).ProcessBatchAsync(CancellationToken.None);
        var deferred = await fixture.Outbox.QueueAsync(fixture.Request() with { EventKey = "quota:access-revoked" }, CancellationToken.None);
        if (reason == "unlinked")
            (await fixture.Db.MaxRecipientLinks.SingleAsync()).Status = MaxRecipientLinkStatuses.OptedOut;
        else
        {
            var user = await fixture.Db.Users.SingleAsync();
            user.IsActive = reason != "inactive";
            user.IsApproved = reason != "unapproved";
        }
        await fixture.Db.SaveChangesAsync();
        fixture.MoveTimeTo(new DateTimeOffset(deferred.NextAttemptAt));
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        Assert.Equal(MaxOutboxStatuses.Cancelled, deferred.Status);
        Assert.Equal(errorCode, deferred.LastErrorCode);
        Assert.Equal(0, deferred.AttemptCount);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task ProcessorDefersAlreadyQueuedMessageWhenQuietHoursTurnedOn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: true, dailyLimit: 20);
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        var stored = await fixture.Db.MaxOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
        Assert.Equal(MaxOutboxStatuses.Queued, stored.Status);
        Assert.Equal(new DateTime(2026, 8, 1, 2, 0, 0, DateTimeKind.Utc), stored.NextAttemptAt);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task ProcessorCancelsPendingNotificationAfterPersonalMaxOptOut()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            Type = UserNotificationTypes.SystemAdminWarning,
            EventKey = "critical:event:1",
            Title = "Системное предупреждение",
            BodyPreview = "Подробности остаются в CRM",
            ObjectType = "system",
        });
        fixture.Db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                [], new Dictionary<string, IReadOnlyList<string>>
                {
                    [UserNotificationTypes.SystemAdminWarning] = [NotificationChannels.Max],
                })),
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        var stored = await fixture.Db.MaxOutboxMessages.AsNoTracking().SingleAsync(message => message.Id == queued.Id);
        Assert.Equal(MaxOutboxStatuses.Cancelled, stored.Status);
        Assert.Equal("recipient-preference-disabled", stored.LastErrorCode);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task ProcessorSendsQueuedMessageAndStoresOnlyProviderId()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        var api = new RecordingApiClient();
        var processor = fixture.Processor(api);

        Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));
        var stored = await fixture.Db.MaxOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
        Assert.Equal(MaxOutboxStatuses.Sent, stored.Status);
        Assert.Equal("max-message-1", stored.ProviderMessageId);
        Assert.Single(api.Messages);
        Assert.Equal(MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.SystemAdminWarning), api.Messages[0].Text);
        Assert.Equal("https://crm.example.test/opaque-link", api.Messages[0].Url?.AbsoluteUri);
    }

    [Fact]
    public async Task NotificationUsesSafeServerTemplateAndRequiresHttpsAction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unsafeRequest = fixture.Request() with
        {
            Text = "Паспорт 1234 567890",
            ActionUrl = null,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(unsafeRequest, CancellationToken.None));

        var queued = await fixture.Outbox.QueueAsync(unsafeRequest with { ActionUrl = "https://crm.example.test/open/opaque" }, CancellationToken.None);
        var api = new RecordingApiClient();
        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);
        Assert.DoesNotContain("1234", queued.ProtectedText, StringComparison.Ordinal);
        Assert.DoesNotContain("567890", queued.ProtectedText, StringComparison.Ordinal);
        Assert.Equal(MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.SystemAdminWarning), api.Messages.Single().Text);
    }

    [Theory]
    [InlineData(UserNotificationTypes.TaskAssigned, "Новая задача")]
    [InlineData(UserNotificationTypes.CourtDecisionRecorded, "Судебное решение")]
    [InlineData(UserNotificationTypes.CourtDecisionPublished, "Судебный акт или решение")]
    [InlineData(UserNotificationTypes.PaymentChanged, "Изменение платежа")]
    public async Task NotificationTypesUseNeutralTemplatesWithoutSourceDetails(string type, string label)
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request() with
        {
            NotificationType = type,
            Text = "Секретное содержание дела №123 и платёж 500 ₽",
            EventKey = $"event:{type}:123",
        }, CancellationToken.None);

        var api = new RecordingApiClient();
        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);
        var sentText = Assert.Single(api.Messages).Text;
        Assert.Contains(label, sentText, StringComparison.Ordinal);
        Assert.DoesNotContain("№123", sentText, StringComparison.Ordinal);
        Assert.DoesNotContain("500", sentText, StringComparison.Ordinal);
        Assert.DoesNotContain("Секретное", queued.ProtectedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAndSilentNotificationTypesCannotBeQueued()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(
            fixture.Request() with { NotificationType = "unknown" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(
            fixture.Request() with { NotificationType = UserNotificationTypes.CaseDocument }, CancellationToken.None));
        Assert.Empty(await fixture.Db.MaxOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task TransientFailureGetsBoundedBackoffAndUnlinkedRecipientIsCancelled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        await fixture.Processor(new RecordingApiClient(transientFailure: true)).ProcessBatchAsync(CancellationToken.None);
        var failed = await fixture.Db.MaxOutboxMessages.SingleAsync(item => item.Id == queued.Id);
        Assert.Equal(MaxOutboxStatuses.Failed, failed.Status);
        Assert.Equal(fixture.Now.AddMinutes(1), failed.NextAttemptAt);

        failed.NextAttemptAt = fixture.Now;
        failed.RecipientLink.Status = MaxRecipientLinkStatuses.OptedOut;
        await fixture.Db.SaveChangesAsync();
        await fixture.Processor(new RecordingApiClient()).ProcessBatchAsync(CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var cancelled = await fixture.Db.MaxOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
        Assert.Equal(MaxOutboxStatuses.Cancelled, cancelled.Status);
        Assert.Equal("recipient-unlinked", cancelled.LastErrorCode);
    }

    [Fact]
    public async Task ProcessorCancelsVerifiedLinkWithoutProviderRecipientId()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        var link = await fixture.Db.MaxRecipientLinks.SingleAsync(item => item.UserId == fixture.UserId);
        link.MaxUserId = null;
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var cancelled = await fixture.Db.MaxOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
        Assert.Equal(MaxOutboxStatuses.Cancelled, cancelled.Status);
        Assert.Equal("recipient-unlinked", cancelled.LastErrorCode);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task ProviderTimeoutGetsRetryWithoutLeakingException()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        await fixture.Processor(new TimeoutApiClient()).ProcessBatchAsync(CancellationToken.None);

        var failed = await fixture.Db.MaxOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
        Assert.Equal(MaxOutboxStatuses.Failed, failed.Status);
        Assert.Equal("provider-timeout", failed.LastErrorCode);
        Assert.Equal(fixture.Now.AddMinutes(1), failed.NextAttemptAt);
    }

    [Fact]
    public async Task ManualRetryRequeuesFailedMessageAndCreatesAuditEvent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        await fixture.Processor(new RecordingApiClient(transientFailure: true)).ProcessBatchAsync(CancellationToken.None);

        var retried = await fixture.Outbox.RetryAsync(queued.PublicId, fixture.UserId, "Администратор", CancellationToken.None);
        var stored = await fixture.Db.MaxOutboxMessages.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
        var audit = await fixture.Db.ObjectAuditEvents.AsNoTracking().SingleAsync(item => item.ObjectType == "max-outbox-message");

        Assert.True(retried);
        Assert.Equal(MaxOutboxStatuses.Queued, stored.Status);
        Assert.Equal("Retried", audit.Action);
        Assert.DoesNotContain("Критическое уведомление", audit.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxSettingsAuditListsChangesWithoutSecrets()
    {
        var previous = new MaxNotificationDiagnosticsDto();
        var next = new MaxNotificationDiagnosticsDto
        {
            Enabled = true,
            BotTokenSet = true,
            WebhookSecretSet = true,
            DocumentsEnabled = true,
            DailyLimitPerRecipient = 15,
            TechnicalPayloadRetentionDays = 30
        };

        var audit = NotificationSettingsAudit.CreateMaxSettingsUpdatedEvent(
            null, previous, next, botTokenChanged: true, webhookSecretChanged: true);

        Assert.NotNull(audit);
        Assert.Contains("MaxBotToken", audit.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("MaxWebhookSecret", audit.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("bot-token", audit.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook-secret", audit.MetadataJson, StringComparison.Ordinal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DirectoryInfo _keys;
        private readonly IDataProtectionProvider _protection;
        private readonly SystemMaxNotificationService _settings;
        private readonly FixedTimeProvider _time;
        public PaymentContext Db { get; }
        public MaxOutboxService Outbox { get; }
        public int UserId { get; }
        public DateTime Now => _time.GetUtcNow().UtcDateTime;
        public void MoveTimeTo(DateTimeOffset now) => _time.Set(now);
        public void SetPayload(MaxOutboxMessage message, string payload)
            => message.ProtectedText = _protection.CreateProtector("PayPlanner.Notifications.Max.Outbox.v1").Protect(payload);
        public string ReadPayload(MaxOutboxMessage message)
            => _protection.CreateProtector("PayPlanner.Notifications.Max.Outbox.v1").Unprotect(message.ProtectedText);

        private Fixture(SqliteConnection connection, DirectoryInfo keys, IDataProtectionProvider protection, SystemMaxNotificationService settings, FixedTimeProvider time, PaymentContext db, MaxOutboxService outbox, int userId)
        {
            _connection = connection; _keys = keys; _protection = protection; _settings = settings; _time = time;
            Db = db; Outbox = outbox; UserId = userId;
        }

        public MaxQueueRequest Request() => new(
            UserId,
            MaxMessengerScenarios.Notifications,
            "critical:event:1",
            "Критическое уведомление",
            "Открыть CRM",
            "https://crm.example.test/opaque-link",
            UserNotificationTypes.SystemAdminWarning);

        public MaxOutboxProcessor Processor(IMaxApiClient api, Microsoft.Extensions.Configuration.IConfiguration? configuration = null) => new(
            Db, _settings, api, _protection, _time, NullLogger<MaxOutboxProcessor>.Instance, configuration);

        public async Task ConfigureLimitsAsync(bool quietHoursEnabled, int dailyLimit, string timeZone = "Asia/Novosibirsk") => await _settings.UpdateSettingsAsync(new UpdateMaxNotificationSettingsRequest
        {
            Enabled = true,
            KeepExistingBotToken = true,
            KeepExistingWebhookSecret = true,
            NotificationsEnabled = true,
            DocumentsEnabled = true,
            QuietHoursEnabled = quietHoursEnabled,
            QuietHoursStart = "20:00",
            QuietHoursEnd = "09:00",
            QuietHoursTimeZone = timeZone,
            DailyLimitPerRecipient = dailyLimit,
        });

        public static async Task<Fixture> CreateAsync(bool linked = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "employee", Description = "Сотрудник" };
            var user = new User { Email = "max@example.test", FullName = "MAX", PasswordHash = "hash", Role = role, IsActive = true, IsApproved = true };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            if (linked)
            {
                db.MaxRecipientLinks.Add(new MaxRecipientLink
                {
                    UserId = user.Id,
                    MaxUserId = 7001,
                    MaxChatId = 7001,
                    Status = MaxRecipientLinkStatuses.Verified,
                    LinkTokenHash = Guid.NewGuid().ToString("N"),
                    LinkTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1),
                    VerifiedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
            var keys = Directory.CreateTempSubdirectory("pp-max-outbox-");
            var protection = DataProtectionProvider.Create(keys);
            var settings = new SystemMaxNotificationService(db, protection);
            await settings.UpdateSettingsAsync(new UpdateMaxNotificationSettingsRequest
            {
                Enabled = true,
                BotToken = "bot-token",
                KeepExistingBotToken = false,
                WebhookSecret = "webhook-secret",
                KeepExistingWebhookSecret = false,
                NotificationsEnabled = true,
                DocumentsEnabled = true,
                QuietHoursEnabled = false
            });
            var time = new FixedTimeProvider(new DateTimeOffset(2026, 7, 31, 16, 0, 0, TimeSpan.Zero));
            var outbox = new MaxOutboxService(db, settings, protection, time);
            return new Fixture(connection, keys, protection, settings, time, db, outbox, user.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await _connection.DisposeAsync(); _keys.Delete(true);
        }
    }

    private sealed class RecordingApiClient(bool transientFailure = false) : IMaxApiClient
    {
        public List<(long UserId, string Text, Uri? Url)> Messages { get; } = [];
        public Task<MaxApiResult> CheckHealthAsync(string botToken, CancellationToken cancellationToken = default) => Task.FromResult(new MaxApiResult(true));
        public Task<MaxUploadSlotResult> CreateUploadSlotAsync(string mediaType, string botToken, CancellationToken cancellationToken = default) => Task.FromResult(new MaxUploadSlotResult(false));
        public Task<MaxApiResult> SendTextAsync(long userId, string text, string botToken, string? actionText = null, Uri? actionUrl = null, CancellationToken cancellationToken = default)
        {
            Messages.Add((userId, text, actionUrl));
            return Task.FromResult(transientFailure
                ? new MaxApiResult(false, ErrorCode: "rate-limited", IsTransient: true)
                : new MaxApiResult(true, "max-message-1"));
        }
    }

    private sealed class TimeoutApiClient : IMaxApiClient
    {
        public Task<MaxApiResult> CheckHealthAsync(string botToken, CancellationToken cancellationToken = default) => Task.FromResult(new MaxApiResult(true));
        public Task<MaxUploadSlotResult> CreateUploadSlotAsync(string mediaType, string botToken, CancellationToken cancellationToken = default) => Task.FromResult(new MaxUploadSlotResult(false));
        public Task<MaxApiResult> SendTextAsync(long userId, string text, string botToken, string? actionText = null, Uri? actionUrl = null, CancellationToken cancellationToken = default) =>
            Task.FromException<MaxApiResult>(new TaskCanceledException("timeout"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Set(DateTimeOffset value) => _now = value;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
