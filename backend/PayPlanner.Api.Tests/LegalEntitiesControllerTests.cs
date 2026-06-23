using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
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

public class LegalEntitiesControllerTests
{
    [Fact]
    public async Task AdminFilter_ReturnsForbid_ForUserWithoutAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var controller = CreateController(db, userId, isAdmin: true);
        var context = CreateAdminActionExecutingContext(controller, userId);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.False(nextCalled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task AdminFilter_AllowsUserWithAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId, isAdmin: false);
        var context = CreateAdminActionExecutingContext(controller, userId);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task GetById_HidesMainCompany_WhenAdminClaimIsNotAdminInDatabase()
    {
        await using var db = await CreateContextAsync();
        var companyId = await CreateLegalEntityAsync(db, "Основная компания");
        var userId = await CreateUserAsync(db);
        var controller = CreateController(db, userId, isAdmin: true);
        await controller.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                CompanyIds = [companyId],
                DefaultCompanyId = companyId,
            },
            CancellationToken.None);

        var action = await controller.GetById(companyId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task GetById_ReturnsMainCompany_ForUserWithAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var companyId = await CreateLegalEntityAsync(db, "Основная компания");
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId, isAdmin: false);
        await controller.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                CompanyIds = [companyId],
                DefaultCompanyId = companyId,
            },
            CancellationToken.None);

        var action = await controller.GetById(companyId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);
        Assert.Equal(companyId, dto.Id);
    }

    [Fact]
    public async Task UpdateMainCompanySettings_SavesMultipleCompanies_AndHidesThemFromLegalEntitiesRegistry()
    {
        await using var db = await CreateContextAsync();
        var firstCompanyId = await CreateLegalEntityAsync(db, "Основная компания 1");
        var secondCompanyId = await CreateLegalEntityAsync(db, "Основная компания 2");
        var regularCompanyId = await CreateLegalEntityAsync(db, "Обычное юрлицо");

        var controller = CreateController(db, userId: 31);

        var settingsAction = await controller.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                CompanyIds = [firstCompanyId, secondCompanyId],
                DefaultCompanyId = secondCompanyId,
            },
            CancellationToken.None);

        var settingsOk = Assert.IsType<OkObjectResult>(settingsAction.Result);
        var settingsDto = Assert.IsType<MainCompanySettingsDto>(settingsOk.Value);
        Assert.Equal(secondCompanyId, settingsDto.DefaultCompanyId);
        Assert.Equal(new[] { firstCompanyId, secondCompanyId }, settingsDto.Companies.Select(item => item.Id).OrderBy(id => id));

        var listAction = await controller.GetAll(null, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listAction.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<LegalEntityListItemDto>>(listOk.Value).ToList();

        Assert.Single(items);
        Assert.Equal(regularCompanyId, items[0].Id);
    }

    [Fact]
    public async Task GetAllAndPaged_ForRegularUser_ReturnOnlyAssignedLegalEntities()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var otherUserId = await CreateUserAsync(db);
        var assignedId = await CreateLegalEntityAsync(db, "Назначенное юрлицо", userId);
        var otherId = await CreateLegalEntityAsync(db, "Чужое юрлицо", otherUserId);
        var unassignedId = await CreateLegalEntityAsync(db, "Юрлицо без ответственного");

        var controller = CreateController(db, userId, isAdmin: false);

        var listAction = await controller.GetAll(null, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listAction.Result);
        var listItems = Assert.IsAssignableFrom<IEnumerable<LegalEntityListItemDto>>(listOk.Value).ToList();
        var listItem = Assert.Single(listItems);
        Assert.Equal(assignedId, listItem.Id);

        var pagedAction = await controller.GetPaged(
            search: null,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);
        var pagedOk = Assert.IsType<OkObjectResult>(pagedAction.Result);
        var page = Assert.IsType<PagedResponse<LegalEntityListItemDto>>(pagedOk.Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(assignedId, Assert.Single(page.Items).Id);

        var assignedResult = await controller.GetById(assignedId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(assignedResult.Result);
        Assert.IsType<NotFoundResult>((await controller.GetById(otherId, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.GetById(unassignedId, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetAllAndPaged_WithCounterpartyRole_FilterLegalEntities()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var clientStatusId = await CreateClientStatusAsync(db, "Клиент", ClientStatusSystemKeys.Client);
        var supplierStatusId = await CreateClientStatusAsync(db, "Поставщик", ClientStatusSystemKeys.Supplier);
        var creditorStatusId = await CreateClientStatusAsync(db, "Кредитор", ClientStatusSystemKeys.Creditor);
        var clientId = await CreateLegalEntityAsync(db, "ООО Клиент", clientStatusId: clientStatusId);
        var mixedId = await CreateLegalEntityAsync(db, "ООО Клиент и поставщик", clientStatusId: supplierStatusId, isClientAndSupplier: true);
        var supplierId = await CreateLegalEntityAsync(db, "ООО Поставщик", clientStatusId: supplierStatusId);
        var creditorId = await CreateLegalEntityAsync(db, "ООО Кредитор", clientStatusId: creditorStatusId);
        var controller = CreateController(db, userId);

        var clientAction = await controller.GetAll(null, CancellationToken.None, "client");
        var clientOk = Assert.IsType<OkObjectResult>(clientAction.Result);
        var clientItems = Assert.IsAssignableFrom<IEnumerable<LegalEntityListItemDto>>(clientOk.Value).ToList();

        Assert.Contains(clientItems, item => item.Id == clientId);
        Assert.Contains(clientItems, item => item.Id == mixedId);
        Assert.DoesNotContain(clientItems, item => item.Id == supplierId);
        Assert.DoesNotContain(clientItems, item => item.Id == creditorId);

        var supplierAction = await controller.GetPaged(
            search: null,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None,
            counterpartyRole: "supplier");
        var supplierOk = Assert.IsType<OkObjectResult>(supplierAction.Result);
        var page = Assert.IsType<PagedResponse<LegalEntityListItemDto>>(supplierOk.Value);

        Assert.Contains(page.Items, item => item.Id == mixedId);
        Assert.Contains(page.Items, item => item.Id == supplierId);
        Assert.Contains(page.Items, item => item.Id == creditorId);
        Assert.DoesNotContain(page.Items, item => item.Id == clientId);
    }

    [Fact]
    public async Task Create_ForRegularUser_AssignsCurrentUserAsResponsible()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var otherUserId = await CreateUserAsync(db);
        var controller = CreateController(db, userId, isAdmin: false);
        var request = BuildRequest();
        request.ResponsibleUserId = otherUserId;

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(created.Value);
        Assert.Equal(userId, dto.ResponsibleUserId);

        var storedResponsibleId = await db.LegalEntities
            .Where(entity => entity.Id == dto.Id)
            .Select(entity => entity.ResponsibleUserId)
            .SingleAsync();
        Assert.Equal(userId, storedResponsibleId);
    }

    [Fact]
    public async Task WriteEndpoints_RequireLegalEntityActionPermissions()
    {
        await using var db = await CreateContextAsync();
        var noCreateUserId = await CreateUserAsync(db);
        var noEditUserId = await CreateUserAsync(db);
        var noDeleteUserId = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, noCreateUserId, canCreate: false, canEdit: true, canDelete: true);
        await SetLegalEntityPermissionAsync(db, noEditUserId, canCreate: true, canEdit: false, canDelete: true);
        await SetLegalEntityPermissionAsync(db, noDeleteUserId, canCreate: true, canEdit: true, canDelete: false);

        var editEntityId = await CreateLegalEntityAsync(db, "Юрлицо без права редактирования", responsibleUserId: noEditUserId);
        var deleteEntityId = await CreateLegalEntityAsync(db, "Юрлицо без права удаления", responsibleUserId: noDeleteUserId);

        var createResult = await CreateController(db, noCreateUserId, isAdmin: false).Create(
            BuildRequest(),
            CancellationToken.None);
        var updateResult = await CreateController(db, noEditUserId, isAdmin: false).Update(
            editEntityId,
            BuildRequest(),
            CancellationToken.None);
        var deleteResult = await CreateController(db, noDeleteUserId, isAdmin: false).Delete(
            deleteEntityId,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(createResult.Result);
        Assert.IsType<ForbidResult>(updateResult.Result);
        Assert.IsType<ForbidResult>(deleteResult);
        Assert.False(await db.LegalEntities.AnyAsync(item => item.ShortName == BuildRequest().ShortName));

        var editEntity = await db.LegalEntities.AsNoTracking().SingleAsync(item => item.Id == editEntityId);
        Assert.Equal("Юрлицо без права редактирования", editEntity.ShortName);
        Assert.True(await db.LegalEntities.AnyAsync(item => item.Id == deleteEntityId));
    }

    [Fact]
    public async Task Delete_RemovesDeletedCompanyFromMainCompanySettings()
    {
        await using var db = await CreateContextAsync();
        var firstCompanyId = await CreateLegalEntityAsync(db, "Компания для удаления");
        var secondCompanyId = await CreateLegalEntityAsync(db, "Компания которая остается");

        var controller = CreateController(db, userId: 32);
        await controller.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                CompanyIds = [firstCompanyId, secondCompanyId],
                DefaultCompanyId = firstCompanyId,
            },
            CancellationToken.None);

        var deleteResult = await controller.Delete(firstCompanyId, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResult);

        var settingsAction = await controller.GetMainCompanySettings(CancellationToken.None);
        var settingsOk = Assert.IsType<OkObjectResult>(settingsAction.Result);
        var settingsDto = Assert.IsType<MainCompanySettingsDto>(settingsOk.Value);

        Assert.Single(settingsDto.Companies);
        Assert.Equal(secondCompanyId, settingsDto.Companies[0].Id);
        Assert.Equal(secondCompanyId, settingsDto.DefaultCompanyId);
    }

    [Fact]
    public async Task UpdateMainCompanySettings_RemovesStaleUserCompanyAccesses()
    {
        await using var db = await CreateContextAsync();
        var firstCompanyId = await CreateLegalEntityAsync(db, "Основная компания 1");
        var secondCompanyId = await CreateLegalEntityAsync(db, "Основная компания 2");

        var role = new Role
        {
            Name = "user",
            Description = "User",
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "employee@example.com",
            PasswordHash = "hash",
            FullName = "Employee",
            RoleId = role.Id,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserCompanyAccesses.AddRange(
            new UserCompanyAccess { UserId = user.Id, LegalEntityId = firstCompanyId },
            new UserCompanyAccess { UserId = user.Id, LegalEntityId = secondCompanyId });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 33);
        var action = await controller.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                CompanyIds = [firstCompanyId],
                DefaultCompanyId = firstCompanyId,
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);

        var accessCompanyIds = await db.UserCompanyAccesses
            .AsNoTracking()
            .Where(item => item.UserId == user.Id)
            .Select(item => item.LegalEntityId)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal([firstCompanyId], accessCompanyIds);
    }

    [Fact]
    public async Task Create_RoundTripsNestedPayload_IncludingImagesAndClientLinks()
    {
        await using var db = await CreateContextAsync();
        var firstClientId = await CreateClientAsync(db, "Клиент 1");
        var secondClientId = await CreateClientAsync(db, "Клиент 2");

        var controller = CreateController(db, userId: 42);
        var request = BuildRequest();
        request.ClientIds.Add(firstClientId);
        request.ClientIds.Add(secondClientId);
        var expectedAddress = Assert.Single(request.Addresses!, address => address.IsPrimary).UnrestrictedValue!;

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(created.Value);

        Assert.Equal(request.ShortName, dto.ShortName);
        Assert.Null(dto.LogoDataUrl);
        Assert.Null(dto.SealDataUrl);
        Assert.Null(dto.DirectorSignatureDataUrl);
        Assert.Equal(request.DocumentDirectorJson, dto.DocumentDirectorJson);
        Assert.Equal(request.ChiefAccountantJson, dto.ChiefAccountantJson);
        Assert.Equal(2, dto.Clients.Count);
        Assert.Equal(expectedAddress, dto.Address);

        var primaryAddress = Assert.Single(dto.Addresses, address => address.IsPrimary);
        Assert.Equal(1, primaryAddress.Type);
        Assert.Equal("446200", primaryAddress.PostalCode);
        Assert.Equal("Россия", primaryAddress.Country);
        Assert.Equal("Самарская обл", primaryAddress.RegionWithType);
        Assert.Equal("Безенчукский р-н", primaryAddress.AreaWithType);
        Assert.Equal("г Новокуйбышевск", primaryAddress.CityWithType);
        Assert.Equal("п Гранный", primaryAddress.SettlementWithType);
        Assert.Equal("ул Миронова", primaryAddress.StreetWithType);
        Assert.Equal("9", primaryAddress.House);
        Assert.Equal("1", primaryAddress.Flat);
        Assert.Equal("fias-1", primaryAddress.FiasId);
        Assert.Equal("kladr-1", primaryAddress.KladrId);
        Assert.Equal("53.095", primaryAddress.GeoLat);
        Assert.Equal("49.945", primaryAddress.GeoLon);

        var stored = await db.LegalEntities
            .Include(entity => entity.Clients)
            .Include(entity => entity.Addresses)
            .SingleAsync();

        Assert.Equal(request.LogoDataUrl, stored.LogoDataUrl);
        Assert.Equal(request.SealDataUrl, stored.SealDataUrl);
        Assert.Equal(request.DirectorSignatureDataUrl, stored.DirectorSignatureDataUrl);
        Assert.Equal(request.DocumentDirectorJson, stored.DocumentDirectorJson);
        Assert.Equal(request.ChiefAccountantJson, stored.ChiefAccountantJson);
        Assert.Equal(2, stored.Clients.Count);
        Assert.Equal(expectedAddress, stored.Address);

        var auditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(item => item.ObjectType == "legal-entity" && item.ObjectId == stored.Id);

        Assert.NotNull(auditEvent);
        Assert.Equal("Created", auditEvent!.Action);
    }

    [Fact]
    public async Task Update_PreservesStructuredAddresses_AndUsesCorrectAddressTypeIds()
    {
        await using var db = await CreateContextAsync();
        var entity = new LegalEntity
        {
            ShortName = "Тестовое юрлицо",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 7);
        var request = BuildRequest();
        request.Addresses =
        [
            new LegalEntityAddressRequest
            {
                Type = 1,
                Value = "Адрес регистрации",
                UnrestrictedValue = "Адрес регистрации",
                PostalCode = "446200",
                Country = "Россия",
                RegionWithType = "Самарская обл",
                AreaWithType = "Безенчукский р-н",
                CityWithType = "г Новокуйбышевск",
                SettlementWithType = "п Гранный",
                StreetWithType = "ул Миронова",
                House = "9",
                Flat = "1",
                FiasId = "fias-registration",
                KladrId = "kladr-registration",
                GeoLat = "53.095",
                GeoLon = "49.945",
                IsPrimary = true,
            },
            new LegalEntityAddressRequest
            {
                Type = 2,
                Value = "Фактический адрес",
                CityWithType = "г Самара",
                StreetWithType = "ул Ленинградская",
                House = "15",
            },
            new LegalEntityAddressRequest
            {
                Type = 3,
                Value = "Юридический адрес",
                CityWithType = "г Тольятти",
                StreetWithType = "ул Победы",
                House = "4",
            },
            new LegalEntityAddressRequest
            {
                Type = 9,
                Value = "Иной адрес",
                CityWithType = "г Чапаевск",
                StreetWithType = "ул Рабочая",
                House = "3",
            },
        ];

        var action = await controller.Update(entity.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);
        var types = dto.Addresses.Select(address => address.Type).OrderBy(type => type).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 9 }, types);
        var expectedAddress = request.Addresses![0].UnrestrictedValue!;
        Assert.Equal(expectedAddress, dto.Address);

        var storedAddresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.LegalEntityId == entity.Id)
            .OrderBy(item => item.Type)
            .ToListAsync();

        Assert.Equal(4, storedAddresses.Count);
        Assert.Equal("Россия", storedAddresses[0].Country);
        Assert.Equal("446200", storedAddresses[0].PostalCode);
        Assert.Equal("Самарская обл", storedAddresses[0].RegionWithType);
        Assert.Equal("Безенчукский р-н", storedAddresses[0].AreaWithType);
        Assert.Equal("г Новокуйбышевск", storedAddresses[0].CityWithType);
        Assert.Equal("п Гранный", storedAddresses[0].SettlementWithType);
        Assert.Equal("ул Миронова", storedAddresses[0].StreetWithType);
        Assert.Equal("9", storedAddresses[0].House);
        Assert.Equal("1", storedAddresses[0].Flat);
        Assert.Equal("fias-registration", storedAddresses[0].FiasId);
        Assert.Equal("kladr-registration", storedAddresses[0].KladrId);
        Assert.Equal("53.095", storedAddresses[0].GeoLat);
        Assert.Equal("49.945", storedAddresses[0].GeoLon);
        Assert.Equal("г Самара", storedAddresses[1].CityWithType);
        Assert.Equal("г Тольятти", storedAddresses[2].CityWithType);
        Assert.Equal("г Чапаевск", storedAddresses[3].CityWithType);

        var storedEntity = await db.LegalEntities
            .AsNoTracking()
            .SingleAsync(item => item.Id == entity.Id);
        Assert.Equal(expectedAddress, storedEntity.Address);

        var auditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(item => item.ObjectType == "legal-entity" && item.ObjectId == entity.Id && item.Action == "Updated");

        Assert.NotNull(auditEvent);
    }

    [Fact]
    public async Task Update_DeduplicatesEquivalentAddresses_AndKeepsSinglePrimary()
    {
        await using var db = await CreateContextAsync();
        var entity = new LegalEntity
        {
            ShortName = "Тест адресов",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 8);
        var request = BuildRequest();
        request.Addresses =
        [
            new LegalEntityAddressRequest
            {
                Type = 3,
                Value = "Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
                UnrestrictedValue = "446200, Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
                PostalCode = "446200",
                CityWithType = "г Новокуйбышевск",
                StreetWithType = "ул Миронова",
                House = "9",
                IsPrimary = true,
            },
            new LegalEntityAddressRequest
            {
                Type = 3,
                Value = "446200 Самарская обл г Новокуйбышевск ул Миронова дом 9",
                UnrestrictedValue = "446200 Самарская обл г Новокуйбышевск ул Миронова дом 9",
                PostalCode = "446200",
                CityWithType = "Новокуйбышевск",
                StreetWithType = "Миронова",
                House = "9",
                IsPrimary = false,
            },
        ];

        var action = await controller.Update(entity.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);
        Assert.Single(dto.Addresses);
        Assert.True(dto.Addresses[0].IsPrimary);

        var storedAddresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.LegalEntityId == entity.Id)
            .ToListAsync();

        Assert.Single(storedAddresses);
        Assert.Equal("446200", storedAddresses[0].PostalCode);
    }

    [Fact]
    public async Task Update_ReplacesExistingAddresses_WhenEntityAlreadyHasStoredAddressRows()
    {
        await using var db = await CreateContextAsync();
        var entity = new LegalEntity
        {
            ShortName = "Замена адресов",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        db.AddressRecords.Add(new AddressRecord
        {
            LegalEntityId = entity.Id,
            Type = AddressType.Legal,
            Value = "Старый адрес",
            CityWithType = "г Самара",
            StreetWithType = "ул Старая",
            House = "1",
            IsPrimary = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 12);
        var request = BuildRequest();
        request.Addresses =
        [
            new LegalEntityAddressRequest
            {
                Type = 3,
                Value = "Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
                UnrestrictedValue = "446200, Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
                PostalCode = "446200",
                CityWithType = "г Новокуйбышевск",
                StreetWithType = "ул Миронова",
                House = "9",
                IsPrimary = true,
            },
        ];

        var action = await controller.Update(entity.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);
        Assert.Single(dto.Addresses);
        Assert.True(dto.Addresses[0].IsPrimary);

        var storedAddresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.LegalEntityId == entity.Id)
            .ToListAsync();

        Assert.Single(storedAddresses);
        Assert.Equal("446200", storedAddresses[0].PostalCode);
        Assert.Equal("г Новокуйбышевск", storedAddresses[0].CityWithType);
        Assert.Null(storedAddresses[0].ClientId);
        Assert.Equal(entity.Id, storedAddresses[0].LegalEntityId);
        Assert.DoesNotContain(storedAddresses, item => item.Value == "Старый адрес");
    }

    [Fact]
    public async Task Update_PreservesExistingImages_WhenTheyWereNotChanged()
    {
        await using var db = await CreateContextAsync();
        var entity = new LegalEntity
        {
            ShortName = "Тест изображений",
            LogoDataUrl = "data:image/png;base64,AAAA",
            SealDataUrl = "data:image/png;base64,BBBB",
            DirectorSignatureDataUrl = "data:image/png;base64,CCCC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 9);
        var request = BuildRequest();
        request.LogoDataUrl = null;
        request.SealDataUrl = null;
        request.DirectorSignatureDataUrl = null;

        var action = await controller.Update(entity.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);

        Assert.Null(dto.LogoDataUrl);
        Assert.Null(dto.SealDataUrl);
        Assert.Null(dto.DirectorSignatureDataUrl);

        var stored = await db.LegalEntities.AsNoTracking().SingleAsync(item => item.Id == entity.Id);
        Assert.Equal("data:image/png;base64,AAAA", stored.LogoDataUrl);
        Assert.Equal("data:image/png;base64,BBBB", stored.SealDataUrl);
        Assert.Equal("data:image/png;base64,CCCC", stored.DirectorSignatureDataUrl);
    }

    [Fact]
    public async Task GetById_ReturnsStoredAddresses()
    {
        await using var db = await CreateContextAsync();
        var entity = new LegalEntity
        {
            ShortName = "Прочитать адреса",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        db.AddressRecords.Add(new AddressRecord
        {
            LegalEntityId = entity.Id,
            Type = AddressType.Legal,
            Value = "Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9",
            PostalCode = "446200",
            CityWithType = "г Новокуйбышевск",
            StreetWithType = "ул Миронова",
            House = "9",
            IsPrimary = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 13);
        var action = await controller.GetById(entity.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);

        var address = Assert.Single(dto.Addresses);
        Assert.Equal("446200", address.PostalCode);
        Assert.Equal("г Новокуйбышевск", address.CityWithType);
        Assert.Equal("ул Миронова", address.StreetWithType);
    }

    [Fact]
    public async Task Update_ClearsImages_WhenTheyWereExplicitlyChanged()
    {
        await using var db = await CreateContextAsync();
        var entity = new LegalEntity
        {
            ShortName = "Очистка изображений",
            LogoDataUrl = "data:image/png;base64,AAAA",
            SealDataUrl = "data:image/png;base64,BBBB",
            DirectorSignatureDataUrl = "data:image/png;base64,CCCC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 10);
        var request = BuildRequest();
        request.LogoDataUrl = null;
        request.SealDataUrl = null;
        request.DirectorSignatureDataUrl = null;
        request.LogoChanged = true;
        request.SealChanged = true;
        request.DirectorSignatureChanged = true;

        var action = await controller.Update(entity.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(ok.Value);

        Assert.Null(dto.LogoDataUrl);
        Assert.Null(dto.SealDataUrl);
        Assert.Null(dto.DirectorSignatureDataUrl);

        var stored = await db.LegalEntities.AsNoTracking().SingleAsync(item => item.Id == entity.Id);
        Assert.Null(stored.LogoDataUrl);
        Assert.Null(stored.SealDataUrl);
        Assert.Null(stored.DirectorSignatureDataUrl);
    }

    [Fact]
    public async Task Update_RejectsOversizedInlineAssetPayload()
    {
        await using var db = await CreateContextAsync();
        var entity = new LegalEntity
        {
            ShortName = "Перегруженное юрлицо",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 11);
        var request = BuildRequest();
        request.LogoDataUrl = "data:image/png;base64," + new string('A', 360_000);
        request.LogoChanged = true;

        var action = await controller.Update(entity.Id, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("Логотип", message);
    }

    [Fact]
    public async Task Create_DoesNotCreateServiceLanguage_FromQuestionMarks()
    {
        await using var db = await CreateContextAsync();
        db.ServiceLanguages.Add(new ServiceLanguageDictionaryItem
        {
            Name = "Русский",
            Description = "Русский язык",
            ColorHex = "#9333EA",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 17);
        var request = BuildRequest();
        request.ServiceLanguage = new string('?', 7);

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(created.Value);
        Assert.Null(dto.ServiceLanguage);

        var serviceLanguages = await db.ServiceLanguages.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.Single(serviceLanguages);
        Assert.Equal("Русский", serviceLanguages[0].Name);

        var storedEntity = await db.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Null(storedEntity.ServiceLanguage);
    }

    [Fact]
    public async Task Suggest_DeduplicatesSameInnSuggestions_AndKeepsFullAddress()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(
            db,
            new StubLegalEntityEnrichmentService
            {
                Suggestions =
                [
                    new LegalEntitySuggestion(
                        "ИП Кожеваткин Андрей Владимирович",
                        "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
                        "633009620345",
                        null,
                        "317631300153603",
                        "446200, Россия, Самарская область, г. Новокуйбышевск",
                        null,
                        null,
                        null),
                    new LegalEntitySuggestion(
                        "ИП Кожеваткин Андрей Владимирович",
                        "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
                        "633009620345",
                        null,
                        "307631300200020",
                        "446200, Россия, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9",
                        null,
                        null,
                        null),
                    new LegalEntitySuggestion(
                        "ООО Пример",
                        "Общество с ограниченной ответственностью Пример",
                        "7707083893",
                        "770701001",
                        "1027700132195",
                        "109012, Россия, г. Москва, ул. Ильинка, д. 9",
                        null,
                        null,
                        null),
                ]
            },
            userId: 18);

        var action = await controller.Suggest(
            new LegalEntitySuggestionRequest
            {
                Query = "633009620345",
                Limit = 6,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<LegalEntitySuggestionDto>>(ok.Value).ToList();

        Assert.Equal(2, items.Count);
        var entrepreneur = Assert.Single(items, item => item.Inn == "633009620345");
        Assert.Contains("446200", entrepreneur.Address);
        Assert.Contains("Новокуйбышевск", entrepreneur.Address);
    }

    [Fact]
    public async Task Update_ReplacesClientLinks_AndClearsRemovedClients()
    {
        await using var db = await CreateContextAsync();
        var firstClientId = await CreateClientAsync(db, "Клиент 1");
        var secondClientId = await CreateClientAsync(db, "Клиент 2");
        var thirdClientId = await CreateClientAsync(db, "Клиент 3");

        var controller = CreateController(db, userId: 56);
        var createRequest = BuildRequest();
        createRequest.ClientIds = [firstClientId, secondClientId];

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(created.Value);

        var updateRequest = BuildRequest();
        updateRequest.ClientIds = [thirdClientId];

        var updateAction = await controller.Update(dto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updated = Assert.IsType<LegalEntityDetailDto>(ok.Value);
        Assert.Single(updated.Clients);
        Assert.Equal(thirdClientId, updated.Clients[0].Id);

        var clients = await db.Clients.AsNoTracking()
            .Where(client => client.Id == firstClientId || client.Id == secondClientId || client.Id == thirdClientId)
            .OrderBy(client => client.Id)
            .ToListAsync();

        Assert.Null(clients.Single(client => client.Id == firstClientId).LegalEntityId);
        Assert.Null(clients.Single(client => client.Id == secondClientId).LegalEntityId);
        Assert.Equal(dto.Id, clients.Single(client => client.Id == thirdClientId).LegalEntityId);
    }

    [Fact]
    public async Task Delete_RejectsLegalEntity_WhenLinkedClientsExist()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Клиент для конфликта");

        var controller = CreateController(db, userId: 57);
        var createRequest = BuildRequest();
        createRequest.ClientIds = [clientId];

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<LegalEntityDetailDto>(created.Value);

        var deleteAction = await controller.Delete(dto.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(deleteAction);
        Assert.NotNull(conflict.Value);
        Assert.NotNull(await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == dto.Id));
    }

    [Fact]
    public async Task Delete_RemovesUserCompanyAccesses_AndSoftDeletesAddresses()
    {
        await using var db = await CreateContextAsync();
        var employeeUserId = await CreateUserAsync(db);

        var entity = new LegalEntity
        {
            ShortName = "Удаляемое юрлицо",
            FullName = "Удаляемое юридическое лицо",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        db.AddressRecords.Add(new AddressRecord
        {
            LegalEntityId = entity.Id,
            Type = AddressType.Legal,
            Value = "Адрес для удаления",
            IsPrimary = true,
        });
        db.UserCompanyAccesses.Add(new UserCompanyAccess
        {
            UserId = employeeUserId,
            LegalEntityId = entity.Id,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 58);
        var deleteAction = await controller.Delete(entity.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteAction);
        Assert.Null(await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == entity.Id));
        Assert.Equal(0, await db.AddressRecords.AsNoTracking().CountAsync(item => item.LegalEntityId == entity.Id));
        Assert.Equal(0, await db.UserCompanyAccesses.AsNoTracking().CountAsync(item => item.LegalEntityId == entity.Id));
    }

    private static LegalEntityRequest BuildRequest()
    {
        return new LegalEntityRequest
        {
            ShortName = "ИП Кожеваткин А.В.",
            FullName = "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
            Inn = "633009620345",
            Kpp = "123456789",
            Ogrn = "1234567890123",
            Address = "Самарская обл, г Новокуйбышевск, ул. Миронова, д. 9",
            Phone = "+7 (927) 000-00-00",
            Email = "office@example.com",
            Director = "Кожеваткин Андрей Владимирович",
            Country = "Россия",
            BaseCurrency = "RUB",
            ServiceLanguage = "Русский",
            OrganizationForm = "ИП",
            Timezone = "Asia/Novosibirsk",
            TaxSystem = "УСН",
            TaxSystemMode = "Доходы",
            IncomeTaxRate = "6",
            IncomeExpenseTaxRate = "15",
            VatWithoutVat = true,
            Okved = "69.10",
            Okpo = "12345678",
            Okato = "36413000000",
            Oktmo = "36713000",
            Okogu = "4210015",
            Okfs = "16",
            Okopf = "50102",
            Pfr = "077012345678",
            Fss = "6300000000",
            CharterCapital = "0",
            TaxRegistrationDate = "2026-04-10",
            TaxOfficeCode = "6330",
            TaxRegion = "Самарская область",
            IfnsName = "Межрайонная ИФНС России № 16 по Самарской области",
            TaxOfficePhone = "+7 (846) 000-00-00",
            TreasuryInn = "6317000000",
            TreasuryKpp = "631701001",
            TreasuryRecipientName = "УФК по Самарской области",
            TreasuryBankBik = "044525104",
            TreasuryBankName = "ООО Банк Точка",
            TreasuryAccountNumber = "40802810414500006073",
            TreasuryCorrAccountNumber = "30101810745374525104",
            TreasuryKbk = "18210501011011000110",
            BankAccountsJson = "[{\"bank\":\"Точка\",\"name\":\"Основной\",\"bik\":\"044525104\",\"currency\":\"RUB\",\"accountNumber\":\"40802810414500006073\",\"accountType\":\"расчетный\"}]",
            CashRegistersJson = "[{\"name\":\"ККТ-1\",\"currency\":\"RUB\",\"access\":\"owner\"}]",
            CardsJson = "[{\"number\":\"220220******0001\",\"bank\":\"Точка\",\"currency\":\"RUB\",\"holder\":\"Кожеваткин А.В.\"}]",
            RequisitesEnglishJson = "{\"organizationName\":\"ElitPravo\",\"fullName\":\"Individual entrepreneur Kozhevatkin Andrey Vladimirovich\",\"form\":\"IE\",\"legalAddress\":\"Samara region\",\"actualAddress\":\"Samara region\",\"position\":\"Director\",\"lastName\":\"Kozhevatkin\",\"firstName\":\"Andrey\",\"middleName\":\"Vladimirovich\"}",
            LogoDataUrl = "data:image/png;base64,QUFBQUFB",
            SealDataUrl = "data:image/png;base64,QkJCQkJC",
            DirectorSignatureDataUrl = "data:image/png;base64,Q0NDQ0ND",
            DocumentDirectorJson = "{\"position\":\"Директор\",\"lastName\":\"Кожеваткин\",\"firstName\":\"Андрей\",\"middleName\":\"Владимирович\",\"noMiddleName\":false}",
            ChiefAccountantJson = "{\"position\":\"Главный бухгалтер\",\"lastName\":\"Кожеваткина\",\"firstName\":\"Анна\",\"middleName\":\"Игоревна\",\"noMiddleName\":false}",
            ContactPhonesJson = "[\"+7 (927) 000-00-00\",\"+7 (927) 111-11-11\"]",
            ContactEmailsJson = "[\"office@example.com\",\"docs@example.com\"]",
            Notes = "Тестовая заметка",
            Addresses =
            [
                new LegalEntityAddressRequest
                {
                    Type = 1,
                    Value = "Самарская обл, г Новокуйбышевск, ул Миронова, д 9, кв 1",
                    UnrestrictedValue = "446200, Россия, Самарская обл, г Новокуйбышевск, ул Миронова, д 9, кв 1",
                    PostalCode = "446200",
                    Country = "Россия",
                    RegionWithType = "Самарская обл",
                    AreaWithType = "Безенчукский р-н",
                    CityWithType = "г Новокуйбышевск",
                    SettlementWithType = "п Гранный",
                    StreetWithType = "ул Миронова",
                    House = "9",
                    Flat = "1",
                    FiasId = "fias-1",
                    KladrId = "kladr-1",
                    GeoLat = "53.095",
                    GeoLon = "49.945",
                    IsPrimary = true,
                },
            ],
        };
    }

    private static async Task<int> CreateClientAsync(PaymentContext db, string name)
    {
        var client = new Client
        {
            Name = name,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<int> CreateLegalEntityAsync(
        PaymentContext db,
        string shortName,
        int? responsibleUserId = null,
        int? clientStatusId = null,
        bool isClientAndSupplier = false)
    {
        var entity = new LegalEntity
        {
            ShortName = shortName,
            FullName = shortName,
            ResponsibleUserId = responsibleUserId,
            ClientStatusId = clientStatusId,
            IsClientAndSupplier = isClientAndSupplier,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateClientStatusAsync(PaymentContext db, string name, string systemKey)
    {
        var status = new ClientStatusEntity
        {
            Name = name,
            SystemKey = systemKey,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientStatuses.Add(status);
        await db.SaveChangesAsync();
        return status.Id;
    }

    private static async Task<int> CreateUserAsync(PaymentContext db, string? roleName = null)
    {
        var role = new Role
        {
            Name = roleName ?? $"user-{Guid.NewGuid():N}",
            Description = "User",
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = $"legal-entities-tests-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Employee",
            RoleId = role.Id,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task SetLegalEntityPermissionAsync(
        PaymentContext db,
        int userId,
        bool canCreate,
        bool canEdit,
        bool canDelete,
        bool canView = true)
    {
        var roleId = await db.Users
            .Where(item => item.Id == userId)
            .Select(item => item.RoleId)
            .SingleAsync();

        var permission = await db.RolePermissions
            .SingleOrDefaultAsync(item => item.RoleId == roleId && item.Section == RolePermissionCatalog.LegalEntities);
        if (permission is null)
        {
            permission = new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.LegalEntities,
                ViewScope = DataVisibilityScope.All,
            };
            db.RolePermissions.Add(permission);
        }

        permission.CanView = canView;
        permission.CanCreate = canCreate;
        permission.CanEdit = canEdit;
        permission.CanDelete = canDelete;
        permission.CanExport = true;
        permission.CanViewAnalytics = false;
        permission.ViewScope = DataVisibilityScope.All;
        await db.SaveChangesAsync();
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

    private static LegalEntitiesController CreateController(PaymentContext db, int userId, bool isAdmin = true)
        => CreateController(db, new NoopLegalEntityEnrichmentService(), userId, isAdmin);

    private static LegalEntitiesController CreateController(
        PaymentContext db,
        ILegalEntityEnrichmentService enrichmentService,
        int userId,
        bool isAdmin = true)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый пользователь"),
        };

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        var controller = new LegalEntitiesController(db, enrichmentService, new NoopFormRequirementsService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };

        return controller;
    }

    private static ActionExecutingContext CreateAdminActionExecutingContext(
        LegalEntitiesController controller,
        int userId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, $"User {userId}"),
                    new Claim(ClaimTypes.Role, "admin"),
                ],
                "TestAuth")),
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor
            {
                EndpointMetadata = [new AuthorizeAttribute { Policy = "Admin" }],
            });
        controller.ControllerContext = new ControllerContext(actionContext);

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller);
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private class NoopLegalEntityEnrichmentService : ILegalEntityEnrichmentService
    {
        public virtual Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>());

        public virtual Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult<CleanedAddress?>(null);

        public virtual Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
            => Task.FromResult<CleanedEmail?>(null);

        public virtual Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
            => Task.FromResult<CleanedPhone?>(null);

        public virtual Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
            => Task.FromResult<LegalEntityDetails?>(null);

        public virtual Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LegalEntitySuggestion>>(Array.Empty<LegalEntitySuggestion>());
    }

    private sealed class StubLegalEntityEnrichmentService : NoopLegalEntityEnrichmentService
    {
        public IReadOnlyList<LegalEntitySuggestion> Suggestions { get; init; } = Array.Empty<LegalEntitySuggestion>();

        public override Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
            => Task.FromResult(Suggestions);
    }
}
