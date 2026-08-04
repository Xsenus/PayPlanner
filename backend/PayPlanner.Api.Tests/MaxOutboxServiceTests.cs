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

public sealed class MaxOutboxServiceTests
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
    public async Task QuietHoursAndDailyRecipientLimitRejectBeforeCreatingMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: true, dailyLimit: 20);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None));
        Assert.Empty(await fixture.Db.MaxOutboxMessages.ToListAsync());

        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);
        var first = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(first.Id, (await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(
            fixture.Request() with { EventKey = "critical:event:2" }, CancellationToken.None));
        Assert.Single(await fixture.Db.MaxOutboxMessages.ToListAsync());
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

        public MaxOutboxProcessor Processor(IMaxApiClient api) => new(
            Db, _settings, api, _protection, _time, NullLogger<MaxOutboxProcessor>.Instance);

        public async Task ConfigureLimitsAsync(bool quietHoursEnabled, int dailyLimit) => await _settings.UpdateSettingsAsync(new UpdateMaxNotificationSettingsRequest
        {
            Enabled = true,
            KeepExistingBotToken = true,
            KeepExistingWebhookSecret = true,
            NotificationsEnabled = true,
            DocumentsEnabled = true,
            QuietHoursEnabled = quietHoursEnabled,
            QuietHoursStart = "20:00",
            QuietHoursEnd = "09:00",
            QuietHoursTimeZone = "Asia/Novosibirsk",
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
        public override DateTimeOffset GetUtcNow() => now;
    }
}
