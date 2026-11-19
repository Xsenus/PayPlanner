using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenLineTaskNotification_RendersBothPayloadGenerationsWithoutCopyingCustomBody(bool legacy)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        const string legacyText = "В CRM новое уведомление: Изменение задачи. Откройте CRM, чтобы безопасно посмотреть подробности.";
        Assert.Equal(legacyText, MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.TaskChanged));
        fixture.Db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = """{"templates":[{"key":"task.changed","isEnabled":true,"subject":"PRIVATE CUSTOM SUBJECT","body":"PRIVATE CUSTOM BODY\nwith another line"}]}""",
        });
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskChanged) with
        {
            EventKey = "task:seven-lines:custom",
            Text = "PRIVATE request text\nwith another line",
        }, CancellationToken.None);
        if (legacy) fixture.SetPayload(queued, legacyText);
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            Type = UserNotificationTypes.TaskChanged,
            EventKey = queued.EventKey,
            ObjectType = "task",
            ObjectId = task.Id,
            TaskJournalItemId = task.Id,
            Title = "PRIVATE CUSTOM SUBJECT",
            BodyPreview = "PRIVATE CUSTOM BODY\nwith another line",
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var sent = Assert.Single(api.Messages);
        Assert.Equal(7, sent.Text.Split('\n').Length);
        Assert.Contains($"Объект: задача №{task.Id}, {task.Title}", sent.Text);
        Assert.Contains("Суть: Изменены данные задачи.", sent.Text);
        Assert.DoesNotContain("PRIVATE", sent.Text);
        Assert.Equal($"https://crm.example.test/tasks/{task.Id}", sent.Url!.AbsoluteUri);
        Assert.Equal(MaxOutboxStatuses.Sent, queued.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SevenLineTaskNotification_UsesOnlyChangeMetadataBoundToTheAuthorizedTask(bool matchingTask)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var sourceTask = task;
        if (!matchingTask)
        {
            sourceTask = new TaskJournalItem
            {
                Title = "Other task",
                ObjectName = "Other",
                DueAt = fixture.Now,
            };
            fixture.Db.TaskJournalItems.Add(sourceTask);
            await fixture.Db.SaveChangesAsync();
        }
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskChanged) with
        {
            EventKey = "task:seven-lines:metadata",
        }, CancellationToken.None);
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            Type = UserNotificationTypes.TaskChanged,
            EventKey = queued.EventKey,
            ObjectType = "task",
            ObjectId = sourceTask.Id,
            TaskJournalItemId = sourceTask.Id,
            MetadataJson = JsonSerializer.Serialize(new
            {
                changeLabels = matchingTask
                    ? new[] { "Срок:\r\n01.08.2026 -> 02.08.2026", "Приоритет: высокий" }
                    : new[] { "Срок: CONFIDENTIAL change in another task" },
            }),
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var text = Assert.Single(api.Messages).Text;
        var lines = text.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.Equal(matchingTask
            ? "Суть: Изменены поля: срок, приоритет"
            : "Суть: Изменены данные задачи.", lines[2]);
        Assert.DoesNotContain("CONFIDENTIAL", text);
        Assert.DoesNotContain('\r', text);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"changeLabels":"PRIVATE arbitrary text"}""")]
    [InlineData("""{"changeLabels":[null,4,{}]}""")]
    public async Task SevenLineTaskNotification_InvalidMetadataKeepsTheTypedEventSummary(string metadata)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskChanged) with
        {
            EventKey = "task:seven-lines:invalid-metadata",
        }, CancellationToken.None);
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            Type = UserNotificationTypes.TaskChanged,
            EventKey = queued.EventKey,
            ObjectType = "task",
            ObjectId = task.Id,
            TaskJournalItemId = task.Id,
            MetadataJson = metadata,
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var text = Assert.Single(api.Messages).Text;
        Assert.Equal(7, text.Split('\n').Length);
        Assert.Contains("Суть: Изменены данные задачи.", text);
        Assert.DoesNotContain("PRIVATE", text);
    }

    [Fact]
    public async Task SevenLineTaskNotification_FormatsFreshDeadlineAndAssigneeInTheRecipientTimeZone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        fixture.Db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.UserId,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Kamchatka",
        });
        await fixture.Outbox.QueueAsync(TaskRequest(fixture, task), CancellationToken.None);
        task.DueAt = new DateTime(2026, 11, 4, 14, 0, 0, DateTimeKind.Utc);
        task.Status = "inProgress";
        task.Priority = "urgent";
        task.AssigneeName = "  Иван\r\n\tПетров\u202E ";
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var lines = Assert.Single(api.Messages).Text.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.EndsWith(" — ВЫСОКИЙ", lines[0]);
        Assert.Equal("Данные: срок 05.11.2026 02:00 (Asia/Kamchatka), статус В работе, приоритет Срочно, исполнитель Иван Петров", lines[3]);
        Assert.DoesNotContain('\u202E', string.Join('\n', lines));
    }

    [Theory]
    [InlineData("pp-max-notification:v1:unknown")]
    [InlineData("pp-max-notification:v999:taskAssigned")]
    [InlineData("pp-max-notification:v1:caseDocument")]
    public async Task SevenLineTaskNotification_UnresolvedOrSilentPayloadStillCancelsBeforeRendering(string payload)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task), CancellationToken.None);
        fixture.SetPayload(queued, payload);
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Null(queued.ProviderMessageId);
        Assert.Null(queued.SentAt);
    }

    [Fact]
    public async Task SevenLineTaskNotification_OpaqueContextKeepsNeutralDetailsAndTheExistingCrmTransition()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request() with
        {
            NotificationType = UserNotificationTypes.TaskChanged,
            Text = "PRIVATE task name and client details",
        }, CancellationToken.None);
        fixture.SetPayload(queued, "В CRM новое уведомление: Изменение задачи. Откройте CRM, чтобы безопасно посмотреть подробности.");
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var sent = Assert.Single(api.Messages);
        Assert.Equal(string.Join('\n',
            "[ИЗМЕНЕНИЕ ЗАДАЧИ] Изменение задачи",
            "Объект: задача в CRM",
            "Суть: Изменение задачи",
            "Данные: Срок и исполнитель не указаны",
            "Действие: Ознакомиться с уведомлением в CRM",
            "Контекст: https://crm.example.test/",
            "Кнопки: Открыть CRM"), sent.Text);
        Assert.Equal(7, sent.Text.Split('\n').Length);
        Assert.DoesNotContain("PRIVATE", sent.Text);
        Assert.DoesNotContain('№', sent.Text);
        Assert.Equal("https://crm.example.test/", sent.Url!.AbsoluteUri);
        Assert.Equal("Открыть CRM", queued.ActionText);
        Assert.Empty(await fixture.Db.TaskJournalItems.ToArrayAsync());
        Assert.Equal(MaxOutboxStatuses.Sent, queued.Status);
    }

    [Fact]
    public async Task SevenLineTaskNotification_ExcludesFinancialValuesAndArbitraryMetadataText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskChanged) with
        {
            EventKey = "task:seven-lines:private-metadata",
        }, CancellationToken.None);
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            Type = UserNotificationTypes.TaskChanged,
            EventKey = queued.EventKey,
            ObjectType = "task",
            ObjectId = task.Id,
            TaskJournalItemId = task.Id,
            MetadataJson = JsonSerializer.Serialize(new
            {
                changeLabels = new[]
                {
                    "Оплата исполнителю: нет -> да",
                    "Сумма оплаты исполнителю: 50 000 ₽ -> 100 000 ₽",
                    "Результат: PRIVATE free text",
                    "Статус: новая -> done\nPRIVATE injected value",
                    "Приоритет: PRIVATE arbitrary value",
                    "Произвольный текст PRIVATE",
                    "Срок оплаты исполнителю: 21.11.2026",
                    "Исполнитель: OLD_PERSON -> NEW_PERSON",
                    "Клиент/дело: SECRET owner",
                },
            }),
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var text = Assert.Single(api.Messages).Text;
        Assert.Equal(7, text.Split('\n').Length);
        Assert.Equal("Суть: Изменены поля: статус, приоритет, исполнитель", text.Split('\n')[2]);
        Assert.DoesNotContain("PRIVATE", text);
        Assert.DoesNotContain("50 000", text);
        Assert.DoesNotContain("100 000", text);
        Assert.DoesNotContain('₽', text);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("OLD_PERSON", text);
        Assert.DoesNotContain("NEW_PERSON", text);
    }

    [Fact]
    public async Task SevenLineTaskNotification_BoundsCombiningCharactersAndOversizedMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture, "e" + new string('\u0301', 2000));
        var queued = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskChanged) with
        {
            EventKey = "task:seven-lines:bounds",
        }, CancellationToken.None);
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            Type = UserNotificationTypes.TaskChanged,
            EventKey = queued.EventKey,
            ObjectType = "task",
            ObjectId = task.Id,
            TaskJournalItemId = task.Id,
            MetadataJson = JsonSerializer.Serialize(new { changeLabels = new[] { "Статус: " + new string('x', 20000) } }),
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        var text = Assert.Single(api.Messages).Text;
        var lines = text.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.Equal($"Объект: задача №{task.Id}, Без названия", lines[1]);
        Assert.Equal("Суть: Изменены данные задачи.", lines[2]);
        Assert.DoesNotContain('\u0301', text);
        Assert.True(text.Length < MaxApiClient.MaxTextLength);
        Assert.Equal(MaxOutboxStatuses.Sent, queued.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenLineTaskNotification_PreservesTheCompleteLongCanonicalUrl(bool unicodeHost)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var host = (unicodeHost ? "пример." : "") + new string('a', 60) + "." + new string('b', 60)
            + "." + new string('c', 40) + ".example.test";
        var frontend = new Uri($"https://{host}/");
        var taskUri = new Uri(frontend, $"/tasks/{task.Id}");
        Assert.True(taskUri.AbsoluteUri.Length > 160);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:FrontendUrl"] = frontend.AbsoluteUri,
        }).Build();
        await fixture.Outbox.QueueAsync(TaskRequest(fixture, task) with
        {
            ActionUrl = taskUri.AbsoluteUri,
        }, CancellationToken.None);
        var api = new RecordingApiClient();

        await fixture.Processor(api, configuration).ProcessBatchAsync(CancellationToken.None);

        var sent = Assert.Single(api.Messages);
        Assert.Equal(7, sent.Text.Split('\n').Length);
        Assert.Equal("Контекст: " + taskUri.AbsoluteUri, sent.Text.Split('\n')[5]);
        Assert.Equal(taskUri.AbsoluteUri, sent.Url!.AbsoluteUri);
        Assert.True(sent.Text.Length <= MaxApiClient.MaxTextLength);
        Assert.DoesNotContain('…', sent.Text.Split('\n')[5]);
    }

    [Theory]
    [InlineData(0, "Открыть CRM")]
    [InlineData(1, "Открыть CRM")]
    [InlineData(1, null)]
    [InlineData(1, " ")]
    public async Task SevenLineTaskNotification_UsesTheButtonWhenTheCompleteUrlExceedsTheMessageBudget(int extraCharacter, string? actionText)
    {
        var fixedLines = new[]
        {
            "[ЗАДАЧА ВЫПОЛНЕНА] Задача выполнена",
            "Объект: задача в CRM",
            "Суть: Задача выполнена",
            "Данные: Срок и исполнитель не указаны",
            "Действие: Ознакомиться с уведомлением в CRM",
            "Контекст: ",
            string.IsNullOrWhiteSpace(actionText) ? "Кнопки: отсутствуют" : "Кнопки: Открыть CRM",
        };
        const string baseUri = "https://crm.example.test/";
        var availableUrlLength = MaxApiClient.MaxTextLength - string.Join('\n', fixedLines).Length;
        var uri = new Uri(baseUri + new string('x', availableUrlLength - baseUri.Length + extraCharacter));

        var presentation = typeof(MaxOutboxProcessor).Assembly.GetType(
            "PayPlanner.Api.Services.Notifications.MaxTaskNotificationPresentation", throwOnError: true)!;
        var render = Assert.Single(presentation.GetMethods(BindingFlags.NonPublic | BindingFlags.Static),
            method => method.Name == "RenderNeutral");
        var text = Assert.IsType<string>(render.Invoke(null, [UserNotificationTypes.TaskCompleted, uri, actionText]));

        var lines = text.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.True(text.Length <= MaxApiClient.MaxTextLength);
        if (extraCharacter == 0)
        {
            Assert.Equal(MaxApiClient.MaxTextLength, text.Length);
            Assert.Equal("Контекст: " + uri.AbsoluteUri, lines[5]);
        }
        else
        {
            Assert.Equal(string.IsNullOrWhiteSpace(actionText)
                ? "Контекст: откройте уведомления в CRM"
                : "Контекст: полная ссылка доступна по кнопке «Открыть CRM»", lines[5]);
            Assert.DoesNotContain("https://", lines[5]);
            Assert.DoesNotContain('…', lines[5]);
        }
        var handler = new SevenLineMaxPayloadHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://platform-api2.max.ru/"),
        };
        var result = await new MaxApiClient(http).SendTextAsync(7001, text, "synthetic-token", actionText, uri);
        Assert.True(result.Success);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(text, payload.RootElement.GetProperty("text").GetString());
        if (string.IsNullOrWhiteSpace(actionText))
        {
            Assert.Equal("Кнопки: отсутствуют", lines[6]);
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("attachments").ValueKind);
        }
        else
        {
            var button = payload.RootElement.GetProperty("attachments")[0].GetProperty("payload").GetProperty("buttons")[0][0];
            Assert.Equal("Открыть CRM", button.GetProperty("text").GetString());
            Assert.Equal(uri.AbsoluteUri, button.GetProperty("url").GetString());
        }
    }

    private sealed class SevenLineMaxPayloadHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"body":{"mid":"synthetic-message"}}}"""),
            };
        }
    }
}
