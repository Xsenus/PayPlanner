using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyRequisitesResolverTests
{
    private const string Accounts =
        "[{\"id\":\"main\",\"bank\":\"Банк А\",\"bik\":\"044525225\",\"accountNumber\":\"40702810200000000001\",\"corrAccountNumber\":\"30101810400000000225\",\"isPrimary\":true},"
        + "{\"id\":\"second\",\"bank\":\"Банк Б\",\"bik\":\"044525104\",\"accountNumber\":\"40702810200000000002\"},"
        + "{\"id\":\"old\",\"bank\":\"Банк В\",\"bik\":\"044525000\",\"accountNumber\":\"40702810200000000003\",\"isArchived\":true}]";

    private static LegalEntity Company() => new()
    {
        Id = 5,
        ShortName = "ООО Своя",
        FullName = "Общество Своя",
        Inn = "7707083893",
        Kpp = "770701001",
        Ogrn = "1027700132195",
        Address = "Москва, ул. Главная, 1",
        BankAccountsJson = Accounts,
        DocumentDirectorJson = "{\"lastName\":\"Иванов\"}",
    };

    [Fact]
    public void Resolve_WithoutBranch_UsesCompanyValuesAndPrimaryAccount()
    {
        var snapshot = CompanyRequisitesResolver.Resolve(Company(), null, new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("770701001", snapshot.Kpp);
        Assert.Equal("Москва, ул. Главная, 1", snapshot.Address);
        Assert.Equal("40702810200000000001", snapshot.AccountNumber);
        Assert.Equal("main", snapshot.BankAccountId);
        Assert.Equal("{\"lastName\":\"Иванов\"}", snapshot.SignerJson);
        Assert.All(snapshot.Sources.Values, source => Assert.Equal("company", source));
    }

    [Fact]
    public void Resolve_WithBranch_OverridesOnlyFilledFieldsAndNeverInnOrName()
    {
        var branch = new CompanyBranch
        {
            Id = 9,
            LegalEntityId = 5,
            Kind = CompanyBranchKinds.Office,
            Name = "Офис СПб",
            Kpp = "780201001",
            Address = "Санкт-Петербург, Невский, 1",
            BankAccountId = "second",
        };

        var snapshot = CompanyRequisitesResolver.Resolve(Company(), branch, DateTime.UtcNow);

        Assert.Equal("780201001", snapshot.Kpp);
        Assert.Equal("Санкт-Петербург, Невский, 1", snapshot.Address);
        Assert.Equal("40702810200000000002", snapshot.AccountNumber);
        Assert.Equal("7707083893", snapshot.Inn);
        Assert.Equal("ООО Своя", snapshot.Name);
        Assert.Equal("{\"lastName\":\"Иванов\"}", snapshot.SignerJson);
        Assert.Equal("branch", snapshot.Sources["kpp"]);
        Assert.Equal("branch", snapshot.Sources["bankAccount"]);
        Assert.Equal("company", snapshot.Sources["signer"]);
        Assert.Equal(9, snapshot.BranchId);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("foreign-account")]
    public void Resolve_FallsBackToCompanyAccount_WhenBranchAccountIsArchivedOrNotTheirs(string accountId)
    {
        var branch = new CompanyBranch { LegalEntityId = 5, Name = "Точка", BankAccountId = accountId };

        var snapshot = CompanyRequisitesResolver.Resolve(Company(), branch, DateTime.UtcNow);

        Assert.Equal("40702810200000000001", snapshot.AccountNumber);
        Assert.Equal("company", snapshot.Sources["bankAccount"]);
    }

    [Fact]
    public void Resolve_RejectsBranchOfAnotherCompany()
    {
        var branch = new CompanyBranch { LegalEntityId = 6, Name = "Чужой" };

        Assert.Throws<InvalidOperationException>(() => CompanyRequisitesResolver.Resolve(Company(), branch, DateTime.UtcNow));
    }

    [Fact]
    public void Snapshot_IsIndependentOfLaterCompanyEdits()
    {
        var company = Company();
        var snapshot = CompanyRequisitesResolver.Resolve(company, null, DateTime.UtcNow);

        company.Kpp = "999999999";
        company.BankAccountsJson = "[]";

        Assert.Equal("770701001", snapshot.Kpp);
        Assert.Equal("40702810200000000001", snapshot.AccountNumber);
    }
}

public class CompanyBranchesControllerTests
{
    private static async Task<(PaymentContext Db, int CompanyId)> CreateAsync(string? accountsJson = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var company = new LegalEntity { ShortName = "ООО Своя", Inn = "7707083893", BankAccountsJson = accountsJson };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        return (db, company.Id);
    }

    private static CompanyBranchRequest Request(string name = "Офис", Action<CompanyBranchRequest>? tweak = null)
    {
        var request = new CompanyBranchRequest { Name = name, Kind = "office" };
        tweak?.Invoke(request);
        return request;
    }

    [Fact]
    public async Task Create_SavesBranchWithDefaults_AndRejectsDuplicateName()
    {
        var (db, companyId) = await CreateAsync();
        await using var _ = db;
        var controller = new CompanyBranchesController(db);

        var created = await controller.Create(companyId, Request(" Офис СПб "), CancellationToken.None);

        var dto = Assert.IsType<CompanyBranchDto>(Assert.IsType<CreatedResult>(created.Result).Value);
        Assert.Equal("Офис СПб", dto.Name);
        Assert.Equal(CompanyBranchKinds.Office, dto.Kind);
        Assert.Equal(CompanyProfileStatuses.Active, dto.Status);

        var duplicate = await controller.Create(companyId, Request("Офис СПб"), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicate.Result);
    }

    [Theory]
    [InlineData("Kind", "unknown")]
    [InlineData("Status", "Draft")]
    [InlineData("Kpp", "123")]
    [InlineData("SignerJson", "[1]")]
    [InlineData("SignerJson", "не json")]
    [InlineData("BankAccountId", "foreign")]
    public async Task Create_RejectsInvalidFields(string field, string value)
    {
        var (db, companyId) = await CreateAsync();
        await using var _ = db;
        var controller = new CompanyBranchesController(db);

        var action = await controller.Create(companyId, Request("Офис", r =>
        {
            typeof(CompanyBranchRequest).GetProperty(field)!.SetValue(r, value);
        }), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Empty(db.CompanyBranches);
    }

    [Fact]
    public async Task Create_AcceptsOwnActiveAccount_ButNotArchivedOne()
    {
        var (db, companyId) = await CreateAsync(
            "[{\"id\":\"a\",\"accountNumber\":\"1\"},{\"id\":\"b\",\"accountNumber\":\"2\",\"isArchived\":true}]");
        await using var _ = db;
        var controller = new CompanyBranchesController(db);

        var ok = await controller.Create(companyId, Request("Один", r => r.BankAccountId = "a"), CancellationToken.None);
        var archived = await controller.Create(companyId, Request("Два", r => r.BankAccountId = "b"), CancellationToken.None);

        Assert.IsType<CreatedResult>(ok.Result);
        Assert.IsType<BadRequestObjectResult>(archived.Result);
    }

    [Fact]
    public async Task Create_IsRefused_ForArchivedCompanyOrUnknownCompany()
    {
        var (db, companyId) = await CreateAsync();
        await using var _ = db;
        var controller = new CompanyBranchesController(db);

        Assert.IsType<NotFoundResult>((await controller.Create(999, Request(), CancellationToken.None)).Result);

        (await db.LegalEntities.SingleAsync()).ProfileStatus = CompanyProfileStatuses.Archived;
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>((await controller.Create(companyId, Request(), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task UpdateAndList_ArchiveHidesBranchUnlessRequested_AndDoesNotCrossCompanies()
    {
        var (db, companyId) = await CreateAsync();
        await using var _ = db;
        var other = new LegalEntity { ShortName = "Другая" };
        db.LegalEntities.Add(other);
        await db.SaveChangesAsync();
        var controller = new CompanyBranchesController(db);
        var created = Assert.IsType<CompanyBranchDto>(
            Assert.IsType<CreatedResult>((await controller.Create(companyId, Request(), CancellationToken.None)).Result).Value);

        var wrongCompany = await controller.Update(other.Id, created.Id, Request(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(wrongCompany.Result);

        var archived = await controller.Update(companyId, created.Id, Request("Офис", r => r.Status = "Archived"), CancellationToken.None);
        Assert.Equal(CompanyProfileStatuses.Archived, Assert.IsType<CompanyBranchDto>(Assert.IsType<OkObjectResult>(archived.Result).Value).Status);

        var visible = Assert.IsType<List<CompanyBranchDto>>(Assert.IsType<OkObjectResult>((await controller.List(companyId, false, CancellationToken.None)).Result).Value);
        var all = Assert.IsType<List<CompanyBranchDto>>(Assert.IsType<OkObjectResult>((await controller.List(companyId, true, CancellationToken.None)).Result).Value);
        Assert.Empty(visible);
        Assert.Single(all);
    }

    [Fact]
    public async Task EffectiveRequisites_ShowInheritedAndOverriddenValues()
    {
        var (db, companyId) = await CreateAsync();
        await using var _ = db;
        (await db.LegalEntities.SingleAsync()).Kpp = "770701001";
        await db.SaveChangesAsync();
        var controller = new CompanyBranchesController(db);
        var created = Assert.IsType<CompanyBranchDto>(
            Assert.IsType<CreatedResult>((await controller.Create(companyId, Request("Точка", r => r.Kpp = "780201001"), CancellationToken.None)).Result).Value);

        var action = await controller.GetEffectiveRequisites(companyId, created.Id, CancellationToken.None);

        var snapshot = Assert.IsType<CompanyEffectiveRequisitesResponse>(Assert.IsType<OkObjectResult>(action.Result).Value).Requisites;
        Assert.Equal("780201001", snapshot.Kpp);
        Assert.Equal("7707083893", snapshot.Inn);
    }
}
