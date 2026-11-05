using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    public static IEnumerable<object[]> LegacyCourtCurrentCases() =>
        from kind in new[] { "event", "deadline", "hearing-date", "decision-date" }
        from channel in new[] { "email", "push", "max" }
        where channel != "email" || kind is "event" or "decision-date"
        from postgres in new[] { false, true }
        select new object[] { kind, channel, postgres };

    [Theory]
    [MemberData(nameof(LegacyCourtCurrentCases))]
    public async Task LegacyCourtCurrent_ChangedSourceCreatesNewNoticeAndPreservesHistory(string kind, string channel, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, true, channel == "email");
        if (channel != "email") db.UserSettings.Add(new()
        {
            UserId = seed.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>()))
        });
        var source = await db.ClientCaseCourtEvents.SingleAsync();
        source.Title = "LEGACY-OLD судебное решение";
        var clientCase = await db.ClientCases.SingleAsync();
        clientCase.Title = "LEGACY-OLD дело";
        clientCase.Stage = "Суд";
        clientCase.NextHearingAt = clientCase.CourtDecisionAt = DateTime.UtcNow.AddDays(3).Date;
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            Title = "LEGACY-OLD процессуальный срок",
            DeadlineType = "appeal",
            DueAt = DateTime.UtcNow.AddDays(5).Date,
            Status = "active"
        };
        db.ClientCaseProceduralDeadlines.Add(deadline);
        if (channel == "push") db.UserPushSubscriptions.Add(new()
        {
            UserId = seed.UserId,
            Endpoint = "https://push.synthetic.local/legacy-court",
            P256dh = "fake",
            Auth = "fake"
        });
        if (channel == "max") db.MaxRecipientLinks.Add(new()
        {
            UserId = seed.UserId,
            MaxUserId = 79001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        var email = new LegacyCourtEmail { Fail = true };
        var push = new LegacyCourtPush(db) { Fail = true };
        var maxApi = new SecondaryMaxApi { Fail = true };
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, email, new NotificationServiceTests.RecordingTelegramService(), realtime,
            channel == "push" ? push : null, maxService: channel == "max" ? runtime : null,
            maxOutboxService: channel == "max" ? new MaxOutboxService(db, runtime, protection, TimeProvider.System) : null,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        switch (kind)
        {
            case "event": await service.NotifyCaseCourtEventAsync(clientCase, source, "updated", "Автор", 0, [seed.UserId]); break;
            case "deadline": await service.NotifyCaseProceduralDeadlineAsync(clientCase, deadline, "updated", "Автор", 0, [seed.UserId]); break;
            case "hearing-date": await service.NotifyCaseHearingDateChangedAsync(clientCase, null, clientCase.NextHearingAt!.Value, "Автор", 0, [seed.UserId], DateTime.UtcNow); break;
            case "decision-date": await service.NotifyCaseDecisionDateChangedAsync(clientCase, null, clientCase.CourtDecisionAt, "Автор", 0, [seed.UserId], DateTime.UtcNow); break;
        }
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.StartsWith("case:", inbox.EventKey);
        var oldInbox = JsonSerializer.Serialize(inbox);
        var max = new MaxOutboxProcessor(db, runtime, maxApi, protection, TimeProvider.System, NullLogger<MaxOutboxProcessor>.Instance);
        var original = channel == "max" ? null : await db.NotificationDeliveries.AsNoTracking()
            .SingleAsync(row => row.Channel == (channel == "email" ? NotificationChannels.Email : NotificationChannels.WebPush));
        if (original is not null) Assert.Equal(NotificationDeliveryStatus.Failed, original.Status);
        var oldPush = channel == "push" ? Assert.Single(push.Payloads) : null;
        var oldBody = original?.BodyPreview;
        clientCase.Title = "CURRENT дело";
        clientCase.NextHearingAt = clientCase.CourtDecisionAt = DateTime.UtcNow.AddDays(9).Date;
        source.Title = "CURRENT судебное решение";
        source.ScheduledAt = source.ScheduledAt.AddDays(2);
        source.UpdatedAt = DateTime.UtcNow;
        deadline.Title = "CURRENT процессуальный срок";
        deadline.DueAt = deadline.DueAt.AddDays(3);
        deadline.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await DomainAsync(db);
        var beforeDeadline = JsonSerializer.Serialize(await db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync());
        email.Fail = push.Fail = maxApi.Fail = false;
        email.Bodies.Clear(); push.Payloads.Clear();
        if (channel == "email") await service.RetryDeliveryAsync(original!.Id);
        else if (channel == "push") await service.RetryDeliveryAsync(original!.Id);
        else
        {
            await db.MaxOutboxMessages.ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
            Assert.Equal(1, await max.ProcessBatchAsync(default));
            Assert.Equal(0, maxApi.Calls);
            var current = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "current-state");
            Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(current.Id, default));
            Assert.Equal(1, await max.ProcessBatchAsync(default));
        }
        var fresh = await db.UserNotifications.AsNoTracking().SingleAsync(row => row.Id != inbox.Id);
        Assert.StartsWith("case-court:", fresh.EventKey);
        Assert.NotEqual(inbox.EventKey, fresh.EventKey);
        Assert.Contains("CURRENT", fresh.Title);
        Assert.Contains("Актуальные сведения CRM", fresh.BodyPreview);
        Assert.DoesNotContain("LEGACY-OLD", fresh.BodyPreview);
        Assert.Single(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "current-state").ToArrayAsync());
        Assert.Single(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "legacy-origin").ToArrayAsync());
        if (original is not null)
        {
            var attempts = await db.NotificationDeliveries.CountAsync();
            await service.RetryDeliveryAsync(original.Id);
            Assert.Equal(attempts, await db.NotificationDeliveries.CountAsync());
        }
        Assert.Equal(oldInbox, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().SingleAsync(row => row.Id == inbox.Id)));
        Assert.Equal(before, await DomainAsync(db));
        Assert.Equal(beforeDeadline, JsonSerializer.Serialize(await db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync()));
        if (channel == "email") { Assert.DoesNotContain(oldBody!, email.Bodies); Assert.Contains(email.Bodies, text => text.Contains("CURRENT", StringComparison.Ordinal)); }
        if (channel == "push") { Assert.DoesNotContain(LegacyPushVisibleText(oldPush!), push.Payloads.Select(LegacyPushVisibleText)); Assert.Contains(push.Payloads.Select(LegacyPushVisibleText), text => text.Contains("CURRENT", StringComparison.Ordinal)); }
        if (channel == "max") Assert.Equal(1, maxApi.Calls);
    }

    private static string LegacyPushVisibleText(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("title").GetString() + "\n" + document.RootElement.GetProperty("body").GetString();
    }

    private sealed class LegacyCourtEmail : IEmailService
    {
        public bool Fail { get; set; }
        public List<string> Bodies { get; } = [];
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false,
            CancellationToken cancellationToken = default, EmailSendSettings? settings = null)
        {
            Bodies.Add(body);
            if (Fail) throw new IOException("Synthetic legacy court email failure");
            return Task.CompletedTask;
        }
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException();
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException();
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => throw new InvalidOperationException();
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => throw new InvalidOperationException();
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => throw new InvalidOperationException();
    }

    private sealed class LegacyCourtPush : WebPushNotificationService
    {
        private readonly PaymentContext context;
        public LegacyCourtPush(PaymentContext db) : base(db,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebPush:Enabled"] = "true",
            ["WebPush:PublicKey"] = "public-key",
            ["WebPush:PrivateKey"] = "private-key",
            ["WebPush:Subject"] = "mailto:test@synthetic.local"
        }).Build(), NullLogger<WebPushNotificationService>.Instance) => context = db;
        public bool Fail { get; set; }
        public List<string> Payloads { get; } = [];
        protected override Task SendSubscriptionAsync(UserPushSubscription subscription, string payload, string subject,
            string publicKey, string privateKey, CancellationToken cancellationToken)
        {
            Assert.Null(context.Database.CurrentTransaction);
            Payloads.Add(payload);
            if (Fail) throw new IOException("Synthetic legacy court push failure");
            return Task.CompletedTask;
        }
    }
}
