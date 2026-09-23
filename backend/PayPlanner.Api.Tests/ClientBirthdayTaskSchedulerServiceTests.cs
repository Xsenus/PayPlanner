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
                Email = "birthday@example.com", FullName = "Именинник", PasswordHash = "test",
                IsActive = true, IsApproved = true, IsEmployee = true,
                DateOfBirth = new DateTime(1990, 9, 28), RoleId = assignee.RoleId,
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
                Email = "birthday@example.com", FullName = "Именинник", PasswordHash = "test",
                IsActive = true, IsApproved = true, IsEmployee = true,
                DateOfBirth = new DateTime(1990, 9, 28), RoleId = roleId,
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
                Email = "selected@example.com", FullName = "Назначенный сотрудник", PasswordHash = "test",
                IsActive = true, IsApproved = true, RoleId = roleId,
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

        public static async Task<BirthdayFixture> CreateAsync(bool enabled)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<PaymentContext>(options => options.UseSqlite(connection));
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
}
