using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SystemJobsControllerTests
{
    [Fact]
    public async Task AdminFilter_ReturnsForbid_ForUserWithoutAdminRoleInDatabase()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await CreateUserAsync(scope.Db, "user");
        var controller = CreateController(scope.Db);
        var context = CreateActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.False(nextCalled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task AdminFilter_AllowsUserWithAdminRoleInDatabase()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await CreateUserAsync(scope.Db, "admin");
        var controller = CreateController(scope.Db);
        var context = CreateActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task GetDiagnostics_ReturnsKnownBackgroundJobsWithoutSecrets()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
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
            new SystemSetting
            {
                Key = CourtSyncMetricsRollupService.ArchiveSettingKey,
                Value = JsonSerializer.Serialize(
                    new CourtSyncMetricsArchive
                    {
                        Days =
                        [
                            new CourtSyncMetricsDailyBucket
                            {
                                Day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                RequestCount = 20,
                                FailedRequestCount = 2,
                                TotalLatencyTicks = TimeSpan.FromSeconds(4).Ticks,
                                MaxLatencyTicks = TimeSpan.FromMilliseconds(500).Ticks,
                                DuplicateSkipCount = 3,
                                DocumentCount = 4,
                                DocumentBytes = 8_192,
                            },
                        ],
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SystemSetting { Key = NotificationDeliveryRetentionService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.RetentionDaysSettingKey, Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.IntervalHoursSettingKey, Value = "12", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationDeliveryRetentionService.BatchSizeSettingKey, Value = "25", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncLogRetentionService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncLogRetentionService.RetentionDaysSettingKey, Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncLogRetentionService.IntervalHoursSettingKey, Value = "24", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncLogRetentionService.BatchSizeSettingKey, Value = "100", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.IntervalHoursSettingKey, Value = "6", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.BatchSizeSettingKey, Value = "15", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.WarningAgeDaysSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.CriticalAgeDaysSettingKey, Value = "20", CreatedAt = now, UpdatedAt = now },
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
        client.ResponsibleUserId = user.Id;
        scope.Db.Payments.AddRange(
            new Payment
            {
                ClientId = client.Id,
                Type = PaymentType.Income,
                DocumentKind = DocumentKind.Payment,
                Amount = 7_100m,
                PaidAmount = 7_100m,
                Status = PaymentStatus.Completed,
                IsPaid = true,
                Date = now.Date,
                PlannedDate = now.Date,
                PaidDate = now.Date,
                LastPaymentDate = now.Date,
                Description = "Advance candidate",
                AdvanceReviewStatus = PaymentAdvanceReviewStatus.None,
            },
            new Payment
            {
                ClientId = client.Id,
                Type = PaymentType.Income,
                DocumentKind = DocumentKind.Payment,
                Amount = 8_200m,
                PaidAmount = 8_200m,
                Status = PaymentStatus.Completed,
                IsPaid = true,
                Date = now.Date.AddDays(-40),
                PlannedDate = now.Date.AddDays(-40),
                PaidDate = now.Date.AddDays(-40),
                LastPaymentDate = now.Date.AddDays(-40),
                Description = "Critical advance candidate",
                AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision,
            });
        scope.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = user.Id,
            EventKey = "payment:advance-review:diagnostics",
            Type = UserNotificationTypes.PaymentAdvanceReviewEscalation,
            Title = "Advance review escalated",
            BodyPreview = "Critical advance requires review",
            Tone = "red",
            ObjectType = "payment",
            ObjectId = 1,
            MetadataJson = "{}",
            CreatedAt = now.AddMinutes(-5),
        });
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
        scope.Db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog
        {
            Operation = "upsert",
            Message = "Old Google log",
            CreatedAt = now.AddDays(-40),
        });
        scope.Db.YandexCalendarSyncLogs.Add(new YandexCalendarSyncLog
        {
            Operation = "upsert",
            Message = "Old Yandex log",
            CreatedAt = now.AddDays(-45),
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

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:TaskReminders:Enabled"] = "true",
                ["Notifications:TaskReminders:PollSeconds"] = "45",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        using var serviceProvider = services.BuildServiceProvider();
        var notificationRetryRunner = new NotificationRetrySchedulerService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<NotificationRetrySchedulerService>.Instance);
        var courtSyncMetrics = new CourtSyncMetrics();
        courtSyncMetrics.RecordRequest(TimeSpan.FromMilliseconds(120), failed: false);
        courtSyncMetrics.RecordRequest(TimeSpan.FromMilliseconds(280), failed: true);
        courtSyncMetrics.RecordDuplicateSkipped();
        courtSyncMetrics.RecordDocument(4_096);
        var controller = new SystemJobsController(
            scope.Db,
            configuration,
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
                    LastSuccessAt = now.AddMinutes(-4),
                    ProcessedCount = 3,
                    Summary = "Проверено дел: 3.",
                },
            }),
            notificationRetryRunner: notificationRetryRunner,
            courtSyncMetrics: courtSyncMetrics);

        interceptor.Commands.Clear();
        var result = await controller.GetDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);

        Assert.Equal(12, dto.Items.Count);
        Assert.Contains(dto.Items, item => item.Key == "database-backup" && item.Enabled && item.IntervalText.Contains("02:00"));
        Assert.Contains(dto.Items, item => item.Key == "task-reminders" && item.PendingCount == 1 && item.LastActivityDescription == "Reminder sent");
        Assert.Contains(dto.Items, item => item.Key == "notification-retry" && item.PendingCount == 1 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == SystemJobKeys.YandexDiskCrmSync && item.PendingCount == 1 && item.LastProcessedCount == 20 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "notification-delivery-retention" && item.PendingCount == 1 && item.LastProcessedCount == 25 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == SystemJobKeys.CalendarSyncLogRetention && item.PendingCount == 2 && item.LastProcessedCount == 200 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == SystemJobKeys.PaymentAdvanceReviewMonitoring && item.Status == "warning" && item.PendingCount == 2 && item.LastProcessedCount == 15 && item.LastActivityDescription == "Advance review escalated" && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "external-calendar-sync" && item.Status == "warning" && item.LastProcessedCount == 50 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item =>
            item.Key == "case-inactivity-control" &&
            item.PendingCount == 1 &&
            item.LastProcessedCount == 7 &&
            item.IntervalText.StartsWith("Ежедневно в 09:00", StringComparison.Ordinal));
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.Status == "active" && item.PendingCount == 1 && item.LastProcessedCount == 12);
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.LastRunStatus == "success" && item.LastRunStatusText == "Успешно");
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.LastSuccessAt == now.AddMinutes(-4) && item.LastRunDurationMs == 60000);
        var courtSyncMetricsDto = Assert.Single(dto.Items, item => item.Key == "court-sync").CourtSyncMetrics;
        Assert.NotNull(courtSyncMetricsDto);
        Assert.Equal(2, courtSyncMetricsDto.RequestCount);
        Assert.Equal(1, courtSyncMetricsDto.FailedRequestCount);
        Assert.Equal(200, courtSyncMetricsDto.AverageLatencyMs);
        Assert.Equal(280, courtSyncMetricsDto.MaxLatencyMs);
        Assert.Equal(1, courtSyncMetricsDto.DuplicateSkipCount);
        Assert.Equal(1, courtSyncMetricsDto.DocumentCount);
        Assert.Equal(4_096, courtSyncMetricsDto.DocumentBytes);
        Assert.NotNull(courtSyncMetricsDto.Retained);
        Assert.Equal(20, courtSyncMetricsDto.Retained.RequestCount);
        Assert.Equal(2, courtSyncMetricsDto.Retained.FailedRequestCount);
        Assert.Equal(200, courtSyncMetricsDto.Retained.AverageLatencyMs);
        Assert.Equal(500, courtSyncMetricsDto.Retained.MaxLatencyMs);
        Assert.Equal(8_192, courtSyncMetricsDto.Retained.DocumentBytes);
        Assert.Contains(dto.Items, item => item.Key == "database-backup" && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "money-balance-reconciliation" && item.LastProcessedCount == 2);
        Assert.Contains(dto.Items, item => item.Key == "user-activity-retention" && item.Enabled && item.NextRunHint!.Contains("30"));
        var courtSyncStatisticsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("ClientCases", StringComparison.Ordinal) &&
            command.Contains("EventHistory", StringComparison.Ordinal) &&
            command.Contains("\"Value\"", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
        Assert.Contains("COUNT", courtSyncStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", courtSyncStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        var caseControlStatisticsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("ClientCases", StringComparison.Ordinal) &&
            command.Contains("ObjectAuditEvents", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
        Assert.Contains("COUNT", caseControlStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", caseControlStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Single(interceptor.Commands, command =>
            command.Contains(NotificationRetrySchedulerService.EnabledSettingKey, StringComparison.Ordinal));
        var taskReminderStatisticsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("TaskJournalItems", StringComparison.Ordinal) &&
            command.Contains("NotificationDeliveries", StringComparison.Ordinal) &&
            command.Contains("\"Value\"", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
        Assert.Contains("COUNT", taskReminderStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", taskReminderStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        var calendarLogStatisticsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("GoogleCalendarSyncLogs", StringComparison.Ordinal) &&
            command.Contains("YandexCalendarSyncLogs", StringComparison.Ordinal));
        Assert.Equal(2, Regex.Count(calendarLogStatisticsCommand, @"SELECT\s+COUNT\(\*\)", RegexOptions.IgnoreCase));
        Assert.Equal(2, Regex.Count(calendarLogStatisticsCommand, @"SELECT\s+MIN\(", RegexOptions.IgnoreCase));
        var deliveryRetentionCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("NotificationDeliveries", StringComparison.Ordinal) &&
            command.Contains("OldestAt", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
        Assert.Contains("COUNT", deliveryRetentionCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", deliveryRetentionCommand, StringComparison.OrdinalIgnoreCase);
        var yandexDiskStatisticsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("YandexDiskCrmSyncStates", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
        Assert.Contains("COUNT", yandexDiskStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", yandexDiskStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        var advanceReviewStatisticsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("Payments", StringComparison.Ordinal) &&
            command.Contains("UserNotifications", StringComparison.Ordinal) &&
            command.Contains("PendingCount", StringComparison.Ordinal) &&
            command.Contains("CriticalPendingCount", StringComparison.Ordinal));
        Assert.Contains("COUNT", advanceReviewStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.All(dto.Items, item =>
        {
            Assert.DoesNotContain("password", item.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", item.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", item.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task GetDiagnostics_PreservesRetentionActivityAndSkipsDisabledCandidateCounts()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var oldestAt = DateTime.UtcNow.AddDays(-60);
        var oldestDeliveryAt = oldestAt.AddDays(5);
        var lastReminderAt = oldestDeliveryAt.AddDays(1);
        var user = await CreateUserAsync(scope.Db, "user");
        scope.Db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = CalendarSyncLogRetentionService.EnabledSettingKey,
                Value = "false",
                CreatedAt = oldestAt,
                UpdatedAt = oldestAt,
            },
            new SystemSetting
            {
                Key = NotificationDeliveryRetentionService.EnabledSettingKey,
                Value = "false",
                CreatedAt = oldestAt,
                UpdatedAt = oldestAt,
            },
            new SystemSetting
            {
                Key = PaymentAdvanceReviewMonitoringService.EnabledSettingKey,
                Value = "false",
                CreatedAt = oldestAt,
                UpdatedAt = oldestAt,
            },
            new SystemSetting
            {
                Key = YandexDiskCrmSyncSchedulerService.EnabledSettingKey,
                Value = "false",
                CreatedAt = oldestAt,
                UpdatedAt = oldestAt,
            },
            new SystemSetting
            {
                Key = "tasks.automation.caseControl.enabled",
                Value = "false",
                CreatedAt = oldestAt,
                UpdatedAt = oldestAt,
            });
        scope.Db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog
        {
            Operation = "upsert",
            Message = "Preserved old Google log",
            CreatedAt = oldestAt,
        });
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "retention-disabled",
            Channel = NotificationChannels.Email,
            Subject = "Preserved old delivery",
            Status = NotificationDeliveryStatus.Sent,
            CreatedAt = oldestDeliveryAt,
        });
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "task:15:reminder:before1d",
            Channel = NotificationChannels.InApp,
            Subject = "Preserved task reminder",
            Status = NotificationDeliveryStatus.Sent,
            CreatedAt = lastReminderAt,
        });
        scope.Db.TaskJournalItems.Add(new TaskJournalItem
        {
            ObjectName = "Reminder candidate",
            Title = "Disabled reminder candidate",
            AssigneeUserId = user.Id,
            DueAt = DateTime.UtcNow.AddDays(1),
            RemindersJson = "[\"before1d\"]",
            ChannelsJson = "[\"in-app\"]",
        });
        scope.Db.YandexDiskCrmSyncStates.Add(new YandexDiskCrmSyncState
        {
            DocumentKind = "contract",
            ObjectType = "contract",
            ObjectId = 15,
            FileName = "contract-15.pdf",
            Status = YandexDiskCrmSyncStatus.Failed,
            LastError = "Preserved Yandex Disk error",
            AttemptCount = 1,
            CreatedAt = oldestAt,
            UpdatedAt = oldestDeliveryAt,
        });
        var lastCaseAutomationAt = oldestDeliveryAt.AddDays(2);
        scope.Db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "client-case",
            Action = "Automation",
            Description = "Preserved case control activity",
            MetadataJson = "{\"event\":\"caseInactivityControlTaskCreated\"}",
            CreatedAt = lastCaseAutomationAt,
        });
        var lastAdvanceReviewAt = oldestDeliveryAt.AddDays(3);
        scope.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = user.Id,
            EventKey = "payment:advance-review:preserved",
            Type = UserNotificationTypes.PaymentAdvanceReviewRequired,
            Title = "Preserved advance review notification",
            BodyPreview = "Advance review remains visible",
            Tone = "amber",
            ObjectType = "payment",
            ObjectId = 15,
            MetadataJson = "{}",
            CreatedAt = lastAdvanceReviewAt,
        });
        await scope.Db.SaveChangesAsync();

        interceptor.Commands.Clear();
        var controller = new SystemJobsController(
            scope.Db,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Notifications:TaskReminders:Enabled"] = "false",
                })
                .Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()));
        var result = await controller.GetDiagnostics(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);
        var retention = Assert.Single(dto.Items, item => item.Key == SystemJobKeys.CalendarSyncLogRetention);
        var deliveryRetention = Assert.Single(dto.Items,
            item => item.Key == SystemJobKeys.NotificationDeliveryRetention);
        var advanceReview = Assert.Single(dto.Items,
            item => item.Key == SystemJobKeys.PaymentAdvanceReviewMonitoring);
        var yandexDisk = Assert.Single(dto.Items,
            item => item.Key == SystemJobKeys.YandexDiskCrmSync);
        var taskReminders = Assert.Single(dto.Items,
            item => item.Key == SystemJobKeys.TaskReminders);
        var caseControl = Assert.Single(dto.Items,
            item => item.Key == SystemJobKeys.CaseInactivityControl);

        Assert.False(retention.Enabled);
        Assert.Equal(0, retention.PendingCount);
        Assert.Equal(oldestAt, retention.LastActivityAt);
        Assert.False(deliveryRetention.Enabled);
        Assert.Equal(0, deliveryRetention.PendingCount);
        Assert.Equal(oldestDeliveryAt, deliveryRetention.LastActivityAt);
        Assert.False(advanceReview.Enabled);
        Assert.Equal(0, advanceReview.PendingCount);
        Assert.Equal(lastAdvanceReviewAt, advanceReview.LastActivityAt);
        Assert.Equal("Preserved advance review notification", advanceReview.LastActivityDescription);
        Assert.False(yandexDisk.Enabled);
        Assert.Equal(0, yandexDisk.PendingCount);
        Assert.Equal(oldestDeliveryAt, yandexDisk.LastActivityAt);
        Assert.Contains("Preserved Yandex Disk error", yandexDisk.LastActivityDescription);
        Assert.False(taskReminders.Enabled);
        Assert.Equal(0, taskReminders.PendingCount);
        Assert.Equal(lastReminderAt, taskReminders.LastActivityAt);
        Assert.Equal("Preserved task reminder", taskReminders.LastActivityDescription);
        Assert.False(caseControl.Enabled);
        Assert.Equal(0, caseControl.PendingCount);
        Assert.Equal(lastCaseAutomationAt, caseControl.LastActivityAt);
        Assert.Equal("Preserved case control activity", caseControl.LastActivityDescription);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("TaskJournalItems", StringComparison.Ordinal) &&
            command.Contains("NotificationDeliveries", StringComparison.Ordinal) &&
            command.Contains("\"Value\"", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("GoogleCalendarSyncLogs", StringComparison.Ordinal) &&
            command.Contains("YandexCalendarSyncLogs", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("NotificationDeliveries", StringComparison.Ordinal) &&
            command.Contains("OldestAt", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
        Assert.DoesNotContain(interceptor.Commands, command =>
            command.Contains("Payments", StringComparison.Ordinal) &&
            command.Contains("PendingCount", StringComparison.Ordinal) &&
            command.Contains("CriticalPendingCount", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("UserNotifications", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("YandexDiskCrmSyncStates", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDiagnostics_CaseControlCountsOnlyCasesWithAvailableAssignee()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var responsible = await CreateUserAsync(scope.Db, $"case-control-responsible-{Guid.NewGuid():N}");
        var assignedClient = new Client
        {
            Name = "Assigned client",
            ResponsibleUserId = responsible.Id,
        };
        var unassignedClient = new Client { Name = "Unassigned client" };
        scope.Db.Clients.AddRange(assignedClient, unassignedClient);
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = "tasks.automation.caseControl.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.inactiveDays", Value = "14", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.limit", Value = "25", CreatedAt = now, UpdatedAt = now });
        scope.Db.ClientCases.AddRange(
            new ClientCase
            {
                Client = assignedClient,
                Title = "Assigned inactive case",
                Status = ClientCaseStatus.Open,
                CreatedAt = now.AddDays(-30),
                LastViewedAt = now.AddDays(-30),
                ActivityConfirmedAt = now.AddDays(-30),
                InactivityRemindersEnabled = true,
            },
            new ClientCase
            {
                Client = unassignedClient,
                Title = "Unassigned inactive case",
                Status = ClientCaseStatus.Open,
                CreatedAt = now.AddDays(-30),
                LastViewedAt = now.AddDays(-30),
                ActivityConfirmedAt = now.AddDays(-30),
                InactivityRemindersEnabled = true,
            });
        await scope.Db.SaveChangesAsync();

        var result = await CreateController(scope.Db).GetDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);
        var caseControl = Assert.Single(dto.Items, item => item.Key == SystemJobKeys.CaseInactivityControl);
        Assert.Equal(1, caseControl.PendingCount);
        Assert.Contains("готовы 1 дел", caseControl.NextRunHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiagnostics_MarksCourtSyncBlockedWhenFeatureFlagIsOff()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var lastCourtSyncAt = DateTime.UtcNow.AddHours(-2);
        scope.Db.SystemSettings.Add(new SystemSetting
        {
            Key = ClientCaseCourtSyncSchedulerService.EnabledSettingKey,
            Value = "true",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var client = new Client { Name = "Court sync client" };
        scope.Db.Clients.Add(client);
        scope.Db.ClientCases.Add(new ClientCase
        {
            Client = client,
            Title = "Court sync candidate",
            CaseNumber = "COURT-BLOCKED-1",
            Status = ClientCaseStatus.Open,
            CourtUrl = "https://example.sudrf.ru/case/blocked-1",
            EventHistory = "судебная активность сохранена.",
            CreatedAt = lastCourtSyncAt,
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

        interceptor.Commands.Clear();
        var result = await controller.GetDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);
        var courtSync = Assert.Single(dto.Items, item => item.Key == "court-sync");

        Assert.False(courtSync.Enabled);
        Assert.Equal("blocked", courtSync.Status);
        Assert.Equal("Ожидает feature flag", courtSync.StatusText);
        Assert.Equal(0, courtSync.PendingCount);
        Assert.Equal(lastCourtSyncAt, courtSync.LastActivityAt);
        Assert.Contains("COURT-BLOCKED-1", courtSync.LastActivityDescription);
        Assert.False(courtSync.ManualRunEnabled);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("ClientCases", StringComparison.Ordinal) &&
            command.Contains("EventHistory", StringComparison.Ordinal) &&
            command.Contains("\"Value\"", StringComparison.Ordinal) &&
            command.Contains("EligibleCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDiagnostics_FormatsSystemLogDescriptionsInProfileTimeZone()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var role = new Role { Name = $"system-log-role-{Guid.NewGuid():N}", Description = "System log role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var admin = new User
        {
            Email = $"system-log-admin-{Guid.NewGuid():N}@example.com",
            FullName = "System Log Admin",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(admin);
        await scope.Db.SaveChangesAsync();
        scope.Db.UserSettings.Add(new UserSetting
        {
            UserId = admin.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = now,
            UpdatedAt = now,
        });
        scope.Db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = NotificationDeliveryRetentionService.EnabledSettingKey,
                Value = "true",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SystemSetting
            {
                Key = NotificationDeliveryRetentionService.RetentionDaysSettingKey,
                Value = "30",
                CreatedAt = now,
                UpdatedAt = now,
            });
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "task:timezone:email",
            Channel = NotificationChannels.Email,
            Subject = "Timezone delivery",
            Status = NotificationDeliveryStatus.Sent,
            CreatedAt = new DateTime(2026, 6, 4, 20, 30, 0, DateTimeKind.Utc),
        });
        await scope.Db.SaveChangesAsync();

        var controller = new SystemJobsController(
            scope.Db,
            new ConfigurationBuilder().Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString())
                ], "test"))
            }
        };

        var result = await controller.GetDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);
        var retention = Assert.Single(dto.Items, item => item.Key == SystemJobKeys.NotificationDeliveryRetention);
        Assert.Equal(
            "Самая старая техническая запись: 05.06.2026 00:30.",
            retention.LastActivityDescription);
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
    public async Task RunJob_UsesProfileTimeZoneForMoneyBalanceReconciliationDate()
    {
        await using var scope = await CreateDbScopeAsync();
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        var targetDate = selectedZone.ProfileToday.AddDays(-1);
        var role = new Role { Name = "admin", Description = "Money admin" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var user = new User
        {
            Email = $"money-admin-{Guid.NewGuid():N}@example.com",
            FullName = "Money Admin",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(user);
        await scope.Db.SaveChangesAsync();
        scope.Db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.TimeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        scope.Db.MoneyOperations.Add(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Timezone bank",
            Amount = 750m,
            Currency = "RUB",
            Date = targetDate,
            CreatedAt = DateTime.UtcNow,
        });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        await using var provider = services.BuildServiceProvider();
        var runner = new MoneyBalanceReconciliationSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoneyBalanceReconciliationSchedulerService>.Instance);
        var controller = new SystemJobsController(
            scope.Db,
            new ConfigurationBuilder().Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()),
            moneyBalanceRunner: runner);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Role, "admin"),
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                ], "Test")),
            },
        };

        var result = await controller.RunJob(SystemJobKeys.MoneyBalanceReconciliation, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobManualRunResultDto>(ok.Value);
        Assert.Contains($"{targetDate:dd.MM.yyyy}", dto.Summary);
        var saved = Assert.Single(await scope.Db.MoneyBalanceReconciliationResults.AsNoTracking().ToListAsync());
        Assert.Equal(targetDate, saved.Date);
        Assert.Equal(750m, saved.Income);
    }

    [Fact]
    public async Task PaymentAdvanceReviewMonitoringService_MarksOldCandidatesAndCreatesNotification()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var now = DateTime.UtcNow;
        var role = new Role { Name = $"advance-monitor-role-{Guid.NewGuid():N}", Description = "Advance monitor role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var responsible = new User
        {
            Email = $"advance-monitor-{Guid.NewGuid():N}@example.com",
            FullName = "Advance Monitor",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(responsible);
        await scope.Db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Advance Monitor Client",
            ResponsibleUserId = responsible.Id,
            CreatedAt = now,
        };
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        var candidate = new Payment
        {
            ClientId = client.Id,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Amount = 12_500m,
            PaidAmount = 12_500m,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Date = now.Date.AddDays(-2),
            PlannedDate = now.Date.AddDays(-2),
            PaidDate = now.Date.AddDays(-2),
            LastPaymentDate = now.Date.AddDays(-2),
            Description = "Old unallocated advance candidate",
            AdvanceReviewStatus = PaymentAdvanceReviewStatus.None,
        };
        scope.Db.Payments.Add(candidate);
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddScoped<INotificationService>(provider => new NotificationService(
            provider.GetRequiredService<PaymentContext>(),
            new ConfigurationBuilder().Build(),
            new NoopEmailService(),
            new NoopTelegramNotificationService(),
            NullLogger<NotificationService>.Instance));
        await using var provider = services.BuildServiceProvider();
        var service = new PaymentAdvanceReviewMonitoringService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<PaymentAdvanceReviewMonitoringService>.Instance);

        interceptor.Commands.Clear();
        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(1, result.ProcessedCount);
        Assert.Equal(1, result.MarkedCount);
        Assert.Equal(1, result.NotifiedCount);
        Assert.Single(interceptor.Commands, command =>
            command.Contains(PaymentAdvanceReviewMonitoringService.EnabledSettingKey, StringComparison.Ordinal));
        var refreshed = await scope.Db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == candidate.Id);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, refreshed.AdvanceReviewStatus);
        var notification = Assert.Single(await scope.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.PaymentAdvanceReviewRequired, notification.Type);
    }

    [Fact]
    public async Task PaymentAdvanceReviewMonitoringService_EscalatesCriticalCandidatesToAdmins()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var responsibleRole = new Role { Name = $"advance-critical-role-{Guid.NewGuid():N}", Description = "Advance critical role" };
        var adminRole = new Role { Name = "admin", Description = "Administrator" };
        scope.Db.Roles.AddRange(responsibleRole, adminRole);
        await scope.Db.SaveChangesAsync();
        var responsible = new User
        {
            Email = $"advance-critical-responsible-{Guid.NewGuid():N}@example.com",
            FullName = "Advance Critical Responsible",
            PasswordHash = "hash",
            RoleId = responsibleRole.Id,
            IsApproved = true,
            IsActive = true,
        };
        var admin = new User
        {
            Email = $"advance-critical-admin-{Guid.NewGuid():N}@example.com",
            FullName = "Advance Critical Admin",
            PasswordHash = "hash",
            RoleId = adminRole.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.AddRange(responsible, admin);
        await scope.Db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Critical Advance Client",
            ResponsibleUserId = responsible.Id,
            CreatedAt = now,
        };
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        var candidate = new Payment
        {
            ClientId = client.Id,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Amount = 18_400m,
            PaidAmount = 18_400m,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Date = now.Date.AddDays(-5),
            PlannedDate = now.Date.AddDays(-5),
            PaidDate = now.Date.AddDays(-5),
            LastPaymentDate = now.Date.AddDays(-5),
            Description = "Critical unallocated advance candidate",
            AdvanceReviewStatus = PaymentAdvanceReviewStatus.None,
        };
        scope.Db.Payments.Add(candidate);
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddScoped<INotificationService>(provider => new NotificationService(
            provider.GetRequiredService<PaymentContext>(),
            new ConfigurationBuilder().Build(),
            new NoopEmailService(),
            new NoopTelegramNotificationService(),
            NullLogger<NotificationService>.Instance));
        await using var provider = services.BuildServiceProvider();
        var service = new PaymentAdvanceReviewMonitoringService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<PaymentAdvanceReviewMonitoringService>.Instance);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(1, result.ProcessedCount);
        Assert.Equal(1, result.MarkedCount);
        Assert.Equal(1, result.NotifiedCount);
        Assert.Equal(1, result.EscalatedCount);
        var notifications = await scope.Db.UserNotifications
            .AsNoTracking()
            .OrderBy(item => item.Type)
            .ToListAsync();
        Assert.Contains(notifications, item =>
            item.RecipientUserId == responsible.Id &&
            item.Type == UserNotificationTypes.PaymentAdvanceReviewRequired);
        var escalation = Assert.Single(notifications, item => item.Type == UserNotificationTypes.PaymentAdvanceReviewEscalation);
        Assert.Equal(admin.Id, escalation.RecipientUserId);
        Assert.Contains(candidate.Id.ToString(CultureInfo.InvariantCulture), escalation.EventKey);
    }

    [Fact]
    public async Task RunJob_AdvanceReviewMonitoring_UsesRunnerAndReturnsDiagnostics()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var role = new Role { Name = $"advance-manual-role-{Guid.NewGuid():N}", Description = "Advance manual role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var responsible = new User
        {
            Email = $"advance-manual-{Guid.NewGuid():N}@example.com",
            FullName = "Advance Manual",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(responsible);
        await scope.Db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Advance Manual Client",
            ResponsibleUserId = responsible.Id,
            CreatedAt = now,
        };
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        scope.Db.Payments.Add(new Payment
        {
            ClientId = client.Id,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Amount = 9_000m,
            PaidAmount = 9_000m,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Date = now.Date.AddDays(-1),
            PlannedDate = now.Date.AddDays(-1),
            PaidDate = now.Date.AddDays(-1),
            LastPaymentDate = now.Date.AddDays(-1),
            Description = "Manual monitoring candidate",
            AdvanceReviewStatus = PaymentAdvanceReviewStatus.None,
        });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddScoped<INotificationService>(provider => new NotificationService(
            provider.GetRequiredService<PaymentContext>(),
            new ConfigurationBuilder().Build(),
            new NoopEmailService(),
            new NoopTelegramNotificationService(),
            NullLogger<NotificationService>.Instance));
        await using var provider = services.BuildServiceProvider();
        var runner = new PaymentAdvanceReviewMonitoringService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<PaymentAdvanceReviewMonitoringService>.Instance);
        var controller = new SystemJobsController(
            scope.Db,
            new ConfigurationBuilder().Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()),
            advanceReviewMonitoringRunner: runner);

        var result = await controller.RunJob(SystemJobKeys.PaymentAdvanceReviewMonitoring, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobManualRunResultDto>(ok.Value);
        Assert.Equal(SystemJobKeys.PaymentAdvanceReviewMonitoring, dto.JobKey);
        Assert.Equal(2, dto.ProcessedCount);
        Assert.Contains("поставил на контроль: 1", dto.Summary);
        Assert.Single(await scope.Db.UserNotifications.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task NotificationDeliveryRetentionService_DeletesOnlyExpiredCompletedDeliveries()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
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

        interceptor.Commands.Clear();
        Assert.Equal(2, await service.CountEligibleDeliveriesAsync(CancellationToken.None));
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Single(interceptor.Commands, command => command.Contains("SystemSettings", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command => command.Contains("NotificationDeliveries", StringComparison.Ordinal));

        interceptor.Commands.Clear();
        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(2, result.DeletedCount);
        Assert.Single(interceptor.Commands, command => command.Contains("SystemSettings", StringComparison.Ordinal));
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
    public async Task CalendarSyncLogRetentionService_DeletesExpiredLogsInBoundedProviderBatches()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = CalendarSyncLogRetentionService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncLogRetentionService.RetentionDaysSettingKey, Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = CalendarSyncLogRetentionService.BatchSizeSettingKey, Value = "100", CreatedAt = now, UpdatedAt = now });
        scope.Db.GoogleCalendarSyncLogs.AddRange(Enumerable.Range(1, 105).Select(index => new GoogleCalendarSyncLog
        {
            Operation = "upsert",
            Message = $"Old Google log {index}",
            CreatedAt = now.AddDays(-45).AddSeconds(index),
        }));
        scope.Db.YandexCalendarSyncLogs.AddRange(Enumerable.Range(1, 105).Select(index => new YandexCalendarSyncLog
        {
            Operation = "upsert",
            Message = $"Old Yandex log {index}",
            CreatedAt = now.AddDays(-45).AddSeconds(index),
        }));
        scope.Db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog { Operation = "upsert", Message = "Fresh Google log", CreatedAt = now.AddDays(-2) });
        scope.Db.YandexCalendarSyncLogs.Add(new YandexCalendarSyncLog { Operation = "upsert", Message = "Fresh Yandex log", CreatedAt = now.AddDays(-2) });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new CalendarSyncLogRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CalendarSyncLogRetentionService>.Instance);

        interceptor.Commands.Clear();
        var eligibleBefore = await service.CountEligibleLogsAsync(CancellationToken.None);
        Assert.Equal(105, eligibleBefore.GoogleCount);
        Assert.Equal(105, eligibleBefore.YandexCount);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Single(interceptor.Commands, command => command.Contains("SystemSettings", StringComparison.Ordinal));
        var eligibleCountCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("GoogleCalendarSyncLogs", StringComparison.Ordinal) &&
            command.Contains("YandexCalendarSyncLogs", StringComparison.Ordinal));
        Assert.Equal(2, Regex.Count(eligibleCountCommand, @"SELECT\s+COUNT\(\*\)", RegexOptions.IgnoreCase));

        interceptor.Commands.Clear();
        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(100, result.GoogleDeletedCount);
        Assert.Equal(100, result.YandexDeletedCount);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Single(interceptor.Commands, command => command.Contains("SystemSettings", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("GoogleCalendarSyncLogs", StringComparison.Ordinal) &&
            command.Contains("YandexCalendarSyncLogs", StringComparison.Ordinal) &&
            command.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase));
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(6, await scope.Db.GoogleCalendarSyncLogs.CountAsync());
        Assert.Equal(6, await scope.Db.YandexCalendarSyncLogs.CountAsync());
        Assert.Contains(await scope.Db.GoogleCalendarSyncLogs.Select(item => item.Message).ToListAsync(), message => message == "Fresh Google log");
        Assert.Contains(await scope.Db.YandexCalendarSyncLogs.Select(item => item.Message).ToListAsync(), message => message == "Fresh Yandex log");
    }

    [Fact]
    public async Task CalendarSyncLogRetentionService_DoesNotDeleteLogsWhenDisabled()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.Add(new SystemSetting { Key = CalendarSyncLogRetentionService.EnabledSettingKey, Value = "false", CreatedAt = now, UpdatedAt = now });
        scope.Db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog { Operation = "upsert", Message = "Preserved", CreatedAt = now.AddDays(-365) });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new CalendarSyncLogRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CalendarSyncLogRetentionService>.Instance);

        Assert.Equal(0, (await service.CountEligibleLogsAsync()).TotalCount);
        var result = await service.RunOnceAsync();
        Assert.False(result.Enabled);
        Assert.Equal(0, result.TotalDeletedCount);
        Assert.Equal(1, await scope.Db.GoogleCalendarSyncLogs.CountAsync());
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(90, 90)]
    [InlineData(5000, 3650)]
    public void CalendarSyncLogRetentionService_NormalizesRetentionDays(int value, int expected)
        => Assert.Equal(expected, CalendarSyncLogRetentionService.NormalizeRetentionDays(value));

    [Fact]
    public async Task NotificationRetrySchedulerService_RetriesOversizedWebPushDeliveries()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = NotificationRetrySchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.DelayMinutesSettingKey, Value = "1", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.BatchSizeSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.MaxAttemptsSettingKey, Value = "3", CreatedAt = now, UpdatedAt = now });
        var role = new Role { Name = $"webpush-retry-role-{Guid.NewGuid():N}", Description = "Web push retry role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var user = new User
        {
            Email = $"webpush-retry-{Guid.NewGuid():N}@example.com",
            FullName = "Web Push Retry User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(user);
        await scope.Db.SaveChangesAsync();
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "task:webpush-delivery",
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = user.Id,
            RecipientAddress = "https://push.example/retry",
            Subject = "Retry Web Push",
            BodyPreview = "Retry delivery",
            Status = NotificationDeliveryStatus.Failed,
            Error = "Bad Request. Details: binary data passed in the request must be less than 4096 bytes.",
            CreatedAt = now.AddMinutes(-30),
        });
        scope.Db.UserPushSubscriptions.Add(new UserPushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://push.example/retry",
            P256dh = "p256dh",
            Auth = "auth",
            Browser = "Chrome",
            Platform = "Windows",
            DeviceName = "Chrome Windows",
            UserAgent = "Test",
            IsActive = true,
            CreatedAt = now.AddMinutes(-40),
            UpdatedAt = now.AddMinutes(-30),
            LastSeenAt = now.AddMinutes(-30),
        });
        scope.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = user.Id,
            EventKey = "task:webpush-delivery",
            Type = UserNotificationTypes.TaskChanged,
            Title = "Retry Web Push",
            BodyPreview = "Retry delivery",
            Tone = "amber",
            ObjectType = "task",
            ObjectId = 77,
            MetadataJson = "{}",
            CreatedAt = now.AddMinutes(-30),
        });
        await scope.Db.SaveChangesAsync();
        var original = await scope.Db.NotificationDeliveries.SingleAsync();

        var webPush = new RecordingWebPushRetryService();
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(new RecordingTaskReminderNotificationService());
        services.AddSingleton<IWebPushNotificationService>(webPush);
        await using var provider = services.BuildServiceProvider();
        var service = new NotificationRetrySchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationRetrySchedulerService>.Instance);

        Assert.Equal(1, await service.CountEligibleRetriesAsync(CancellationToken.None));
        interceptor.Commands.Clear();
        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(1, result.RetriedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(original.Id, Assert.Single(webPush.RetryDeliveryIds));
        Assert.Single(interceptor.Commands, command => command.Contains("SystemSettings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotificationRetrySchedulerService_SkipsNonRetryableWebPushDeliveries()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = NotificationRetrySchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.DelayMinutesSettingKey, Value = "1", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.BatchSizeSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.MaxAttemptsSettingKey, Value = "3", CreatedAt = now, UpdatedAt = now });
        var role = new Role { Name = $"webpush-nonretry-role-{Guid.NewGuid():N}", Description = "Web push non retry role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var user = new User
        {
            Email = $"webpush-nonretry-{Guid.NewGuid():N}@example.com",
            FullName = "Web Push Non Retry User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(user);
        await scope.Db.SaveChangesAsync();
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "task:webpush-vapid-mismatch",
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = user.Id,
            RecipientAddress = "https://push.example/non-retry",
            Subject = "Retry Web Push",
            BodyPreview = "Retry delivery",
            Status = NotificationDeliveryStatus.Failed,
            Error = "Received unexpected response code: 403. Details: the VAPID credentials in the authorization header do not correspond to the credentials used to create the subscriptions.",
            CreatedAt = now.AddMinutes(-30),
        });
        scope.Db.UserPushSubscriptions.Add(new UserPushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://push.example/non-retry",
            P256dh = "p256dh",
            Auth = "auth",
            Browser = "Chrome",
            Platform = "Windows",
            DeviceName = "Chrome Windows",
            UserAgent = "Test",
            IsActive = true,
            CreatedAt = now.AddMinutes(-40),
            UpdatedAt = now.AddMinutes(-30),
            LastSeenAt = now.AddMinutes(-30),
        });
        scope.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = user.Id,
            EventKey = "task:webpush-vapid-mismatch",
            Type = UserNotificationTypes.TaskChanged,
            Title = "Retry Web Push",
            BodyPreview = "Retry delivery",
            Tone = "amber",
            ObjectType = "task",
            ObjectId = 77,
            MetadataJson = "{}",
            CreatedAt = now.AddMinutes(-30),
        });
        await scope.Db.SaveChangesAsync();

        var webPush = new RecordingWebPushRetryService();
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(new RecordingTaskReminderNotificationService());
        services.AddSingleton<IWebPushNotificationService>(webPush);
        await using var provider = services.BuildServiceProvider();
        var service = new NotificationRetrySchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationRetrySchedulerService>.Instance);

        Assert.Equal(0, await service.CountEligibleRetriesAsync(CancellationToken.None));
        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(0, result.RetriedCount);
        Assert.Empty(webPush.RetryDeliveryIds);
    }

    [Fact]
    public async Task NotificationRetrySchedulerService_LoadsEligibilityInBatches()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = NotificationRetrySchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.DelayMinutesSettingKey, Value = "1", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.BatchSizeSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.MaxAttemptsSettingKey, Value = "3", CreatedAt = now, UpdatedAt = now });
        var role = new Role { Name = $"webpush-batch-role-{Guid.NewGuid():N}", Description = "Web push batch role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var user = new User
        {
            Email = $"webpush-batch-{Guid.NewGuid():N}@example.com",
            FullName = "Web Push Batch User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(user);
        await scope.Db.SaveChangesAsync();
        var eventKeys = new[] { "task:webpush-batch-a", "task:webpush-batch-b", "task:webpush-batch-c" };
        for (var index = 0; index < eventKeys.Length; index++)
        {
            var endpoint = $"https://push.example/batch-{index}";
            scope.Db.NotificationDeliveries.Add(new NotificationDelivery
            {
                EventKey = eventKeys[index],
                Channel = NotificationChannels.WebPush,
                SenderName = "Web Push",
                RecipientUserId = user.Id,
                RecipientAddress = endpoint,
                Subject = $"Retry Web Push {index}",
                BodyPreview = "Retry delivery",
                Status = NotificationDeliveryStatus.Failed,
                Error = "Bad Request. Details: binary data passed in the request must be less than 4096 bytes.",
                CreatedAt = now.AddMinutes(-30).AddSeconds(index),
            });
            scope.Db.UserPushSubscriptions.Add(new UserPushSubscription
            {
                UserId = user.Id,
                Endpoint = endpoint,
                P256dh = $"p256dh-{index}",
                Auth = $"auth-{index}",
                Browser = "Chrome",
                Platform = "Windows",
                DeviceName = $"Chrome Windows {index}",
                UserAgent = "Test",
                IsActive = true,
                CreatedAt = now.AddMinutes(-40),
                UpdatedAt = now.AddMinutes(-30),
                LastSeenAt = now.AddMinutes(-30),
            });
            scope.Db.UserNotifications.Add(new UserNotification
            {
                RecipientUserId = user.Id,
                EventKey = eventKeys[index],
                Type = UserNotificationTypes.TaskChanged,
                Title = $"Retry Web Push {index}",
                BodyPreview = "Retry delivery",
                Tone = "amber",
                ObjectType = "task",
                ObjectId = 100 + index,
                MetadataJson = "{}",
                CreatedAt = now.AddMinutes(-30),
            });
        }

        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = $"{eventKeys[1]}:retry:already-sent",
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = user.Id,
            RecipientAddress = "https://push.example/batch-1",
            Subject = "Existing retry",
            BodyPreview = "Existing retry delivery",
            Status = NotificationDeliveryStatus.Sent,
            CreatedAt = now.AddMinutes(-20),
        });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new NotificationRetrySchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationRetrySchedulerService>.Instance);
        interceptor.Commands.Clear();

        var eligible = await service.CountEligibleRetriesAsync(CancellationToken.None);

        Assert.Equal(2, eligible);
        Assert.Equal(5, interceptor.Commands.Count);
        Assert.Equal(2, interceptor.Commands.Count(command => command.Contains("NotificationDeliveries", StringComparison.Ordinal)));
        Assert.Single(interceptor.Commands, command => command.Contains("UserPushSubscriptions", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command => command.Contains("UserNotifications", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotificationRetrySchedulerService_ChunksMaximumCandidateWindow()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = NotificationRetrySchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.DelayMinutesSettingKey, Value = "1", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.BatchSizeSettingKey, Value = "100", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.MaxAttemptsSettingKey, Value = "3", CreatedAt = now, UpdatedAt = now });
        scope.Db.NotificationDeliveries.AddRange(Enumerable.Range(1, 1000).Select(index => new NotificationDelivery
        {
            EventKey = $"email:maximum-window:{index}",
            Channel = NotificationChannels.Email,
            SenderName = "Email",
            RecipientAddress = $"maximum-window-{index}@example.com",
            Subject = $"Maximum retry {index}",
            BodyPreview = "Maximum retry delivery",
            Status = NotificationDeliveryStatus.Failed,
            Error = "SMTP unavailable",
            CreatedAt = now.AddMinutes(-30).AddMilliseconds(index),
        }));
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var service = new NotificationRetrySchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationRetrySchedulerService>.Instance);
        interceptor.Commands.Clear();

        var eligible = await service.CountEligibleRetriesAsync(CancellationToken.None);

        Assert.Equal(200, eligible);
        Assert.Equal(7, interceptor.Commands.Count);
        Assert.Equal(6, interceptor.Commands.Count(command => command.Contains("NotificationDeliveries", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NotificationRetrySchedulerService_SerializesConcurrentRuns()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.AddRange(
            new SystemSetting { Key = NotificationRetrySchedulerService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.DelayMinutesSettingKey, Value = "1", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.BatchSizeSettingKey, Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = NotificationRetrySchedulerService.MaxAttemptsSettingKey, Value = "3", CreatedAt = now, UpdatedAt = now });
        scope.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = "email:serialized-retry",
            Channel = NotificationChannels.Email,
            SenderName = "Email",
            RecipientAddress = "serialized-retry@example.com",
            Subject = "Serialized retry",
            BodyPreview = "Serialized retry delivery",
            Status = NotificationDeliveryStatus.Failed,
            Error = "SMTP unavailable",
            CreatedAt = now.AddMinutes(-30),
        });
        await scope.Db.SaveChangesAsync();

        var notificationService = new RecordingTaskReminderNotificationService
        {
            RetrySucceeds = true,
            RetryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            RetryRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(notificationService);
        await using var provider = services.BuildServiceProvider();
        var service = new NotificationRetrySchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationRetrySchedulerService>.Instance);

        var firstRun = service.RunOnceAsync(CancellationToken.None);
        await notificationService.RetryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRun = service.RunOnceAsync(CancellationToken.None);
        await Task.Delay(100);

        var maxConcurrentCallsWhileBlocked = notificationService.MaxConcurrentRetryCalls;
        notificationService.RetryRelease.SetResult(true);
        await Task.WhenAll(firstRun, secondRun);
        Assert.Equal(1, maxConcurrentCallsWhileBlocked);
        Assert.Equal(1, notificationService.MaxConcurrentRetryCalls);
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
    public async Task YandexDiskCrmSyncSchedulerService_SerializesConcurrentRuns()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        scope.Db.SystemSettings.Add(new SystemSetting
        {
            Key = YandexDiskCrmSyncSchedulerService.EnabledSettingKey,
            Value = "true",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var jobRunState = new BlockingSystemJobRunStateService();
        var service = new YandexDiskCrmSyncSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<YandexDiskCrmSyncSchedulerService>.Instance,
            jobRunState);

        var firstRun = service.RunOnceAsync(CancellationToken.None);
        await jobRunState.SuccessEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRun = service.RunOnceAsync(CancellationToken.None);
        await Task.Delay(100);

        var maxConcurrentCallsWhileBlocked = jobRunState.MaxConcurrentSuccessCalls;
        jobRunState.SuccessRelease.SetResult(true);
        await Task.WhenAll(firstRun, secondRun);

        Assert.Equal(1, maxConcurrentCallsWhileBlocked);
        Assert.Equal(1, jobRunState.MaxConcurrentSuccessCalls);
        Assert.Equal(2, jobRunState.SuccessCalls);
    }

    [Fact]
    public async Task TaskJournalAutomationSchedulerService_SerializesConcurrentRuns()
    {
        await using var scope = await CreateDbScopeAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        await using var provider = services.BuildServiceProvider();
        var jobRunState = new BlockingSystemJobRunStateService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskJournal:Automation:Enabled"] = "false",
            })
            .Build();
        var service = new TaskJournalAutomationSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<TaskJournalAutomationSchedulerService>.Instance,
            jobRunState);

        var firstRun = service.RunOnceAsync(CancellationToken.None);
        await jobRunState.SuccessEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRun = service.RunOnceAsync(CancellationToken.None);
        await Task.Delay(100);

        var maxConcurrentCallsWhileBlocked = jobRunState.MaxConcurrentSuccessCalls;
        jobRunState.SuccessRelease.SetResult(true);
        await Task.WhenAll(firstRun, secondRun);

        Assert.Equal(1, maxConcurrentCallsWhileBlocked);
        Assert.Equal(1, jobRunState.MaxConcurrentSuccessCalls);
        Assert.Equal(2, jobRunState.SuccessCalls);
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
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
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
            DueDate = now.AddDays(4),
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

        interceptor.Commands.Clear();
        var state = await service.BuildDiagnosticsStateAsync(CancellationToken.None);

        Assert.True(state.SchedulerEnabled);
        Assert.True(state.FeatureEnabled);
        Assert.False(state.Readiness.ReadyToConnect);
        Assert.Equal(3, state.CandidateCount);
        Assert.Equal(now.AddDays(2), state.OldestCandidateAt);
        Assert.Equal(2, interceptor.Commands.Count);
        var candidateStatisticsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("TaskJournalItems", StringComparison.Ordinal) &&
            command.Contains("ClientCases", StringComparison.Ordinal));
        Assert.Contains("COUNT", candidateStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN", candidateStatisticsCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("SystemSettings", StringComparison.Ordinal));

        scope.Db.TaskJournalItems.RemoveRange(await scope.Db.TaskJournalItems.ToListAsync());
        scope.Db.ClientCases.RemoveRange(await scope.Db.ClientCases.ToListAsync());
        await scope.Db.SaveChangesAsync();
        interceptor.Commands.Clear();

        var emptyState = await service.BuildDiagnosticsStateAsync(CancellationToken.None);

        Assert.Equal(0, emptyState.CandidateCount);
        Assert.Null(emptyState.OldestCandidateAt);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("TaskJournalItems", StringComparison.Ordinal) &&
            command.Contains("ClientCases", StringComparison.Ordinal));

        interceptor.Commands.Clear();
        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.False(result.ReadyToConnect);
        Assert.Equal(0, result.ProcessedCount);
        Assert.True(result.SkippedCount > 0);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("SystemSettings", StringComparison.Ordinal));
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
    public async Task TaskReminderService_BackfillsCasesBeyondFirstBatchOnLaterRuns()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var role = new Role { Name = $"paged-deadline-role-{Guid.NewGuid():N}", Description = "Deadline role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var responsible = new User
        {
            Email = $"paged-deadline-user-{Guid.NewGuid():N}@example.com",
            FullName = "Deadline Owner",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        var client = new Client { Name = "Paged deadline client" };
        scope.Db.Users.Add(responsible);
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        scope.Db.ClientCases.AddRange(
            new ClientCase { ClientId = client.Id, ResponsibleUserId = responsible.Id, CaseNumber = "PAGED-1", Title = "First case", Status = ClientCaseStatus.Open, DueDate = now.AddDays(8) },
            new ClientCase { ClientId = client.Id, ResponsibleUserId = responsible.Id, CaseNumber = "PAGED-2", Title = "Second case", Status = ClientCaseStatus.Open, DueDate = now.AddDays(8) });
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
                    ["Notifications:TaskReminders:DeadlineSyncBatchSize"] = "1",
                })
                .Build(),
            NullLogger<TaskJournalReminderNotificationService>.Instance);

        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(1, await scope.Db.TaskJournalItems.CountAsync(task => task.TagsJson.Contains("case-deadline:due")));

        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(2, await scope.Db.TaskJournalItems.CountAsync(task => task.TagsJson.Contains("case-deadline:due")));

        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(2, await scope.Db.TaskJournalItems.CountAsync(task => task.TagsJson.Contains("case-deadline:due")));
    }

    [Fact]
    public async Task TaskReminderService_ProcessesRemindersBeyondFirstBatchOnLaterRuns()
    {
        await using var scope = await CreateDbScopeAsync();
        var role = new Role { Name = $"paged-reminder-role-{Guid.NewGuid():N}", Description = "Reminder role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var assignee = new User
        {
            Email = $"paged-reminder-user-{Guid.NewGuid():N}@example.com",
            FullName = "Reminder Owner",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(assignee);
        await scope.Db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        scope.Db.TaskJournalItems.AddRange(
            new TaskJournalItem { ObjectName = "PAGED-1", Title = "First reminder", AssigneeUserId = assignee.Id, AssigneeName = assignee.FullName, DueAt = now.AddMinutes(10), RemindersJson = "[\"before15m\"]", ChannelsJson = "[\"inApp\"]", Status = "new" },
            new TaskJournalItem { ObjectName = "PAGED-2", Title = "Second reminder", AssigneeUserId = assignee.Id, AssigneeName = assignee.FullName, DueAt = now.AddMinutes(11), RemindersJson = "[\"before15m\"]", ChannelsJson = "[\"inApp\"]", Status = "new" });
        await scope.Db.SaveChangesAsync();
        var taskIds = await scope.Db.TaskJournalItems
            .Where(task => task.ObjectName == "PAGED-1" || task.ObjectName == "PAGED-2")
            .OrderBy(task => task.DueAt)
            .Select(task => task.Id)
            .ToArrayAsync();

        var notificationService = new RecordingTaskReminderNotificationService();
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(notificationService);
        await using var provider = services.BuildServiceProvider();
        var service = new TaskJournalReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Notifications:TaskReminders:BatchSize"] = "1",
                })
                .Build(),
            NullLogger<TaskJournalReminderNotificationService>.Instance);

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(taskIds, notificationService.Reminders.Select(reminder => reminder.TaskId));
    }

    [Fact]
    public async Task TaskReminderService_ForPretrialCasesBackfillsOnlyInternalCaseDeadline()
    {
        await using var scope = await CreateDbScopeAsync();
        var now = DateTime.UtcNow;
        var role = new Role { Name = $"pretrial-deadline-role-{Guid.NewGuid():N}", Description = "Pretrial deadline role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var responsible = new User
        {
            Email = $"pretrial-deadline-user-{Guid.NewGuid():N}@example.com",
            FullName = "Pretrial Deadline Owner",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        var client = new Client { Name = "Pretrial deadline client" };
        scope.Db.Users.Add(responsible);
        scope.Db.Clients.Add(client);
        await scope.Db.SaveChangesAsync();
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            ResponsibleUserId = responsible.Id,
            CaseNumber = "CASE-PRETRIAL-DEADLINE",
            Title = "Pretrial case with court residue",
            Status = ClientCaseStatus.Open,
            CaseType = "Внесудебное урегулирование",
            Stage = "Внесудебное производство",
            DueDate = now.AddDays(8),
            NextHearingAt = now.AddDays(4),
            AppealDeadlineAt = now.AddDays(5),
        };
        scope.Db.ClientCases.Add(clientCase);
        await scope.Db.SaveChangesAsync();
        scope.Db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            DeadlineType = "cassation",
            Title = "Старый срок кассации",
            DueAt = now.AddDays(6),
            AllDay = true,
            Status = "active",
            WarningDaysBefore = 7,
            CreatedAt = now.AddDays(-1),
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
        var tasks = await scope.Db.TaskJournalItems
            .AsNoTracking()
            .Where(task => task.ClientCaseId == clientCase.Id)
            .ToListAsync();
        var deadlineTask = Assert.Single(tasks);
        Assert.Contains("case-deadline:due", deadlineTask.TagsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("case-deadline:hearing", deadlineTask.TagsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("case-deadline:appeal", deadlineTask.TagsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("case-deadline:entity-", deadlineTask.TagsJson, StringComparison.Ordinal);
        Assert.Equal(responsible.Id, deadlineTask.AssigneeUserId);
    }

    [Fact]
    public async Task TaskReminderService_UsesAssigneeLocalDateForDailyOverdue()
    {
        await using var scope = await CreateDbScopeAsync();
        var nowUtc = DateTime.UtcNow;
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        var expectReminder = selectedZone.ProfileToday > selectedZone.UtcToday;
        var dueAt = expectReminder
            ? selectedZone.UtcToday.AddHours(9)
            : selectedZone.UtcToday.AddDays(-1).AddHours(23);
        var role = new Role { Name = $"daily-overdue-role-{Guid.NewGuid():N}", Description = "Daily overdue role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var assignee = new User
        {
            Email = $"daily-overdue-{Guid.NewGuid():N}@example.com",
            FullName = "Daily Overdue User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(assignee);
        await scope.Db.SaveChangesAsync();
        scope.Db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.TimeZoneId,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        });
        var task = new TaskJournalItem
        {
            ObjectName = "CASE-DAILY-OVERDUE",
            Title = "Daily overdue boundary",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = dueAt,
            RemindersJson = "[\"dailyOverdue\"]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
        };
        scope.Db.TaskJournalItems.Add(task);
        await scope.Db.SaveChangesAsync();

        var notificationService = new RecordingTaskReminderNotificationService();
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(notificationService);
        await using var provider = services.BuildServiceProvider();
        var service = new TaskJournalReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<TaskJournalReminderNotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(expectReminder ? 1 : 0, sent);
        Assert.Equal(expectReminder ? 1 : 0, notificationService.Reminders.Count);
        if (expectReminder)
        {
            var reminder = Assert.Single(notificationService.Reminders);
            Assert.Equal(task.Id, reminder.TaskId);
            Assert.Equal("dailyOverdue", reminder.ReminderKey);
        }
    }

    [Fact]
    public async Task TaskReminderService_NotifiesOverdueApprovalEvenWhenTaskDueDateIsLater()
    {
        await using var scope = await CreateDbScopeAsync();
        var nowUtc = DateTime.UtcNow;
        var role = new Role { Name = $"approval-overdue-role-{Guid.NewGuid():N}", Description = "Approval overdue role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var assignee = new User
        {
            Email = $"approval-overdue-assignee-{Guid.NewGuid():N}@example.com",
            FullName = "Approval Overdue Assignee",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        var approver = new User
        {
            Email = $"approval-overdue-approver-{Guid.NewGuid():N}@example.com",
            FullName = "Approval Overdue Approver",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.AddRange(assignee, approver);
        await scope.Db.SaveChangesAsync();
        var task = new TaskJournalItem
        {
            ObjectName = "CASE-APPROVAL-OVERDUE",
            Title = "Approval overdue boundary",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = nowUtc.AddDays(14),
            ApprovalDueAt = nowUtc.AddHours(-1),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "onApproval",
            Participants =
            [
                new TaskJournalParticipant
                {
                    UserId = approver.Id,
                    DisplayName = approver.FullName,
                    Role = "approver",
                    ApprovalStatus = "pending",
                    SortOrder = 0,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc,
                }
            ]
        };
        scope.Db.TaskJournalItems.Add(task);
        await scope.Db.SaveChangesAsync();

        var notificationService = new RecordingTaskReminderNotificationService();
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(notificationService);
        await using var provider = services.BuildServiceProvider();
        var service = new TaskJournalReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<TaskJournalReminderNotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Equal([task.Id], notificationService.ApprovalOverdueTaskIds);
        Assert.Empty(notificationService.Reminders);
    }

    [Fact]
    public async Task TaskReminderService_LoadsAssigneeTimeZonesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var scope = await CreateDbScopeAsync(interceptor);
        var role = new Role { Name = $"timezone-batch-role-{Guid.NewGuid():N}", Description = "Timezone batch role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var users = Enumerable.Range(1, 3)
            .Select(index => new User
            {
                Email = $"timezone-batch-{index}-{Guid.NewGuid():N}@example.com",
                FullName = $"Timezone Batch User {index}",
                PasswordHash = "hash",
                RoleId = role.Id,
                IsApproved = true,
                IsActive = true,
            })
            .ToList();
        scope.Db.Users.AddRange(users);
        await scope.Db.SaveChangesAsync();
        var nowUtc = DateTime.UtcNow;
        scope.Db.UserSettings.AddRange(users.Select(user => new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Moscow",
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        }));
        scope.Db.TaskJournalItems.AddRange(users.Select((user, index) => new TaskJournalItem
        {
            ObjectName = $"TIMEZONE-BATCH-{index}",
            Title = $"Timezone batch task {index}",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = nowUtc.AddDays(1),
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
        }));
        await scope.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(new RecordingTaskReminderNotificationService());
        await using var provider = services.BuildServiceProvider();
        var service = new TaskJournalReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<TaskJournalReminderNotificationService>.Instance);
        interceptor.Commands.Clear();

        var sent = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Single(interceptor.Commands, command => command.Contains("UserSettings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskReminderService_SerializesConcurrentRuns()
    {
        await using var scope = await CreateDbScopeAsync();
        var role = new Role { Name = $"serialized-reminders-role-{Guid.NewGuid():N}", Description = "Serialized reminders role" };
        scope.Db.Roles.Add(role);
        await scope.Db.SaveChangesAsync();
        var user = new User
        {
            Email = $"serialized-reminders-{Guid.NewGuid():N}@example.com",
            FullName = "Serialized Reminder User",
            PasswordHash = "hash",
            RoleId = role.Id,
            IsApproved = true,
            IsActive = true,
        };
        scope.Db.Users.Add(user);
        await scope.Db.SaveChangesAsync();
        scope.Db.TaskJournalItems.Add(new TaskJournalItem
        {
            ObjectName = "SERIALIZED-REMINDER",
            Title = "Serialized reminder task",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = DateTime.UtcNow.AddMinutes(10),
            RemindersJson = "[\"before15m\"]",
            ChannelsJson = "[\"inApp\"]",
            Status = "new",
        });
        await scope.Db.SaveChangesAsync();

        var notificationService = new RecordingTaskReminderNotificationService
        {
            ReminderEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ReminderRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var services = new ServiceCollection();
        services.AddScoped(_ => new PaymentContext(scope.Options, useObjectAuditModel: false));
        services.AddSingleton<INotificationService>(notificationService);
        await using var provider = services.BuildServiceProvider();
        var service = new TaskJournalReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<TaskJournalReminderNotificationService>.Instance);

        var firstRun = service.RunOnceAsync(CancellationToken.None);
        await notificationService.ReminderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRun = service.RunOnceAsync(CancellationToken.None);
        await Task.Delay(100);

        var maxConcurrentCallsWhileBlocked = notificationService.MaxConcurrentReminderCalls;
        notificationService.ReminderRelease.SetResult(true);
        await Task.WhenAll(firstRun, secondRun);
        Assert.Equal(1, maxConcurrentCallsWhileBlocked);
        Assert.Equal(1, notificationService.MaxConcurrentReminderCalls);
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
        Assert.NotNull(state.LastSuccessAt);

        var previousSuccessAt = state.LastSuccessAt;
        await service.RecordFailureAsync(
            SystemJobKeys.TaskReminders,
            DateTime.UtcNow.AddSeconds(-10),
            new InvalidOperationException("smtp unavailable"),
            "Проверка напоминаний завершилась с ошибкой.",
            CancellationToken.None);

        states = await service.GetLastRunsAsync([SystemJobKeys.TaskReminders], CancellationToken.None);
        state = Assert.Single(states.Values);

        Assert.Equal("error", state.Status);
        Assert.Equal("smtp unavailable", state.ErrorMessage);
        Assert.Equal(previousSuccessAt, state.LastSuccessAt);
    }

    private static async Task<TestDbScope> CreateDbScopeAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var options = optionsBuilder.Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();

        return new TestDbScope(connection, options, db);
    }

    private static (string TimeZoneId, DateTime UtcToday, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZone.Id, utcToday, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC date was found.");
    }

    private static SystemJobsController CreateController(PaymentContext db)
        => new(
            db,
            new ConfigurationBuilder().Build(),
            new TestOptionsMonitor<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()));

    private static async Task<User> CreateUserAsync(PaymentContext db, string roleName)
    {
        var role = new Role
        {
            Name = roleName,
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"{roleName}-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = roleName,
            IsActive = true,
            IsApproved = true,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ActionExecutingContext CreateActionExecutingContext(
        SystemJobsController controller,
        int userId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, $"User {userId}"),
                    new Claim(ClaimTypes.Role, "admin"),
                ],
                "TestAuth")),
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor());
        controller.ControllerContext = new ControllerContext(actionContext);

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller);
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

    private sealed class BlockingSystemJobRunStateService : ISystemJobRunStateService
    {
        private int _activeSuccessCalls;
        private int _maxConcurrentSuccessCalls;
        private int _successCalls;

        public TaskCompletionSource<bool> SuccessEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SuccessRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SuccessCalls => Volatile.Read(ref _successCalls);
        public int MaxConcurrentSuccessCalls => Volatile.Read(ref _maxConcurrentSuccessCalls);

        public async Task RecordSuccessAsync(
            string jobKey,
            DateTime startedAt,
            int? processedCount,
            int? skippedCount,
            string summary,
            CancellationToken cancellationToken = default)
        {
            var activeCalls = Interlocked.Increment(ref _activeSuccessCalls);
            Interlocked.Increment(ref _successCalls);
            UpdateMaximum(activeCalls);
            SuccessEntered.TrySetResult(true);
            try
            {
                await SuccessRelease.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSuccessCalls);
            }
        }

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
            => Task.FromResult<IReadOnlyDictionary<string, SystemJobRunState>>(
                new Dictionary<string, SystemJobRunState>(StringComparer.OrdinalIgnoreCase));

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maxConcurrentSuccessCalls);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref _maxConcurrentSuccessCalls, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;

        public Task SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            CancellationToken cancellationToken = default,
            EmailSendSettings? settings = null)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoopTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(
            long chatId,
            string text,
            string botToken,
            CancellationToken cancellationToken = default,
            TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));

        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RecordingTaskReminderNotificationService : INotificationService
    {
        private int _activeReminderCalls;
        private int _activeRetryCalls;
        public List<(int TaskId, string ReminderKey)> Reminders { get; } = [];
        public List<int> ApprovalOverdueTaskIds { get; } = [];
        public TaskCompletionSource<bool>? ReminderEntered { get; init; }
        public TaskCompletionSource<bool>? ReminderRelease { get; init; }
        public int MaxConcurrentReminderCalls { get; private set; }
        public TaskCompletionSource<bool>? RetryEntered { get; init; }
        public TaskCompletionSource<bool>? RetryRelease { get; init; }
        public bool RetrySucceeds { get; init; }
        public int MaxConcurrentRetryCalls { get; private set; }

        public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationChannelDto>>(Array.Empty<NotificationChannelDto>());

        public Task<NotificationSettingsDto> UpdateEmailSettingsAsync(UpdateEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(UpdateTelegramNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateSmsSettingsAsync(UpdateSmsNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationTemplateDto>>(Array.Empty<NotificationTemplateDto>());

        public Task<NotificationTemplateDto> UpdateTemplateAsync(string key, UpdateNotificationTemplateRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationMessage> RenderTemplateAsync(string key, string fallbackSubject, string fallbackBody, IReadOnlyDictionary<string, string?> values, bool isSensitive = false, string purpose = NotificationPurposes.ManualTest, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationMessage(fallbackSubject, fallbackBody, IsSensitive: isSensitive, Purpose: purpose));

        public Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(int userId, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> SendAsync(NotificationRecipient recipient, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public async Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
        {
            var activeCalls = Interlocked.Increment(ref _activeRetryCalls);
            MaxConcurrentRetryCalls = Math.Max(MaxConcurrentRetryCalls, activeCalls);
            try
            {
                RetryEntered?.TrySetResult(true);
                if (RetryRelease is not null)
                {
                    await RetryRelease.Task.WaitAsync(cancellationToken);
                }

                return RetrySucceeds
                    ?
                    [
                        new NotificationDelivery
                        {
                            Id = deliveryId + 1000,
                            EventKey = $"retry:{deliveryId}",
                            Channel = NotificationChannels.Email,
                            Status = NotificationDeliveryStatus.Sent,
                            CreatedAt = DateTime.UtcNow,
                        }
                    ]
                    : Array.Empty<NotificationDelivery>();
            }
            finally
            {
                Interlocked.Decrement(ref _activeRetryCalls);
            }
        }

        public Task<bool> NotifyAsync(NotificationEventEnvelope notificationEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<NotificationSettingsDto> UpsertSenderAsync(UpsertNotificationSenderRequest request, int? senderId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteSenderAsync(int senderId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationApprovedAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPasswordIssuedAsync(User user, string password, IEnumerable<string>? channels = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public async Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var activeCalls = Interlocked.Increment(ref _activeReminderCalls);
            MaxConcurrentReminderCalls = Math.Max(MaxConcurrentReminderCalls, activeCalls);
            try
            {
                ReminderEntered?.TrySetResult(true);
                if (ReminderRelease is not null)
                {
                    await ReminderRelease.Task.WaitAsync(cancellationToken);
                }

                Reminders.Add((task.Id, reminderKey));
                return
                [
                    new NotificationDelivery
                    {
                        EventKey = $"task:{task.Id}:reminder:{reminderKey}",
                        Channel = NotificationChannels.InApp,
                        Subject = "Reminder sent",
                        CreatedAt = nowUtc,
                        Status = NotificationDeliveryStatus.Sent,
                    }
                ];
            }
            finally
            {
                Interlocked.Decrement(ref _activeReminderCalls);
            }
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskAssignedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(TaskJournalItem task, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCancelledAsync(TaskJournalItem task, string actorName, int actorUserId, string action, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskScheduleConflictAsync(TaskJournalItem task, IReadOnlyList<TaskJournalItem> conflicts, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalDecisionAsync(TaskJournalItem task, TaskJournalParticipant approver, string decision, string actorName, int actorUserId, string? resolution = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRequestedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeApprovedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRejectedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalOverdueAsync(TaskJournalItem task, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            ApprovalOverdueTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(
            [
                new NotificationDelivery
                {
                    EventKey = $"task:{task.Id}:approval-overdue",
                    Channel = NotificationChannels.InApp,
                    Subject = "Approval overdue",
                    CreatedAt = nowUtc,
                    Status = NotificationDeliveryStatus.Sent,
                }
            ]);
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? additionalRecipientUserIds = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(TaskJournalItem task, int? previousAssigneeUserId, string? previousAssigneeName, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? additionalRecipientUserIds = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(TaskJournalItem task, TaskJournalComment comment, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyCaseInactivityControlTaskCreatedAsync(TaskJournalItem task, int daysWithoutActiveTask, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task NotifyCalendarAccessChangedAsync(int roleId, IReadOnlyCollection<int> addedVisibleUserIds, IReadOnlyCollection<int> removedVisibleUserIds, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyExternalCalendarSyncWarningAsync(string providerName, string operation, string summary, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteMentionAsync(int noteId, int boardId, string boardTitle, string noteText, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteAssignedAsync(int noteId, int boardId, string boardTitle, string noteText, int assignedUserId, string actorName, int actorUserId, DateTime? reminderAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteReminderAsync(StickyNote note, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteImportantChangeAsync(int noteId, int boardId, string boardTitle, string noteText, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, int noteVersion, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentAssignedAsync(Payment payment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentChangedAsync(Payment payment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceCreatedAsync(Payment invoicePayment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceChangedAsync(Payment invoicePayment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActCreatedAsync(Act act, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActChangedAsync(Act act, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractCreatedAsync(Contract contract, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractChangedAsync(Contract contract, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractExpiringAsync(Contract contract, int daysLeft, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseCourtEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseProceduralDeadlineAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseConversationAsync(ClientCase clientCase, string messageText, int attachmentCount, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseDocumentAsync(ClientCase clientCase, ClientCaseDocument document, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessRequestedAsync(ClientCase clientCase, string requesterName, int requesterUserId, string? reason, DateTime requestedAt, IReadOnlyCollection<int> managerUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessResolvedAsync(ClientCase clientCase, bool granted, string actorName, int actorUserId, int requestedByUserId, string requestedByUserName, string? requestReason, string? comment, DateTime resolvedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCourtSyncFailedAsync(ClientCase clientCase, string providerName, string summary, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCourtSyncStalledAsync(ClientCase clientCase, string providerName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseHearingDateChangedAsync(ClientCase clientCase, DateTime? previousAt, DateTime currentAt, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, DateTime changedAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseDecisionDateChangedAsync(ClientCase clientCase, DateTime? previousAt, DateTime? currentAt, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, DateTime changedAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingWebPushRetryService : IWebPushNotificationService
    {
        public List<int> RetryDeliveryIds { get; } = [];

        public WebPushPublicKeyStatus GetPublicKeyStatus()
            => new(true, true, "public-key", true, true, true);

        public Task SendUserNotificationAsync(
            UserNotification notification,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<NotificationDelivery?> RetryDeliveryAsync(
            int deliveryId,
            CancellationToken cancellationToken = default)
        {
            RetryDeliveryIds.Add(deliveryId);
            return Task.FromResult<NotificationDelivery?>(new NotificationDelivery
            {
                Id = deliveryId + 1000,
                Channel = NotificationChannels.WebPush,
                Status = NotificationDeliveryStatus.Sent,
                CreatedAt = DateTime.UtcNow,
            });
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
