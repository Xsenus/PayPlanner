using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ClientsControllerCoverageTests
{
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";

    [Fact]
    public async Task GetAll_RequiresCurrentUser_AndHonorsVisibility()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "user", "Ответственный");
        var emailOnlyResponsible = await CreateUserAsync(db, "user", string.Empty);
        var admin = await CreateUserAsync(db, "admin", "Администратор");

        db.Clients.AddRange(
            new Client
            {
                Name = "Видимый клиент",
                ResponsibleUserId = owner.Id,
                IsActive = true,
            },
            new Client
            {
                Name = "Клиент по email",
                ResponsibleUserId = emailOnlyResponsible.Id,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var anonymousController = CreateController(db, currentUserId: null);
        Assert.IsType<UnauthorizedResult>(await anonymousController.GetAll(CancellationToken.None));

        var userController = CreateController(db, owner.Id);
        var userOk = Assert.IsType<OkObjectResult>(await userController.GetAll(CancellationToken.None));
        var visibleClients = Assert.IsAssignableFrom<IEnumerable<Client>>(userOk.Value).ToList();

        var visibleClient = Assert.Single(visibleClients);
        Assert.Equal("Видимый клиент", visibleClient.Name);
        Assert.Equal("Ответственный", visibleClient.ResponsibleUserName);

        var adminController = CreateController(db, admin.Id);
        var adminOk = Assert.IsType<OkObjectResult>(await adminController.GetAll(CancellationToken.None));
        var allClients = Assert.IsAssignableFrom<IEnumerable<Client>>(adminOk.Value).OrderBy(item => item.Name).ToList();

        Assert.Equal(2, allClients.Count);
        Assert.Equal(emailOnlyResponsible.Email, allClients.Single(item => item.Name == "Клиент по email").ResponsibleUserName);
    }

    [Fact]
    public async Task GetById_ReturnsNotFound_WhenClientIsHidden_AndReturnsAuditMetadata_WhenVisible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "user", "Владелец");
        var otherUser = await CreateUserAsync(db, "user", "Другой");

        var client = new Client
        {
            Name = "Клиент с делом",
            ResponsibleUserId = owner.Id,
            IsActive = true,
            Cases =
            [
                new ClientCase
                {
                    Title = "Основное дело",
                    Description = "Описание",
                    Status = ClientCaseStatus.Open,
                },
            ],
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var anonymousController = CreateController(db, currentUserId: null);
        Assert.IsType<UnauthorizedResult>(await anonymousController.GetById(client.Id, CancellationToken.None));

        var hiddenController = CreateController(db, otherUser.Id);
        Assert.IsType<NotFoundResult>(await hiddenController.GetById(client.Id, CancellationToken.None));

        var ownerController = CreateController(db, owner.Id);
        var ok = Assert.IsType<OkObjectResult>(await ownerController.GetById(client.Id, CancellationToken.None));
        var dto = Assert.IsType<Client>(ok.Value);

        Assert.Equal(client.Id, dto.Id);
        Assert.Equal("Владелец", dto.ResponsibleUserName);
        Assert.False(dto.AuditIsDeleted);
        Assert.Single(dto.Cases);
        Assert.False(dto.Cases.Single().AuditIsDeleted);
    }

    [Fact]
    public async Task GetStats_AndCaseFinanceSummaries_ReturnExpectedEmptyBranches()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin", "Админ");
        var controller = CreateController(db, admin.Id);
        var anonymousController = CreateController(db, currentUserId: null);

        Assert.IsType<UnauthorizedResult>(await anonymousController.GetStats(1, null, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await anonymousController.GetCaseFinanceSummaries(1, CancellationToken.None));

        Assert.IsType<NotFoundResult>(await controller.GetStats(404, null, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.GetCaseFinanceSummaries(404, CancellationToken.None));

        var client = new Client
        {
            Name = "Клиент без дел",
            ResponsibleUserId = admin.Id,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var summariesAction = await controller.GetCaseFinanceSummaries(client.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(summariesAction);
        var summaries = Assert.IsAssignableFrom<IEnumerable<ClientCaseFinanceSummaryDto>>(ok.Value);

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task CleanAddress_ValidatesInput_HandlesMissingMatch_AndMapsResponse()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(
            db,
            currentUserId: null,
            enrichmentService: new StubEnrichmentService
            {
                CleanedAddresses =
                {
                    ["Самара, Миронова, 9"] = new CleanedAddress(
                        Source: "Самара, Миронова, 9",
                        Result: "446200, Самарская обл, г Новокуйбышевск, ул Миронова, д 9",
                        PostalCode: "446200",
                        Country: "Россия",
                        CountryIsoCode: "RU",
                        FederalDistrict: "Приволжский",
                        RegionWithType: "Самарская обл",
                        AreaWithType: "Безенчукский р-н",
                        CityWithType: "г Новокуйбышевск",
                        SettlementWithType: null,
                        StreetWithType: "ул Миронова",
                        House: "9",
                        Block: null,
                        Flat: null,
                        FiasId: "fias-1",
                        FiasLevel: "8",
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
                        Timezone: "Asia/Samara"),
                },
            });

        var blank = await controller.CleanAddress(new ClientCleanAddressRequest(" "), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(blank);

        var missing = await controller.CleanAddress(new ClientCleanAddressRequest("Не найден"), CancellationToken.None);
        Assert.IsType<NotFoundResult>(missing);

        var ok = Assert.IsType<OkObjectResult>(
            await controller.CleanAddress(new ClientCleanAddressRequest("Самара, Миронова, 9"), CancellationToken.None));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("446200", json.RootElement.GetProperty("postalCode").GetString());
        Assert.Equal("Россия", json.RootElement.GetProperty("country").GetString());
        Assert.Equal("г Новокуйбышевск", json.RootElement.GetProperty("cityWithType").GetString());
        Assert.Equal("fias-1", json.RootElement.GetProperty("fiasId").GetString());
    }

    [Fact]
    public async Task SuggestAddress_ReturnsEmptyForBlankQuery_AndMapsSuggestions()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(
            db,
            currentUserId: null,
            enrichmentService: new StubEnrichmentService
            {
                AddressSuggestions =
                {
                    ["миронова"] =
                    [
                        new ILegalEntityEnrichmentService.DadataAddressSuggestion(
                            Value: "Самара, Миронова, 9",
                            UnrestrictedValue: "446200, Россия, Самарская обл, г Новокуйбышевск, ул Миронова, д 9",
                            PostalCode: "446200",
                            FiasId: "fias-9",
                            KladrId: "kladr-9",
                            GeoLat: "53.095",
                            GeoLon: "49.945",
                            RegionWithType: "Самарская обл",
                            AreaWithType: null,
                            CityWithType: "г Новокуйбышевск",
                            SettlementWithType: null,
                            StreetWithType: "ул Миронова",
                            House: "9",
                            Flat: null),
                    ],
                },
            });

        var blankAction = await controller.SuggestAddress(new ClientSuggestAddressRequest(" "), CancellationToken.None);
        var blankOk = Assert.IsType<OkObjectResult>(blankAction);
        using (var blankJson = JsonDocument.Parse(JsonSerializer.Serialize(blankOk.Value)))
        {
            Assert.Equal(JsonValueKind.Array, blankJson.RootElement.ValueKind);
            Assert.Equal(0, blankJson.RootElement.GetArrayLength());
        }

        var suggestionsAction = await controller.SuggestAddress(new ClientSuggestAddressRequest("миронова", 5), CancellationToken.None);
        var suggestionsOk = Assert.IsType<OkObjectResult>(suggestionsAction);
        using var suggestionsJson = JsonDocument.Parse(JsonSerializer.Serialize(suggestionsOk.Value));
        var first = Assert.Single(suggestionsJson.RootElement.EnumerateArray());
        Assert.Equal("Самара, Миронова, 9", first.GetProperty("value").GetString());
        Assert.Equal("446200", first.GetProperty("postalCode").GetString());
        Assert.Equal("г Новокуйбышевск", first.GetProperty("cityWithType").GetString());
    }

    [Fact]
    public async Task Create_RequiresUser_AndValidatesReferences()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin", "Админ");
        var legalEntityId = await CreateLegalEntityAsync(db, "ООО Альфа");
        var clientStatusId = await CreateClientStatusAsync(db, "Новый");
        var inactiveResponsible = await CreateUserAsync(db, "user", "Неактивный", isActive: false);

        var anonymousController = CreateController(db, currentUserId: null);
        var unauthorized = await anonymousController.Create(new Client { Name = "Без пользователя" }, CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(unauthorized);

        var controller = CreateController(db, admin.Id);

        var missingLegalEntity = await controller.Create(
            new Client
            {
                Name = "Клиент",
                LegalEntityId = legalEntityId + 100,
            },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingLegalEntity);

        var missingStatus = await controller.Create(
            new Client
            {
                Name = "Клиент",
                LegalEntityId = legalEntityId,
                ClientStatusId = clientStatusId + 100,
            },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingStatus);

        var missingResponsible = await controller.Create(
            new Client
            {
                Name = "Клиент",
                LegalEntityId = legalEntityId,
                ClientStatusId = clientStatusId,
                ResponsibleUserId = inactiveResponsible.Id,
            },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingResponsible);
    }

    [Fact]
    public async Task Create_ForNonAdmin_UsesCurrentResponsible_AndPersistsLegacyProfileArtifacts()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "user", "Владелец");
        var anotherResponsible = await CreateUserAsync(db, "user", "Другой");
        var legalEntityId = await CreateLegalEntityAsync(db, "ООО Бета");
        var clientStatusId = await CreateClientStatusAsync(db, "Активный");
        var controller = CreateController(db, owner.Id);

        var action = await controller.Create(
            new Client
            {
                Name = "Клиент из legacy",
                IsActive = true,
                LegalEntityId = legalEntityId,
                ClientStatusId = clientStatusId,
                ResponsibleUserId = anotherResponsible.Id,
                Notes = BuildLegacyProfileNotes(
                    registrationAddress: "г. Самара, ул. Победы, д. 1",
                    actualAddress: "г. Самара, ул. Гагарина, д. 2",
                    passportSeries: "1234",
                    passportNumber: "567890"),
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var dto = Assert.IsType<Client>(created.Value);

        Assert.Equal(owner.Id, dto.ResponsibleUserId);
        Assert.Equal(2, await db.AddressRecords.CountAsync(item => item.ClientId == dto.Id));
        Assert.Equal(1, await db.ClientPassports.CountAsync(item => item.ClientId == dto.Id));

        var storedPassport = await db.ClientPassports.AsNoTracking().SingleAsync(item => item.ClientId == dto.Id);
        Assert.Equal("1234", storedPassport.Series);
        Assert.Equal("567890", storedPassport.Number);
    }

    [Fact]
    public async Task Update_ValidatesAccessAndReferences()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin", "Админ");
        var owner = await CreateUserAsync(db, "user", "Владелец");
        var legalEntityId = await CreateLegalEntityAsync(db, "ООО Гамма");
        var clientStatusId = await CreateClientStatusAsync(db, "В работе");
        var inactiveResponsible = await CreateUserAsync(db, "user", "Неактивный", isActive: false);

        var client = new Client
        {
            Name = "Обновляемый клиент",
            ResponsibleUserId = owner.Id,
            IsActive = true,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var anonymousController = CreateController(db, currentUserId: null);
        var unauthorized = await anonymousController.Update(client.Id, new Client { Name = "Без пользователя" }, CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(unauthorized);

        var controller = CreateController(db, admin.Id);
        Assert.IsType<NotFoundResult>(
            await controller.Update(999, new Client { Name = "Не найден" }, CancellationToken.None));

        Assert.IsType<BadRequestObjectResult>(
            await controller.Update(
                client.Id,
                new Client
                {
                    Name = "Клиент",
                    LegalEntityId = legalEntityId + 100,
                },
                CancellationToken.None));

        Assert.IsType<BadRequestObjectResult>(
            await controller.Update(
                client.Id,
                new Client
                {
                    Name = "Клиент",
                    LegalEntityId = legalEntityId,
                    ClientStatusId = clientStatusId + 100,
                },
                CancellationToken.None));

        Assert.IsType<BadRequestObjectResult>(
            await controller.Update(
                client.Id,
                new Client
                {
                    Name = "Клиент",
                    LegalEntityId = legalEntityId,
                    ClientStatusId = clientStatusId,
                    ResponsibleUserId = inactiveResponsible.Id,
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Update_ForNonAdmin_PreservesResponsible_AndReplacesLegacyArtifacts()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "user", "Владелец");
        var anotherResponsible = await CreateUserAsync(db, "user", "Новый ответственный");

        var client = new Client
        {
            Name = "Клиент для обновления",
            ResponsibleUserId = owner.Id,
            IsActive = true,
            Notes = BuildLegacyProfileNotes(
                registrationAddress: "Старый адрес регистрации",
                actualAddress: "Старый фактический адрес",
                passportSeries: "0000",
                passportNumber: "111111"),
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.AddressRecords.AddRange(
            new AddressRecord
            {
                ClientId = client.Id,
                Type = AddressType.Registration,
                Value = "Старый адрес регистрации",
                IsPrimary = true,
            },
            new AddressRecord
            {
                ClientId = client.Id,
                Type = AddressType.Actual,
                Value = "Старый фактический адрес",
                IsPrimary = false,
            });
        db.ClientPassports.Add(new ClientPassport
        {
            ClientId = client.Id,
            Series = "0000",
            Number = "111111",
            IsPrimary = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, owner.Id);
        var action = await controller.Update(
            client.Id,
            new Client
            {
                Name = "Клиент после обновления",
                ResponsibleUserId = anotherResponsible.Id,
                IsActive = true,
                Notes = BuildLegacyProfileNotes(
                    registrationAddress: "Новый адрес регистрации",
                    actualAddress: null,
                    passportSeries: "9999",
                    passportNumber: "222222"),
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var dto = Assert.IsType<Client>(ok.Value);

        Assert.Equal(owner.Id, dto.ResponsibleUserId);

        var addresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.ClientId == client.Id)
            .OrderBy(item => item.Type)
            .ToListAsync();

        var registration = Assert.Single(addresses);
        Assert.Equal((int)AddressType.Registration, (int)registration.Type);
        Assert.Equal("Новый адрес регистрации", registration.Value);

        var passport = await db.ClientPassports.AsNoTracking().SingleAsync(item => item.ClientId == client.Id);
        Assert.Equal("9999", passport.Series);
        Assert.Equal("222222", passport.Number);
    }

    [Fact]
    public async Task Delete_AndBulkDelete_ReportValidationErrors_AndUsageSummary()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin", "Админ");
        var controller = CreateController(db, admin.Id);
        var anonymousController = CreateController(db, currentUserId: null);

        var unauthorizedDelete = await anonymousController.Delete(1, CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(unauthorizedDelete);
        Assert.IsType<UnauthorizedResult>(await anonymousController.BulkDelete(new BulkIdsRequest { Ids = [1] }, CancellationToken.None));

        Assert.IsType<NotFoundResult>(await controller.Delete(999, CancellationToken.None));

        var emptyBulkDelete = await controller.BulkDelete(new BulkIdsRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(emptyBulkDelete);

        var plainClient = new Client
        {
            Name = "Обычный клиент",
            ResponsibleUserId = admin.Id,
            IsActive = true,
        };

        var blockedClient = new Client
        {
            Name = "Занятый клиент",
            ResponsibleUserId = admin.Id,
            IsActive = true,
        };

        db.Clients.AddRange(plainClient, blockedClient);
        await db.SaveChangesAsync();

        var missingIdsAction = await controller.BulkDelete(
            new BulkIdsRequest { Ids = [plainClient.Id, 999] },
            CancellationToken.None);
        var missingIds = Assert.IsType<NotFoundObjectResult>(missingIdsAction);
        using (var missingJson = JsonDocument.Parse(JsonSerializer.Serialize(missingIds.Value)))
        {
            Assert.Equal(1, missingJson.RootElement.GetProperty("missingIds").GetArrayLength());
            Assert.Equal(999, missingJson.RootElement.GetProperty("missingIds")[0].GetInt32());
        }

        var contract = new Contract
        {
            Number = "Д-1",
            Title = "Договор",
            Date = new DateTime(2026, 4, 18),
        };

        db.Acts.Add(new Act
        {
            Number = "А-1",
            Date = new DateTime(2026, 4, 18),
            Amount = 1000m,
            ClientId = blockedClient.Id,
        });
        db.ClientCases.Add(new ClientCase
        {
            ClientId = blockedClient.Id,
            Title = "Дело клиента",
            Description = "Описание",
            Status = ClientCaseStatus.Open,
        });
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        db.ClientContracts.Add(new ClientContract
        {
            ClientId = blockedClient.Id,
            ContractId = contract.Id,
        });
        db.PowersOfAttorney.Add(new PowerOfAttorney
        {
            ClientId = blockedClient.Id,
            Number = "ДОВ-1",
            IssuedAt = new DateTime(2026, 4, 18),
            IsActive = true,
        });
        db.Payments.Add(new Payment
        {
            Amount = 5000m,
            PaidAmount = 0m,
            CounterpartyName = blockedClient.Name,
            Date = new DateTime(2026, 4, 18),
            PlannedDate = new DateTime(2026, 4, 18),
            Description = "Платеж клиента",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = blockedClient.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        });
        await db.SaveChangesAsync();

        var blockedAction = await controller.BulkDelete(
            new BulkIdsRequest { Ids = [blockedClient.Id] },
            CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(blockedAction);
        using var conflictJson = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        var blocked = Assert.Single(conflictJson.RootElement.GetProperty("blockedClients").EnumerateArray());
        Assert.Equal(blockedClient.Id, blocked.GetProperty("id").GetInt32());
        Assert.Equal(5, blocked.GetProperty("usage").GetArrayLength());
    }

    [Fact]
    public async Task ChangeResponsible_ValidatesRequestAndMissingClients()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin", "Админ");
        var nextResponsible = await CreateUserAsync(db, "user", "Следующий");
        var controller = CreateController(db, admin.Id);
        var anonymousController = CreateController(db, currentUserId: null);

        Assert.IsType<UnauthorizedResult>(
            await anonymousController.ChangeResponsible(
                new BulkAssignResponsibleRequest
                {
                    Ids = [1],
                    ResponsibleUserId = nextResponsible.Id,
                },
                CancellationToken.None));

        var emptyIds = await controller.ChangeResponsible(new BulkAssignResponsibleRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(emptyIds);

        var missingResponsible = await controller.ChangeResponsible(
            new BulkAssignResponsibleRequest
            {
                Ids = [1],
                ResponsibleUserId = 999,
            },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingResponsible);

        var client = new Client
        {
            Name = "Клиент для смены",
            ResponsibleUserId = admin.Id,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var notFound = await controller.ChangeResponsible(
            new BulkAssignResponsibleRequest
            {
                Ids = [client.Id, 999],
                ResponsibleUserId = nextResponsible.Id,
            },
            CancellationToken.None);

        var result = Assert.IsType<NotFoundObjectResult>(notFound);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(999, json.RootElement.GetProperty("missingIds")[0].GetInt32());
    }

    private static string BuildLegacyProfileNotes(
        string? registrationAddress,
        string? actualAddress,
        string? passportSeries,
        string? passportNumber)
    {
        return ClientProfileNotesPrefix + JsonSerializer.Serialize(new
        {
            registrationAddress,
            actualAddress,
            actualAddressMatchesRegistration = false,
            passportSeries,
            passportNumber,
        });
    }

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string roleName,
        string fullName,
        bool isActive = true)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == roleName);
        if (role is null)
        {
            role = new Role
            {
                Name = roleName,
                Description = roleName,
                CreatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = $"clients-coverage-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = fullName,
            RoleId = role.Id,
            IsActive = isActive,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<int> CreateLegalEntityAsync(PaymentContext db, string shortName)
    {
        var entity = new LegalEntity
        {
            ShortName = shortName,
            FullName = shortName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateClientStatusAsync(PaymentContext db, string name)
    {
        var status = new ClientStatusEntity
        {
            Name = name,
            ColorHex = "#2563EB",
        };

        db.ClientStatuses.Add(status);
        await db.SaveChangesAsync();
        return status.Id;
    }

    private static ClientsController CreateController(
        PaymentContext db,
        int? currentUserId,
        ILegalEntityEnrichmentService? enrichmentService = null,
        IFormRequirementsService? formRequirementsService = null)
    {
        var claims = new List<Claim>();
        if (currentUserId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, currentUserId.Value.ToString()));
            claims.Add(new Claim(ClaimTypes.Name, "Тестовый пользователь"));
        }

        return new ClientsController(
            db,
            enrichmentService ?? new StubEnrichmentService(),
            formRequirementsService ?? new NoopFormRequirementsService())
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

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    private sealed class StubEnrichmentService : ILegalEntityEnrichmentService
    {
        public Dictionary<string, CleanedAddress> CleanedAddresses { get; } = new(StringComparer.OrdinalIgnoreCase);
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
            => Task.FromResult(CleanedAddresses.TryGetValue(rawAddress, out var result) ? result : null);

        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
            => Task.FromResult<CleanedEmail?>(null);

        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
            => Task.FromResult<CleanedPhone?>(null);

        public Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
            => Task.FromResult<LegalEntityDetails?>(null);

        public Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LegalEntitySuggestion>>(Array.Empty<LegalEntitySuggestion>());
    }
}
