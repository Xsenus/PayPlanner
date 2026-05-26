using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SystemJobsControllerTests
{
    [Fact]
    public async Task GetDiagnostics_ReturnsKnownBackgroundJobsWithoutSecrets()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = "database-backup.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "database-backup.schedule-local", Value = "02:00,14:00", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.inactiveDays", Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.limit", Value = "7", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = ClientCaseCourtSyncSchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey, Value = "90", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey, Value = "12", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "user-activity.retention-days", Value = "30", CreatedAt = now, UpdatedAt = now });
        var role = new Role { Name = $"admin-{Guid.NewGuid():N}", Description = "Admin" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var user = new User
        {
            Email = $"admin-{Guid.NewGuid():N}@example.com",
            FullName = "Admin",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        var client = new Client { Name = "Client" };
        scope.Db.Users.Add(user);
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        scope.Db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "CASE-1",
            Status = ClientCaseStatus.Open,
            CreatedAt = now.AddDays(-20),
            LastViewedAt = now.AddDays(-20),
            ActivityConfirmedAt = now.AddDays(-20),
            InactivityRemindersEnabled = true,
            CourtUrl = "https://example.sudrf.ru/case",
            EventHistory = "Судебный источник проверен.",
            AuditUpdatedAt = now.AddHours(-2),
        });
        scope.Db.TaskJournalItems.Add(new TaskJournalItem
        {
            ObjectName = "CASE-1",
            Title = "Task with reminder",
            AssigneeUserId = user.Id,
            DueAt = now.AddDays(1),
            RemindersJson = "[\"before1d\"]",
            ChannelsJson = "[\"in-app\"]",
        });
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "task:1:reminder:before1d",
            Channel = "in-app",
            Subject = "Reminder sent",
            CreatedAt = now.AddMinutes(-30),
        });
        scope.Db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "client-case",
            Action = "Automation",
            Description = "Control task created.",
            MetadataJson = "{\"event\":\"caseInactivityControlTaskCreated\"}",
            CreatedAt = now.AddDays(-1),
        });
        scope.Db.MoneyBalanceReconciliationResults.Add(new MoneyBalanceReconciliationResult
        {
            Date = now.Date.AddDays(-1),
            Source = MoneyOperationSource.Cash,
            AccountName = "Main",
            CheckedAt = now.AddHours(-3),
            IssueCount = 2,
        });
        scope.Db.UserActivityLogs.Add(new UserActivityLog
        {
            UserId = user.Id,
            UserFullName = "Admin",
            Category = "settings",
            Action = "open",
            Status = UserActivityStatus.Success,
            CreatedAt = now.AddDays(-5),
        });
        await scope.Db.SaveChangesAsync();

        var controller = new SystemJobsController(
            scope.Db,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Notifications:TaskReminders:Enabled"] = "true",
                    ["Notifications:TaskReminders:PollSeconds"] = "45",
                })
                .Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions
                {
                    ExternalCourtSync = true,
                },
            }),
            new FakeSystemJobRunStateService(new Dictionary<string, SystemJobRunState>
            {
                [SystemJobKeys.CourtSync] = new()
                {
                    JobKey = SystemJobKeys.CourtSync,
                    Status = "success",
                    StartedAt = now.AddMinutes(-5),
                    FinishedAt = now.AddMinutes(-4),
                    ProcessedCount = 3,
                    Summary = "Проверено дел: 3.",
                },
            }));

        var result = await controller.GetDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);

        Assert.Equal(6, dto.Items.Count);
        Assert.Contains(dto.Items, item => item.Key == "database-backup" && item.Enabled && item.IntervalText.Contains("02:00"));
        Assert.Contains(dto.Items, item => item.Key == "task-reminders" && item.PendingCount == 1 && item.LastActivityDescription == "Reminder sent");
        Assert.Contains(dto.Items, item => item.Key == "case-inactivity-control" && item.PendingCount == 1 && item.LastProcessedCount == 7);
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.Status == "active" && item.PendingCount == 1 && item.LastProcessedCount == 12);
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.LastRunStatus == "success" && item.LastRunStatusText == "Успешно");
        Assert.Contains(dto.Items, item => item.Key == "database-backup" && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "money-balance-reconciliation" && item.LastProcessedCount == 2);
        Assert.Contains(dto.Items, item => item.Key == "user-activity-retention" && item.Enabled && item.NextRunHint!.Contains("30"));
        Assert.All(dto.Items, item =>
        {
            Assert.DoesNotContain("password", item.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", item.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", item.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task GetDiagnostics_MarksCourtSyncBlockedWhenFeatureFlagIsOff()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting
        {
            Key = ClientCaseCourtSyncSchedulerService.EnabledSettingKey,
            Value = "true",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await scope.Db.SaveChangesAsync();
        var controller = new SystemJobsController(
            scope.Db,
            new ConfigurationBuilder().Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions
                {
                    ExternalCourtSync = false,
                },
            }));

        var result = await controller.GetDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);
        var courtSync = Assert.Single(dto.Items, item => item.Key == "court-sync");

        Assert.False(courtSync.Enabled);
        Assert.Equal("blocked", courtSync.Status);
        Assert.Equal("Ожидает feature flag", courtSync.StatusText);
        Assert.False(courtSync.ManualRunEnabled);
    }

    [Fact]
    public async Task RunJob_ReturnsBadRequestWhenManualRunIsDisabled()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.Add(new SystemSetting
        {
            Key = ClientCaseCourtSyncSchedulerService.EnabledSettingKey,
            Value = "true",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await scope.Db.SaveChangesAsync();
        var controller = new SystemJobsController(
            scope.Db,
            new ConfigurationBuilder().Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions
                {
                    ExternalCourtSync = false,
                },
            }));

        var result = await controller.RunJob(SystemJobKeys.CourtSync, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SystemJobRunStateService_RoundTripsLastRunState()
    {
        await using var scope = await CreateDbScopeAsync();
        var factory = new TestPaymentContextFactory(scope.Options);
        var service = new SystemJobRunStateService(factory, NullLogger<SystemJobRunStateService>.Instance);
        var startedAt = DateTime.UtcNow.AddMinutes(-2);

        await service.RecordSuccessAsync(
            SystemJobKeys.TaskReminders,
            startedAt,
            processedCount: 4,
            skippedCount: 1,
            summary: "Отправлены напоминания.",
            CancellationToken.None);

        var states = await service.GetLastRunsAsync([SystemJobKeys.TaskReminders], CancellationToken.None);
        var state = Assert.Single(states.Values);

        Assert.Equal(SystemJobKeys.TaskReminders, state.JobKey);
        Assert.Equal("success", state.Status);
        Assert.Equal(4, state.ProcessedCount);
        Assert.Equal(1, state.SkippedCount);
        Assert.Equal("Отправлены напоминания.", state.Summary);
        Assert.Null(state.ErrorMessage);
    }

    private static async Task<TestDbScope> CreateDbScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();

        return new TestDbScope(connection, options, db);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeSystemJobRunStateService(IReadOnlyDictionary<string, SystemJobRunState> states) : ISystemJobRunStateService
    {
        public Task RecordSuccessAsync(
            string jobKey,
            DateTime startedAt,
            int? processedCount,
            int? skippedCount,
            string summary,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordFailureAsync(
            string jobKey,
            DateTime startedAt,
            Exception exception,
            string? summary = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, SystemJobRunState>> GetLastRunsAsync(
            IEnumerable<string> jobKeys,
            CancellationToken cancellationToken = default)
        {
            var requested = jobKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, SystemJobRunState>>(
                states
                    .Where(item => requested.Contains(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed class TestPaymentContextFactory(DbContextOptions<PaymentContext> options) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext() => new(options, useObjectAuditModel: false);
    }

    private sealed class TestDbScope(SqliteConnection connection, DbContextOptions<PaymentContext> options, PaymentContext db) : IAsyncDisposable
    {
        public DbContextOptions<PaymentContext> Options { get; } = options;
        public PaymentContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
