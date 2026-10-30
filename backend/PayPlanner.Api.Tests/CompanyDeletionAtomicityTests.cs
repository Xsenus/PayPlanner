using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Fact]
    public async Task CompanyDelete_LastOwnCompanyClearsRegistryAndAssignmentsButPreservesPolicyAndHistory()
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, true);
        var controller = CreateController(db, fixture.Actor);
        Assert.IsType<OkObjectResult>((await controller.UpdateMainCompanySettings(new PayPlanner.Api.Models.Requests.UpdateMainCompanySettingsRequest
        {
            ExpectedVersion = await MainCompanySettingsVersionTestAccess.ReadAsync(controller),
            CompanyIds = [fixture.Company],
            DefaultCompanyId = fixture.Company,
        }, default)).Result);
        var protectedBefore = await CompanyDeleteProtectedSnapshot(db);
        Assert.IsType<NoContentResult>(await controller.Delete(fixture.Company, default));
        Assert.Empty(await OwnCompanyRegistry.LoadIdsAsync(db)); Assert.Empty(await db.UserCompanyAccesses.ToArrayAsync());
        Assert.Equal(0, await db.SystemSettings.CountAsync(row => row.Key == "legalEntities.mainCompanyIds" || row.Key == "legalEntities.mainCompanyId" || row.Key == "legalEntities.defaultMainCompanyId"));
        Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, await new DocumentSellerService(db).ReadCreationPolicyAsync(default));
        var settings = await ReadMainSettings(controller); Assert.Empty(settings.Companies); Assert.Null(settings.DefaultCompanyId);
        Assert.Matches("^[A-F0-9]{64}$", settings.SettingsVersion!);
        Assert.Equal(protectedBefore, await CompanyDeleteProtectedSnapshot(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyDelete_SuccessPreservesIssuedDocumentsTasksAndOtherCompany(bool own)
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, own);
        var protectedBefore = await CompanyDeleteProtectedSnapshot(db);
        var controller = CreateController(db, fixture.Actor);
        Assert.IsType<NoContentResult>(await controller.Delete(fixture.Company, default));
        Assert.Equal(protectedBefore, await CompanyDeleteProtectedSnapshot(db));
        Assert.False(await db.LegalEntities.AsNoTracking().AnyAsync(row => row.Id == fixture.Company));
        Assert.True(await db.LegalEntities.IgnoreQueryFilters().Where(row => row.Id == fixture.Company).Select(row => EF.Property<bool>(row, "IsDeleted")).SingleAsync());
        Assert.Null((await db.Clients.AsNoTracking().SingleAsync(row => row.Id == fixture.Client)).LegalEntityId);
        Assert.Null((await db.ExternalContacts.AsNoTracking().SingleAsync()).LinkedLegalEntityId);
        Assert.Empty(await db.AddressRecords.AsNoTracking().Where(row => row.LegalEntityId == fixture.Company).ToArrayAsync());
        Assert.True(await db.AddressRecords.IgnoreQueryFilters().Where(row => row.LegalEntityId == fixture.Company).Select(row => EF.Property<bool>(row, "IsDeleted")).SingleAsync());
        Assert.Equal(fixture.Other, (await db.UserCompanyAccesses.AsNoTracking().SingleAsync()).LegalEntityId);
        Assert.False(await db.SystemSettings.AnyAsync(row => row.Key == CompanyDocumentImagePrintDefaultsService.Key(fixture.Company)));
        var ids = await OwnCompanyRegistry.LoadIdsAsync(db, default);
        Assert.Equal(own ? [fixture.Other] : Array.Empty<int>(), ids.Order().ToArray());
        Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, await new DocumentSellerService(db).ReadCreationPolicyAsync(default));
        var document = await db.Payments.AsNoTracking().SingleAsync(row => row.Id == fixture.Payment);
        var seller = await new DocumentSellerService(db).ResolveAsync(document, null, false, default);
        Assert.NotNull(seller); Assert.Equal("Issued seller", seller.ShortName); Assert.Equal(CompanyImageTestData.TransparentPng, seller.LogoDataUrl);
        var after = await CompanyDeleteSnapshot(db);
        Assert.IsType<NotFoundResult>(await controller.Delete(fixture.Company, default));
        Assert.Equal(after, await CompanyDeleteSnapshot(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompanyDelete_RejectsCallerOwnedUnitOfWorkWithoutSavingOrClearingIt(bool own, bool ambient)
    {
        await using var db = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(db, own);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var pending = new Client { Name = "Caller-owned pending change" }; db.Clients.Add(pending);
        var before = await CompanyDeleteSnapshot(db);
        Assert.IsType<ConflictObjectResult>(await CreateController(db, fixture.Actor).Delete(fixture.Company, default));
        Assert.Same(transaction, db.Database.CurrentTransaction); Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.Equal(before, await CompanyDeleteSnapshot(db));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("cancel")]
    public async Task CompanyDelete_ConflictOrCancellationAfterSaveRollsBack(string kind)
    {
        var failure = new CompanyDeleteFailure { Kind = kind };
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await CompanyDeleteFixture(db, true); var before = await CompanyDeleteSnapshot(db);
        failure.Point = "company";
        if (kind == "conflict") Assert.IsType<ConflictObjectResult>(await CreateController(db, fixture.Actor).Delete(fixture.Company, default));
        else await Assert.ThrowsAsync<OperationCanceledException>(() => CreateController(db, fixture.Actor).Delete(fixture.Company, default));
        Assert.Equal(1, failure.Hits); Assert.Equal(before, await CompanyDeleteSnapshot(db));
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("regular-owner", 204)]
    [InlineData("regular-foreign", 404)]
    [InlineData("no-delete", 403)]
    [InlineData("no-view", 401)]
    [InlineData("unapproved", 403)]
    [InlineData("inactive", 401)]
    [InlineData("missing-user", 401)]
    [InlineData("own-assigned-editor", 403)]
    [InlineData("own-unapproved-admin", 403)]
    [InlineData("own-inactive-admin", 403)]
    [InlineData("own-missing-admin", 403)]
    public async Task CompanyDelete_UsesApprovedDatabaseActorAndCurrentDeleteScope(string mode, int status)
    {
        await using var db = await CreateContextAsync(); var own = mode.StartsWith("own-", StringComparison.Ordinal);
        var fixture = await CompanyDeleteFixture(db, own);
        var actor = own && mode != "own-assigned-editor" ? fixture.Actor : fixture.Staff;
        await SetLegalEntityPermissionAsync(db, fixture.Staff, false, false, mode != "no-delete", mode != "no-view");
        var staff = await db.Users.SingleAsync(row => row.Id == fixture.Staff);
        var permission = await db.RolePermissions.SingleAsync(row => row.RoleId == staff.RoleId && row.Section == RolePermissionCatalog.LegalEntities);
        if (mode is "regular-owner" or "regular-foreign") permission.ViewScope = DataVisibilityScope.Own;
        if (mode == "regular-owner") (await db.LegalEntities.SingleAsync(row => row.Id == fixture.Company)).ResponsibleUserId = actor;
        if (mode.Contains("unapproved", StringComparison.Ordinal)) (await db.Users.SingleAsync(row => row.Id == actor)).IsApproved = false;
        if (mode.Contains("inactive", StringComparison.Ordinal)) (await db.Users.SingleAsync(row => row.Id == actor)).IsActive = false;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (mode.Contains("missing", StringComparison.Ordinal)) actor = 999999;
        var before = await CompanyDeleteSnapshot(db);
        var result = await CreateController(db, actor, isAdmin: true).Delete(fixture.Company, default);
        if (status == 204) Assert.IsType<NoContentResult>(result);
        else
        {
            Assert.Equal(status, result switch { ForbidResult => 403, UnauthorizedResult => 401, NotFoundResult => 404, _ => 0 });
            Assert.Equal(before, await CompanyDeleteSnapshot(db));
        }
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(true, "registry")]
    [InlineData(true, "company")]
    [InlineData(false, "company")]
    public async Task CompanyDelete_FailureAfterDatabaseSaveRestoresWholeOperation(bool own, string point)
    {
        var failure = new CompanyDeleteFailure();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await CompanyDeleteFixture(db, own);
        var before = await CompanyDeleteSnapshot(db);
        failure.Point = point;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController(db, fixture.Actor).Delete(fixture.Company, default));
        Assert.Equal("Synthetic company-delete failure after a real database save", error.Message);

        Assert.Equal(1, failure.Hits);
        Assert.Equal(before, await CompanyDeleteSnapshot(db));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class CompanyDeleteFailure : SaveChangesInterceptor
    {
        internal string? Point { get; set; }
        internal int Hits { get; private set; }
        internal string Kind { get; set; } = "error";
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var deletingCompany = eventData.Context!.ChangeTracker.Entries<LegalEntity>().Any(entry => entry.Property<bool>("IsDeleted").CurrentValue);
            if (Point == "registry" || Point == "company" && deletingCompany)
            {
                Point = null; ++Hits;
                if (Kind == "conflict") throw new DbUpdateConcurrencyException("Synthetic concurrent delete");
                if (Kind == "cancel") throw new OperationCanceledException("Synthetic canceled delete");
                throw new InvalidOperationException("Synthetic company-delete failure after a real database save");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed record CompanyDeleteData(int Actor, int Staff, int Company, int Other, int Client, int Branch, int Payment, int Case, int Task);

    private static async Task<CompanyDeleteData> CompanyDeleteFixture(PaymentContext db, bool own)
    {
        var actor = await CreateUserAsync(db, "admin"); var staff = await CreateUserAsync(db);
        var company = await CreateLegalEntityAsync(db, "Company to remove"); var other = await CreateLegalEntityAsync(db, "Company to keep");
        var image = new CompanyBrandImageVersion
        {
            LegalEntityId = company,
            Kind = CompanyBrandImageKinds.Logo,
            DataUrl = CompanyImageTestData.TransparentPng,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(CompanyImageTestData.TransparentPng.Split(',')[1])))
        };
        db.CompanyBrandImageVersions.Add(image); await db.SaveChangesAsync();
        var client = new Client { Name = "Linked client to preserve", LegalEntityId = company };
        var branch = new CompanyBranch { LegalEntityId = company, Name = "Historical branch" };
        var linkedCase = new ClientCase { LegalEntityId = company, Title = "Keep case", CaseNumber = "CASE-OLD" };
        var snapshot = SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity { Id = company, ShortName = "Issued seller", Inn = "7700000121" }, logoVersionId: image.Id, imagesFrozen: true);
        var invoice = new Payment
        {
            Account = "INV-OLD",
            Amount = 1234.56m,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
            ClientCase = linkedCase,
            LegalEntityId = company,
            SellerCompanyId = company,
            SellerSnapshotJson = snapshot
        };
        var act = new Act { Number = "ACT-OLD", Amount = 345.67m, Client = client, LegalEntityId = company, SellerCompanyId = company, SellerSnapshotJson = snapshot };
        var contract = new Contract { Number = "CONTRACT-OLD", Amount = 9876.54m, SellerCompanyId = company, SellerSnapshotJson = snapshot };
        var task = new TaskJournalItem
        {
            Title = "Keep task",
            ObjectName = "Keep case links",
            Client = client,
            ClientCase = linkedCase,
            LegalEntityId = company,
            BillingInvoice = invoice,
            BillingContract = contract,
            ContractorPaymentAmount = 234.56m
        };
        db.AddRange(client, branch, linkedCase, invoice, act, contract, task);
        db.Add(new InvoiceClientCase { Payment = invoice, ClientCase = linkedCase }); db.Add(new ContractAct { Contract = contract, Act = act });
        db.AddressRecords.Add(new AddressRecord { LegalEntityId = company, Value = "Historical address", Type = AddressType.Legal });
        db.ExternalContacts.Add(new ExternalContact { FullName = "Source company contact", ContactType = "Organization", LinkedLegalEntityId = company });
        db.UserCompanyAccesses.AddRange(new UserCompanyAccess { UserId = staff, LegalEntityId = company }, new UserCompanyAccess { UserId = staff, LegalEntityId = other });
        db.SystemSettings.AddRange(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate },
            new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(company), Value = CompanyDocumentImagePrintDefaultsService.Serialize(new(new(false, false, false), null, null)) });
        if (!await db.SystemSettings.AnyAsync(row => row.Key == "notifications.email.enabled"))
            db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        if (own)
            db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company},{other}]" },
                new SystemSetting { Key = "legalEntities.mainCompanyId", Value = company.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new SystemSetting { Key = "legalEntities.defaultMainCompanyId", Value = company.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await db.SaveChangesAsync();
        invoice.SellerBranchId = branch.Id; act.SellerBranchId = branch.Id; contract.SellerBranchId = branch.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); // Match a fresh HTTP request, not fixture-owned navigations.
        return new(actor, staff, company, other, client.Id, branch.Id, invoice.Id, linkedCase.Id, task.Id);
    }

    private static async Task<string> CompanyDeleteSnapshot(PaymentContext db) => JsonSerializer.Serialize(new
    {
        companies = await CompanyDeleteRows<LegalEntity>(db),
        clients = await CompanyDeleteRows<Client>(db),
        addresses = await CompanyDeleteRows<AddressRecord>(db),
        assignments = await CompanyDeleteRows<UserCompanyAccess>(db),
        contacts = await CompanyDeleteRows<ExternalContact>(db),
        settings = await CompanyDeleteRows<SystemSetting>(db),
        branches = await CompanyDeleteRows<CompanyBranch>(db),
        payments = await CompanyDeleteRows<Payment>(db),
        acts = await CompanyDeleteRows<Act>(db),
        contracts = await CompanyDeleteRows<Contract>(db),
        cases = await CompanyDeleteRows<ClientCase>(db),
        tasks = await CompanyDeleteRows<TaskJournalItem>(db),
        audit = await CompanyDeleteRows<ObjectAuditEvent>(db),
        versions = await CompanyDeleteRows<CompanyBrandImageVersion>(db),
        invoiceCases = await CompanyDeleteRows<InvoiceClientCase>(db),
        contractActs = await CompanyDeleteRows<ContractAct>(db),
    });

    private static async Task<string> CompanyDeleteProtectedSnapshot(PaymentContext db) => JsonSerializer.Serialize(new
    {
        branches = await CompanyDeleteRows<CompanyBranch>(db),
        payments = await CompanyDeleteRows<Payment>(db),
        acts = await CompanyDeleteRows<Act>(db),
        contracts = await CompanyDeleteRows<Contract>(db),
        cases = await CompanyDeleteRows<ClientCase>(db),
        tasks = await CompanyDeleteRows<TaskJournalItem>(db),
        versions = await CompanyDeleteRows<CompanyBrandImageVersion>(db),
        invoiceCases = await CompanyDeleteRows<InvoiceClientCase>(db),
        contractActs = await CompanyDeleteRows<ContractAct>(db),
    });

    // Assert all persisted scalar values (including shadow audit/deletion fields),
    // not only the fields the deletion implementation currently happens to touch.
    private static async Task<string[]> CompanyDeleteRows<T>(PaymentContext db) where T : class
    {
        var entity = Expression.Parameter(typeof(T), "row");
        var values = db.Model.FindEntityType(typeof(T))!.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => Expression.Convert(Expression.Call(typeof(EF), nameof(EF.Property), [property.ClrType], entity, Expression.Constant(property.Name)), typeof(object)));
        var selector = Expression.Lambda<Func<T, object[]>>(Expression.NewArrayInit(typeof(object), values), entity);
        var rows = await db.Set<T>().IgnoreQueryFilters().AsNoTracking().Select(selector).ToArrayAsync();
        return rows.Select(row => JsonSerializer.Serialize(row)).OrderBy(row => row, StringComparer.Ordinal).ToArray();
    }
}
