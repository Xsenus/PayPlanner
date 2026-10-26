using System.Reflection;
using System.Text.RegularExpressions;
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
    [InlineData("unregistered-share-owner")]
    [InlineData("unregistered-key-share-owner")]
    [InlineData("unregistered-multiline-share-owner")]
    [InlineData("contract-root-unparameterized")]
    [InlineData("policy-key-unparameterized")]
    [InlineData("seller-share-without-nowait")]
    [InlineData("seller-unbounded-addresses")]
    [InlineData("seller-unsupported-provider")]
    public void ForUpdateRegistry_RejectsUnregisteredOrUnguardedSql(string mutation)
    {
        var root = FindRepositoryRoot();
        var relativePath = mutation switch
        {
            "unregistered-owner" or "unregistered-share-owner" or "unregistered-key-share-owner"
                or "unregistered-multiline-share-owner" => "backend/Controllers/UnexpectedLocksController.cs",
            "bulk-unparameterized" or "contract-root-unparameterized" or "policy-key-unparameterized"
                => "backend/Controllers/TasksController.cs",
            "seller-share-without-nowait" or "seller-unbounded-addresses" or "seller-unsupported-provider"
                => "backend/Services/Companies/DocumentSellerLease.cs",
            _ => "backend/Services/Invoices/FinancialSourceLease.cs",
        };
        var original = relativePath == "backend/Controllers/UnexpectedLocksController.cs"
            ? "" : File.ReadAllText(Path.Combine(root, relativePath));
        var changed = mutation switch
        {
            "unregistered-owner" => "SELECT 1 FOR UPDATE",
            "lease-without-nowait" => original.Replace("FOR UPDATE NOWAIT", "FOR UPDATE", StringComparison.Ordinal),
            "bulk-unparameterized" => original.Replace("ANY({visibleIds})", "ANY(1, 2)", StringComparison.Ordinal),
            "unsupported-provider-fallback" => original.Replace("if (!db.Database.IsSqlite())", "if (false)", StringComparison.Ordinal),
            "unregistered-share-owner" => "SELECT 1 FOR SHARE",
            "unregistered-key-share-owner" => "SELECT 1 FOR KEY SHARE",
            "unregistered-multiline-share-owner" => "SELECT 1 FOR\nSHARE",
            "contract-root-unparameterized" => original.Replace("ANY({contractRoots})", "ANY(1, 2)", StringComparison.Ordinal),
            "policy-key-unparameterized" => original.Replace("{ContractTaskBillingModeSettingKey} FOR SHARE", "'tasks.contractTaskBillingMode' FOR SHARE", StringComparison.Ordinal),
            "seller-share-without-nowait" => original.Replace("FOR SHARE NOWAIT", "FOR SHARE", StringComparison.Ordinal),
            "seller-unbounded-addresses" => original.Replace("LIMIT 5001 FOR SHARE", "FOR SHARE", StringComparison.Ordinal),
            "seller-unsupported-provider" => original.Replace("if (!db.Database.IsNpgsql() && !db.Database.IsSqlite())", "if (false)", StringComparison.Ordinal),
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

        var matches = files.SelectMany(path =>
        {
            var source = ReadSource(path);
            var lines = source.Split('\n');
            return Regex.Matches(source, @"\bFOR\s+(?:(?:NO\s+KEY\s+)?UPDATE|(?:KEY\s+)?SHARE)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(match => source[..match.Index].Count(character => character == '\n'))
                .Select(index => $"{path}:{index + 1}:{lines[index].Trim()}");
        }).ToList();
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
        var originalKnownUpdates = new HashSet<string>(approved, StringComparer.Ordinal);

        var bulkStart = taskSource.IndexOf("private async Task<ActionResult<object>> BulkUpdateTaskBillingAsync", StringComparison.Ordinal);
        var bulkEnd = taskSource.IndexOf("private static IEnumerable<int> ReadContractorPaymentTaskIds", bulkStart, StringComparison.Ordinal);
        Assert.True(bulkStart >= 0 && bulkEnd > bulkStart);
        var bulkMethod = taskSource[bulkStart..bulkEnd];
        Assert.Contains("if (request.Ids.Count > 500)", bulkMethod, StringComparison.Ordinal);
        Assert.Contains("request.Ids.Distinct().OrderBy(id => id).ToArray()", bulkMethod, StringComparison.Ordinal);
        AssertOrdered(bulkMethod, "BuildVisibleTaskBaseQuery(access)", "if (visibleIds.Length > 0)",
            "if (_db.Database.IsNpgsql())", "ANY({visibleIds}) ORDER BY", "else if (_db.Database.IsSqlite())",
            ".ExecuteUpdateAsync(", "var freshAccess = await GetTaskAccessAsync(ct)");
        AssertOrdered(bulkMethod, "if (request.AllowContractClassification)",
            "await LockTaskContractBillingPolicyAsync(ct, shared: true)", "lockedPolicyRows == 1",
            "BuildVisibleTaskBaseQuery(access)", "GetEligibleContractTaskIdsAsync(visibleIds, preliminaryContractIds",
            "var contractRoots = preliminary.Where(task => preliminaryEligible.Contains(task.Id))",
            ".Distinct().OrderBy(id => id).ToArray()", "if (_db.Database.IsNpgsql() && contractRoots.Length > 0)",
            "ANY({contractRoots}) ORDER BY", "if (visibleIds.Length > 0)",
            "if (_db.Database.IsNpgsql() && contractRoots.Length > 0)", "ANY({visibleIds}) ORDER BY",
            "var freshAccess = await GetTaskAccessAsync(ct)");
        var bulkUpdates = Approve("backend/Controllers/TasksController.cs", taskSource, "BulkUpdateTaskBillingAsync", bulkStart, bulkEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"Id\" = ANY({visibleIds}) ORDER BY \"Id\" FOR UPDATE")""",
             """await _db.Database.SqlQuery<int>($"SELECT \"Id\" FROM \"Contracts\" WHERE \"Id\" = ANY({contractRoots}) ORDER BY \"Id\" FOR UPDATE NOWAIT")""",
             """await _db.Database.SqlQuery<int>($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"Id\" = ANY({visibleIds}) ORDER BY \"Id\" FOR UPDATE NOWAIT")"""]);
        originalKnownUpdates.UnionWith(bulkUpdates.Where(statement => !statement.Contains("NOWAIT", StringComparison.Ordinal)));
        Assert.Equal(8, originalKnownUpdates.Count);

        var policyStart = taskSource.IndexOf("private async Task<int> LockTaskContractBillingPolicyAsync", StringComparison.Ordinal);
        var policyEnd = taskSource.IndexOf("private async Task<HashSet<int>> GetEligibleContractTaskIdsAsync", policyStart, StringComparison.Ordinal);
        Assert.True(policyStart >= 0 && policyEnd > policyStart);
        var policyMethod = taskSource[policyStart..policyEnd];
        Assert.Contains("private const string ContractTaskBillingModeSettingKey = \"tasks.contractTaskBillingMode\";", taskSource, StringComparison.Ordinal);
        AssertOrdered(policyMethod, "if (shared && _db.Database.IsNpgsql())", "{ContractTaskBillingModeSettingKey} FOR SHARE",
            ".ToListAsync(ct)).Count", "return await _db.SystemSettings.Where(item => item.Key == ContractTaskBillingModeSettingKey)",
            ".ExecuteUpdateAsync(update => update.SetProperty(item => item.Value, item => item.Value), ct)");
        Approve("backend/Controllers/TasksController.cs", taskSource, "LockTaskContractBillingPolicyAsync", policyStart, policyEnd,
            ["""return (await _db.Database.SqlQuery<int>($"SELECT \"Id\" FROM \"SystemSettings\" WHERE \"Key\" = {ContractTaskBillingModeSettingKey} FOR SHARE")"""], "FOR SHARE");

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
        originalKnownUpdates.UnionWith(Approve("backend/Services/Invoices/FinancialSourceLease.cs", leaseSource, "LockAsync", lockStart, lockEnd,
            ["""Root.Payments => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.Acts => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.Contracts => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Contracts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.Clients => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Clients\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),""",
             """Root.LegalEntities => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),"""]));
        var idsMethod = Method(leaseSource, "private static int[] Ids", "private static void EnsureBound");
        AssertOrdered(idsMethod, "ids.Where(id => id > 0).Distinct().Take(MaximumRoots + 1).OrderBy(id => id).ToArray()",
            "EnsureBound(result.Length)");
        Assert.Contains("if (count > MaximumRoots) throw new SourceGraphTooLargeException();", leaseSource, StringComparison.Ordinal);
        Assert.Equal(13, originalKnownUpdates.Count);
        Assert.Equal(15, approved.Count(statement => statement.Contains("FOR UPDATE", StringComparison.Ordinal)));

        var sellerSource = ReadSource("backend/Services/Companies/DocumentSellerLease.cs");
        var sellerBegin = Method(sellerSource, "private static async Task<DocumentSellerLease> BeginAsync", "public static void RequireCleanTracker");
        AssertOrdered(sellerBegin, "if (!db.Database.IsNpgsql() && !db.Database.IsSqlite())", "throw new NotSupportedException(",
            "if (db.Database.CurrentTransaction is not null) return new(db, null)",
            "if (db.Database.IsNpgsql())", "BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)",
            "connection.DefaultTimeout = 1", "connection.BeginTransaction(IsolationLevel.Serializable, deferred: false)",
            "db.Database.UseTransactionAsync(native, ct)", "finally { connection.DefaultTimeout = timeout; }");
        var sellerRun = Method(sellerSource, "public static async Task<T> RunAsync<T>", "internal static async Task<DocumentSellerLease> BeginForCreationAsync");
        AssertOrdered(sellerRun, "lease = await BeginForCreationAsync(db, ct)", "var result = await action(lease)",
            "await lease.CommitAsync(ct)", "finally { if (lease is not null) await lease.DisposeAsync(); }");
        Assert.Contains("private const int MaximumPayments = 5000;", sellerSource, StringComparison.Ordinal);

        var documentStart = sellerSource.IndexOf("public async Task<IDocumentSeller[]> LockDocumentAsync", StringComparison.Ordinal);
        var documentEnd = sellerSource.IndexOf("private async Task<int[]> PaymentIdsAsync", documentStart, StringComparison.Ordinal);
        Assert.True(documentStart >= 0 && documentEnd > documentStart);
        var documentMethod = sellerSource[documentStart..documentEnd];
        AssertOrdered(documentMethod, "if (document is Payment payment)", "if (db.Database.IsNpgsql() && invoiceId.InvoiceId.HasValue)",
            "{invoiceId.InvoiceId.Value} FOR UPDATE NOWAIT", "var ids = await PaymentIdsAsync(",
            "var visibleBefore = await VisiblePaymentRootsAsync(ids, ct)", "if (db.Database.IsNpgsql())",
            "ANY({ids}) ORDER BY", "if (!ids.SequenceEqual(await PaymentIdsAsync(",
            "if (!visibleBefore.SequenceEqual(await VisiblePaymentRootsAsync(", "var rows = await db.Payments.AsNoTracking()",
            "if (document is Act act)", "if (db.Database.IsNpgsql())", "{act.Id} FOR UPDATE NOWAIT",
            "if (document is Contract contract)", "if (db.Database.IsNpgsql())", "{contract.Id} FOR UPDATE NOWAIT");
        Approve("backend/Services/Companies/DocumentSellerLease.cs", sellerSource, "LockDocumentAsync", documentStart, documentEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Invoices\" WHERE \"Id\" = {invoiceId.InvoiceId.Value} FOR UPDATE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = {act.Id} FOR UPDATE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Contracts\" WHERE \"Id\" = {contract.Id} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        var paymentIds = Method(sellerSource, "private async Task<int[]> PaymentIdsAsync", "private sealed record VisiblePaymentRoot");
        AssertOrdered(paymentIds, "db.Payments.IgnoreQueryFilters().AsNoTracking()", ".OrderBy(p => p.Id).Select(p => p.Id)",
            ".Take(MaximumPayments + 1).ToArrayAsync(ct)", "if (ids.Length > MaximumPayments) throw new SellerSelectionException(");
        var visibleRoots = Method(sellerSource, "private Task<VisiblePaymentRoot[]> VisiblePaymentRootsAsync", "public async Task LockCompanyAsync");
        Assert.Contains("db.Payments.AsNoTracking().Where(payment => ids.Contains(payment.Id))", visibleRoots, StringComparison.Ordinal);
        Assert.DoesNotContain("IgnoreQueryFilters", visibleRoots, StringComparison.Ordinal);

        var companyStart = sellerSource.IndexOf("public async Task LockCompanyAsync", StringComparison.Ordinal);
        var companyEnd = sellerSource.IndexOf("public async Task LockRegistryAsync", companyStart, StringComparison.Ordinal);
        Assert.True(companyStart >= 0 && companyEnd > companyStart);
        var companyMethod = sellerSource[companyStart..companyEnd];
        AssertOrdered(companyMethod, "if (!db.Database.IsNpgsql())", ".Take(MaximumPayments + 1).CountAsync(ct) > MaximumPayments)",
            "throw new SellerSelectionException(", "return;", "await LockRegistryAsync(ct)", "{companyId} FOR UPDATE NOWAIT",
            "LIMIT 5001 FOR SHARE NOWAIT", "if (addresses.Length > MaximumPayments) throw new SellerSelectionException(",
            "if (branchId.HasValue)", "{branchId.Value} FOR SHARE NOWAIT");
        Approve("backend/Services/Companies/DocumentSellerLease.cs", sellerSource, "LockCompanyAsync", companyStart, companyEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        Approve("backend/Services/Companies/DocumentSellerLease.cs", sellerSource, "LockCompanyAsync", companyStart, companyEnd,
            ["""var addresses = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"AddressRecords\" WHERE \"LegalEntityId\" = {companyId} ORDER BY \"Id\" LIMIT 5001 FOR SHARE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"CompanyBranches\" WHERE \"Id\" = {branchId.Value} FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");

        var registryStart = sellerSource.IndexOf("public async Task LockRegistryAsync", StringComparison.Ordinal);
        var registryEnd = sellerSource.IndexOf("private static bool IsConflict", registryStart, StringComparison.Ordinal);
        Assert.True(registryStart >= 0 && registryEnd > registryStart);
        var registryMethod = sellerSource[registryStart..registryEnd];
        AssertOrdered(registryMethod, "if (!db.Database.IsNpgsql()) return;",
            "var keys = new[] { \"legalEntities.mainCompanyId\", \"legalEntities.mainCompanyIds\", \"legalEntities.defaultMainCompanyId\" };",
            "ANY({keys}) ORDER BY");
        Approve("backend/Services/Companies/DocumentSellerLease.cs", sellerSource, "LockRegistryAsync", registryStart, registryEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = ANY({keys}) ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");

        Assert.Equal(20, approved.Count(statement => statement.Contains("FOR UPDATE", StringComparison.Ordinal)));
        Assert.Equal(4, approved.Count(statement => statement.Contains("FOR SHARE", StringComparison.Ordinal)));
        Assert.Equal(24, approved.Count);

        // Exact identities include owner, method range, line and complete SQL call.
        // An extra statement in any other production file/method remains a failure.
        Assert.Equal(approved.OrderBy(value => value, StringComparer.Ordinal), matches.OrderBy(value => value, StringComparer.Ordinal));

        string ReadSource(string relativePath)
            => overrides is not null && overrides.TryGetValue(relativePath, out var source)
                ? source : File.ReadAllText(Path.Combine(root, relativePath));

        string[] Approve(string relativePath, string source, string method, int start, int end, string[] expected, string clause = "FOR UPDATE")
        {
            var firstLine = source[..start].Count(character => character == '\n') + 1;
            var statements = source[start..end].Split('\n')
                .Select((text, index) => new { Text = text.Trim(), Line = firstLine + index })
                .Where(line => line.Text.Contains(clause, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(expected.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(statements.Select(line => line.Text).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal),
                $"Unregistered SQL template in {relativePath}/{method}.");
            foreach (var line in statements)
                Assert.True(approved.Add($"{relativePath}:{line.Line}:{line.Text}"), $"Overlapping SQL owner: {relativePath}/{method}.");
            return statements.Select(line => $"{relativePath}:{line.Line}:{line.Text}").ToArray();
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
