using Xunit;

namespace PayPlanner.Api.Tests;

public class CalendarNotificationInventoryTests
{
    [Fact]
    public void CalendarTaskMutationEndpoints_KeepNotificationsAndExternalSync()
    {
        var controllerSource = ReadSourceFile("backend", "Controllers", "TasksController.cs");

        AssertMethodContains(
            controllerSource,
            "CreateTask",
            "NotifyTaskAssignedAsync",
            "NotifyTaskScheduleConflictIfNeededAsync",
            "SafeSyncExternalCalendarTaskAsync");

        AssertMethodContains(
            controllerSource,
            "UpdateTask",
            "NotifyTaskChangedAsync",
            "NotifyTaskReassignedAsync",
            "NotifyTaskCancelledAsync",
            "NotifyTaskCompletedAsync",
            "NotifyTaskApprovalRequestedAsync",
            "NotifyTaskScheduleConflictIfNeededAsync",
            "SafeSyncExternalCalendarTaskAsync");

        AssertMethodContains(
            controllerSource,
            "UpdateStatus",
            "NotifyTaskChangedAsync",
            "NotifyTaskCancelledAsync",
            "NotifyTaskCompletedAsync",
            "NotifyTaskApprovalRequestedAsync",
            "SafeSyncExternalCalendarTaskAsync");

        AssertMethodContains(
            controllerSource,
            "BulkUpdateTasks",
            "NotifyTaskChangedAsync",
            "NotifyTaskReassignedAsync",
            "NotifyTaskCancelledAsync",
            "NotifyTaskCompletedAsync",
            "NotifyTaskApprovalRequestedAsync",
            "NotifyTaskScheduleConflictIfNeededAsync",
            "SafeSyncExternalCalendarTaskAsync");

        AssertMethodContains(
            controllerSource,
            "UpdateApproval",
            "NotifyTaskApprovalDecisionAsync",
            "SafeSyncExternalCalendarTaskAsync");

        AssertMethodContains(
            controllerSource,
            "DeleteTask",
            "DeleteTaskForConnectedUsersAsync",
            "NotifyTaskCancelledAsync");

        AssertMethodContains(
            controllerSource,
            "BulkDeleteTasks",
            "DeleteTaskForConnectedUsersAsync",
            "NotifyTaskCancelledAsync");
    }

    [Fact]
    public void CalendarAccessAndExternalSyncWarnings_KeepRealtimeNotificationPaths()
    {
        var rolePermissionsSource = ReadSourceFile("backend", "Controllers", "RolePermissionsController.cs");
        var coordinatorSource = ReadSourceFile("backend", "Services", "ExternalCalendarSyncCoordinator.cs");
        var googleBackgroundSource = ReadSourceFile("backend", "Services", "GoogleCalendarBackgroundSyncService.cs");
        var yandexBackgroundSource = ReadSourceFile("backend", "Services", "YandexCalendarBackgroundSyncService.cs");

        Assert.Contains("NotifyCalendarAccessChangedAsync", rolePermissionsSource, StringComparison.Ordinal);
        Assert.Contains("NotifyCalendarWarningAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("NotifyExternalCalendarSyncWarningAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("NotifyExternalCalendarSyncWarningAsync", googleBackgroundSource, StringComparison.Ordinal);
        Assert.Contains("NotifyExternalCalendarSyncWarningAsync", yandexBackgroundSource, StringComparison.Ordinal);
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
        var methodNameIndex = source.IndexOf(methodName + "(", StringComparison.Ordinal);
        Assert.True(methodNameIndex >= 0, $"Method {methodName} was not found.");

        var bodyStart = source.IndexOf('{', methodNameIndex);
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
