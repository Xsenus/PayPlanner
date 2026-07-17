using System.Text.RegularExpressions;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class StickyNoteNotificationInventoryTests
{
    [Fact]
    public void StickyNoteMutationEndpoints_KeepRealtimeAndNotificationPaths()
    {
        var controllerSource = ReadSourceFile("backend", "Controllers", "StickyNotesController.cs");

        AssertMethodContains(
            controllerSource,
            "CreateNote",
            "PublishAsync",
            "NotifyMentionedUsersAsync",
            "NotifyAssignedUserAsync");

        AssertMethodContains(
            controllerSource,
            "UpdateNote",
            "PublishAsync",
            "NotifyMentionedUsersAsync",
            "NotifyAssignedUserAsync",
            "ResolveImportantNoteAction",
            "NotifyImportantSharedBoardChangeAsync");

        AssertMethodContains(
            controllerSource,
            "UpdateNoteLayout",
            "PublishAsync");

        AssertMethodContains(
            controllerSource,
            "DeleteNote",
            "PublishAsync",
            "NotifyImportantSharedBoardChangeAsync");

        AssertMethodContains(
            controllerSource,
            "RestoreNote",
            "PublishAsync");
    }

    [Fact]
    public void StickyNotesController_UsesDatabaseAwareAdminFallback()
    {
        var controllerSource = ReadSourceFile("backend", "Controllers", "StickyNotesController.cs");

        Assert.DoesNotContain("User.IsInRole(\"admin\")", controllerSource, StringComparison.Ordinal);
        AssertMethodContains(
            controllerSource,
            "ResolvePermissionValuesAsync",
            "IsUserAdminOrLegacyFallbackAsync");
    }

    [Fact]
    public void StickyNoteReminderService_KeepsVisibilityCheckAndNotificationPath()
    {
        var serviceSource = ReadSourceFile("backend", "Services", "Notifications", "StickyNoteReminderNotificationService.cs");

        AssertMethodContains(
            serviceSource,
            "ProcessDueRemindersAsync",
            "CanAssignedUserView",
            "RolePermissionCatalog.Notes",
            "StickyNoteBoardVisibility.Shared",
            "OwnerUserId",
            "Members.Any",
            "NotifyStickyNoteReminderAsync",
            "ReminderSentAt");
    }

    [Fact]
    public void StickyNoteNotificationTypes_HaveExpectedRoutingMetadata()
    {
        var expected = new[]
        {
            UserNotificationTypes.NoteMention,
            UserNotificationTypes.NoteAssigned,
            UserNotificationTypes.NoteReminder,
            UserNotificationTypes.NoteImportantChanged,
        };
        var actual = UserNotificationTypes.All
            .Where(item => string.Equals(item.Domain, UserNotificationDomains.Note, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            expected.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            actual.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var key in expected)
        {
            var type = actual[key];

            Assert.Equal(UserNotificationDomains.Note, type.Domain);
            Assert.Equal(NotificationPriorities.High, type.Priority);
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
}
