using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ProviderSpecificSqlTests
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
    [InlineData("profile-lock-without-nowait")]
    [InlineData("profile-lock-wrong-provider")]
    [InlineData("print-defaults-lock-without-nowait")]
    [InlineData("print-defaults-lock-wrong-provider")]
    [InlineData("act-invoice-lock-without-nowait")]
    [InlineData("payment-attempt-without-nowait")]
    [InlineData("payment-input-without-nowait")]
    [InlineData("payment-input-wrong-provider")]
    [InlineData("payment-input-without-transaction")]
    [InlineData("request-lock-without-nowait")]
    [InlineData("request-access-without-nowait")]
    [InlineData("upload-share-without-nowait")]
    [InlineData("upload-wrong-provider")]
    [InlineData("deadline-lease-without-nowait")]
    [InlineData("deadline-lease-wrong-provider")]
    [InlineData("note-lease-without-nowait")]
    [InlineData("note-lease-wrong-provider")]
    [InlineData("binding-share-without-nowait")]
    [InlineData("binding-revoke-without-nowait")]
    [InlineData("binding-wrong-provider")]
    [InlineData("binding-stale-session")]
    [InlineData("binding-foreign-money-lock")]
    [InlineData("binding-guard-without-nowait")]
    [InlineData("binding-guard-wrong-provider")]
    [InlineData("binding-guard-unbounded")]
    [InlineData("binding-guard-unparameterized")]
    [InlineData("document-bank-without-nowait")]
    [InlineData("document-bank-wrong-provider")]
    [InlineData("document-bank-weak-source-lock")]
    [InlineData("document-bank-foreign-scope")]
    [InlineData("document-bank-unbounded")]
    [InlineData("document-bank-without-transaction")]
    [InlineData("balance-without-nowait")]
    [InlineData("balance-wrong-provider")]
    [InlineData("balance-source-without-nowait")]
    [InlineData("allocation-without-nowait")]
    [InlineData("allocation-unbounded")]
    [InlineData("overdue-without-nowait")]
    [InlineData("advance-cursor-without-nowait")]
    [InlineData("invoice-cancel-without-nowait")]
    [InlineData("act-delete-without-nowait")]
    [InlineData("act-delete-wrong-provider")]
    [InlineData("act-delete-without-transaction")]
    [InlineData("act-delete-wrong-id")]
    [InlineData("act-delete-early-number-scope")]
    [InlineData("act-rollback-without-nowait")]
    [InlineData("act-rollback-wrong-provider")]
    [InlineData("act-rollback-without-transaction")]
    [InlineData("act-rollback-wrong-id")]
    [InlineData("act-rollback-without-id-guard")]
    [InlineData("act-rollback-early-number-scope")]
    public void ForUpdateRegistry_RejectsUnregisteredOrUnguardedSql(string mutation)
    {
        var root = FindRepositoryRoot();
        AssertRepositoryForUpdateSql(root);
        var relativePath = mutation switch
        {
            "act-delete-without-nowait" or "act-delete-wrong-provider" or "act-delete-without-transaction"
                or "act-delete-wrong-id" or "act-delete-early-number-scope" => "backend/Controllers/ActsController.cs",
            "act-rollback-without-nowait" or "act-rollback-wrong-provider" or "act-rollback-without-transaction"
                or "act-rollback-wrong-id" or "act-rollback-without-id-guard" or "act-rollback-early-number-scope"
                => "backend/Services/SpreadsheetImportService.Rollback.cs",
            "document-bank-without-nowait" or "document-bank-wrong-provider" or "document-bank-weak-source-lock"
                or "document-bank-foreign-scope" or "document-bank-unbounded" or "document-bank-without-transaction"
                => "backend/Services/Companies/CompanyBankDocumentSelectionService.cs",
            "balance-without-nowait" or "balance-wrong-provider" => "backend/Services/Payments/BalanceApplicationLease.cs",
            "balance-source-without-nowait" => "backend/Services/Payments/BalanceSourceMutationRules.cs",
            "allocation-without-nowait" or "allocation-unbounded" => "backend/Services/MoneyOperationInvoiceAllocationService.cs",
            "overdue-without-nowait" => "backend/Services/Notifications/NotificationService.PaymentOverdue.cs",
            "advance-cursor-without-nowait" => "backend/Services/PaymentAdvanceReviewMonitoringService.cs",
            "invoice-cancel-without-nowait" => "backend/Controllers/InvoicesController.cs",
            "payment-input-without-nowait" or "payment-input-wrong-provider" or "payment-input-without-transaction"
                => "backend/Services/Payments/PaymentCreateInputRules.cs",
            "binding-share-without-nowait" or "binding-revoke-without-nowait" or "binding-wrong-provider" or "binding-stale-session" or "binding-foreign-money-lock"
                => "backend/Services/Companies/CompanyBankAccountBindingService.cs",
            "binding-guard-without-nowait" or "binding-guard-wrong-provider" or "binding-guard-unbounded" or "binding-guard-unparameterized"
                => "backend/Services/Companies/CompanyBankAccountBindingProfileGuard.cs",
            "unregistered-owner" or "unregistered-share-owner" or "unregistered-key-share-owner"
                or "unregistered-multiline-share-owner" => "backend/Controllers/UnexpectedLocksController.cs",
            "bulk-unparameterized" or "contract-root-unparameterized" or "policy-key-unparameterized"
                => "backend/Controllers/TasksController.cs",
            "seller-share-without-nowait" or "seller-unbounded-addresses" or "seller-unsupported-provider" or "profile-lock-without-nowait" or "profile-lock-wrong-provider" or "act-invoice-lock-without-nowait"
                => "backend/Services/Companies/DocumentSellerLease.cs",
            "print-defaults-lock-without-nowait" or "print-defaults-lock-wrong-provider" => "backend/Services/Companies/CompanyDocumentImagePrintDefaultsService.cs",
            "payment-attempt-without-nowait" => "backend/Services/Payments/PaymentCreateAttemptLease.cs",
            "request-lock-without-nowait" or "request-access-without-nowait" => "backend/Services/Requests/SalesRequestService.cs",
            "note-lease-without-nowait" or "note-lease-wrong-provider" => "backend/Services/Notes/StickyNoteAccessLease.cs",
            "deadline-lease-without-nowait" or "deadline-lease-wrong-provider" => "backend/Services/Tasks/TaskDeadlineRequestLease.cs",
            "upload-share-without-nowait" or "upload-wrong-provider" => "backend/Services/Storage/ManagedTaskFileUploadService.cs",
            _ => "backend/Services/Invoices/FinancialSourceLease.cs",
        };
        var original = relativePath == "backend/Controllers/UnexpectedLocksController.cs"
            ? "" : File.ReadAllText(Path.Combine(root, relativePath));
        var changed = mutation switch
        {
            "act-delete-without-nowait" or "act-rollback-without-nowait"
                => original.Replace("FOR UPDATE NOWAIT", "FOR UPDATE", StringComparison.Ordinal),
            "act-delete-wrong-provider" => original.Replace("if (_db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "act-rollback-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "act-delete-without-transaction" => original.Replace("DocumentSellerLease.RunAsync<IActionResult>", "WithoutTransactionAsync<IActionResult>", StringComparison.Ordinal),
            "act-rollback-without-transaction" => original.Replace("transaction = db.Database.CurrentTransaction is null", "transaction = false", StringComparison.Ordinal),
            "act-delete-wrong-id" => original.Replace("{id} FOR UPDATE NOWAIT", "{0} FOR UPDATE NOWAIT", StringComparison.Ordinal),
            "act-rollback-wrong-id" => original.Replace("{operation.EntityId.Value} FOR UPDATE NOWAIT", "{0} FOR UPDATE NOWAIT", StringComparison.Ordinal),
            "act-rollback-without-id-guard" => original.Replace("if (operation.EntityId.HasValue)", "if (true)", StringComparison.Ordinal),
            "act-delete-early-number-scope" => original.Replace("if (_db.Database.IsNpgsql())",
                "await new ActAnnualNumberingService(_db).LockAsync(ct);\n            if (_db.Database.IsNpgsql())", StringComparison.Ordinal),
            "act-rollback-early-number-scope" => original.Replace("if (db.Database.IsNpgsql())",
                "await numbering.LockAsync(cancellationToken);\n                if (db.Database.IsNpgsql())", StringComparison.Ordinal),
            "document-bank-without-nowait" => original.Replace("NOWAIT", "", StringComparison.Ordinal),
            "document-bank-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "document-bank-weak-source-lock" => original.Replace("FOR UPDATE NOWAIT", "FOR SHARE NOWAIT", StringComparison.Ordinal),
            "document-bank-foreign-scope" => original.Replace("{company.Id} FOR SHARE NOWAIT", "{0} FOR SHARE NOWAIT", StringComparison.Ordinal),
            "document-bank-unbounded" => original.Replace(".Take(CompanyBankAccountMatchService.MaximumProfileAccounts + 1)", ".Take(int.MaxValue)", StringComparison.Ordinal),
            "document-bank-without-transaction" => original.Replace("if (db.Database.CurrentTransaction is null)", "if (false)", StringComparison.Ordinal),
            "balance-without-nowait" or "balance-source-without-nowait" or "allocation-without-nowait" or "overdue-without-nowait" or "advance-cursor-without-nowait" or "invoice-cancel-without-nowait"
                => original.Replace("FOR UPDATE NOWAIT", "FOR UPDATE", StringComparison.Ordinal),
            "balance-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "allocation-unbounded" => original.Replace(".Take(MaximumRelatedPayments + 1)", ".Take(int.MaxValue)", StringComparison.Ordinal),
            "payment-input-without-nowait" => original.Replace("FOR SHARE NOWAIT", "FOR SHARE", StringComparison.Ordinal),
            "payment-input-wrong-provider" => original.Replace("if (!db.Database.IsNpgsql()) return;", "if (false) return;", StringComparison.Ordinal),
            "payment-input-without-transaction" => original.Replace("if (db.Database.CurrentTransaction is null)", "if (false)", StringComparison.Ordinal),
            "binding-share-without-nowait" => original.Replace("FOR SHARE NOWAIT", "FOR SHARE", StringComparison.Ordinal),
            "binding-revoke-without-nowait" or "binding-guard-without-nowait" => original.Replace("FOR UPDATE NOWAIT", "FOR UPDATE", StringComparison.Ordinal),
            "binding-wrong-provider" or "binding-guard-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "binding-stale-session" => original.Replace("actor.AuthTokenVersion != tokenVersion", "false", StringComparison.Ordinal),
            "binding-foreign-money-lock" => original.Replace("if (lockedBank.Length == 0) return;", "if (false) return;", StringComparison.Ordinal),
            "binding-guard-unbounded" => original.Replace("changes.Chunk(16)", "changes.Chunk(int.MaxValue)", StringComparison.Ordinal),
            "binding-guard-unparameterized" => original.Replace("ANY({ids})", "ANY(ARRAY[1,2])", StringComparison.Ordinal),
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
            "payment-attempt-without-nowait" or "request-lock-without-nowait" => original.Replace("FOR UPDATE NOWAIT", "FOR UPDATE", StringComparison.Ordinal),
            "note-lease-without-nowait" or "deadline-lease-without-nowait" => original.Replace("FOR SHARE NOWAIT", "FOR SHARE", StringComparison.Ordinal),
            "note-lease-wrong-provider" or "deadline-lease-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "upload-share-without-nowait" => original.Replace("FOR SHARE NOWAIT", "FOR SHARE", StringComparison.Ordinal),
            "upload-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "request-access-without-nowait" => original.Replace("FOR SHARE NOWAIT", "FOR SHARE", StringComparison.Ordinal),
            "seller-unbounded-addresses" => original.Replace("LIMIT 5001 FOR SHARE", "FOR SHARE", StringComparison.Ordinal),
            "seller-unsupported-provider" => original.Replace("if (!db.Database.IsNpgsql() && !db.Database.IsSqlite())", "if (false)", StringComparison.Ordinal),
            "profile-lock-without-nowait" => original.Replace("{companyId} FOR UPDATE NOWAIT", "{companyId} FOR UPDATE", StringComparison.Ordinal),
            "profile-lock-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "print-defaults-lock-without-nowait" => original.Replace("FOR UPDATE NOWAIT", "FOR UPDATE", StringComparison.Ordinal),
            "print-defaults-lock-wrong-provider" => original.Replace("if (db.Database.IsNpgsql())", "if (true)", StringComparison.Ordinal),
            "act-invoice-lock-without-nowait" => original.Replace("{id} FOR SHARE NOWAIT", "{id} FOR SHARE", StringComparison.Ordinal),
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

        var linkStart = taskSource.IndexOf("private async Task<ActionResult<TaskJournalItemDto[]>> LinkTaskBillingInvoiceAsync(", StringComparison.Ordinal);
        var linkEnd = taskSource.IndexOf("private IQueryable<Payment> ReconciliationInvoiceQuery", linkStart, StringComparison.Ordinal);
        Assert.True(linkStart >= 0 && linkEnd > linkStart);
        var linkMethod = taskSource[linkStart..linkEnd];
        AssertOrdered(linkMethod, "BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)", "if (_db.Database.IsNpgsql())",
            "{invoiceId} FOR UPDATE", "ANY({ids}) ORDER BY \\\"Id\\\" FOR UPDATE", "var entities = await BuildVisibleTasksQuery(access)");
        Assert.Equal(2, linkMethod.Split("FOR UPDATE", StringSplitOptions.None).Length - 1);
        Approve("backend/Controllers/TasksController.cs", taskSource, "LinkTaskBillingInvoiceAsync", linkStart, linkEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT 1 FROM \"Payments\" WHERE \"Id\" = {invoiceId} FOR UPDATE")""",
             """await _db.Database.SqlQuery<int>($"SELECT 1 FROM \"TaskJournalItems\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE")"""]);

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
            "if (db.Database.IsNpgsql())", "BeginTransactionAsync(isolation, ct)",
            "connection.DefaultTimeout = 1", "connection.BeginTransaction(IsolationLevel.Serializable, deferred: false)",
            "db.Database.UseTransactionAsync(native, ct)", "finally { connection.DefaultTimeout = timeout; }");
        var sellerRun = Method(sellerSource, "public static async Task<T> RunAsync<T>", "internal static async Task<DocumentSellerLease> BeginForCreationAsync");
        AssertOrdered(sellerRun, "static _ => true, IsolationLevel.ReadCommitted", "if (db.Database.CurrentTransaction is not null) throw new SellerSnapshotConflictException()",
            "shouldCommit, IsolationLevel.Serializable", "lease = await BeginAsync(db, ct, isolation)", "var result = await action(lease)",
            "if (shouldCommit(result)) await lease.CommitAsync(ct)", "finally { if (lease is not null) await lease.DisposeAsync(); }");
        var profileStart = sellerSource.IndexOf("internal async Task LockCompanyRowAsync", StringComparison.Ordinal);
        var profileEnd = sellerSource.IndexOf("public async Task<IDocumentSeller[]> LockDocumentAsync", profileStart, StringComparison.Ordinal);
        Assert.True(profileStart >= 0 && profileEnd > profileStart);
        AssertOrdered(sellerSource[profileStart..profileEnd], "if (db.Database.IsNpgsql())", "{companyId} FOR UPDATE NOWAIT");
        Approve("backend/Services/Companies/DocumentSellerLease.cs", sellerSource, "LockCompanyRowAsync", profileStart, profileEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        Assert.Contains("private const int MaximumPayments = 5000;", sellerSource, StringComparison.Ordinal);

        var printDefaultsSource = ReadSource("backend/Services/Companies/CompanyDocumentImagePrintDefaultsService.cs");
        var printDefaultsStart = printDefaultsSource.IndexOf("public Task<T> EditAsync<T>", StringComparison.Ordinal);
        var printDefaultsEnd = printDefaultsSource.IndexOf("public async Task", printDefaultsStart, StringComparison.Ordinal);
        Assert.True(printDefaultsStart >= 0 && printDefaultsEnd > printDefaultsStart);
        AssertOrdered(printDefaultsSource[printDefaultsStart..printDefaultsEnd], "DocumentSellerLease.RunAsync", "lease.AdmitCleanTracker()",
            "if (db.Database.IsNpgsql())", "{companyId} FOR UPDATE NOWAIT", "if (ids.Length != 1) throw new SellerSnapshotConflictException()",
            "ReadAsync(companyId", "expectedVersion", "return await edit()");
        Approve("backend/Services/Companies/CompanyDocumentImagePrintDefaultsService.cs", printDefaultsSource, "EditAsync", printDefaultsStart, printDefaultsEnd,
            ["""var ids = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);

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
        var actInvoiceStart = sellerSource.IndexOf("public async Task<PaymentType?> ReadLockedActInvoiceTypeAsync", StringComparison.Ordinal);
        Assert.True(actInvoiceStart >= 0 && companyStart > actInvoiceStart);
        AssertOrdered(sellerSource[actInvoiceStart..companyStart], "if (!act.InvoiceId.HasValue)", "if (db.Database.IsNpgsql())",
            "{id} FOR SHARE NOWAIT", "db.Payments.AsNoTracking()", "type != act.Invoice.Type", "throw new SellerSnapshotConflictException()");
        Approve("backend/Services/Companies/DocumentSellerLease.cs", sellerSource, "ReadLockedActInvoiceTypeAsync", actInvoiceStart, companyStart,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = {id} FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");
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
        var registryEnd = sellerSource.IndexOf("internal static bool IsConflict", registryStart, StringComparison.Ordinal);
        Assert.True(registryStart >= 0 && registryEnd > registryStart);
        var registryMethod = sellerSource[registryStart..registryEnd];
        AssertOrdered(registryMethod, "if (!db.Database.IsNpgsql()) return;",
            "var keys = new[] { \"legalEntities.mainCompanyId\", \"legalEntities.mainCompanyIds\", \"legalEntities.defaultMainCompanyId\" };",
            "ANY({keys}) ORDER BY");
        Approve("backend/Services/Companies/DocumentSellerLease.cs", sellerSource, "LockRegistryAsync", registryStart, registryEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = ANY({keys}) ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");

        var requestSource = ReadSource("backend/Services/Requests/SalesRequestService.cs");
        var bindStart = requestSource.IndexOf("public async Task<SalesRequestResult<SalesRequest>> SetServiceInvoiceAsync", StringComparison.Ordinal);
        var bindEnd = requestSource.IndexOf("public async Task<SalesRequestResult<SalesRequest>> TransitionAsync", bindStart, StringComparison.Ordinal);
        Assert.True(bindStart >= 0 && bindEnd > bindStart);
        var bindMethod = requestSource[bindStart..bindEnd];
        AssertOrdered(bindMethod, ".ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), ct)",
            "await LockRequestAccessAsync(request, userId, ct)", "await LockAccountAccessAsync(userId, ct)",
            "Distinct().OrderBy(value => value).ToArray()", "if (!await CanUseInvoiceAsync(access, candidateId))", "FROM \\\"Invoices\\\"", "Take(5001).CountAsync(ct)",
            "FROM \\\"Payments\\\"", "LockInvoiceVisibilityAsync", "await CanUseInvoiceAsync(access, candidateId)", "IsInvoiceAllocationValidAsync", "request.ServiceInvoiceId = invoiceId", "request.ServiceInvoiceAllocationAmount = allocationAmount");
        Approve("backend/Services/Requests/SalesRequestService.cs", requestSource, "SetServiceInvoiceAsync", bindStart, bindEnd,
            ["""await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Invoices\" WHERE \"Id\" = {candidateId} FOR UPDATE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"InvoiceId\" = {candidateId} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);"""]);
        var transitionStart = requestSource.IndexOf("public async Task<SalesRequestResult<SalesRequest>> TransitionAsync", StringComparison.Ordinal);
        var transitionEnd = requestSource.IndexOf("public async Task<SalesRequestResult<TaskJournalItem>> CreateManualTaskAsync", transitionStart, StringComparison.Ordinal);
        Assert.True(transitionStart >= 0 && transitionEnd > transitionStart);
        var transitionMethod = requestSource[transitionStart..transitionEnd];
        AssertOrdered(transitionMethod, ".ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Version, item => item.Version), ct)",
            "target.FinalKind == SalesRequestFinalKinds.Success && _db.Database.IsNpgsql()", "ORDER BY \\\"Id\\\" FOR UPDATE NOWAIT",
            "PostgresErrorCodes.LockNotAvailable", "_db.TaskJournalItems.AsNoTracking()", "openTasks.Count > 0");
        Approve("backend/Services/Requests/SalesRequestService.cs", requestSource, "TransitionAsync", transitionStart, transitionEnd,
            ["""await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"SalesRequestId\" = {id} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);"""]);
        AssertOrdered(transitionMethod, "await LockRequestAccessAsync(request, userId, ct)", "PostgresErrorCodes.LockNotAvailable", "await CanEditRequestAsync(request.Id, userId, ct)");
        var accessStart = requestSource.IndexOf("private async Task LockRequestAccessAsync", StringComparison.Ordinal);
        var accessEnd = requestSource.IndexOf("private async Task LockAccountAccessAsync", accessStart, StringComparison.Ordinal);
        Assert.True(accessStart >= 0 && accessEnd > accessStart);
        AssertOrdered(requestSource[accessStart..accessEnd], "new[] { userId, request.ResponsibleUserId, request.CreatedByUserId }.Distinct().OrderBy(id => id).ToArray()",
            "FROM \\\"Users\\\"", "FROM \\\"Roles\\\"", "FROM \\\"RolePermissions\\\"");
        Approve("backend/Services/Requests/SalesRequestService.cs", requestSource, "LockRequestAccessAsync", accessStart, accessEnd,
            ["""await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY({userIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) AND \"Section\" = {RolePermissionCatalog.Requests} FOR SHARE NOWAIT", ct);"""], "FOR SHARE");
        var accountEnd = requestSource.IndexOf("private async Task<bool> IsServiceInvoiceFullyPaidAsync", accessEnd, StringComparison.Ordinal);
        Assert.True(accountEnd > accessEnd);
        Approve("backend/Services/Requests/SalesRequestService.cs", requestSource, "LockAccountAccessAsync", accessEnd, accountEnd,
            ["""await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) AND \"Section\" = {RolePermissionCatalog.Accounts} FOR SHARE NOWAIT", ct);"""], "FOR SHARE");
        var invoiceCheckEnd = requestSource.IndexOf("private async Task<bool> LockInvoiceVisibilityAsync", accountEnd, StringComparison.Ordinal);
        var visibilityEnd = requestSource.IndexOf("private async Task<bool> CanEditRequestAsync", invoiceCheckEnd, StringComparison.Ordinal);
        Assert.True(invoiceCheckEnd > accountEnd);
        Assert.True(visibilityEnd > invoiceCheckEnd);
        AssertOrdered(requestSource[accountEnd..invoiceCheckEnd], "if (_db.Database.IsNpgsql())", "await LockAccountAccessAsync(userId, ct)",
            "LOCK TABLE", "FROM \\\"Invoices\\\"", "Take(SalesRequestInvoiceSettlement.MaximumRows + 1).CountAsync(ct)", "FROM \\\"Payments\\\"", "FROM \\\"MoneyOperations\\\"",
            "BankStatementImportAccess.ResolveAsync", "SalesRequestServiceInvoicePayment.IsFullyPaid");
        Approve("backend/Services/Requests/SalesRequestService.cs", requestSource, "IsServiceInvoiceFullyPaidAsync", accountEnd, invoiceCheckEnd,
            ["""await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Invoices\" WHERE \"Id\" = {invoiceId} FOR UPDATE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"InvoiceId\" = {invoiceId} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"MoneyOperations\" WHERE \"LinkedInvoiceId\" = {invoiceId} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);"""]);
        Approve("backend/Services/Requests/SalesRequestService.cs", requestSource, "IsServiceInvoiceFullyPaidAsync", accountEnd, invoiceCheckEnd,
            ["""await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) AND \"Section\" = {RolePermissionCatalog.Payments} FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserCompanyAccesses\" WHERE \"UserId\" = {userId} ORDER BY \"LegalEntityId\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"MoneyAccounts\" WHERE \"Id\" = ANY({accountIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"BankConnectionAccounts\" WHERE \"Id\" = ANY({bankIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"BankConnections\" WHERE \"Id\" IN (SELECT \"BankConnectionId\" FROM \"BankConnectionAccounts\" WHERE \"Id\" = ANY({bankIds})) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);"""], "FOR SHARE");
        Approve("backend/Services/Requests/SalesRequestService.cs", requestSource, "LockInvoiceVisibilityAsync", invoiceCheckEnd, visibilityEnd,
            [
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"PaymentId\" FROM \"PaymentResponsibles\" WHERE \"PaymentId\" = ANY({paymentIds}) ORDER BY \"PaymentId\", \"UserId\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"PaymentId\" FROM \"InvoiceClientCases\" WHERE \"PaymentId\" = ANY({paymentIds}) ORDER BY \"PaymentId\", \"ClientCaseId\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = ANY({caseIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Contracts\" WHERE \"Id\" = ANY({contractIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"ContractId\" FROM \"ClientContracts\" WHERE \"ContractId\" = ANY({contractIds}) ORDER BY \"ContractId\", \"ClientId\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"ContractId\" FROM \"LegalEntityContracts\" WHERE \"ContractId\" = ANY({contractIds}) ORDER BY \"ContractId\", \"LegalEntityId\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = ANY({clientIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = ANY({companyIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY({userIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);"""], "FOR SHARE");

        var attemptSource = ReadSource("backend/Services/Payments/PaymentCreateAttemptLease.cs");
        var actorStart = attemptSource.IndexOf("public async Task LockActorPermissionsAsync", StringComparison.Ordinal);
        var attemptStart = attemptSource.IndexOf("public async Task<PaymentCreateAttempt?> LockAttemptAsync", StringComparison.Ordinal);
        Assert.True(actorStart >= 0 && attemptStart > actorStart);
        var attemptEnd = attemptSource.IndexOf("public Task LockSellerSelectionAsync", attemptStart, StringComparison.Ordinal);
        Assert.True(attemptEnd > attemptStart);
        var sellerStart = attemptEnd;
        var sellerEnd = attemptSource.IndexOf("public async Task LockPrimaryAsync", sellerStart, StringComparison.Ordinal);
        var primaryStart = sellerEnd;
        var primaryEnd = attemptSource.IndexOf("public async Task CommitAsync", primaryStart, StringComparison.Ordinal);
        Assert.True(attemptStart >= 0 && attemptEnd > attemptStart && sellerEnd > sellerStart && primaryEnd > primaryStart);
        AssertOrdered(attemptSource[actorStart..attemptStart], "if (!db.Database.IsNpgsql()) return;", "SET LOCAL lock_timeout",
            "FROM \\\"Users\\\"", "FROM \\\"Roles\\\"", "FROM \\\"RolePermissions\\\"", "FROM \\\"UserCompanyAccesses\\\"");
        Approve("backend/Services/Payments/PaymentCreateAttemptLease.cs", attemptSource, "LockActorPermissionsAsync", actorStart, attemptStart,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actorId} FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId}) FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserCompanyAccesses\" WHERE \"UserId\" = {actorId} ORDER BY \"LegalEntityId\" FOR SHARE NOWAIT", ct);"""], "FOR SHARE");
        AssertOrdered(attemptSource[attemptStart..attemptEnd], "if (db.Database.IsNpgsql())", "{actorId} FOR UPDATE NOWAIT", "db.PaymentCreateAttempts.SingleOrDefaultAsync");
        Approve("backend/Services/Payments/PaymentCreateAttemptLease.cs", attemptSource, "LockAttemptAsync", attemptStart, attemptEnd,
            ["""await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"PaymentCreateAttempts\" WHERE \"Id\" = {id} AND \"ActorUserId\" = {actorId} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        Assert.Contains("=> PaymentCreateInputRules.LockSellerSelectionAsync(db, companyId, branchId, ct);", attemptSource[sellerStart..sellerEnd], StringComparison.Ordinal);
        var inputSource = ReadSource("backend/Services/Payments/PaymentCreateInputRules.cs");
        var inputStart = inputSource.IndexOf("internal static async Task LockSellerSelectionAsync", StringComparison.Ordinal);
        Assert.True(inputStart >= 0);
        var inputEnd = inputSource.IndexOf("public static string? Validate", inputStart, StringComparison.Ordinal);
        Assert.True(inputEnd > inputStart);
        AssertOrdered(inputSource[inputStart..inputEnd], "if (db.Database.CurrentTransaction is null)", "if (!db.Database.IsNpgsql()) return;", "ANY({keys}) ORDER BY", "if (companyId.HasValue)", "if (branchId.HasValue)");
        Approve("backend/Services/Payments/PaymentCreateInputRules.cs", inputSource, "LockSellerSelectionAsync", inputStart, inputEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId.Value} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        Approve("backend/Services/Payments/PaymentCreateInputRules.cs", inputSource, "LockSellerSelectionAsync", inputStart, inputEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = ANY({keys}) ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"CompanyBranches\" WHERE \"Id\" = {branchId.Value} FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");
        AssertOrdered(attemptSource[primaryStart..primaryEnd], "if (db.Database.IsNpgsql())", "{id} FOR UPDATE NOWAIT");
        Approve("backend/Services/Payments/PaymentCreateAttemptLease.cs", attemptSource, "LockPrimaryAsync", primaryStart, primaryEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);

        var noteSource = ReadSource("backend/Services/Notes/StickyNoteAccessLease.cs");
        var noteStart = noteSource.IndexOf("public static async Task<StickyNoteAccessLease?> TryAcquireAsync", StringComparison.Ordinal);
        var noteEnd = noteSource.IndexOf("private static bool IsLockConflict", noteStart, StringComparison.Ordinal);
        Assert.True(noteStart >= 0 && noteEnd > noteStart);
        AssertOrdered(noteSource[noteStart..noteEnd],
            "db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()",
            "if (db.Database.IsNpgsql())", "IsolationLevel.ReadCommitted",
            "else if (db.Database.IsSqlite())", "IsolationLevel.Serializable, deferred: false",
            "else throw new InvalidOperationException(\"Unsupported provider",
            "if (!await db.Users.AsNoTracking().AnyAsync", "await lease!.DisposeAsync()",
            "catch (Exception error) when (IsLockConflict(error))");
        Approve("backend/Services/Notes/StickyNoteAccessLease.cs", noteSource, "TryAcquireAsync", noteStart, noteEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR UPDATE NOWAIT", ct);"""]);
        Approve("backend/Services/Notes/StickyNoteAccessLease.cs", noteSource, "TryAcquireAsync", noteStart, noteEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"StickyNoteBoards\" WHERE \"Id\" = (SELECT \"BoardId\" FROM \"StickyNotes\" WHERE \"Id\" = {noteId}) FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"StickyNoteBoardMembers\" WHERE \"BoardId\" = (SELECT \"BoardId\" FROM \"StickyNotes\" WHERE \"Id\" = {noteId}) AND \"UserId\" = {userId} FOR SHARE NOWAIT", ct);"""], "FOR SHARE");

        var deadlineSource = ReadSource("backend/Services/Tasks/TaskDeadlineRequestLease.cs");
        var deadlineStart = deadlineSource.IndexOf("public static async Task<TaskDeadlineRequestLease?> TryAcquireAsync", StringComparison.Ordinal);
        var deadlineEnd = deadlineSource.IndexOf("private static bool IsLockConflict", deadlineStart, StringComparison.Ordinal);
        Assert.True(deadlineStart >= 0 && deadlineEnd > deadlineStart);
        AssertOrdered(deadlineSource[deadlineStart..deadlineEnd],
            "db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()",
            "if (db.Database.IsNpgsql())", "IsolationLevel.ReadCommitted",
            "else if (db.Database.IsSqlite())", "IsolationLevel.Serializable, deferred: false",
            "else throw new InvalidOperationException(\"Unsupported provider",
            "if (!await db.Users.AsNoTracking().AnyAsync", "await lease!.DisposeAsync()",
            "catch (Exception error) when (IsLockConflict(error))");
        Approve("backend/Services/Tasks/TaskDeadlineRequestLease.cs", deadlineSource, "TryAcquireAsync", deadlineStart, deadlineEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"Id\" = {taskId} FOR UPDATE NOWAIT", ct);"""]);
        Approve("backend/Services/Tasks/TaskDeadlineRequestLease.cs", deadlineSource, "TryAcquireAsync", deadlineStart, deadlineEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalParticipants\" WHERE \"TaskJournalItemId\" = {taskId} ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" IN (SELECT \"ResponsibleUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} UNION SELECT \"CreatedByUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);"""], "FOR SHARE");

        var uploadSource = ReadSource("backend/Services/Storage/ManagedTaskFileUploadService.cs");
        var uploadStart = uploadSource.IndexOf("private async Task<ManagedTaskFileUploadResult> UploadCoreAsync", StringComparison.Ordinal);
        var uploadEnd = uploadSource.IndexOf("private async Task<ProductStorageBindingResult> PublishObjectAsync", uploadStart, StringComparison.Ordinal);
        Assert.True(uploadStart >= 0 && uploadEnd > uploadStart);
        AssertOrdered(uploadSource[uploadStart..uploadEnd],
            "await db.Database.BeginTransactionAsync(ct)", "if (db.Database.IsNpgsql())",
            "FROM \\\"Users\\\"", "FROM \\\"Roles\\\"", "FROM \\\"RolePermissions\\\"",
            "PostgresErrorCodes.LockNotAvailable", "var locked = await db.TaskJournalItems",
            "if (db.Database.IsNpgsql())", "FROM \\\"TaskJournalParticipants\\\"",
            "if (salesRequestId.HasValue)", "FROM \\\"SalesRequests\\\"",
            "request.AuthorizePublication is not null && !await request.AuthorizePublication(ct)");
        Approve("backend/Services/Storage/ManagedTaskFileUploadService.cs", uploadSource, "UploadCoreAsync", uploadStart, uploadEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {intent.ActorId} FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {intent.ActorId}) FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {intent.ActorId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalParticipants\" WHERE \"TaskJournalItemId\" = {intent.TaskId} ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" IN (SELECT \"ResponsibleUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} UNION SELECT \"CreatedByUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);"""], "FOR SHARE");

        var bindingSource = ReadSource("backend/Services/Companies/CompanyBankAccountBindingService.cs");
        var revokeStart = bindingSource.IndexOf("public Task<CompanyBankBindingReceipt> RevokeAsync", StringComparison.Ordinal);
        var revokeEnd = bindingSource.IndexOf("private sealed record Actor", revokeStart, StringComparison.Ordinal);
        Assert.True(revokeStart >= 0 && revokeEnd > revokeStart);
        AssertOrdered(bindingSource[revokeStart..revokeEnd], "if (db.Database.IsNpgsql())", "FOR UPDATE NOWAIT", "var binding = await db.CompanyBankAccountBindings");
        Approve("backend/Services/Companies/CompanyBankAccountBindingService.cs", bindingSource, "RevokeAsync", revokeStart, revokeEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"CompanyBankAccountBindings\" WHERE \"Id\" = {bindingId} AND \"LegalEntityId\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        var bindingRunStart = bindingSource.IndexOf("private async Task<CompanyBankBindingReceipt> RunAsync", StringComparison.Ordinal);
        var bindingRunEnd = bindingSource.IndexOf("private async Task LockBankSourceAsync", bindingRunStart, StringComparison.Ordinal);
        Assert.True(bindingRunStart >= 0 && bindingRunEnd > bindingRunStart);
        AssertOrdered(bindingSource[bindingRunStart..bindingRunEnd], "DocumentSellerLease.RunSerializableAsync", "lease.AdmitCleanTracker()",
            "if (db.Database.IsNpgsql())", "FROM \\\"Users\\\"", "FROM \\\"Roles\\\"", "IsApprovedUserAdminAsync", "AuthSessionClaims.TryGetTokenVersion",
            "actor.AuthTokenVersion != tokenVersion", "lease.LockRegistryAsync", "lease.LockCompanyRowAsync", "if (db.Database.IsNpgsql())", "FROM \\\"BankConnections\\\"");
        Approve("backend/Services/Companies/CompanyBankAccountBindingService.cs", bindingSource, "RunAsync", bindingRunStart, bindingRunEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Users\" WHERE \"Id\" = {id} FOR SHARE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {id}) FOR SHARE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnections\" WHERE \"Id\" = {connectionId} FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");
        var sourceEnd = bindingSource.IndexOf("private async Task TouchCompanyAsync", bindingRunEnd, StringComparison.Ordinal);
        Assert.True(sourceEnd > bindingRunEnd);
        AssertOrdered(bindingSource[bindingRunEnd..sourceEnd], "if (!db.Database.IsNpgsql()) return;", "FROM \\\"BankConnectionAccounts\\\"", "if (lockedBank.Length == 0) return;", "FROM \\\"MoneyAccounts\\\"");
        Approve("backend/Services/Companies/CompanyBankAccountBindingService.cs", bindingSource, "LockBankSourceAsync", bindingRunEnd, sourceEnd,
            ["""var lockedBank = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnectionAccounts\" WHERE \"Id\" = {bankId} AND \"BankConnectionId\" = {connectionId} FOR SHARE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"MoneyAccounts\" WHERE \"BankConnectionAccountId\" = {bankId} ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");

        var bindingGuardSource = ReadSource("backend/Services/Companies/CompanyBankAccountBindingProfileGuard.cs");
        var trackedStart = bindingGuardSource.IndexOf("public static async Task ValidateTrackedAsync", StringComparison.Ordinal);
        var singleStart = bindingGuardSource.IndexOf("public static async Task ValidateAsync", trackedStart, StringComparison.Ordinal);
        var bindingsReadStart = bindingGuardSource.IndexOf("private static async Task<BindingIdentity[]> ReadBindingsAsync", singleStart, StringComparison.Ordinal);
        Assert.True(trackedStart >= 0 && singleStart > trackedStart && bindingsReadStart > singleStart);
        AssertOrdered(bindingGuardSource[trackedStart..singleStart], "EntityState.Modified && x.Entity.Id > 0", "if (db.Database.CurrentTransaction is null)",
            "changes.Chunk(16)", "if (db.Database.IsNpgsql())", "ANY({ids}) ORDER BY", "ReadBindingsAsync", "ValidateCandidate");
        Approve("backend/Services/Companies/CompanyBankAccountBindingProfileGuard.cs", bindingGuardSource, "ValidateTrackedAsync", trackedStart, singleStart,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        AssertOrdered(bindingGuardSource[singleStart..bindingsReadStart], "if (db.Database.CurrentTransaction is null)", "if (db.Database.IsNpgsql())",
            "{companyId} FOR UPDATE NOWAIT", "ReadBindingsAsync", "ValidateCandidate");
        Approve("backend/Services/Companies/CompanyBankAccountBindingProfileGuard.cs", bindingGuardSource, "ValidateAsync", singleStart, bindingsReadStart,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);

        var cancellationStart = invoiceSource.IndexOf("private Task LockInvoiceCancellationAggregateAsync", StringComparison.Ordinal);
        Assert.True(cancellationStart >= 0);
        var cancellationEnd = invoiceSource.IndexOf("private async Task<bool> HasInvoiceCancellationCompensationAsync", cancellationStart, StringComparison.Ordinal);
        Assert.True(cancellationEnd > cancellationStart);
        AssertOrdered(invoiceSource[cancellationStart..cancellationEnd], "_db.Database.IsNpgsql()", "{invoiceId} ORDER BY", ": Task.CompletedTask");
        Approve("backend/Controllers/InvoicesController.cs", invoiceSource, "LockInvoiceCancellationAggregateAsync", cancellationStart, cancellationEnd,
            ["""? _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"InvoiceId\" = {invoiceId} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct)"""]);

        var allocationSource = ReadSource("backend/Services/MoneyOperationInvoiceAllocationService.cs");
        var allocationStart = allocationSource.IndexOf("public static async Task ApplyAsync", StringComparison.Ordinal);
        Assert.True(allocationStart >= 0);
        var allocationEnd = allocationSource.IndexOf("private static async Task<List<Payment>> ReloadGraphAsync", allocationStart, StringComparison.Ordinal);
        Assert.True(allocationEnd > allocationStart);
        AssertOrdered(allocationSource[allocationStart..allocationEnd], "if (db.Database.CurrentTransaction is null)", ".Take(MaximumRelatedPayments + 1)",
            "ids.Length == 0 || ids.Length > MaximumRelatedPayments", "if (db.Database.IsNpgsql())", "ANY ({ids}) ORDER BY", "else if (db.Database.IsSqlite())", "else throw new NotSupportedException");
        Approve("backend/Services/MoneyOperationInvoiceAllocationService.cs", allocationSource, "ApplyAsync", allocationStart, allocationEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);"""]);

        var monitoringSource = ReadSource("backend/Services/PaymentAdvanceReviewMonitoringService.cs");
        var monitoringStart = monitoringSource.IndexOf("private async Task<PaymentAdvanceReviewMonitoringResult> RunOnceAsync", StringComparison.Ordinal);
        Assert.True(monitoringStart >= 0);
        var monitoringEnd = monitoringSource.IndexOf("private async Task<int> DispatchIsolatedAsync", monitoringStart, StringComparison.Ordinal);
        Assert.True(monitoringEnd > monitoringStart);
        AssertOrdered(monitoringSource[monitoringStart..monitoringEnd], "BalanceApplicationLease.BeginSystemAsync", "if (db.Database.IsNpgsql())", "{CursorSettingKey} FOR UPDATE NOWAIT", "lease.LockPaymentsAsync");
        Approve("backend/Services/PaymentAdvanceReviewMonitoringService.cs", monitoringSource, "RunOnceAsync", monitoringStart, monitoringEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SystemSettings\" WHERE \"Key\" = {CursorSettingKey} FOR UPDATE NOWAIT", cancellationToken);"""]);

        var overdueSource = ReadSource("backend/Services/Notifications/NotificationService.PaymentOverdue.cs");
        var overdueStart = overdueSource.IndexOf("public async Task NotifyOverdueForEventAsync", StringComparison.Ordinal);
        Assert.True(overdueStart >= 0);
        var overdueEnd = overdueSource.IndexOf("private async Task<bool> IsOverdueRecipientCurrentAsync", overdueStart, StringComparison.Ordinal);
        Assert.True(overdueEnd > overdueStart);
        AssertOrdered(overdueSource[overdueStart..overdueEnd], "BalanceApplicationLease.BeginAsync", "lease.LockPaymentAsync", "if (_context.Database.IsNpgsql())",
            "FROM \\\"PaymentOverdueNotificationOutboxes\\\"", "IsOverdueRecipientCurrentAsync", "lease.CommitAsync");
        Approve("backend/Services/Notifications/NotificationService.PaymentOverdue.cs", overdueSource, "NotifyOverdueForEventAsync", overdueStart, overdueEnd,
            ["""await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"PaymentOverdueNotificationOutboxes\" WHERE \"Id\" = {item.Id} FOR UPDATE NOWAIT", ct);"""]);

        var balanceSource = ReadSource("backend/Services/Payments/BalanceApplicationLease.cs");
        var balanceStart = balanceSource.IndexOf("private static async Task<BalanceApplicationLease> BeginCoreAsync", StringComparison.Ordinal);
        Assert.True(balanceStart >= 0);
        var actorPinStart = balanceSource.IndexOf("internal static async Task PinActorAsync", balanceStart, StringComparison.Ordinal);
        var ownedStart = balanceSource.IndexOf("public async Task LockOwnedPaymentAsync", balanceStart, StringComparison.Ordinal);
        var paymentStart = balanceSource.IndexOf("public async Task LockPaymentAsync", ownedStart, StringComparison.Ordinal);
        var paymentsStart = balanceSource.IndexOf("public async Task LockPaymentsAsync", paymentStart, StringComparison.Ordinal);
        var balanceEnd = balanceSource.IndexOf("public async Task CommitAsync", paymentsStart, StringComparison.Ordinal);
        Assert.True(actorPinStart > balanceStart && ownedStart > actorPinStart && paymentStart > ownedStart && paymentsStart > paymentStart && balanceEnd > paymentsStart);
        AssertOrdered(balanceSource[balanceStart..actorPinStart], "db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()", "if (db.Database.IsNpgsql())",
            "IsolationLevel.Serializable", "if (actorId.HasValue) await PinActorAsync(db, actorId.Value, ct);",
            "else if (db.Database.IsSqlite())", "deferred: false", "else throw new NotSupportedException");
        AssertOrdered(balanceSource[actorPinStart..ownedStart], "if (db.Database.CurrentTransaction is null)", "if (!db.Database.IsNpgsql()) return;",
            "FROM \\\"Users\\\"", "FROM \\\"Roles\\\"", "FROM \\\"RolePermissions\\\"", "FROM \\\"UserCompanyAccesses\\\"");
        Approve("backend/Services/Payments/BalanceApplicationLease.cs", balanceSource, "PinActorAsync", actorPinStart, ownedStart,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actorId} FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId}) FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserCompanyAccesses\" WHERE \"UserId\" = {actorId} ORDER BY \"LegalEntityId\" FOR SHARE NOWAIT", ct);"""], "FOR SHARE");
        AssertOrdered(balanceSource[ownedStart..paymentStart], "if (!db.Database.IsNpgsql()) return;", "if (clientId.HasValue)", "{clientId.Value} AND", "{legalEntityId!.Value} AND");
        Approve("backend/Services/Payments/BalanceApplicationLease.cs", balanceSource, "LockOwnedPaymentAsync", ownedStart, paymentStart,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {id} AND \"ClientId\" = {clientId.Value} AND \"LegalEntityId\" IS NULL FOR UPDATE NOWAIT", ct);""",
             """await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {id} AND \"LegalEntityId\" = {legalEntityId!.Value} AND \"ClientId\" IS NULL FOR UPDATE NOWAIT", ct);"""]);
        AssertOrdered(balanceSource[paymentStart..paymentsStart], "if (db.Database.IsNpgsql())", "{id} FOR UPDATE NOWAIT");
        Approve("backend/Services/Payments/BalanceApplicationLease.cs", balanceSource, "LockPaymentAsync", paymentStart, paymentsStart,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);"""]);
        AssertOrdered(balanceSource[paymentsStart..balanceEnd], "if (ids.Length > 0 && db.Database.IsNpgsql())", "ANY ({ids}) ORDER BY");
        Approve("backend/Services/Payments/BalanceApplicationLease.cs", balanceSource, "LockPaymentsAsync", paymentsStart, balanceEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);"""]);

        var sourceRules = ReadSource("backend/Services/Payments/BalanceSourceMutationRules.cs");
        var sourceRulesStart = sourceRules.IndexOf("public static async Task<ClientBalanceTransaction?> LoadAsync", StringComparison.Ordinal);
        Assert.True(sourceRulesStart >= 0);
        var sourceRulesEnd = sourceRules.IndexOf("public static async Task ValidateAsync", sourceRulesStart, StringComparison.Ordinal);
        Assert.True(sourceRulesEnd > sourceRulesStart);
        AssertOrdered(sourceRules[sourceRulesStart..sourceRulesEnd], "if (db.Database.CurrentTransaction is null", "IsolationLevel.Serializable", "if (db.Database.IsNpgsql())", "{paymentId} FOR UPDATE NOWAIT", "else if (!db.Database.IsSqlite()) throw new NotSupportedException");
        Approve("backend/Services/Payments/BalanceSourceMutationRules.cs", sourceRules, "LoadAsync", sourceRulesStart, sourceRulesEnd,
            ["""await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {paymentId} FOR UPDATE NOWAIT", ct);"""]);

        var documentBankSource = ReadSource("backend/Services/Companies/CompanyBankDocumentSelectionService.cs");
        var documentBankStart = documentBankSource.IndexOf("public async Task<CompanyBankDocumentSelection?> ValidateAsync", StringComparison.Ordinal);
        var documentBankEnd = documentBankSource.IndexOf("private static SellerSelectionException Unavailable", documentBankStart, StringComparison.Ordinal);
        Assert.True(documentBankStart >= 0 && documentBankEnd > documentBankStart);
        var documentBankMethod = documentBankSource[documentBankStart..documentBankEnd];
        AssertOrdered(documentBankMethod, "if (db.Database.CurrentTransaction is null)", ".Take(CompanyBankAccountMatchService.MaximumProfileAccounts + 1)",
            "x.BankConnection.LegalEntityId == company.Id", "if (source is null)", "if (db.Database.IsNpgsql())",
            "FROM \\\"BankConnections\\\"", "if (connections.Length != 1)", "FROM \\\"BankConnectionAccounts\\\"", "if (banks.Length != 1)", "FROM \\\"MoneyAccounts\\\"");
        Approve("backend/Services/Companies/CompanyBankDocumentSelectionService.cs", documentBankSource, "ValidateAsync", documentBankStart, documentBankEnd,
            ["""var banks = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnectionAccounts\" WHERE \"Id\" = {binding.BankConnectionAccountId} AND \"BankConnectionId\" = {source.BankConnectionId} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);
        Approve("backend/Services/Companies/CompanyBankDocumentSelectionService.cs", documentBankSource, "ValidateAsync", documentBankStart, documentBankEnd,
            ["""var connections = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnections\" WHERE \"Id\" = {source.BankConnectionId} AND \"LegalEntityId\" = {company.Id} FOR SHARE NOWAIT").ToArrayAsync(ct);""",
             """await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"MoneyAccounts\" WHERE \"BankConnectionAccountId\" = {binding.BankConnectionAccountId} AND \"CompanyId\" = {company.Id} ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);"""], "FOR SHARE");
        Assert.Equal(46, approved.Count(statement => statement.Contains("FOR UPDATE", StringComparison.Ordinal)));
        Assert.Equal(57, approved.Count(statement => statement.Contains("FOR SHARE", StringComparison.Ordinal)));
        Assert.Equal(103, approved.Count);
        ApproveSupplementarySqlLocks(root, ReadSource, approved);
        Assert.Equal(105, approved.Count(statement => statement.Contains("FOR UPDATE", StringComparison.Ordinal)));
        Assert.Equal(129, approved.Count(statement => statement.Contains("FOR SHARE", StringComparison.Ordinal)));
        Assert.Equal(234, approved.Count);

        var actDeleteSource = ReadSource("backend/Controllers/ActsController.cs");
        var actDeleteStart = actDeleteSource.IndexOf("public async Task<IActionResult> Delete(", StringComparison.Ordinal);
        var actDeleteEnd = actDeleteSource.IndexOf("public async Task<ActionResult<InvoiceDocumentTemplateSettingsDto>> GetDocumentTemplateSettings", actDeleteStart, StringComparison.Ordinal);
        Assert.True(actDeleteStart >= 0 && actDeleteEnd > actDeleteStart);
        var actDeleteMethod = actDeleteSource[actDeleteStart..actDeleteEnd];
        AssertOrdered(actDeleteMethod, "DocumentSellerLease.RunAsync<IActionResult>",
            "ApplyActVisibility(visibility).AnyAsync(item => item.Id == id", "if (_db.Database.IsNpgsql())",
            "{id} FOR UPDATE NOWAIT", "await new ActAnnualNumberingService(_db).LockAsync(ct);",
            "RequireActsPermissionAsync(permission => permission.CanDelete, ct) is { } changedPermission",
            "RaiseFloorAsync(act.Date, act.Number, ct)", "_db.Acts.Remove(act)");
        Assert.Equal(1, actDeleteMethod.Split("await new ActAnnualNumberingService(_db).LockAsync(ct);", StringSplitOptions.None).Length - 1);
        Approve("backend/Controllers/ActsController.cs", actDeleteSource, "Delete", actDeleteStart, actDeleteEnd,
            ["""await _db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT").ToArrayAsync(ct);"""]);

        var actRollbackSource = ReadSource("backend/Services/SpreadsheetImportService.Rollback.cs");
        var actRollbackStart = actRollbackSource.IndexOf("private async Task<bool> RollbackActAsync(", StringComparison.Ordinal);
        var actRollbackEnd = actRollbackSource.IndexOf("private async Task<bool> RollbackEntityAsync<", actRollbackStart, StringComparison.Ordinal);
        Assert.True(actRollbackStart >= 0 && actRollbackEnd > actRollbackStart);
        var actRollbackMethod = actRollbackSource[actRollbackStart..actRollbackEnd];
        AssertOrdered(actRollbackMethod, "transaction = db.Database.CurrentTransaction is null",
            "? await db.Database.BeginTransactionAsync(cancellationToken) : null;", "if (operation.EntityId.HasValue)",
            "if (db.Database.IsNpgsql())", "{operation.EntityId.Value} FOR UPDATE NOWAIT",
            "await numbering.LockAsync(cancellationToken);", "await db.Entry(operation).ReloadAsync(cancellationToken);",
            "await RollbackEntityAsync<Act, ActSnapshot>");
        Assert.Equal(1, actRollbackMethod.Split("await numbering.LockAsync(cancellationToken);", StringSplitOptions.None).Length - 1);
        Approve("backend/Services/SpreadsheetImportService.Rollback.cs", actRollbackSource, "RollbackActAsync", actRollbackStart, actRollbackEnd,
            ["""await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = {operation.EntityId.Value} FOR UPDATE NOWAIT").ToArrayAsync(cancellationToken);"""]);
        Assert.Equal(107, approved.Count(statement => statement.Contains("FOR UPDATE", StringComparison.Ordinal)));
        Assert.Equal(129, approved.Count(statement => statement.Contains("FOR SHARE", StringComparison.Ordinal)));
        Assert.Equal(236, approved.Count);

        // Exact identities include owner, method range, line and complete SQL call.
        // An extra statement in any other production file/method remains a failure.
        Assert.True(approved.SetEquals(matches), "Missing registered SQL: " + string.Join("\n", approved.Except(matches))
            + "\nUnexpected SQL: " + string.Join("\n", matches.Except(approved)));
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
