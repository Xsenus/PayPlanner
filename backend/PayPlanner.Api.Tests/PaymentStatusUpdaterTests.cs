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

public class PaymentStatusUpdaterTests
{
    [Fact]
    public async Task RunOnceAsync_NotifiesResponsibleWhenPaymentBecomesOverdue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<PaymentContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IEmailService, NoopEmailService>();
        services.AddSingleton<ITelegramNotificationService, NoopTelegramNotificationService>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<NotificationService>>(NullLogger<NotificationService>.Instance);
        services.AddScoped<INotificationService, NotificationService>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = $"payments-overdue-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                ViewScope = DataVisibilityScope.Own
            });
            var user = new User
            {
                Email = "payment-overdue@example.com",
                FullName = "Ответственный",
                PasswordHash = "hash",
                RoleId = role.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Users.Add(user);
            db.PaymentStatuses.Add(new PaymentStatusEntity { Name = "Overdue", Description = "Просрочен" });
            await db.SaveChangesAsync();
            db.Payments.Add(new Payment
            {
                Description = "Просроченный платеж",
                Date = DateTime.UtcNow.Date.AddDays(-1),
                Amount = 1000m,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                DocumentKind = DocumentKind.Payment,
                Responsibles = [new PaymentResponsible { UserId = user.Id }]
            });
            await db.SaveChangesAsync();
        }

        var updater = new PaymentStatusUpdater(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PaymentStatusUpdater>.Instance);

        var affected = await updater.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, affected);
        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<PaymentContext>();
        var payment = await assertDb.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Overdue, payment.Status);
        var notification = await assertDb.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.PaymentChanged, notification.Type);
        Assert.Equal("payment", notification.ObjectType);
        Assert.Equal(payment.Id, notification.ObjectId);
        Assert.Contains("Просрочен", notification.BodyPreview);
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
