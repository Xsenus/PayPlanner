using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task SeedMergeOwnHistoryAsync(PaymentContext db, int company, string history)
    {
        var date = new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        switch (history)
        {
            case "branch": db.CompanyBranches.Add(new CompanyBranch { LegalEntityId = company, Name = "Synthetic archived branch", Status = CompanyProfileStatuses.Archived }); break;
            case "account": db.MoneyAccounts.Add(new MoneyAccount { CompanyId = company, Name = "Synthetic inactive account", Source = MoneyOperationSource.Bank, Currency = "RUB", IsActive = false }); break;
            case "order": db.CashOrders.Add(new CashOrder { CompanyId = company, Number = "SYNTHETIC-MERGE-1", Type = CashOrderType.Incoming, Status = CashOrderStatus.Cancelled, Date = date, Amount = 10, Currency = "RUB", CancelledAt = date, CancelledReason = "Synthetic cancellation" }); break;
            case "inventory": db.CashInventories.Add(new CashInventory { CompanyId = company, Number = "SYNTHETIC-MERGE-2", Date = date, AccountName = "Synthetic inventory", AccountingBalance = 100, ActualBalance = 100, Difference = 0 }); break;
            case "import": db.BankStatementImportBatches.Add(new BankStatementImportBatch { CompanyId = company, FileName = "synthetic.csv", FileHash = "synthetic-hash", Status = BankStatementImportBatchStatus.RolledBack, RolledBackAt = date }); break;
            case "reconciliation": db.MoneyBalanceReconciliationResults.Add(new MoneyBalanceReconciliationResult { CompanyId = company, Date = date, AccountName = "Synthetic reconciliation", Currency = "RUB", Source = MoneyOperationSource.Bank, ScopeKey = "synthetic-scope", OpeningBalance = 100, Income = 10, Expense = 5, ClosingBalance = 105, IncomeCount = 1, ExpenseCount = 1, IsBalanced = true, IssueCount = 0 }); break;
            default: throw new InvalidOperationException("Unknown synthetic history.");
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    }
    private static async Task<string> MergeOwnSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Branches = await db.CompanyBranches.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.LegalEntityId, x.Name, x.Status, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(),
        Accounts = await db.MoneyAccounts.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.Name, x.Currency, x.IsActive, x.Source, x.CreatedAt }).ToArrayAsync(),
        Orders = await db.CashOrders.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.Number, x.Date, x.Status, x.Amount, x.Currency, x.CancelledAt, x.CancelledReason }).ToArrayAsync(),
        Inventories = await db.CashInventories.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.Number, x.AccountingBalance, x.ActualBalance, x.Difference }).ToArrayAsync(),
        Imports = await db.BankStatementImportBatches.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.FileName, x.FileHash, x.Status, x.RolledBackAt }).ToArrayAsync(),
        Reconciliations = await db.MoneyBalanceReconciliationResults.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.ScopeKey, x.OpeningBalance, x.Income, x.Expense, x.ClosingBalance, x.IsBalanced }).ToArrayAsync(),
    });
    public static IEnumerable<object[]> MergeOwnHistoryCases() => from kind in new[] { "legal", "external+legal" } from history in new[] { "branch", "account", "order", "inventory", "import", "reconciliation" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, history, execute, postgres };
    [Theory]
    [MemberData(nameof(MergeOwnHistoryCases))]
    public async Task MergeOwnBoundary_ProtectsCompanyIdentityOutsideCurrentRegistry(string kind, string history, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); await SeedMergeOwnHistoryAsync(db, seed.Input.DuplicateSourceId, history); var before = await MergeSnapshotAsync(db); var owned = await MergeOwnSnapshotAsync(db); var result = await RunMergeAsync(db, seed, execute);
        if (execute) Assert.True(result is ConflictObjectResult, $"kind={kind}; history={history}; result={result?.GetType().Name}; archived={!await db.LegalEntities.AnyAsync(x => x.Id == seed.Input.DuplicateSourceId)}"); else { var reply = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(result).Value); Assert.False(reply.CanMerge); Assert.Contains(reply.Conflicts, x => x.Contains("собственн", StringComparison.OrdinalIgnoreCase)); }
        Assert.Equal(before, await MergeSnapshotAsync(db)); Assert.Equal(owned, await MergeOwnSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeOwnBoundary_UnchangedPrimaryCompanyKeepsHistory(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, "legal"); foreach (var history in new[] { "branch", "account", "order", "inventory", "import", "reconciliation" }) await SeedMergeOwnHistoryAsync(db, seed.Input.PrimarySourceId, history); var before = await MergeOwnSnapshotAsync(db); Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, true)); Assert.Equal(before, await MergeOwnSnapshotAsync(db));
    }
}
