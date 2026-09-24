using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientBirthdayTaskSchedulerServiceTests
{
    [Fact]
    public async Task Migration_BackfillsBirthdayFromExistingClientProfile()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .UsePayPlannerSqliteMigrations()
            .Options;
        await using var db = new PaymentContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260921101627_AddTaskRecurrence");
        const string profileNotes = "__PP_CLIENT_PROFILE_V2__:{\"birthDate\":\"1990-09-28\"}";
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Clients" ("Address", "Company", "CreatedAt", "Email", "IsActive", "IsClientAndSupplier", "Name", "Notes", "Phone")
            VALUES ('', '', CURRENT_TIMESTAMP, '', 1, 0, 'Клиент', {0}, '');
            """,
            profileNotes);

        await migrator.MigrateAsync();

        var birthday = await db.Clients.Select(client => client.BirthDate).SingleAsync();
        Assert.Equal(new DateTime(1990, 9, 28), birthday);
    }

    [Fact]
    public async Task RunOnceAsync_CreatesOneTaskSevenDaysBeforeBirthdayAndDoesNotDuplicateIt()
    {
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: true);
        var now = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc);

        var first = await fixture.Service.RunOnceAsync(now);
        var second = await fixture.Service.RunOnceAsync(now.AddHours(1));

        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(0, second.CreatedCount);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var task = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync();
        Assert.Equal("Поздравить с днём рождения: Виктория Мокшина", task.Title);
        Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc), task.DueAt);
        Assert.Contains("birthday:1:2026", task.TagsJson, StringComparison.Ordinal);
        Assert.Equal(TaskClientBillingClassification.Internal, task.ClientBillingClassification);
        Assert.Single(task.Participants);
        Assert.Equal("Ответственный сотрудник", task.Participants.Single().DisplayName);
        Assert.Contains(task.Comments, comment => comment.Kind == "system");
    }

    [Fact]
    public async Task RunOnceAsync_PicksUpBirthdayAddedAfterEarlierCheckOnSameDay()
    {
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: true);
        var now = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, (await fixture.Service.RunOnceAsync(now)).CreatedCount);

        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var responsibleId = await db.Users.Select(user => user.Id).SingleAsync();
            db.Clients.Add(new Client
            {
                Name = "Клиент с днём рождения сегодня",
                BirthDate = new DateTime(1987, 9, 21),
                ResponsibleUserId = responsibleId,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await fixture.Service.RunOnceAsync(now.AddHours(1))).CreatedCount);
        Assert.Equal(0, (await fixture.Service.RunOnceAsync(now.AddHours(2))).CreatedCount);
        await using var checkScope = fixture.Provider.CreateAsyncScope();
        var tasks = await checkScope.ServiceProvider.GetRequiredService<PaymentContext>()
            .TaskJournalItems.OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, task => task.Title.Contains("сегодня", StringComparison.Ordinal) && task.DueAt == new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task RunOnceAsync_FindsBirthdaysAcrossNewYearWithoutLoadingUnrelatedMonth()
    {
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: true);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var responsibleId = await db.Users.Select(user => user.Id).SingleAsync();
            db.Clients.Add(new Client
            {
                Name = "Январский клиент",
                BirthDate = new DateTime(1987, 1, 2),
                ResponsibleUserId = responsibleId,
                IsActive = true,
            });
            db.SystemSettings.Add(new SystemSetting
            {
                Key = ClientBirthdayTaskSchedulerService.LeadDaysSettingKey,
                Value = "14",
            });
            await db.SaveChangesAsync();
        }

        var result = await fixture.Service.RunOnceAsync(new DateTime(2026, 12, 25, 3, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, result.CreatedCount);
        await using var checkScope = fixture.Provider.CreateAsyncScope();
        var task = await checkScope.ServiceProvider.GetRequiredService<PaymentContext>().TaskJournalItems.SingleAsync();
        Assert.Contains("Январский клиент", task.Title, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2027, 1, 2, 10, 0, 0, DateTimeKind.Utc), task.DueAt);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNothingWhenAutomationIsDisabled()
    {
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: false);

        var result = await fixture.Service.RunOnceAsync(new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc));

        Assert.False(result.Enabled);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        Assert.Empty(await db.TaskJournalItems.ToListAsync());
    }

    [Fact]
    public async Task RunOnceAsync_CreatesEmployeeTaskForSelectedAssigneeOnlyOnce()
    {
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: false);
        int assigneeId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var assignee = await db.Users.SingleAsync();
            assigneeId = assignee.Id;
            db.Users.Add(new User
            {
                Email = "birthday@example.com",
                FullName = "Именинник",
                PasswordHash = "test",
                IsActive = true,
                IsApproved = true,
                IsEmployee = true,
                DateOfBirth = new DateTime(1990, 9, 28),
                RoleId = assignee.RoleId,
            });
            db.SystemSettings.AddRange(
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.EmployeeEnabledSettingKey, Value = "true" },
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.AssigneeUserIdSettingKey, Value = assigneeId.ToString() });
            await db.SaveChangesAsync();
        }

        var now = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc);
        var first = await fixture.Service.RunOnceAsync(now);
        var second = await fixture.Service.RunOnceAsync(now.AddHours(1));

        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(0, second.CreatedCount);
        await using var checkScope = fixture.Provider.CreateAsyncScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<PaymentContext>();
        var task = await checkDb.TaskJournalItems.Include(item => item.Participants).SingleAsync();
        Assert.Contains("Именинник", task.Title, StringComparison.Ordinal);
        Assert.Contains("employee-birthday:", task.TagsJson, StringComparison.Ordinal);
        Assert.Equal(assigneeId, task.AssigneeUserId);
        Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc), task.DueAt);
        Assert.Null(task.ClientId);
        Assert.Single(task.Participants);

        var roleId = await checkDb.Roles.Select(role => role.Id).SingleAsync();
        checkDb.Users.Add(new User
        {
            Email = "late-birthday@example.com",
            FullName = "Новый именинник",
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
            DateOfBirth = new DateTime(1985, 9, 21),
            RoleId = roleId,
        });
        await checkDb.SaveChangesAsync();
        Assert.Equal(1, (await fixture.Service.RunOnceAsync(now.AddHours(2))).CreatedCount);
        var newTask = await checkDb.TaskJournalItems.OrderByDescending(item => item.Id).FirstAsync();
        Assert.Contains("Новый именинник", newTask.Title, StringComparison.Ordinal);
        Assert.Equal(assigneeId, newTask.AssigneeUserId);
    }

    [Fact]
    public async Task RunOnceAsync_NotifiesSelectedAssigneeForNewClientAndEmployeeTasksOnlyOnce()
    {
        var notifications = DispatchProxy.Create<INotificationService, RecordingBirthdayNotificationServiceProxy>();
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: true, notifications);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var assignee = await db.Users.SingleAsync();
            assignee.IsEmployee = true;
            assignee.DateOfBirth = new DateTime(1990, 9, 28);
            db.SystemSettings.AddRange(
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.EmployeeEnabledSettingKey, Value = "true" },
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.AssigneeUserIdSettingKey, Value = assignee.Id.ToString() });
            await db.SaveChangesAsync();
        }

        var now = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(2, (await fixture.Service.RunOnceAsync(now)).CreatedCount);
        Assert.Equal(0, (await fixture.Service.RunOnceAsync(now.AddHours(1))).CreatedCount);

        var calls = ((RecordingBirthdayNotificationServiceProxy)notifications).AssignedTasks;
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(0, call.ActorUserId));
        Assert.Contains(calls, call => call.Task.Title.StartsWith("Поздравить с днём рождения:", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Task.Title.StartsWith("Поздравить коллегу", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnceAsync_SkipsEmployeeWhenSelectedAssigneeIsUnavailable()
    {
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: false);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var roleId = await db.Roles.Select(role => role.Id).SingleAsync();
            db.Users.Add(new User
            {
                Email = "birthday@example.com",
                FullName = "Именинник",
                PasswordHash = "test",
                IsActive = true,
                IsApproved = true,
                IsEmployee = true,
                DateOfBirth = new DateTime(1990, 9, 28),
                RoleId = roleId,
            });
            db.SystemSettings.AddRange(
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.EmployeeEnabledSettingKey, Value = "true" },
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.AssigneeUserIdSettingKey, Value = "999999" });
            await db.SaveChangesAsync();
        }

        var result = await fixture.Service.RunOnceAsync(new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, result.CreatedCount);
        Assert.Contains("ответственный", result.Error, StringComparison.OrdinalIgnoreCase);
        await using var checkScope = fixture.Provider.CreateAsyncScope();
        Assert.Empty(await checkScope.ServiceProvider.GetRequiredService<PaymentContext>().TaskJournalItems.ToListAsync());
    }

    [Fact]
    public async Task RunOnceAsync_UsesConfiguredLeadAndSelectedClientAssignee()
    {
        await using var fixture = await BirthdayFixture.CreateAsync(enabled: true);
        int selectedId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var roleId = await db.Roles.Select(role => role.Id).SingleAsync();
            var selected = new User
            {
                Email = "selected@example.com",
                FullName = "Назначенный сотрудник",
                PasswordHash = "test",
                IsActive = true,
                IsApproved = true,
                RoleId = roleId,
            };
            db.Users.Add(selected);
            await db.SaveChangesAsync();
            selectedId = selected.Id;
            db.SystemSettings.AddRange(
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.LeadDaysSettingKey, Value = "14" },
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.ClientUseSelectedAssigneeSettingKey, Value = "true" },
                new SystemSetting { Key = ClientBirthdayTaskSchedulerService.AssigneeUserIdSettingKey, Value = selectedId.ToString() });
            await db.SaveChangesAsync();
        }

        var result = await fixture.Service.RunOnceAsync(new DateTime(2026, 9, 14, 3, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, result.CreatedCount);
        await using var checkScope = fixture.Provider.CreateAsyncScope();
        var task = await checkScope.ServiceProvider.GetRequiredService<PaymentContext>().TaskJournalItems.SingleAsync();
        Assert.Equal(selectedId, task.AssigneeUserId);
        Assert.Equal(1, task.ClientId);
    }

    private sealed class BirthdayFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private BirthdayFixture(SqliteConnection connection, ServiceProvider provider, ClientBirthdayTaskSchedulerService service)
        {
            _connection = connection;
            Provider = provider;
            Service = service;
        }

        public ServiceProvider Provider { get; }
        public ClientBirthdayTaskSchedulerService Service { get; }

        public static async Task<BirthdayFixture> CreateAsync(bool enabled, INotificationService? notifications = null)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<PaymentContext>(options => options.UseSqlite(connection));
            if (notifications is not null)
            {
                services.AddSingleton(notifications);
            }
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                await db.Database.EnsureCreatedAsync();
                var role = new Role { Name = "employee", Description = "Сотрудник" };
                var user = new User
                {
                    Email = "responsible@example.com",
                    FullName = "Ответственный сотрудник",
                    PasswordHash = "test",
                    IsActive = true,
                    IsApproved = true,
                    Role = role,
                };
                db.Clients.Add(new Client
                {
                    Id = 1,
                    Name = "Виктория Мокшина",
                    BirthDate = new DateTime(1990, 9, 28),
                    ResponsibleUser = user,
                    IsActive = true,
                });
                db.SystemSettings.AddRange(
                    new SystemSetting
                    {
                        Key = ClientBirthdayTaskSchedulerService.EnabledSettingKey,
                        Value = enabled ? "true" : "false",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    },
                    new SystemSetting
                    {
                        Key = "tasks.automation.caseControl.scheduleTimeZone",
                        Value = "UTC",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                await db.SaveChangesAsync();
            }

            var configuration = new ConfigurationBuilder().Build();
            var service = new ClientBirthdayTaskSchedulerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<ClientBirthdayTaskSchedulerService>.Instance);
            return new BirthdayFixture(connection, provider, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    public class RecordingBirthdayNotificationServiceProxy : DispatchProxy
    {
        public List<(TaskJournalItem Task, int ActorUserId)> AssignedTasks { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(INotificationService.NotifyTaskAssignedAsync) && args is not null)
            {
                AssignedTasks.Add(((TaskJournalItem)args[0]!, (int)args[2]!));
                return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
