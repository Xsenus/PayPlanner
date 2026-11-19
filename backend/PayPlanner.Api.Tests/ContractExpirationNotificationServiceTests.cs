using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task RunOnceAsync_LoadsRecipientsInFixedNumberOfQueriesForMultipleContracts()
    {
        var clock = new ExpiryTestClock(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new RecordingCommandInterceptor();
        var notificationService = DispatchProxy.Create<INotificationService, RecordingNotificationServiceProxy>();
        var notificationRecorder = (RecordingNotificationServiceProxy)(object)notificationService;
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddDbContext<PaymentContext>(options => options
            .UseSqlite(connection)
            .AddInterceptors(interceptor));
        services.AddSingleton(notificationService);
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = $"contracts-batch-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
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
                Email = "contract-batch@example.com",
                FullName = "Ответственный по договорам",
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
                Name = "Клиент с несколькими договорами",
                ResponsibleUserId = user.Id
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            for (var index = 1; index <= 12; index++)
            {
                db.Contracts.Add(new Contract
                {
                    Number = $"BATCH-{index}",
                    Title = $"Договор {index}",
                    Date = clock.GetUtcNow().UtcDateTime.Date.AddMonths(-1),
                    ValidUntil = clock.GetUtcNow().UtcDateTime.Date.AddDays(3),
                    BillingMode = ContractBillingMode.Fixed,
                    Amount = 10000m,
                    Status = ContractStatus.Concluded,
                    ClientContracts =
                    {
                        new ClientContract { ClientId = client.Id }
                    }
                });
            }

            var deniedRole = new Role { Name = $"contracts-denied-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(deniedRole);
            await db.SaveChangesAsync();
            var deniedUser = new User
            {
                Email = "contract-denied@example.com",
                FullName = "Сотрудник без доступа к договорам",
                PasswordHash = "hash",
                RoleId = deniedRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Users.Add(deniedUser);
            await db.SaveChangesAsync();
            var deniedClient = new Client
            {
                Name = "Клиент закрытого договора",
                ResponsibleUserId = deniedUser.Id
            };
            db.Clients.Add(deniedClient);
            await db.SaveChangesAsync();
            db.Contracts.Add(new Contract
            {
                Number = "DENIED-1",
                Title = "Недоступный договор",
                Date = clock.GetUtcNow().UtcDateTime.Date.AddMonths(-1),
                ValidUntil = clock.GetUtcNow().UtcDateTime.Date.AddDays(3),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 10000m,
                Status = ContractStatus.Concluded,
                ClientContracts =
                {
                    new ClientContract { ClientId = deniedClient.Id }
                }
            });

            await db.SaveChangesAsync();
        }

        interceptor.ReaderCommands.Clear();
        var service = new ContractExpirationNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContractExpirationNotificationService>.Instance, clock);

        var affected = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(12, affected);
        Assert.Equal(12, notificationRecorder.Calls.Count);
        Assert.All(notificationRecorder.Calls, call => Assert.Single(call.RecipientUserIds));
        Assert.Equal(5, interceptor.ReaderCommands.Count);
        Assert.Single(interceptor.ReaderCommands, command => command.Contains(ContractExpirationRecipientSettings.SettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command => command.Contains(CalendarTimeZoneHelper.UserTimeZoneSettingKey, StringComparison.Ordinal));
        Assert.Single(interceptor.ReaderCommands, command =>
            command.Contains("ClientContracts", StringComparison.Ordinal)
            && command.Contains("LegalEntityContracts", StringComparison.Ordinal)
            && command.Contains("ContractInvoices", StringComparison.Ordinal)
            && command.Contains("ContractPayments", StringComparison.Ordinal)
            && command.Contains("ContractActs", StringComparison.Ordinal)
            && command.Contains("FROM \"Payments\"", StringComparison.Ordinal)
            && command.Contains("PaymentResponsibles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnceAsync_NotifiesResponsibleWhenContractExpiresSoon()
    {
        var clock = new ExpiryTestClock(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
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
                Date = clock.GetUtcNow().UtcDateTime.Date.AddMonths(-1),
                ValidUntil = clock.GetUtcNow().UtcDateTime.Date.AddDays(3),
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
            NullLogger<ContractExpirationNotificationService>.Instance, clock);

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

    private sealed class ExpiryTestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

    private class RecordingNotificationServiceProxy : DispatchProxy
    {
        public List<ContractNotificationCall> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(INotificationService.NotifyContractExpiringAsync)
                && args is { Length: 4 })
            {
                var contract = Assert.IsType<Contract>(args[0]);
                var recipientUserIds = Assert.IsAssignableFrom<IReadOnlyCollection<int>>(args[2]);
                Calls.Add(new ContractNotificationCall(contract.Id, recipientUserIds.ToArray()));
                return Task.CompletedTask;
            }

            throw new NotSupportedException($"Unexpected notification call: {targetMethod?.Name}.");
        }
    }

    private sealed record ContractNotificationCall(int ContractId, IReadOnlyCollection<int> RecipientUserIds);
}
