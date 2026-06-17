using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
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
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.EnabledSettingKey, Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.IntervalHoursSettingKey, Value = "6", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = PaymentAdvanceReviewMonitoringService.BatchSizeSettingKey, Value = "15", CreatedAt = now, UpdatedAt = now },
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
        scope.Db.Payments.Add(new Payment
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
                    LastSuccessAt = now.AddMinutes(-4),
                    ProcessedCount = 3,
                    Summary = "Проверено дел: 3.",
                },
            }));

        var result = await controller.GetDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SystemJobDiagnosticsDto>(ok.Value);

        Assert.Equal(11, dto.Items.Count);
        Assert.Contains(dto.Items, item => item.Key == "database-backup" && item.Enabled && item.IntervalText.Contains("02:00"));
        Assert.Contains(dto.Items, item => item.Key == "task-reminders" && item.PendingCount == 1 && item.LastActivityDescription == "Reminder sent");
        Assert.Contains(dto.Items, item => item.Key == "notification-retry" && item.PendingCount == 1 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == SystemJobKeys.YandexDiskCrmSync && item.PendingCount == 1 && item.LastProcessedCount == 20 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "notification-delivery-retention" && item.PendingCount == 1 && item.LastProcessedCount == 25 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == SystemJobKeys.PaymentAdvanceReviewMonitoring && item.PendingCount == 1 && item.LastProcessedCount == 15 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "external-calendar-sync" && item.Status == "warning" && item.LastProcessedCount == 50 && item.ManualRunEnabled);
        Assert.Contains(dto.Items, item => item.Key == "case-inactivity-control" && item.PendingCount == 1 && item.LastProcessedCount == 7);
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.Status == "active" && item.PendingCount == 1 && item.LastProcessedCount == 12);
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.LastRunStatus == "success" && item.LastRunStatusText == "Успешно");
        Assert.Contains(dto.Items, item => item.Key == "court-sync" && item.LastSuccessAt == now.AddMinutes(-4) && item.LastRunDurationMs == 60000);
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
        var role = new Role { Name = $"money-admin-{Guid.NewGuid():N}", Description = "Money admin" };
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
        await using var scope = await CreateDbScopeAsync();
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

    private sealed class RecordingTaskReminderNotificationService : INotificationService
    {
        public List<(int TaskId, string ReminderKey)> Reminders { get; } = [];
        public List<int> ApprovalOverdueTaskIds { get; } = [];

        public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationChannelDto>>(Array.Empty<NotificationChannelDto>());

        public Task<NotificationSettingsDto> UpdateEmailSettingsAsync(UpdateEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(UpdateTelegramNotificationSettingsRequest request, CancellationToken cancellationToken = default)
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

        public Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

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

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            Reminders.Add((task.Id, reminderKey));
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(
            [
                new NotificationDelivery
                {
                    EventKey = $"task:{task.Id}:reminder:{reminderKey}",
                    Channel = NotificationChannels.InApp,
                    Subject = "Reminder sent",
                    CreatedAt = nowUtc,
                    Status = NotificationDeliveryStatus.Sent,
                }
            ]);
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

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(TaskJournalItem task, int? previousAssigneeUserId, string? previousAssigneeName, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default)
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

        public Task NotifyCaseDocumentAsync(ClientCase clientCase, ClientCaseDocument document, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCourtSyncFailedAsync(ClientCase clientCase, string providerName, string summary, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
