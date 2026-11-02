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
        NormalizePath("backend/Services/Notifications/NotificationService.cs"),
        NormalizePath("backend/Services/Notifications/NotificationService.PaymentOverdue.cs")
    ];

    [Fact]
    public void ProductionUserNotifications_AreCreatedOnlyThroughNotificationService()
    {
        Assert.Empty(FindUnauthorizedWrites(ResolveRepositoryRoot()));
    }

    [Fact]
    public void Inventory_RejectsUnreviewedPartsAndOtherNotificationProducers()
    {
        var directory = Directory.CreateTempSubdirectory("payplanner-notification-inventory-");
        string[] reviewedFiles =
        [
            "backend/Services/Notifications/NotificationService.cs",
            "backend/Services/Notifications/NotificationService.PaymentOverdue.cs"
        ];
        string[] forbiddenFiles =
        [
            "backend/Services/Notifications/NotificationService.Other.cs",
            "backend/Services/Notifications/NotificationService.PaymentOverdue.cs.backup.cs",
            "backend/Controllers/NotificationService.cs"
        ];
        try
        {
            foreach (var relativePath in reviewedFiles.Concat(forbiddenFiles))
            {
                var file = Path.Combine(directory.FullName, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "new UserNotification();\ncontext.UserNotifications.Add(row);\ncontext.UserNotifications.AddRange(rows);");
            }

            var offenders = FindUnauthorizedWrites(directory.FullName);

            Assert.Equal(forbiddenFiles.Order(StringComparer.Ordinal),
                offenders.Select(hit => hit.RelativePath).Distinct().Order(StringComparer.Ordinal));
            Assert.All(forbiddenFiles, file => Assert.Equal(3, offenders.Count(hit => hit.RelativePath == file)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static DirectWriteHit[] FindUnauthorizedWrites(string repositoryRoot)
        => Directory.EnumerateFiles(Path.Combine(repositoryRoot, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionSourceFile)
            .SelectMany(file => FindDirectUserNotificationWrites(repositoryRoot, file))
            .Where(hit => !AllowedProductionFiles.Contains(hit.RelativePath, StringComparer.OrdinalIgnoreCase))
            .ToArray();

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
