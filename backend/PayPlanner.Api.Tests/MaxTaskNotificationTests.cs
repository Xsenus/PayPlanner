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
    [InlineData(UserNotificationTypes.TaskAssigned, " — ВЫСОКИЙ", "Задача назначена исполнителю.", "Ознакомиться с задачей и сроком выполнения")]
    [InlineData(UserNotificationTypes.TaskReminder, " — СРОЧНО", "Напоминание о выполнении задачи.", "Проверить выполнение задачи и действующий срок")]
    [InlineData(UserNotificationTypes.TaskChanged, "", "Изменены данные задачи.", "Проверить изменения в карточке задачи")]
    [InlineData(UserNotificationTypes.TaskComment, "", "В задаче появился новый комментарий.", "Прочитать комментарий в карточке задачи")]
    [InlineData(UserNotificationTypes.TaskCompleted, "", "Работа по задаче завершена.", "Ознакомиться с результатом в карточке задачи")]
    [InlineData(UserNotificationTypes.TaskCancelled, " — ВЫСОКИЙ", "Задача отменена.", "не требуется")]
    [InlineData(UserNotificationTypes.TaskScheduleConflict, " — ВЫСОКИЙ", "Обнаружено пересечение задач в расписании.", "Проверить расписание и сроки задач")]
    [InlineData(UserNotificationTypes.TaskApprovalRequested, " — ВЫСОКИЙ", "Задача передана на согласование.", "Принять решение в карточке задачи")]
    [InlineData(UserNotificationTypes.TaskApproved, "", "Задача согласована.", "не требуется")]
    [InlineData(UserNotificationTypes.TaskRevisionRequested, " — ВЫСОКИЙ", "Запрошена доработка задачи.", "Ознакомиться с замечаниями и доработать задачу")]
    [InlineData(UserNotificationTypes.TaskDeadlineChangeRequested, " — ВЫСОКИЙ", "Запрошен перенос срока задачи.", "Рассмотреть запрос в карточке задачи")]
    [InlineData(UserNotificationTypes.TaskDeadlineChangeApproved, "", "Перенос срока задачи согласован.", "Ознакомиться с новым сроком задачи")]
    [InlineData(UserNotificationTypes.TaskDeadlineChangeRejected, " — ВЫСОКИЙ", "Запрос переноса срока задачи отклонён.", "Проверить действующий срок и решение в карточке задачи")]
    [InlineData(UserNotificationTypes.TaskApprovalOverdue, " — СРОЧНО", "Срок согласования задачи истёк.", "Рассмотреть задачу в карточке согласования")]
    public async Task TaskNotification_RendersFreshTitleEventAndCanonicalLink(string type, string prioritySuffix, string summary, string action)
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
        var label = UserNotificationTypes.Find(type)!.Label;
        Assert.Equal(string.Join('\n',
            $"[{label.ToUpperInvariant()}] {label}{prioritySuffix}",
            $"Объект: задача №{task.Id}, Актуальное название",
            $"Суть: {summary}",
            "Данные: срок 01.08.2026 19:00 (Europe/Moscow), статус Новая, приоритет Обычный, исполнитель не назначен",
            $"Действие: {action}",
            $"Контекст: https://crm.example.test/tasks/{task.Id}",
            "Кнопки: Открыть задачу"), sent.Text);
        Assert.Equal(7, sent.Text.Split('\n').Length);
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
        Assert.Equal(7, lines.Length);
        Assert.StartsWith($"Объект: задача №{task.Id}, Договор готов 👩‍💻", lines[1]);
        Assert.EndsWith("👩‍💻…", lines[1]);
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

    [Theory]
    [InlineData(false, "done")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "done")]
    [InlineData(true, "cancelled")]
    [InlineData(false, " Done ")]
    [InlineData(true, " CANCELLED ")]
    public async Task TaskReminder_DeferredDeliveryCancelsAfterTaskCloses(bool retry, string status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        if (!retry) await fixture.ConfigureLimitsAsync(quietHoursEnabled: true, dailyLimit: 20);
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskReminder), CancellationToken.None);
        if (retry)
        {
            await fixture.Processor(new RecordingApiClient(transientFailure: true), TaskFrontend).ProcessBatchAsync(CancellationToken.None);
            Assert.Equal(MaxOutboxStatuses.Failed, queued.Status);
        }
        else
        {
            Assert.True(queued.NextAttemptAt > fixture.Now);
        }

        task.Status = status;
        await fixture.Db.SaveChangesAsync();
        fixture.MoveTimeTo(new DateTimeOffset(queued.NextAttemptAt, TimeSpan.Zero));
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal("task-no-longer-active", queued.LastErrorCode);
        Assert.Null(queued.ProviderMessageId);
        Assert.Null(queued.SentAt);
    }

    [Theory]
    [InlineData(UserNotificationTypes.TaskCompleted, "done")]
    [InlineData(UserNotificationTypes.TaskCompleted, "cancelled")]
    [InlineData(UserNotificationTypes.TaskComment, "done")]
    [InlineData(UserNotificationTypes.TaskComment, "cancelled")]
    [InlineData(UserNotificationTypes.TaskChanged, "done")]
    [InlineData(UserNotificationTypes.TaskChanged, "cancelled")]
    public async Task TaskNotification_ClosedTaskRetainsHistoricalEvents(string type, string status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, type), CancellationToken.None);
        task.Status = status;
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Single(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Sent, queued.Status);
        Assert.Null(queued.LastErrorCode);
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
