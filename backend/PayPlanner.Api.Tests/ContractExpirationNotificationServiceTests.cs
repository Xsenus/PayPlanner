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

public class ContractExpirationNotificationServiceTests
{
    [Fact]
    public async Task RunOnceAsync_NotifiesResponsibleWhenContractExpiresSoon()
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
            var role = new Role { Name = $"contracts-expiring-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Contracts,
                CanView = true,
                ViewScope = DataVisibilityScope.Own
            });
            var user = new User
            {
                Email = "contract-expiring@example.com",
                FullName = "Ответственный по договору",
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
                Name = "Клиент договора",
                ResponsibleUserId = user.Id
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            var contract = new Contract
            {
                Number = "EXP-1",
                Title = "Срочный договор",
                Date = DateTime.UtcNow.Date.AddMonths(-1),
                ValidUntil = DateTime.UtcNow.Date.AddDays(3),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 10000m,
                Status = ContractStatus.Concluded,
                ClientContracts =
                {
                    new ClientContract { ClientId = client.Id }
                }
            };
            db.Contracts.Add(contract);
            await db.SaveChangesAsync();
        }

        var service = new ContractExpirationNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContractExpirationNotificationService>.Instance);

        var affected = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, affected);
        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<PaymentContext>();
        var storedContract = await assertDb.Contracts.SingleAsync();
        var notification = await assertDb.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.ContractExpiring, notification.Type);
        Assert.Equal("contract", notification.ObjectType);
        Assert.Equal(storedContract.Id, notification.ObjectId);
        Assert.Contains("EXP-1", notification.BodyPreview);
        Assert.Contains("Осталось дней: 3", notification.BodyPreview);
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
