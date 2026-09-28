using Xunit;

namespace PayPlanner.Api.Tests;

public class BatchOperationAuditCoverageTests
{
    [Fact]
    public void BatchOperationsThatBypassChangeTracker_AreDocumented()
    {
        var root = FindRepositoryRoot();
        var backendRoot = Path.Combine(root, "backend");
        var document = File.ReadAllText(Path.Combine(root, "docs", "batch-operations-audit-coverage-2026-06-10.md"));
        var files = Directory
            .EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}PayPlanner.Api.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var operations = files
            .SelectMany(path => File.ReadLines(path).Select((line, index) => new
            {
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
                Line = index + 1,
                Text = line,
            }))
            .Where(item =>
                item.Text.Contains("ExecuteUpdateAsync", StringComparison.Ordinal)
                || item.Text.Contains("ExecuteDeleteAsync", StringComparison.Ordinal))
            .Select(item => $"{item.RelativePath}:{item.Line}")
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(29, operations.Count);
        foreach (var operation in operations)
        {
            Assert.Contains(operation, document, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && Directory.Exists(Path.Combine(current.FullName, "backend")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
