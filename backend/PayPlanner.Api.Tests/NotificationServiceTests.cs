using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class NotificationServiceTests
{
    [Fact]
    public async Task GetChannelsAsync_ReturnsActiveChannelsAndFutureAdapters()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var channels = await service.GetChannelsAsync();

        Assert.Contains(channels, item => item.Key == NotificationChannels.InApp && item.IsEnabled && item.IsSendable && !item.IsFuture);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Toast && item.IsEnabled && !item.IsSendable && item.SupportsUserPreferences && !item.IsFuture);
        Assert.Contains(channels, item => item.Key == NotificationChannels.WebPush && item.IsEnabled && item.IsSendable && !item.RequiresProvider && !item.IsFuture);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Email && item.IsEnabled && item.IsSendable && item.RequiresProvider);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Telegram && item.IsEnabled && item.IsSendable && item.RequiresProvider);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Sms && !item.IsEnabled && !item.IsSendable && item.IsFuture);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Max && !item.IsEnabled && !item.IsSendable && item.IsFuture);
        Assert.Equal(new[] { NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.WebPush, NotificationChannels.Email, NotificationChannels.Telegram }, NotificationChannels.All);
        Assert.Equal(new[] { NotificationChannels.Sms, NotificationChannels.Max }, NotificationChannels.Future);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsMaxDiagnosticsWithoutEnablingFutureChannel()
    {
        await using var db = await CreateContextAsync();
        db.NotificationDeliveries.AddRange(
            new NotificationDelivery
            {
                EventKey = "max:test:sent",
                Channel = NotificationChannels.Max,
                SenderName = "MAX",
                RecipientAddress = "max-user",
                Subject = "MAX тест",
                BodyPreview = "Успешная доставка MAX",
                Status = NotificationDeliveryStatus.Sent,
                CreatedAt = new DateTime(2026, 7, 8, 8, 0, 0, DateTimeKind.Utc),
                SentAt = new DateTime(2026, 7, 8, 8, 1, 0, DateTimeKind.Utc)
            },
            new NotificationDelivery
            {
                EventKey = "max:test:failed",
                Channel = NotificationChannels.Max,
                SenderName = "MAX",
                RecipientAddress = "max-user",
                Subject = "MAX тест",
                BodyPreview = "Ошибка доставки MAX",
                Status = NotificationDeliveryStatus.Failed,
                Error = "MAX API не настроен",
                CreatedAt = new DateTime(2026, 7, 8, 9, 0, 0, DateTimeKind.Utc)
            });
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var settings = await service.GetSettingsAsync();

        Assert.False(settings.Max.Enabled);
        Assert.False(settings.Max.Ready);
        Assert.False(settings.Max.ApiConfirmed);
        Assert.False(settings.Max.BotConfigured);
        Assert.False(settings.Max.ScenariosConfirmed);
        Assert.Empty(settings.Max.AllowedEventTypes);
        Assert.Equal(new DateTime(2026, 7, 8, 8, 1, 0, DateTimeKind.Utc), settings.Max.LastSuccessfulDeliveryAt);
        Assert.Equal(new DateTime(2026, 7, 8, 9, 0, 0, DateTimeKind.Utc), settings.Max.LastFailedDeliveryAt);
        Assert.Equal("MAX API не настроен", settings.Max.LastDeliveryError);
        Assert.Contains("не подтверждены", settings.Max.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmsChannel_IsSkippedAndLoggedWhenProviderIsDisabled()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "sms-recipient@example.com", phoneNumber: "+7 (999) 111-22-33");
        var emailService = new RecordingEmailService();
        var telegramService = new RecordingTelegramService();
        var smsService = new DisabledSmsNotificationService();
        var service = CreateService(db, emailService, telegramService, smsNotificationService: smsService);

        var settings = await service.GetSettingsAsync();
        var deliveries = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("SMS тест", "Проверка SMS-канала"),
            [NotificationChannels.Sms],
            "sms:test:disabled");

        Assert.False(settings.Sms.Enabled);
        Assert.False(settings.Sms.Ready);
        Assert.Contains("SMS-уведомления отключены", settings.Sms.StatusText, StringComparison.Ordinal);
        var delivery = Assert.Single(deliveries);
        Assert.Equal(NotificationChannels.Sms, delivery.Channel);
        Assert.Equal(NotificationDeliveryStatus.Skipped, delivery.Status);
        Assert.Equal("+79991112233", delivery.RecipientAddress);
        Assert.Contains("SMS-уведомления отключены", delivery.Error, StringComparison.Ordinal);
        Assert.Empty(emailService.Messages);
        Assert.Empty(telegramService.Messages);

        var savedDelivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(delivery.Id, savedDelivery.Id);
        Assert.Equal(NotificationDeliveryStatus.Skipped, savedDelivery.Status);
    }

    [Fact]
    public async Task SmsSettings_AreSavedAndKeepDeliveryDisabledUntilProviderAdapterExists()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "sms-configured@example.com", phoneNumber: "+7 (999) 222-33-44");
        var emailService = new RecordingEmailService();
        var telegramService = new RecordingTelegramService();
        var smsService = new SystemSmsNotificationService(db);
        var service = CreateService(db, emailService, telegramService, smsNotificationService: smsService);

        var settings = await service.UpdateSmsSettingsAsync(new UpdateSmsNotificationSettingsRequest
        {
            Enabled = true,
            ProviderKey = "sms-ru",
            SenderName = "PayPlanner",
            ApiToken = "sms-secret-token",
            KeepExistingApiToken = false,
            DailyLimit = 250,
            MaxMessageLength = 320,
            Notes = "Тестовые настройки SMS"
        });

        Assert.True(settings.Sms.Enabled);
        Assert.True(settings.Sms.ProviderConfigured);
        Assert.True(settings.Sms.ApiTokenSet);
        Assert.False(settings.Sms.AdapterConfigured);
        Assert.False(settings.Sms.Ready);
        Assert.Equal("sms-ru", settings.Sms.ProviderKey);
        Assert.Equal("PayPlanner", settings.Sms.SenderName);
        Assert.Equal(250, settings.Sms.DailyLimit);
        Assert.Equal(320, settings.Sms.MaxMessageLength);
        Assert.Contains("адаптер", settings.Sms.StatusText, StringComparison.OrdinalIgnoreCase);

        var updated = await service.UpdateSmsSettingsAsync(new UpdateSmsNotificationSettingsRequest
        {
            Enabled = true,
            ProviderKey = "custom",
            SenderName = "PayPlanner CRM",
            ApiToken = string.Empty,
            KeepExistingApiToken = true,
            DailyLimit = 10,
            MaxMessageLength = 120
        });

        Assert.True(updated.Sms.ApiTokenSet);
        Assert.Equal("custom", updated.Sms.ProviderKey);
        Assert.Equal("PayPlanner CRM", updated.Sms.SenderName);

        var deliveries = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("SMS тест", "Проверка сохраненных SMS-настроек"),
            [NotificationChannels.Sms],
            "sms:test:configured");

        var delivery = Assert.Single(deliveries);
        Assert.Equal(NotificationChannels.Sms, delivery.Channel);
        Assert.Equal(NotificationDeliveryStatus.Skipped, delivery.Status);
        Assert.Equal("+79992223344", delivery.RecipientAddress);
        Assert.Contains("адаптер", delivery.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmsSettings_NormalizeInvalidValuesAndSkipDeliveryWhenNotReady()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "sms-invalid@example.com", phoneNumber: "+7 (999) 333-44-55");
        var smsService = new SystemSmsNotificationService(db);
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            smsNotificationService: smsService);

        var settings = await service.UpdateSmsSettingsAsync(new UpdateSmsNotificationSettingsRequest
        {
            Enabled = true,
            ProviderKey = "   ",
            SenderName = "   ",
            ApiToken = "   ",
            KeepExistingApiToken = false,
            DailyLimit = -15,
            MaxMessageLength = 10,
            Notes = new string('x', 600)
        });

        Assert.True(settings.Sms.Enabled);
        Assert.False(settings.Sms.Ready);
        Assert.False(settings.Sms.ProviderConfigured);
        Assert.False(settings.Sms.ApiTokenSet);
        Assert.Equal(1, settings.Sms.DailyLimit);
        Assert.Equal(70, settings.Sms.MaxMessageLength);
        Assert.Equal(500, settings.Sms.Notes.Length);
        Assert.Contains("провайдер", settings.Sms.StatusText, StringComparison.OrdinalIgnoreCase);

        var deliveries = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("SMS тест", "Проверка невалидных настроек SMS"),
            [NotificationChannels.Sms],
            "sms:test:invalid-settings");

        var delivery = Assert.Single(deliveries);
        Assert.Equal(NotificationChannels.Sms, delivery.Channel);
        Assert.Equal(NotificationDeliveryStatus.Skipped, delivery.Status);
        Assert.Equal("+79993334455", delivery.RecipientAddress);
        Assert.Equal(settings.Sms.StatusText, delivery.Error);

        var savedDelivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(delivery.Id, savedDelivery.Id);
        Assert.Equal(NotificationDeliveryStatus.Skipped, savedDelivery.Status);
        Assert.Equal(settings.Sms.StatusText, savedDelivery.Error);
    }

    [Fact]
    public async Task SmsDelivery_UsesReadyProviderAndPersistsSentLog()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "sms-sent@example.com", phoneNumber: "+7 (999) 444-55-66");
        var smsService = new RecordingSmsNotificationService
        {
            NextResult = new SmsSendResult(true, NotificationDeliveryStatus.Sent)
        };
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            smsNotificationService: smsService);

        var deliveries = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("SMS тест", "Сообщение для успешной SMS-доставки"),
            [NotificationChannels.Sms],
            "sms:test:sent");

        var delivery = Assert.Single(deliveries);
        Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
        Assert.Null(delivery.Error);
        Assert.NotNull(delivery.SentAt);
        Assert.Equal("+79994445566", delivery.RecipientAddress);
        Assert.Equal("Тестовый SMS", delivery.SenderName);

        var message = Assert.Single(smsService.Messages);
        Assert.Equal("+79994445566", message.PhoneNumber);
        Assert.Equal("Сообщение для успешной SMS-доставки", message.Text);

        var savedDelivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(delivery.Id, savedDelivery.Id);
        Assert.Equal(NotificationDeliveryStatus.Sent, savedDelivery.Status);
        Assert.Equal("Сообщение для успешной SMS-доставки", savedDelivery.BodyPreview);
        Assert.NotNull(savedDelivery.SentAt);
    }

    [Fact]
    public async Task SmsDelivery_PersistsProviderErrorInDeliveryLog()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "sms-failed@example.com", phoneNumber: "+7 (999) 555-66-77");
        var smsService = new RecordingSmsNotificationService
        {
            NextResult = new SmsSendResult(false, NotificationDeliveryStatus.Failed, "Provider rejected phone")
        };
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            smsNotificationService: smsService);

        var deliveries = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("SMS тест", "Сообщение для ошибки SMS-провайдера"),
            [NotificationChannels.Sms],
            "sms:test:failed");

        var delivery = Assert.Single(deliveries);
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal("Provider rejected phone", delivery.Error);
        Assert.Null(delivery.SentAt);
        Assert.Equal("+79995556677", delivery.RecipientAddress);

        var message = Assert.Single(smsService.Messages);
        Assert.Equal("+79995556677", message.PhoneNumber);
        Assert.Equal("Сообщение для ошибки SMS-провайдера", message.Text);

        var savedDelivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(delivery.Id, savedDelivery.Id);
        Assert.Equal(NotificationDeliveryStatus.Failed, savedDelivery.Status);
        Assert.Equal("Provider rejected phone", savedDelivery.Error);
        Assert.Null(savedDelivery.SentAt);
    }

    [Fact]
    public void NotificationEventEnvelope_UsesCatalogDefaultsAndExplicitOverrides()
    {
        var envelope = new NotificationEventEnvelope(
            RecipientUserId: 10,
            Type: UserNotificationTypes.PaymentAdvanceReviewRequired,
            EventKey: "payment:advance:42",
            Title: "Контроль аванса",
            BodyPreview: "Есть нераспределенный аванс.",
            ObjectType: "payment",
            ObjectId: 42,
            TaskJournalItemId: null,
            Tone: null,
            Metadata: new { paymentId = 42 },
            Channels: [NotificationChannels.InApp, NotificationChannels.WebPush],
            ActionUrl: "/payments/42");

        Assert.Equal(UserNotificationTypes.PaymentAdvanceReviewRequired, envelope.EffectiveType);
        Assert.Equal(UserNotificationDomains.Payment, envelope.Domain);
        Assert.Equal(NotificationPriorities.Critical, envelope.Priority);
        Assert.Equal("rose", envelope.EffectiveTone);
        Assert.Equal([NotificationChannels.InApp, NotificationChannels.WebPush], envelope.EffectiveChannels);
        Assert.Equal("/payments/42", envelope.ActionUrl);
    }

    [Fact]
    public void UserNotificationAntiSpamPolicy_ResolvesSelfDuplicateAndGroupingRules()
    {
        var envelope = new NotificationEventEnvelope(
            RecipientUserId: 15,
            Type: UserNotificationTypes.TaskChanged,
            EventKey: "task:77:changed",
            Title: "Задача изменена",
            BodyPreview: "Проверить изменения.",
            ObjectType: "task",
            ObjectId: 77,
            TaskJournalItemId: 77);
        var duplicate = new UserNotification
        {
            RecipientUserId = 15,
            EventKey = "task:77:changed",
        };
        var otherRecipient = new UserNotification
        {
            RecipientUserId = 16,
            EventKey = "task:77:changed",
        };

        Assert.True(UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(15, 15));
        Assert.False(UserNotificationAntiSpamPolicy.ShouldSuppressSelfNotification(15, null));
        Assert.True(UserNotificationAntiSpamPolicy.IsExactDuplicate(duplicate, envelope));
        Assert.False(UserNotificationAntiSpamPolicy.IsExactDuplicate(otherRecipient, envelope));
        Assert.True(UserNotificationAntiSpamPolicy.ExactDuplicatePredicate(envelope).Compile().Invoke(duplicate));
        Assert.Equal(300, UserNotificationAntiSpamPolicy.ResolveGroupingWindowSeconds(UserNotificationTypes.TaskChanged));
        Assert.Equal(0, UserNotificationAntiSpamPolicy.ResolveGroupingWindowSeconds("unknown-type"));
    }

    [Fact]
    public void UserNotificationTypes_AllHaveInventoryMetadata()
    {
        Assert.Equal(UserNotificationTypes.All.Count, UserNotificationTypes.All.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var type in UserNotificationTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(type.Key));
            Assert.False(string.IsNullOrWhiteSpace(type.Label));
            Assert.False(string.IsNullOrWhiteSpace(type.Description));
            Assert.False(string.IsNullOrWhiteSpace(type.Domain));
            Assert.Contains(type.Priority, NotificationPriorities.All);
            Assert.NotEmpty(type.EffectiveChannels);
            Assert.All(type.EffectiveChannels, channel => Assert.Contains(channel, NotificationChannels.All));
            Assert.NotEmpty(type.PreferenceChannels);
            Assert.All(type.PreferenceChannels, channel => Assert.Contains(channel, NotificationChannels.UserPreferences));
            Assert.All(type.EffectiveChannels, channel => Assert.Contains(channel, type.PreferenceChannels));
            Assert.Contains(NotificationChannels.InApp, type.EffectiveChannels);
            Assert.True(type.GroupingWindowSeconds >= 0);
            Assert.Same(type, UserNotificationTypes.Find(type.Key));
            Assert.True(UserNotificationTypes.IsKnown(type.Key));
        }
    }

    [Fact]
    public void UserNotificationTypes_ExposeSmsOnlyAsPreferenceChannelForUrgentSupportedTypes()
    {
        var taskReminder = UserNotificationTypes.Find(UserNotificationTypes.TaskReminder)!;
        var courtHearing = UserNotificationTypes.Find(UserNotificationTypes.CourtHearing)!;
        var caseDeadline = UserNotificationTypes.Find(UserNotificationTypes.CaseProceduralDeadline)!;
        var taskApproval = UserNotificationTypes.Find(UserNotificationTypes.TaskApprovalRequested)!;
        var taskCompleted = UserNotificationTypes.Find(UserNotificationTypes.TaskCompleted)!;
        var paymentAdvance = UserNotificationTypes.Find(UserNotificationTypes.PaymentAdvanceReviewRequired)!;

        Assert.DoesNotContain(NotificationChannels.Sms, NotificationChannels.All);
        Assert.Contains(NotificationChannels.Sms, NotificationChannels.UserPreferences);
        Assert.Contains(NotificationChannels.Sms, taskReminder.PreferenceChannels);
        Assert.Contains(NotificationChannels.Sms, courtHearing.PreferenceChannels);
        Assert.Contains(NotificationChannels.Sms, caseDeadline.PreferenceChannels);
        Assert.Contains(NotificationChannels.Sms, taskApproval.PreferenceChannels);
        Assert.DoesNotContain(NotificationChannels.Sms, taskReminder.EffectiveChannels);
        Assert.DoesNotContain(NotificationChannels.Sms, taskCompleted.PreferenceChannels);
        Assert.DoesNotContain(NotificationChannels.Sms, paymentAdvance.PreferenceChannels);
    }

    [Fact]
    public async Task SmsNotificationTemplates_AreAvailableCompactAndEditable()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        var expectedKeys = new[]
        {
            NotificationTemplateKeys.SmsTaskReminder,
            NotificationTemplateKeys.SmsTaskApprovalRequested,
            NotificationTemplateKeys.SmsCourtEvent,
            NotificationTemplateKeys.SmsCaseProceduralDeadline,
            NotificationTemplateKeys.SmsWorkOrderApproval
        };

        var templates = await service.GetTemplatesAsync();
        var smsTemplates = templates
            .Where(template => expectedKeys.Contains(template.Key, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(
            expectedKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase),
            smsTemplates.Select(template => template.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        Assert.All(smsTemplates, template =>
        {
            Assert.StartsWith("SMS:", template.Label, StringComparison.Ordinal);
            Assert.True(template.DefaultBody.Length <= 240, $"{template.Key} default SMS body is too long.");
            Assert.DoesNotContain("\n", template.DefaultBody, StringComparison.Ordinal);
            Assert.NotEmpty(template.Variables);
        });

        var updated = await service.UpdateTemplateAsync(
            NotificationTemplateKeys.SmsCaseProceduralDeadline,
            new UpdateNotificationTemplateRequest
            {
                IsEnabled = true,
                Subject = "SMS: срок {{case.title}}",
                Body = "SMS {{case.title}} {{deadline.dueAt}}"
            });

        Assert.Equal("SMS: срок {{case.title}}", updated.Subject);
        Assert.Equal("SMS {{case.title}} {{deadline.dueAt}}", updated.Body);

        var rendered = await service.RenderTemplateAsync(
            NotificationTemplateKeys.SmsCaseProceduralDeadline,
            "Резервная тема",
            "Резервный текст",
            new Dictionary<string, string?>
            {
                ["case.title"] = "А40-123",
                ["deadline.dueAt"] = "10.07.2026"
            });

        Assert.Equal("SMS: срок А40-123", rendered.Subject);
        Assert.Equal("SMS А40-123 10.07.2026", rendered.Body);

        var reset = await service.ResetTemplateAsync(NotificationTemplateKeys.SmsCaseProceduralDeadline);
        Assert.Equal(reset.DefaultSubject, reset.Subject);
        Assert.Equal(reset.DefaultBody, reset.Body);
    }

    [Fact]
    public void TaskNotificationTypes_HaveExpectedRoutingMetadata()
    {
        var expected = new Dictionary<string, (string Priority, bool RequiresAction, bool ShowsToast, bool PushEligible)>(StringComparer.OrdinalIgnoreCase)
        {
            [UserNotificationTypes.TaskAssigned] = (NotificationPriorities.High, true, true, true),
            [UserNotificationTypes.TaskReminder] = (NotificationPriorities.Critical, true, true, true),
            [UserNotificationTypes.TaskChanged] = (NotificationPriorities.Normal, false, true, true),
            [UserNotificationTypes.TaskComment] = (NotificationPriorities.Normal, false, true, true),
            [UserNotificationTypes.TaskCompleted] = (NotificationPriorities.Silent, false, false, false),
            [UserNotificationTypes.TaskCancelled] = (NotificationPriorities.High, true, true, true),
            [UserNotificationTypes.TaskScheduleConflict] = (NotificationPriorities.High, true, true, true),
            [UserNotificationTypes.TaskApprovalRequested] = (NotificationPriorities.High, true, true, true),
            [UserNotificationTypes.TaskApproved] = (NotificationPriorities.Normal, true, true, true),
            [UserNotificationTypes.TaskRevisionRequested] = (NotificationPriorities.High, true, true, true),
            [UserNotificationTypes.TaskDeadlineChangeRequested] = (NotificationPriorities.High, true, true, true),
            [UserNotificationTypes.TaskDeadlineChangeApproved] = (NotificationPriorities.Normal, false, true, true),
            [UserNotificationTypes.TaskDeadlineChangeRejected] = (NotificationPriorities.High, false, true, true),
            [UserNotificationTypes.TaskApprovalOverdue] = (NotificationPriorities.Critical, true, true, true),
        };
        var actual = UserNotificationTypes.All
            .Where(item => string.Equals(item.Domain, UserNotificationDomains.Task, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            expected.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            actual.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var (key, metadata) in expected)
        {
            var type = actual[key];

            Assert.Equal(UserNotificationDomains.Task, type.Domain);
            Assert.Equal(metadata.Priority, type.Priority);
            Assert.Equal(metadata.RequiresAction, type.RequiresAction);
            Assert.Equal(metadata.ShowsToast, type.ShowToastByDefault);
            Assert.Equal(metadata.PushEligible, type.PushEligible);
            Assert.Contains(NotificationChannels.InApp, type.EffectiveChannels);

            if (NotificationPriorities.IsSilent(metadata.Priority))
            {
                Assert.Equal(new[] { NotificationChannels.InApp }, type.EffectiveChannels);
            }
            else
            {
                Assert.Contains(NotificationChannels.Toast, type.EffectiveChannels);
                Assert.Contains(NotificationChannels.WebPush, type.EffectiveChannels);
            }
        }
    }

    [Fact]
    public async Task NotifyAsync_CreatesUserNotificationAndPublishesRealtimeAndWebPush()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "generic-notification@example.com");
        var paymentId = await CreateProtectedNotificationObjectAsync(db, "payment", user);
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        var created = await service.NotifyAsync(new NotificationEventEnvelope(
            RecipientUserId: user.Id,
            Type: UserNotificationTypes.PaymentAdvanceReviewRequired,
            EventKey: "payment:advance:generic:42",
            Title: "Платеж требует решения",
            BodyPreview: "Проверьте аванс.",
            ObjectType: "payment",
            ObjectId: paymentId,
            TaskJournalItemId: null,
            Metadata: new { paymentId }));

        Assert.True(created);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.PaymentAdvanceReviewRequired, notification.Type);
        Assert.Equal("payment", notification.ObjectType);
        Assert.Equal(paymentId, notification.ObjectId);
        Assert.Single(realtime.Notifications);
        Assert.Single(webPush.Notifications);
    }

    [Fact]
    public async Task ProductionAcceptanceCriticalEvents_CreateRealtimeAndWebPushNotifications()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "acceptance-actor@example.com");
        var recipient = new User
        {
            Email = "acceptance-recipient@example.com",
            FullName = "Получатель приемочных уведомлений",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        var task = new TaskJournalItem
        {
            ObjectName = "Приемочное дело",
            Title = "Проверить новую задачу",
            Description = "Проверка моментальной связи по задачам",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipient.Id,
            AssigneeName = recipient.FullName,
            DueAt = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        await service.NotifyTaskAssignedAsync(task, actor.FullName, actor.Id);

        var changedDueAt = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        task.DueAt = changedDueAt;
        await db.SaveChangesAsync();
        await service.NotifyTaskChangedAsync(
            task,
            ["Срок: 18.06.2026 10:00 -> 18.06.2026 12:00"],
            actor.FullName,
            actor.Id);

        var note = await CreateStickyNoteAsync(db, actor, StickyNoteBoardVisibility.Shared);
        await service.NotifyStickyNoteMentionAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "@acceptance-recipient проверь заметку",
            actor.FullName,
            actor.Id,
            [recipient.Id]);
        await service.NotifyStickyNoteAssignedAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "Заметка назначена ответственному",
            recipient.Id,
            actor.FullName,
            actor.Id,
            new DateTime(2026, 6, 18, 13, 0, 0, DateTimeKind.Utc));

        var payment = new Payment
        {
            Description = "Платеж на контроле",
            Date = new DateTime(2026, 6, 18),
            Amount = 35000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Responsibles = { new PaymentResponsible { UserId = recipient.Id } }
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var paymentNotified = await service.NotifyAsync(new NotificationEventEnvelope(
            RecipientUserId: recipient.Id,
            Type: UserNotificationTypes.PaymentAdvanceReviewRequired,
            EventKey: $"payment:acceptance:advance-review:{payment.Id}",
            Title: "Платеж требует контроля",
            BodyPreview: "Проверьте платеж на контроле.",
            ObjectType: "payment",
            ObjectId: payment.Id,
            TaskJournalItemId: null,
            Metadata: new { paymentId = payment.Id },
            ActionUrl: $"/payments/{payment.Id}"));

        Assert.True(paymentNotified);

        var notifications = await db.UserNotifications
            .OrderBy(item => item.Id)
            .ToListAsync();
        var expectedTypes = new[]
        {
            UserNotificationTypes.TaskAssigned,
            UserNotificationTypes.TaskChanged,
            UserNotificationTypes.NoteMention,
            UserNotificationTypes.NoteAssigned,
            UserNotificationTypes.PaymentAdvanceReviewRequired
        };

        Assert.Equal(expectedTypes, notifications.Select(item => item.Type).ToArray());
        Assert.All(notifications, notification =>
        {
            Assert.Equal(recipient.Id, notification.RecipientUserId);
            Assert.False(notification.IsRead);
            Assert.False(notification.IsArchived);
        });
        Assert.Equal(expectedTypes, realtime.Notifications.Select(item => item.Type).ToArray());
        Assert.Equal(expectedTypes, webPush.Notifications.Select(item => item.Type).ToArray());
        Assert.All(realtime.ToastEnabledValues, Assert.True);
        Assert.Contains(notifications, item =>
            item.Type == UserNotificationTypes.TaskChanged
            && item.TaskJournalItemId == task.Id
            && item.Title.Contains("срок", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notifications, item =>
            item.Type == UserNotificationTypes.PaymentAdvanceReviewRequired
            && item.ObjectType == "payment"
            && item.ObjectId == payment.Id
            && item.MetadataJson.Contains($"\"paymentId\":{payment.Id}", StringComparison.Ordinal));
        Assert.DoesNotContain(notifications, item => item.RecipientUserId == actor.Id);
    }

    [Theory]
    [InlineData("payment", RolePermissionCatalog.Payments, UserNotificationTypes.PaymentAssigned)]
    [InlineData("invoice", RolePermissionCatalog.Accounts, UserNotificationTypes.InvoiceCreated)]
    [InlineData("act", RolePermissionCatalog.Acts, UserNotificationTypes.ActCreated)]
    [InlineData("contract", RolePermissionCatalog.Contracts, UserNotificationTypes.ContractCreated)]
    [InlineData("case", RolePermissionCatalog.Cases, UserNotificationTypes.CaseCourtEvent)]
    [InlineData("task", RolePermissionCatalog.Tasks, UserNotificationTypes.TaskAssigned)]
    public async Task NotifyAsync_SkipsProtectedObjectNotification_WhenRecipientHasNoSectionAccess(
        string objectType,
        string section,
        string notificationType)
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, $"{objectType}-notification-owner@example.com");
        var recipient = await CreateUserWithoutSectionAccessAsync(db, $"{objectType}-notification-blocked@example.com", section);
        var objectId = await CreateProtectedNotificationObjectAsync(db, objectType, owner);
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        var created = await service.NotifyAsync(new NotificationEventEnvelope(
            RecipientUserId: recipient.Id,
            Type: notificationType,
            EventKey: $"{objectType}:{objectId}:blocked-access",
            Title: "Закрытый объект",
            BodyPreview: "Получатель не должен видеть это уведомление.",
            ObjectType: objectType,
            ObjectId: objectId,
            TaskJournalItemId: null));

        Assert.False(created);
        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(realtime.Notifications);
        Assert.Empty(webPush.Notifications);
    }

    [Fact]
    public async Task NotifyAsync_SkipsPersonalNoteNotification_WhenRecipientCannotOpenBoard()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "personal-note-owner@example.com");
        var recipient = new User
        {
            Email = "personal-note-recipient@example.com",
            FullName = "Получатель личной заметки",
            PasswordHash = "hash",
            RoleId = owner.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var note = await CreateStickyNoteAsync(db, owner, StickyNoteBoardVisibility.Personal);
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        var created = await service.NotifyAsync(new NotificationEventEnvelope(
            RecipientUserId: recipient.Id,
            Type: UserNotificationTypes.NoteReminder,
            EventKey: $"note:{note.Id}:personal-blocked",
            Title: "Личная заметка",
            BodyPreview: "Эту заметку нельзя раскрывать.",
            ObjectType: "note",
            ObjectId: note.Id,
            TaskJournalItemId: null));

        Assert.False(created);
        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(realtime.Notifications);
        Assert.Empty(webPush.Notifications);
    }

    [Fact]
    public async Task NotifyAsync_AllowsSharedNoteNotification_WhenRecipientCanOpenBoard()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "shared-note-owner@example.com");
        var recipient = new User
        {
            Email = "shared-note-recipient@example.com",
            FullName = "Получатель общей заметки",
            PasswordHash = "hash",
            RoleId = owner.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var note = await CreateStickyNoteAsync(db, owner, StickyNoteBoardVisibility.Shared);
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        var created = await service.NotifyAsync(new NotificationEventEnvelope(
            RecipientUserId: recipient.Id,
            Type: UserNotificationTypes.NoteReminder,
            EventKey: $"note:{note.Id}:shared-visible",
            Title: "Общая заметка",
            BodyPreview: "Эту заметку можно показать.",
            ObjectType: "note",
            ObjectId: note.Id,
            TaskJournalItemId: null));

        Assert.True(created);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(note.Id, notification.ObjectId);
        Assert.Single(realtime.Notifications);
        Assert.Single(webPush.Notifications);
    }

    [Fact]
    public async Task NotifyPasswordIssuedAsync_SendsEmailAndTelegram_AndHidesPasswordInDeliveryLog()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var user = await CreateUserAsync(db, "employee@example.com", chatId: 100500);
        var emailService = new RecordingEmailService();
        var telegramService = new RecordingTelegramService();
        var service = CreateService(db, emailService, telegramService);

        var deliveries = await service.NotifyPasswordIssuedAsync(
            user,
            "Secret-Password-123",
            [NotificationChannels.Email, NotificationChannels.Telegram]);

        Assert.Equal(2, deliveries.Count);
        Assert.Single(emailService.Messages);
        Assert.Single(telegramService.Messages);
        Assert.All(deliveries, delivery => Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status));

        var previews = await db.NotificationDeliveries
            .AsNoTracking()
            .Select(item => item.BodyPreview)
            .ToListAsync();
        Assert.DoesNotContain(previews, preview => preview.Contains("Secret-Password-123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyPasswordIssuedAsync_UsesConfiguredNotificationTemplate()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.PasswordIssued,
                        isEnabled = true,
                        subject = "Пароль для {{user.email}}",
                        body = "Здравствуйте, {{user.name}}. Ваш пароль: {{password}}"
                    }
                }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var user = await CreateUserAsync(db, "templated@example.com");
        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        await service.NotifyPasswordIssuedAsync(user, "Template-Password-1", [NotificationChannels.Email]);

        var message = Assert.Single(emailService.Messages);
        Assert.Equal("Пароль для templated@example.com", message.Subject);
        Assert.Contains("Template-Password-1", message.Body);
    }

    [Fact]
    public async Task NotifyTaskReminderAsync_DoesNotSendDuplicateReminderForSameEvent()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var user = await CreateUserAsync(db, "assignee@example.com");
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Подготовить документы",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = DateTime.UtcNow.AddMinutes(10),
            RemindersJson = "[\"before15m\"]",
            ChannelsJson = "[\"email\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var firstDeliveries = await service.NotifyTaskReminderAsync(task, "before15m", DateTime.UtcNow);
        var duplicateDeliveries = await service.NotifyTaskReminderAsync(task, "before15m", DateTime.UtcNow);

        Assert.Single(firstDeliveries);
        Assert.Empty(duplicateDeliveries);
        Assert.Single(emailService.Messages);
        Assert.Equal(1, await db.NotificationDeliveries.CountAsync());
    }

    [Fact]
    public async Task NotifyTaskReminderAsync_FormatsDueAtInRecipientTimeZone()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var user = await CreateUserAsync(db, "assignee-timezone@example.com");
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Yekaterinburg",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.TaskReminder,
                        isEnabled = true,
                        subject = "Срок задачи {{task.dueAt}}",
                        body = "Срок шаблона: {{task.dueAt}}\n{{reminder.body}}"
                    }
                }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var dueAtUtc = new DateTime(2026, 6, 4, 11, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Проверить заседание",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = dueAtUtc,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"email\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        await service.NotifyTaskReminderAsync(task, "atDue", dueAtUtc);

        var message = Assert.Single(emailService.Messages);
        Assert.Contains("Срок задачи 04.06.2026 16:00", message.Subject);
        Assert.Contains("Срок шаблона: 04.06.2026 16:00", message.Body);
        Assert.Contains("Срок: 04.06.2026 16:00", message.Body);
        Assert.DoesNotContain("04.06.2026 11:00", message.Body);
    }

    [Fact]
    public async Task NotifyTaskReminderAsync_DeduplicatesDailyOverdueByRecipientLocalDate()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var user = await CreateUserAsync(db, "daily-overdue-local-date@example.com");
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Pacific/Pago_Pago",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Просроченная задача",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Utc),
            RemindersJson = "[\"dailyOverdue\"]",
            ChannelsJson = "[\"email\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var firstDeliveries = await service.NotifyTaskReminderAsync(
            task,
            "dailyOverdue",
            new DateTime(2026, 6, 4, 23, 30, 0, DateTimeKind.Utc));
        var duplicateDeliveries = await service.NotifyTaskReminderAsync(
            task,
            "dailyOverdue",
            new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc));

        Assert.Single(firstDeliveries);
        Assert.Empty(duplicateDeliveries);
        Assert.Single(emailService.Messages);
        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.EndsWith(":20260604", delivery.EventKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyTaskReminderAsync_AllowsSameReminderAfterDueAtChanges()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var user = await CreateUserAsync(db, "changed-due-reminder@example.com");
        var firstDueAt = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Перенос срока напоминания",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = firstDueAt,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"email\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var firstDeliveries = await service.NotifyTaskReminderAsync(task, "atDue", firstDueAt);
        task.DueAt = firstDueAt.AddDays(1);
        var secondDeliveries = await service.NotifyTaskReminderAsync(task, "atDue", task.DueAt);

        Assert.Single(firstDeliveries);
        Assert.Single(secondDeliveries);
        Assert.Equal(2, emailService.Messages.Count);
        var eventKeys = await db.NotificationDeliveries
            .OrderBy(item => item.Id)
            .Select(item => item.EventKey)
            .ToListAsync();
        Assert.Equal(2, eventKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(":20260604100000", eventKeys[0], StringComparison.Ordinal);
        Assert.Contains(":20260605100000", eventKeys[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyTaskReminderAsync_SendsWebPushForAtDueWhenEnabled()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "at-due-web-push@example.com");
        var dueAt = DateTime.UtcNow.AddMinutes(-1);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Наступил срок",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = dueAt,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        var deliveries = await service.NotifyTaskReminderAsync(task, "atDue", DateTime.UtcNow);

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
        Assert.Equal(user.Id, pushed.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskReminder, pushed.Type);
        Assert.Contains("Наступил срок", notification.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotifyTaskReminderAsync_DoesNotAttachDetachedTaskGraphWhenCreatingInAppNotification()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "detached-reminder-assignee@example.com");
        var dueAt = DateTime.UtcNow.AddMinutes(-5);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Напомнить по отсоединенной задаче",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = dueAt,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = user.Id,
                    User = user,
                    Role = "assignee",
                    DisplayName = user.FullName,
                    Email = user.Email
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var detachedTask = await db.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.Participants)
                .ThenInclude(participant => participant.User)
            .SingleAsync(item => item.Id == task.Id);
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskReminderAsync(detachedTask, "atDue", DateTime.UtcNow);

        Assert.Empty(deliveries);
        Assert.Equal(1, await db.Users.CountAsync(item => item.Id == user.Id));
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(user.Id, notification.RecipientUserId);
        Assert.Equal(task.Id, notification.TaskJournalItemId);
        Assert.Equal(UserNotificationTypes.TaskReminder, notification.Type);
    }

    [Fact]
    public async Task NotifyTaskReminderAsync_DeduplicatesDailyOverdueWebPushByRecipientLocalDate()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "daily-overdue-web-push@example.com");
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Pacific/Pago_Pago",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Просроченная push-задача",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Utc),
            RemindersJson = "[\"dailyOverdue\"]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyTaskReminderAsync(
            task,
            "dailyOverdue",
            new DateTime(2026, 6, 4, 23, 30, 0, DateTimeKind.Utc));
        await service.NotifyTaskReminderAsync(
            task,
            "dailyOverdue",
            new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc));

        var notification = await db.UserNotifications.SingleAsync();
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
        Assert.Equal(UserNotificationTypes.TaskReminder, pushed.Type);
        Assert.Contains("Просрочена", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(":20260604", notification.EventKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyTaskChangedAsync_CreatesInAppNotificationWithoutExternalChannels()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "author@example.com");
        var assignee = new User
        {
            Email = "assignee-in-app@example.com",
            FullName = "assignee-in-app@example.com",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Подготовить заявление",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(3),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskChangedAsync(
            task,
            ["Статус"],
            author.FullName,
            author.Id);

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskChanged, notification.Type);
        Assert.Equal(task.Id, notification.TaskJournalItemId);
        Assert.False(notification.IsRead);
        Assert.False(notification.IsArchived);
    }

    [Fact]
    public async Task NotifyTaskChangedAsync_HighlightsDueAtChange()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "author-due-change@example.com");
        var assignee = new User
        {
            Email = "assignee-due-change@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Yekaterinburg",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var oldDueAt = new DateTime(2026, 6, 4, 11, 0, 0, DateTimeKind.Utc);
        var newDueAt = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Подготовить заявление",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = newDueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskChangedAsync(
            task,
            [$"Срок: {oldDueAt:dd.MM.yyyy HH:mm} -> {newDueAt:dd.MM.yyyy HH:mm}"],
            author.FullName,
            author.Id);

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.TaskChanged, notification.Type);
        Assert.Equal("rose", notification.Tone);
        Assert.Contains("Изменен срок задачи", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Срок: 04.06.2026 16:00 -> 04.06.2026 17:00", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("04.06.2026 11:00", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($":due-at:{DateTime.SpecifyKind(newDueAt, DateTimeKind.Utc):yyyyMMddHHmm}:", notification.EventKey, StringComparison.Ordinal);
        Assert.Contains("\"hasDueAtChange\":true", notification.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotifyTaskChangedAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "task-change-timezone-query-actor@example.com",
            FullName = "Автор изменения",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"task-change-timezone-query-recipient-{index}@example.com",
                FullName = $"Получатель изменения {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        var oldDueAt = new DateTime(2026, 7, 19, 11, 0, 0, DateTimeKind.Utc);
        var newDueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Проверить изменения",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = newDueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskChangedAsync(
            task,
            [$"Срок: {oldDueAt:dd.MM.yyyy HH:mm} -> {newDueAt:dd.MM.yyyy HH:mm}"],
            actor.FullName,
            actor.Id);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Contains(
            "Срок: 19.07.2026 11:00 -> 19.07.2026 12:00",
            notifications.Single(notification => notification.RecipientUserId == recipients[0].Id).BodyPreview);
        Assert.Contains(
            "Срок: 19.07.2026 18:00 -> 19.07.2026 19:00",
            notifications.Single(notification => notification.RecipientUserId == recipients[1].Id).BodyPreview);
    }

    [Fact]
    public async Task NotifyTaskChangedAsync_SkipsInAppNotificationWhenTypeDisabledForUser()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "author-disabled-type@example.com");
        var assignee = new User
        {
            Email = "assignee-disabled-type@example.com",
            FullName = "Исполнитель без уведомлений об изменениях",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = JsonSerializer.Serialize(new { disabledTypes = new[] { UserNotificationTypes.TaskChanged } }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Подготовить заявление",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(3),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskChangedAsync(
            task,
            ["Статус"],
            author.FullName,
            author.Id);

        Assert.Empty(deliveries);
        Assert.Equal(0, await db.UserNotifications.CountAsync());
    }

    [Fact]
    public void UserNotificationPreferenceSettings_IsChannelEnabled_UsesPerTypeChannelPreferences()
    {
        var rawValue = JsonSerializer.Serialize(new
        {
            disabledTypes = new[] { UserNotificationTypes.TaskComment },
            disabledChannels = new Dictionary<string, string[]>
            {
                [UserNotificationTypes.TaskAssigned] =
                [
                    NotificationChannels.Email,
                    NotificationChannels.ForcedModal,
                    "unknown",
                    NotificationChannels.Email
                ]
            }
        });

        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(rawValue, UserNotificationTypes.TaskAssigned, NotificationChannels.Email));
        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(rawValue, UserNotificationTypes.TaskAssigned, NotificationChannels.ForcedModal));
        Assert.True(UserNotificationPreferenceSettings.IsChannelEnabled(rawValue, UserNotificationTypes.TaskAssigned, NotificationChannels.Telegram));
        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(rawValue, UserNotificationTypes.TaskComment, NotificationChannels.InApp));
    }

    [Fact]
    public void UserNotificationDoNotDisturbSettings_MutesOnlyNoisyChannelsAndKeepsCritical()
    {
        var settings = UserNotificationDoNotDisturbSettings.Parse(JsonSerializer.Serialize(new
        {
            enabled = true,
            workStart = "09:00",
            workEnd = "18:00",
            muteWeekends = true,
            allowCritical = true
        }));
        var saturdayNoon = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(UserNotificationDoNotDisturbSettings.IsChannelMuted(
            settings,
            UserNotificationTypes.Find(UserNotificationTypes.TaskAssigned),
            NotificationChannels.InApp,
            saturdayNoon,
            TimeZoneInfo.Utc));
        Assert.True(UserNotificationDoNotDisturbSettings.IsChannelMuted(
            settings,
            UserNotificationTypes.Find(UserNotificationTypes.TaskAssigned),
            NotificationChannels.Toast,
            saturdayNoon,
            TimeZoneInfo.Utc));
        Assert.False(UserNotificationDoNotDisturbSettings.IsChannelMuted(
            settings,
            UserNotificationTypes.Find(UserNotificationTypes.TaskReminder),
            NotificationChannels.WebPush,
            saturdayNoon,
            TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_RespectsPerTypeChannelPreferences()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var author = await CreateUserAsync(db, "task-author-channel-preferences@example.com");
        var assignee = new User
        {
            Email = "task-assignee-channel-preferences@example.com",
            FullName = "Channel Preferences Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = JsonSerializer.Serialize(new
            {
                disabledChannels = new Dictionary<string, string[]>
                {
                    [UserNotificationTypes.TaskAssigned] = [NotificationChannels.Email]
                }
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare channel preferences test",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\",\"email\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var deliveries = await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Empty(deliveries);
        Assert.Empty(emailService.Messages);
        Assert.Equal(UserNotificationTypes.TaskAssigned, (await db.UserNotifications.SingleAsync()).Type);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_UsesRolePreferencesWhenUserHasNoPersonalPreferences()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var author = await CreateUserAsync(db, "task-author-role-preferences@example.com");
        var assignee = new User
        {
            Email = "task-assignee-role-preferences@example.com",
            FullName = "Role Preferences Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting
        {
            Key = RoleNotificationPreferenceSettings.SettingKey,
            Value = RoleNotificationPreferenceSettings.Serialize(new Dictionary<int, UserNotificationPreferenceSnapshot>
            {
                [assignee.RoleId] = new(
                    [],
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        [UserNotificationTypes.TaskAssigned] = [NotificationChannels.Email]
                    })
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare role preferences test",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\",\"email\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var deliveries = await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Empty(deliveries);
        Assert.Empty(emailService.Messages);
        Assert.Equal(UserNotificationTypes.TaskAssigned, (await db.UserNotifications.SingleAsync()).Type);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_UsesBuiltInLawyerRoleDefaultsWhenNoRoleOverrideExists()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var author = await CreateUserAsync(db, "task-author-lawyer-defaults@example.com");
        var lawyerRole = new Role
        {
            Name = "Юрист",
            Description = "Юрист",
            CreatedAt = DateTime.UtcNow
        };
        db.Roles.Add(lawyerRole);
        await db.SaveChangesAsync();
        var assignee = new User
        {
            Email = "task-assignee-lawyer-defaults@example.com",
            FullName = "Lawyer Defaults Assignee",
            PasswordHash = "hash",
            RoleId = lawyerRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare lawyer defaults test",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\",\"email\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var deliveries = await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Empty(deliveries);
        Assert.Empty(emailService.Messages);
        Assert.Equal(UserNotificationTypes.TaskAssigned, (await db.UserNotifications.SingleAsync()).Type);
    }

    [Fact]
    public void RoleNotificationPreferenceDefaults_ReturnExpectedProfiles()
    {
        var secretary = RoleNotificationPreferenceDefaults.Resolve("Секретарь");
        var finance = RoleNotificationPreferenceDefaults.Resolve("Бухгалтерия");
        var manager = RoleNotificationPreferenceDefaults.Resolve("Руководитель практики");

        Assert.Equal("secretary", secretary.Key);
        Assert.Equal("finance", finance.Key);
        Assert.Equal("manager", manager.Key);
        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(
            secretary.Preferences,
            UserNotificationTypes.PaymentAssigned,
            NotificationChannels.Email));
        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(
            finance.Preferences,
            UserNotificationTypes.PaymentAssigned,
            NotificationChannels.Email));
        Assert.True(UserNotificationPreferenceSettings.IsChannelEnabled(
            finance.Preferences,
            UserNotificationTypes.PaymentAdvanceReviewRequired,
            NotificationChannels.Email));
        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(
            manager.Preferences,
            UserNotificationTypes.CourtHearing,
            NotificationChannels.Telegram));
        Assert.True(UserNotificationPreferenceSettings.IsChannelEnabled(
            manager.Preferences,
            UserNotificationTypes.CourtSyncFailed,
            NotificationChannels.Telegram));
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_CreatesInAppNotificationForAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author@example.com");
        var assignee = new User
        {
            Email = "new-assignee@example.com",
            FullName = "Новый исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "ЗЕМЦОВ - банкротство",
            Title = "Подготовить отзыв",
            Description = "Проверить материалы дела",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddDays(3),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskAssigned, notification.Type);
        Assert.Equal($"task:{task.Id}:assigned", notification.EventKey);
        Assert.Equal("blue", notification.Tone);
        Assert.Contains("Новая задача", notification.Title);
        Assert.Contains("Подготовить отзыв", notification.BodyPreview);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "task-timezone-query-actor@example.com",
            FullName = "Автор задачи",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"task-timezone-query-recipient-{index}@example.com",
                FullName = $"Получатель задачи {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Проверить пакет документов",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskAssignedAsync(task, actor.FullName, actor.Id);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Contains(
            "19.07.2026 12:00",
            notifications.Single(notification => notification.RecipientUserId == recipients[0].Id).BodyPreview);
        Assert.Contains(
            "19.07.2026 19:00",
            notifications.Single(notification => notification.RecipientUserId == recipients[1].Id).BodyPreview);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_HighlightsUrgentDueAt()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author-urgent@example.com");
        var assignee = new User
        {
            Email = "urgent-assignee@example.com",
            FullName = "Срочный исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "ЗЕМЦОВ - банкротство",
            Title = "Срочно подготовить отзыв",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);

        var deliveries = await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal("rose", notification.Tone);
        Assert.Contains("\"isUrgent\":true", notification.MetadataJson, StringComparison.OrdinalIgnoreCase);
        var published = Assert.Single(realtime.Notifications);
        Assert.Equal("rose", published.Tone);
        Assert.Equal(UserNotificationTypes.TaskAssigned, published.Type);
    }

    [Fact]
    public async Task NotifyTaskReassignedAsync_NotifiesNewAndPreviousAssignees()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-reassign-author@example.com");
        var previousAssignee = new User
        {
            Email = "task-reassign-previous@example.com",
            FullName = "Старый исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var newAssignee = new User
        {
            Email = "task-reassign-new@example.com",
            FullName = "Новый исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(previousAssignee, newAssignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "ЗЕМЦОВ - банкротство",
            Title = "Передать документы",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = newAssignee.Id,
            AssigneeName = newAssignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskReassignedAsync(
            task,
            previousAssignee.Id,
            previousAssignee.FullName,
            [$"Исполнитель: {previousAssignee.FullName} -> {newAssignee.FullName}"],
            author.FullName,
            author.Id,
            "Передали задачу другому исполнителю.");

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications
            .OrderBy(item => item.RecipientUserId)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);

        var newAssigneeNotification = Assert.Single(notifications, item => item.RecipientUserId == newAssignee.Id);
        Assert.Equal(UserNotificationTypes.TaskAssigned, newAssigneeNotification.Type);
        Assert.Contains("Задача назначена вам", newAssigneeNotification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Старый исполнитель", newAssigneeNotification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Новый исполнитель", newAssigneeNotification.BodyPreview, StringComparison.OrdinalIgnoreCase);

        var previousAssigneeNotification = Assert.Single(notifications, item => item.RecipientUserId == previousAssignee.Id);
        Assert.Equal(UserNotificationTypes.TaskChanged, previousAssigneeNotification.Type);
        Assert.Equal("amber", previousAssigneeNotification.Tone);
        Assert.Contains("Задача снята с вас", previousAssigneeNotification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Передали задачу другому исполнителю.", previousAssigneeNotification.BodyPreview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotifyTaskReassignedAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "task-reassign-timezone-query-actor@example.com",
            FullName = "Автор",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"task-reassign-timezone-query-recipient-{index}@example.com",
                FullName = $"Сотрудник {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        var previousDueAt = new DateTime(2026, 7, 19, 11, 0, 0, DateTimeKind.Utc);
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело",
            Title = "Документы",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress",
            Participants = recipients
                .Skip(2)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskReassignedAsync(
            task,
            recipients[1].Id,
            recipients[1].FullName,
            [
                $"Срок: {previousDueAt:dd.MM.yyyy HH:mm} -> {dueAt:dd.MM.yyyy HH:mm}",
                $"Исполнитель: {recipients[1].FullName} -> {recipients[0].FullName}",
            ],
            actor.FullName,
            actor.Id);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        var currentAssigneeNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Equal(UserNotificationTypes.TaskAssigned, currentAssigneeNotification.Type);
        Assert.Contains("Срок: 19.07.2026 12:00", currentAssigneeNotification.BodyPreview);
        var previousAssigneeNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Equal(UserNotificationTypes.TaskChanged, previousAssigneeNotification.Type);
        Assert.Contains(
            "Срок: 19.07.2026 18:00 -> 19.07.2026 19:00",
            previousAssigneeNotification.BodyPreview);
        Assert.All(
            recipients.Skip(2),
            recipient => Assert.Equal(
                UserNotificationTypes.TaskChanged,
                notifications.Single(notification => notification.RecipientUserId == recipient.Id).Type));
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_PublishesRealtimeNotificationForAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author-realtime@example.com");
        var assignee = new User
        {
            Email = "new-assignee-realtime@example.com",
            FullName = "Realtime Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare response",
            Description = "Check documents",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);

        await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        var published = Assert.Single(realtime.Notifications);
        Assert.Equal(assignee.Id, published.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskAssigned, published.Type);
        Assert.Equal(task.Id, published.TaskJournalItemId);
        Assert.True(published.Id > 0);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_SendsWebPushWhenChannelEnabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author-web-push@example.com");
        var assignee = new User
        {
            Email = "new-assignee-web-push@example.com",
            FullName = "Web Push Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare push response",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        var notification = await db.UserNotifications.SingleAsync();
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
        Assert.Equal(assignee.Id, pushed.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskAssigned, pushed.Type);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_SuppressesNoisyChannelsDuringDoNotDisturb()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author-dnd@example.com");
        var assignee = new User
        {
            Email = "new-assignee-dnd@example.com",
            FullName = "DND Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = UserNotificationDoNotDisturbSettings.SettingKey,
            Value = JsonSerializer.Serialize(new
            {
                enabled = true,
                vacationUntil = "2099-01-01",
                allowCritical = true
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare quiet response",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Equal(UserNotificationTypes.TaskAssigned, (await db.UserNotifications.SingleAsync()).Type);
        Assert.Single(realtime.Notifications);
        Assert.False(Assert.Single(realtime.ToastEnabledValues));
        Assert.Empty(webPush.Notifications);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_DoesNotSendWebPushWhenChannelDisabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author-web-push-disabled@example.com");
        var assignee = new User
        {
            Email = "new-assignee-web-push-disabled@example.com",
            FullName = "Web Push Disabled Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = JsonSerializer.Serialize(new
            {
                disabledChannels = new Dictionary<string, string[]>
                {
                    [UserNotificationTypes.TaskAssigned] = [NotificationChannels.WebPush]
                }
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare disabled push response",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Empty(webPush.Notifications);
        Assert.Equal(UserNotificationTypes.TaskAssigned, (await db.UserNotifications.SingleAsync()).Type);
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_PublishesRealtimeWithToastDisabledFlagWhenToastChannelDisabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author-toast-disabled@example.com");
        var assignee = new User
        {
            Email = "new-assignee-toast-disabled@example.com",
            FullName = "Toast Disabled Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = JsonSerializer.Serialize(new
            {
                disabledChannels = new Dictionary<string, string[]>
                {
                    [UserNotificationTypes.TaskAssigned] = [NotificationChannels.Toast]
                }
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare disabled toast response",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);

        await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Equal(UserNotificationTypes.TaskAssigned, (await db.UserNotifications.SingleAsync()).Type);
        Assert.Single(realtime.Notifications);
        Assert.False(Assert.Single(realtime.ToastEnabledValues));
    }

    [Fact]
    public async Task NotifyTaskAssignedAsync_DoesNotPublishRealtimeWhenInAppTypeDisabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "task-author-realtime-disabled@example.com");
        var assignee = new User
        {
            Email = "new-assignee-realtime-disabled@example.com",
            FullName = "Realtime Disabled Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = JsonSerializer.Serialize(new { disabledTypes = new[] { UserNotificationTypes.TaskAssigned } }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Prepare disabled response",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);

        await service.NotifyTaskAssignedAsync(task, author.FullName, author.Id);

        Assert.Empty(realtime.Notifications);
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task NotifyTaskCommentAsync_NotifiesTaskParticipantsExceptCommentAuthor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "comment-author-task-owner@example.com");
        var assignee = new User
        {
            Email = "comment-assignee@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var approver = new User
        {
            Email = "comment-approver@example.com",
            FullName = "Согласующий",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var observer = new User
        {
            Email = "comment-observer@example.com",
            FullName = "Наблюдатель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(assignee, approver, observer);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Проверить документы",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddDays(2),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = assignee.Id,
                    Role = "assignee",
                    DisplayName = assignee.FullName,
                    Email = assignee.Email,
                    SortOrder = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                },
                new TaskJournalParticipant
                {
                    UserId = approver.Id,
                    Role = "approver",
                    DisplayName = approver.FullName,
                    Email = approver.Email,
                    SortOrder = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                },
                new TaskJournalParticipant
                {
                    UserId = observer.Id,
                    Role = "observer",
                    DisplayName = observer.FullName,
                    Email = observer.Email,
                    SortOrder = 2,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var comment = new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            Kind = "comment",
            Text = "Клиент прислал документы.",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            CreatedAt = DateTime.UtcNow
        };
        db.TaskJournalComments.Add(comment);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);

        var deliveries = await service.NotifyTaskCommentAsync(task, comment);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications
            .OrderBy(item => item.RecipientUserId)
            .ToListAsync();
        Assert.Equal([assignee.Id, approver.Id, observer.Id], notifications.Select(item => item.RecipientUserId).ToArray());
        Assert.DoesNotContain(notifications, item => item.RecipientUserId == author.Id);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskComment, notification.Type);
            Assert.Equal("emerald", notification.Tone);
            Assert.Contains("Комментарий по задаче", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Клиент прислал документы.", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            notifications.Select(item => item.Id).OrderBy(id => id),
            realtime.Notifications.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task NotifyTaskCommentAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "task-comment-timezone-query-actor@example.com",
            FullName = "Автор комментария",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"task-comment-timezone-query-recipient-{index}@example.com",
                FullName = $"Участник обсуждения {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.TaskComment,
                        isEnabled = true,
                        subject = "Комментарий по задаче: {{task.title}}",
                        body = "Срок: {{task.dueAt}}\n{{comment.authorName}}: {{comment.text}}",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Обсудить документы",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var comment = new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            Kind = "comment",
            Text = "Проверьте новую версию документов.",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            CreatedAt = DateTime.UtcNow,
        };
        db.TaskJournalComments.Add(comment);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskCommentAsync(task, comment);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.DoesNotContain(notifications, notification => notification.RecipientUserId == actor.Id);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        var utcNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Срок: 19.07.2026 12:00", utcNotification.BodyPreview);
        var novosibirskNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Срок: 19.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskComment, notification.Type);
            Assert.Contains("Проверьте новую версию документов.", notification.BodyPreview);
            Assert.Contains($"\"commentId\":{comment.Id}", notification.MetadataJson);
        });
    }

    [Fact]
    public async Task NotifyTaskDeadlineChangeRequestedAsync_NotifiesReviewerAndObservers()
    {
        await using var db = await CreateContextAsync();
        var (task, author, assignee, observer) = await CreateTaskWithObserverAsync(db, "deadline-requested");
        var request = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = assignee.FullName,
            ReviewerUserId = author.Id,
            ReviewerName = author.FullName,
            CurrentDueAt = task.DueAt,
            RequestedDueAt = task.DueAt.AddDays(2),
            Reason = "Жду документы от клиента.",
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };
        db.TaskJournalDeadlineChangeRequests.Add(request);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskDeadlineChangeRequestedAsync(task, request);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.OrderBy(item => item.RecipientUserId).ToListAsync();
        Assert.Equal([author.Id, observer.Id], notifications.Select(item => item.RecipientUserId).ToArray());
        Assert.DoesNotContain(notifications, item => item.RecipientUserId == assignee.Id);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskDeadlineChangeRequested, notification.Type);
            Assert.True(UserNotificationTypes.RequiresAction(notification.Type));
            Assert.Equal(task.Id, notification.TaskJournalItemId);
            Assert.Contains("Запрос переноса срока", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Жду документы от клиента", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task NotifyTaskDeadlineChangeRequestedAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var requester = new User
        {
            Email = "deadline-request-timezone-query-requester@example.com",
            FullName = "Инициатор переноса",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"deadline-request-timezone-query-recipient-{index}@example.com",
                FullName = $"Получатель переноса {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(requester));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.TaskChanged,
                        isEnabled = true,
                        subject = "{{task.changeSubject}}",
                        body = "Текущий срок: {{task.dueAt}}\n{{task.changes}}\n{{task.commentBlock}}",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var requestedDueAt = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Перенести срок документов",
            AuthorUserId = recipients[0].Id,
            AuthorName = recipients[0].FullName,
            AssigneeUserId = requester.Id,
            AssigneeName = requester.FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .Append(new TaskJournalParticipant
                {
                    UserId = recipients[0].Id,
                    DisplayName = recipients[0].FullName,
                    Role = "observer",
                    SortOrder = recipients.Length,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var request = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = requester.Id,
            RequestedByName = requester.FullName,
            ReviewerUserId = recipients[0].Id,
            ReviewerName = recipients[0].FullName,
            CurrentDueAt = dueAt,
            RequestedDueAt = requestedDueAt,
            Reason = "Нужно дождаться новых документов.",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        };
        db.TaskJournalDeadlineChangeRequests.Add(request);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskDeadlineChangeRequestedAsync(task, request);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.DoesNotContain(notifications, notification => notification.RecipientUserId == requester.Id);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        var utcNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Текущий срок: 19.07.2026 12:00", utcNotification.BodyPreview);
        Assert.Contains("Запрошен перенос срока на 21.07.2026 12:00", utcNotification.BodyPreview);
        var novosibirskNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Текущий срок: 19.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.Contains("Запрошен перенос срока на 21.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskDeadlineChangeRequested, notification.Type);
            Assert.Contains("Нужно дождаться новых документов.", notification.BodyPreview);
            Assert.Contains($"\"deadlineChangeRequestId\":{request.Id}", notification.MetadataJson);
        });
    }

    [Fact]
    public async Task NotifyTaskDeadlineChangeApprovedAsync_NotifiesRequesterAndObservers()
    {
        await using var db = await CreateContextAsync();
        var (task, author, assignee, observer) = await CreateTaskWithObserverAsync(db, "deadline-approved");
        var request = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = assignee.FullName,
            ReviewerUserId = author.Id,
            ReviewerName = author.FullName,
            CurrentDueAt = task.DueAt,
            RequestedDueAt = task.DueAt.AddDays(2),
            Reason = "Нужен новый срок.",
            Status = "approved",
            DecisionDueAt = task.DueAt.AddDays(3),
            DecisionComment = "Согласовано до новой даты.",
            DecidedByUserId = author.Id,
            DecidedByName = author.FullName,
            DecidedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        db.TaskJournalDeadlineChangeRequests.Add(request);
        task.DueAt = request.DecisionDueAt.Value;
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskDeadlineChangeApprovedAsync(task, request);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.OrderBy(item => item.RecipientUserId).ToListAsync();
        Assert.Equal([assignee.Id, observer.Id], notifications.Select(item => item.RecipientUserId).ToArray());
        Assert.DoesNotContain(notifications, item => item.RecipientUserId == author.Id);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskDeadlineChangeApproved, notification.Type);
            Assert.Equal("emerald", notification.Tone);
            Assert.Contains("Перенос срока согласован", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Согласовано до новой даты", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task NotifyTaskDeadlineChangeApprovedAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var reviewer = new User
        {
            Email = "deadline-decision-timezone-query-reviewer@example.com",
            FullName = "Проверяющий переноса",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"deadline-decision-timezone-query-recipient-{index}@example.com",
                FullName = $"Получатель решения {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(reviewer));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.TaskChanged,
                        isEnabled = true,
                        subject = "{{task.changeSubject}}",
                        body = "Срок задачи: {{task.dueAt}}\n{{task.changes}}\n{{task.commentBlock}}",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var currentDueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var decisionDueAt = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Решить перенос срока",
            AuthorUserId = reviewer.Id,
            AuthorName = reviewer.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = decisionDueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .Append(new TaskJournalParticipant
                {
                    UserId = recipients[0].Id,
                    DisplayName = recipients[0].FullName,
                    Role = "observer",
                    SortOrder = recipients.Length,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var request = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = recipients[0].Id,
            RequestedByName = recipients[0].FullName,
            ReviewerUserId = reviewer.Id,
            ReviewerName = reviewer.FullName,
            CurrentDueAt = currentDueAt,
            RequestedDueAt = decisionDueAt.AddDays(-1),
            Reason = "Требуется дополнительное время.",
            Status = "approved",
            DecisionDueAt = decisionDueAt,
            DecisionComment = "Новый срок подтверждён.",
            DecidedByUserId = reviewer.Id,
            DecidedByName = reviewer.FullName,
            DecidedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.TaskJournalDeadlineChangeRequests.Add(request);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskDeadlineChangeApprovedAsync(task, request);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.DoesNotContain(notifications, notification => notification.RecipientUserId == reviewer.Id);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        var utcNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Срок задачи: 22.07.2026 12:00", utcNotification.BodyPreview);
        Assert.Contains("Перенос срока согласован на 22.07.2026 12:00", utcNotification.BodyPreview);
        var novosibirskNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Срок задачи: 22.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.Contains("Перенос срока согласован на 22.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskDeadlineChangeApproved, notification.Type);
            Assert.Contains("Новый срок подтверждён.", notification.BodyPreview);
            Assert.Contains($"\"deadlineChangeRequestId\":{request.Id}", notification.MetadataJson);
            Assert.Contains("\"status\":\"approved\"", notification.MetadataJson);
        });
    }

    [Fact]
    public async Task NotifyTaskDeadlineChangeRejectedAsync_NotifiesRequesterAndObservers()
    {
        await using var db = await CreateContextAsync();
        var (task, author, assignee, observer) = await CreateTaskWithObserverAsync(db, "deadline-rejected");
        var request = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = assignee.FullName,
            ReviewerUserId = author.Id,
            ReviewerName = author.FullName,
            CurrentDueAt = task.DueAt,
            RequestedDueAt = task.DueAt.AddDays(2),
            Reason = "Нужен новый срок.",
            Status = "rejected",
            DecisionComment = "Срок менять нельзя.",
            DecidedByUserId = author.Id,
            DecidedByName = author.FullName,
            DecidedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        db.TaskJournalDeadlineChangeRequests.Add(request);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskDeadlineChangeRejectedAsync(task, request);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.OrderBy(item => item.RecipientUserId).ToListAsync();
        Assert.Equal([assignee.Id, observer.Id], notifications.Select(item => item.RecipientUserId).ToArray());
        Assert.DoesNotContain(notifications, item => item.RecipientUserId == author.Id);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskDeadlineChangeRejected, notification.Type);
            Assert.Equal("orange", notification.Tone);
            Assert.Contains("Перенос срока отклонен", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Срок менять нельзя", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task NotifyTaskCommentAsync_SendsWebPushWhenChannelEnabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "comment-author-web-push@example.com");
        var assignee = new User
        {
            Email = "comment-assignee-web-push@example.com",
            FullName = "Web Push Comment Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Discuss comment push",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddDays(1),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var comment = new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            Kind = "comment",
            Text = "Please check the file.",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            CreatedAt = DateTime.UtcNow
        };
        db.TaskJournalComments.Add(comment);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyTaskCommentAsync(task, comment);

        var notification = await db.UserNotifications.SingleAsync();
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
        Assert.Equal(assignee.Id, pushed.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskComment, pushed.Type);
    }

    [Fact]
    public async Task NotifyCaseCourtEventAsync_CreatesCourtHearingNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "court-recipient@example.com");
        var client = new Client
        {
            Name = "Клиент по суду",
            IsActive = true,
            ResponsibleUserId = recipient.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Спор о взыскании",
            CaseNumber = "CASE-COURT-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = recipient.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var courtEvent = new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            EventType = "hearing",
            Title = "Предварительное заседание",
            ScheduledAt = new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Utc),
            CourtInstance = "first",
            CourtName = "Арбитражный суд",
            CourtCaseNumber = "А55-100/2026",
            MovementStatus = "scheduled",
            SourceType = "manual",
            CreatedAt = new DateTime(2026, 6, 18, 1, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 6, 18, 1, 0, 0, DateTimeKind.Utc),
        };
        db.ClientCaseCourtEvents.Add(courtEvent);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyCaseCourtEventAsync(
            clientCase,
            courtEvent,
            "created",
            "Court Actor",
            999,
            [recipient.Id]);
        await service.NotifyCaseCourtEventAsync(
            clientCase,
            courtEvent,
            "created",
            "Court Actor",
            999,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CourtHearing, notification.Type);
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("CASE-COURT-1", notification.Title);
        Assert.Contains("Предварительное заседание", notification.BodyPreview);
        Assert.Contains($"\"courtEventId\":{courtEvent.Id}", notification.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyCaseProceduralDeadlineAsync_CreatesNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "deadline-recipient@example.com");
        var client = new Client
        {
            Name = "Клиент по сроку",
            IsActive = true,
            ResponsibleUserId = recipient.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Дело со сроком",
            CaseNumber = "CASE-DEADLINE-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = recipient.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            DeadlineType = "appeal",
            Title = "Апелляционная жалоба",
            DueAt = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc),
            AllDay = false,
            Status = "active",
            WarningDaysBefore = 5,
            Notes = "Подготовить пакет документов",
            CreatedAt = new DateTime(2026, 6, 18, 2, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 6, 18, 2, 0, 0, DateTimeKind.Utc),
        };
        db.ClientCaseProceduralDeadlines.Add(deadline);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyCaseProceduralDeadlineAsync(
            clientCase,
            deadline,
            "updated",
            "Deadline Actor",
            999,
            [recipient.Id]);
        await service.NotifyCaseProceduralDeadlineAsync(
            clientCase,
            deadline,
            "updated",
            "Deadline Actor",
            999,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CaseProceduralDeadline, notification.Type);
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("CASE-DEADLINE-1", notification.Title);
        Assert.Contains("Апелляционная жалоба", notification.BodyPreview);
        Assert.Contains("Подготовить пакет документов", notification.BodyPreview);
        Assert.Contains($"\"proceduralDeadlineId\":{deadline.Id}", notification.MetadataJson, StringComparison.Ordinal);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyCaseDocumentAsync_CreatesNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "case-document-recipient@example.com");
        var client = new Client
        {
            Name = "Клиент по документу",
            IsActive = true,
            ResponsibleUserId = recipient.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Дело с документом",
            CaseNumber = "CASE-DOCUMENT-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = recipient.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var document = new ClientCaseDocument
        {
            ClientCaseId = clientCase.Id,
            DocumentKey = "claim",
            Version = 2,
            DocumentType = "Иск",
            Status = DocumentWorkflowStatus.NeedsCorrection,
            OriginalFileName = "claim-v2.pdf",
            StoredFileName = "claim-v2.pdf",
            StoragePath = "1/claim-v2.pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            UploadedByName = recipient.FullName,
            UploadedAt = new DateTime(2026, 6, 18, 3, 0, 0, DateTimeKind.Utc),
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyCaseDocumentAsync(
            clientCase,
            document,
            "updated",
            "Document Actor",
            999,
            [recipient.Id]);
        await service.NotifyCaseDocumentAsync(
            clientCase,
            document,
            "updated",
            "Document Actor",
            999,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CaseDocument, notification.Type);
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("claim-v2.pdf", notification.Title);
        Assert.Contains("Требуется действие", notification.BodyPreview);
        Assert.Contains($"\"clientCaseDocumentId\":{document.Id}", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"requiresAction\":true", notification.MetadataJson, StringComparison.Ordinal);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        Assert.Empty(webPush.Notifications);
    }

    [Fact]
    public async Task NotifyCaseAccessRequestedAsync_CreatesManagerNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var requester = await CreateUserAsync(db, "case-access-requester@example.com");
        var manager = new User
        {
            Email = "case-access-manager@example.com",
            FullName = "case-access-manager@example.com",
            PasswordHash = "hash",
            RoleId = requester.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(manager);
        await db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Клиент доступа",
            IsActive = true,
            ResponsibleUserId = requester.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Client = client,
            Title = "Скрытое дело",
            CaseNumber = "CASE-ACCESS-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = requester.Id,
            IsHiddenFromExecutor = true,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);
        var requestedAt = new DateTime(2026, 6, 18, 4, 0, 0, DateTimeKind.Utc);

        await service.NotifyCaseAccessRequestedAsync(
            clientCase,
            requester.FullName,
            requester.Id,
            "Нужно проверить срок",
            requestedAt,
            [manager.Id]);
        await service.NotifyCaseAccessRequestedAsync(
            clientCase,
            requester.FullName,
            requester.Id,
            "Нужно проверить срок",
            requestedAt,
            [manager.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CaseAccessRequested, notification.Type);
        Assert.Equal(manager.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("CASE-ACCESS-1", notification.Title);
        Assert.Contains("Нужно проверить срок", notification.BodyPreview);
        Assert.Contains($"\"requestedByUserId\":{requester.Id}", notification.MetadataJson, StringComparison.Ordinal);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyCaseAccessResolvedAsync_CreatesRequesterNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var requester = await CreateUserAsync(db, "case-access-resolved-requester@example.com");
        var manager = new User
        {
            Email = "case-access-resolved-manager@example.com",
            FullName = "case-access-resolved-manager@example.com",
            PasswordHash = "hash",
            RoleId = requester.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(manager);
        await db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Клиент решения",
            IsActive = true,
            ResponsibleUserId = requester.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Client = client,
            Title = "Скрытое дело для решения",
            CaseNumber = "CASE-ACCESS-2",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = requester.Id,
            IsHiddenFromExecutor = false,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);
        var resolvedAt = new DateTime(2026, 6, 18, 4, 30, 0, DateTimeKind.Utc);

        await service.NotifyCaseAccessResolvedAsync(
            clientCase,
            true,
            manager.FullName,
            manager.Id,
            requester.Id,
            requester.FullName,
            "Нужно проверить срок",
            "Можно работать",
            resolvedAt);
        await service.NotifyCaseAccessResolvedAsync(
            clientCase,
            true,
            manager.FullName,
            manager.Id,
            requester.Id,
            requester.FullName,
            "Нужно проверить срок",
            "Можно работать",
            resolvedAt);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CaseAccessResolved, notification.Type);
        Assert.Equal(requester.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("разрешен", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Можно работать", notification.BodyPreview);
        Assert.Contains("\"granted\":true", notification.MetadataJson, StringComparison.Ordinal);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyCaseInactivityControlTaskCreatedAsync_CreatesDedicatedInAppNotification()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateUserAsync(db, "inactive-assignee@example.com");
        var task = new TaskJournalItem
        {
            ObjectName = "ЗЕМЦОВ - банкротство",
            Title = "Проверить движение по делу",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
            Type = "control"
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyCaseInactivityControlTaskCreatedAsync(task, 20);

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CaseInactivityControl, notification.Type);
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(task.Id, notification.TaskJournalItemId);
        Assert.Contains("20 дн", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyCaseInactivityControlTaskCreatedAsync_NotifiesEscalationObserver()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateUserAsync(db, "inactive-assignee@example.com");
        var observer = new User
        {
            Email = "inactive-observer@example.com",
            FullName = "inactive-observer@example.com",
            PasswordHash = "hash",
            RoleId = assignee.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(observer);
        await db.SaveChangesAsync();
        var task = new TaskJournalItem
        {
            ObjectName = "ЗЕМЦОВ - банкротство",
            Title = "Проверить движение по делу",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
            Type = "control",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = assignee.Id,
                    Role = "assignee",
                    DisplayName = assignee.FullName,
                    Email = assignee.Email,
                    ApprovalStatus = "pending"
                },
                new TaskJournalParticipant
                {
                    UserId = observer.Id,
                    Role = "observer",
                    DisplayName = observer.FullName,
                    Email = observer.Email,
                    ApprovalStatus = "pending"
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyCaseInactivityControlTaskCreatedAsync(task, 35);

        var notifications = await db.UserNotifications
            .OrderBy(item => item.RecipientUserId)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.Contains(notifications, item => item.RecipientUserId == assignee.Id);
        Assert.Contains(notifications, item => item.RecipientUserId == observer.Id);
    }

    [Fact]
    public async Task NotifyCaseInactivityControlTaskCreatedAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"case-inactivity-timezone-query-recipient-{index}@example.com",
                FullName = $"Сотрудник {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients);
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.CaseInactivityControl,
                        isEnabled = true,
                        subject = "Автоконтроль дела: {{task.objectName}}",
                        body = "Срок: {{task.dueAt}}",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело",
            Title = "Контроль",
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
            Type = "control",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .Append(new TaskJournalParticipant
                {
                    UserId = recipients[0].Id,
                    DisplayName = recipients[0].FullName,
                    Role = "observer",
                    SortOrder = recipients.Length,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyCaseInactivityControlTaskCreatedAsync(task, 35);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        var assigneeNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Срок: 19.07.2026 12:00", assigneeNotification.BodyPreview);
        Assert.Contains("\"isEscalationObserver\":false", assigneeNotification.MetadataJson);
        var observerNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Срок: 19.07.2026 19:00", observerNotification.BodyPreview);
        Assert.Contains("\"isEscalationObserver\":true", observerNotification.MetadataJson);
    }

    [Fact]
    public async Task NotifyTaskScheduleConflictAsync_CreatesInAppNotificationForAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "schedule-author@example.com");
        var assignee = new User
        {
            Email = "schedule-assignee@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "ЗЕМЦОВ - банкротство",
            Title = "Новая задача",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(2),
            PlannedDurationMinutes = 60,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        var conflictTask = new TaskJournalItem
        {
            ObjectName = "ЗЕМЦОВ - банкротство",
            Title = "Существующая задача",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = task.DueAt.AddMinutes(30),
            PlannedDurationMinutes = 60,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.AddRange(task, conflictTask);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskScheduleConflictAsync(task, [conflictTask], author.FullName, author.Id);

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskScheduleConflict, notification.Type);
        Assert.Contains("Конфликт расписания", notification.Title);
        Assert.Contains("Существующая задача", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyTaskScheduleConflictAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "schedule-conflict-timezone-query-actor@example.com",
            FullName = "Автор",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"schedule-conflict-timezone-query-recipient-{index}@example.com",
                FullName = $"Сотрудник {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело",
            Title = "Новая встреча",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt,
            PlannedDurationMinutes = 60,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .ToList(),
        };
        var conflictTask = new TaskJournalItem
        {
            ObjectName = "Дело",
            Title = "Существующая встреча",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt.AddMinutes(30),
            PlannedDurationMinutes = 60,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
        };
        db.TaskJournalItems.AddRange(task, conflictTask);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskScheduleConflictAsync(
            task,
            [conflictTask],
            actor.FullName,
            actor.Id);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        var utcNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Срок: 19.07.2026 12:00", utcNotification.BodyPreview);
        Assert.Contains("19.07.2026 12:30", utcNotification.BodyPreview);
        var novosibirskNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Срок: 19.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.Contains("19.07.2026 19:30", novosibirskNotification.BodyPreview);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskScheduleConflict, notification.Type);
            Assert.Contains($"\"conflictTaskIds\":[{conflictTask.Id}]", notification.MetadataJson);
        });
    }

    [Fact]
    public async Task NotifyTaskScheduleConflictAsync_SendsWebPushWhenEnabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "conflict-push-author@example.com");
        var assignee = new User
        {
            Email = "conflict-push-assignee@example.com",
            FullName = "Conflict Push Assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(assignee);
        await db.SaveChangesAsync();

        var dueAt = DateTime.UtcNow.AddHours(2);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Новая встреча",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = dueAt,
            PlannedDurationMinutes = 60,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        var conflictTask = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Существующая встреча",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = dueAt.AddMinutes(30),
            PlannedDurationMinutes = 60,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new"
        };
        db.TaskJournalItems.AddRange(task, conflictTask);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyTaskScheduleConflictAsync(task, [conflictTask], author.FullName, author.Id);

        var notification = await db.UserNotifications.SingleAsync();
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
        Assert.Equal(assignee.Id, pushed.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskScheduleConflict, pushed.Type);
    }

    [Fact]
    public async Task NotifyTaskCancelledAsync_NotifiesTaskParticipantsWithoutTaskForeignKey()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "cancel-author@example.com");
        var assignee = new User
        {
            Email = "cancel-assignee@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var observer = new User
        {
            Email = "cancel-observer@example.com",
            FullName = "Наблюдатель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(assignee, observer);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Отмененная встреча",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(3),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "cancelled",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = observer.Id,
                    Role = "observer",
                    DisplayName = observer.FullName,
                    Email = observer.Email,
                    SortOrder = 0
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);

        var deliveries = await service.NotifyTaskCancelledAsync(task, author.FullName, author.Id, "Удалена");

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications
            .OrderBy(item => item.RecipientUserId)
            .ToListAsync();
        Assert.Equal([assignee.Id, observer.Id], notifications.Select(item => item.RecipientUserId).ToArray());
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskCancelled, notification.Type);
            Assert.Equal("task", notification.ObjectType);
            Assert.Equal(task.Id, notification.ObjectId);
            Assert.Null(notification.TaskJournalItemId);
            Assert.Contains("Задача отменена", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Удалена", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            notifications.Select(item => item.Id).OrderBy(id => id),
            realtime.Notifications.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task NotifyTaskCancelledAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "task-cancel-timezone-query-actor@example.com",
            FullName = "Автор",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"task-cancel-timezone-query-recipient-{index}@example.com",
                FullName = $"Сотрудник {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "Дело",
            Title = "Встреча",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "cancelled",
            Participants = recipients
                .Skip(1)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskCancelledAsync(
            task,
            actor.FullName,
            actor.Id,
            "Удалена");

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Contains(
            "Срок: 19.07.2026 12:00",
            notifications.Single(notification => notification.RecipientUserId == recipients[0].Id).BodyPreview);
        Assert.Contains(
            "Срок: 19.07.2026 19:00",
            notifications.Single(notification => notification.RecipientUserId == recipients[1].Id).BodyPreview);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskCancelled, notification.Type);
            Assert.Equal("task", notification.ObjectType);
            Assert.Equal(task.Id, notification.ObjectId);
            Assert.Null(notification.TaskJournalItemId);
        });
    }

    [Fact]
    public async Task NotifyCalendarAccessChangedAsync_NotifiesActiveUsersInRole()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "calendar-access-admin@example.com");
        var role = new Role { Name = "calendar-secretary", Description = "Calendar role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var recipient = new User
        {
            Email = "calendar-access-recipient@example.com",
            FullName = "Секретарь",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var inactive = new User
        {
            Email = "calendar-access-inactive@example.com",
            FullName = "Неактивный",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = false,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var added = new User
        {
            Email = "calendar-added@example.com",
            FullName = "Добавленный календарь",
            PasswordHash = "hash",
            RoleId = admin.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var removed = new User
        {
            Email = "calendar-removed@example.com",
            FullName = "Убранный календарь",
            PasswordHash = "hash",
            RoleId = admin.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(recipient, inactive, added, removed);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyCalendarAccessChangedAsync(
            role.Id,
            [added.Id],
            [removed.Id],
            admin.FullName,
            admin.Id);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CalendarAccessChanged, notification.Type);
        Assert.Equal("calendar", notification.ObjectType);
        Assert.Equal(role.Id, notification.ObjectId);
        Assert.Contains("Добавленный календарь", notification.BodyPreview);
        Assert.Contains("Убранный календарь", notification.BodyPreview);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyExternalCalendarSyncWarningAsync_NotifiesAdminsOnlyAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var adminRole = new Role { Name = "admin", Description = "Admin" };
        var userRole = new Role { Name = "user", Description = "User" };
        db.Roles.AddRange(adminRole, userRole);
        await db.SaveChangesAsync();
        var admin = new User
        {
            Email = "calendar-sync-admin@example.com",
            FullName = "Администратор",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var employee = new User
        {
            Email = "calendar-sync-employee@example.com",
            FullName = "Сотрудник",
            PasswordHash = "hash",
            RoleId = userRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(admin, employee);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyExternalCalendarSyncWarningAsync(
            "Google Calendar",
            "Фоновая синхронизация",
            "Синхронизация завершилась с ошибками: 2.",
            "Подключений обработано: 3.");
        await service.NotifyExternalCalendarSyncWarningAsync(
            "Google Calendar",
            "Фоновая синхронизация",
            "Синхронизация завершилась с ошибками: 2.",
            "Подключений обработано: 3.");

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(admin.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.SystemAdminWarning, notification.Type);
        Assert.Equal("system", notification.ObjectType);
        Assert.Null(notification.ObjectId);
        Assert.Contains("Google Calendar", notification.Title);
        Assert.Contains("Синхронизация завершилась с ошибками", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == employee.Id);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyStickyNoteMentionAsync_CreatesNoteNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "note-mention-actor@example.com");
        var recipient = new User
        {
            Email = "note-mention-recipient@example.com",
            FullName = "note-mention-recipient@example.com",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var note = await CreateStickyNoteAsync(db, actor, StickyNoteBoardVisibility.Shared);
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyStickyNoteMentionAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "@note-mention-recipient проверь документ",
            actor.FullName,
            actor.Id,
            [recipient.Id, actor.Id]);
        await service.NotifyStickyNoteMentionAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "@note-mention-recipient проверь документ",
            actor.FullName,
            actor.Id,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteMention, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(note.Id, notification.ObjectId);
        Assert.Null(notification.TaskJournalItemId);
        Assert.Contains("Общее", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == actor.Id);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyStickyNoteAssignedAsync_CreatesNoteNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "note-assigned-actor@example.com");
        var recipient = new User
        {
            Email = "note-assigned-recipient@example.com",
            FullName = "Ответственный",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var note = await CreateStickyNoteAsync(db, actor, StickyNoteBoardVisibility.Shared);
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);
        var reminderAt = new DateTime(2026, 6, 17, 9, 0, 0, DateTimeKind.Utc);

        await service.NotifyStickyNoteAssignedAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "Подготовить звонок",
            recipient.Id,
            actor.FullName,
            actor.Id,
            reminderAt);
        await service.NotifyStickyNoteAssignedAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "Подготовить звонок",
            recipient.Id,
            actor.FullName,
            actor.Id,
            reminderAt);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteAssigned, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(note.Id, notification.ObjectId);
        Assert.Contains("Общее", notification.BodyPreview);
        Assert.Contains("Подготовить звонок", notification.BodyPreview);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyStickyNoteReminderAsync_CreatesNoteNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateUserAsync(db, "note-reminder-assignee@example.com");
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork"
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Проверить оплату",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
            AssignedUserId = assignee.Id,
            AssignedUserName = assignee.FullName,
            AssignedUser = assignee,
            ReminderAt = new DateTime(2026, 6, 17, 10, 30, 0, DateTimeKind.Utc)
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyStickyNoteReminderAsync(note);
        await service.NotifyStickyNoteReminderAsync(note);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteReminder, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(note.Id, notification.ObjectId);
        Assert.Contains("Проверить оплату", notification.BodyPreview);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyStickyNoteImportantChangeAsync_CreatesNotificationsAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "note-important-actor@example.com");
        var recipient = new User
        {
            Email = "note-important-recipient@example.com",
            FullName = "Участник доски",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var note = await CreateStickyNoteAsync(db, actor, StickyNoteBoardVisibility.Shared);
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyStickyNoteImportantChangeAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "Важная информация для всех",
            "pinned",
            actor.FullName,
            actor.Id,
            [recipient.Id, actor.Id],
            2);
        await service.NotifyStickyNoteImportantChangeAsync(
            note.Id,
            note.BoardId,
            "Общее",
            "Важная информация для всех",
            "pinned",
            actor.FullName,
            actor.Id,
            [recipient.Id],
            2);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteImportantChanged, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(note.Id, notification.ObjectId);
        Assert.Contains("Общее", notification.BodyPreview);
        Assert.Contains("закрепил", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == actor.Id);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyPaymentAssignedAsync_CreatesPaymentNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "payment-actor@example.com");
        var recipient = new User
        {
            Email = "payment-recipient@example.com",
            FullName = "Ответственный по платежу",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Description = "Оплата по счету",
            Date = new DateTime(2026, 6, 17),
            Amount = 15000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Responsibles = { new PaymentResponsible { UserId = recipient.Id } }
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyPaymentAssignedAsync(payment, actor.FullName, actor.Id, [recipient.Id, actor.Id]);
        await service.NotifyPaymentAssignedAsync(payment, actor.FullName, actor.Id, [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.PaymentAssigned, notification.Type);
        Assert.Equal("payment", notification.ObjectType);
        Assert.Equal(payment.Id, notification.ObjectId);
        Assert.Contains("Оплата по счету", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == actor.Id);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyPaymentAssignedAsync_ReusesVisibilityCheckForAllRecipients()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "payment-query-actor@example.com",
            FullName = "Автор платежа",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"payment-query-recipient-{index}@example.com",
                FullName = $"Получатель платежа {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Description = "Платеж с несколькими получателями",
            Date = new DateTime(2026, 7, 19),
            Amount = 12000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Responsibles = { new PaymentResponsible { UserId = recipients[0].Id } },
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        await service.NotifyPaymentAssignedAsync(
            payment,
            actor.FullName,
            actor.Id,
            recipients.Select(user => user.Id).ToArray());

        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Equal(
            recipients.Select(user => user.Id).Order(),
            notifications.Select(notification => notification.RecipientUserId).Order());
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("SELECT EXISTS", StringComparison.Ordinal)
            && command.Contains("FROM \"Payments\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyPaymentAssignedAsync_DoesNotShareVisibilityAcrossDifferentScopes()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        var ownRole = new Role
        {
            Name = "payment-own-viewer",
            Description = "Платежи только ответственного",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.AddRange(adminRole, ownRole);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = ownRole.Id,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });
        var actor = new User
        {
            Email = "payment-scope-actor@example.com",
            FullName = "Автор платежа с разными областями",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var owner = new User
        {
            Email = "payment-scope-owner@example.com",
            FullName = "Ответственный за платеж",
            PasswordHash = "hash",
            RoleId = ownRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var other = new User
        {
            Email = "payment-scope-other@example.com",
            FullName = "Другой сотрудник",
            PasswordHash = "hash",
            RoleId = ownRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(actor, owner, other);
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Description = "Платеж с разными областями видимости",
            Date = new DateTime(2026, 7, 19),
            Amount = 12000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Responsibles = { new PaymentResponsible { UserId = owner.Id } },
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        await service.NotifyPaymentAssignedAsync(
            payment,
            actor.FullName,
            actor.Id,
            [owner.Id, other.Id]);

        var notification = await db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(owner.Id, notification.RecipientUserId);
        Assert.Equal(2, interceptor.ReaderCommands.Count(command =>
            command.Contains("SELECT EXISTS", StringComparison.Ordinal)
            && command.Contains("FROM \"Payments\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NotifyPaymentChangedAsync_CreatesPaymentNotificationAndDeduplicatesByState()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "payment-change-actor@example.com");
        var recipient = new User
        {
            Email = "payment-change-recipient@example.com",
            FullName = "Ответственный по изменению платежа",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Description = "Контроль оплаты",
            Date = new DateTime(2026, 6, 18),
            Amount = 42000m,
            Status = PaymentStatus.Overdue,
            Type = PaymentType.Income,
            Responsibles = { new PaymentResponsible { UserId = recipient.Id } }
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyPaymentChangedAsync(
            payment,
            ["Статус: Ожидается -> Просрочен", "Сумма: 40 000,00 -> 42 000,00"],
            actor.FullName,
            actor.Id,
            [recipient.Id, actor.Id]);
        await service.NotifyPaymentChangedAsync(
            payment,
            ["Статус: Ожидается -> Просрочен", "Сумма: 40 000,00 -> 42 000,00"],
            actor.FullName,
            actor.Id,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.PaymentChanged, notification.Type);
        Assert.Equal("payment", notification.ObjectType);
        Assert.Equal(payment.Id, notification.ObjectId);
        Assert.Contains("Просрочен", notification.BodyPreview);
        Assert.Contains("42 000", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyInvoiceCreatedAsync_CreatesInvoiceNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "invoice-actor@example.com");
        var recipient = new User
        {
            Email = "invoice-recipient@example.com",
            FullName = "Ответственный по счету",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Account = "Счет №45",
            Date = new DateTime(2026, 6, 19),
            Amount = 38000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            Responsibles = { new PaymentResponsible { UserId = recipient.Id } },
            Invoice = new Invoice
            {
                Number = "45",
                Date = new DateTime(2026, 6, 19)
            }
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyInvoiceCreatedAsync(payment, actor.FullName, actor.Id, [recipient.Id, actor.Id]);
        await service.NotifyInvoiceCreatedAsync(payment, actor.FullName, actor.Id, [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.InvoiceCreated, notification.Type);
        Assert.Equal("invoice", notification.ObjectType);
        Assert.Equal(payment.Id, notification.ObjectId);
        Assert.Contains("45", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == actor.Id);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyInvoiceCreatedAsync_ReusesVisibilityCheckForAllRecipients()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "invoice-query-actor@example.com",
            FullName = "Автор счета",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"invoice-query-recipient-{index}@example.com",
                FullName = $"Получатель счета {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Account = "Счет №QUERY-12",
            Date = new DateTime(2026, 7, 19),
            Amount = 12000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            Responsibles = { new PaymentResponsible { UserId = recipients[0].Id } },
            Invoice = new Invoice
            {
                Number = "QUERY-12",
                Date = new DateTime(2026, 7, 19),
            },
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        await service.NotifyInvoiceCreatedAsync(
            payment,
            actor.FullName,
            actor.Id,
            recipients.Select(user => user.Id).ToArray());

        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Equal(
            recipients.Select(user => user.Id).Order(),
            notifications.Select(notification => notification.RecipientUserId).Order());
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("SELECT EXISTS", StringComparison.Ordinal)
            && command.Contains("FROM \"Payments\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyInvoiceChangedAsync_CreatesInvoiceNotificationAndDeduplicatesByState()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "invoice-change-actor@example.com");
        var recipient = new User
        {
            Email = "invoice-change-recipient@example.com",
            FullName = "Ответственный по изменению счета",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            Account = "Счет №46",
            Date = new DateTime(2026, 6, 20),
            Amount = 56000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.PartiallyPaid,
            Responsibles = { new PaymentResponsible { UserId = recipient.Id } },
            Invoice = new Invoice
            {
                Number = "46",
                Date = new DateTime(2026, 6, 20)
            }
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyInvoiceChangedAsync(
            payment,
            ["Статус счета: Черновик -> Частично оплачен", "Оплачено: 0,00 -> 12 000,00"],
            actor.FullName,
            actor.Id,
            [recipient.Id, actor.Id]);
        await service.NotifyInvoiceChangedAsync(
            payment,
            ["Статус счета: Черновик -> Частично оплачен", "Оплачено: 0,00 -> 12 000,00"],
            actor.FullName,
            actor.Id,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.InvoiceChanged, notification.Type);
        Assert.Equal("invoice", notification.ObjectType);
        Assert.Equal(payment.Id, notification.ObjectId);
        Assert.Contains("Частично оплачен", notification.BodyPreview);
        Assert.Contains("12 000", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyActCreatedAsync_CreatesActNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "act-actor@example.com");
        var recipient = new User
        {
            Email = "act-recipient@example.com",
            FullName = "Ответственный по акту",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var act = new Act
        {
            Number = "A-45",
            Title = "Акт юридических услуг",
            Date = new DateTime(2026, 6, 21),
            Amount = 24000m,
            Status = ActStatus.Created,
            WorkflowStatus = ActWorkflowStatus.Draft,
            ResponsibleId = recipient.Id
        };
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyActCreatedAsync(act, actor.FullName, actor.Id, [recipient.Id, actor.Id]);
        await service.NotifyActCreatedAsync(act, actor.FullName, actor.Id, [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.ActCreated, notification.Type);
        Assert.Equal("act", notification.ObjectType);
        Assert.Equal(act.Id, notification.ObjectId);
        Assert.Contains("A-45", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == actor.Id);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyActCreatedAsync_ReusesVisibilityCheckForAllRecipients()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "act-query-actor@example.com",
            FullName = "Автор акта",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"act-query-recipient-{index}@example.com",
                FullName = $"Получатель акта {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        var act = new Act
        {
            Number = "A-QUERY-12",
            Title = "Акт с несколькими получателями",
            Date = new DateTime(2026, 7, 19),
            Amount = 12000m,
            Status = ActStatus.Created,
            WorkflowStatus = ActWorkflowStatus.Draft,
            ResponsibleId = recipients[0].Id,
        };
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        await service.NotifyActCreatedAsync(
            act,
            actor.FullName,
            actor.Id,
            recipients.Select(user => user.Id).ToArray());

        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Equal(
            recipients.Select(user => user.Id).Order(),
            notifications.Select(notification => notification.RecipientUserId).Order());
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("SELECT EXISTS", StringComparison.Ordinal)
            && command.Contains("FROM \"Acts\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyActChangedAsync_CreatesActNotificationAndDeduplicatesByState()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "act-change-actor@example.com");
        var recipient = new User
        {
            Email = "act-change-recipient@example.com",
            FullName = "Ответственный по изменению акта",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var act = new Act
        {
            Number = "A-46",
            Title = "Акт проверки",
            Date = new DateTime(2026, 6, 22),
            Amount = 32000m,
            Status = ActStatus.Signed,
            WorkflowStatus = ActWorkflowStatus.Signed,
            ResponsibleId = recipient.Id
        };
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyActChangedAsync(
            act,
            ["Состояние документа: Отправлен на подпись -> Подписан", "Статус акта: Передан -> Подписан"],
            actor.FullName,
            actor.Id,
            [recipient.Id, actor.Id]);
        await service.NotifyActChangedAsync(
            act,
            ["Состояние документа: Отправлен на подпись -> Подписан", "Статус акта: Передан -> Подписан"],
            actor.FullName,
            actor.Id,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.ActChanged, notification.Type);
        Assert.Equal("act", notification.ObjectType);
        Assert.Equal(act.Id, notification.ObjectId);
        Assert.Contains("Подписан", notification.BodyPreview);
        Assert.Contains("A-46", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyContractCreatedAsync_CreatesContractNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "contract-actor@example.com");
        var recipient = new User
        {
            Email = "contract-recipient@example.com",
            FullName = "Ответственный по договору",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Клиент договора",
            IsActive = true,
            ResponsibleUserId = recipient.Id
        };
        var contract = new Contract
        {
            Number = "D-45",
            Title = "Договор сопровождения",
            Date = new DateTime(2026, 6, 23),
            StartDate = new DateTime(2026, 6, 24),
            ValidUntil = new DateTime(2026, 12, 31),
            Amount = 120000m,
            Status = ContractStatus.Draft,
            ClientContracts = { new ClientContract { Client = client } }
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyContractCreatedAsync(contract, actor.FullName, actor.Id, [recipient.Id, actor.Id]);
        await service.NotifyContractCreatedAsync(contract, actor.FullName, actor.Id, [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.ContractCreated, notification.Type);
        Assert.Equal("contract", notification.ObjectType);
        Assert.Equal(contract.Id, notification.ObjectId);
        Assert.Contains("D-45", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == actor.Id);
        var realtimeNotification = Assert.Single(realtime.Notifications);
        Assert.Equal(notification.Id, realtimeNotification.Id);
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(notification.Id, pushed.Id);
    }

    [Fact]
    public async Task NotifyContractChangedAsync_CreatesContractNotificationAndDeduplicatesByState()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "contract-change-actor@example.com");
        var recipient = new User
        {
            Email = "contract-change-recipient@example.com",
            FullName = "Ответственный по изменению договора",
            PasswordHash = "hash",
            RoleId = actor.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(recipient);
        await db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Клиент договора проверки",
            IsActive = true,
            ResponsibleUserId = recipient.Id
        };
        var contract = new Contract
        {
            Number = "D-46",
            Title = "Договор проверки",
            Date = new DateTime(2026, 6, 24),
            ValidUntil = new DateTime(2026, 11, 30),
            Amount = 90000m,
            Status = ContractStatus.Concluded,
            ClientContracts = { new ClientContract { Client = client } }
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyContractChangedAsync(
            contract,
            ["Статус договора: Отправлен на подпись -> Заключен", "Срок действия: 31.10.2026 -> 30.11.2026"],
            actor.FullName,
            actor.Id,
            [recipient.Id, actor.Id]);
        await service.NotifyContractChangedAsync(
            contract,
            ["Статус договора: Отправлен на подпись -> Заключен", "Срок действия: 31.10.2026 -> 30.11.2026"],
            actor.FullName,
            actor.Id,
            [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.ContractChanged, notification.Type);
        Assert.Equal("contract", notification.ObjectType);
        Assert.Equal(contract.Id, notification.ObjectId);
        Assert.Contains("Заключен", notification.BodyPreview);
        Assert.Contains("D-46", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyContractExpiringAsync_CreatesContractNotificationAndDeduplicates()
    {
        await using var db = await CreateContextAsync();
        var recipient = await CreateUserAsync(db, "contract-expiring-recipient@example.com");
        var client = new Client
        {
            Name = "Клиент срочного договора",
            IsActive = true,
            ResponsibleUserId = recipient.Id
        };
        var contract = new Contract
        {
            Number = "D-47",
            Title = "Срочный договор",
            Date = new DateTime(2026, 6, 24),
            ValidUntil = DateTime.UtcNow.Date.AddDays(3),
            Amount = 45000m,
            Status = ContractStatus.Concluded,
            ClientContracts = { new ClientContract { Client = client } }
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyContractExpiringAsync(contract, 3, [recipient.Id]);
        await service.NotifyContractExpiringAsync(contract, 3, [recipient.Id]);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.ContractExpiring, notification.Type);
        Assert.Equal("contract", notification.ObjectType);
        Assert.Equal(contract.Id, notification.ObjectId);
        Assert.Contains("Осталось дней: 3", notification.BodyPreview);
        Assert.Contains("D-47", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyContractExpiringAsync_ReusesVisibilityCheckForAllRecipients()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"contract-expiring-query-recipient-{index}@example.com",
                FullName = $"Получатель напоминания о договоре {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients);
        await db.SaveChangesAsync();
        var contract = new Contract
        {
            Number = "D-EXPIRING-QUERY-12",
            Title = "Договор с несколькими получателями напоминания",
            Date = new DateTime(2026, 7, 19),
            ValidUntil = new DateTime(2026, 7, 22),
            Amount = 12000m,
            Status = ContractStatus.Concluded,
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        await service.NotifyContractExpiringAsync(
            contract,
            3,
            recipients.Select(user => user.Id).ToArray());

        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.Equal(
            recipients.Select(user => user.Id).Order(),
            notifications.Select(notification => notification.RecipientUserId).Order());
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("SELECT EXISTS", StringComparison.Ordinal)
            && command.Contains("FROM \"Contracts\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyContractExpiringAsync_SkipsRecipientWithoutContractsAccess()
    {
        await using var db = await CreateContextAsync();
        var deniedRecipient = await CreateUserWithoutSectionAccessAsync(
            db,
            "contract-expiring-denied-recipient@example.com",
            RolePermissionCatalog.Contracts);
        var client = new Client
        {
            Name = "Клиент скрытого истекающего договора",
            IsActive = true,
            ResponsibleUserId = deniedRecipient.Id,
        };
        var contract = new Contract
        {
            Number = "D-EXPIRING-DENIED",
            Title = "Скрытый истекающий договор",
            Date = new DateTime(2026, 7, 19),
            ValidUntil = new DateTime(2026, 7, 22),
            Amount = 12000m,
            Status = ContractStatus.Concluded,
            ClientContracts = { new ClientContract { Client = client } },
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            realtime,
            webPush);

        await service.NotifyContractExpiringAsync(contract, 3, [deniedRecipient.Id]);

        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(realtime.Notifications);
        Assert.Empty(webPush.Notifications);
    }

    [Fact]
    public async Task NotifyPaymentAssignedAsync_SkipsRecipientWithoutPaymentsAccess()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "payment-denied-actor@example.com");
        var deniedRecipient = await CreateUserWithoutSectionAccessAsync(db, "payment-denied-recipient@example.com", RolePermissionCatalog.Payments);
        var payment = new Payment
        {
            Description = "Платеж со скрытыми правами",
            Date = new DateTime(2026, 6, 25),
            Amount = 77777m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Responsibles = { new PaymentResponsible { UserId = deniedRecipient.Id } }
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var realtime = new RecordingRealtimeNotificationPublisher();
        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime, webPush);

        await service.NotifyPaymentAssignedAsync(payment, actor.FullName, actor.Id, [deniedRecipient.Id]);

        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(realtime.Notifications);
        Assert.Empty(webPush.Notifications);
    }

    [Fact]
    public async Task NotifyInvoiceCreatedAsync_SkipsRecipientWithoutAccountsAccess()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "invoice-denied-actor@example.com");
        var deniedRecipient = await CreateUserWithoutSectionAccessAsync(db, "invoice-denied-recipient@example.com", RolePermissionCatalog.Accounts);
        var payment = new Payment
        {
            Account = "Счет №77",
            Date = new DateTime(2026, 6, 25),
            Amount = 88000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            Responsibles = { new PaymentResponsible { UserId = deniedRecipient.Id } },
            Invoice = new Invoice
            {
                Number = "77",
                Date = new DateTime(2026, 6, 25)
            }
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyInvoiceCreatedAsync(payment, actor.FullName, actor.Id, [deniedRecipient.Id]);

        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task NotifyActCreatedAsync_SkipsRecipientWithoutActsAccess()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "act-denied-actor@example.com");
        var deniedRecipient = await CreateUserWithoutSectionAccessAsync(db, "act-denied-recipient@example.com", RolePermissionCatalog.Acts);
        var act = new Act
        {
            Number = "A-77",
            Title = "Скрытый акт",
            Date = new DateTime(2026, 6, 25),
            Amount = 99000m,
            Status = ActStatus.Created,
            WorkflowStatus = ActWorkflowStatus.Draft,
            ResponsibleId = deniedRecipient.Id
        };
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyActCreatedAsync(act, actor.FullName, actor.Id, [deniedRecipient.Id]);

        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task NotifyContractCreatedAsync_SkipsRecipientWithoutContractsAccess()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "contract-denied-actor@example.com");
        var deniedRecipient = await CreateUserWithoutSectionAccessAsync(db, "contract-denied-recipient@example.com", RolePermissionCatalog.Contracts);
        var client = new Client
        {
            Name = "Клиент скрытого договора",
            IsActive = true,
            ResponsibleUserId = deniedRecipient.Id
        };
        var contract = new Contract
        {
            Number = "D-77",
            Title = "Скрытый договор",
            Date = new DateTime(2026, 6, 25),
            ValidUntil = new DateTime(2026, 12, 25),
            Amount = 100000m,
            Status = ContractStatus.Draft,
            ClientContracts = { new ClientContract { Client = client } }
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyContractCreatedAsync(contract, actor.FullName, actor.Id, [deniedRecipient.Id]);

        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task NotifyTaskApprovalRequestedAsync_CreatesInAppNotificationForApproverAndAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "approval-author@example.com");
        var assignee = new User
        {
            Email = "approval-assignee@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var approver = new User
        {
            Email = "approver@example.com",
            FullName = "Согласующий",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(assignee, approver);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "ЕГОРОВ - спор",
            Title = "Проверить заявление",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "onApproval",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = approver.Id,
                    DisplayName = approver.FullName,
                    Role = "approver",
                    ApprovalStatus = "pending",
                    SortOrder = 0
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskApprovalRequestedAsync(task, author.FullName, author.Id);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications
            .OrderBy(item => item.RecipientUserId)
            .ToListAsync();
        Assert.Equal(
            new[] { assignee.Id, approver.Id }.OrderBy(id => id).ToArray(),
            notifications.Select(item => item.RecipientUserId).OrderBy(id => id).ToArray());
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskApprovalRequested, notification.Type);
            Assert.Contains("ожидает согласования", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Проверить заявление", notification.BodyPreview);
        });
    }

    [Fact]
    public async Task NotifyTaskApprovalRequestedAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var actor = new User
        {
            Email = "approval-request-timezone-query-actor@example.com",
            FullName = "Автор согласования",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"approval-request-timezone-query-recipient-{index}@example.com",
                FullName = $"Участник согласования {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(actor));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.TaskApprovalRequested,
                        isEnabled = true,
                        subject = "Задача ожидает согласования: {{task.title}}",
                        body = "Срок: {{task.dueAt}}\nСогласующий: {{approval.approverName}}",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var approver = new TaskJournalParticipant
        {
            UserId = recipients[11].Id,
            User = recipients[11],
            DisplayName = recipients[11].FullName,
            Role = "approver",
            ApprovalStatus = "pending",
            SortOrder = 11,
        };
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Согласовать документы",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            AssigneeUserId = recipients[0].Id,
            AssigneeName = recipients[0].FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "onApproval",
            Participants = recipients
                .Skip(1)
                .Take(10)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .Append(new TaskJournalParticipant
                {
                    UserId = recipients[0].Id,
                    DisplayName = recipients[0].FullName,
                    Role = "observer",
                    SortOrder = 10,
                })
                .Append(approver)
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskApprovalRequestedAsync(task, actor.FullName, actor.Id);

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.DoesNotContain(notifications, notification => notification.RecipientUserId == actor.Id);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        var utcNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Срок: 19.07.2026 12:00", utcNotification.BodyPreview);
        var novosibirskNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Срок: 19.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskApprovalRequested, notification.Type);
            Assert.Contains($"\"currentApproverId\":{approver.Id}", notification.MetadataJson);
        });
    }

    [Fact]
    public async Task NotifyTaskApprovalDecisionAsync_CreatesRevisionNotificationForAssignee()
    {
        await using var db = await CreateContextAsync();
        var roleUser = await CreateUserAsync(db, "approval-role@example.com");
        var assignee = new User
        {
            Email = "revision-assignee@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = roleUser.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var approver = new User
        {
            Email = "revision-approver@example.com",
            FullName = "Согласующий",
            PasswordHash = "hash",
            RoleId = roleUser.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(assignee, approver);
        await db.SaveChangesAsync();

        var participant = new TaskJournalParticipant
        {
            UserId = approver.Id,
            DisplayName = approver.FullName,
            Role = "approver",
            ApprovalStatus = "revisionRequested",
            SortOrder = 0
        };
        var task = new TaskJournalItem
        {
            ObjectName = "ЕГОРОВ - спор",
            Title = "Проверить результат",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "revision",
            Result = "Исполнитель подготовил документы.",
            Participants = [participant]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        var deliveries = await service.NotifyTaskApprovalDecisionAsync(
            task,
            participant,
            "revisionRequested",
            approver.FullName,
            approver.Id,
            "Нужно исправить результат");

        Assert.Empty(deliveries);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskRevisionRequested, notification.Type);
        Assert.Contains("доработку", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Исполнитель подготовил документы.", notification.BodyPreview);
        Assert.Contains("Нужно исправить результат", notification.BodyPreview);
    }

    [Fact]
    public async Task NotifyTaskApprovalDecisionAsync_CreatesApprovedNotificationForTaskStakeholders()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "approval-approved-author@example.com");
        var assignee = new User
        {
            Email = "approval-approved-assignee@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var approver = new User
        {
            Email = "approval-approved-approver@example.com",
            FullName = "Согласующий",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var observer = new User
        {
            Email = "approval-approved-observer@example.com",
            FullName = "Наблюдатель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(assignee, approver, observer);
        await db.SaveChangesAsync();

        var participant = new TaskJournalParticipant
        {
            UserId = approver.Id,
            DisplayName = approver.FullName,
            Role = "approver",
            ApprovalStatus = "approved",
            SortOrder = 0
        };
        var task = new TaskJournalItem
        {
            ObjectName = "ЕГОРОВ - спор",
            Title = "Согласовать результат",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "approved",
            Result = "Задача выполнена и результат приложен.",
            Participants =
            [
                participant,
                new TaskJournalParticipant
                {
                    UserId = observer.Id,
                    DisplayName = observer.FullName,
                    Role = "observer",
                    SortOrder = 1
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var realtime = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);

        var deliveries = await service.NotifyTaskApprovalDecisionAsync(
            task,
            participant,
            "approved",
            approver.FullName,
            approver.Id,
            "Можно закрывать");

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications
            .OrderBy(item => item.RecipientUserId)
            .ToListAsync();
        Assert.Equal([author.Id, assignee.Id, observer.Id], notifications.Select(item => item.RecipientUserId).ToArray());
        Assert.DoesNotContain(notifications, item => item.RecipientUserId == approver.Id);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskApproved, notification.Type);
            Assert.Equal("emerald", notification.Tone);
            Assert.Contains("согласована", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Задача выполнена и результат приложен.", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Можно закрывать", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            notifications.Select(item => item.Id).OrderBy(id => id),
            realtime.Notifications.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task NotifyTaskApprovalDecisionAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var approverUser = new User
        {
            Email = "approval-decision-timezone-query-approver@example.com",
            FullName = "Согласующий",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"approval-decision-timezone-query-recipient-{index}@example.com",
                FullName = $"Участник решения {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients.Append(approverUser));
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.TaskApproved,
                        isEnabled = true,
                        subject = "Задача согласована: {{task.title}}",
                        body = "Срок: {{task.dueAt}}\nРешение: {{approval.resolution}}",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var dueAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var approver = new TaskJournalParticipant
        {
            UserId = approverUser.Id,
            DisplayName = approverUser.FullName,
            Role = "approver",
            ApprovalStatus = "approved",
            SortOrder = 12,
        };
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Завершить согласование",
            AuthorUserId = recipients[0].Id,
            AuthorName = recipients[0].FullName,
            AssigneeUserId = recipients[1].Id,
            AssigneeName = recipients[1].FullName,
            DueAt = dueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "done",
            Result = "Документы проверены.",
            Participants = recipients
                .Skip(2)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .Append(new TaskJournalParticipant
                {
                    UserId = recipients[1].Id,
                    DisplayName = recipients[1].FullName,
                    Role = "observer",
                    SortOrder = 10,
                })
                .Append(approver)
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var deliveries = await service.NotifyTaskApprovalDecisionAsync(
            task,
            approver,
            "approved",
            approverUser.FullName,
            approverUser.Id,
            "Можно завершать задачу");

        Assert.Empty(deliveries);
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        Assert.DoesNotContain(notifications, notification => notification.RecipientUserId == approverUser.Id);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(UserNotificationDoNotDisturbSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal)
            && command.Contains("\"RoleId\"", StringComparison.Ordinal)
            && command.Contains("\"RoleName\"", StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
            && command.Contains(RoleNotificationPreferenceSettings.SettingKey, StringComparison.Ordinal));
        var utcNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Срок: 19.07.2026 12:00", utcNotification.BodyPreview);
        var novosibirskNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Срок: 19.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskApproved, notification.Type);
            Assert.Contains("Можно завершать задачу", notification.BodyPreview);
            Assert.Contains("\"decision\":\"approved\"", notification.MetadataJson);
            Assert.Contains("\"completed\":true", notification.MetadataJson);
        });
    }

    [Fact]
    public async Task NotifyTaskApprovalRequestedAsync_SendsWebPushForCurrentApprover()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "approval-request-web-push-author@example.com");
        var approver = new User
        {
            Email = "approval-request-web-push-approver@example.com",
            FullName = "Web Push Approver",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(approver);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Client case",
            Title = "Approve with push",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            DueAt = DateTime.UtcNow.AddHours(4),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "onApproval",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = approver.Id,
                    DisplayName = approver.FullName,
                    Role = "approver",
                    ApprovalStatus = "pending",
                    SortOrder = 0
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyTaskApprovalRequestedAsync(task, author.FullName, author.Id);

        var notification = await db.UserNotifications.SingleAsync();
        var pushed = Assert.Single(webPush.Notifications);
        Assert.Equal(approver.Id, notification.RecipientUserId);
        Assert.Equal(notification.Id, pushed.Id);
        Assert.Equal(UserNotificationTypes.TaskApprovalRequested, pushed.Type);
    }

    [Fact]
    public async Task NotifyTaskApprovalOverdueAsync_NotifiesRecipientsOncePerLocalDayAndSendsWebPush()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "approval-overdue-author@example.com");
        var assignee = new User
        {
            Email = "approval-overdue-assignee@example.com",
            FullName = "Исполнитель",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var approver = new User
        {
            Email = "approval-overdue-approver@example.com",
            FullName = "Согласующий",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(assignee, approver);
        await db.SaveChangesAsync();

        var nowUtc = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            ObjectName = "ЕГОРОВ - спор",
            Title = "Просроченное согласование",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = nowUtc.AddDays(3),
            ApprovalDueAt = nowUtc.AddHours(-2),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "onApproval",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = approver.Id,
                    DisplayName = approver.FullName,
                    Role = "approver",
                    ApprovalStatus = "pending",
                    SortOrder = 0
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var webPush = new RecordingWebPushNotificationService();
        var service = CreateService(
            db,
            new RecordingEmailService(),
            new RecordingTelegramService(),
            webPushNotificationService: webPush);

        await service.NotifyTaskApprovalOverdueAsync(task, nowUtc);
        await service.NotifyTaskApprovalOverdueAsync(task, nowUtc.AddMinutes(5));

        var notifications = await db.UserNotifications
            .OrderBy(item => item.RecipientUserId)
            .ToListAsync();
        Assert.Equal([author.Id, assignee.Id, approver.Id], notifications.Select(item => item.RecipientUserId).ToArray());
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskApprovalOverdue, notification.Type);
            Assert.Equal("rose", notification.Tone);
            Assert.Contains("Просрочено согласование", notification.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Срок согласования", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            notifications.Select(item => item.Id).OrderBy(id => id),
            webPush.Notifications.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task NotifyTaskApprovalOverdueAsync_LoadsRecipientTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var adminRole = new Role
        {
            Name = "admin",
            Description = "Администратор",
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var recipients = Enumerable.Range(1, 12)
            .Select(index => new User
            {
                Email = $"approval-overdue-timezone-query-recipient-{index}@example.com",
                FullName = $"Получатель просрочки {index}",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .ToArray();
        db.Users.AddRange(recipients);
        await db.SaveChangesAsync();
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = recipients[0].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "UTC",
            },
            new UserSetting
            {
                UserId = recipients[1].Id,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "notifications.templates.v1",
            Value = JsonSerializer.Serialize(new
            {
                templates = new[]
                {
                    new
                    {
                        key = NotificationTemplateKeys.TaskApprovalOverdue,
                        isEnabled = true,
                        subject = "Просрочено согласование: {{task.title}}",
                        body = "Срок задачи: {{task.dueAt}}\nСрок согласования: {{approval.dueAt}}\nСогласующий: {{approval.approverName}}",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var nowUtc = new DateTime(2026, 7, 19, 20, 30, 0, DateTimeKind.Utc);
        var approvalDueAt = new DateTime(2026, 7, 19, 10, 0, 0, DateTimeKind.Utc);
        var dueAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var currentApprover = new TaskJournalParticipant
        {
            UserId = recipients[11].Id,
            DisplayName = recipients[11].FullName,
            Role = "approver",
            ApprovalStatus = "pending",
            SortOrder = 11,
        };
        var task = new TaskJournalItem
        {
            ObjectName = "Массовая задача",
            Title = "Просроченное массовое согласование",
            AuthorUserId = recipients[0].Id,
            AuthorName = recipients[0].FullName,
            AssigneeUserId = recipients[1].Id,
            AssigneeName = recipients[1].FullName,
            DueAt = dueAt,
            ApprovalDueAt = approvalDueAt,
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "onApproval",
            Participants = recipients
                .Skip(2)
                .Take(9)
                .Select((user, index) => new TaskJournalParticipant
                {
                    UserId = user.Id,
                    DisplayName = user.FullName,
                    Role = "observer",
                    SortOrder = index,
                })
                .Append(new TaskJournalParticipant
                {
                    UserId = recipients[1].Id,
                    DisplayName = recipients[1].FullName,
                    Role = "observer",
                    SortOrder = 9,
                })
                .Append(currentApprover)
                .ToList(),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        interceptor.ReaderCommands.Clear();

        var firstDeliveries = await service.NotifyTaskApprovalOverdueAsync(task, nowUtc);

        Assert.Empty(firstDeliveries);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        var notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(12, notifications.Count);
        var utcNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[0].Id);
        Assert.Contains("Срок задачи: 23.07.2026 12:00", utcNotification.BodyPreview);
        Assert.Contains("Срок согласования: 19.07.2026 10:00", utcNotification.BodyPreview);
        Assert.EndsWith(":20260719", utcNotification.EventKey, StringComparison.Ordinal);
        var novosibirskNotification = notifications.Single(
            notification => notification.RecipientUserId == recipients[1].Id);
        Assert.Contains("Срок задачи: 23.07.2026 19:00", novosibirskNotification.BodyPreview);
        Assert.Contains("Срок согласования: 19.07.2026 17:00", novosibirskNotification.BodyPreview);
        Assert.EndsWith(":20260720", novosibirskNotification.EventKey, StringComparison.Ordinal);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(UserNotificationTypes.TaskApprovalOverdue, notification.Type);
            Assert.Contains($"\"currentApproverId\":{currentApprover.Id}", notification.MetadataJson);
        });
        interceptor.ReaderCommands.Clear();

        var repeatedDeliveries = await service.NotifyTaskApprovalOverdueAsync(task, nowUtc.AddMinutes(5));

        Assert.Empty(repeatedDeliveries);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal)
            && command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Equal(12, await db.UserNotifications.CountAsync());
    }

    [Fact]
    public async Task SendToUserAsync_SendsThroughAllMatchingSendersForPurpose()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "multi@example.com");
        db.NotificationSenders.AddRange(
            CreateEmailSender("Основная почта", "main@example.com", NotificationPurposes.PasswordIssue, 10),
            CreateEmailSender("Резервная почта", "backup@example.com", NotificationPurposes.PasswordIssue, 20));
        await db.SaveChangesAsync();

        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var deliveries = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("Пароль", "Новый пароль", Purpose: NotificationPurposes.PasswordIssue),
            [NotificationChannels.Email],
            "password:test:multi");

        Assert.Equal(2, deliveries.Count);
        Assert.Equal(2, emailService.Messages.Count);
        Assert.Equal(["Основная почта", "Резервная почта"], deliveries.Select(item => item.SenderName).ToArray());
    }

    [Fact]
    public async Task SendToUserAsync_UsesSubscriptionForSpecificTelegramSender()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "telegram@example.com", chatId: 100500);
        var sender = CreateTelegramSender("Бот задач", NotificationPurposes.PasswordIssue);
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();

        var telegramService = new RecordingTelegramService();
        var service = CreateService(db, new RecordingEmailService(), telegramService);

        var skipped = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("Пароль", "Новый пароль", Purpose: NotificationPurposes.PasswordIssue),
            [NotificationChannels.Telegram],
            "telegram:missing-subscription",
            senderId: sender.Id);

        Assert.Single(skipped);
        Assert.Equal(NotificationDeliveryStatus.Skipped, skipped[0].Status);
        Assert.Empty(telegramService.Messages);

        db.NotificationBotSubscriptions.Add(new NotificationBotSubscription
        {
            NotificationSenderId = sender.Id,
            UserId = user.Id,
            TelegramUserId = "12345",
            ChatId = 222333,
            Username = "telegram_user",
            DisplayName = "Telegram User",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sent = await service.SendToUserAsync(
            user.Id,
            new NotificationMessage("Пароль", "Новый пароль", Purpose: NotificationPurposes.PasswordIssue),
            [NotificationChannels.Telegram],
            "telegram:with-subscription",
            senderId: sender.Id);

        Assert.Single(sent);
        Assert.Equal(NotificationDeliveryStatus.Sent, sent[0].Status);
        Assert.Equal(222333, telegramService.Messages.Single().ChatId);
    }

    [Fact]
    public async Task RetryDeliveryAsync_CreatesNewDeliveryAndKeepsOriginalFailed()
    {
        await using var db = await CreateContextAsync();
        await SeedEmailSettingsAsync(db);
        var user = await CreateUserAsync(db, "retry@example.com");
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "manual:failed",
            Channel = NotificationChannels.Email,
            RecipientUserId = user.Id,
            RecipientAddress = user.Email,
            Subject = "Неотправленное уведомление",
            BodyPreview = "Текст для повторной отправки.",
            Status = NotificationDeliveryStatus.Failed,
            Error = "SMTP unavailable",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();
        var original = await db.NotificationDeliveries.SingleAsync();
        var emailService = new RecordingEmailService();
        var service = CreateService(db, emailService, new RecordingTelegramService());

        var deliveries = await service.RetryDeliveryAsync(original.Id);

        var retry = Assert.Single(deliveries);
        Assert.NotEqual(original.Id, retry.Id);
        Assert.Equal(NotificationDeliveryStatus.Sent, retry.Status);
        Assert.StartsWith("manual:failed:retry:", retry.EventKey, StringComparison.Ordinal);
        Assert.Single(emailService.Messages);

        await db.Entry(original).ReloadAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, original.Status);
    }

    [Fact]
    public async Task HandleWebhookAsync_StartCommandLinksUserToTelegramSender()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "webhook@example.com");
        db.UserExternalIdentities.Add(new UserExternalIdentity
        {
            UserId = user.Id,
            Provider = "telegram",
            ProviderUserId = "12345",
            TelegramUserId = "12345",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var sender = CreateTelegramSender(
            "Бот уведомлений",
            NotificationPurposes.TaskReminder,
            updateMode: TelegramBotUpdateModes.Webhook,
            webhookSecret: "secret");
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();

        using var update = JsonDocument.Parse("""
            {
              "update_id": 10,
              "message": {
                "message_id": 1,
                "text": "/start",
                "chat": { "id": 777888 },
                "from": {
                  "id": 12345,
                  "username": "ivan",
                  "first_name": "Иван",
                  "last_name": "Петров"
                }
              }
            }
            """);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "secret";
        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramOkHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance);

        await updateService.HandleWebhookAsync(sender.Id, update, context.Request);

        var subscription = await db.NotificationBotSubscriptions.SingleAsync();
        Assert.Equal(sender.Id, subscription.NotificationSenderId);
        Assert.Equal(user.Id, subscription.UserId);
        Assert.Equal("12345", subscription.TelegramUserId);
        Assert.Equal(777888, subscription.ChatId);

        var identity = await db.UserExternalIdentities.SingleAsync(item => item.UserId == user.Id && item.Provider == "telegram");
        Assert.Equal(777888, identity.ChatId);
        Assert.Equal("@ivan", (await db.Users.FindAsync(user.Id))?.Telegram);
    }

    [Fact]
    public async Task PollEnabledSendersAsync_StartCommandLinksUserToPollingSender()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "polling@example.com");
        db.UserExternalIdentities.Add(new UserExternalIdentity
        {
            UserId = user.Id,
            Provider = "telegram",
            ProviderUserId = "555",
            TelegramUserId = "555",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var sender = CreateTelegramSender(
            "Polling bot",
            NotificationPurposes.TaskReminder,
            updateMode: TelegramBotUpdateModes.Polling);
        var recentPollingAt = DateTime.UtcNow.AddMinutes(-1);
        sender.ConfigJson = JsonSerializer.Serialize(new
        {
            botToken = "token",
            botUsername = "payplanner_bot",
            updateMode = TelegramBotUpdateModes.Polling,
            lastPollingAt = recentPollingAt
        });
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();

        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramPollingHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance);

        await updateService.PollEnabledSendersAsync();

        var subscription = await db.NotificationBotSubscriptions.SingleAsync();
        Assert.Equal(sender.Id, subscription.NotificationSenderId);
        Assert.Equal(user.Id, subscription.UserId);
        Assert.Equal("555", subscription.TelegramUserId);
        Assert.Equal(999000, subscription.ChatId);

        await db.Entry(sender).ReloadAsync();
        using var config = JsonDocument.Parse(sender.ConfigJson);
        Assert.Equal(25, config.RootElement.GetProperty("lastUpdateId").GetInt64());
        Assert.True(config.RootElement.GetProperty("lastPollingAt").GetDateTime() > recentPollingAt);
    }

    [Fact]
    public async Task PollEnabledSendersAsync_RecentEmptyPollDoesNotRewriteSender()
    {
        await using var db = await CreateContextAsync();
        var recentPollingAt = DateTime.UtcNow.AddMinutes(-1);
        var originalUpdatedAt = DateTime.UtcNow.AddHours(-1);
        var sender = CreateTelegramSender(
            "Idle polling bot",
            NotificationPurposes.TaskReminder,
            updateMode: TelegramBotUpdateModes.Polling);
        sender.ConfigJson = JsonSerializer.Serialize(new
        {
            botToken = "token",
            botUsername = "payplanner_bot",
            updateMode = TelegramBotUpdateModes.Polling,
            lastUpdateId = 25,
            lastPollingAt = recentPollingAt
        });
        sender.UpdatedAt = originalUpdatedAt;
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();
        var originalConfig = sender.ConfigJson;
        db.ChangeTracker.Clear();

        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramEmptyPollingHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance);

        await updateService.PollEnabledSendersAsync();

        var persisted = await db.NotificationSenders.AsNoTracking().SingleAsync();
        Assert.Equal(originalConfig, persisted.ConfigJson);
        Assert.Equal(originalUpdatedAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task PollEnabledSendersAsync_StaleEmptyPollRefreshesHeartbeat()
    {
        await using var db = await CreateContextAsync();
        var stalePollingAt = DateTime.UtcNow.AddMinutes(-6);
        var sender = CreateTelegramSender(
            "Idle polling bot",
            NotificationPurposes.TaskReminder,
            updateMode: TelegramBotUpdateModes.Polling);
        sender.ConfigJson = JsonSerializer.Serialize(new
        {
            botToken = "token",
            botUsername = "payplanner_bot",
            updateMode = TelegramBotUpdateModes.Polling,
            lastUpdateId = 25,
            lastPollingAt = stalePollingAt
        });
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();

        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramEmptyPollingHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance);

        await updateService.PollEnabledSendersAsync();

        await db.Entry(sender).ReloadAsync();
        using var config = JsonDocument.Parse(sender.ConfigJson);
        Assert.True(config.RootElement.GetProperty("lastPollingAt").GetDateTime() > stalePollingAt);
        Assert.NotNull(sender.UpdatedAt);
    }

    [Fact]
    public async Task SyncBotProfileAsync_StoresTelegramBotProfile()
    {
        await using var db = await CreateContextAsync();
        var sender = CreateTelegramSender("Profile bot", NotificationPurposes.ManualTest);
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();

        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramProfileHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance);

        var result = await updateService.SyncBotProfileAsync(sender.Id);

        Assert.True(result.Success);
        Assert.NotNull(result.Profile);
        var profile = result.Profile!;
        Assert.Equal(4242, profile.BotId);
        Assert.Equal("payplanner_test_bot", profile.BotUsername);
        Assert.Equal("PayPlanner Bot", profile.Name);
        Assert.Equal("Служебный бот CRM", profile.Description);
        Assert.Equal("Уведомления CRM", profile.ShortDescription);
        Assert.True(profile.HasProfilePhoto);
        Assert.Equal(["start", "status"], profile.Commands.Select(item => item.Command).ToArray());

        await db.Entry(sender).ReloadAsync();
        using var config = JsonDocument.Parse(sender.ConfigJson);
        Assert.Equal("payplanner_test_bot", config.RootElement.GetProperty("botUsername").GetString());
        Assert.Equal("large-photo-file-id", config.RootElement.GetProperty("profilePhotoFileId").GetString());
    }

    [Fact]
    public async Task ApplyBotProfileAsync_RejectsInvalidCommandBeforeTelegramCall()
    {
        await using var db = await CreateContextAsync();
        var sender = CreateTelegramSender("Profile bot", NotificationPurposes.ManualTest);
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();

        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramProfileHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance);

        var result = await updateService.ApplyBotProfileAsync(sender.Id, new()
        {
            Name = "PayPlanner Bot",
            Commands =
            [
                new() { Command = "НевернаяКоманда", Description = "Описание" }
            ]
        });

        Assert.False(result.Success);
        Assert.Contains("Команда Telegram", result.Message);
    }


    [Fact]
    public async Task GetPublicSettingsAsync_UsesNotificationSenderForTelegramLoginBot()
    {
        await using var db = await CreateContextAsync();
        db.NotificationSenders.Add(CreateTelegramSender("Login bot", NotificationPurposes.TelegramAuth));
        await db.SaveChangesAsync();

        var service = CreateExternalAuthService(db);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("crm.example.test");

        var settings = await service.GetPublicSettingsAsync(httpContext.Request);

        Assert.True(settings.TelegramEnabled);
        Assert.Equal("payplanner_bot", settings.TelegramBotUsername);
    }

    [Fact]
    public async Task HandleWebhookAsync_LoginStartCompletesTelegramAuthBeforeBotSubscription()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "link@example.com");
        db.TelegramAuthRequests.Add(new TelegramAuthRequest
        {
            State = "login-state",
            UserId = user.Id,
            Intent = "link",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        });
        var sender = CreateTelegramSender(
            "Login bot",
            NotificationPurposes.TelegramAuth,
            updateMode: TelegramBotUpdateModes.Webhook,
            webhookSecret: "secret");
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();

        using var update = JsonDocument.Parse("""
            {
              "update_id": 11,
              "message": {
                "message_id": 1,
                "text": "/start login_login-state",
                "chat": { "id": 777888 },
                "from": {
                  "id": 12345,
                  "username": "auth_user",
                  "first_name": "Auth",
                  "last_name": "User"
                }
              }
            }
            """);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "secret";
        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramOkHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance,
            CreateExternalAuthService(db, httpFactory));

        await updateService.HandleWebhookAsync(sender.Id, update, context.Request);

        var authRequest = await db.TelegramAuthRequests.SingleAsync();
        Assert.Equal("completed", authRequest.Status);
        Assert.Equal("12345", authRequest.TelegramUserId);
        Assert.Equal(777888, authRequest.ChatId);
        Assert.Empty(await db.NotificationBotSubscriptions.ToListAsync());

        var identity = await db.UserExternalIdentities.SingleAsync(item => item.UserId == user.Id && item.Provider == "telegram");
        Assert.Equal("12345", identity.TelegramUserId);
        Assert.Equal(777888, identity.ChatId);
    }

    [Fact]
    public async Task HandleWebhookAsync_WhenTelegramWebhookSecretMissingRejectsRequest()
    {
        await using var db = await CreateContextAsync();
        var sender = CreateTelegramSender(
            "Login bot",
            NotificationPurposes.TelegramAuth,
            updateMode: TelegramBotUpdateModes.Webhook,
            webhookSecret: "secret");
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();
        using var update = JsonDocument.Parse("""{"update_id":1}""");
        var updateService = new TelegramBotUpdateService(
            db,
            new StaticHttpClientFactory(new HttpClient(new TelegramOkHandler())),
            NullLogger<TelegramBotUpdateService>.Instance,
            CreateExternalAuthService(db));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            updateService.HandleWebhookAsync(sender.Id, update, new DefaultHttpContext().Request));
    }

    [Fact]
    public async Task HandleWebhookAsync_WhenTelegramWebhookSecretInvalidRejectsRequest()
    {
        await using var db = await CreateContextAsync();
        var sender = CreateTelegramSender(
            "Login bot",
            NotificationPurposes.TelegramAuth,
            updateMode: TelegramBotUpdateModes.Webhook,
            webhookSecret: "secret");
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();
        using var update = JsonDocument.Parse("""{"update_id":1}""");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "wrong";
        var updateService = new TelegramBotUpdateService(
            db,
            new StaticHttpClientFactory(new HttpClient(new TelegramOkHandler())),
            NullLogger<TelegramBotUpdateService>.Instance,
            CreateExternalAuthService(db));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            updateService.HandleWebhookAsync(sender.Id, update, context.Request));
    }

    [Fact]
    public async Task HandleWebhookAsync_WhenTelegramAuthRequestExpiredDoesNotLinkUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "expired-telegram-link@example.com");
        db.TelegramAuthRequests.Add(new TelegramAuthRequest
        {
            State = "expired-state",
            UserId = user.Id,
            Intent = "link",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });
        var sender = CreateTelegramSender(
            "Login bot",
            NotificationPurposes.TelegramAuth,
            updateMode: TelegramBotUpdateModes.Webhook,
            webhookSecret: "secret");
        db.NotificationSenders.Add(sender);
        await db.SaveChangesAsync();
        using var update = JsonDocument.Parse("""
            {
              "update_id": 12,
              "message": {
                "message_id": 1,
                "text": "/start login_expired-state",
                "chat": { "id": 777888 },
                "from": { "id": 12345, "username": "auth_user" }
              }
            }
            """);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "secret";
        var httpFactory = new StaticHttpClientFactory(new HttpClient(new TelegramOkHandler()));
        var updateService = new TelegramBotUpdateService(
            db,
            httpFactory,
            NullLogger<TelegramBotUpdateService>.Instance,
            CreateExternalAuthService(db, httpFactory));

        await updateService.HandleWebhookAsync(sender.Id, update, context.Request);

        var authRequest = await db.TelegramAuthRequests.SingleAsync();
        Assert.Equal("expired", authRequest.Status);
        Assert.False(await db.UserExternalIdentities.AnyAsync(item => item.UserId == user.Id && item.Provider == "telegram"));
    }

    [Fact]
    public async Task GetExternalStatusForClientAsync_WhenStateUnknownThrows()
    {
        await using var db = await CreateContextAsync();
        var service = CreateExternalAuthService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.GetExternalStatusForClientAsync("missing-state", CancellationToken.None));
    }

    [Fact]
    public async Task GetExternalStatusForClientAsync_WhenStateExpiredMarksRequestExpired()
    {
        await using var db = await CreateContextAsync();
        db.ExternalAuthRequests.Add(new ExternalAuthRequest
        {
            Provider = "google",
            State = "expired-state",
            ReturnUrl = "/",
            Intent = "signin",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        var service = CreateExternalAuthService(db);

        var status = await service.GetExternalStatusForClientAsync("expired-state", CancellationToken.None);

        Assert.Equal("expired", status.Status);
        Assert.False(status.Completed);
        Assert.Equal("expired", (await db.ExternalAuthRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task HandleTelegramWebhookFlowAsync_WhenSecretMissingRejectsRequest()
    {
        await using var db = await CreateContextAsync();
        using var update = JsonDocument.Parse("""{"update_id":1}""");
        var service = CreateExternalAuthService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.HandleTelegramWebhookFlowAsync(update, new DefaultHttpContext().Request, CancellationToken.None));
    }

    [Fact]
    public async Task HandleTelegramWebhookFlowAsync_WhenSecretInvalidRejectsRequest()
    {
        await using var db = await CreateContextAsync();
        db.SystemSettings.Add(Setting("auth.telegram.webhookSecret", "secret"));
        await db.SaveChangesAsync();
        using var update = JsonDocument.Parse("""{"update_id":1}""");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "wrong";
        var service = CreateExternalAuthService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.HandleTelegramWebhookFlowAsync(update, context.Request, CancellationToken.None));
    }

    private static NotificationService CreateService(
        PaymentContext db,
        IEmailService emailService,
        ITelegramNotificationService telegramService,
        IRealtimeNotificationPublisher? realtimePublisher = null,
        IWebPushNotificationService? webPushNotificationService = null,
        ISmsNotificationService? smsNotificationService = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new NotificationService(
            db,
            configuration,
            emailService,
            telegramService,
            NullLogger<NotificationService>.Instance,
            realtimePublisher,
            webPushNotificationService,
            smsNotificationService);
    }

    private static ExternalAuthService CreateExternalAuthService(
        PaymentContext db,
        IHttpClientFactory? httpClientFactory = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var passwordHasher = new PasswordHasher<User>();
        var authService = new AuthService(
            db,
            configuration,
            new RecordingEmailService(),
            new FakeWebHostEnvironment(),
            passwordHasher);

        return new ExternalAuthService(
            db,
            configuration,
            httpClientFactory ?? new StaticHttpClientFactory(new HttpClient(new TelegramOkHandler())),
            passwordHasher,
            authService,
            NullLogger<ExternalAuthService>.Instance);
    }

    private static async Task<(TaskJournalItem Task, User Author, User Assignee, User Observer)> CreateTaskWithObserverAsync(
        PaymentContext db,
        string prefix)
    {
        var author = await CreateUserAsync(db, $"{prefix}-author@example.com");
        var assignee = new User
        {
            Email = $"{prefix}-assignee@example.com",
            FullName = $"{prefix} assignee",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var observer = new User
        {
            Email = $"{prefix}-observer@example.com",
            FullName = $"{prefix} observer",
            PasswordHash = "hash",
            RoleId = author.RoleId,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(assignee, observer);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = $"Задача {prefix}",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "inProgress",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = observer.Id,
                    Role = "observer",
                    DisplayName = observer.FullName,
                    Email = observer.Email,
                    SortOrder = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            ]
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        return (task, author, assignee, observer);
    }

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var db = new PaymentContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task SeedEmailSettingsAsync(PaymentContext db)
    {
        db.SystemSettings.AddRange(
            Setting("notifications.email.enabled", "true"),
            Setting("notifications.email.smtpHost", "smtp.example.com"),
            Setting("notifications.email.smtpPort", "587"),
            Setting("notifications.email.smtpUsername", "robot@example.com"),
            Setting("notifications.email.smtpPassword", "password"),
            Setting("notifications.email.fromEmail", "robot@example.com"),
            Setting("notifications.email.fromName", "PayPlanner"),
            Setting("notifications.email.enableSsl", "true"));
        await db.SaveChangesAsync();
    }

    private static SystemSetting Setting(string key, string value)
        => new()
        {
            Key = key,
            Value = value,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static NotificationSender CreateEmailSender(string name, string fromEmail, string purpose, int order)
        => new()
        {
            Channel = NotificationChannels.Email,
            Name = name,
            IsEnabled = true,
            PurposesJson = System.Text.Json.JsonSerializer.Serialize(new[] { purpose }),
            SortOrder = order,
            ConfigJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                smtpHost = "smtp.example.com",
                smtpPort = 587,
                smtpUsername = fromEmail,
                smtpPassword = "password",
                fromEmail,
                fromName = name,
                enableSsl = true
            }),
            CreatedAt = DateTime.UtcNow
        };

    private static NotificationSender CreateTelegramSender(
        string name,
        string purpose,
        int order = 10,
        string updateMode = TelegramBotUpdateModes.Disabled,
        string webhookSecret = "")
        => new()
        {
            Channel = NotificationChannels.Telegram,
            Name = name,
            IsEnabled = true,
            PurposesJson = JsonSerializer.Serialize(new[] { purpose }),
            SortOrder = order,
            ConfigJson = JsonSerializer.Serialize(new
            {
                botToken = "token",
                botUsername = "payplanner_bot",
                updateMode,
                webhookSecret
            }),
            CreatedAt = DateTime.UtcNow
        };

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string email,
        long? chatId = null,
        string? phoneNumber = null)
    {
        var role = new Role
        {
            Name = "user",
            Description = "user",
            CreatedAt = DateTime.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            RoleId = role.Id,
            PhoneNumber = phoneNumber,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        if (chatId.HasValue)
        {
            db.UserExternalIdentities.Add(new UserExternalIdentity
            {
                UserId = user.Id,
                Provider = "telegram",
                ProviderUserId = chatId.Value.ToString(),
                TelegramUserId = chatId.Value.ToString(),
                ChatId = chatId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        return user;
    }

    private static async Task<User> CreateUserWithoutSectionAccessAsync(PaymentContext db, string email, string section)
    {
        var role = new Role
        {
            Name = $"denied-{section}-{Guid.NewGuid():N}",
            Description = "Denied test role",
            CreatedAt = DateTime.UtcNow,
            Permissions =
            {
                new RolePermission
                {
                    Section = section,
                    CanView = false,
                    CanCreate = false,
                    CanEdit = false,
                    CanDelete = false,
                    CanExport = false,
                    CanViewAnalytics = false,
                    ViewScope = DataVisibilityScope.All
                }
            }
        };
        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<int> CreateProtectedNotificationObjectAsync(PaymentContext db, string objectType, User owner)
    {
        var now = DateTime.UtcNow;
        switch (objectType)
        {
            case "payment":
            case "invoice":
                {
                    var payment = new Payment
                    {
                        Amount = 12000m,
                        Date = now.Date,
                        PlannedDate = now.Date,
                        Type = PaymentType.Income,
                        Status = PaymentStatus.Pending,
                        Description = $"{objectType} notification access fixture",
                        DocumentKind = objectType == "invoice" ? DocumentKind.Invoice : DocumentKind.Payment,
                        Responsibles =
                    {
                        new PaymentResponsible { UserId = owner.Id }
                    }
                    };
                    db.Payments.Add(payment);
                    await db.SaveChangesAsync();
                    return payment.Id;
                }

            case "act":
                {
                    var act = new Act
                    {
                        Number = $"ACT-{Guid.NewGuid():N}",
                        Date = now.Date,
                        Amount = 12000m,
                        ResponsibleId = owner.Id
                    };
                    db.Acts.Add(act);
                    await db.SaveChangesAsync();
                    return act.Id;
                }

            case "contract":
                {
                    var client = new Client
                    {
                        Name = "Клиент закрытого договора",
                        ResponsibleUserId = owner.Id
                    };
                    var contract = new Contract
                    {
                        Number = $"CONTRACT-{Guid.NewGuid():N}",
                        Date = now.Date,
                        Amount = 12000m,
                        Status = ContractStatus.Draft,
                        ClientContracts =
                        {
                            new ClientContract { Client = client }
                        }
                    };
                    db.Contracts.Add(contract);
                    await db.SaveChangesAsync();
                    return contract.Id;
                }

            case "case":
                {
                    var client = new Client
                    {
                        Name = "Клиент закрытого дела",
                        Phone = "+70000000000",
                        ResponsibleUserId = owner.Id
                    };
                    var clientCase = new ClientCase
                    {
                        Client = client,
                        CaseNumber = $"CASE-{Guid.NewGuid():N}",
                        CaseType = "Тестовое дело",
                        Stage = "В работе",
                        ResponsibleUserId = owner.Id
                    };
                    db.ClientCases.Add(clientCase);
                    await db.SaveChangesAsync();
                    return clientCase.Id;
                }

            case "task":
                {
                    var task = new TaskJournalItem
                    {
                        ObjectName = "Тестовый объект",
                        Title = "Закрытая тестовая задача",
                        AssigneeUserId = owner.Id,
                        AssigneeName = owner.FullName,
                        AuthorUserId = owner.Id,
                        AuthorName = owner.FullName,
                        DueAt = now.AddHours(2),
                        Status = "new"
                    };
                    db.TaskJournalItems.Add(task);
                    await db.SaveChangesAsync();
                    return task.Id;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(objectType), objectType, "Unsupported notification object type.");
        }
    }

    private static async Task<StickyNote> CreateStickyNoteAsync(
        PaymentContext db,
        User owner,
        string visibility)
    {
        var board = new StickyNoteBoard
        {
            Title = $"{visibility} board",
            Visibility = visibility,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Тестовая заметка",
            AuthorUserId = owner.Id,
            AuthorUserName = owner.FullName
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        return note;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> ReaderCommands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public List<(string To, string Subject, string Body)> Messages { get; } = [];

        public Task SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            CancellationToken cancellationToken = default,
            EmailSendSettings? settings = null)
        {
            Messages.Add((toEmail, subject, body));
            return Task.CompletedTask;
        }

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class RecordingTelegramService : ITelegramNotificationService
    {
        public List<(long ChatId, string Text)> Messages { get; } = [];

        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto
            {
                Enabled = true,
                Ready = true,
                BotTokenSet = true,
                LinkedUsersCount = 1
            });

        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
        {
            Messages.Add((chatId, text));
            return Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));
        }

        public Task<TelegramSendResult> SendMessageWithTokenAsync(
            long chatId,
            string text,
            string botToken,
            CancellationToken cancellationToken = default,
            TelegramBotConnectionSettings? connection = null)
        {
            Messages.Add((chatId, text));
            return Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));
        }
    }

    private sealed class RecordingSmsNotificationService : ISmsNotificationService
    {
        public List<(string PhoneNumber, string Text)> Messages { get; } = [];

        public SmsNotificationSettingsDto Settings { get; set; } = new()
        {
            Enabled = true,
            Ready = true,
            ProviderConfigured = true,
            AdapterConfigured = true,
            ApiTokenSet = true,
            ProviderKey = "test-provider",
            SenderName = "Тестовый SMS",
            DailyLimit = 100,
            MaxMessageLength = 480,
            StatusText = "SMS-канал готов к отправке."
        };

        public SmsSendResult NextResult { get; set; } = new(true, NotificationDeliveryStatus.Sent);

        public Task<SmsNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Settings);

        public Task<SmsNotificationSettingsDto> UpdateSettingsAsync(
            UpdateSmsNotificationSettingsRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Settings);

        public Task<SmsSendResult> SendMessageAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            Messages.Add((phoneNumber, text));
            return Task.FromResult(NextResult);
        }
    }

    private sealed class RecordingRealtimeNotificationPublisher : IRealtimeNotificationPublisher
    {
        public List<UserNotification> Notifications { get; } = [];
        public List<bool> ToastEnabledValues { get; } = [];

        public Task PublishUserNotificationAsync(
            UserNotification notification,
            bool toastEnabled = true,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            ToastEnabledValues.Add(toastEnabled);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWebPushNotificationService : IWebPushNotificationService
    {
        public List<UserNotification> Notifications { get; } = [];

        public WebPushPublicKeyStatus GetPublicKeyStatus()
            => new(true, true, "public-key", true, true, true);

        public Task SendUserNotificationAsync(
            UserNotification notification,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }

        public Task<NotificationDelivery?> RetryDeliveryAsync(
            int deliveryId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<NotificationDelivery?>(null);
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StaticHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class TelegramOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":true}""")
            });
    }

    private sealed class TelegramPollingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri?.AbsolutePath.EndsWith("/getUpdates", StringComparison.OrdinalIgnoreCase) == true
                ? """
                  {
                    "ok": true,
                    "result": [
                      {
                        "update_id": 25,
                        "message": {
                          "message_id": 1,
                          "text": "/start",
                          "chat": { "id": 999000 },
                          "from": {
                            "id": 555,
                            "username": "polling_user",
                            "first_name": "Полина"
                          }
                        }
                      }
                    ]
                  }
                  """
                : """{"ok":true,"result":true}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
        }
    }

    private sealed class TelegramEmptyPollingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":[]}""")
            });
    }

    private sealed class TelegramProfileHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var payload = path switch
            {
                var item when item.EndsWith("/getMe", StringComparison.OrdinalIgnoreCase) => """
                    {
                      "ok": true,
                      "result": {
                        "id": 4242,
                        "is_bot": true,
                        "first_name": "PayPlanner",
                        "username": "payplanner_test_bot",
                        "can_join_groups": true,
                        "can_read_all_group_messages": false,
                        "supports_inline_queries": true
                      }
                    }
                    """,
                var item when item.EndsWith("/getMyName", StringComparison.OrdinalIgnoreCase) => """
                    { "ok": true, "result": { "name": "PayPlanner Bot" } }
                    """,
                var item when item.EndsWith("/getMyDescription", StringComparison.OrdinalIgnoreCase) => """
                    { "ok": true, "result": { "description": "Служебный бот CRM" } }
                    """,
                var item when item.EndsWith("/getMyShortDescription", StringComparison.OrdinalIgnoreCase) => """
                    { "ok": true, "result": { "short_description": "Уведомления CRM" } }
                    """,
                var item when item.EndsWith("/getMyCommands", StringComparison.OrdinalIgnoreCase) => """
                    {
                      "ok": true,
                      "result": [
                        { "command": "start", "description": "Привязать Telegram" },
                        { "command": "status", "description": "Проверить привязку" }
                      ]
                    }
                    """,
                var item when item.EndsWith("/getUserProfilePhotos", StringComparison.OrdinalIgnoreCase) => """
                    {
                      "ok": true,
                      "result": {
                        "total_count": 1,
                        "photos": [
                          [
                            { "file_id": "small-photo-file-id", "file_unique_id": "small" },
                            { "file_id": "large-photo-file-id", "file_unique_id": "large" }
                          ]
                        ]
                      }
                    }
                    """,
                _ => """{"ok":true,"result":true}"""
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
