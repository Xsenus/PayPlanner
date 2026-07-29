using System.Text.RegularExpressions;
using Xunit;

namespace PayPlanner.Api.Tests;

public class HostedServiceLifecycleCoverageTests
{
    private static readonly string[] RequiredHostedServices =
    [
        "DatabaseBackupSchedulerService",
        "PaymentStatusUpdater",
        "InvoicePaymentStateReconciliationService",
        "TaskJournalReminderNotificationService",
        "NotificationRetrySchedulerService",
        "NotificationDeliveryRetentionService",
        "CalendarSyncLogRetentionService",
        "CaseProceduralDeadlineReminderNotificationService",
        "CalendarSyncSchedulerService",
        "GoogleCalendarBackgroundSyncService",
        "YandexCalendarBackgroundSyncService",
        "TaskJournalAutomationSchedulerService",
        "TelegramBotPollingService",
        "MoneyBalanceReconciliationSchedulerService",
        "ClientCaseCourtSyncSchedulerService",
        "CourtSyncMetricsRollupService",
        "UserActivityRetentionService",
        "YandexDiskCrmSyncSchedulerService",
    ];

    [Fact]
    public void Program_RegistersEveryRoadmapHostedService()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "backend", "Program.cs"));

        foreach (var serviceName in RequiredHostedServices)
        {
            Assert.Contains(serviceName, program, StringComparison.Ordinal);
        }

        var registrations = Regex.Matches(program, @"AddHostedService(?:<(?<type>[^>]+)>|\(sp => sp\.GetRequiredService<(?<type>[^>]+)>\(\)\))")
            .Select(match => match.Groups["type"].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        foreach (var serviceName in RequiredHostedServices)
        {
            Assert.Contains(serviceName, registrations);
        }
    }

    [Fact]
    public void HostedServiceLifecycleCoverage_DocumentsEveryRoadmapHostedService()
    {
        var root = FindRepositoryRoot();
        var document = File.ReadAllText(Path.Combine(root, "docs", "hosted-service-lifecycle-coverage-2026-06-10.md"));

        Assert.Contains("Start/cancellation coverage", document, StringComparison.Ordinal);
        Assert.Contains("One tick coverage", document, StringComparison.Ordinal);
        Assert.Contains("Exception isolation coverage", document, StringComparison.Ordinal);
        Assert.Contains("External API isolation", document, StringComparison.Ordinal);

        foreach (var serviceName in RequiredHostedServices)
        {
            var row = document
                .Split('\n', StringSplitOptions.TrimEntries)
                .SingleOrDefault(line => line.StartsWith($"| {serviceName} |", StringComparison.Ordinal));

            Assert.False(string.IsNullOrWhiteSpace(row), $"Missing lifecycle coverage row for {serviceName}.");
            Assert.DoesNotContain("TODO", row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TBD", row, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                row.Contains("Direct", StringComparison.OrdinalIgnoreCase)
                || row.Contains("Documented gap", StringComparison.OrdinalIgnoreCase),
                $"Row for {serviceName} must either reference direct coverage or document the gap.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && File.Exists(Path.Combine(current.FullName, "backend", "Program.cs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
