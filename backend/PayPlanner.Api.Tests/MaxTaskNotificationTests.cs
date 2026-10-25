using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxServiceTests
{
    private static IConfiguration TaskFrontend => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test/" }).Build();

    private static async Task<TaskJournalItem> AddMaxTaskAsync(Fixture fixture, string title = "Подготовить договор")
    {
        var user = await fixture.Db.Users.SingleAsync(x => x.Id == fixture.UserId);
        fixture.Db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Tasks,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });
        var task = new TaskJournalItem
        {
            Title = title,
            ObjectName = "Synthetic object",
            AssigneeUserId = user.Id,
            Description = "PRIVATE body with client and financial data",
            DueAt = fixture.Now.AddDays(1),
        };
        fixture.Db.TaskJournalItems.Add(task);
        await fixture.Db.SaveChangesAsync();
        return task;
    }

    private static MaxQueueRequest TaskRequest(Fixture fixture, TaskJournalItem task, string type = UserNotificationTypes.TaskAssigned) =>
        fixture.Request() with { NotificationType = type, ActionUrl = $"https://crm.example.test/tasks/{task.Id}", ActionText = "Открыть задачу" };

    [Theory]
    [InlineData(UserNotificationTypes.TaskAssigned)]
    [InlineData(UserNotificationTypes.TaskChanged)]
    [InlineData(UserNotificationTypes.TaskComment)]
    [InlineData(UserNotificationTypes.TaskCompleted)]
    [InlineData(UserNotificationTypes.TaskApprovalRequested)]
    [InlineData(UserNotificationTypes.TaskDeadlineChangeRejected)]
    public async Task TaskNotification_RendersFreshTitleEventAndCanonicalLink(string type)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var message = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, type) with { EventKey = new string('я', 240) }, CancellationToken.None);
        Assert.DoesNotContain(task.Title, fixture.ReadPayload(message));
        task.Title = "Актуальное название";
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var sent = Assert.Single(api.Messages);
        Assert.Equal($"Задача: Актуальное название\n{UserNotificationTypes.Find(type)!.Label}\nhttps://crm.example.test/tasks/{task.Id}", sent.Text);
        Assert.Equal($"https://crm.example.test/tasks/{task.Id}", sent.Url!.AbsoluteUri);
        Assert.DoesNotContain("PRIVATE", sent.Text);
        Assert.Equal(MaxOutboxStatuses.Sent, message.Status);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("participant")]
    [InlineData("active")]
    [InlineData("approval")]
    [InlineData("deleted")]
    [InlineData("sales")]
    public async Task TaskNotification_CancelsAfterAccessIsLost(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var message = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task), CancellationToken.None);
        var user = await fixture.Db.Users.SingleAsync();
        if (change == "permission") (await fixture.Db.RolePermissions.SingleAsync()).CanView = false;
        if (change == "participant") task.AssigneeUserId = null;
        if (change == "active") user.IsActive = false;
        if (change == "approval") user.IsApproved = false;
        if (change == "deleted") fixture.Db.TaskJournalItems.Remove(task);
        if (change == "sales") task.SalesRequestId = 900;
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);
        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, message.Status);
    }

    [Theory]
    [InlineData("https://foreign.example.test/tasks/1")]
    [InlineData("https://crm.example.test:444/tasks/1")]
    [InlineData("https://actor@crm.example.test/tasks/1")]
    [InlineData("https://crm.example.test/tasks/1?export=1")]
    [InlineData("https://crm.example.test/tasks/1#private")]
    [InlineData("https://crm.example.test/tasks/01")]
    [InlineData("https://crm.example.test/tasks/-1")]
    [InlineData("https://crm.example.test/tasks/2147483648")]
    [InlineData("https://crm.example.test/tasks/1/files")]
    public async Task TaskNotification_RejectsNoncanonicalTaskLinks(string url)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var message = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task) with { ActionUrl = url }, CancellationToken.None);
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);
        Assert.Empty(api.Messages);
        Assert.Equal("task-link-invalid", message.LastErrorCode);
    }

    [Theory]
    [InlineData("optout")]
    [InlineData("unlink")]
    [InlineData("permission")]
    [InlineData("participant")]
    public async Task TaskNotification_RechecksSecurityAfterEarlierMessageInSameBatch(string revoke)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        await fixture.Outbox.QueueAsync(TaskRequest(fixture, task) with { EventKey = "task:first" }, CancellationToken.None);
        var second = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task) with { EventKey = "task:second" }, CancellationToken.None);
        var api = new CallbackMaxApi(async () =>
        {
            if (revoke == "optout") fixture.Db.UserSettings.Add(new UserSetting
            {
                UserId = fixture.UserId,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                    [UserNotificationTypes.TaskAssigned], new Dictionary<string, IReadOnlyList<string>>())),
            });
            if (revoke == "unlink") (await fixture.Db.MaxRecipientLinks.SingleAsync()).Status = MaxRecipientLinkStatuses.OptedOut;
            if (revoke == "permission") (await fixture.Db.RolePermissions.SingleAsync()).CanView = false;
            if (revoke == "participant") task.AssigneeUserId = null;
            await fixture.Db.SaveChangesAsync();
        });
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(1, api.Calls);
        Assert.Equal(MaxOutboxStatuses.Cancelled, second.Status);
    }

    [Fact]
    public async Task TaskNotification_FormatsOneLineTitleWithoutBreakingEmojiOrCopyingBody()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture, "  Договор\r\n\tготов\u202E " + string.Concat(Enumerable.Repeat("👩‍💻", 170)));
        await fixture.Outbox.QueueAsync(TaskRequest(fixture, task), CancellationToken.None);
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);
        var text = Assert.Single(api.Messages).Text;
        var lines = text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Задача: Договор готов 👩‍💻", lines[0]);
        Assert.EndsWith("👩‍💻…", lines[0]);
        Assert.DoesNotContain('\u202E', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain("PRIVATE", text);
        Assert.True(text.Length < MaxApiClient.MaxTextLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskNotification_DeferredRetryStillChecksAccess(bool retry)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        if (!retry) await fixture.ConfigureLimitsAsync(quietHoursEnabled: true, dailyLimit: 20);
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task), CancellationToken.None);
        if (retry) await fixture.Processor(new RecordingApiClient(transientFailure: true), TaskFrontend).ProcessBatchAsync(CancellationToken.None);
        fixture.MoveTimeTo(new DateTimeOffset(queued.NextAttemptAt, TimeSpan.Zero));
        task.AssigneeUserId = null;
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);
        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
    }

    private sealed class CallbackMaxApi(Func<Task> afterFirstSend) : IMaxApiClient
    {
        public int Calls { get; private set; }
        public Task<MaxApiResult> CheckHealthAsync(string botToken, CancellationToken cancellationToken = default) => Task.FromResult(new MaxApiResult(true));
        public Task<MaxUploadSlotResult> CreateUploadSlotAsync(string mediaType, string botToken, CancellationToken cancellationToken = default) => Task.FromResult(new MaxUploadSlotResult(false));
        public async Task<MaxApiResult> SendTextAsync(long userId, string text, string botToken, string? actionText = null, Uri? actionUrl = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1) await afterFirstSend();
            return new MaxApiResult(true, "synthetic");
        }
    }
}
