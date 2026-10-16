using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    [Theory]
    [InlineData("list", false)]
    [InlineData("detail", false)]
    [InlineData("rollback", false)]
    [InlineData("reprocess", false)]
    [InlineData("list", true)]
    [InlineData("detail", true)]
    [InlineData("rollback", true)]
    [InlineData("reprocess", true)]
    public async Task BankImportAccess_DeniedViewOrDisabledUser_NeverReadsOrMutatesPackage(
        string endpoint,
        bool disabledAdmin)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(
            db,
            "bank-import-denied@example.test",
            "Проверка запрещённого доступа",
            isActive: !disabledAdmin,
            roleName: disabledAdmin ? "admin" : "bank-import-action-without-view");
        if (!disabledAdmin)
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Payments,
                CanView = false,
                CanCreate = true,
                CanEdit = true,
                ViewScope = DataVisibilityScope.All,
            });
        }

        var batch = new BankStatementImportBatch
        {
            FileName = "private-synthetic-statement.csv",
            FileHash = "private-synthetic-hash",
            FileSize = 100,
            RowsRead = 2,
            CreatedRows = 1,
            ErrorRows = 1,
            ErrorSummary = "Закрытые сведения синтетической строки",
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Синтетический счёт",
            Amount = 123m,
            Currency = "RUB",
            Date = new DateTime(2026, 10, 1),
            ResponsibleUserId = user.Id,
            BankStatementImportBatch = batch,
        };
        db.MoneyOperations.Add(operation);
        batch.Rows.Add(new BankStatementImportRow
        {
            RowNumber = 1,
            ImportKey = "created-row",
            Status = BankStatementImportRowStatus.Created,
            MoneyOperation = operation,
            Date = operation.Date,
            Direction = operation.Direction,
            Amount = operation.Amount,
            AccountName = operation.AccountName,
        });
        batch.Rows.Add(new BankStatementImportRow
        {
            RowNumber = 2,
            ImportKey = "retry-row",
            Status = BankStatementImportRowStatus.Error,
            Message = "Закрытая ошибка синтетической строки",
            Date = operation.Date,
            Direction = MoneyOperationDirection.Expense,
            Amount = 456m,
            AccountName = operation.AccountName,
            CounterpartyName = "Синтетический поставщик",
        });
        await db.SaveChangesAsync();
        var before = await CaptureBankImportStateAsync(db);
        var controller = CreateController(db, user.Id, isAdminClaim: true);

        IActionResult? result = endpoint switch
        {
            "list" => (await controller.GetBankStatementImports(ct: CancellationToken.None)).Result,
            "detail" => (await controller.GetBankStatementImport(batch.Id, CancellationToken.None)).Result,
            "rollback" => (await controller.RollbackBankStatementImport(batch.Id, CancellationToken.None)).Result,
            "reprocess" => (await controller.ReprocessBankStatementImport(batch.Id, CancellationToken.None)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

        Assert.Equal(before, await CaptureBankImportStateAsync(db));
        Assert.IsType<ForbidResult>(result);
    }

    [Theory]
    [InlineData("list", false)]
    [InlineData("detail", false)]
    [InlineData("rollback", false)]
    [InlineData("reprocess", false)]
    [InlineData("list", true)]
    [InlineData("detail", true)]
    [InlineData("rollback", true)]
    [InlineData("reprocess", true)]
    public async Task BankImportAccess_ForeignOrMixedResponsibilities_HidesWholePackageAndPreservesMoney(
        string endpoint,
        bool mixed)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "bank-import-own@example.test", "Свой ответственный");
        var foreignUser = await CreateUserAsync(db, "bank-import-foreign@example.test", "Чужой ответственный");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            ViewScope = DataVisibilityScope.Own,
        });
        var batch = new BankStatementImportBatch
        {
            FileName = "foreign-package.csv",
            FileHash = "foreign-package-hash",
            RowsRead = mixed ? 2 : 1,
            CreatedRows = mixed ? 2 : 1,
        };
        // When mixed, the first row belongs to the caller. Access to that row
        // must not grant access to another employee's part of the same package.
        var responsibleIds = mixed ? new[] { user.Id, foreignUser.Id } : [foreignUser.Id];
        for (var index = 0; index < responsibleIds.Length; index++)
        {
            var operation = new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Синтетический счёт",
                Amount = 100m + index,
                Currency = "RUB",
                Date = new DateTime(2026, 10, 1),
                ResponsibleUserId = responsibleIds[index],
                BankStatementImportBatch = batch,
            };
            db.MoneyOperations.Add(operation);
            batch.Rows.Add(new BankStatementImportRow
            {
                RowNumber = index + 1,
                ImportKey = $"foreign-row-{index}",
                Status = BankStatementImportRowStatus.Created,
                MoneyOperation = operation,
                Date = operation.Date,
                Direction = operation.Direction,
                Amount = operation.Amount,
                AccountName = operation.AccountName,
            });
        }

        await db.SaveChangesAsync();
        var before = await CaptureBankImportStateAsync(db);
        var controller = CreateController(db, user.Id, isAdminClaim: true);
        IActionResult? result = endpoint switch
        {
            "list" => (await controller.GetBankStatementImports(ct: CancellationToken.None)).Result,
            "detail" => (await controller.GetBankStatementImport(batch.Id, CancellationToken.None)).Result,
            "rollback" => (await controller.RollbackBankStatementImport(batch.Id, CancellationToken.None)).Result,
            "reprocess" => (await controller.ReprocessBankStatementImport(batch.Id, CancellationToken.None)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

        Assert.Equal(before, await CaptureBankImportStateAsync(db));
        if (endpoint == "list")
        {
            var page = Assert.IsType<PagedResponse<BankStatementImportBatchListItemDto>>(
                Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(0, page.Total);
            Assert.Empty(page.Items);
        }
        else
        {
            Assert.IsType<NotFoundResult>(result);
        }
    }

    private static async Task<string> CaptureBankImportStateAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            Batches = await db.BankStatementImportBatches.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.Status,
                    item.RowsRead,
                    item.CreatedRows,
                    item.SkippedRows,
                    item.ErrorRows,
                    item.RollbackRows,
                    item.RolledBackAt,
                }).ToListAsync(),
            Rows = await db.BankStatementImportRows.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.BatchId,
                    item.Status,
                    item.Message,
                    item.MoneyOperationId,
                    item.Amount,
                    item.RolledBackAt,
                }).ToListAsync(),
            Operations = await db.MoneyOperations.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.Amount,
                    item.ResponsibleUserId,
                    item.BankStatementImportBatchId,
                    item.LinkedInvoiceId,
                    item.MatchStatus,
                }).ToListAsync(),
            Payments = await db.Payments.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.Amount,
                    item.PaidAmount,
                    item.Status,
                    item.InvoiceId,
                }).ToListAsync(),
            InvoiceCount = await db.Invoices.CountAsync(),
            CashOrderCount = await db.CashOrders.CountAsync(),
        });
}
