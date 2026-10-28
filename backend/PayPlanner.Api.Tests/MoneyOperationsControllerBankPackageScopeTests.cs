using System.Security.Cryptography;
using System.Text;
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
    public static IEnumerable<object[]> BankPackageDenialCases
    {
        get
        {
            foreach (var postgres in new[] { false, true })
                foreach (var endpoint in new[] { "list", "detail", "rollback", "reprocess" })
                    foreach (var boundary in new[]
                    {
                        "foreign-company", "foreign-account", "foreign-raw", "mixed-responsible",
                        "foreign-extra-operation", "foreign-row-operation", "archived-foreign-operation",
                        "foreign-parent", "mixed-parent", "foreign-origin", "mixed-origin",
                        "empty-foreign", "errors-foreign", "archived-foreign-package",
                        "unknown-origin", "foreign-declared-bank", "no-view", "disabled-admin",
                        "second-account-movement", "second-account-raw", "second-account-declared",
                        "second-account-parent", "second-account-origin", "archived-other-key", "incoherent-account-bank",
                    })
                        yield return [postgres, endpoint, boundary];
        }
    }

    [Theory]
    [MemberData(nameof(BankPackageDenialCases))]
    public async Task BankPackageScope_RejectsWholeForbiddenPackageBeforeReadOrMutation(bool postgres, string endpoint, string boundary)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, boundary);
        var before = await CaptureBankScopeStateAsync(db);
        var result = await InvokeBankPackageEndpointAsync(CreateController(db, seed.ActorId, isAdminClaim: true), endpoint, seed.Batch.Id);

        Assert.Equal(before, await CaptureBankScopeStateAsync(db));
        if (boundary is "no-view" or "disabled-admin")
        {
            Assert.IsType<ForbidResult>(result);
        }
        else if (endpoint == "list")
        {
            var page = Assert.IsType<PagedResponse<BankStatementImportBatchListItemDto>>(Assert.IsType<OkObjectResult>(result).Value);
            var expectedVisible = boundary is "second-account-parent" or "second-account-origin" ? 1 : 0;
            Assert.Equal(expectedVisible, page.Total);
            Assert.Equal(expectedVisible, page.Items.Count);
            Assert.DoesNotContain(page.Items, item => item.Id == seed.Batch.Id);
        }
        else
        {
            Assert.IsType<NotFoundResult>(result);
        }
    }

    [Theory]
    [InlineData(false, "own")]
    [InlineData(true, "own")]
    [InlineData(false, "empty-own")]
    [InlineData(true, "empty-own")]
    [InlineData(false, "errors-own")]
    [InlineData(true, "errors-own")]
    [InlineData(false, "archived-own")]
    [InlineData(true, "archived-own")]
    public async Task BankPackageScope_ReadsOwnHistoryAndReturnsPersistedOrigin(bool postgres, string kind)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, kind);
        var controller = CreateController(db, seed.ActorId, isAdminClaim: true);
        var before = await CaptureBankScopeStateAsync(db);

        var list = Assert.IsType<PagedResponse<BankStatementImportBatchListItemDto>>(
            Assert.IsType<OkObjectResult>((await controller.GetBankStatementImports(ct: CancellationToken.None)).Result).Value);
        var item = Assert.Single(list.Items);
        Assert.Equal(1, list.Total);
        Assert.Equal(seed.Batch.Id, item.Id);
        var detail = Assert.IsType<BankStatementImportBatchDetailDto>(
            Assert.IsType<OkObjectResult>((await controller.GetBankStatementImport(seed.Batch.Id, CancellationToken.None)).Result).Value);
        foreach (var response in new object[] { item, detail })
        {
            var json = JsonSerializer.SerializeToElement(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(seed.Batch.CompanyId, json.GetProperty("companyId").GetInt32());
            Assert.Equal(seed.Batch.MoneyAccountId, json.GetProperty("moneyAccountId").GetInt32());
            Assert.Equal(seed.ActorId, json.GetProperty("importedByUserId").GetInt32());
            Assert.Equal(BankStatementImportSources.File, json.GetProperty("source").GetString());
        }
        Assert.Equal(before, await CaptureBankScopeStateAsync(db));
    }

    [Theory]
    [InlineData("RUB")]
    [InlineData("USD")]
    [InlineData(null)]
    public async Task BankPackageScope_DetailReturnsStoredCurrencyWithoutChangingHistory(string? currency)
    {
        await using var fixture = await CreateModelDatabaseAsync(false);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, "own");
        var rowId = Assert.Single(seed.Batch.Rows).Id;
        var storedRow = await db.BankStatementImportRows.SingleAsync(row => row.Id == rowId);
        storedRow.Currency = currency;
        await db.SaveChangesAsync();
        Assert.Equal(currency, await db.BankStatementImportRows.AsNoTracking()
            .Where(row => row.Id == rowId).Select(row => row.Currency).SingleAsync());
        var before = await CaptureBankScopeStateAsync(db);
        var response = await CreateController(db, seed.ActorId, isAdminClaim: true)
            .GetBankStatementImport(seed.Batch.Id, CancellationToken.None);
        var detail = Assert.IsType<BankStatementImportBatchDetailDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(currency, Assert.Single(detail.Rows).Currency);
        Assert.Equal(before, await CaptureBankScopeStateAsync(db));
    }

    [Fact]
    public async Task BankPackageScope_DetailPagesRowsAndReportsActionsAcrossEntireBatch()
    {
        await using var fixture = await CreateModelDatabaseAsync(false);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, "own");
        for (var i = 2; i <= 106; i++)
            db.BankStatementImportRows.Add(new BankStatementImportRow
            {
                BatchId = seed.Batch.Id,
                RowNumber = i,
                ImportKey = $"synthetic-page-{i}",
                Status = i == 106 ? BankStatementImportRowStatus.Error : BankStatementImportRowStatus.Created,
                Date = BankScopeDate,
                Amount = i,
                Currency = "RUB",
            });
        await db.SaveChangesAsync();
        Assert.Equal(106, await db.BankStatementImportRows.CountAsync(row => row.BatchId == seed.Batch.Id));
        var before = await CaptureBankScopeStateAsync(db);
        var controller = CreateController(db, seed.ActorId, isAdminClaim: true);
        var first = Assert.IsType<BankStatementImportBatchDetailDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetBankStatementImport(seed.Batch.Id, CancellationToken.None)).Result).Value);
        var last = Assert.IsType<BankStatementImportBatchDetailDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetBankStatementImport(seed.Batch.Id, CancellationToken.None, 3)).Result).Value);
        Assert.Equal(106, first.RowsTotal);
        Assert.Equal(50, first.Rows.Count);
        Assert.True(first.HasReprocessableRows);
        Assert.Equal(6, last.Rows.Count);
        Assert.Equal(106, last.Rows.Last().RowNumber);
        Assert.Equal(before, await CaptureBankScopeStateAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankPackageScope_AdminReadsUnattributedHistoryWithoutAssigningOrigin(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, "admin-unknown");
        var before = await CaptureBankScopeStateAsync(db);
        var controller = CreateController(db, seed.ActorId, isAdminClaim: false);
        var list = Assert.IsType<PagedResponse<BankStatementImportBatchListItemDto>>(
            Assert.IsType<OkObjectResult>((await controller.GetBankStatementImports(ct: CancellationToken.None)).Result).Value);
        Assert.Equal(seed.Batch.Id, Assert.Single(list.Items).Id);
        Assert.IsType<OkObjectResult>((await controller.GetBankStatementImport(seed.Batch.Id, CancellationToken.None)).Result);
        Assert.Equal(before, await CaptureBankScopeStateAsync(db));
        Assert.Null((await db.BankStatementImportBatches.SingleAsync()).CompanyId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankPackageScope_RollbackOwnPackageRetainsOriginAndArchivedIdentity(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, "own");
        var operationId = (await db.MoneyOperations.SingleAsync()).Id;
        var result = Assert.IsType<BankStatementImportRollbackResultDto>(Assert.IsType<OkObjectResult>(
            (await CreateController(db, seed.ActorId, isAdminClaim: true).RollbackBankStatementImport(seed.Batch.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(1, result.DeletedOperations);
        Assert.Empty(await db.MoneyOperations.ToArrayAsync());
        Assert.Equal(operationId, (await db.MoneyOperations.IgnoreQueryFilters().SingleAsync()).Id);
        var batch = await db.BankStatementImportBatches.SingleAsync();
        Assert.Equal(seed.Batch.CompanyId, batch.CompanyId);
        Assert.Equal(seed.Batch.MoneyAccountId, batch.MoneyAccountId);
        Assert.Equal(seed.ActorId, batch.ImportedByUserId);
        Assert.NotNull((await db.BankStatementImportRows.SingleAsync()).MovementKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankPackageScope_ReprocessOwnRowsPreservesLineageAndReplaysSameResult(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, "errors-own");
        var origin = await db.BankStatementImportRows.SingleAsync();
        var controller = CreateController(db, seed.ActorId, isAdminClaim: true);
        var first = Assert.IsType<BankStatementImportResultDto>(Assert.IsType<OkObjectResult>(
            (await controller.ReprocessBankStatementImport(seed.Batch.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(1, first.Created);
        Assert.NotNull(first.BatchId);
        var retry = await db.BankStatementImportBatches.SingleAsync(item => item.Id == first.BatchId);
        Assert.Equal(seed.Batch.Id, retry.ParentBatchId);
        Assert.Equal(seed.Batch.CompanyId, retry.CompanyId);
        Assert.Equal(seed.Batch.MoneyAccountId, retry.MoneyAccountId);
        Assert.Equal(seed.ActorId, retry.ImportedByUserId);
        Assert.Equal(origin.Id, (await db.BankStatementImportRows.SingleAsync(item => item.BatchId == retry.Id)).OriginRowId);
        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(origin.MovementKey, operation.SourceMovementKey);
        Assert.Equal(seed.Batch.MoneyAccountId, operation.MoneyAccountId);

        var second = Assert.IsType<BankStatementImportResultDto>(Assert.IsType<OkObjectResult>(
            (await controller.ReprocessBankStatementImport(seed.Batch.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(first.BatchId, second.BatchId);
        Assert.Equal(operation.Id, (await db.MoneyOperations.SingleAsync()).Id);
        Assert.Single(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
    }

    private static async Task<IActionResult?> InvokeBankPackageEndpointAsync(PayPlanner.Api.Controllers.MoneyOperationsController controller, string endpoint, int batchId)
        => endpoint switch
        {
            "list" => (await controller.GetBankStatementImports(ct: CancellationToken.None)).Result,
            "detail" => (await controller.GetBankStatementImport(batchId, CancellationToken.None)).Result,
            "rollback" => (await controller.RollbackBankStatementImport(batchId, CancellationToken.None)).Result,
            "reprocess" => (await controller.ReprocessBankStatementImport(batchId, CancellationToken.None)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

    [Theory]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "reprocess")]
    [InlineData(true, "reprocess")]
    public async Task BankPackageScope_ViewPermissionDoesNotGrantMutation(bool postgres, string endpoint)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, "own");
        var permission = await db.RolePermissions.SingleAsync();
        permission.CanCreate = false;
        permission.CanEdit = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = CreateController(db, seed.ActorId, isAdminClaim: true);
        Assert.IsType<OkObjectResult>((await controller.GetBankStatementImport(seed.Batch.Id, CancellationToken.None)).Result);
        var before = await CaptureBankScopeStateAsync(db);
        Assert.IsType<ForbidResult>(await InvokeBankPackageEndpointAsync(controller, endpoint, seed.Batch.Id));
        Assert.Equal(before, await CaptureBankScopeStateAsync(db));
    }

    private static readonly DateTime BankScopeDate = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record BankScopeSeed(int ActorId, BankStatementImportBatch Batch);

    private static async Task<BankScopeSeed> SeedBankScopeAsync(PaymentContext db, string kind)
    {
        var admin = kind is "disabled-admin" or "admin-unknown";
        var ownScope = kind is "mixed-responsible" or "foreign-extra-operation" or "archived-foreign-operation" or "mixed-parent" or "mixed-origin"
            or "own" or "empty-own" or "errors-own" or "archived-own";
        var actor = await CreateUserAsync(db, "scope-actor@example.test", "Синтетический сотрудник", isActive: kind != "disabled-admin", roleName: admin ? "admin" : "bank-scope");
        var other = await CreateUserAsync(db, "scope-other@example.test", "Другой синтетический сотрудник", roleName: "bank-scope-other");
        actor.IsEmployee = true;
        other.IsEmployee = true;
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = actor.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = kind != "no-view",
            CanCreate = true,
            CanEdit = true,
            ViewScope = ownScope ? DataVisibilityScope.Own : DataVisibilityScope.All,
        });
        var company = new LegalEntity { ShortName = "Своя компания" };
        var foreign = new LegalEntity { ShortName = "Чужая компания" };
        db.LegalEntities.AddRange(company, foreign);
        await db.SaveChangesAsync();
        var ownBank = new BankConnectionAccount
        {
            BankConnection = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Tochka },
            ExternalAccountId = "own-bank",
            AccountNumber = "synthetic-own",
            Bik = "bik-own",
            Currency = "RUB",
        };
        var foreignBank = new BankConnectionAccount
        {
            BankConnection = new BankConnection { LegalEntityId = foreign.Id, Provider = BankProviderKeys.Tochka },
            ExternalAccountId = "foreign-bank",
            AccountNumber = "synthetic-foreign",
            Bik = "bik-foreign",
            Currency = "RUB",
        };
        var ownAccount = new MoneyAccount { Name = "Свой счёт", Source = MoneyOperationSource.Bank, CompanyId = company.Id, BankConnectionAccount = ownBank, BankAccountNumber = ownBank.AccountNumber, BankBik = ownBank.Bik, Currency = "RUB" };
        var foreignAccount = new MoneyAccount { Name = "Чужой счёт", Source = MoneyOperationSource.Bank, CompanyId = foreign.Id, BankConnectionAccount = foreignBank, BankAccountNumber = foreignBank.AccountNumber, BankBik = foreignBank.Bik, Currency = "RUB" };
        db.MoneyAccounts.AddRange(ownAccount, foreignAccount);
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { company.Id, foreign.Id }) });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor.Id, LegalEntityId = company.Id });
        await db.SaveChangesAsync();
        var secondBank = new BankConnectionAccount
        {
            BankConnection = ownBank.BankConnection,
            ExternalAccountId = "second-own-bank",
            AccountNumber = "synthetic-second-own",
            Bik = ownBank.Bik,
            Currency = "RUB",
        };
        var secondAccount = new MoneyAccount { Name = "Второй свой счёт", Source = MoneyOperationSource.Bank, CompanyId = company.Id, BankConnectionAccount = secondBank, BankAccountNumber = secondBank.AccountNumber, BankBik = secondBank.Bik, Currency = "RUB" };
        if (kind.StartsWith("second-account", StringComparison.Ordinal))
        {
            db.MoneyAccounts.Add(secondAccount);
            await db.SaveChangesAsync();
        }
        var batch = BuildScopeBatch("target", ownAccount, actor.Id);
        var first = batch.Rows.Single();
        var operation = first.MoneyOperation!;
        if (kind is "foreign-company" or "empty-foreign" or "errors-foreign" or "archived-foreign-package")
        {
            batch.CompanyId = foreign.Id;
            batch.MoneyAccountId = foreignAccount.Id;
            batch.BankConnectionAccountId = foreignBank.Id;
            operation.MoneyAccountId = foreignAccount.Id;
            operation.AccountName = foreignAccount.Name;
            first.AccountName = foreignAccount.Name;
        }
        if (kind == "foreign-account") batch.MoneyAccountId = foreignAccount.Id;
        if (kind == "foreign-row-operation") operation.MoneyAccountId = foreignAccount.Id;
        if (kind == "foreign-raw")
            first.BankStatementOperation = new BankStatementOperation { BankConnectionAccountId = foreignBank.Id, ExternalOperationId = "foreign-raw", OperationDate = BankScopeDate, Amount = 123m };
        if (kind == "foreign-declared-bank") batch.BankConnectionAccountId = foreignBank.Id;
        if (kind == "second-account-movement") operation.MoneyAccountId = secondAccount.Id;
        if (kind == "second-account-declared") batch.BankConnectionAccountId = secondBank.Id;
        if (kind == "second-account-raw")
            first.BankStatementOperation = new BankStatementOperation { BankConnectionAccountId = secondBank.Id, ExternalOperationId = "second-own-raw", OperationDate = BankScopeDate, Amount = 123m };
        if (kind is "second-account-parent" or "second-account-origin")
        {
            var parent = BuildScopeBatch("second-parent", secondAccount, actor.Id);
            db.BankStatementImportBatches.Add(parent);
            if (kind.EndsWith("parent", StringComparison.Ordinal)) batch.ParentBatch = parent;
            else first.OriginRow = parent.Rows.First();
        }
        if (kind == "incoherent-account-bank")
        {
            ownAccount.BankConnectionAccount = new BankConnectionAccount
            {
                BankConnection = foreignBank.BankConnection,
                ExternalAccountId = "incoherent-foreign-bank",
                AccountNumber = ownAccount.BankAccountNumber!,
                Bik = ownAccount.BankBik,
                Currency = ownAccount.Currency,
            };
            batch.BankConnectionAccountId = null;
        }
        MoneyOperation? foreignCanonical = null;
        if (kind == "archived-other-key")
        {
            var parent = BuildScopeBatch("canonical-foreign", foreignAccount, other.Id);
            db.BankStatementImportBatches.Add(parent);
            foreignCanonical = parent.Rows.Single().MoneyOperation!;
            first.MoneyOperation = null;
            first.MovementKey = foreignCanonical.SourceMovementKey;
            first.ImportKey = foreignCanonical.SourceMovementKey!;
            first.Status = BankStatementImportRowStatus.Error;
            batch.CreatedRows = 0;
            batch.ErrorRows = 1;
        }
        if (kind == "mixed-responsible") AddScopeMovement(batch, ownAccount, other.Id, 2);
        if (kind == "foreign-extra-operation")
        {
            var extra = BuildScopeMovement(batch, ownAccount, other.Id, 2);
            db.MoneyOperations.Add(extra);
        }
        if (kind == "archived-foreign-operation") operation.ResponsibleUserId = other.Id;
        if (kind is "foreign-parent" or "mixed-parent" or "foreign-origin" or "mixed-origin")
        {
            var parent = BuildScopeBatch("parent", kind.StartsWith("foreign", StringComparison.Ordinal) ? foreignAccount : ownAccount, actor.Id);
            if (kind.StartsWith("mixed", StringComparison.Ordinal)) AddScopeMovement(parent, ownAccount, other.Id, 2);
            db.BankStatementImportBatches.Add(parent);
            if (kind.EndsWith("parent", StringComparison.Ordinal)) batch.ParentBatch = parent;
            else first.OriginRow = parent.Rows.First();
        }
        if (kind is "empty-foreign" or "empty-own")
        {
            batch.Rows.Clear();
            batch.RowsRead = 0;
            batch.CreatedRows = 0;
        }
        if (kind is "errors-foreign" or "errors-own")
        {
            first.MoneyOperation = null;
            first.Status = BankStatementImportRowStatus.Error;
            first.Message = "Синтетическая ошибка распознавания";
            batch.CreatedRows = 0;
            batch.ErrorRows = 1;
        }
        if (kind is "unknown-origin" or "admin-unknown")
        {
            batch.CompanyId = null;
            batch.MoneyAccountId = null;
            batch.BankConnectionAccountId = null;
            batch.ImportedByUserId = null;
            batch.ResponsibleUserId = null;
            batch.Source = null;
            batch.Trigger = null;
            batch.PackageKey = null;
            batch.PeriodFrom = null;
            batch.PeriodTo = null;
        }
        db.BankStatementImportBatches.Add(batch);
        await db.SaveChangesAsync();
        if (foreignCanonical is not null)
        {
            db.MoneyOperations.Remove(foreignCanonical);
            await db.SaveChangesAsync();
        }
        if (kind is "archived-foreign-operation" or "archived-foreign-package" or "archived-own")
        {
            db.MoneyOperations.Remove(operation);
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        return new(actor.Id, batch);
    }

    private static BankStatementImportBatch BuildScopeBatch(string label, MoneyAccount account, int actorId)
    {
        var batch = new BankStatementImportBatch
        {
            FileName = $"private-{label}.csv",
            FileHash = BankScopeKey(label),
            PackageKey = BankScopeKey("package:" + label),
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
            BankConnectionAccountId = account.BankConnectionAccountId,
            ImportedByUserId = actorId,
            ResponsibleUserId = actorId,
            Source = BankStatementImportSources.File,
            Trigger = BankStatementImportTriggers.Manual,
            PeriodFrom = BankScopeDate,
            PeriodTo = BankScopeDate,
            RowsRead = 1,
            CreatedRows = 1,
            ErrorSummary = "Закрытое синтетическое описание",
        };
        AddScopeMovement(batch, account, actorId, 1);
        return batch;
    }

    private static void AddScopeMovement(BankStatementImportBatch batch, MoneyAccount account, int responsible, int number)
    {
        var operation = BuildScopeMovement(batch, account, responsible, number);
        batch.Rows.Add(new BankStatementImportRow
        {
            RowNumber = number,
            ImportKey = operation.SourceMovementKey!,
            MovementKey = operation.SourceMovementKey,
            MoneyOperation = operation,
            Status = BankStatementImportRowStatus.Created,
            Date = BankScopeDate,
            Amount = operation.Amount,
            Currency = "RUB",
            IsBooked = true,
            AccountName = account.Name,
            Direction = operation.Direction,
            CounterpartyName = "Закрытый синтетический контрагент",
            PaymentOrderNumber = number.ToString(),
        });
    }

    private static MoneyOperation BuildScopeMovement(BankStatementImportBatch batch, MoneyAccount account, int responsible, int number)
        => new()
        {
            BankStatementImportBatch = batch,
            MoneyAccountId = account.Id,
            AccountName = account.Name,
            ResponsibleUserId = responsible,
            Source = MoneyOperationSource.Bank,
            AccountingType = MoneyOperationAccountingType.Fact,
            Direction = MoneyOperationDirection.Expense,
            Amount = 123m + number,
            Currency = "RUB",
            Date = BankScopeDate,
            SourceMovementKey = BankScopeKey(batch.FileHash + ":" + number),
            PaymentOrderNumber = number.ToString(),
            CounterpartyName = "Закрытый синтетический контрагент",
        };

    private static string BankScopeKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> CaptureBankScopeStateAsync(PaymentContext db, int? excludedManualOperationId = null)
        => JsonSerializer.Serialize(new
        {
            Batches = await db.BankStatementImportBatches.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.CompanyId, item.MoneyAccountId, item.BankConnectionAccountId, item.ImportedByUserId, item.ResponsibleUserId, item.Source, item.Trigger, item.ParentBatchId, item.PackageKey, item.Status, item.CreatedRows, item.SkippedRows, item.RollbackRows, item.RolledBackAt }).ToListAsync(),
            Rows = await db.BankStatementImportRows.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.BatchId, item.OriginRowId, item.BankStatementOperationId, item.MoneyOperationId, item.MovementKey, item.Status, item.Message, item.Amount, item.RolledBackAt }).ToListAsync(),
            Money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().Where(item => !excludedManualOperationId.HasValue || item.Id != excludedManualOperationId.Value).OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.MoneyAccountId, item.ResponsibleUserId, item.BankStatementImportBatchId, item.SourceMovementKey, item.Amount, item.LinkedInvoiceId, item.MatchStatus, Deleted = EF.Property<bool>(item, "IsDeleted") }).ToListAsync(),
            Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Amount, item.PaidAmount, item.InvoiceId, item.Status }).ToListAsync(),
            BalanceCount = await db.ClientBalanceTransactions.CountAsync(),
            RawCount = await db.BankStatementOperations.CountAsync(),
        });
}
