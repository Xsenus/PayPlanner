using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private static async Task<string> CaptureMoneyDeleteAsync(PaymentContext db) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Manual = await CaptureManualCompanyAsync(db),
        Cash = await db.CashOrders.IgnoreQueryFilters().AsNoTracking().OrderBy(order => order.Id)
            .Select(order => new { order.Id, order.MoneyOperationId, order.Status, order.UpdatedAt }).ToArrayAsync(),
        Rows = await db.BankStatementImportRows.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.BatchId, row.MoneyOperationId, row.Status }).ToArrayAsync(),
        PaymentStates = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(payment => payment.Id)
            .Select(payment => new { payment.Id, payment.Status, payment.IsPaid, payment.PaidDate, payment.LastPaymentDate }).ToArrayAsync(),
    });
    private static async Task<MoneyAccount> PrepareMoneyDeleteAsync(PaymentContext db, AccountScopeSeed seed)
    {
        var cash = await PrepareManualCompanyAsync(db, seed);
        await db.RolePermissions.Where(permission => permission.RoleId == seed.Actor.RoleId && permission.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(setters => setters.SetProperty(permission => permission.CanDelete, true));
        return cash;
    }

    public static IEnumerable<object[]> MoneyDeleteDeniedCases =>
        from postgres in new[] { false, true }
        from reason in new[] { "foreign", "unknown", "incoherent", "revoked", "closed-bank", "inactive-account", "inactive-user", "unapproved-user", "no-action", "missing-account" }
        select new object[] { postgres, reason };

    [Theory]
    [MemberData(nameof(MoneyDeleteDeniedCases))]
    public async Task MoneyDelete_DeniedOriginNeverChangesFinancialHistory(bool postgres, string reason)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await PrepareMoneyDeleteAsync(db, seed);
        int? accountId = reason switch { "foreign" => seed.Foreign.Id, "unknown" => seed.Unknown.Id, "incoherent" => seed.Incoherent.Id, "missing-account" => null, _ => seed.Own.Id };
        var operation = ManualCompanyOperation(seed, accountId);
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        if (reason == "revoked") await db.UserCompanyAccesses.Where(access => access.UserId == seed.Actor.Id).ExecuteDeleteAsync();
        if (reason == "closed-bank") await db.BankConnectionAccounts.Where(account => account.Id == seed.Own.BankConnectionAccountId).ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Status, BankAccountStatuses.Closed));
        if (reason == "inactive-account") await db.MoneyAccounts.Where(account => account.Id == seed.Own.Id).ExecuteUpdateAsync(setters => setters.SetProperty(account => account.IsActive, false));
        if (reason == "inactive-user") await db.Users.Where(user => user.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
        if (reason == "unapproved-user") await db.Users.Where(user => user.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsApproved, false));
        if (reason == "no-action") await db.RolePermissions.Where(permission => permission.RoleId == seed.Actor.RoleId && permission.Section == RolePermissionCatalog.Payments).ExecuteUpdateAsync(setters => setters.SetProperty(permission => permission.CanDelete, false));
        var before = await CaptureMoneyDeleteAsync(db);
        var result = await CreateController(db, seed.Actor.Id).Delete(operation.Id, default);
        Assert.True(result is ForbidResult or NotFoundResult or BadRequestObjectResult);
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureMoneyDeleteAsync(verify));
    }

    public static IEnumerable<object[]> MoneyDeleteImportedCases =>
        from postgres in new[] { false, true }
        from admin in new[] { false, true }
        from origin in new[] { "key", "batch", "row" }
        select new object[] { postgres, admin, origin };

    [Theory]
    [MemberData(nameof(MoneyDeleteImportedCases))]
    public async Task MoneyDelete_ImportedOriginRequiresImportWorkflow(bool postgres, bool admin, string origin)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin);
        await using var db = fixture.Open();
        await PrepareMoneyDeleteAsync(db, fixture.Seed);
        var operation = ManualCompanyOperation(fixture.Seed, fixture.Seed.Own.Id);
        if (origin == "key") operation.SourceMovementKey = "synthetic:imported:delete-boundary";
        var batch = new BankStatementImportBatch
        {
            CompanyId = fixture.Seed.Company.Id,
            MoneyAccountId = fixture.Seed.Own.Id,
            Source = BankStatementImportSources.File,
            FileName = "synthetic-delete.csv",
            FileHash = "synthetic-delete"
        };
        if (origin == "batch") operation.BankStatementImportBatch = batch;
        if (origin == "row") db.BankStatementImportRows.Add(new BankStatementImportRow
        {
            Batch = batch,
            MoneyOperation = operation,
            RowNumber = 1,
            ImportKey = "synthetic-delete-row",
            Status = BankStatementImportRowStatus.Created,
            Date = BankScopeDate,
            Amount = operation.Amount,
            Direction = operation.Direction,
        });
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var before = await CaptureMoneyDeleteAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, fixture.Seed.Actor.Id).Delete(operation.Id, default));
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureMoneyDeleteAsync(verify));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoneyDelete_AdminUnattributedHistoryRemainsReadOnly(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        await PrepareMoneyDeleteAsync(db, fixture.Seed);
        var operation = ManualCompanyOperation(fixture.Seed, fixture.Seed.Unknown.Id);
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var before = await CaptureMoneyDeleteAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, fixture.Seed.Actor.Id).Delete(operation.Id, default));
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureMoneyDeleteAsync(verify));
    }

    private sealed class MoneyDeleteSavedFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Fired { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<CashOrder>().Any(entry => entry.Entity.Status == CashOrderStatus.Cancelled))
            {
                Fired = true;
                throw new InvalidOperationException("synthetic delete acknowledgement failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoneyDelete_LateSaveFailurePreservesOperationCashOrderAndOuterWork(bool postgres, bool outer)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        var fault = new MoneyDeleteSavedFault();
        await using var db = fixture.Open(fault);
        var cash = await PrepareMoneyDeleteAsync(db, fixture.Seed);
        var operation = ManualCompanyOperation(fixture.Seed, cash.Id);
        Assert.IsType<CreatedResult>(await CreateController(db, fixture.Seed.Actor.Id).Create(operation));
        Assert.Single(await db.CashOrders.ToListAsync());
        await using var transaction = outer ? await db.Database.BeginTransactionAsync() : null;
        db.SystemSettings.Add(new SystemSetting { Key = "synthetic.delete.outer-marker", Value = "preserved" });
        await db.SaveChangesAsync();
        var before = await CaptureMoneyDeleteAsync(db);
        fault.Enabled = true;
        var error = await Record.ExceptionAsync(() => CreateController(db, fixture.Seed.Actor.Id).Delete(operation.Id, default));
        Assert.IsType<InvalidOperationException>(error);
        Assert.True(fault.Fired);
        if (transaction is not null) await transaction.CommitAsync();
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureMoneyDeleteAsync(verify));
        Assert.Equal("preserved", (await verify.SystemSettings.SingleAsync(setting => setting.Key == "synthetic.delete.outer-marker")).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoneyDelete_AllowedOwnCashCancelsOrderAndPreservesArchivedIdentity(bool postgres, bool admin)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin);
        await using var db = fixture.Open();
        var cash = await PrepareMoneyDeleteAsync(db, fixture.Seed);
        var operation = ManualCompanyOperation(fixture.Seed, cash.Id);
        Assert.IsType<CreatedResult>(await CreateController(db, fixture.Seed.Actor.Id).Create(operation));
        Assert.IsType<NoContentResult>(await CreateController(db, fixture.Seed.Actor.Id).Delete(operation.Id, default));
        await using var verify = fixture.Open();
        Assert.Empty(await verify.MoneyOperations.ToListAsync());
        Assert.Equal(operation.Id, (await verify.MoneyOperations.IgnoreQueryFilters().SingleAsync()).Id);
        Assert.Equal(CashOrderStatus.Cancelled, (await verify.CashOrders.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoneyDelete_AppliedInvoiceRequiresReversalAndPreservesPayment(bool postgres, bool admin)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await PrepareMoneyDeleteAsync(db, seed);
        var invoice = new Invoice { Number = "synthetic-matched-delete", Date = BankScopeDate };
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            SellerCompanyId = seed.Company.Id,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Expense,
            Date = BankScopeDate,
            Amount = 20m,
            PaidAmount = 10m,
            Status = PaymentStatus.Processing
        });
        var operation = ManualCompanyOperation(seed, seed.Own.Id);
        operation.LinkedInvoice = invoice;
        operation.MatchStatus = MoneyOperationMatchStatus.Matched;
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var before = await CaptureMoneyDeleteAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, seed.Actor.Id).Delete(operation.Id, default));
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureMoneyDeleteAsync(verify));
    }
}
