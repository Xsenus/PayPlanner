using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ClientsControllerTests
{
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";

    [Fact]
    public async Task Create_RoundTripsStructuredAddressEntries_WithFullAddress()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var controller = CreateController(db, userId);

        const string fullAddress = "633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19";
        var model = new Client
        {
            Name = "Тестовый клиент",
            Address = fullAddress,
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "registration",
                        sourceQuery = fullAddress,
                        fullAddress,
                        postalCode = "633208",
                        country = "Россия",
                        region = "Новосибирская обл",
                        district = "Искитимский р-н",
                        city = "г Искитим",
                        settlement = "Южный мкр",
                        street = "Южный мкр",
                        house = "37",
                        flat = "19",
                        fiasId = "fias-iskitim",
                        kladrId = "kladr-iskitim",
                        geoLat = "54.6265",
                        geoLon = "83.3045",
                    },
                },
            }),
            IsActive = true,
        };

        var action = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var dto = Assert.IsType<Client>(created.Value);
        var address = Assert.Single(dto.Addresses);

        Assert.Equal(fullAddress, address.UnrestrictedValue);
        Assert.Equal("Россия", address.Country);
        Assert.Equal("Новосибирская обл", address.RegionWithType);
        Assert.Equal("Искитимский р-н", address.AreaWithType);
        Assert.Equal("г Искитим", address.CityWithType);
        Assert.Equal("Южный мкр", address.SettlementWithType);
        Assert.Equal("Южный мкр", address.StreetWithType);
        Assert.Equal("37", address.House);
        Assert.Equal("19", address.Flat);

        var stored = await db.AddressRecords.SingleAsync(item => item.ClientId == dto.Id);
        Assert.Equal(fullAddress, stored.UnrestrictedValue);
        Assert.Equal("Россия", stored.Country);
        Assert.Equal("Новосибирская обл", stored.RegionWithType);
        Assert.Equal("Искитимский р-н", stored.AreaWithType);
        Assert.Equal("г Искитим", stored.CityWithType);
        Assert.Equal("Южный мкр", stored.SettlementWithType);
        Assert.Equal("Южный мкр", stored.StreetWithType);
        Assert.Equal("37", stored.House);
        Assert.Equal("19", stored.Flat);
    }

    [Fact]
    public async Task GetStats_IncludesPaymentsLinkedThroughInvoiceCases_ForSelectedCase()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Тестовый клиент",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело по счету",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "30",
            Date = new DateTime(2026, 4, 8),
        };

        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var linkedPayment = new Payment
        {
            Amount = 18_000m,
            PaidAmount = 9_000m,
            CounterpartyName = "Тестовый клиент",
            Date = new DateTime(2026, 4, 9),
            PlannedDate = new DateTime(2026, 4, 9),
            Description = "Рассрочка по счету №30",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = client.Id,
            ClientCaseId = null,
            InvoiceId = invoice.Id,
            Account = "30",
            AccountDate = invoice.Date,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.Add(linkedPayment);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedPayment.Id,
            ClientCaseId = clientCase.Id,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetStats(client.Id, clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(client.Id, stats.ClientId);
        Assert.Equal(9_000m, stats.TotalIncome);
        Assert.Equal(9_000m, stats.OutstandingIncome);
        Assert.Equal(0m, stats.TotalExpenses);
        Assert.Equal(1, stats.TotalPayments);
        Assert.Equal(0, stats.PaidPayments);
        Assert.Equal(1, stats.PendingPayments);
        Assert.Equal(0, stats.OverduePayments);
        Assert.Collection(
            stats.RecentPayments,
            payment => Assert.Equal(linkedPayment.Id, payment.Id));
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_AggregatesPaymentsLinkedThroughInvoiceCases()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Тестовый клиент",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var primaryCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };

        var secondaryCase = new ClientCase
        {
            Client = client,
            Title = "Второе дело",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "31",
            Date = new DateTime(2026, 4, 10),
        };

        db.Clients.Add(client);
        db.ClientCases.AddRange(primaryCase, secondaryCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var linkedIncomePayment = new Payment
        {
            Amount = 18_000m,
            PaidAmount = 6_000m,
            CounterpartyName = "Тестовый клиент",
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            Description = "Платеж по счету №31",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = client.Id,
            ClientCaseId = null,
            InvoiceId = invoice.Id,
            Account = "31",
            AccountDate = invoice.Date,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        var unrelatedExpense = new Payment
        {
            Amount = 4_500m,
            PaidAmount = 4_500m,
            CounterpartyName = "Поставщик",
            Date = new DateTime(2026, 4, 11),
            PlannedDate = new DateTime(2026, 4, 11),
            Description = "Расход по другому делу",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Expense,
            ClientId = client.Id,
            ClientCaseId = secondaryCase.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.AddRange(linkedIncomePayment, unrelatedExpense);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedIncomePayment.Id,
            ClientCaseId = primaryCase.Id,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetCaseFinanceSummaries(client.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var summaries = Assert.IsAssignableFrom<List<ClientCaseFinanceSummaryDto>>(ok.Value);

        var primarySummary = Assert.Single(summaries, item => item.CaseId == primaryCase.Id);
        Assert.Equal(18_000m, primarySummary.IncomeTotal);
        Assert.Equal(6_000m, primarySummary.IncomePaid);
        Assert.Equal(12_000m, primarySummary.IncomeOutstanding);
        Assert.Equal(1, primarySummary.IncomeCount);
        Assert.Equal(0m, primarySummary.ExpenseTotal);

        var secondarySummary = Assert.Single(summaries, item => item.CaseId == secondaryCase.Id);
        Assert.Equal(0m, secondarySummary.IncomeTotal);
        Assert.Equal(4_500m, secondarySummary.ExpenseTotal);
        Assert.Equal(4_500m, secondarySummary.ExpensePaid);
        Assert.Equal(0m, secondarySummary.ExpenseOutstanding);
        Assert.Equal(1, secondarySummary.ExpenseCount);
    }

    [Fact]
    public async Task Create_Update_Delete_RoundTripsStructuredProfile_AndCleansChildrenOnDelete()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var firstLegalEntityId = await CreateLegalEntityAsync(db, "РЋСЂР»РёС†Рѕ 1");
        var secondLegalEntityId = await CreateLegalEntityAsync(db, "РЋСЂР»РёС†Рѕ 2");
        var firstStatusId = await CreateClientStatusAsync(db, "РќРѕРІС‹Р№");
        var secondStatusId = await CreateClientStatusAsync(db, "VIP");
        var controller = CreateController(db, userId);

        var createModel = new Client
        {
            Name = "РљР»РёРµРЅС‚ CRUD",
            Email = "client-crud@example.com",
            Phone = "+7 (900) 000-00-00",
            Company = "РћРћРћ РџРµСЂРІРѕРµ",
            Address = "РџРµСЂРІС‹Р№ Р°РґСЂРµСЃ",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "registration",
                        sourceQuery = "РџРµСЂРІС‹Р№ Р°РґСЂРµСЃ",
                        fullAddress = "РџРµСЂРІС‹Р№ Р°РґСЂРµСЃ",
                        postalCode = "630001",
                        country = "Р РѕСЃСЃРёСЏ",
                        region = "РќРѕРІРѕСЃРёР±РёСЂСЃРєР°СЏ РѕР±Р»",
                        city = "Рі РќРѕРІРѕСЃРёР±РёСЂСЃРє",
                        street = "СѓР» РџРµСЂРІР°СЏ",
                        house = "1",
                        flat = "10",
                    },
                },
                passportEntries = new[]
                {
                    new
                    {
                        series = "1234",
                        number = "567890",
                        issuedBy = "РћР’Р” 1",
                        issueDate = new DateTime(2020, 1, 1),
                    },
                },
            }),
            IsActive = true,
            LegalEntityId = firstLegalEntityId,
            ResponsibleUserId = userId,
            ClientStatusId = firstStatusId,
        };

        var createAction = await controller.Create(createModel, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createAction);
        var dto = Assert.IsType<Client>(created.Value);
        Assert.Equal(firstLegalEntityId, dto.LegalEntityId);
        Assert.Equal(firstStatusId, dto.ClientStatusId);
        Assert.Equal(userId, dto.ResponsibleUserId);
        Assert.Single(dto.Addresses);
        Assert.Equal("РџРµСЂРІС‹Р№ Р°РґСЂРµСЃ", dto.Addresses.Single().UnrestrictedValue);
        Assert.Equal("1234", (await db.ClientPassports.AsNoTracking().SingleAsync(item => item.ClientId == dto.Id)).Series);

        var updateModel = new Client
        {
            Name = "РљР»РёРµРЅС‚ CRUD Updated",
            Email = "client-crud-updated@example.com",
            Phone = "+7 (900) 111-11-11",
            Company = "РћРћРћ Р’С‚РѕСЂРѕРµ",
            Address = "Р’С‚РѕСЂРѕР№ Р°РґСЂРµСЃ",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "actual",
                        sourceQuery = "Р’С‚РѕСЂРѕР№ Р°РґСЂРµСЃ",
                        fullAddress = "Р’С‚РѕСЂРѕР№ Р°РґСЂРµСЃ",
                        postalCode = "630002",
                        country = "Р РѕСЃСЃРёСЏ",
                        region = "РќРѕРІРѕСЃРёР±РёСЂСЃРєР°СЏ РѕР±Р»",
                        city = "Рі РќРѕРІРѕСЃРёР±РёСЂСЃРє",
                        street = "СѓР» Р’С‚РѕСЂР°СЏ",
                        house = "2",
                        flat = "20",
                    },
                },
                passportEntries = new[]
                {
                    new
                    {
                        series = "4321",
                        number = "098765",
                        issuedBy = "РћР’Р” 2",
                        issueDate = new DateTime(2021, 2, 2),
                    },
                },
            }),
            IsActive = false,
            LegalEntityId = secondLegalEntityId,
            ResponsibleUserId = userId,
            ClientStatusId = secondStatusId,
        };

        var updateAction = await controller.Update(dto.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction);
        var updated = Assert.IsType<Client>(ok.Value);
        Assert.Equal("РљР»РёРµРЅС‚ CRUD Updated", updated.Name);
        Assert.Equal(secondLegalEntityId, updated.LegalEntityId);
        Assert.Equal(secondStatusId, updated.ClientStatusId);
        Assert.False(updated.IsActive);
        Assert.Single(updated.Addresses);
        Assert.Equal("Р’С‚РѕСЂРѕР№ Р°РґСЂРµСЃ", updated.Addresses.Single().UnrestrictedValue);

        var storedPassport = await db.ClientPassports.AsNoTracking().SingleAsync(item => item.ClientId == dto.Id);
        Assert.Equal("4321", storedPassport.Series);
        Assert.Equal("098765", storedPassport.Number);

        var deleteAction = await controller.Delete(dto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteAction);
        Assert.Null(await db.Clients.AsNoTracking().SingleOrDefaultAsync(item => item.Id == dto.Id));
        Assert.Equal(0, await db.AddressRecords.AsNoTracking().CountAsync(item => item.ClientId == dto.Id));
        Assert.Equal(0, await db.ClientPassports.AsNoTracking().CountAsync(item => item.ClientId == dto.Id));
    }

    [Fact]
    public async Task Create_QuickModalValidation_UsesStructuredFields_AndIgnoresEmailRequirement()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(
            db,
            userId,
            new StaticFormRequirementsService(
                ("clients-modal", "Name"),
                ("clients-modal", "Notes"),
                ("clients-modal", "Phone"),
                ("clients-modal", "Email")));

        var missingStructuredFieldModel = new Client
        {
            Name = "РРІР°РЅРѕРІ РРІР°РЅ",
            Phone = "+7 (999) 000-00-00",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                lastName = "РРІР°РЅРѕРІ",
                firstName = "РРІР°РЅ",
                middleName = "",
                comment = "",
            }),
            IsActive = true,
        };

        var badRequestAction = await controller.Create(missingStructuredFieldModel, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(badRequestAction);
        var missingFields = Assert.IsAssignableFrom<IEnumerable<string>>(badRequest.Value?.GetType().GetProperty("missingFields")?.GetValue(badRequest.Value));
        Assert.Contains("Comment", missingFields);
        Assert.DoesNotContain("Name", missingFields);
        Assert.DoesNotContain("Notes", missingFields);
        Assert.DoesNotContain("Email", missingFields);
        Assert.DoesNotContain("FirstName", missingFields);
        Assert.DoesNotContain("LastName", missingFields);
        Assert.DoesNotContain("MiddleName", missingFields);

        var validModel = new Client
        {
            Name = "РРІР°РЅРѕРІ РРІР°РЅ РРІР°РЅРѕРІРёС‡",
            Phone = "+7 (999) 000-00-00",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                lastName = "РРІР°РЅРѕРІ",
                firstName = "РРІР°РЅ",
                middleName = "РРІР°РЅРѕРІРёС‡",
                comment = "РџРµСЂРµР·РІРѕРЅРёС‚СЊ РїРѕСЃР»Рµ РѕР±РµРґР°",
            }),
            IsActive = true,
        };

        var createAction = await controller.Create(validModel, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createAction);
        var dto = Assert.IsType<Client>(created.Value);
        Assert.Equal("РРІР°РЅРѕРІ РРІР°РЅ РРІР°РЅРѕРІРёС‡", dto.Name);
        Assert.Equal("+7 (999) 000-00-00", dto.Phone);
        Assert.Equal(string.Empty, dto.Email);
    }

    [Fact]
    public async Task Create_QuickModalValidation_LegacyNameRequirement_MapsToFirstNameOnly()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(
            db,
            userId,
            new StaticFormRequirementsService(("clients-modal", "Name")));

        var missingFirstNameModel = new Client
        {
            Name = "Client",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                lastName = "Ivanov",
                firstName = "",
                middleName = "",
                comment = "callback",
            }),
            IsActive = true,
        };

        var badRequestAction = await controller.Create(missingFirstNameModel, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(badRequestAction);
        var missingFields = Assert.IsAssignableFrom<IEnumerable<string>>(badRequest.Value?.GetType().GetProperty("missingFields")?.GetValue(badRequest.Value));
        Assert.Contains("FirstName", missingFields);
        Assert.DoesNotContain("LastName", missingFields);
        Assert.DoesNotContain("MiddleName", missingFields);
        Assert.DoesNotContain("Name", missingFields);
    }

    [Fact]
    public async Task Delete_RejectsClient_WhenPaymentUsageExists()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "РљР»РёРµРЅС‚ СЃ РїР»Р°С‚РµР¶РѕРј",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Amount = 7_500m,
            PaidAmount = 0m,
            CounterpartyName = "РљР»РёРµРЅС‚ СЃ РїР»Р°С‚РµР¶РѕРј",
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Description = "РџР»Р°С‚РµР¶ РєР»РёРµРЅС‚Р°",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            ClientId = client.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.Delete(client.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(action);
        var usage = Assert.IsAssignableFrom<IEnumerable<string>>(conflict.Value?.GetType().GetProperty("usage")?.GetValue(conflict.Value));
        Assert.Contains(usage, item => !string.IsNullOrWhiteSpace(item));
        Assert.NotNull(await db.Clients.AsNoTracking().SingleOrDefaultAsync(item => item.Id == client.Id));
    }

    [Fact]
    public async Task BulkDelete_RemovesSelectedUnusedClients_AndSoftDeletesProfiles()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var firstClient = new Client
        {
            Name = "РљР»РёРµРЅС‚ 1",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var secondClient = new Client
        {
            Name = "РљР»РёРµРЅС‚ 2",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        db.Clients.AddRange(firstClient, secondClient);
        await db.SaveChangesAsync();

        db.AddressRecords.AddRange(
            new AddressRecord { ClientId = firstClient.Id, Type = AddressType.Registration, Value = "РђРґСЂРµСЃ 1", IsPrimary = true },
            new AddressRecord { ClientId = secondClient.Id, Type = AddressType.Registration, Value = "РђРґСЂРµСЃ 2", IsPrimary = true });
        db.ClientPassports.AddRange(
            new ClientPassport { ClientId = firstClient.Id, Series = "1111", Number = "111111", IsPrimary = true },
            new ClientPassport { ClientId = secondClient.Id, Series = "2222", Number = "222222", IsPrimary = true });
        await db.SaveChangesAsync();

        var action = await controller.BulkDelete(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [firstClient.Id, secondClient.Id],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal(2, ok.Value?.GetType().GetProperty("deleted")?.GetValue(ok.Value));
        Assert.Equal(0, await db.Clients.AsNoTracking().CountAsync(item => item.Id == firstClient.Id || item.Id == secondClient.Id));
        Assert.Equal(0, await db.AddressRecords.AsNoTracking().CountAsync(item => item.ClientId == firstClient.Id || item.ClientId == secondClient.Id));
        Assert.Equal(0, await db.ClientPassports.AsNoTracking().CountAsync(item => item.ClientId == firstClient.Id || item.ClientId == secondClient.Id));
    }

    [Fact]
    public async Task ChangeResponsible_UpdatesSelectedClients()
    {
        await using var db = await CreateContextAsync();
        var adminUserId = await CreateUserAsync(db, roleName: "admin");
        var nextUserId = await CreateUserAsync(db, roleName: "user");
        var controller = CreateController(db, adminUserId);

        var firstClient = new Client
        {
            Name = "РљР»РёРµРЅС‚ A",
            ResponsibleUserId = adminUserId,
            IsActive = true,
        };

        var secondClient = new Client
        {
            Name = "РљР»РёРµРЅС‚ B",
            ResponsibleUserId = adminUserId,
            IsActive = true,
        };

        db.Clients.AddRange(firstClient, secondClient);
        await db.SaveChangesAsync();

        var action = await controller.ChangeResponsible(new PayPlanner.Api.Models.Requests.BulkAssignResponsibleRequest
        {
            Ids = [firstClient.Id, secondClient.Id],
            ResponsibleUserId = nextUserId,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal(2, ok.Value?.GetType().GetProperty("updated")?.GetValue(ok.Value));

        var responsibles = await db.Clients.AsNoTracking()
            .Where(item => item.Id == firstClient.Id || item.Id == secondClient.Id)
            .Select(item => item.ResponsibleUserId)
            .Distinct()
            .ToListAsync();

        Assert.Equal([nextUserId], responsibles);
    }

    private static async Task<int> CreateUserAsync(PaymentContext db, string roleName = "user")
    {
        var role = new Role
        {
            Name = roleName,
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = $"client-tests-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Тестовый пользователь",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
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
        int userId,
        IFormRequirementsService? formRequirementsService = null)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый пользователь"),
        };

        var controller = new ClientsController(
            db,
            new NoopEnrichmentService(),
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

        return controller;
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
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class StaticFormRequirementsService : IFormRequirementsService
    {
        private readonly Dictionary<string, List<string>> _requiredBySection;

        public StaticFormRequirementsService(params (string Section, string FieldKey)[] requiredFields)
        {
            _requiredBySection = requiredFields
                .GroupBy(item => item.Section, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.FieldKey).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(_requiredBySection.TryGetValue(section, out var fields) ? fields.ToList() : new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
        {
            var requiredFields = _requiredBySection.TryGetValue(section, out var fields)
                ? fields
                : new List<string>();

            var properties = model.GetType()
                .GetProperties()
                .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();
            foreach (var field in requiredFields)
            {
                if (!properties.TryGetValue(field, out var property))
                {
                    missing.Add(field);
                    continue;
                }

                var value = property.GetValue(model);
                if (value is null)
                {
                    missing.Add(field);
                    continue;
                }

                if (value is string text && string.IsNullOrWhiteSpace(text))
                {
                    missing.Add(field);
                }
            }

            return Task.FromResult(missing);
        }
    }

    private sealed class NoopEnrichmentService : ILegalEntityEnrichmentService
    {
        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>());

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult<CleanedAddress?>(null);

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
