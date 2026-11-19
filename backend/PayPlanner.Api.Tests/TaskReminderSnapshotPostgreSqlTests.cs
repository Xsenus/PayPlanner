using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskReminderSnapshotPostgreSqlTests(PostgreSqlModelTemplate databases) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    [InlineData("assignee removed")]
    public async Task ReminderRechecksCurrentTaskOnPostgreSql(string change)
    {
        await using var database = await databases.CreateDatabaseAsync();
        var interceptor = new NotificationServiceTests.ChangeTaskDuringReminderDuplicateCheck(change);
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new PostgreSqlPaymentContext(options);
        Assert.Equal("false", await db.SystemSettings.Where(setting => setting.Key == "notifications.email.enabled")
            .Select(setting => setting.Value).SingleAsync());
        var assignee = await NotificationServiceTests.CreateUserAsync(db, "reminder-race@fixture.local");
        var dueAt = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Synthetic task",
            Title = "Reminder to current assignee",
            AssigneeUserId = assignee.Id,
            DueAt = dueAt,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var snapshot = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        interceptor.TaskId = task.Id;
        var email = new NotificationServiceTests.RecordingEmailService();
        var realtime = new NotificationServiceTests.RecordingRealtimeNotificationPublisher();
        var service = NotificationServiceTests.CreateService(db, email,
            new NotificationServiceTests.RecordingTelegramService(), realtime);

        var deliveries = await service.NotifyTaskReminderAsync(snapshot, "atDue", dueAt);

        Assert.True(interceptor.Triggered);
        Assert.Empty(email.Messages);
        Assert.Empty(deliveries);
        Assert.False(await db.NotificationDeliveries.AnyAsync());
        if (change == "unchanged")
        {
            var notification = await db.UserNotifications.SingleAsync();
            Assert.Equal(assignee.Id, notification.RecipientUserId);
            Assert.Equal(notification.Id, Assert.Single(realtime.Notifications).Id);
        }
        else
        {
            Assert.False(await db.UserNotifications.AnyAsync());
            Assert.Empty(realtime.Notifications);
        }
    }
}
