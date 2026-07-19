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

public class CaseProceduralDeadlineReminderNotificationServiceTests
{
    [Fact]
    public async Task RunOnceAsync_BatchesRecipientQueriesAndPreservesCaseVisibility()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new RecordingCommandInterceptor();
        var notificationService = DispatchProxy.Create<INotificationService, RecordingNotificationServiceProxy>();
        var notificationRecorder = (RecordingNotificationServiceProxy)(object)notificationService;
        var services = new ServiceCollection();
        services.AddDbContext<PaymentContext>(options => options
            .UseSqlite(connection)
            .AddInterceptors(interceptor));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:CaseProceduralDeadlineReminders:BatchSize"] = "20",
            })
            .Build());
        services.AddSingleton(notificationService);
        await using var provider = services.BuildServiceProvider();

        int hiddenCaseId;
        int adminHiddenCaseId;
        int deniedCaseId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            await db.Database.EnsureCreatedAsync();
            var allowedRole = new Role { Name = $"case-deadline-batch-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(allowedRole);
            await db.SaveChangesAsync();
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = allowedRole.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own
            });
            var allowedUser = new User
            {
                Email = "case-deadline-batch@example.com",
                FullName = "Ответственный по срокам",
                PasswordHash = "hash",
                RoleId = allowedRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Users.Add(allowedUser);
            await db.SaveChangesAsync();
            var client = new Client
            {
                Name = "Клиент с несколькими сроками",
                IsActive = true,
                ResponsibleUserId = allowedUser.Id
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            var allowedCases = Enumerable.Range(1, 12)
                .Select(index => new ClientCase
                {
                    ClientId = client.Id,
                    Title = $"Дело со сроком {index}",
                    CaseNumber = $"CASE-BATCH-{index}",
                    Status = ClientCaseStatus.Open,
                    ResponsibleUserId = allowedUser.Id
                })
                .ToArray();
            var hiddenCase = new ClientCase
            {
                ClientId = client.Id,
                Title = "Скрытое дело со сроком",
                CaseNumber = "CASE-HIDDEN-DEADLINE",
                Status = ClientCaseStatus.Open,
                ResponsibleUserId = allowedUser.Id,
                IsHiddenFromExecutor = true
            };
            db.ClientCases.AddRange(allowedCases);
            db.ClientCases.Add(hiddenCase);
            await db.SaveChangesAsync();
            hiddenCaseId = hiddenCase.Id;

            var adminRole = new Role { Name = "admin", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(adminRole);
            await db.SaveChangesAsync();
            var adminUser = new User
            {
                Email = "case-deadline-admin@example.com",
                FullName = "Администратор процессуальных сроков",
                PasswordHash = "hash",
                RoleId = adminRole.Id,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Users.Add(adminUser);
            await db.SaveChangesAsync();
            var adminHiddenCase = new ClientCase
            {
                ClientId = client.Id,
                Title = "Скрытое дело администратора",
                CaseNumber = "CASE-ADMIN-HIDDEN-DEADLINE",
                Status = ClientCaseStatus.Open,
                ResponsibleUserId = adminUser.Id,
                IsHiddenFromExecutor = true
            };
            db.ClientCases.Add(adminHiddenCase);
            await db.SaveChangesAsync();
            adminHiddenCaseId = adminHiddenCase.Id;

            var deniedRole = new Role { Name = $"case-deadline-denied-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(deniedRole);
            await db.SaveChangesAsync();
            var deniedUser = new User
            {
                Email = "case-deadline-denied@example.com",
                FullName = "Сотрудник без доступа к делам",
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
                Name = "Клиент закрытого дела",
                IsActive = true,
                ResponsibleUserId = deniedUser.Id
            };
            var deniedCase = new ClientCase
            {
                Client = deniedClient,
                Title = "Дело без разрешения",
                CaseNumber = "CASE-DENIED-DEADLINE",
                Status = ClientCaseStatus.Open,
                ResponsibleUserId = deniedUser.Id
            };
            db.Clients.Add(deniedClient);
            db.ClientCases.Add(deniedCase);
            await db.SaveChangesAsync();
            deniedCaseId = deniedCase.Id;

            var deadlineCases = allowedCases.Append(hiddenCase).Append(adminHiddenCase).Append(deniedCase);
            db.ClientCaseProceduralDeadlines.AddRange(deadlineCases.Select((clientCase, index) =>
                new ClientCaseProceduralDeadline
                {
                    ClientCaseId = clientCase.Id,
                    DeadlineType = "appeal",
                    Title = $"Процессуальный срок {index + 1}",
                    DueAt = DateTime.UtcNow.Date.AddDays(3),
                    AllDay = true,
                    Status = "active",
                    WarningDaysBefore = 7,
                    CreatedAt = DateTime.UtcNow.AddDays(-1)
                }));
            await db.SaveChangesAsync();
        }

        interceptor.ReaderCommands.Clear();
        var service = new CaseProceduralDeadlineReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<CaseProceduralDeadlineReminderNotificationService>.Instance);

        var notified = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(13, notified);
        Assert.Equal(13, notificationRecorder.Calls.Count);
        Assert.DoesNotContain(notificationRecorder.Calls, call => call.ClientCaseId == hiddenCaseId);
        Assert.Contains(notificationRecorder.Calls, call => call.ClientCaseId == adminHiddenCaseId);
        Assert.DoesNotContain(notificationRecorder.Calls, call => call.ClientCaseId == deniedCaseId);
        Assert.All(notificationRecorder.Calls, call => Assert.Single(call.RecipientUserIds));
        Assert.Equal(3, interceptor.ReaderCommands.Count);
        Assert.Single(interceptor.ReaderCommands, command =>
            command.StartsWith("SELECT \"c\".\"Id\"", StringComparison.Ordinal)
            && command.Contains("IsHiddenFromExecutor", StringComparison.Ordinal));
    }

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

    [Fact]
    public async Task RunOnceAsync_SkipsPretrialCaseProceduralDeadlineNotifications()
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
            var role = new Role { Name = $"pretrial-deadline-reminder-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
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
                Email = "pretrial-deadline-reminder@example.com",
                FullName = "Ответственный по внесудебному делу",
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
                Name = "Клиент внесудебного дела",
                IsActive = true,
                ResponsibleUserId = user.Id
            };
            var clientCase = new ClientCase
            {
                Client = client,
                Title = "Внесудебное дело со старым процессуальным сроком",
                CaseNumber = "CASE-PRETRIAL-REMINDER",
                Status = ClientCaseStatus.Open,
                CaseType = "Внесудебное урегулирование",
                Stage = "Внесудебное производство",
                ResponsibleUserId = user.Id
            };
            db.Clients.Add(client);
            db.ClientCases.Add(clientCase);
            await db.SaveChangesAsync();
            db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
            {
                ClientCaseId = clientCase.Id,
                DeadlineType = "appeal",
                Title = "Старый срок апелляции",
                DueAt = DateTime.UtcNow.Date.AddDays(2),
                AllDay = true,
                Status = "active",
                WarningDaysBefore = 7,
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var service = new CaseProceduralDeadlineReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<CaseProceduralDeadlineReminderNotificationService>.Instance);

        var notified = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, notified);
        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<PaymentContext>();
        Assert.Empty(await assertDb.UserNotifications.ToListAsync());
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
        public List<CaseNotificationCall> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(INotificationService.NotifyCaseProceduralDeadlineAsync)
                && args is { Length: 7 })
            {
                var clientCase = Assert.IsType<ClientCase>(args[0]);
                var recipientUserIds = Assert.IsAssignableFrom<IReadOnlyCollection<int>>(args[5]);
                Calls.Add(new CaseNotificationCall(clientCase.Id, recipientUserIds.ToArray()));
                return Task.CompletedTask;
            }

            throw new NotSupportedException($"Unexpected notification call: {targetMethod?.Name}.");
        }
    }

    private sealed record CaseNotificationCall(int ClientCaseId, IReadOnlyCollection<int> RecipientUserIds);
}
