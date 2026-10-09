using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SellerLegalEntityComposerTests
{
    private static LegalEntity Company() => new()
    {
        Id = 5,
        ShortName = "ООО Своя",
        Inn = "7707083893",
        Kpp = "770701001",
        Address = "Москва",
        TaxSystem = "УСН",
        DocumentDirectorJson = "{\"lastName\":\"Иванов\"}",
        BankAccountsJson = "[{\"id\":\"main\",\"bank\":\"Банк А\",\"accountNumber\":\"1\",\"isPrimary\":true},{\"id\":\"second\",\"bank\":\"Банк Б\",\"accountNumber\":\"2\"}]",
        SealDataUrl = "data:image/png;base64,QUFB",
    };

    [Fact]
    public void Compose_WithoutBranch_KeepsCompanyValues()
    {
        var seller = SellerLegalEntityComposer.Compose(Company(), null);

        Assert.Equal("770701001", seller.Kpp);
        Assert.Equal("Москва", seller.Address);
        Assert.Contains("\"main\"", seller.BankAccountsJson);
        Assert.DoesNotContain("\"second\"", seller.BankAccountsJson);
        Assert.Equal("УСН", seller.TaxSystem);
    }

    [Fact]
    public void Compose_WithBranch_UsesOnlyFilledOverridesAndSelectedAccount()
    {
        var branch = new CompanyBranch { LegalEntityId = 5, Name = "Офис", Kpp = "780201001", BankAccountId = "second" };

        var seller = SellerLegalEntityComposer.Compose(Company(), branch);

        Assert.Equal("780201001", seller.Kpp);
        Assert.Equal("Москва", seller.Address);
        Assert.Equal("{\"lastName\":\"Иванов\"}", seller.DocumentDirectorJson);
        Assert.Contains("\"second\"", seller.BankAccountsJson);
        Assert.DoesNotContain("\"main\"", seller.BankAccountsJson);
    }

    [Fact]
    public void Snapshot_RoundTripsTextFieldsAndTakesImagesFromLiveCompany()
    {
        var seller = SellerLegalEntityComposer.Compose(Company(), null);
        var json = SellerLegalEntityComposer.ToSnapshotJson(seller);

        Assert.DoesNotContain("data:image", json);
        var restored = SellerLegalEntityComposer.FromSnapshot(json, Company())!;
        Assert.Equal("770701001", restored.Kpp);
        Assert.Equal("data:image/png;base64,QUFB", restored.SealDataUrl);
        Assert.Null(SellerLegalEntityComposer.FromSnapshot(json, null)!.SealDataUrl);
        Assert.Null(SellerLegalEntityComposer.FromSnapshot("not json", Company()));
        Assert.Null(SellerLegalEntityComposer.FromSnapshot(null, Company()));
    }

    [Fact]
    public void Compose_RejectsBranchOfAnotherCompany()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SellerLegalEntityComposer.Compose(Company(), new CompanyBranch { LegalEntityId = 6, Name = "Чужой" }));
    }
}

public class DocumentSellerServiceTests
{
    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<LegalEntity> AddCompanyAsync(PaymentContext db, string name, bool own, string status = CompanyProfileStatuses.Active, string? kpp = "770701001")
    {
        var company = new LegalEntity { ShortName = name, Inn = "7707083893", Kpp = kpp, ProfileStatus = status };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        if (own)
        {
            var existing = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == "legalEntities.mainCompanyIds");
            var ids = existing is null ? new List<int>() : System.Text.Json.JsonSerializer.Deserialize<List<int>>(existing.Value)!;
            ids.Add(company.Id);
            if (existing is null)
            {
                db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(ids) });
            }
            else
            {
                existing.Value = System.Text.Json.JsonSerializer.Serialize(ids);
            }

            await db.SaveChangesAsync();
        }

        return company;
    }

    [Fact]
    public async Task ValidateSelection_AcceptsOnlyActiveOwnCompanyAndItsActiveBranch()
    {
        await using var db = await CreateContextAsync();
        var own = await AddCompanyAsync(db, "Своя", own: true);
        var foreign = await AddCompanyAsync(db, "Чужая", own: false);
        var draft = await AddCompanyAsync(db, "Черновик", own: true, status: CompanyProfileStatuses.Draft);
        var branch = new CompanyBranch { LegalEntityId = own.Id, Name = "Офис" };
        var archivedBranch = new CompanyBranch { LegalEntityId = own.Id, Name = "Старый", Status = CompanyProfileStatuses.Archived };
        var foreignBranch = new CompanyBranch { LegalEntityId = foreign.Id, Name = "Офис" };
        db.CompanyBranches.AddRange(branch, archivedBranch, foreignBranch);
        await db.SaveChangesAsync();
        var service = new DocumentSellerService(db);

        Assert.Null(await service.ValidateSelectionAsync(null, null, default));
        Assert.Null(await service.ValidateSelectionAsync(own.Id, null, default));
        Assert.Null(await service.ValidateSelectionAsync(own.Id, branch.Id, default));
        Assert.NotNull(await service.ValidateSelectionAsync(null, branch.Id, default));
        Assert.NotNull(await service.ValidateSelectionAsync(foreign.Id, null, default));
        Assert.NotNull(await service.ValidateSelectionAsync(draft.Id, null, default));
        Assert.NotNull(await service.ValidateSelectionAsync(own.Id, archivedBranch.Id, default));
        Assert.NotNull(await service.ValidateSelectionAsync(own.Id, foreignBranch.Id, default));
        Assert.NotNull(await service.ValidateSelectionAsync(own.Id, 9999, default));
    }

    [Fact]
    public async Task Resolve_UsesSnapshotOnceFrozen_EvenAfterCompanyChanges()
    {
        await using var db = await CreateContextAsync();
        var company = await AddCompanyAsync(db, "Своя", own: true);
        var service = new DocumentSellerService(db);
        var act = new Act { Number = "A-1" };

        var preview = await service.ResolveAsync(act, company, freeze: false, default);
        Assert.Equal("770701001", preview!.Kpp);
        Assert.Null(act.SellerSnapshotJson);

        var frozen = await service.ResolveAsync(act, company, freeze: true, default);
        Assert.Equal("770701001", frozen!.Kpp);
        Assert.NotNull(act.SellerSnapshotJson);
        Assert.Equal(company.Id, act.SellerCompanyId);

        var tracked = await db.LegalEntities.SingleAsync(le => le.Id == company.Id);
        tracked.Kpp = "999999999";
        await db.SaveChangesAsync();
        var later = await service.ResolveAsync(act, tracked, freeze: true, default);
        Assert.Equal("770701001", later!.Kpp);
    }

    [Fact]
    public async Task Resolve_IgnoresStaleSelection_AndNeverFreezesIt()
    {
        await using var db = await CreateContextAsync();
        var defaultCompany = await AddCompanyAsync(db, "По умолчанию", own: true, kpp: "111111111");
        var removed = await AddCompanyAsync(db, "Убрана из собственных", own: false, kpp: "222222222");
        var service = new DocumentSellerService(db);
        var act = new Act { SellerCompanyId = removed.Id };

        var seller = await service.ResolveAsync(act, defaultCompany, freeze: true, default);

        Assert.Equal("111111111", seller!.Kpp);
        Assert.Equal(defaultCompany.Id, act.SellerCompanyId);
        Assert.Contains("111111111", act.SellerSnapshotJson);
    }

    [Fact]
    public async Task Snapshot_KeepsOnlyChosenAccount_SoItStaysSmallForManyAccounts()
    {
        await using var db = await CreateContextAsync();
        var company = await AddCompanyAsync(db, "Много счетов", own: true);
        var accounts = Enumerable.Range(1, 40).Select(i =>
            $"{{\"id\":\"a{i}\",\"bank\":\"Банк {i}\",\"bik\":\"044525225\",\"accountNumber\":\"4070281020000000{i:0000}\"}}");
        company.BankAccountsJson = "[" + string.Join(",", accounts) + "]";
        await db.SaveChangesAsync();
        var service = new DocumentSellerService(db);
        var act = new Act();

        await service.ResolveAsync(act, company, freeze: true, default);

        Assert.NotNull(act.SellerSnapshotJson);
        Assert.True(act.SellerSnapshotJson!.Length < 3000);
        Assert.Contains("a1", act.SellerSnapshotJson);
        Assert.DoesNotContain("a2\\\"", act.SellerSnapshotJson);
    }

    [Fact]
    public async Task Resolve_ChosenCompanyWinsOverDefault_AndReturnsDefaultWhenNothingConfigured()
    {
        await using var db = await CreateContextAsync();
        var defaultCompany = await AddCompanyAsync(db, "По умолчанию", own: true, kpp: "111111111");
        var chosen = await AddCompanyAsync(db, "Выбранная", own: true, kpp: "222222222");
        var service = new DocumentSellerService(db);

        var viaChoice = await service.ResolveAsync(new Act { SellerCompanyId = chosen.Id }, defaultCompany, false, default);
        var viaDefault = await service.ResolveAsync(new Act(), defaultCompany, false, default);
        var none = await service.ResolveAsync(new Act(), null, false, default);

        Assert.Equal("222222222", viaChoice!.Kpp);
        Assert.Equal("111111111", viaDefault!.Kpp);
        Assert.Null(none);
    }
}

public class ActSellerEndpointTests
{
    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private static async Task<(PaymentContext Db, LegalEntity Company, ActsController Controller)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var company = new LegalEntity { ShortName = "ООО Своя", Inn = "7707083893", Kpp = "770701001" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.defaultMainCompanyId", Value = company.Id.ToString() });
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "admin")], "Test")),
                },
            },
        };
        return (db, company, controller);
    }

    private static async Task<Act> AddActAsync(PaymentContext db)
    {
        var act = new Act { Number = "ACT-SELLER-1", Date = new DateTime(2026, 10, 9), Amount = 1000m };
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        return act;
    }

    [Fact]
    public async Task SetSeller_SavesSelection_RejectsForeignCompany_AndLocksAfterFirstExport()
    {
        var (db, company, controller) = await CreateAsync();
        await using var _ = db;
        var act = await AddActAsync(db);

        var ok = await controller.SetSeller(act.Id, new DocumentSellerRequest { CompanyId = company.Id }, default);
        Assert.IsType<OkObjectResult>(ok.Result);
        Assert.Equal(company.Id, (await db.Acts.AsNoTracking().SingleAsync()).SellerCompanyId);

        var foreign = await controller.SetSeller(act.Id, new DocumentSellerRequest { CompanyId = 999 }, default);
        Assert.IsType<BadRequestObjectResult>(foreign.Result);

        // Просмотр (html) реквизиты не фиксирует.
        await controller.GetDocument(act.Id, "html", false, default);
        Assert.Null((await db.Acts.AsNoTracking().SingleAsync()).SellerSnapshotJson);

        // Выдача файла фиксирует их; после этого продавца менять нельзя, а правки профиля документ не меняют.
        await controller.GetDocument(act.Id, "word", false, default);
        var frozen = await db.Acts.AsNoTracking().SingleAsync();
        Assert.NotNull(frozen.SellerSnapshotJson);
        Assert.Contains("770701001", frozen.SellerSnapshotJson);

        var locked = await controller.SetSeller(act.Id, new DocumentSellerRequest { CompanyId = null }, default);
        Assert.IsType<ConflictObjectResult>(locked.Result);

        (await db.LegalEntities.SingleAsync()).Kpp = "999999999";
        await db.SaveChangesAsync();
        var afterEdit = await controller.GetDocument(act.Id, "html", false, default);
        var html = Assert.IsType<ContentResult>(afterEdit).Content!;
        Assert.Contains("770701001", html);
        Assert.DoesNotContain("999999999", html);
    }
}

public class OwnCompaniesControllerTests
{
    [Fact]
    public async Task GetOptions_ReturnsOnlyActiveOwnCompaniesWithActiveBranches_AndMarksDefault()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var first = new LegalEntity { ShortName = "Б Компания" };
        var second = new LegalEntity { ShortName = "А Компания" };
        var draft = new LegalEntity { ShortName = "Черновик", ProfileStatus = CompanyProfileStatuses.Draft };
        var foreign = new LegalEntity { ShortName = "Чужая" };
        db.LegalEntities.AddRange(first, second, draft, foreign);
        await db.SaveChangesAsync();
        db.CompanyBranches.AddRange(
            new CompanyBranch { LegalEntityId = first.Id, Name = "Офис" },
            new CompanyBranch { LegalEntityId = first.Id, Name = "Архив", Status = CompanyProfileStatuses.Archived });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{first.Id},{second.Id},{draft.Id}]" });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.defaultMainCompanyId", Value = first.Id.ToString() });
        await db.SaveChangesAsync();

        var result = await new OwnCompaniesController(db).GetOptions(default);

        var options = Assert.IsType<List<OwnCompanyOption>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["А Компания", "Б Компания"], options.Select(o => o.ShortName).ToArray());
        Assert.False(options[0].IsDefault);
        Assert.True(options[1].IsDefault);
        Assert.Equal(["Офис"], options[1].Branches.Select(b => b.Name).ToArray());
        Assert.Empty(options[0].Branches);
    }

    [Fact]
    public async Task GetOptions_ReturnsEmptyList_WhenNoOwnCompaniesConfigured()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var result = await new OwnCompaniesController(db).GetOptions(default);

        Assert.Empty(Assert.IsType<List<OwnCompanyOption>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }
}
