using System.Text.RegularExpressions;
using Xunit;

namespace PayPlanner.Api.Tests;

public class NotificationCreationInventoryTests
{
    private static readonly Regex DirectUserNotificationWritePattern = new(
        @"new\s+UserNotification\b|UserNotifications\.(Add|AddRange)\b",
        RegexOptions.Compiled);

    private static readonly string[] AllowedProductionFiles =
    [
        NormalizePath("backend/Services/Notifications/NotificationService.cs")
    ];

    [Fact]
    public void ProductionUserNotifications_AreCreatedOnlyThroughNotificationService()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var backendRoot = Path.Combine(repositoryRoot, "backend");

        var offenders = Directory
            .EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionSourceFile)
            .SelectMany(file => FindDirectUserNotificationWrites(repositoryRoot, file))
            .Where(hit => !AllowedProductionFiles.Contains(hit.RelativePath, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<DirectWriteHit> FindDirectUserNotificationWrites(string repositoryRoot, string file)
    {
        var text = File.ReadAllText(file);
        if (!DirectUserNotificationWritePattern.IsMatch(text))
        {
            yield break;
        }

        var relativePath = NormalizePath(Path.GetRelativePath(repositoryRoot, file));
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (DirectUserNotificationWritePattern.IsMatch(lines[index]))
            {
                yield return new DirectWriteHit(relativePath, index + 1, lines[index].Trim());
            }
        }
    }

    private static bool IsProductionSourceFile(string file)
    {
        var normalized = NormalizePath(file);
        return !normalized.Contains("/PayPlanner.Api.Tests/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase);
    }

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

    private static string NormalizePath(string value)
        => value.Replace('\\', '/');

    private sealed record DirectWriteHit(string RelativePath, int Line, string SourceLine)
    {
        public override string ToString() => $"{RelativePath}:{Line}: {SourceLine}";
    }
}
