using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ProviderSpecificSqlTests
{
    [Fact]
    public async Task InvoiceDeletionLock_ExecutesAgainstPostgreSql()
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TEMP TABLE \"Payments\" (\"Id\" integer PRIMARY KEY); INSERT INTO \"Payments\" (\"Id\") VALUES (1)";
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connection)
            .Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var controller = new InvoicesController(db, null!, null!);
        var method = typeof(InvoicesController).GetMethod("LockInvoiceDeletionPaymentsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var lockTask = Assert.IsAssignableFrom<Task>(method.Invoke(controller,
            [new[] { new Payment { Id = 1 } }, CancellationToken.None]));
        await lockTask;
        await transaction.RollbackAsync();
    }

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
    public void Repository_ForUpdateSqlIsLimitedToGuardedInvoiceAndTaskLocks()
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

        Assert.Equal(5, matches.Count);
        Assert.Equal(2, matches.Count(match => match.Contains("InvoicesController.cs", StringComparison.Ordinal)));
        Assert.Equal(3, matches.Count(match => match.Contains("TasksController.cs", StringComparison.Ordinal)));

        var invoiceSource = File.ReadAllText(Path.Combine(root, "backend", "Controllers", "InvoicesController.cs"));
        var deletionLockStart = invoiceSource.IndexOf("private async Task LockInvoiceDeletionPaymentsAsync", StringComparison.Ordinal);
        var deletionLockEnd = invoiceSource.IndexOf("private static List<int> GetInvoiceEntityIdsToDelete", deletionLockStart, StringComparison.Ordinal);
        Assert.True(deletionLockStart >= 0 && deletionLockEnd > deletionLockStart);
        var deletionLockMethod = invoiceSource[deletionLockStart..deletionLockEnd];
        Assert.Contains("if (!_db.Database.IsNpgsql())", deletionLockMethod, StringComparison.Ordinal);
        Assert.Equal(1, deletionLockMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);

        var taskSource = File.ReadAllText(Path.Combine(root, "backend", "Controllers", "TasksController.cs"));
        var classificationStart = taskSource.IndexOf("public async Task<ActionResult<TaskJournalItemDto>> UpdateTaskBillingClassification", StringComparison.Ordinal);
        var classificationEnd = taskSource.IndexOf("[HttpGet(\"{id:int}/billing-invoices\")]", classificationStart, StringComparison.Ordinal);
        Assert.True(classificationStart >= 0 && classificationEnd > classificationStart);
        var classificationMethod = taskSource[classificationStart..classificationEnd];
        Assert.Contains("if (_db.Database.IsNpgsql())", classificationMethod, StringComparison.Ordinal);
        Assert.Equal(1, classificationMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);

        var linkStart = taskSource.IndexOf("public async Task<ActionResult<TaskJournalItemDto>> LinkTaskBillingInvoice", StringComparison.Ordinal);
        var linkEnd = taskSource.IndexOf("private IQueryable<Payment> EligibleTaskBillingInvoices", linkStart, StringComparison.Ordinal);
        Assert.True(linkStart >= 0 && linkEnd > linkStart);
        var linkMethod = taskSource[linkStart..linkEnd];
        Assert.Contains("if (_db.Database.IsNpgsql())", linkMethod, StringComparison.Ordinal);
        Assert.Equal(2, linkMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);
    }

    private static string FindRepositoryRoot()
    {
        var current = TestRepository.FindDirectory();

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
