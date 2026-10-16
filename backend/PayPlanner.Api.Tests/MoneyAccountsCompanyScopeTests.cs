using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private static async Task<int> RegisterAccountCompanyAsync(PaymentContext db)
    {
        Assert.False(await db.SystemSettings.AnyAsync(item => item.Key == "legalEntities.mainCompanyIds"));
        var company = new LegalEntity { ShortName = "Синтетическая компания старого сценария" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { company.Id }) });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private sealed record AccountScopeSeed(User Actor, LegalEntity Company, LegalEntity Other, LegalEntity Supplier,
        MoneyAccount Own, MoneyAccount Foreign, MoneyAccount Unknown, MoneyAccount Incoherent, BankConnectionAccount AvailableBank);

    private static async Task<AccountScopeSeed> SeedAccountScopeAsync(PaymentContext db, bool admin)
    {
        var actor = await CreateUserAsync(db, "account-scope@example.test", "Синтетический сотрудник", roleName: admin ? "admin" : "account-reader");
        actor.IsEmployee = true;
        db.RolePermissions.Add(new RolePermission { RoleId = actor.RoleId, Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All });
        var company = new LegalEntity { ShortName = "Своя компания" };
        var other = new LegalEntity { ShortName = "Другая собственная компания" };
        var supplier = new LegalEntity { ShortName = "Сторонняя организация" };
        db.LegalEntities.AddRange(company, other, supplier);
        await db.SaveChangesAsync();
        BankConnectionAccount Bank(LegalEntity owner, string id) => new()
        {
            BankConnection = new BankConnection { LegalEntityId = owner.Id, Provider = BankProviderKeys.Tochka },
            ExternalAccountId = id,
            AccountNumber = "synthetic-" + id,
            Bik = "044525225",
            Currency = "RUB",
            Status = BankAccountStatuses.Open,
        };
        var ownBank = Bank(company, "own");
        var otherBank = Bank(other, "other");
        var badBank = Bank(supplier, "incoherent");
        var available = Bank(company, "available");
        MoneyAccount Account(LegalEntity owner, string name, BankConnectionAccount bank) => new()
        {
            CompanyId = owner.Id,
            Name = name,
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            BankAccountNumber = bank.AccountNumber,
            BankBik = bank.Bik,
            BankConnectionAccount = bank,
            IsActive = true,
            IsDefault = true,
        };
        var own = Account(company, "Свой счёт", ownBank);
        var foreign = Account(other, "Чужой счёт", otherBank);
        var unknown = new MoneyAccount { Name = "Исторический счёт", Source = MoneyOperationSource.Cash, IsActive = true };
        var incoherent = Account(company, "Несогласованный счёт", badBank);
        incoherent.IsDefault = false;
        db.MoneyAccounts.AddRange(own, foreign, unknown, incoherent);
        db.BankConnectionAccounts.Add(available);
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { company.Id, other.Id }) });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor.Id, LegalEntityId = company.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(actor, company, other, supplier, own, foreign, unknown, incoherent, available);
    }

    private static MoneyAccount NewScopedAccount(AccountScopeSeed seed) => new()
    {
        Name = "Новый счёт",
        CompanyId = seed.Company.Id,
        Source = MoneyOperationSource.Bank,
        Currency = "rub",
        BankConnectionAccountId = seed.AvailableBank.Id,
        BankAccountNumber = seed.AvailableBank.AccountNumber,
        BankBik = seed.AvailableBank.Bik,
        IsActive = true,
        IsDefault = true,
    };

    private static async Task<string> CaptureAccountScopeAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Accounts = await db.MoneyAccounts.AsNoTracking().OrderBy(item => item.Id).Select(item => new
        { item.Id, item.Name, item.CompanyId, item.Source, item.Currency, item.BankAccountNumber, item.BankBik, item.BankConnectionAccountId, item.IsDefault, item.IsActive }).ToArrayAsync(),
        Operations = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new
        { item.Id, item.MoneyAccountId, item.Amount, IsDeleted = EF.Property<bool>(item, "IsDeleted"), item.SourceMovementKey }).ToArrayAsync(),
        Batches = await db.BankStatementImportBatches.AsNoTracking().OrderBy(item => item.Id).Select(item => new
        { item.Id, item.CompanyId, item.MoneyAccountId, item.BankConnectionAccountId, item.RowsRead, item.ErrorRows }).ToArrayAsync(),
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_ListOnlyAssignedCompanyAndCoherentAccounts(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, false);
        var result = await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id).Get(activeOnly: false);
        var accounts = Assert.IsAssignableFrom<IReadOnlyList<MoneyAccount>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(seed.Own.Id, Assert.Single(accounts).Id);
    }

    public static IEnumerable<object[]> AccountReadDeniedCases =>
        from postgres in new[] { false, true }
        from endpoint in new[] { "list", "detail" }
        from reason in new[] { "inactive", "unapproved", "no-view", "anonymous" }
        select new object[] { postgres, endpoint, reason };

    [Theory]
    [MemberData(nameof(AccountReadDeniedCases))]
    public async Task AccountScope_ReadRequiresCurrentApprovedPermission(bool postgres, string endpoint, string reason)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, false);
        if (reason == "inactive") await db.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        if (reason == "unapproved") await db.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsApproved, false));
        if (reason == "no-view") await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanView, false));
        var controller = CreateMoneyAccountsController(db, reason == "anonymous" ? int.MaxValue : seed.Actor.Id);
        var before = await CaptureAccountScopeAsync(db);
        IActionResult? action = endpoint == "list" ? (await controller.Get()).Result : (await controller.GetById(seed.Own.Id, default)).Result;
        Assert.IsType<ForbidResult>(action);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    [Theory]
    [InlineData(false, "foreign")]
    [InlineData(false, "unknown")]
    [InlineData(false, "incoherent")]
    [InlineData(true, "foreign")]
    [InlineData(true, "unknown")]
    [InlineData(true, "incoherent")]
    public async Task AccountScope_DirectIdDoesNotRevealUnassignedOrIncoherentAccount(bool postgres, string kind)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, false);
        var id = kind == "foreign" ? seed.Foreign.Id : kind == "unknown" ? seed.Unknown.Id : seed.Incoherent.Id;
        Assert.IsType<NotFoundResult>((await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id).GetById(id, default)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_CompanyGrantDoesNotActivateNonEmployee(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, false);
        await fixture.Db.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsEmployee, false));
        var controller = CreateMoneyAccountsController(fixture.Db, seed.Actor.Id);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<MoneyAccount>>(Assert.IsType<OkObjectResult>((await controller.Get()).Result).Value));
        Assert.IsType<NotFoundResult>((await controller.GetById(seed.Own.Id, default)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_AdminReadsUnknownWithoutAssigningCompany(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, true);
        var before = await CaptureAccountScopeAsync(fixture.Db);
        var account = Assert.IsType<MoneyAccount>(Assert.IsType<OkObjectResult>((await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id, false).GetById(seed.Unknown.Id, default)).Result).Value);
        Assert.Null(account.CompanyId);
        Assert.Equal(before, await CaptureAccountScopeAsync(fixture.Db));
    }

    public static IEnumerable<object[]> AccountCreateDeniedCases =>
        from postgres in new[] { false, true }
        from boundary in new[] { "missing-company", "not-own-company", "foreign-bank", "number-mismatch", "currency-mismatch", "bik-mismatch", "unknown-source", "duplicate-bank-link" }
        select new object[] { postgres, boundary };

    [Theory]
    [MemberData(nameof(AccountCreateDeniedCases))]
    public async Task AccountScope_CreateValidatesDeclaredOriginBeforeWriting(bool postgres, string boundary)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, true);
        var model = NewScopedAccount(seed);
        if (boundary == "missing-company") model.CompanyId = null;
        if (boundary == "not-own-company") model.CompanyId = seed.Supplier.Id;
        if (boundary == "foreign-bank") model.BankConnectionAccountId = seed.Foreign.BankConnectionAccountId;
        if (boundary == "number-mismatch") model.BankAccountNumber = "different-account";
        if (boundary == "currency-mismatch") model.Currency = "USD";
        if (boundary == "bik-mismatch") model.BankBik = "000000000";
        if (boundary == "unknown-source") model.Source = (MoneyOperationSource)999;
        if (boundary == "duplicate-bank-link")
        {
            model.BankConnectionAccountId = seed.Own.BankConnectionAccountId;
            model.BankAccountNumber = seed.Own.BankAccountNumber;
        }
        var before = await CaptureAccountScopeAsync(db);
        var action = (await CreateMoneyAccountsController(db, seed.Actor.Id, false).Create(model, default)).Result;
        if (boundary == "duplicate-bank-link") Assert.IsType<ConflictObjectResult>(action);
        else Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_CreatePersistsExplicitCompanyAndBankOrigin(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, true);
        var account = Assert.IsType<MoneyAccount>(Assert.IsType<CreatedAtActionResult>((await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id, false).Create(NewScopedAccount(seed), default)).Result).Value);
        Assert.Equal(seed.Company.Id, account.CompanyId);
        Assert.Equal(seed.AvailableBank.Id, account.BankConnectionAccountId);
        Assert.Equal(seed.AvailableBank.Bik, account.BankBik);
        Assert.Equal(seed.Company.ShortName, account.CompanyName);
        Assert.Equal("RUB", account.Currency);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_DefaultsRemainIndependentBetweenCompanies(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, true);
        var controller = CreateMoneyAccountsController(fixture.Db, seed.Actor.Id, false);
        var first = NewScopedAccount(seed);
        first.Name = "Новый основной А";
        var created = Assert.IsType<MoneyAccount>(Assert.IsType<CreatedAtActionResult>((await controller.Create(first, default)).Result).Value);
        var second = new MoneyAccount { CompanyId = seed.Other.Id, Name = "Новый основной Б", Source = MoneyOperationSource.Bank, IsDefault = true };
        Assert.IsType<CreatedAtActionResult>((await controller.Create(second, default)).Result);
        Assert.True(await fixture.Db.MoneyAccounts.Where(item => item.Id == created.Id).Select(item => item.IsDefault).SingleAsync());
        Assert.Equal(1, await fixture.Db.MoneyAccounts.CountAsync(item => item.CompanyId == seed.Company.Id && item.Source == MoneyOperationSource.Bank && item.IsDefault));
        Assert.Equal(1, await fixture.Db.MoneyAccounts.CountAsync(item => item.CompanyId == seed.Other.Id && item.Source == MoneyOperationSource.Bank && item.IsDefault));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_SameNameAllowedInDifferentOwnCompanies(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, true);
        var controller = CreateMoneyAccountsController(fixture.Db, seed.Actor.Id, false);
        foreach (var company in new[] { seed.Company.Id, seed.Other.Id })
            Assert.IsType<CreatedAtActionResult>((await controller.Create(new MoneyAccount { CompanyId = company, Name = "Одинаковое название", Source = MoneyOperationSource.Cash }, default)).Result);
        Assert.Equal(2, await fixture.Db.MoneyAccounts.CountAsync(item => item.Name == "Одинаковое название"));
    }

    private static async Task AddAccountHistoryAsync(PaymentContext db, AccountScopeSeed seed, string kind)
    {
        if (kind is "empty" or "error")
        {
            var batch = new BankStatementImportBatch { CompanyId = seed.Company.Id, MoneyAccountId = seed.Own.Id, FileName = "synthetic.csv", FileHash = "synthetic", Source = BankStatementImportSources.File };
            if (kind == "error")
            {
                batch.ErrorRows = 1;
                batch.Rows.Add(new BankStatementImportRow { RowNumber = 1, ImportKey = "error", Status = BankStatementImportRowStatus.Error, Message = "Синтетическая ошибка" });
            }
            db.BankStatementImportBatches.Add(batch);
        }
        else if (kind != "unused")
        {
            var operation = new MoneyOperation { MoneyAccountId = seed.Own.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Amount = 10m, Date = BankScopeDate };
            db.MoneyOperations.Add(operation);
            await db.SaveChangesAsync();
            if (kind == "archived") db.MoneyOperations.Remove(operation);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(false, "error")]
    [InlineData(false, "archived")]
    [InlineData(false, "unused")]
    [InlineData(true, "empty")]
    [InlineData(true, "error")]
    [InlineData(true, "archived")]
    [InlineData(true, "unused")]
    public async Task AccountScope_DeleteArchivesEveryReferencedAccountAndKeepsHistory(bool postgres, string kind)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, true);
        await AddAccountHistoryAsync(db, seed, kind);
        IActionResult? action = null;
        var error = await Record.ExceptionAsync(async () => action = await CreateMoneyAccountsController(db, seed.Actor.Id, false).Delete(seed.Own.Id, default));
        Assert.Null(error);
        Assert.IsType<NoContentResult>(action);
        var saved = await db.MoneyAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == seed.Own.Id);
        if (kind == "unused") Assert.Null(saved);
        else
        {
            Assert.NotNull(saved);
            Assert.False(saved.IsActive);
            Assert.False(saved.IsDefault);
            if (kind == "archived") Assert.Equal(seed.Own.Id, (await db.MoneyOperations.IgnoreQueryFilters().SingleAsync()).MoneyAccountId);
            else Assert.Equal(seed.Own.Id, (await db.BankStatementImportBatches.SingleAsync()).MoneyAccountId);
        }
        Assert.True((await db.MoneyAccounts.SingleAsync(item => item.Id == seed.Foreign.Id)).IsDefault);
    }

    public static IEnumerable<object[]> AccountIdentityChangeCases =>
        from postgres in new[] { false, true }
        from field in new[] { "company", "source", "currency", "number", "bik", "bank-link" }
        select new object[] { postgres, field };

    [Theory]
    [MemberData(nameof(AccountIdentityChangeCases))]
    public async Task AccountScope_UsedAccountCannotChangeFinancialIdentity(bool postgres, string field)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, true);
        await AddAccountHistoryAsync(db, seed, "active");
        var model = new MoneyAccount { Name = seed.Own.Name, Source = seed.Own.Source, CompanyId = seed.Company.Id, Currency = "RUB", BankAccountNumber = seed.Own.BankAccountNumber, BankBik = seed.Own.BankBik, BankConnectionAccountId = seed.Own.BankConnectionAccountId };
        if (field == "company") model.CompanyId = seed.Other.Id;
        if (field == "source") model.Source = MoneyOperationSource.Cash;
        if (field == "currency") model.Currency = "USD";
        if (field == "number") model.BankAccountNumber = "changed";
        if (field == "bik") model.BankBik = "000000000";
        if (field == "bank-link") model.BankConnectionAccountId = seed.AvailableBank.Id;
        var before = await CaptureAccountScopeAsync(db);
        Assert.IsType<ConflictObjectResult>((await CreateMoneyAccountsController(db, seed.Actor.Id, false).Update(seed.Own.Id, model, default)).Result);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    [Theory]
    [InlineData(false, "update")]
    [InlineData(false, "delete")]
    [InlineData(true, "update")]
    [InlineData(true, "delete")]
    public async Task AccountScope_UnknownHistoryRemainsReadOnlyForAdmin(bool postgres, string endpoint)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, true);
        var controller = CreateMoneyAccountsController(db, seed.Actor.Id, false);
        var before = await CaptureAccountScopeAsync(db);
        var action = endpoint == "delete" ? await controller.Delete(seed.Unknown.Id, default)
            : (await controller.Update(seed.Unknown.Id, new MoneyAccount { Name = "Не назначать владельца", Source = MoneyOperationSource.Cash }, default)).Result;
        Assert.IsType<ConflictObjectResult>(action);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    [Theory]
    [InlineData(false, "create", false)]
    [InlineData(false, "update", false)]
    [InlineData(false, "delete", false)]
    [InlineData(true, "create", false)]
    [InlineData(true, "update", false)]
    [InlineData(true, "delete", false)]
    [InlineData(false, "create", true)]
    [InlineData(false, "update", true)]
    [InlineData(false, "delete", true)]
    [InlineData(true, "create", true)]
    [InlineData(true, "update", true)]
    [InlineData(true, "delete", true)]
    public async Task AccountScope_WritesRequireCurrentApprovedDatabaseAdmin(bool postgres, string endpoint, bool unapprovedAdmin)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, unapprovedAdmin);
        if (unapprovedAdmin) await db.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsApproved, false));
        var controller = CreateMoneyAccountsController(db, seed.Actor.Id);
        var before = await CaptureAccountScopeAsync(db);
        IActionResult? action = endpoint switch
        {
            "create" => (await controller.Create(NewScopedAccount(seed), default)).Result,
            "update" => (await controller.Update(seed.Own.Id, NewScopedAccount(seed), default)).Result,
            _ => await controller.Delete(seed.Own.Id, default),
        };
        Assert.IsType<ForbidResult>(action);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_CreateIgnoresRootIdentityAndNestedGraph(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, true);
        var model = NewScopedAccount(seed);
        model.Id = seed.Foreign.Id;
        model.Company = new LegalEntity { ShortName = "Не создавать вложенную компанию" };
        model.BankConnectionAccount = new BankConnectionAccount { ExternalAccountId = "injected", AccountNumber = "injected", BankConnection = new BankConnection { Provider = "injected" } };
        model.MoneyOperations.Add(new MoneyOperation { Amount = 123m, Date = BankScopeDate, Source = MoneyOperationSource.Bank });
        ActionResult<MoneyAccount>? result = null;
        var error = await Record.ExceptionAsync(async () => result = await CreateMoneyAccountsController(db, seed.Actor.Id, false).Create(model, default));
        Assert.Null(error);
        var saved = Assert.IsType<MoneyAccount>(Assert.IsType<CreatedAtActionResult>(result!.Result).Value);
        Assert.NotEqual(seed.Foreign.Id, saved.Id);
        Assert.Equal(seed.Company.Id, saved.CompanyId);
        Assert.Empty(await db.MoneyOperations.ToArrayAsync());
        Assert.Equal(3, await db.LegalEntities.CountAsync());
        Assert.Equal(4, await db.BankConnectionAccounts.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_DoesNotDuplicatePhysicalBankAccountWhenLinkIsOmitted(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, true);
        var model = NewScopedAccount(seed);
        model.BankConnectionAccountId = null;
        model.BankAccountNumber = seed.Own.BankAccountNumber;
        var before = await CaptureAccountScopeAsync(fixture.Db);
        Assert.IsType<ConflictObjectResult>((await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id, false).Create(model, default)).Result);
        Assert.Equal(before, await CaptureAccountScopeAsync(fixture.Db));
    }

    [Theory]
    [InlineData(false, "Draft")]
    [InlineData(false, "Archived")]
    [InlineData(false, "Deleted")]
    [InlineData(true, "Draft")]
    [InlineData(true, "Archived")]
    [InlineData(true, "Deleted")]
    public async Task AccountScope_ActiveAccountRequiresExistingActiveOwnProfile(bool postgres, string state)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, true);
        if (state == "Deleted")
        {
            fixture.Db.LegalEntities.Remove(seed.Company);
            await fixture.Db.SaveChangesAsync();
        }
        else await fixture.Db.LegalEntities.Where(item => item.Id == seed.Company.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ProfileStatus, state));
        var before = await CaptureAccountScopeAsync(fixture.Db);
        Assert.IsType<BadRequestObjectResult>((await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id, false).Create(NewScopedAccount(seed), default)).Result);
        Assert.Equal(before, await CaptureAccountScopeAsync(fixture.Db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_CompanyFilterNarrowsGrantedCompaniesAndReturnsName(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, false);
        fixture.Db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = seed.Actor.Id, LegalEntityId = seed.Other.Id });
        await fixture.Db.SaveChangesAsync();
        var result = await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id).Get(companyId: seed.Other.Id);
        var account = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyAccount>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(seed.Foreign.Id, account.Id);
        Assert.Equal(seed.Other.ShortName, account.CompanyName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountScope_DuplicateNameUsesSameUnicodeComparisonOnBothProviders(bool postgres)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, true);
        var model = NewScopedAccount(seed);
        model.Name = seed.Own.Name.ToUpperInvariant();
        var before = await CaptureAccountScopeAsync(fixture.Db);
        Assert.IsType<ConflictObjectResult>((await CreateMoneyAccountsController(fixture.Db, seed.Actor.Id, false).Create(model, default)).Result);
        Assert.Equal(before, await CaptureAccountScopeAsync(fixture.Db));
    }
}
