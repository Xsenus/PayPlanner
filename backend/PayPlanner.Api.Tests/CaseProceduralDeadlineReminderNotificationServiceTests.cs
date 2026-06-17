using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CaseProceduralDeadlineReminderNotificationServiceTests
{
    [Fact]
    public async Task RunOnceAsync_NotifiesResponsibleWhenDeadlineIsInWarningWindow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<PaymentContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:CaseProceduralDeadlineReminders:BatchSize"] = "10",
            })
            .Build());
        services.AddSingleton<IEmailService, NoopEmailService>();
        services.AddSingleton<ITelegramNotificationService, NoopTelegramNotificationService>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<NotificationService>>(NullLogger<NotificationService>.Instance);
        services.AddScoped<INotificationService, NotificationService>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = $"case-deadline-reminder-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own
            });
            var user = new User
            {
                Email = "case-deadline-reminder@example.com",
                FullName = "Ответственный по делу",
                PasswordHash = "hash",
                RoleId = role.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var client = new Client
            {
                Name = "Клиент дела",
                IsActive = true,
                ResponsibleUserId = user.Id
            };
            var clientCase = new ClientCase
            {
                Client = client,
                Title = "Дело с приближающимся сроком",
                CaseNumber = "CASE-REMINDER-1",
                Status = ClientCaseStatus.Open,
                ResponsibleUserId = user.Id
            };
            db.Clients.Add(client);
            db.ClientCases.Add(clientCase);
            await db.SaveChangesAsync();
            db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
            {
                ClientCaseId = clientCase.Id,
                DeadlineType = "appeal",
                Title = "Подать апелляцию",
                DueAt = DateTime.UtcNow.Date.AddDays(3),
                AllDay = true,
                Status = "active",
                WarningDaysBefore = 7,
                Notes = "Проверить срок",
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var service = new CaseProceduralDeadlineReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<CaseProceduralDeadlineReminderNotificationService>.Instance);

        var firstRun = await service.RunOnceAsync(CancellationToken.None);
        var secondRun = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, firstRun);
        Assert.Equal(1, secondRun);
        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<PaymentContext>();
        var notification = await assertDb.UserNotifications.SingleAsync();
        var deadline = await assertDb.ClientCaseProceduralDeadlines.SingleAsync();
        Assert.Equal(UserNotificationTypes.CaseProceduralDeadline, notification.Type);
        Assert.Equal("case", notification.ObjectType);
        Assert.Contains("CASE-REMINDER-1", notification.Title);
        Assert.Contains("Подать апелляцию", notification.BodyPreview);
        Assert.Contains($"\"proceduralDeadlineId\":{deadline.Id}", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"reminder\"", notification.MetadataJson, StringComparison.Ordinal);
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default, EmailSendSettings? settings = null)
            => Task.CompletedTask;

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
            => Task.CompletedTask;

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoopTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(long chatId, string text, string botToken, CancellationToken cancellationToken = default, TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());
    }
}
