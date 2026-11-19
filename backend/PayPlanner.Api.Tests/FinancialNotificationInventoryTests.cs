using System.Text.RegularExpressions;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class FinancialNotificationInventoryTests
{
    [Fact]
    public void PaymentEndpoints_KeepNotificationAndAdvanceReviewPaths()
    {
        var source = ReadSourceFile("backend", "Controllers", "PaymentsController.cs");

        AssertMethodContains(
            source,
            "Create",
            "PaymentAdvanceReviewService.ApplyAutomaticReviewState",
            "PaymentAdvanceReviewService.SyncBalanceAsync",
            "PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync",
            "NotifyPaymentAssignedAsync",
            "NotifyInvoiceStateChangedAsync");

        AssertMethodContains(
            source,
            "Update",
            "PaymentAdvanceReviewService.ApplyAutomaticReviewState",
            "PaymentAdvanceReviewService.SyncBalanceAsync",
            "PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync",
            "NotifyPaymentChangedAsync",
            "NotifyInvoiceStateChangedAsync");

        AssertMethodContains(
            source,
            "UpdateStatus",
            "PaymentAdvanceReviewService.ApplyAutomaticReviewState",
            "PaymentAdvanceReviewService.SyncBalanceAsync",
            "NotifyPaymentChangedAsync",
            "NotifyInvoiceStateChangedAsync");
    }

    [Fact]
    public void DocumentEndpoints_KeepNotificationPaths()
    {
        var invoicesSource = ReadSourceFile("backend", "Controllers", "InvoicesController.cs");
        var actsSource = ReadSourceFile("backend", "Controllers", "ActsController.cs");
        var contractsSource = ReadSourceFile("backend", "Controllers", "ContractsController.cs");

        AssertMethodContains(invoicesSource, "Create", "NotifyInvoiceCreatedAsync");
        AssertMethodContains(invoicesSource, "Update", "NotifyInvoiceChangedAsync");
        AssertMethodContains(invoicesSource, "SendToSigning", "NotifyInvoiceChangedAsync");
        AssertMethodContains(invoicesSource, "MarkSigned", "NotifyInvoiceChangedAsync");
        AssertMethodContains(invoicesSource, "MarkNotSigned", "NotifyInvoiceChangedAsync");
        AssertMethodContains(invoicesSource, "UpdateWorkflowStatus", "NotifyInvoiceChangedAsync");
        AssertMethodContains(invoicesSource, "Cancel", "CancelCoreAsync", "lease.CommitAsync", "scope.AfterCommit");
        AssertMethodContains(invoicesSource, "CancelCoreAsync", "NotifyInvoiceChangedAsync");

        AssertMethodContains(actsSource, "Create", "return await CreateActAsync(request, ct);");
        AssertMethodContains(actsSource, "CreateActAsync", "await NotifyActCreatedAsync(act, ct);");
        AssertMethodContains(actsSource, "NotifyActCreatedAsync", "await _notificationService.NotifyActCreatedAsync(");
        AssertMethodContains(actsSource, "Update", "NotifyActChangedAsync");
        AssertMethodContains(actsSource, "CreateCorrection", "NotifyActCreatedAsync");
        AssertMethodContains(actsSource, "SendToSigning", "NotifyActChangedAsync");
        AssertMethodContains(actsSource, "MarkSigned", "NotifyActChangedAsync");
        AssertMethodContains(actsSource, "MarkNotSigned", "NotifyActChangedAsync");
        AssertMethodContains(actsSource, "UpdateWorkflowStatus", "NotifyActChangedAsync");
        AssertMethodContains(actsSource, "Cancel", "NotifyActChangedAsync");

        AssertMethodContains(contractsSource, "Create", "NotifyContractCreatedAsync");
        AssertMethodContains(contractsSource, "Update", "NotifyContractChangedAsync");
        AssertMethodContains(contractsSource, "SendToSigning", "NotifyContractChangedAsync");
        AssertMethodContains(contractsSource, "MarkConcluded", "NotifyContractChangedAsync");
        AssertMethodContains(contractsSource, "MarkNotSigned", "NotifyContractChangedAsync");
        AssertMethodContains(contractsSource, "UpdateStatus", "NotifyContractChangedAsync");
        AssertMethodContains(contractsSource, "Terminate", "NotifyContractChangedAsync");
        AssertMethodContains(contractsSource, "Cancel", "NotifyContractChangedAsync");
    }

    [Fact]
    public void BackgroundFinancialJobs_KeepNotificationPaths()
    {
        var statusUpdaterSource = ReadSourceFile("backend", "Services", "PaymentStatusUpdater.cs");
        var advanceReviewSource = ReadSourceFile("backend", "Services", "PaymentAdvanceReviewService.cs");
        var advanceReviewMonitoringSource = ReadSourceFile("backend", "Services", "PaymentAdvanceReviewMonitoringService.cs");
        var contractExpirationSource = ReadSourceFile("backend", "Services", "ContractExpirationNotificationService.cs");
        var overdueQueueSource = ReadSourceFile("backend", "Services", "Notifications", "PaymentOverdueNotificationQueue.cs");
        var overdueDispatcherSource = ReadSourceFile("backend", "Services", "Notifications", "PaymentOverdueNotificationDispatchProcessor.cs");
        var overdueNotificationSource = ReadSourceFile("backend", "Services", "Notifications", "NotificationService.PaymentOverdue.cs");

        AssertMethodContains(
            statusUpdaterSource,
            "RunOnceAsync",
            "PaymentOverdueNotificationQueue.Stage",
            "db.SaveChangesAsync",
            "lease.CommitAsync",
            "PaymentOverdueNotificationDispatchProcessor",
            "ProcessBatchAsync");
        var statusBody = ExtractMethodBody(statusUpdaterSource, "RunOnceAsync");
        var staged = statusBody.IndexOf("PaymentOverdueNotificationQueue.Stage", StringComparison.Ordinal);
        var saved = statusBody.IndexOf("await db.SaveChangesAsync(ct)", staged, StringComparison.Ordinal);
        var committed = statusBody.IndexOf("await lease.CommitAsync(ct)", saved, StringComparison.Ordinal);
        Assert.True(staged >= 0 && saved > staged && committed > saved);
        Assert.True(statusBody.LastIndexOf("await RecoverAsync();", StringComparison.Ordinal) > committed);
        AssertMethodContains(overdueQueueSource, "Stage", "CurrentTransaction", "PaymentStatus.Pending", "PaymentStatus.Overdue",
            "TransitionToken", "EventKey", "PaymentOverdueNotificationOutboxes.AddRange");
        AssertMethodContains(overdueQueueSource, "CurrentPayments", "PaymentStatus.Overdue", "!payment.IsPaid", "TransitionToken", "CancellationProtectedPayments");
        AssertMethodContains(overdueQueueSource, "RecipientIds", "RolePermissionCatalog.Accounts", "RolePermissionCatalog.Payments",
            "user.IsActive", "user.IsApproved", "PaymentResponsible", "permission.CanView");
        AssertMethodContains(overdueDispatcherSource, "ProcessBatchAsync", "ProcessAsync");
        AssertMethodContains(overdueDispatcherSource, "ProcessAsync", "CurrentPayments", "RecipientIds",
            "IDurablePaymentOverdueNotificationService", "NotifyOverdueForEventAsync", "row.LeaseToken == token", "row.CompletedAt == null");
        AssertMethodContains(overdueNotificationSource, "NotifyOverdueForEventAsync", "DocumentKind.Invoice",
            "UserNotificationTypes.InvoiceChanged", "UserNotificationTypes.PaymentChanged", "BalanceApplicationLease.BeginAsync",
            "lease.LockPaymentAsync", "IsOverdueRecipientCurrentAsync", "_context.UserNotifications.Add", "lease.CommitAsync",
            "QueueMaxNotificationIfAvailableAsync", "PublishDurableRealtimeAsync", "SendUserNotificationAsync");
        AssertMethodContains(
            advanceReviewSource,
            "EnsureReviewRequiredNotificationAsync",
            "UserNotificationTypes.PaymentAdvanceReviewRequired",
            "NotifyAsync");
        AssertMethodContains(
            advanceReviewSource,
            "EnsureReviewEscalationNotificationsAsync",
            "UserNotificationTypes.PaymentAdvanceReviewEscalation",
            "NotifyAsync");
        AssertMethodContains(
            advanceReviewMonitoringSource,
            "RunOnceAsync",
            "EnsureReviewRequiredNotificationAsync",
            "EnsureReviewEscalationNotificationsAsync");
        AssertMethodContains(
            contractExpirationSource,
            "RunOnceAsync",
            "ContractExpirationNotificationPolicy.ResponsibleCandidates",
            "NotifyContractExpiringAsync");
    }

    [Fact]
    public void FinancialNotificationTypes_HaveExpectedRoutingMetadata()
    {
        var expected = new Dictionary<string, (string Domain, string Priority)>(StringComparer.OrdinalIgnoreCase)
        {
            [UserNotificationTypes.PaymentAssigned] = (UserNotificationDomains.Payment, NotificationPriorities.High),
            [UserNotificationTypes.PaymentChanged] = (UserNotificationDomains.Payment, NotificationPriorities.High),
            [UserNotificationTypes.PaymentAdvanceReviewRequired] = (UserNotificationDomains.Payment, NotificationPriorities.Critical),
            [UserNotificationTypes.PaymentAdvanceReviewEscalation] = (UserNotificationDomains.Payment, NotificationPriorities.Critical),
            [UserNotificationTypes.InvoiceCreated] = (UserNotificationDomains.Invoice, NotificationPriorities.High),
            [UserNotificationTypes.InvoiceChanged] = (UserNotificationDomains.Invoice, NotificationPriorities.High),
            [UserNotificationTypes.ActCreated] = (UserNotificationDomains.Act, NotificationPriorities.High),
            [UserNotificationTypes.ActChanged] = (UserNotificationDomains.Act, NotificationPriorities.High),
            [UserNotificationTypes.ContractCreated] = (UserNotificationDomains.Contract, NotificationPriorities.High),
            [UserNotificationTypes.ContractChanged] = (UserNotificationDomains.Contract, NotificationPriorities.High),
            [UserNotificationTypes.ContractExpiring] = (UserNotificationDomains.Contract, NotificationPriorities.High),
        };
        var actual = UserNotificationTypes.All
            .Where(item =>
                string.Equals(item.Domain, UserNotificationDomains.Payment, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Domain, UserNotificationDomains.Invoice, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Domain, UserNotificationDomains.Act, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Domain, UserNotificationDomains.Contract, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            expected.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            actual.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var (key, metadata) in expected)
        {
            var type = actual[key];

            Assert.Equal(metadata.Domain, type.Domain);
            Assert.Equal(metadata.Priority, type.Priority);
            Assert.True(type.RequiresAction);
            Assert.True(type.ShowToastByDefault);
            Assert.True(type.PushEligible);
            Assert.Contains(NotificationChannels.InApp, type.EffectiveChannels);
            Assert.Contains(NotificationChannels.Toast, type.EffectiveChannels);
            Assert.Contains(NotificationChannels.WebPush, type.EffectiveChannels);
        }
    }

    private static void AssertMethodContains(string source, string methodName, params string[] expectedTokens)
    {
        var body = ExtractMethodBody(source, methodName);
        foreach (var token in expectedTokens)
        {
            Assert.Contains(token, body, StringComparison.Ordinal);
        }
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var methodMatch = Regex.Match(
            source,
            $@"(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?[^\n;=]*\b{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Singleline);
        Assert.True(methodMatch.Success, $"Method {methodName} was not found.");

        var bodyStart = source.IndexOf('{', methodMatch.Index + methodMatch.Length);
        Assert.True(bodyStart >= 0, $"Method {methodName} body start was not found.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method {methodName} body end was not found.");
    }

    private static string ReadSourceFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), Path.Combine(pathParts)));

    private static string ResolveRepositoryRoot()
    {
        var directory = TestRepository.FindDirectory();
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "PayPlanner.Api.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
