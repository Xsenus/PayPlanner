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
        => AssertRepositoryForUpdateSql(FindRepositoryRoot());

    [Theory]
    [InlineData("unregistered-owner")]
    [InlineData("lease-without-nowait")]
    [InlineData("bulk-unparameterized")]
    [InlineData("unsupported-provider-fallback")]
    public void ForUpdateRegistry_RejectsUnregisteredOrUnguardedSql(string mutation)
    {
        var root = FindRepositoryRoot();
        var relativePath = mutation switch
        {
            "unregistered-owner" => "backend/Controllers/UnexpectedLocksController.cs",
            "bulk-unparameterized" => "backend/Controllers/TasksController.cs",
            _ => "backend/Services/Invoices/FinancialSourceLease.cs",
        };
        var original = mutation == "unregistered-owner" ? "" : File.ReadAllText(Path.Combine(root, relativePath));
        var changed = mutation switch
        {
            "unregistered-owner" => "SELECT 1 FOR UPDATE",
            "lease-without-nowait" => original.Replace("FOR UPDATE NOWAIT", "FOR UPDATE", StringComparison.Ordinal),
            "bulk-unparameterized" => original.Replace("ANY({visibleIds})", "ANY(1, 2)", StringComparison.Ordinal),
            "unsupported-provider-fallback" => original.Replace("if (!db.Database.IsSqlite())", "if (false)", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.NotEqual(original, changed);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRepositoryForUpdateSql(root,
            new Dictionary<string, string>(StringComparer.Ordinal) { [relativePath] = changed }));
    }

    private static void AssertRepositoryForUpdateSql(string root, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var files = Directory
            .EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}PayPlanner.Api.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Concat(overrides?.Keys ?? [])
            .Distinct(StringComparer.Ordinal);

        var matches = files
            .SelectMany(path => ReadSource(path).Split('\n').Select((line, index) => new { Path = path, Line = index + 1, Text = line }))
            .Where(item => item.Text.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.Path}:{item.Line}:{item.Text.Trim()}")
            .ToList();
        var approved = new HashSet<string>(StringComparer.Ordinal);

        var invoiceSource = ReadSource("backend/Controllers/InvoicesController.cs");
        var deletionLockStart = invoiceSource.IndexOf("private async Task LockInvoiceDeletionPaymentsAsync", StringComparison.Ordinal);
        var deletionLockEnd = invoiceSource.IndexOf("private static List<int> GetInvoiceEntityIdsToDelete", deletionLockStart, StringComparison.Ordinal);
        Assert.True(deletionLockStart >= 0 && deletionLockEnd > deletionLockStart);
        var deletionLockMethod = invoiceSource[deletionLockStart..deletionLockEnd];
        Assert.Contains("if (!_db.Database.IsNpgsql())", deletionLockMethod, StringComparison.Ordinal);
        Assert.Equal(1, deletionLockMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);
        Approve("backend/Controllers/InvoicesController.cs", invoiceSource, "LockInvoiceDeletionPaymentsAsync", deletionLockStart, deletionLockEnd,
            [""".SqlQuery<int>($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE")"""]);

        var aggregateStart = invoiceSource.IndexOf("private async Task LockInvoiceAggregateAsync", StringComparison.Ordinal);
        var aggregateEnd = invoiceSource.IndexOf("private static DateTime ResolveInvoiceDueDate", aggregateStart, StringComparison.Ordinal);
        Assert.True(aggregateStart >= 0 && aggregateEnd > aggregateStart);
        var aggregateMethod = invoiceSource[aggregateStart..aggregateEnd];
        Assert.Contains("if (!_db.Database.IsNpgsql())", aggregateMethod, StringComparison.Ordinal);
        Approve("backend/Controllers/InvoicesController.cs", invoiceSource, "LockInvoiceAggregateAsync", aggregateStart, aggregateEnd,
            [""".SqlQuery<int>($"SELECT 1 FROM \"Payments\" WHERE \"InvoiceId\" = {invoiceEntityId} FOR UPDATE")"""]);

        var taskSource = ReadSource("backend/Controllers/TasksController.cs");
        var classificationStart = taskSource.IndexOf("public async Task<ActionResult<TaskJournalItemDto>> UpdateTaskBillingClassification", StringComparison.Ordinal);
        var classificationEnd = taskSource.IndexOf("[HttpGet(\"{id:int}/billing-invoices\")]", classificationStart, StringComparison.Ordinal);
        Assert.True(classificationStart >= 0 && classificationEnd > classificationStart);
        var classificationMethod = taskSource[classificationStart..classificationEnd];
        Assert.Contains("if (_db.Database.IsNpgsql())", classificationMethod, StringComparison.Ordinal);
        Assert.Equal(1, classificationMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);
        Approve("backend/Controllers/TasksController.cs", taskSource, "UpdateTaskBillingClassification", classificationStart, classificationEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT 1 FROM \"TaskJournalItems\" WHERE \"Id\" = {id} FOR UPDATE")"""]);

        var linkStart = taskSource.IndexOf("public async Task<ActionResult<TaskJournalItemDto>> LinkTaskBillingInvoice", StringComparison.Ordinal);
        var linkEnd = taskSource.IndexOf("private IQueryable<Payment> EligibleTaskBillingInvoices", linkStart, StringComparison.Ordinal);
        Assert.True(linkStart >= 0 && linkEnd > linkStart);
        var linkMethod = taskSource[linkStart..linkEnd];
        Assert.Contains("if (_db.Database.IsNpgsql())", linkMethod, StringComparison.Ordinal);
        Assert.Equal(2, linkMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);
        Approve("backend/Controllers/TasksController.cs", taskSource, "LinkTaskBillingInvoice", linkStart, linkEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT 1 FROM \"Payments\" WHERE \"Id\" = {request.InvoiceId} FOR UPDATE")""",
             """await _db.Database.SqlQuery<int>($"SELECT 1 FROM \"TaskJournalItems\" WHERE \"Id\" = {id} FOR UPDATE")"""]);

        var updateStart = taskSource.IndexOf("public async Task<ActionResult<TaskJournalItemDto>> UpdateTask(", StringComparison.Ordinal);
        var updateEnd = taskSource.IndexOf("[HttpPatch(\"{id:int}/status\")]", updateStart, StringComparison.Ordinal);
        Assert.True(updateStart >= 0 && updateEnd > updateStart);
        var updateMethod = taskSource[updateStart..updateEnd];
        Assert.Contains("if (_db.Database.IsNpgsql())", updateMethod, StringComparison.Ordinal);
        Assert.Equal(1, updateMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);
        Approve("backend/Controllers/TasksController.cs", taskSource, "UpdateTask", updateStart, updateEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT 1 FROM \"TaskJournalItems\" WHERE \"Id\" = {id} FOR UPDATE")"""]);

        var createStart = invoiceSource.IndexOf("public async Task<ActionResult<InvoiceDto>> Create(", StringComparison.Ordinal);
        var createEnd = invoiceSource.IndexOf("[HttpPut(\"{id:int}\")]", createStart, StringComparison.Ordinal);
        Assert.True(createStart >= 0 && createEnd > createStart);
        var createMethod = invoiceSource[createStart..createEnd];
        Assert.Contains("if (_db.Database.IsNpgsql() && billingTaskIds.Count > 0)", createMethod, StringComparison.Ordinal);
        Assert.Equal(1, createMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);
        Approve("backend/Controllers/InvoicesController.cs", invoiceSource, "Create", createStart, createEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"Id\" = ANY({billingTaskIds.ToArray()}) ORDER BY \"Id\" FOR UPDATE")"""]);

        // Preserve the original seven guarded statements as a separately checked set.
        Assert.Equal(7, approved.Count);
        Assert.Equal(3, approved.Count(match => match.Contains("InvoicesController.cs", StringComparison.Ordinal)));
        Assert.Equal(4, approved.Count(match => match.Contains("TasksController.cs", StringComparison.Ordinal)));

        var bulkStart = taskSource.IndexOf("private async Task<ActionResult<object>> BulkUpdateTaskBillingAsync", StringComparison.Ordinal);
        var bulkEnd = taskSource.IndexOf("private static IEnumerable<int> ReadContractorPaymentTaskIds", bulkStart, StringComparison.Ordinal);
        Assert.True(bulkStart >= 0 && bulkEnd > bulkStart);
        var bulkMethod = taskSource[bulkStart..bulkEnd];
        Assert.Contains("if (request.Ids.Count > 500)", bulkMethod, StringComparison.Ordinal);
        Assert.Contains("request.Ids.Distinct().OrderBy(id => id).ToArray()", bulkMethod, StringComparison.Ordinal);
        AssertOrdered(bulkMethod, "BuildVisibleTaskBaseQuery(access)", "if (visibleIds.Length > 0)",
            "if (_db.Database.IsNpgsql())", "ANY({visibleIds}) ORDER BY", "else if (_db.Database.IsSqlite())",
            ".ExecuteUpdateAsync(", "var freshAccess = await GetTaskAccessAsync(ct)");
        Approve("backend/Controllers/TasksController.cs", taskSource, "BulkUpdateTaskBillingAsync", bulkStart, bulkEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"Id\" = ANY({visibleIds}) ORDER BY \"Id\" FOR UPDATE")"""]);

        var leaseSource = ReadSource("backend/Services/Invoices/FinancialSourceLease.cs");
        var begin = Method(leaseSource, "private static async Task<FinancialSourceLease> BeginAsync", "public async Task CommitAsync");
        AssertOrdered(begin, "if (db.Database.IsNpgsql())", "BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)",
            "if (!db.Database.IsSqlite())", "throw new InvalidOperationException(\"Unsupported financial source lease provider.\")",
            "connection.DefaultTimeout = 1", "connection.BeginTransaction(IsolationLevel.Serializable, deferred: false)",
            "db.Database.UseTransactionAsync(native, ct)", "finally { connection.DefaultTimeout = timeout; }");
        var run = Method(leaseSource, "public static async Task<ActionResult<T>> RunAsync<T>", "private static async Task<FinancialSourceLease> BeginAsync");
        AssertOrdered(run, "lease = await BeginAsync(db, ct)", "return await action(lease)");
        Assert.Contains("private const int MaximumRoots = 5000;", leaseSource, StringComparison.Ordinal);
        foreach (var (start, end) in new[]
        {
            ("private async Task<PaymentRoot[]> PaymentRootsAsync", "private static async Task<ActRoot[]> BoundedActsAsync"),
            ("private static async Task<ActRoot[]> BoundedActsAsync", "private static async Task<int[]> BoundedIdsAsync"),
            ("private static async Task<int[]> BoundedIdsAsync", "private async Task LockGraphAsync"),
        })
        {
            AssertOrdered(Method(leaseSource, start, end), "Take(MaximumRoots + 1)", ".ToArrayAsync(ct)", "EnsureBound(");
        }
        var graph = Method(leaseSource, "private async Task LockGraphAsync", "private async Task LockAsync");
        AssertOrdered(graph, "EnsureBound(graph.Payments.Length + graph.Acts.Length + graph.Contracts.Length",
            "+ graph.Clients.Length + graph.Companies.Length)",
            "await LockAsync(Root.Payments", "await LockAsync(Root.Acts", "await LockAsync(Root.Contracts",
            "await LockAsync(Root.Clients", "await LockAsync(Root.LegalEntities");
        var lockStart = leaseSource.IndexOf("private async Task LockAsync", StringComparison.Ordinal);
        var lockEnd = leaseSource.IndexOf("private static int[] Ids", lockStart, StringComparison.Ordinal);
        Assert.True(lockStart >= 0 && lockEnd > lockStart);
        var lockMethod = leaseSource[lockStart..lockEnd];
        AssertOrdered(lockMethod, "if (ids.Length == 0 || db.Database.IsSqlite()) return;", "var query = root switch",
            "_ => throw new ArgumentOutOfRangeException(nameof(root))", "await query.ToArrayAsync(ct)");
        Approve("backend/Services/Invoices/FinancialSourceLease.cs", leaseSource, "LockAsync", lockStart, lockEnd,
            ["""Root.Payments => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.Acts => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.Contracts => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Contracts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.Clients => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Clients\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.LegalEntities => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),"""]);
        var idsMethod = Method(leaseSource, "private static int[] Ids", "private static void EnsureBound");
        AssertOrdered(idsMethod, "ids.Where(id => id > 0).Distinct().Take(MaximumRoots + 1).OrderBy(id => id).ToArray()",
            "EnsureBound(result.Length)");
        Assert.Contains("if (count > MaximumRoots) throw new SourceGraphTooLargeException();", leaseSource, StringComparison.Ordinal);

        // Exact identities include owner, method range, line and complete SQL call.
        // An extra statement in any other production file/method remains a failure.
        Assert.Equal(approved.OrderBy(value => value, StringComparer.Ordinal), matches.OrderBy(value => value, StringComparer.Ordinal));

        string ReadSource(string relativePath)
            => overrides is not null && overrides.TryGetValue(relativePath, out var source)
                ? source : File.ReadAllText(Path.Combine(root, relativePath));

        void Approve(string relativePath, string source, string method, int start, int end, string[] expected)
        {
            var firstLine = source[..start].Count(character => character == '\n') + 1;
            var statements = source[start..end].Split('\n')
                .Select((text, index) => new { Text = text.Trim(), Line = firstLine + index })
                .Where(line => line.Text.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(expected.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(statements.Select(line => line.Text).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal),
                $"Unregistered SQL template in {relativePath}/{method}.");
            foreach (var line in statements)
                Assert.True(approved.Add($"{relativePath}:{line.Line}:{line.Text}"), $"Overlapping SQL owner: {relativePath}/{method}.");
        }
    }

    private static string Method(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing method boundary: {endMarker}");
        return source[start..end];
    }

    private static void AssertOrdered(string source, params string[] fragments)
    {
        var cursor = 0;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, cursor, StringComparison.Ordinal);
            Assert.True(index >= cursor, $"Missing or reordered SQL safety condition: {fragment}");
            cursor = index + fragment.Length;
        }
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
