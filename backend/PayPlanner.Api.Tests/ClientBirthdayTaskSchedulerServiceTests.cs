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
