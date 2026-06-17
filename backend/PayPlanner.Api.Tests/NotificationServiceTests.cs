using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
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
        Assert.Contains(channels, item => item.Key == NotificationChannels.Email && item.IsEnabled && item.IsSendable && item.RequiresProvider);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Telegram && item.IsEnabled && item.IsSendable && item.RequiresProvider);
        Assert.Contains(channels, item => item.Key == NotificationChannels.WebPush && item.IsEnabled && item.IsSendable && !item.RequiresProvider && !item.IsFuture);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Sms && !item.IsEnabled && !item.IsSendable && item.IsFuture);
        Assert.Contains(channels, item => item.Key == NotificationChannels.Max && !item.IsEnabled && !item.IsSendable && item.IsFuture);
        Assert.Equal(new[] { NotificationChannels.InApp, NotificationChannels.Email, NotificationChannels.Telegram, NotificationChannels.WebPush }, NotificationChannels.All);
        Assert.Equal(new[] { NotificationChannels.Sms, NotificationChannels.Max }, NotificationChannels.Future);
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
                    "unknown",
                    NotificationChannels.Email
                ]
            }
        });

        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(rawValue, UserNotificationTypes.TaskAssigned, NotificationChannels.Email));
        Assert.True(UserNotificationPreferenceSettings.IsChannelEnabled(rawValue, UserNotificationTypes.TaskAssigned, NotificationChannels.Telegram));
        Assert.False(UserNotificationPreferenceSettings.IsChannelEnabled(rawValue, UserNotificationTypes.TaskComment, NotificationChannels.InApp));
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
            DueAt = DateTime.UtcNow.AddHours(4),
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
        Assert.Contains("Новая задача", notification.Title);
        Assert.Contains("Подготовить отзыв", notification.BodyPreview);
        Assert.False(notification.IsRead);
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
        Assert.Contains("Нужно исправить результат", notification.BodyPreview);
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
        Assert.Equal(JsonValueKind.String, config.RootElement.GetProperty("lastPollingAt").ValueKind);
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
        IWebPushNotificationService? webPushNotificationService = null)
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
            webPushNotificationService);
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

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
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

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, long? chatId = null)
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

    private sealed class RecordingRealtimeNotificationPublisher : IRealtimeNotificationPublisher
    {
        public List<UserNotification> Notifications { get; } = [];

        public Task PublishUserNotificationAsync(
            UserNotification notification,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
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
