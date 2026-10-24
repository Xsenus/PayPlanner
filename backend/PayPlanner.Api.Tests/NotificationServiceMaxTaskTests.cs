using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotifyAsync_TaskMaxLinkComesFromObjectAndNeverFromUserText(bool hasConsistentTaskId)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "max-task@fixture.local");
        var id = await CreateProtectedNotificationObjectAsync(db, "task", user);
        var linkedTaskId = hasConsistentTaskId ? id : await CreateProtectedNotificationObjectAsync(db, "task", user);
        var max = new RecordingMaxOutboxService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(),
            maxService: new ReadyMaxNotificationService(), maxOutboxService: max,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test/" });
        var envelope = new NotificationEventEnvelope(user.Id, UserNotificationTypes.TaskAssigned, "max:task:1",
            "https://foreign.example/SECRET", "PRIVATE comment", "task", id, linkedTaskId);
        Assert.True(await service.NotifyAsync(envelope));
        var queued = Assert.Single(max.Requests);
        Assert.Empty(queued.Text);
        Assert.Equal(hasConsistentTaskId ? $"https://crm.example.test/tasks/{id}" : "https://crm.example.test/", queued.ActionUrl);
        Assert.Equal(hasConsistentTaskId ? "Открыть задачу" : "Открыть CRM", queued.ActionText);
        Assert.Single(await db.UserNotifications.ToListAsync());
    }
}
