using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtNotificationChannelIntegrationTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> EmailCases() =>
        from mode in new[] { "failed", "pending", "ban", "journal", "stale", "two" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(EmailCases))]
    public async Task CourtNoticeChannels_EmailRecoveryRespectsLedgerCurrentSettingsAndCompletedRecipient(string mode, bool postgres)
    {
        await using var database = postgres ? await template.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, decision: true, emailEnabled: true, secondRecipient: mode == "two");
        var email = new CourtEmail { Fail = mode is "failed" or "ban", Unknown = mode is "pending" or "stale" };
        var realtime = new CourtNoticeIntegrationRealtime(db) { FailJournal = mode == "journal" };
        if (mode == "two") realtime.BeforeUser = _ => email.Attempts.Count == 1
            ? Task.FromException(new IOException("Synthetic failure after first recipient email")) : Task.CompletedTask;
        NotificationService NewService() => NotificationServiceTests.CreateService(db, email,
            new NotificationServiceTests.RecordingTelegramService(), realtime);
        var processor = new CaseCourtNotificationDispatchProcessor(db, NewService(), realtime);
        Assert.False(await processor.ProcessAsync(seed.IntentId, default));
        Assert.Single(email.Attempts);
        Assert.Equal(mode is "pending" or "stale" ? NotificationDeliveryStatus.Pending
            : mode is "failed" or "ban" ? NotificationDeliveryStatus.Failed : NotificationDeliveryStatus.Sent,
            (await db.NotificationDeliveries.AsNoTracking().SingleAsync(row => row.Channel == NotificationChannels.Email)).Status);
        if (mode == "ban")
        {
            var preference = await db.UserSettings.SingleAsync(row => row.UserId == seed.UserId && row.Key == UserNotificationPreferenceSettings.SettingKey);
            preference.Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([],
                new Dictionary<string, IReadOnlyList<string>> { [UserNotificationTypes.CourtDecisionRecorded] = [NotificationChannels.Email] }));
            await db.SaveChangesAsync();
        }
        if (mode == "stale") await db.ClientCaseCourtEvents.Where(row => row.Id == seed.SourceId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Новое состояние решения"));
        db.ChangeTracker.Clear();
        var before = await DomainAsync(db);
        var oldInbox = await db.UserNotifications.AsNoTracking().OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync();
        email.Fail = email.Unknown = false;
        realtime.FailJournal = false;
        realtime.BeforeUser = null;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == seed.IntentId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        var restarted = new CaseCourtNotificationDispatchProcessor(db, NewService(), realtime);
        Assert.Equal(mode != "pending", await restarted.ProcessAsync(seed.IntentId, default));
        var final = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        if (mode == "pending")
        {
            Assert.NotNull(final.BlockedAt);
            Assert.Null(final.CompletedAt);
            Assert.False(await restarted.ProcessAsync(seed.IntentId, default));
        }
        else Assert.NotNull(final.CompletedAt);
        Assert.Equal(mode is "failed" or "two" ? 2 : 1, email.Attempts.Count);
        if (mode == "two") Assert.Equal(2, email.Attempts.Distinct().Count());
        Assert.Equal(before, await DomainAsync(db));
        Assert.Equal(oldInbox, await db.UserNotifications.AsNoTracking().OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync());
        Assert.Equal(mode == "two" ? 2 : 1, await db.UserNotifications.CountAsync());
        if (mode == "failed")
        {
            Assert.Equal(1, await db.NotificationDeliveries.CountAsync(row => row.Status == NotificationDeliveryStatus.Failed));
            Assert.Equal(1, await db.NotificationDeliveries.CountAsync(row => row.Status == NotificationDeliveryStatus.Sent));
        }
        if (mode == "two") Assert.Equal(2, await db.NotificationDeliveries.CountAsync(row => row.Status == NotificationDeliveryStatus.Sent));
    }

    public static IEnumerable<object[]> MaxCases() =>
        from mode in new[] { "before", "after", "ban", "unlink", "stale" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(MaxCases))]
    public async Task CourtNoticeChannels_MaxQueueKeepsStableIdentityAndRechecksCurrentConsent(string mode, bool postgres)
    {
        await using var database = postgres ? await template.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, decision: false, emailEnabled: false);
        var link = new MaxRecipientLink
        {
            UserId = seed.UserId,
            MaxUserId = 79001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        };
        db.MaxRecipientLinks.Add(link); await db.SaveChangesAsync();
        var runtime = new CourtMaxSettings();
        var realQueue = new MaxOutboxService(db, runtime, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var queue = new CourtMaxFailure(realQueue, mode == "after");
        var realtime = new CourtNoticeIntegrationRealtime(db);
        NotificationService NewService() => NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), realtime, maxService: runtime, maxOutboxService: queue,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, NewService(), realtime).ProcessAsync(seed.IntentId, default));
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Single(queue.Keys);
        Assert.Equal(mode == "after" ? 1 : 0, await db.MaxOutboxMessages.CountAsync());
        if (mode == "ban")
        {
            db.UserSettings.Add(new()
            {
                UserId = seed.UserId,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([],
                    new Dictionary<string, IReadOnlyList<string>> { [inbox.Type] = [NotificationChannels.Max] }))
            });
            await db.SaveChangesAsync();
        }
        if (mode == "unlink") await db.MaxRecipientLinks.Where(row => row.Id == link.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, MaxRecipientLinkStatuses.OptedOut));
        if (mode == "stale") await db.ClientCaseCourtEvents.Where(row => row.Id == seed.SourceId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Перенесённое заседание"));
        db.ChangeTracker.Clear();
        var before = await DomainAsync(db);
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == seed.IntentId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, NewService(), realtime).ProcessAsync(seed.IntentId, default));
        Assert.Equal(mode is "before" or "after" ? 2 : 1, queue.Keys.Count);
        Assert.All(queue.Keys, key => Assert.Equal(inbox.EventKey, key));
        Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        if (mode is "before" or "after")
        {
            var queued = await db.MaxOutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal(inbox.EventKey, queued.EventKey);
            Assert.Equal(MaxOutboxStatuses.Queued, queued.Status);
            Assert.Equal(0, queued.AttemptCount);
        }
        else Assert.Empty(await db.MaxOutboxMessages.ToArrayAsync());
        Assert.Equal(before, await DomainAsync(db));
    }

    private static PostgreSqlPaymentContext OpenPostgres(string connection) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).Options);
    private static async Task<PaymentContext> OpenSqliteAsync()
    {
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); return db;
    }
    private sealed record Seed(long IntentId, int SourceId, int UserId);
    private static async Task<Seed> SeedAsync(PaymentContext db, bool decision, bool emailEnabled, bool secondRecipient = false)
    {
        var role = new Role { Name = "admin", Description = "Synthetic notification role" };
        User User(string name) => new() { Email = name + "@synthetic.local", FullName = name, PasswordHash = "synthetic", Role = role, IsActive = true, IsApproved = true };
        var first = User("court-channel-first"); db.Users.Add(first);
        var second = secondRecipient ? User("court-channel-second") : first;
        if (secondRecipient) db.Users.Add(second);
        await db.SaveChangesAsync();
        if (emailEnabled)
            foreach (var user in new[] { first, second }.DistinctBy(user => user.Id))
                db.UserSettings.Add(new()
                {
                    UserId = user.Id,
                    Key = UserNotificationPreferenceSettings.SettingKey,
                    Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>()))
                });
        var clientCase = new ClientCase
        {
            Title = "Судебное дело каналов",
            CaseNumber = "SYNTHETIC-CHANNELS",
            Client = new Client { Name = "Синтетический клиент", IsActive = true, ResponsibleUserId = first.Id },
            ResponsibleUserId = second.Id
        };
        db.ClientCases.Add(clientCase); await db.SaveChangesAsync();
        var source = new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            Title = "Судебное событие",
            EventType = decision ? "decision" : "hearing",
            MovementStatus = decision ? "completed" : "scheduled",
            ResultSummary = decision ? "Синтетическое решение" : "",
            ScheduledAt = DateTime.UtcNow.AddDays(1)
        };
        db.ClientCaseCourtEvents.Add(source);
        var settings = new Dictionary<string, string> { ["notifications.email.enabled"] = emailEnabled ? "true" : "false" };
        if (emailEnabled)
        {
            settings["notifications.email.smtpHost"] = "smtp.synthetic.local"; settings["notifications.email.smtpPort"] = "587";
            settings["notifications.email.smtpUsername"] = "robot@synthetic.local"; settings["notifications.email.smtpPassword"] = "synthetic-only";
            settings["notifications.email.fromEmail"] = "robot@synthetic.local"; settings["notifications.email.fromName"] = "Synthetic";
            settings["notifications.email.enableSsl"] = "true";
        }
        foreach (var (key, value) in settings)
        {
            var row = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == key);
            if (row is null) db.SystemSettings.Add(new() { Key = key, Value = value }); else row.Value = value;
        }
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var id = Assert.Single(await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(clientCase, 0, "Сотрудник",
            [new("event", "created", source, null, true)], default));
        await transaction.CommitAsync();
        db.ChangeTracker.Clear(); return new(id, source.Id, first.Id);
    }
    private static async Task<string> DomainAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Cases = await db.ClientCases.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Events = await db.ClientCaseCourtEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Tasks = await db.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
    });
    private sealed class CourtEmail : IEmailService
    {
        public List<string> Attempts { get; } = [];
        public bool Fail { get; set; }
        public bool Unknown { get; set; }
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false,
            CancellationToken cancellationToken = default, EmailSendSettings? settings = null)
        {
            Attempts.Add(toEmail);
            if (Unknown) throw new OperationCanceledException("Synthetic unknown email result");
            if (Fail) throw new IOException("Synthetic email failure");
            return Task.CompletedTask;
        }
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException();
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException();
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => throw new InvalidOperationException();
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => throw new InvalidOperationException();
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => throw new InvalidOperationException();
    }
    private sealed class CourtMaxSettings : IMaxNotificationService
    {
        public Task<MaxRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken ct = default) => Task.FromResult(new MaxRuntimeSettings(
            true, false, "synthetic-token", "synthetic-secret", true, false, false, false, false, new TimeOnly(20, 0), new TimeOnly(9, 0), "Asia/Novosibirsk", 20, 30));
        public Task<MaxNotificationDiagnosticsDto> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new MaxNotificationDiagnosticsDto { Enabled = true, Ready = true });
        public Task<MaxNotificationDiagnosticsDto> UpdateSettingsAsync(UpdateMaxNotificationSettingsRequest request, CancellationToken ct = default) => GetSettingsAsync(ct);
    }
    private sealed class CourtMaxFailure(IMaxOutboxService inner, bool afterCommit) : IMaxOutboxService
    {
        public List<string> Keys { get; } = [];
        public async Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct)
        {
            Keys.Add(request.EventKey);
            if (Keys.Count == 1 && !afterCommit) throw new IOException("Synthetic failure before MAX queue");
            var row = await inner.QueueAsync(request, ct);
            if (Keys.Count == 1) throw new IOException("Synthetic lost MAX queue acknowledgement");
            return row;
        }
        public Task<bool> RetryAsync(Guid publicId, int? actorUserId, string actorName, CancellationToken ct) => inner.RetryAsync(publicId, actorUserId, actorName, ct);
    }
}
