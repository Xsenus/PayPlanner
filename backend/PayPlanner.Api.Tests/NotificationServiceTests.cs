using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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


    private static NotificationService CreateService(
        PaymentContext db,
        IEmailService emailService,
        ITelegramNotificationService telegramService)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new NotificationService(
            db,
            configuration,
            emailService,
            telegramService,
            NullLogger<NotificationService>.Instance);
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
}
