using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CaseNotificationInventoryTests
{
    [Fact]
    public void CaseEndpoints_KeepNotificationPaths()
    {
        var source = ReadSourceFile("backend", "Controllers", "V2", "CasesV2Controller.cs");

        AssertMethodContains(
            source,
            "CreateProceduralDeadline",
            "ApplyClientCaseVisibility",
            "AddProceduralDeadlineUserNotificationsAsync");
        AssertMethodContains(
            source,
            "UpdateProceduralDeadline",
            "ApplyClientCaseVisibility",
            "AddProceduralDeadlineUserNotificationsAsync");
        AssertMethodContains(
            source,
            "CloseProceduralDeadline",
            "ApplyClientCaseVisibility",
            "AddProceduralDeadlineUserNotificationsAsync");
        AssertMethodContains(
            source,
            "CreateCourtEvent",
            "ApplyClientCaseVisibility",
            "AddCourtEventUserNotificationsAsync");
        AssertMethodContains(
            source,
            "UpdateCourtEvent",
            "ApplyClientCaseVisibility",
            "AddCourtEventUserNotificationsAsync");
        AssertMethodContains(
            source,
            "UploadDocument",
            "ApplyClientCaseVisibility",
            "UploadDocumentVersionCore");
        AssertMethodContains(
            source,
            "UploadDocumentVersionById",
            "ApplyClientCaseVisibility",
            "UploadDocumentVersionCore");
        AssertMethodContains(
            source,
            "GenerateDocumentTemplate",
            "ApplyClientCaseVisibility",
            "AddCaseDocumentUserNotificationsAsync");
        AssertMethodContains(
            source,
            "UpdateDocumentById",
            "ApplyClientCaseVisibility",
            "UpdateDocumentCore");
        AssertMethodContains(
            source,
            "UpdateDocument",
            "ApplyClientCaseVisibility",
            "UpdateDocumentCore");
        AssertMethodContains(
            source,
            "DeleteDocument",
            "ApplyClientCaseVisibility",
            "AddCaseDocumentUserNotificationsAsync");
        AssertMethodContains(
            source,
            "RequestAccess",
            "ResolveCaseAccessManagerUserIdsAsync",
            "NotifyCaseAccessRequestedAsync");
        AssertMethodContains(
            source,
            "ResolveAccessRequest",
            "NotifyCaseAccessResolvedAsync");
        AssertMethodContains(
            source,
            "RunCourtSync",
            "NotifyCourtSyncFailedForCaseAsync");
        AssertMethodContains(
            source,
            "NotifyCourtSyncFailedForCaseAsync",
            "ResolveCaseNotificationRecipientIdsAsync",
            "NotifyCourtSyncFailedAsync");

        AssertMethodContains(
            source,
            "AddCourtEventUserNotificationsAsync",
            "ClientCaseStageClassifier.IsPretrial",
            "ResolveCaseNotificationRecipientIdsAsync",
            "NotifyCaseCourtEventAsync");
        AssertMethodContains(
            source,
            "AddProceduralDeadlineUserNotificationsAsync",
            "ClientCaseStageClassifier.IsPretrial",
            "ResolveCaseNotificationRecipientIdsAsync",
            "NotifyCaseProceduralDeadlineAsync");
        AssertMethodContains(
            source,
            "NotifyCourtSyncFailedForCaseAsync",
            "ClientCaseStageClassifier.IsPretrial",
            "NotifyCourtSyncFailedAsync");
        AssertMethodContains(
            source,
            "AddCaseDocumentUserNotificationsAsync",
            "ResolveCaseNotificationRecipientIdsAsync",
            "NotifyCaseDocumentAsync");
        AssertMethodContains(
            source,
            "UploadDocumentVersionCore",
            "AddCaseDocumentUserNotificationsAsync");
        AssertMethodContains(
            source,
            "UpdateDocumentCore",
            "AddCaseDocumentUserNotificationsAsync");
        AssertMethodContains(
            source,
            "ResolveCaseNotificationRecipientIdsAsync",
            "ResolveDataVisibilityAsync",
            "ApplyClientCaseVisibility");
    }

    [Fact]
    public void BackgroundCaseJobs_KeepNotificationAndAccessChecks()
    {
        var deadlineReminderSource = ReadSourceFile("backend", "Services", "CaseProceduralDeadlineReminderNotificationService.cs");
        var automationSource = ReadSourceFile("backend", "Services", "TaskJournalAutomationSchedulerService.cs");
        var courtSyncSource = ReadSourceFile("backend", "Services", "LegalIntegrations", "ClientCaseCourtSyncSchedulerService.cs");
        var inboxSource = ReadSourceFile("backend", "Controllers", "NotificationInboxController.cs");

        AssertMethodContains(
            deadlineReminderSource,
            "RunOnceAsync",
            "NotifyCaseProceduralDeadlineAsync",
            "ResolveCaseNotificationRecipientIdsAsync");
        AssertMethodContains(
            deadlineReminderSource,
            "ResolveCaseNotificationRecipientIdsAsync",
            "RolePermissionCatalog.Cases",
            "permission.CanView",
            "DataVisibilityScope.All",
            "IsHiddenFromExecutor");
        AssertMethodContains(
            automationSource,
            "NotifyCreatedAutomationTasksAsync",
            "NotifyCaseInactivityControlTaskCreatedAsync");
        Assert.Contains("RunCourtSync", courtSyncSource, StringComparison.Ordinal);
        Assert.Contains("ClientCaseStageClassifier.IsPretrial", courtSyncSource, StringComparison.Ordinal);

        Assert.Contains("UserNotificationTypes.CaseCourtEvent", inboxSource, StringComparison.Ordinal);
        Assert.Contains("UserNotificationTypes.CourtHearing", inboxSource, StringComparison.Ordinal);
        Assert.Contains("UserNotificationTypes.CourtStatusChanged", inboxSource, StringComparison.Ordinal);
        Assert.Contains("UserNotificationTypes.CourtDecisionPublished", inboxSource, StringComparison.Ordinal);
        Assert.Contains("UserNotificationTypes.CourtSyncFailed", inboxSource, StringComparison.Ordinal);
        Assert.Contains("UserNotificationTypes.CaseProceduralDeadline", inboxSource, StringComparison.Ordinal);
        Assert.Contains("UserNotificationTypes.CaseDocument", inboxSource, StringComparison.Ordinal);
        Assert.Contains("visibleCaseIds.Contains", inboxSource, StringComparison.Ordinal);
        Assert.Contains("visibleCaseDocumentIds.Contains", inboxSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PersonalNotificationInbox_RemainsSeparateFromAdminNotificationSettings()
    {
        var inboxController = typeof(NotificationInboxController);
        var inboxActions = inboxController
            .GetMethods()
            .Where(method => method.DeclaringType == inboxController)
            .Where(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: true).Any())
            .ToList();
        var notificationSettingsSource = ReadSourceFile("backend", "Controllers", "NotificationsController.cs");

        Assert.NotNull(inboxController.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).SingleOrDefault());
        Assert.Empty(inboxController.GetCustomAttributes(typeof(RequireSectionPermissionAttribute), inherit: true));
        Assert.All(
            inboxActions,
            action => Assert.Empty(action.GetCustomAttributes(typeof(RequireSectionPermissionAttribute), inherit: true)));
        Assert.Contains("CanViewNotificationSettingsAsync(CancellationToken ct)", notificationSettingsSource, StringComparison.Ordinal);
        Assert.Contains("CanEditNotificationSettingsAsync(CancellationToken ct)", notificationSettingsSource, StringComparison.Ordinal);
        Assert.Contains("RolePermissionCatalog.Notifications", notificationSettingsSource, StringComparison.Ordinal);
        Assert.Contains("permission.CanView", notificationSettingsSource, StringComparison.Ordinal);
        Assert.Contains("permission.CanEdit", notificationSettingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseAndCourtNotificationTypes_HaveExpectedRoutingMetadata()
    {
        var expected = new Dictionary<string, ExpectedNotificationType>(StringComparer.OrdinalIgnoreCase)
        {
            [UserNotificationTypes.CaseInactivityControl] = new(UserNotificationDomains.Case, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CaseAccessRequested] = new(UserNotificationDomains.Case, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CaseAccessResolved] = new(UserNotificationDomains.Case, NotificationPriorities.Normal, RequiresAction: false, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CaseProceduralDeadline] = new(UserNotificationDomains.Case, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CaseConversation] = new(UserNotificationDomains.Case, NotificationPriorities.Normal, RequiresAction: false, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CaseDocument] = new(UserNotificationDomains.Case, NotificationPriorities.Silent, RequiresAction: false, ShowToast: false, PushEligible: false),
            [UserNotificationTypes.CaseInactivityReminder] = new(UserNotificationDomains.Case, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CaseCourtEvent] = new(UserNotificationDomains.Court, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CourtHearing] = new(UserNotificationDomains.Court, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CourtStatusChanged] = new(UserNotificationDomains.Court, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CourtDecisionPublished] = new(UserNotificationDomains.Court, NotificationPriorities.High, RequiresAction: true, ShowToast: true, PushEligible: true),
            [UserNotificationTypes.CourtSyncFailed] = new(UserNotificationDomains.Court, NotificationPriorities.Critical, RequiresAction: true, ShowToast: true, PushEligible: true),
        };
        var actual = UserNotificationTypes.All
            .Where(item =>
                string.Equals(item.Domain, UserNotificationDomains.Case, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Domain, UserNotificationDomains.Court, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            expected.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            actual.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var (key, metadata) in expected)
        {
            var type = actual[key];

            Assert.Equal(metadata.Domain, type.Domain);
            Assert.Equal(metadata.Priority, type.Priority);
            Assert.Equal(metadata.RequiresAction, type.RequiresAction);
            Assert.Equal(metadata.ShowToast, type.ShowToastByDefault);
            Assert.Equal(metadata.PushEligible, type.PushEligible);
            Assert.Contains(NotificationChannels.InApp, type.EffectiveChannels);
            Assert.Equal(metadata.ShowToast, type.EffectiveChannels.Contains(NotificationChannels.Toast, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(metadata.PushEligible, type.EffectiveChannels.Contains(NotificationChannels.WebPush, StringComparer.OrdinalIgnoreCase));
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
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

    private sealed record ExpectedNotificationType(
        string Domain,
        string Priority,
        bool RequiresAction,
        bool ShowToast,
        bool PushEligible);
}
