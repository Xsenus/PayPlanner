using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationServiceTests
{
    [Theory]
    [InlineData("event")]
    [InlineData("decision")]
    [InlineData("deadline")]
    public async Task CourtRecoveryBaseline_StoredInboxSurvivesRealtimeFailureButPlainRetryDoesNotRepublish(string kind)
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "court-recovery@synthetic.local");
        var client = new Client { Name = "Клиент судебного уведомления", IsActive = true, ResponsibleUserId = recipient.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var clientCase = new ClientCase { ClientId = client.Id, Title = "Дело восстановления", CaseNumber = "COURT-RECOVERY", ResponsibleUserId = recipient.Id };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var courtEvent = new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            Title = "Сохранённое событие",
            EventType = kind == "decision" ? "decision" : "hearing",
            MovementStatus = kind == "decision" ? "held" : "scheduled",
            ScheduledAt = DateTime.UnixEpoch.AddDays(3),
            UpdatedAt = DateTime.UnixEpoch.AddDays(2)
        };
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            Title = "Сохранённый срок",
            DeadlineType = "appeal",
            DueAt = DateTime.UnixEpoch.AddDays(4),
            UpdatedAt = DateTime.UnixEpoch.AddDays(2)
        };
        db.ClientCaseCourtEvents.Add(courtEvent);
        db.ClientCaseProceduralDeadlines.Add(deadline);
        await db.SaveChangesAsync();
        Task Notify(NotificationService service) => kind == "deadline"
            ? service.NotifyCaseProceduralDeadlineAsync(clientCase, deadline, "created", "Автор", 999, [recipient.Id])
            : service.NotifyCaseCourtEventAsync(clientCase, courtEvent, "created", "Автор", 999, [recipient.Id]);
        var failing = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), new CourtRecoveryFailingRealtime());
        await Assert.ThrowsAsync<CourtRecoveryRealtimeFailure>(() => Notify(failing));
        var notification = await db.UserNotifications.AsNoTracking().SingleAsync();
        var recorder = new RecordingRealtimeNotificationPublisher();
        var restarted = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), recorder);
        await Notify(restarted);
        Assert.Empty(recorder.Notifications);
        Assert.Equal(notification.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.False(notification.IsRead);
    }

    private sealed class CourtRecoveryRealtimeFailure : Exception;

    private sealed class CourtRecoveryFailingRealtime : IRealtimeNotificationPublisher
    {
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => throw new CourtRecoveryRealtimeFailure();

        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
