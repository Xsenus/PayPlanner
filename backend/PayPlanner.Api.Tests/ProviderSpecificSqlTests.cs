using Xunit;

namespace PayPlanner.Api.Tests;

public class ProviderSpecificSqlTests
{
    [Fact]
    public void InvoiceAggregateLock_UsesPostgreSqlOnlyForUpdateSql()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "backend", "Controllers", "InvoicesController.cs"));
        var methodStart = source.IndexOf("private async Task LockInvoiceAggregateAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "LockInvoiceAggregateAsync was not found.");

        var methodEnd = source.IndexOf("private static DateTime ResolveInvoiceDueDate", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "LockInvoiceAggregateAsync end marker was not found.");

        var method = source[methodStart..methodEnd];
        Assert.Contains("Database.IsNpgsql()", method, StringComparison.Ordinal);
        Assert.Contains("return;", method, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_DoesNotContainUnguardedForUpdateSqlOutsideInvoiceLock()
    {
        var root = FindRepositoryRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}PayPlanner.Api.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var matches = files
            .SelectMany(path => File.ReadLines(path).Select((line, index) => new { Path = path, Line = index + 1, Text = line }))
            .Where(item => item.Text.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{Path.GetRelativePath(root, item.Path)}:{item.Line}:{item.Text.Trim()}")
            .ToList();

        var match = Assert.Single(matches);
        Assert.Contains("InvoicesController.cs", match, StringComparison.Ordinal);
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
