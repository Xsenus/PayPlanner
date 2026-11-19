using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxServiceTests
{
    [Theory]
    [InlineData(63, false, "channel")]
    [InlineData(64, false, "type")]
    [InlineData(65, false, "channel")]
    [InlineData(240, false, "type")]
    [InlineData(65, true, "channel")]
    [InlineData(240, true, "type")]
    [InlineData(65, false, "role")]
    [InlineData(65, true, "role")]
    [InlineData(240, false, "allow")]
    [InlineData(240, true, "allow")]
    [InlineData(240, false, "personal-override")]
    public async Task EventKeyLengthDoesNotBypassCurrentPreferences(int length, bool legacy, string restriction)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request() with { EventKey = new string('я', length), NotificationType = UserNotificationTypes.TaskCompleted };
        var queued = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        AddSourceNotification(fixture, request.EventKey);
        if (legacy) fixture.SetPayload(queued, MaxMessageTemplateCatalog.RenderNotification(request.NotificationType!));
        await SetRestrictionAsync(fixture, restriction);
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        Assert.True(queued.EventKey.Length <= 64);
        Assert.Equal(queued.Id, (await fixture.Db.MaxOutboxMessages.SingleAsync()).Id);
        if (restriction is "allow" or "personal-override")
        {
            Assert.Equal(MaxOutboxStatuses.Sent, queued.Status);
            Assert.Equal(string.Join('\n',
                "[ЗАДАЧА ВЫПОЛНЕНА] Задача выполнена",
                "Объект: задача в CRM",
                "Суть: Задача выполнена",
                "Данные: Срок и исполнитель не указаны",
                "Действие: Ознакомиться с уведомлением в CRM",
                "Контекст: https://crm.example.test/opaque-link",
                "Кнопки: Открыть CRM"), Assert.Single(api.Messages).Text);
            Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        }
        else
        {
            Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
            Assert.Equal("recipient-preference-disabled", queued.LastErrorCode);
            Assert.Equal(0, queued.AttemptCount);
            Assert.Empty(api.Messages);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCrmRowDoesNotBypassLongKeyOptOut(bool legacy)
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request() with
        {
            EventKey = new string('x', 200),
            NotificationType = UserNotificationTypes.TaskCompleted,
        }, CancellationToken.None);
        if (legacy) fixture.SetPayload(queued, MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.TaskCompleted));
        await SetRestrictionAsync(fixture, "type");
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal("recipient-preference-disabled", queued.LastErrorCode);
        Assert.Empty(api.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongKeyRetryRechecksOptOutAfterProviderFailure(bool legacy)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request() with
        {
            EventKey = new string('x', 200),
            NotificationType = UserNotificationTypes.TaskCompleted,
        };
        var queued = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        if (legacy) fixture.SetPayload(queued, MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.TaskCompleted));
        await fixture.Db.SaveChangesAsync();
        await fixture.Processor(new RecordingApiClient(transientFailure: true)).ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(MaxOutboxStatuses.Failed, queued.Status);
        Assert.NotEqual(MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.TaskCompleted), fixture.ReadPayload(queued));
        Assert.Equal(queued.Id, (await fixture.Outbox.QueueAsync(request, CancellationToken.None)).Id);
        Assert.Equal(1, await fixture.Db.MaxOutboxMessages.CountAsync());
        await SetRestrictionAsync(fixture, "channel");
        Assert.True(await fixture.Outbox.RetryAsync(queued.PublicId, fixture.UserId, "Тест", CancellationToken.None));
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal("recipient-preference-disabled", queued.LastErrorCode);
        Assert.Equal(1, queued.AttemptCount);
        Assert.Empty(api.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredLongKeyRechecksOptOut(bool quietHours)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: quietHours, dailyLimit: 1);
        if (!quietHours)
        {
            await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
            await fixture.Processor(new RecordingApiClient()).ProcessBatchAsync(CancellationToken.None);
        }
        var queued = await fixture.Outbox.QueueAsync(fixture.Request() with
        {
            EventKey = new string('x', 200),
            NotificationType = UserNotificationTypes.TaskCompleted,
        }, CancellationToken.None);
        Assert.True(queued.NextAttemptAt > fixture.Now);
        await SetRestrictionAsync(fixture, "channel");
        fixture.MoveTimeTo(new DateTimeOffset(queued.NextAttemptAt));
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal(0, queued.AttemptCount);
        Assert.Empty(api.Messages);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("unknown")]
    [InlineData("silent")]
    [InlineData("version")]
    [InlineData("corrupt")]
    public async Task UnresolvedOrCorruptNotificationTypeNeverReachesProvider(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request() with { EventKey = new string('x', 200) }, CancellationToken.None);
        fixture.SetPayload(queued, kind switch
        {
            "unknown" => "pp-max-notification:v1:unknown",
            "silent" => "pp-max-notification:v1:" + UserNotificationTypes.CaseDocument,
            "version" => "pp-max-notification:v999:taskCompleted",
            _ => "Неизвестный старый текст, личные данные не должны уйти",
        });
        if (kind == "corrupt") queued.ProtectedText = "broken-protected-payload";
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Empty(api.Messages);
        Assert.Equal(kind == "corrupt" ? MaxOutboxStatuses.Failed : MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal(kind == "corrupt" ? "protected-payload-invalid" : "notification-type-unresolved", queued.LastErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingSourceTypeCannotReplaceProtectedType(bool legacy)
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request() with
        {
            EventKey = new string('x', 200),
            NotificationType = UserNotificationTypes.TaskCompleted,
        }, CancellationToken.None);
        if (legacy) fixture.SetPayload(queued, MaxMessageTemplateCatalog.RenderNotification(UserNotificationTypes.TaskCompleted));
        AddSourceNotification(fixture, queued.EventKey, UserNotificationTypes.TaskAssigned);
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Equal("notification-type-unresolved", queued.LastErrorCode);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task LegacyShortKeyUsesSourceTypeAfterTemplateLabelChanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request() with { NotificationType = UserNotificationTypes.TaskCompleted };
        var queued = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        fixture.SetPayload(queued, "Старое название безопасного уведомления");
        AddSourceNotification(fixture, request.EventKey);
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(MaxOutboxStatuses.Sent, queued.Status);
        var sent = Assert.Single(api.Messages);
        Assert.Equal(string.Join('\n',
            "[ЗАДАЧА ВЫПОЛНЕНА] Задача выполнена",
            "Объект: задача в CRM",
            "Суть: Задача выполнена",
            "Данные: Срок и исполнитель не указаны",
            "Действие: Ознакомиться с уведомлением в CRM",
            "Контекст: https://crm.example.test/opaque-link",
            "Кнопки: Открыть CRM"), sent.Text);
        Assert.Equal(request.ActionUrl, sent.Url?.AbsoluteUri);
    }

    [Fact]
    public async Task NotificationWithoutEmployeeIdentityIsCancelled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        queued.RecipientUserId = null;
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(MaxOutboxStatuses.Cancelled, queued.Status);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task IdenticalLongKeysDoNotShareRecipientsPreferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstUser = await fixture.Db.Users.SingleAsync();
        var other = new User
        {
            Email = "other-max@example.test",
            FullName = "Другой получатель",
            PasswordHash = "hash",
            RoleId = firstUser.RoleId,
            IsActive = true,
            IsApproved = true,
        };
        fixture.Db.MaxRecipientLinks.Add(new MaxRecipientLink
        {
            User = other,
            MaxUserId = 7002,
            Status = MaxRecipientLinkStatuses.Verified,
            VerifiedAt = fixture.Now,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
        });
        await fixture.Db.SaveChangesAsync();
        var request = fixture.Request() with { EventKey = new string('x', 200), NotificationType = UserNotificationTypes.TaskCompleted };
        var first = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        var second = await fixture.Outbox.QueueAsync(request with { RecipientUserId = other.Id }, CancellationToken.None);
        fixture.Db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>())),
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(first.EventKey, second.EventKey);
        Assert.Equal(MaxOutboxStatuses.Cancelled, first.Status);
        Assert.Equal(MaxOutboxStatuses.Sent, second.Status);
        Assert.Equal(7002, Assert.Single(api.Messages).UserId);
    }

    [Fact]
    public async Task DocumentTextIsNotInterpretedAsNotificationMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request() with { Scenario = MaxMessengerScenarios.Documents, Text = "pp-max-notification:v999:document-text" };
        await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        var api = new RecordingApiClient();

        await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(request.Text, Assert.Single(api.Messages).Text);
    }

    [Fact]
    public async Task EveryLegacyNeutralTemplateResolvesThroughTheRealWorker()
    {
        static string ExpectedText(UserNotificationTypeDefinition type)
        {
            if (type.Domain != UserNotificationDomains.Task)
            {
                return MaxMessageTemplateCatalog.RenderNotification(type.Key);
            }

            var prioritySuffix = type.Priority switch
            {
                NotificationPriorities.Critical => " — СРОЧНО",
                NotificationPriorities.High => " — ВЫСОКИЙ",
                _ => string.Empty,
            };
            var action = type.Key is UserNotificationTypes.TaskCancelled or UserNotificationTypes.TaskApproved
                ? "не требуется" : "Ознакомиться с уведомлением в CRM";
            return string.Join('\n',
                $"[{type.Label.ToUpperInvariant()}] {type.Label}{prioritySuffix}",
                "Объект: задача в CRM",
                $"Суть: {type.Label}",
                "Данные: Срок и исполнитель не указаны",
                $"Действие: {action}",
                "Контекст: уведомления CRM",
                "Кнопки: отсутствуют");
        }

        await using var fixture = await Fixture.CreateAsync();
        var roleId = (await fixture.Db.Users.SingleAsync()).RoleId;
        fixture.Db.SystemSettings.Add(new SystemSetting
        {
            Key = RoleNotificationPreferenceSettings.SettingKey,
            Value = RoleNotificationPreferenceSettings.Serialize(new Dictionary<int, UserNotificationPreferenceSnapshot>
            {
                [roleId] = UserNotificationPreferenceSettings.Empty,
            }),
        });
        var types = UserNotificationTypes.All.Where(type => !NotificationPriorities.IsSilent(type.Priority)).ToArray();
        var expectedTexts = types.Select(type => MaxMessageTemplateCatalog.RenderNotification(type.Key)).ToArray();
        Assert.Equal(types.Length, expectedTexts.Distinct(StringComparer.Ordinal).Count());
        var index = 0;
        foreach (var type in types)
        {
            var user = new User
            {
                Email = $"legacy-max-{index}@example.test",
                FullName = "Тест старого шаблона",
                PasswordHash = "hash",
                RoleId = roleId,
                IsActive = true,
                IsApproved = true,
            };
            var message = new MaxOutboxMessage
            {
                RecipientUser = user,
                RecipientLink = new MaxRecipientLink
                {
                    User = user,
                    MaxUserId = 7100 + index++,
                    Status = MaxRecipientLinkStatuses.Verified,
                    VerifiedAt = fixture.Now,
                    LinkTokenHash = Guid.NewGuid().ToString("N"),
                },
                EventKey = "sha256:" + new string('f', 25) + Guid.NewGuid().ToString("N"),
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Scenario = MaxMessengerScenarios.Notifications,
                Status = MaxOutboxStatuses.Queued,
                NextAttemptAt = fixture.Now,
            };
            fixture.SetPayload(message, MaxMessageTemplateCatalog.RenderNotification(type.Key));
            fixture.Db.MaxOutboxMessages.Add(message);
        }
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();
        for (var batch = 0; batch < (types.Length + 19) / 20; batch++)
            await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None);

        var guardedTypes = new[] { UserNotificationTypes.CaseCourtEvent, UserNotificationTypes.CourtHearing, UserNotificationTypes.CourtStatusChanged,
            UserNotificationTypes.CourtDecisionRecorded, UserNotificationTypes.CourtDecisionPublished, UserNotificationTypes.CaseProceduralDeadline };
        var guardedCount = types.Count(type => guardedTypes.Contains(type.Key));
        Assert.Equal(types.Length, await fixture.Db.MaxOutboxMessages.CountAsync(item => item.Status == MaxOutboxStatuses.Sent
            || item.Status == MaxOutboxStatuses.Cancelled && item.LastErrorCode == "court-source-unresolved"));
        Assert.Equal(guardedCount, await fixture.Db.MaxOutboxMessages.CountAsync(item => item.Status == MaxOutboxStatuses.Cancelled));
        Assert.Equal(types.Length - guardedCount, await fixture.Db.MaxOutboxMessages.CountAsync(item => item.Status == MaxOutboxStatuses.Sent));
        Assert.Equal(types.Where(type => !guardedTypes.Contains(type.Key)).Select(ExpectedText)
            .OrderBy(text => text, StringComparer.Ordinal), api.Messages.Select(message => message.Text).OrderBy(text => text, StringComparer.Ordinal));
        Assert.All(api.Messages, message => Assert.Null(message.Url));
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
    }

    private static void AddSourceNotification(Fixture fixture, string eventKey, string type = UserNotificationTypes.TaskCompleted)
        => fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            EventKey = eventKey,
            Type = type,
            Title = "Синтетическое уведомление",
            ObjectType = "system",
        });

    private static async Task SetRestrictionAsync(Fixture fixture, string restriction)
    {
        var preferences = new UserNotificationPreferenceSnapshot(
            restriction == "type" ? [UserNotificationTypes.TaskCompleted] : [],
            restriction == "type"
                ? new Dictionary<string, IReadOnlyList<string>>()
                : new Dictionary<string, IReadOnlyList<string>> { [UserNotificationTypes.TaskCompleted] = [NotificationChannels.Max] });
        if (restriction is "role" or "personal-override")
        {
            var roleId = (await fixture.Db.Users.SingleAsync()).RoleId;
            fixture.Db.SystemSettings.Add(new SystemSetting
            {
                Key = RoleNotificationPreferenceSettings.SettingKey,
                Value = RoleNotificationPreferenceSettings.Serialize(new Dictionary<int, UserNotificationPreferenceSnapshot> { [roleId] = preferences }),
            });
        }
        if (restriction is "channel" or "type" or "personal-override")
        {
            fixture.Db.UserSettings.Add(new UserSetting
            {
                UserId = fixture.UserId,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(restriction == "personal-override" ? UserNotificationPreferenceSettings.Empty : preferences),
            });
        }
        await fixture.Db.SaveChangesAsync();
    }
}
