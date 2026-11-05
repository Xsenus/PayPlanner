using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    public static IEnumerable<object[]> CourtReminderAdmissionCases() =>
        from mode in new[] { "closed-deadline", "moved-date", "closed-case", "access", "personal", "post-save" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(CourtReminderAdmissionCases))]
    public async Task CourtReminder_AdmissionUsesCurrentStateAndPersistsRecoverableDelivery(string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, false, false);
        var clientCase = await db.ClientCases.SingleAsync();
        clientCase.Stage = "Суд";
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            Title = "Подать апелляцию",
            DeadlineType = "appeal",
            DueAt = DateTime.UtcNow.Date.AddDays(2),
            WarningDaysBefore = 7,
            Status = "active"
        };
        db.ClientCaseProceduralDeadlines.Add(deadline);
        db.UserSettings.Add(new()
        {
            UserId = seed.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([],
                mode == "personal" ? new Dictionary<string, IReadOnlyList<string>> { [UserNotificationTypes.CaseProceduralDeadline] = [NotificationChannels.InApp] }
                    : new Dictionary<string, IReadOnlyList<string>>()))
        });
        await db.SaveChangesAsync();
        if (mode == "closed-deadline") await db.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "closed"));
        if (mode == "moved-date") await db.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.DueAt, deadline.DueAt.AddDays(30)));
        if (mode == "closed-case") await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, ClientCaseStatus.Closed));
        if (mode == "access") await db.Roles.ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "restricted-synthetic"));
        var domain = await DomainAsync(db);
        var deadlines = JsonSerializer.Serialize(await db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync());
        var publisher = new ReminderAdmissionRealtime { Fail = mode == "post-save" };
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        var created = -1;
        var error = await Record.ExceptionAsync(async () => created = await service.NotifyCaseProceduralDeadlineRemindersAsync([new(clientCase, deadline, [seed.UserId])]));
        if (mode == "post-save")
        {
            var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
            publisher.Fail = false;
            Assert.Equal(0, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(clientCase, deadline, [seed.UserId])]));
            var intents = await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.Kind == "reminder").ToArrayAsync();
            foreach (var intent in intents) await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(intent.Id, default);
            Assert.Single(publisher.Delivered, row => row.Id == inbox.Id);
            Assert.Null(error);
            Assert.Equal(1, created);
            Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(0, created);
            Assert.Empty(await db.UserNotifications.ToArrayAsync());
            Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "reminder").ToArrayAsync());
        }
        Assert.Equal(domain, await DomainAsync(db));
        Assert.Equal(deadlines, JsonSerializer.Serialize(await db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync()));
    }

    private sealed class ReminderAdmissionRealtime : IRealtimeNotificationPublisher, IReliableRealtimeNotificationPublisher
    {
        public bool Fail { get; set; }
        public List<UserNotification> Delivered { get; } = [];
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Synthetic reminder transport failure");
            Delivered.Add(notification); return Task.CompletedTask;
        }
        public Task PublishUserNotificationOrThrowAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => PublishUserNotificationAsync(notification, toastEnabled, cancellationToken);
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
