using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.Notifications;
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
            new SystemSetting { Key = NotificationDeliveryRetentionService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.RetentionDaysSettingKey, Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.IntervalHoursSettingKey, Value = "12", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.BatchSizeSettingKey, Value = "25", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncSchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncSchedulerService.IntervalMinutesSettingKey, Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncSchedulerService.BatchSizeSettingKey, Value = "50", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncSchedulerService.LookAheadDaysSettingKey, Value = "60", CreatedAt = now, UpdatedAt = now },
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
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "task:1:email",
            Channel = "email",
            Subject = "Email failed",
            Status = "failed",
            Error = "SMTP temporary error",
            CreatedAt = now.AddMinutes(-30),
        });
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "task:old:email",
            Channel = "email",
            Subject = "Old delivery",
            Status = NotificationDeliveryStatus.Sent,
            CreatedAt = now.AddDays(-40),
        });
        scope.Db.YandexDiskCrmSyncStates.Add(new YandexDiskCrmSyncState
        {
            DocumentKind = "contract",
            ObjectType = "contract",
            ObjectId = 10,
            FileName = "contract-10.pdf",
            Status = YandexDiskCrmSyncStatus.Failed,
            LastError = "Temporary Yandex Disk error",
            AttemptCount = 1,
            CreatedAt = now.AddMinutes(-40),
            UpdatedAt = now.AddMinutes(-30),
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
                    ExternalCalendarSync = true,
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

        Assert.Equal(10, dto.Items.Count);
        Assert.Contains(dto.Items, item => item.Key == "database-backup" && item.Enabled && item.IntervalText.Contains("02:00"));
        Assert.Contains(dto.Items, item => item.Key == "task-reminders" && item.PendingCount == 1 && item.LastActivityDescription == "Reminder sent");
        Assert.Contains(dto.Items, item => item.Key == "notification-retry" && item.PendingCount == 1 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == SystemJobKeys.YandexDiskCrmSync && item.PendingCount == 1 && item.LastProcessedCount == 20 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "notification-delivery-retention" && item.PendingCount == 1 && item.LastProcessedCount == 25 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "external-calendar-sync" && item.Status == "warning" && item.LastProcessedCount == 50 && item.ManualRunEnabled);
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
    public async Task NotificationDeliveryRetentionService_DeletesOnlyExpiredCompletedDeliveries()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = NotificationDeliveryRetentionService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.RetentionDaysSettingKey, Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.BatchSizeSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now });
        scope.Db.NotificationDeliveries.AddRange(
            new NotificationDelivery
            {
                EventKey = "old-sent",
                Channel = NotificationChannels.Email,
                Subject = "Old sent",
                Status = NotificationDeliveryStatus.Sent,
                CreatedAt = now.AddDays(-45),
            },
            new NotificationDelivery
            {
                EventKey = "old-failed",
                Channel = NotificationChannels.Telegram,
                Subject = "Old failed",
                Status = NotificationDeliveryStatus.Failed,
                CreatedAt = now.AddDays(-60),
            },
            new NotificationDelivery
            {
                EventKey = "old-pending",
                Channel = NotificationChannels.Email,
                Subject = "Old pending",
                Status = NotificationDeliveryStatus.Pending,
                CreatedAt = now.AddDays(-90),
            },
            new NotificationDelivery
            {
                EventKey = "fresh-sent",
                Channel = NotificationChannels.Email,
                Subject = "Fresh sent",
                Status = NotificationDeliveryStatus.Sent,
                CreatedAt = now.AddDays(-2),
            });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new NotificationDeliveryRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationDeliveryRetentionService>.Instance);

        Assert.Equal(2, await service.CountEligibleDeliveriesAsync(CancellationToken.None));

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(2, result.DeletedCount);
        scope.Db.ChangeTracker.Clear();
        var eventKeys = await scope.Db.NotificationDeliveries
            .AsNoTracking()
            .Select(item => item.EventKey)
            .ToListAsync();
        Assert.DoesNotContain("old-sent", eventKeys);
        Assert.DoesNotContain("old-failed", eventKeys);
        Assert.Contains("old-pending", eventKeys);
        Assert.Contains("fresh-sent", eventKeys);
    }

    [Fact]
    public async Task YandexDiskCrmSyncSchedulerService_CountsOnlyDueRetryItems()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = YandexDiskCrmSyncSchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = YandexDiskCrmSyncSchedulerService.DelayMinutesSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = YandexDiskCrmSyncSchedulerService.MaxAttemptsSettingKey, Value = "3", CreatedAt = now, UpdatedAt = now });
        scope.Db.YandexDiskCrmSyncStates.AddRange(
            new YandexDiskCrmSyncState
            {
                DocumentKind = "contract",
                ObjectType = "contract",
                ObjectId = 1,
                Status = YandexDiskCrmSyncStatus.Failed,
                AttemptCount = 1,
                CreatedAt = now.AddMinutes(-30),
                UpdatedAt = now.AddMinutes(-20),
            },
            new YandexDiskCrmSyncState
            {
                DocumentKind = "invoice",
                ObjectType = "payment",
                ObjectId = 2,
                Status = YandexDiskCrmSyncStatus.Failed,
                AttemptCount = 1,
                CreatedAt = now.AddMinutes(-30),
                UpdatedAt = now.AddMinutes(-2),
            },
            new YandexDiskCrmSyncState
            {
                DocumentKind = "act",
                ObjectType = "act",
                ObjectId = 3,
                Status = YandexDiskCrmSyncStatus.Failed,
                AttemptCount = 3,
                CreatedAt = now.AddMinutes(-30),
                UpdatedAt = now.AddMinutes(-20),
            },
            new YandexDiskCrmSyncState
            {
                DocumentKind = "caseDocument",
                ObjectType = "caseDocument",
                ObjectId = 4,
                Status = YandexDiskCrmSyncStatus.Success,
                AttemptCount = 1,
                CreatedAt = now.AddMinutes(-30),
                UpdatedAt = now.AddMinutes(-20),
            });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new YandexDiskCrmSyncSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<YandexDiskCrmSyncSchedulerService>.Instance);

        Assert.Equal(1, await service.CountEligibleItemsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task YandexDiskCrmSyncStateRecorder_RecordFailureUpsertsRetryState()
    {
        await using var scope = await CreateDbScopeAsync();

        await YandexDiskCrmSyncStateRecorder.RecordFailureAsync(
            scope.Db,
            "contract",
            "contract",
            15,
            "contract-15.pdf",
            null,
            new InvalidOperationException("Temporary network error"),
            CancellationToken.None);
        await scope.Db.SaveChangesAsync();

        await YandexDiskCrmSyncStateRecorder.RecordFailureAsync(
            scope.Db,
            "contract",
            "contract",
            15,
            "contract-15.pdf",
            null,
            new InvalidOperationException("Second temporary error"),
            CancellationToken.None);
        await scope.Db.SaveChangesAsync();

        var state = await scope.Db.YandexDiskCrmSyncStates.AsNoTracking().SingleAsync();
        Assert.Equal(YandexDiskCrmSyncStatus.Failed, state.Status);
        Assert.Equal(2, state.AttemptCount);
        Assert.Equal("Second temporary error", state.LastError);
        Assert.Equal("contract-15.pdf", state.FileName);
    }

    [Fact]
    public async Task CalendarSyncSchedulerService_CountsCandidatesOnlyAfterReadiness()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = CalendarSyncSchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncSchedulerService.BatchSizeSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncSchedulerService.LookAheadDaysSettingKey, Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting
            {
                Key = "calendarSync.settings",
                Value = JsonSerializer.Serialize(new CalendarSyncSettingsDto
                {
                    SyncMode = "crmMaster",
                    ConflictResolutionMode = "manualConfirmation",
                    RequireManualConfirmation = true,
                    DryRunConfirmed = true,
                    UpdatedAt = now,
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                CreatedAt = now,
                UpdatedAt = now,
            });
        scope.Db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ObjectName = "CASE-1",
                Title = "Future task",
                DueAt = now.AddDays(2),
                Status = "new",
            },
            new TaskJournalItem
            {
                ObjectName = "CASE-2",
                Title = "Completed task",
                DueAt = now.AddDays(3),
                Status = "done",
            });
        var client = new Client { Name = "Calendar client" };
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        scope.Db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "CASE-3",
            Status = ClientCaseStatus.Open,
            Title = "Case with hearing",
            NextHearingAt = now.AddDays(5),
        });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new CalendarSyncSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions
                {
                    ExternalCalendarSync = true,
                },
            }),
            NullLogger<CalendarSyncSchedulerService>.Instance);

        var state = await service.BuildDiagnosticsStateAsync(CancellationToken.None);

        Assert.True(state.SchedulerEnabled);
        Assert.True(state.FeatureEnabled);
        Assert.False(state.Readiness.ReadyToConnect);
        Assert.Equal(2, state.CandidateCount);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.False(result.ReadyToConnect);
        Assert.Equal(0, result.ProcessedCount);
        Assert.True(result.SkippedCount > 0);
    }

    [Fact]
    public async Task TaskReminderService_BackfillsUpcomingCaseDeadlineTasks()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var role = new Role { Name = $"deadline-role-{Guid.NewGuid():N}", Description = "Deadline role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var responsible = new User
        {
            Email = $"deadline-user-{Guid.NewGuid():N}@example.com",
            FullName = "Deadline Owner",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        var client = new Client { Name = "Deadline client" };
        scope.Db.Users.Add(responsible);
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        scope.Db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            ResponsibleUserId = responsible.Id,
            CaseNumber = "CASE-DEADLINE-1",
            Title = "Case with deadline",
            Status = ClientCaseStatus.Open,
            DueDate = now.AddDays(8),
        });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new TaskJournalReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Notifications:TaskReminders:DeadlineSyncLookAheadDays"] = "14",
                    ["Notifications:TaskReminders:DeadlineSyncBatchSize"] = "10",
                })
                .Build(),
            NullLogger<TaskJournalReminderNotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        scope.Db.ChangeTracker.Clear();
        var deadlineTask = await scope.Db.TaskJournalItems
            .AsNoTracking()
            .SingleAsync(task => task.TagsJson.Contains("case-deadline:due"));
        Assert.Equal(responsible.Id, deadlineTask.AssigneeUserId);
        Assert.Contains("before7d", deadlineTask.RemindersJson);
        Assert.Contains("dailyOverdue", deadlineTask.RemindersJson);
        Assert.Contains("inApp", deadlineTask.ChannelsJson);
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
