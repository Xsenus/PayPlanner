using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationServiceTests
{
    [Fact]
    public async Task TaskChanged_LongResultPreservesTitleInMetadata()
    {
        await using var db = await CreateContextAsync();
        var (task, author, assignee, _) = await CreateTaskWithObserverAsync(db, "long-result-title");
        task.Title = "Подготовить заявление по делу";
        task.Result = new string('Р', 2000);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyTaskChangedAsync(task, [$"Результат: — -> {task.Result}"], assignee.FullName, assignee.Id);

        var notifications = await db.UserNotifications.Where(item => item.Type == UserNotificationTypes.TaskChanged).ToListAsync();
        Assert.NotEmpty(notifications);
        Assert.Contains(notifications, item => item.RecipientUserId == author.Id);
        Assert.All(notifications, item =>
        {
            using var metadata = JsonDocument.Parse(item.MetadataJson);
            Assert.Equal(task.Title, metadata.RootElement.GetProperty("taskTitle").GetString());
            Assert.Equal(1000, item.BodyPreview.Length);
        });
    }

    [Fact]
    public async Task TaskReassigned_LongResultPreservesTitleForPreviousAssigneeAndObservers()
    {
        await using var db = await CreateContextAsync();
        var (task, author, previousAssignee, observer) = await CreateTaskWithObserverAsync(db, "reassigned-long-title");
        var newAssignee = CompletionUser(author.RoleId, "reassigned-title-next@example.test");
        db.Users.Add(newAssignee);
        await db.SaveChangesAsync();
        task.AssigneeUserId = newAssignee.Id;
        task.AssigneeName = newAssignee.FullName;
        task.Title = "Проверить документы перед передачей";
        task.Result = new string('Р', 2000);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyTaskReassignedAsync(task, previousAssignee.Id, previousAssignee.FullName,
            [$"Исполнитель: {previousAssignee.FullName} -> {newAssignee.FullName}", $"Результат: — -> {task.Result}"],
            author.FullName, author.Id);

        var notifications = await db.UserNotifications.Where(item => item.Type == UserNotificationTypes.TaskChanged).ToListAsync();
        Assert.Contains(notifications, item => item.RecipientUserId == previousAssignee.Id);
        Assert.Contains(notifications, item => item.RecipientUserId == observer.Id);
        Assert.All(notifications, item =>
        {
            using var metadata = JsonDocument.Parse(item.MetadataJson);
            Assert.Equal(task.Title, metadata.RootElement.GetProperty("taskTitle").GetString());
        });
    }
}
