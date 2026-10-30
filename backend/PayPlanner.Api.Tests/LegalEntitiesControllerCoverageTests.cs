using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public class LegalEntitiesControllerCoverageTests
{
    [Fact]
    public async Task CleanEndpoints_ValidateInputs_HandleNotFound_AndMapResponses()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService
            {
                AddressResults =
                {
                    ["Самара, Миронова, 9"] = new CleanedAddress(
                        Source: "Самара, Миронова, 9",
                        Result: "446200, Самарская обл, г Новокуйбышевск, ул Миронова, д 9",
                        PostalCode: "446200",
                        Country: "Россия",
                        CountryIsoCode: "RU",
                        FederalDistrict: null,
                        RegionWithType: "Самарская обл",
                        AreaWithType: "Безенчукский р-н",
                        CityWithType: "г Новокуйбышевск",
                        SettlementWithType: null,
                        StreetWithType: "ул Миронова",
                        House: "9",
                        Block: null,
                        Flat: null,
                        FiasId: "fias-1",
                        FiasLevel: null,
                        KladrId: "kladr-1",
                        Okato: null,
                        Oktmo: null,
                        TaxOffice: null,
                        TaxOfficeLegal: null,
                        GeoLat: "53.095",
                        GeoLon: "49.945",
                        Qc: 0,
                        QcComplete: 0,
                        QcHouse: 0,
                        QcGeo: 0,
                        UnparsedParts: null,
                        Timezone: null),
                },
                EmailResults =
                {
                    ["docs@example.com"] = new CleanedEmail("docs@example.com", "PRIVATE", 0),
                },
                PhoneResults =
                {
                    ["+79270000000"] = new CleanedPhone("+79270000000", "Россия", "Самара", "МТС", 0),
                },
            },
            currentUserId: 1);

        Assert.IsType<BadRequestObjectResult>((await controller.CleanAddress(new CleanAddressRequest(" "), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.CleanAddress(new CleanAddressRequest("Не найден"), CancellationToken.None)).Result);

        var addressOk = Assert.IsType<OkObjectResult>(
            (await controller.CleanAddress(new CleanAddressRequest("Самара, Миронова, 9"), CancellationToken.None)).Result);
        var addressDto = Assert.IsType<CleanedAddressDto>(addressOk.Value);
        Assert.Equal("446200", addressDto.PostalCode);
        Assert.Equal("г Новокуйбышевск", addressDto.CityWithType);

        Assert.IsType<BadRequestObjectResult>((await controller.CleanEmail(new CleanEmailRequest(" "), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.CleanEmail(new CleanEmailRequest("missing@example.com"), CancellationToken.None)).Result);

        var emailOk = Assert.IsType<OkObjectResult>(
            (await controller.CleanEmail(new CleanEmailRequest("docs@example.com"), CancellationToken.None)).Result);
        var emailDto = Assert.IsType<CleanedEmailDto>(emailOk.Value);
        Assert.Equal("docs@example.com", emailDto.Email);
        Assert.Equal("PRIVATE", emailDto.Type);

        Assert.IsType<BadRequestObjectResult>((await controller.CleanPhone(new CleanPhoneRequest(" "), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.CleanPhone(new CleanPhoneRequest("+79990000000"), CancellationToken.None)).Result);

        var phoneOk = Assert.IsType<OkObjectResult>(
            (await controller.CleanPhone(new CleanPhoneRequest("+79270000000"), CancellationToken.None)).Result);
        var phoneDto = Assert.IsType<CleanedPhoneDto>(phoneOk.Value);
        Assert.Equal("+79270000000", phoneDto.Phone);
        Assert.Equal("Самара", phoneDto.City);
    }

    [Fact]
    public async Task FindById_ValidatesQuery_AndMapsDetails()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService
            {
                DetailsByQuery =
                {
                    ["633009620345"] = new LegalEntityDetails(
                        ShortName: "ИП Кожеваткин А.В.",
                        FullName: "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
                        Inn: "633009620345",
                        Kpp: null,
                        Ogrn: "1234567890123",
                        OpfShort: "ИП",
                        OpfFull: "Индивидуальный предприниматель",
                        Status: "ACTIVE",
                        RegistrationDate: new DateTimeOffset(new DateTime(2026, 4, 10), TimeSpan.Zero),
                        LiquidationDate: null,
                        Okved: "69.10",
                        OkvedType: "Основной",
                        BranchCount: 0,
                        ManagementName: "Кожеваткин Андрей Владимирович",
                        ManagementPost: "Предприниматель",
                        AddressValue: "Самара, Миронова, 9",
                        AddressUnrestrictedValue: "446200, Россия, Самарская обл, г Новокуйбышевск, ул Миронова, д 9",
                        Country: "Россия",
                        RegionWithType: "Самарская обл",
                        AreaWithType: "Безенчукский р-н",
                        CityWithType: "г Новокуйбышевск",
                        SettlementWithType: null,
                        StreetWithType: "ул Миронова",
                        House: "9",
                        Flat: null,
                        PostalCode: "446200",
                        FiasId: "fias-1",
                        KladrId: "kladr-1",
                        GeoLat: "53.095",
                        GeoLon: "49.945",
                        Okato: "36413000000",
                        Oktmo: "36713000",
                        Okpo: "12345678",
                        Okogu: "4210015",
                        Okfs: "16",
                        TaxSystem: "УСН",
                        Phones: ["+79270000000"],
                        Emails: ["docs@example.com"]),
                },
            },
            currentUserId: 1);

        Assert.IsType<BadRequestObjectResult>((await controller.FindById(new LegalEntityFindRequest(" ", null), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.FindById(new LegalEntityFindRequest("7700000000", null), CancellationToken.None)).Result);

        var ok = Assert.IsType<OkObjectResult>(
            (await controller.FindById(new LegalEntityFindRequest("633009620345", null), CancellationToken.None)).Result);
        var dto = Assert.IsType<LegalEntityDetailsDto>(ok.Value);
        Assert.Equal("ИП Кожеваткин А.В.", dto.ShortName);
        Assert.Equal("446200", dto.PostalCode);
        Assert.Equal("Кожеваткин Андрей Владимирович", dto.Director);
        Assert.Equal("+79270000000", dto.Phones.Single());
    }

    [Fact]
    public async Task GetCompanyProfile_UsesLegacyFallback_AndReportsMissingConfigurations()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateAdminUserAsync(db);
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: owner.Id);

        var notConfigured = await controller.GetCompanyProfile(CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(notConfigured.Result);

        db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = "legalEntities.mainCompanyIds",
                Value = "not-json",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new SystemSetting
            {
                Key = "legalEntities.mainCompanyId",
                Value = "999",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var missingEntity = await controller.GetCompanyProfile(CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missingEntity.Result);

        var entity = new LegalEntity
        {
            ShortName = "Головная компания",
            FullName = "Головная компания",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        var legacySetting = await db.SystemSettings.SingleAsync(item => item.Key == "legalEntities.mainCompanyId");
        legacySetting.Value = entity.Id.ToString();
        await db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>((await controller.GetCompanyProfile(CancellationToken.None)).Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);
        Assert.Equal(entity.Id, dto.Id);
    }

    [Fact]
    public async Task SetCompanyProfileMain_UpsertsSettings_AndReturnsNotFoundWhenMissing()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateAdminUserAsync(db);
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: owner.Id);

        Assert.IsType<NotFoundResult>((await controller.SetCompanyProfileMain(404, CancellationToken.None)).Result);

        var firstId = await CreateLegalEntityAsync(db, "Компания 1");
        var secondId = await CreateLegalEntityAsync(db, "Компания 2");

        Assert.IsType<OkObjectResult>((await controller.SetCompanyProfileMain(firstId, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.SetCompanyProfileMain(secondId, CancellationToken.None)).Result);

        var settings = await controller.GetMainCompanySettings(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(settings.Result);
        var dto = Assert.IsType<MainCompanySettingsDto>(ok.Value);

        Assert.Equal(secondId, dto.DefaultCompanyId);
        Assert.Equal(new[] { firstId, secondId }, dto.Companies.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task GetMainCompanySettings_FallsBackToFirstAvailableCompany_WhenDefaultIsInvalidOrMissing()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateAdminUserAsync(db);
        var firstId = await CreateLegalEntityAsync(db, "Компания A");
        var secondId = await CreateLegalEntityAsync(db, "Компания B");
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: owner.Id);

        db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = "legalEntities.mainCompanyIds",
                Value = JsonSerializer.Serialize(new[] { firstId, secondId }),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new SystemSetting
            {
                Key = "legalEntities.defaultMainCompanyId",
                Value = "999999",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var invalidDefault = await controller.GetMainCompanySettings(CancellationToken.None);
        var invalidDefaultOk = Assert.IsType<OkObjectResult>(invalidDefault.Result);
        var invalidDefaultDto = Assert.IsType<MainCompanySettingsDto>(invalidDefaultOk.Value);
        Assert.Equal(firstId, invalidDefaultDto.DefaultCompanyId);

        db.SystemSettings.RemoveRange(db.SystemSettings);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = JsonSerializer.Serialize(new[] { firstId, secondId }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var missingDefault = await controller.GetMainCompanySettings(CancellationToken.None);
        var missingDefaultOk = Assert.IsType<OkObjectResult>(missingDefault.Result);
        var missingDefaultDto = Assert.IsType<MainCompanySettingsDto>(missingDefaultOk.Value);
        Assert.Equal(firstId, missingDefaultDto.DefaultCompanyId);
    }

    [Fact]
    public async Task GetAll_GetPaged_AndGetById_HideMainCompanies_ForRegularUsers()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db);
        var regularUser = await CreateUserAsync(db, isActive: true);
        var mainCompanyId = await CreateLegalEntityAsync(db, "Main Company");
        var visibleId = await CreateLegalEntityAsync(db, "Searchable Service", responsibleUserId: regularUser.Id);
        var anotherId = await CreateLegalEntityAsync(db, "Vector");

        var adminController = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: admin.Id);
        await adminController.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                ExpectedVersion = await MainCompanySettingsVersionTestAccess.ReadAsync(adminController),
                CompanyIds = [mainCompanyId],
                DefaultCompanyId = mainCompanyId,
            },
            CancellationToken.None);

        var allAction = await adminController.GetAll("searchable", CancellationToken.None);
        var allOk = Assert.IsType<OkObjectResult>(allAction.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<LegalEntityListItemDto>>(allOk.Value).ToList();
        var item = Assert.Single(items);
        Assert.Equal(visibleId, item.Id);

        var pagedAction = await adminController.GetPaged(page: 1, pageSize: 1, search: null, ct: CancellationToken.None);
        var pagedOk = Assert.IsType<OkObjectResult>(pagedAction.Result);
        var paged = Assert.IsType<PagedResponse<LegalEntityListItemDto>>(pagedOk.Value);
        Assert.Equal(2, paged.Total);
        Assert.Single(paged.Items);
        Assert.DoesNotContain(paged.Items, candidate => candidate.Id == mainCompanyId);

        var pagedSearchAction = await adminController.GetPaged(page: 1, pageSize: 10, search: "searchable", ct: CancellationToken.None);
        var pagedSearchOk = Assert.IsType<OkObjectResult>(pagedSearchAction.Result);
        var pagedSearch = Assert.IsType<PagedResponse<LegalEntityListItemDto>>(pagedSearchOk.Value);
        var pagedSearchItem = Assert.Single(pagedSearch.Items);
        Assert.Equal(visibleId, pagedSearchItem.Id);

        var regularController = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: regularUser.Id, isAdmin: false);
        Assert.IsType<NotFoundResult>((await regularController.GetById(mainCompanyId, CancellationToken.None)).Result);

        var visibleOk = Assert.IsType<OkObjectResult>((await regularController.GetById(visibleId, CancellationToken.None)).Result);
        var visibleDto = Assert.IsType<LegalEntityDetailDto>(visibleOk.Value);
        Assert.Equal(visibleId, visibleDto.Id);

        Assert.IsType<NotFoundResult>((await regularController.GetById(anotherId, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await regularController.GetById(anotherId + 100, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetPaged_ReturnsContactCounters_ForVisiblePageOnly()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var targetLegalId = await CreateLegalEntityAsync(db, "Alpha Legal", inn: "7700000001");
        var otherLegalId = await CreateLegalEntityAsync(db, "Beta Legal", inn: "7700000002");

        var firstClient = new Client
        {
            Name = "Alpha Client 1",
            LegalEntityId = targetLegalId,
            IsActive = true,
        };
        var secondClient = new Client
        {
            Name = "Alpha Client 2",
            LegalEntityId = targetLegalId,
            IsActive = true,
        };
        var otherClient = new Client
        {
            Name = "Beta Client",
            LegalEntityId = otherLegalId,
            IsActive = true,
        };

        db.Clients.AddRange(firstClient, secondClient, otherClient);
        await db.SaveChangesAsync();

        db.ClientCases.AddRange(
            new ClientCase { LegalEntityId = targetLegalId, Title = "Case 1" },
            new ClientCase { LegalEntityId = targetLegalId, Title = "Case 2" },
            new ClientCase { LegalEntityId = otherLegalId, Title = "Other Case" });

        var contract = new Contract
        {
            Number = "C-1",
            Date = DateTime.UtcNow.Date,
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        db.LegalEntityContracts.Add(new LegalEntityContract { LegalEntityId = targetLegalId, ContractId = contract.Id });

        db.Acts.Add(new Act
        {
            LegalEntityId = targetLegalId,
            Number = "A-1",
            Date = DateTime.UtcNow.Date,
            Amount = 500m,
        });

        db.Payments.AddRange(
            new Payment
            {
                LegalEntityId = targetLegalId,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 1000m,
            },
            new Payment
            {
                ExpenseLegalEntityId = targetLegalId,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 2000m,
            },
            new Payment
            {
                LegalEntityId = targetLegalId,
                DocumentKind = DocumentKind.Payment,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 3000m,
            },
            new Payment
            {
                LegalEntityId = otherLegalId,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 4000m,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: 1);
        interceptor.Commands.Clear();

        var action = await controller.GetPaged(search: "alpha", page: 1, pageSize: 1, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var paged = Assert.IsType<PagedResponse<LegalEntityListItemDto>>(ok.Value);
        var item = Assert.Single(paged.Items);

        Assert.Equal(1, paged.Total);
        Assert.Equal(targetLegalId, item.Id);
        Assert.Equal(2, item.ClientsCount);
        Assert.Equal(2, item.CasesCount);
        Assert.Equal(1, item.ContractsCount);
        Assert.Equal(2, item.InvoicesCount);
        Assert.Equal(1, item.ActsCount);
        Assert.Equal(1, item.PaymentsCount);
        Assert.Equal(2, item.RelatedCases.Count);
        Assert.Single(item.RelatedContracts);
        Assert.Equal(2, item.RelatedInvoices.Count);
        Assert.Single(item.RelatedActs);
        Assert.Single(item.RelatedPayments);
        Assert.Contains(item.RelatedCases, link => link.Label == "Case 1");
        Assert.Contains(item.RelatedContracts, link => link.Label == "№C-1");
        Assert.Contains(item.RelatedActs, link => link.Label == "№A-1" && link.Amount == 500m);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"LegalEntityContracts\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"Acts\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPaged_HidesFinancialRelatedDocuments_WhenSectionViewAccessDenied()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var user = await CreateUserAsync(db, isActive: true);
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.LegalEntities,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Clients,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Accounts,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Acts,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Contracts,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Payments,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            });

        var legalEntityId = await CreateLegalEntityAsync(db, "Restricted Legal", inn: "7700000111");
        db.Clients.Add(new Client
        {
            Name = "Hidden client",
            Email = "hidden-client@example.com",
            Phone = "+70000000000",
            IsActive = true,
            LegalEntityId = legalEntityId,
        });
        var contract = new Contract
        {
            Number = "LC-SEC",
            Date = DateTime.UtcNow.Date,
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        db.LegalEntityContracts.Add(new LegalEntityContract
        {
            LegalEntityId = legalEntityId,
            ContractId = contract.Id,
        });
        db.ClientCases.Add(new ClientCase
        {
            LegalEntityId = legalEntityId,
            Title = "Hidden legal case",
            Status = ClientCaseStatus.Open,
        });
        db.Acts.Add(new Act
        {
            LegalEntityId = legalEntityId,
            Number = "LA-SEC",
            Date = DateTime.UtcNow.Date,
            Amount = 500m,
        });
        db.Payments.AddRange(
            new Payment
            {
                LegalEntityId = legalEntityId,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 1000m,
            },
            new Payment
            {
                LegalEntityId = legalEntityId,
                DocumentKind = DocumentKind.Payment,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Date = DateTime.UtcNow.Date,
                Amount = 2000m,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService(),
            currentUserId: user.Id,
            isAdmin: false);
        interceptor.Commands.Clear();

        var action = await controller.GetPaged(search: "Restricted", page: 1, pageSize: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<PagedResponse<LegalEntityListItemDto>>(ok.Value);
        var item = Assert.Single(page.Items);

        Assert.Equal(legalEntityId, item.Id);
        Assert.Equal(0, item.ClientsCount);
        Assert.Equal(0, item.CasesCount);
        Assert.Equal(0, item.ContractsCount);
        Assert.Equal(0, item.InvoicesCount);
        Assert.Equal(0, item.ActsCount);
        Assert.Equal(0, item.PaymentsCount);
        Assert.Empty(item.Clients);
        Assert.Empty(item.RelatedCases);
        Assert.Empty(item.RelatedContracts);
        Assert.Empty(item.RelatedInvoices);
        Assert.Empty(item.RelatedActs);
        Assert.Empty(item.RelatedPayments);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"RolePermissions\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetById_HidesLinkedClients_WhenClientsViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, isActive: true);
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.LegalEntities,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Clients,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            });

        var legalEntityId = await CreateLegalEntityAsync(db, "Client Hidden Legal", inn: "7700000222");
        db.Clients.Add(new Client
        {
            Name = "Sensitive Client",
            Email = "sensitive@example.com",
            Phone = "+79990000000",
            IsActive = true,
            LegalEntityId = legalEntityId,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService(),
            currentUserId: user.Id,
            isAdmin: false);

        var action = await controller.GetById(legalEntityId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var item = Assert.IsType<LegalEntityDetailDto>(ok.Value);

        Assert.Equal(legalEntityId, item.Id);
        Assert.Equal(0, item.ClientsCount);
        Assert.Empty(item.Clients);
    }

    [Fact]
    public async Task GetPaged_DeduplicatesRelatedInvoices_WithSameVisibleNumber()
    {
        await using var db = await CreateContextAsync();
        var legalEntityId = await CreateLegalEntityAsync(db, "Zemtsov Legal", inn: "7700000999");
        var invoice = new Invoice
        {
            Number = "8",
            Date = new DateTime(2026, 2, 2),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var canonicalInvoiceRow = new Payment
        {
            LegalEntityId = legalEntityId,
            InvoiceId = invoice.Id,
            DocumentKind = DocumentKind.Invoice,
            Account = "8",
            AccountDate = invoice.Date,
            Date = new DateTime(2026, 2, 5),
            PlannedDate = new DateTime(2026, 2, 5),
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 25000m,
        };

        db.Payments.AddRange(
            canonicalInvoiceRow,
            new Payment
            {
                LegalEntityId = legalEntityId,
                DocumentKind = DocumentKind.Invoice,
                Account = "8",
                AccountDate = invoice.Date,
                Date = new DateTime(2026, 2, 5),
                PlannedDate = new DateTime(2026, 2, 5),
                Type = PaymentType.Income,
                Status = PaymentStatus.Cancelled,
                Amount = 100m,
            },
            new Payment
            {
                LegalEntityId = legalEntityId,
                DocumentKind = DocumentKind.Invoice,
                LegacyAccountNumber = "8",
                LegacyAccountDate = invoice.Date,
                Date = new DateTime(2026, 2, 5),
                PlannedDate = new DateTime(2026, 2, 5),
                Type = PaymentType.Income,
                Status = PaymentStatus.Cancelled,
                Amount = 25000m,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: 1);

        var action = await controller.GetPaged(search: "Zemtsov", page: 1, pageSize: 25, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<PagedResponse<LegalEntityListItemDto>>(ok.Value);
        var item = Assert.Single(page.Items);
        var relatedInvoice = Assert.Single(item.RelatedInvoices);

        Assert.Equal(1, item.InvoicesCount);
        Assert.Equal(canonicalInvoiceRow.Id, relatedInvoice.Id);
        Assert.Contains("8", relatedInvoice.Label);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_ReturnsDirectLegalEntityCaseTotals()
    {
        await using var db = await CreateContextAsync();
        var legalEntityId = await CreateLegalEntityAsync(db, "Alpha Legal", inn: "7700000101");
        var otherLegalEntityId = await CreateLegalEntityAsync(db, "Beta Legal", inn: "7700000102");

        var firstCase = new ClientCase
        {
            LegalEntityId = legalEntityId,
            Title = "Case 1",
            Status = ClientCaseStatus.Open,
        };
        var secondCase = new ClientCase
        {
            LegalEntityId = legalEntityId,
            Title = "Case 2",
            Status = ClientCaseStatus.Closed,
        };
        var otherCase = new ClientCase
        {
            LegalEntityId = otherLegalEntityId,
            Title = "Other Case",
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.AddRange(firstCase, secondCase, otherCase);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                LegalEntityId = legalEntityId,
                ClientCaseId = firstCase.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                PlannedDate = DateTime.UtcNow.Date,
                Amount = 1000m,
                PaidAmount = 400m,
            },
            new Payment
            {
                LegalEntityId = otherLegalEntityId,
                ExpenseLegalEntityId = legalEntityId,
                ClientCaseId = firstCase.Id,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                PlannedDate = DateTime.UtcNow.Date,
                Amount = 250m,
                PaidAmount = 100m,
            },
            new Payment
            {
                LegalEntityId = otherLegalEntityId,
                ClientCaseId = otherCase.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                PlannedDate = DateTime.UtcNow.Date,
                Amount = 900m,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: 1);

        var action = await controller.GetCaseFinanceSummaries(legalEntityId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summaries = Assert.IsAssignableFrom<IEnumerable<ClientCaseFinanceSummaryDto>>(ok.Value).ToList();
        var firstSummary = Assert.Single(summaries, item => item.CaseId == firstCase.Id);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(1000m, firstSummary.IncomeTotal);
        Assert.Equal(400m, firstSummary.IncomePaid);
        Assert.Equal(600m, firstSummary.IncomeOutstanding);
        Assert.Equal(250m, firstSummary.ExpenseTotal);
        Assert.Equal(100m, firstSummary.ExpensePaid);
        Assert.Equal(150m, firstSummary.ExpenseOutstanding);
        Assert.Equal(750m, firstSummary.PlannedMargin);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"legal-finance-denied-{Guid.NewGuid():N}",
            Description = "Legal finance denied",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.LegalEntities,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
                },
                new RolePermission
                {
                    Section = RolePermissionCatalog.Payments,
                    CanView = false,
                    ViewScope = DataVisibilityScope.All,
                },
            ],
        };
        var user = new User
        {
            Email = $"legal-finance-denied-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Legal finance denied",
            Role = role,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var legalEntityId = await CreateLegalEntityAsync(db, "Denied Legal", inn: "7700000199");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService(),
            currentUserId: user.Id,
            isAdmin: false);

        var action = await controller.GetCaseFinanceSummaries(legalEntityId, CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_HidesCases_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"legal-finance-no-cases-{Guid.NewGuid():N}",
            Description = "Legal finance without cases",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.LegalEntities,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
                },
                new RolePermission
                {
                    Section = RolePermissionCatalog.Cases,
                    CanView = false,
                    ViewScope = DataVisibilityScope.All,
                },
                new RolePermission
                {
                    Section = RolePermissionCatalog.Payments,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
                },
            ],
        };
        var user = new User
        {
            Email = $"legal-finance-no-cases-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Legal finance without cases",
            Role = role,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var legalEntityId = await CreateLegalEntityAsync(db, "Hidden Cases Legal", inn: "7700000299");
        var clientCase = new ClientCase
        {
            LegalEntityId = legalEntityId,
            Title = "Hidden case finance",
            Status = ClientCaseStatus.Open,
        };
        db.Users.Add(user);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            LegalEntityId = legalEntityId,
            ClientCaseId = clientCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Amount = 1000m,
            PaidAmount = 400m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService(),
            currentUserId: user.Id,
            isAdmin: false);

        var action = await controller.GetCaseFinanceSummaries(legalEntityId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summaries = Assert.IsAssignableFrom<IEnumerable<ClientCaseFinanceSummaryDto>>(ok.Value);
        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_HidesPaymentsOutsidePaymentVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"legal-finance-own-payments-{Guid.NewGuid():N}",
            Description = "Legal finance own payments",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.LegalEntities,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
                },
                new RolePermission
                {
                    Section = RolePermissionCatalog.Cases,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
                },
                new RolePermission
                {
                    Section = RolePermissionCatalog.Payments,
                    CanView = true,
                    ViewScope = DataVisibilityScope.Own,
                },
            ],
        };
        var user = new User
        {
            Email = $"legal-finance-own-payments-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Legal finance own payments",
            Role = role,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var legalEntityId = await CreateLegalEntityAsync(db, "Hidden Payments Legal", inn: "7700000399");
        var clientCase = new ClientCase
        {
            LegalEntityId = legalEntityId,
            Title = "Visible case without visible payments",
            Status = ClientCaseStatus.Open,
        };
        db.Users.Add(user);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            LegalEntityId = legalEntityId,
            ClientCaseId = clientCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Amount = 1000m,
            PaidAmount = 400m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService(),
            currentUserId: user.Id,
            isAdmin: false);

        var action = await controller.GetCaseFinanceSummaries(legalEntityId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ClientCaseFinanceSummaryDto>>(ok.Value));
        Assert.Equal(clientCase.Id, summary.CaseId);
        Assert.Equal(0m, summary.IncomeTotal);
        Assert.Equal(0m, summary.IncomePaid);
        Assert.Equal(0m, summary.IncomeOutstanding);
        Assert.Equal(0, summary.IncomeCount);
    }

    [Fact]
    public async Task Suggest_RejectsEmptyInput()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: 1);

        var action = await controller.Suggest(new LegalEntitySuggestionRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Create_ValidatesShortNameResponsibleInnAndClientIds()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db);
        await CreateLegalEntityAsync(db, "Дубликат ИНН", inn: "633009620345");
        var activeResponsible = await CreateUserAsync(db, isActive: true);
        var inactiveResponsible = await CreateUserAsync(db, isActive: false);
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: admin.Id);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.Create(new LegalEntityRequest { ShortName = " " }, CancellationToken.None)).Result);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.Create(
                new LegalEntityRequest
                {
                    ShortName = "ООО Тест",
                    ResponsibleUserId = inactiveResponsible.Id,
                },
                CancellationToken.None)).Result);

        Assert.IsType<ConflictObjectResult>(
            (await controller.Create(
                new LegalEntityRequest
                {
                    ShortName = "ООО Тест",
                    ResponsibleUserId = activeResponsible.Id,
                    Inn = "633009620345",
                },
                CancellationToken.None)).Result);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.Create(
                new LegalEntityRequest
                {
                    ShortName = "ООО Тест",
                    ResponsibleUserId = activeResponsible.Id,
                    Inn = "7700000000",
                    ClientIds = [999],
                },
                CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenFormRequirementsReportMissingFields()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService(),
            currentUserId: admin.Id,
            formRequirementsService: new StubFormRequirementsService(["ShortName", "Inn"]));

        var action = await controller.Create(new LegalEntityRequest { ShortName = "ООО Проверка" }, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
        Assert.Equal("ShortName", json.RootElement.GetProperty("missingFields")[0].GetString());
        Assert.Equal("Inn", json.RootElement.GetProperty("missingFields")[1].GetString());
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenInlineAssetPayloadIsTooLarge()
    {
        await using var db = await CreateContextAsync();
        var activeResponsible = await CreateUserAsync(db, isActive: true);
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: 1);

        var action = await controller.Create(
            new LegalEntityRequest
            {
                ShortName = "ООО Актив",
                ResponsibleUserId = activeResponsible.Id,
                LogoDataUrl = "data:image/png;base64," + new string('A', 350_001),
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Update_ValidatesExistenceResponsibleInnAndClientIds()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db);
        var activeResponsible = await CreateUserAsync(db, isActive: true);
        var inactiveResponsible = await CreateUserAsync(db, isActive: false);
        var entityId = await CreateLegalEntityAsync(db, "Обновляемая компания", inn: "7700000001");
        await CreateLegalEntityAsync(db, "Компания-дубль", inn: "7700000002");
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: admin.Id);

        Assert.IsType<NotFoundResult>(
            (await controller.Update(404, new LegalEntityRequest { ShortName = "Нет" }, CancellationToken.None)).Result);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.Update(entityId, new LegalEntityRequest { ShortName = " " }, CancellationToken.None)).Result);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.Update(
                entityId,
                new LegalEntityRequest
                {
                    ShortName = "ООО Обновление",
                    ResponsibleUserId = inactiveResponsible.Id,
                },
                CancellationToken.None)).Result);

        Assert.IsType<ConflictObjectResult>(
            (await controller.Update(
                entityId,
                new LegalEntityRequest
                {
                    ShortName = "ООО Обновление",
                    ResponsibleUserId = activeResponsible.Id,
                    Inn = "7700000002",
                },
                CancellationToken.None)).Result);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.Update(
                entityId,
                new LegalEntityRequest
                {
                    ShortName = "ООО Обновление",
                    ResponsibleUserId = activeResponsible.Id,
                    Inn = "7700000099",
                    ClientIds = [999],
                },
                CancellationToken.None)).Result);
    }

    [Fact]
    public async Task UpdateMainCompanySettings_ReportsMissingIds_AndCanClearStoredState()
    {
        await using var db = await CreateContextAsync();
        var firstCompanyId = await CreateLegalEntityAsync(db, "Компания 1");
        var secondCompanyId = await CreateLegalEntityAsync(db, "Компания 2");
        var userId = (await CreateUserAsync(db, isActive: true)).Id;
        var owner = await CreateAdminUserAsync(db);
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: owner.Id);

        var missingAction = await controller.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                ExpectedVersion = await MainCompanySettingsVersionTestAccess.ReadAsync(controller),
                CompanyIds = [firstCompanyId, 999],
                DefaultCompanyId = firstCompanyId,
            },
            CancellationToken.None);
        var missing = Assert.IsType<NotFoundObjectResult>(missingAction.Result);
        using (var missingJson = JsonDocument.Parse(JsonSerializer.Serialize(missing.Value)))
        {
            Assert.Equal(999, missingJson.RootElement.GetProperty("missingIds")[0].GetInt32());
        }

        db.UserCompanyAccesses.AddRange(
            new UserCompanyAccess { UserId = userId, LegalEntityId = firstCompanyId },
            new UserCompanyAccess { UserId = userId, LegalEntityId = secondCompanyId });
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(
            (await controller.UpdateMainCompanySettings(
                new UpdateMainCompanySettingsRequest
                {
                    ExpectedVersion = await MainCompanySettingsVersionTestAccess.ReadAsync(controller),
                    CompanyIds = [firstCompanyId, secondCompanyId],
                    DefaultCompanyId = secondCompanyId,
                },
                CancellationToken.None)).Result);

        var cleared = await controller.UpdateMainCompanySettings(new UpdateMainCompanySettingsRequest { ExpectedVersion = await MainCompanySettingsVersionTestAccess.ReadAsync(controller) }, CancellationToken.None);
        var clearedOk = Assert.IsType<OkObjectResult>(cleared.Result);
        var dto = Assert.IsType<MainCompanySettingsDto>(clearedOk.Value);
        Assert.Empty(dto.Companies);
        Assert.Null(dto.DefaultCompanyId);
        Assert.Equal(0, await db.UserCompanyAccesses.CountAsync());
        Assert.Equal(0, await db.SystemSettings.CountAsync(item =>
            item.Key == "legalEntities.mainCompanyId"
            || item.Key == "legalEntities.mainCompanyIds"
            || item.Key == "legalEntities.defaultMainCompanyId"));
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenEntityMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: 1);

        var action = await controller.Delete(404, CancellationToken.None);
        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task MigrateLegacyAddresses_ProcessesAddsUpdatesDuplicatesAndFallbackSuggestions()
    {
        await using var db = await CreateContextAsync();
        var addEntity = await CreateLegalEntityAsync(db, "Добавление", address: "г. Самара, ул. Добавление 1");
        var duplicateEntity = await CreateLegalEntityAsync(db, "Дубль", address: "г. Самара, ул. Дубль 2");
        var fallbackEntity = await CreateLegalEntityAsync(db, "Подсказка", address: "г. Самара, ул. Подсказка 4");

        var updatedClientId = await CreateClientAsync(db, "Обновление", "г. Самара, ул. Обновление 3");
        var emptyClientId = await CreateClientAsync(db, "Пустой", "   ");
        var unmatchedClientId = await CreateClientAsync(db, "Без совпадения", "г. Самара, ул. Без совпадения 5");

        db.AddressRecords.AddRange(
            new AddressRecord
            {
                LegalEntityId = duplicateEntity,
                Type = AddressType.Legal,
                Value = "г. Самара, ул. Дубль 2",
                UnrestrictedValue = "г. Самара, ул. Дубль 2",
                PostalCode = "446200",
                Country = "Россия",
                RegionWithType = "Самарская обл",
                CityWithType = "г. Самара",
                StreetWithType = "ул. Дубль",
                House = "2",
                IsPrimary = true,
            },
            new AddressRecord
            {
                ClientId = updatedClientId,
                Type = AddressType.Registration,
                Value = "г. Самара, ул. Обновление 3",
                IsPrimary = true,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService
            {
                AddressResults =
                {
                    ["г. Самара, ул. Добавление 1"] = new CleanedAddress(
                        Source: "г. Самара, ул. Добавление 1",
                        Result: "г. Самара, ул. Добавление 1",
                        PostalCode: "446201",
                        Country: "Россия",
                        CountryIsoCode: "RU",
                        FederalDistrict: null,
                        RegionWithType: "Самарская обл",
                        AreaWithType: null,
                        CityWithType: "г. Самара",
                        SettlementWithType: null,
                        StreetWithType: "ул. Добавление",
                        House: "1",
                        Block: null,
                        Flat: null,
                        FiasId: "fias-add",
                        FiasLevel: null,
                        KladrId: "kladr-add",
                        Okato: null,
                        Oktmo: null,
                        TaxOffice: null,
                        TaxOfficeLegal: null,
                        GeoLat: "53.1",
                        GeoLon: "50.1",
                        Qc: 0,
                        QcComplete: 0,
                        QcHouse: 0,
                        QcGeo: 0,
                        UnparsedParts: null,
                        Timezone: null),
                    ["г. Самара, ул. Дубль 2"] = new CleanedAddress(
                        Source: "г. Самара, ул. Дубль 2",
                        Result: "г. Самара, ул. Дубль 2",
                        PostalCode: "446200",
                        Country: "Россия",
                        CountryIsoCode: "RU",
                        FederalDistrict: null,
                        RegionWithType: "Самарская обл",
                        AreaWithType: null,
                        CityWithType: "г. Самара",
                        SettlementWithType: null,
                        StreetWithType: "ул. Дубль",
                        House: "2",
                        Block: null,
                        Flat: null,
                        FiasId: null,
                        FiasLevel: null,
                        KladrId: null,
                        Okato: null,
                        Oktmo: null,
                        TaxOffice: null,
                        TaxOfficeLegal: null,
                        GeoLat: null,
                        GeoLon: null,
                        Qc: 0,
                        QcComplete: 0,
                        QcHouse: 0,
                        QcGeo: 0,
                        UnparsedParts: null,
                        Timezone: null),
                    ["г. Самара, ул. Обновление 3"] = new CleanedAddress(
                        Source: "г. Самара, ул. Обновление 3",
                        Result: "г. Самара, ул. Обновление 3",
                        PostalCode: "446202",
                        Country: "Россия",
                        CountryIsoCode: "RU",
                        FederalDistrict: null,
                        RegionWithType: "Самарская обл",
                        AreaWithType: null,
                        CityWithType: "г. Самара",
                        SettlementWithType: null,
                        StreetWithType: "ул. Обновление",
                        House: "3",
                        Block: null,
                        Flat: null,
                        FiasId: "fias-update",
                        FiasLevel: null,
                        KladrId: "kladr-update",
                        Okato: null,
                        Oktmo: null,
                        TaxOffice: null,
                        TaxOfficeLegal: null,
                        GeoLat: "53.2",
                        GeoLon: "50.2",
                        Qc: 0,
                        QcComplete: 0,
                        QcHouse: 0,
                        QcGeo: 0,
                        UnparsedParts: null,
                        Timezone: null),
                },
                AddressSuggestions =
                {
                    ["г. Самара, ул. Подсказка 4"] =
                    [
                        new ILegalEntityEnrichmentService.DadataAddressSuggestion(
                            Value: "г. Самара, ул. Подсказка 4",
                            UnrestrictedValue: "446204, г. Самара, ул. Подсказка 4",
                            PostalCode: "446204",
                            FiasId: "fias-suggest",
                            KladrId: "kladr-suggest",
                            GeoLat: "53.4",
                            GeoLon: "50.4",
                            RegionWithType: "Самарская обл",
                            AreaWithType: null,
                            CityWithType: "г. Самара",
                            SettlementWithType: null,
                            StreetWithType: "ул. Подсказка",
                            House: "4",
                            Flat: null),
                    ],
                },
            },
            currentUserId: 1);

        var action = await controller.MigrateLegacyAddresses(new LegacyAddressMigrationRequest(), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityAddressMigrationResultDto>(ok.Value);

        Assert.Equal(6, dto.Scanned);
        Assert.Equal(3, dto.Added);
        Assert.Equal(1, dto.Updated);
        Assert.Equal(1, dto.DuplicateSkipped);
        Assert.Equal(1, dto.EmptySkipped);
        Assert.Equal(0, dto.Failed);
        Assert.Equal(4, dto.DadataMatched);
        Assert.Equal(1, dto.DadataNotMatched);

        var addedAddress = await db.AddressRecords.AsNoTracking().SingleAsync(item => item.LegalEntityId == addEntity);
        Assert.Equal("446201", addedAddress.PostalCode);

        var updatedAddress = await db.AddressRecords.AsNoTracking().SingleAsync(item => item.ClientId == updatedClientId);
        Assert.Equal("446202", updatedAddress.PostalCode);
        Assert.Equal("fias-update", updatedAddress.FiasId);

        var fallbackAddress = await db.AddressRecords.AsNoTracking().SingleAsync(item => item.LegalEntityId == fallbackEntity);
        Assert.Equal("446204", fallbackAddress.PostalCode);

        var unmatchedAddress = await db.AddressRecords.AsNoTracking().SingleAsync(item => item.ClientId == unmatchedClientId);
        Assert.Equal("г. Самара, ул. Без совпадения 5", unmatchedAddress.Value);
        Assert.Null(unmatchedAddress.PostalCode);

        Assert.Equal(0, await db.AddressRecords.CountAsync(item => item.ClientId == emptyClientId));
    }

    [Fact]
    public async Task MigrateLegacyAddresses_CanRunWithoutDadataValidation()
    {
        await using var db = await CreateContextAsync();
        var entityId = await CreateLegalEntityAsync(db, "Без валидации", address: "г. Самара, ул. Без проверки 6");
        var controller = CreateController(db, new StubLegalEntityEnrichmentService(), currentUserId: 1);

        var action = await controller.MigrateLegacyAddresses(new LegacyAddressMigrationRequest(false), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityAddressMigrationResultDto>(ok.Value);

        Assert.False(dto.ValidateWithDadata);
        Assert.Equal(1, dto.Added);
        Assert.Equal(0, dto.DadataMatched);
        Assert.Equal(0, dto.DadataNotMatched);

        var address = await db.AddressRecords.AsNoTracking().SingleAsync(item => item.LegalEntityId == entityId);
        Assert.Equal("г. Самара, ул. Без проверки 6", address.Value);
    }

    private static LegalEntitiesController CreateController(
        PaymentContext db,
        ILegalEntityEnrichmentService enrichmentService,
        int currentUserId,
        bool isAdmin = true,
        IFormRequirementsService? formRequirementsService = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, currentUserId.ToString()),
            new(ClaimTypes.Name, "Тестовый пользователь"),
        };

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new LegalEntitiesController(db, enrichmentService, formRequirementsService ?? new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static async Task<int> CreateLegalEntityAsync(
        PaymentContext db,
        string shortName,
        string? inn = null,
        string? address = null,
        int? responsibleUserId = null)
    {
        var entity = new LegalEntity
        {
            ShortName = shortName,
            FullName = shortName,
            Inn = inn,
            Address = address,
            ResponsibleUserId = responsibleUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateClientAsync(PaymentContext db, string name, string? address)
    {
        var client = new Client
        {
            Name = name,
            Address = address ?? string.Empty,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, bool isActive)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "user");
        if (role is null)
        {
            role = new Role
            {
                Name = "user",
                Description = "user",
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = $"legal-coverage-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Сотрудник",
            RoleId = role.Id,
            IsEmployee = true,
            IsActive = isActive,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> CreateAdminUserAsync(PaymentContext db)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "admin");
        if (role is null)
        {
            role = new Role
            {
                Name = "admin",
                Description = "Администратор",
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = $"legal-admin-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Администратор",
            RoleId = role.Id,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var db = new PaymentContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    private sealed class StubFormRequirementsService(IReadOnlyCollection<string> missingFields) : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(missingFields.ToList());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(missingFields.ToList());
    }

    private sealed class StubLegalEntityEnrichmentService : ILegalEntityEnrichmentService
    {
        public Dictionary<string, CleanedAddress> AddressResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CleanedEmail> EmailResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CleanedPhone> PhoneResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LegalEntityDetails> DetailsByQuery { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IReadOnlyList<LegalEntitySuggestion>> SuggestionsByQuery { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> AddressSuggestions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            if (!AddressSuggestions.TryGetValue(query, out var suggestions))
            {
                suggestions = Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>();
            }

            return Task.FromResult(suggestions);
        }

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult(AddressResults.TryGetValue(rawAddress, out var result) ? result : null);

        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
            => Task.FromResult(EmailResults.TryGetValue(rawEmail, out var result) ? result : null);

        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
            => Task.FromResult(PhoneResults.TryGetValue(rawPhone, out var result) ? result : null);

        public Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
            => Task.FromResult<LegalEntityDetails?>(DetailsByQuery.TryGetValue(query, out var result) ? result : null);

        public Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
        {
            var key = query ?? inn ?? string.Empty;
            if (!SuggestionsByQuery.TryGetValue(key, out var suggestions))
            {
                suggestions = Array.Empty<LegalEntitySuggestion>();
            }

            return Task.FromResult(suggestions);
        }
    }
}
