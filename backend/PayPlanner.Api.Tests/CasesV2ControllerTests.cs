using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CasesV2ControllerTests
{
    [Fact]
    public async Task Get_AppliesOwnerVisibility()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var visibleClient = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.ClientCases.AddRange(
            new ClientCase { Client = visibleClient, Title = "Видимое дело", Status = ClientCaseStatus.Open },
            new ClientCase { Client = hiddenClient, Title = "Скрытое дело", Status = ClientCaseStatus.Open });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = GetAnonymousItems(ok.Value);
        var item = Assert.Single(items);

        Assert.Equal("Видимое дело", item.Title);
    }

    [Fact]
    public async Task Get_FiltersInactiveCasesByActivity()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "activity-owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент активности",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var inactiveCase = new ClientCase
        {
            Client = client,
            Title = "Давно без движения",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            LastTaskAt = DateTime.UtcNow.AddDays(-21),
            LastViewedAt = DateTime.UtcNow.AddDays(-20),
            ActivityConfirmedAt = DateTime.UtcNow.AddDays(-19),
            InactivityRemindersEnabled = true,
        };
        var activeCase = new ClientCase
        {
            Client = client,
            Title = "Недавно проверено",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            LastTaskAt = DateTime.UtcNow.AddDays(-21),
            LastViewedAt = DateTime.UtcNow.AddDays(-1),
            InactivityRemindersEnabled = true,
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(inactiveCase, activeCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 50,
            activity: "inactive",
            inactiveDays: 14,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.Single(GetAnonymousItems(ok.Value));
        Assert.Equal(inactiveCase.Id, item.Id);
    }

    [Fact]
    public async Task GetById_HiddenCaseReturnsAccessHintForExecutor()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое от исполнителя дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            IsHiddenFromExecutor = true,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var ownerController = CreateController(db, owner);
        var ownerResult = await ownerController.GetById(clientCase.Id, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(ownerResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var messageProperty = forbidden.Value!.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        Assert.Contains("Дело скрыто от исполнителя", Assert.IsType<string>(messageProperty.GetValue(forbidden.Value)), StringComparison.Ordinal);

        var managerController = CreateController(db, manager);
        var managerResult = await managerController.GetById(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(managerResult);
        Assert.Equal("Скрытое от исполнителя дело", Assert.IsType<ClientCase>(ok.Value).Title);
    }

    [Fact]
    public async Task GetById_AndRelatedParties_HideSensitiveContacts_WhenRoleCannotEditCases()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "readonly@example.com", "Наблюдатель");
        viewer.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanEdit = false;

        var client = new Client
        {
            Name = "Клиент с персональными данными",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
            Phone = "+79991234567",
            Email = "client@example.test",
            Address = "Самара, Полевая, 10",
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с ограниченным раскрытием",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = viewer.Id,
        };
        db.ClientCases.Add(clientCase);
        db.ClientCaseRelatedParties.Add(new ClientCaseRelatedParty
        {
            ClientCase = clientCase,
            SourceType = "client",
            DisplayName = "Связанный участник",
            Phone = "+79995554433",
            Email = "party@example.test",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var caseResult = await controller.GetById(clientCase.Id, CancellationToken.None);

        var caseOk = Assert.IsType<OkObjectResult>(caseResult);
        var dto = Assert.IsType<ClientCase>(caseOk.Value);
        Assert.False(dto.CanRevealSensitiveData);
        Assert.Equal(string.Empty, dto.Client!.Phone);
        Assert.Equal(string.Empty, dto.Client.Email);
        Assert.Equal(string.Empty, dto.Client.Address);

        var partiesResult = await controller.GetRelatedParties(clientCase.Id, includeArchived: false, CancellationToken.None);

        var partiesOk = Assert.IsType<OkObjectResult>(partiesResult.Result);
        var party = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(partiesOk.Value));
        Assert.Equal(string.Empty, party.Phone);
        Assert.Equal(string.Empty, party.Email);
    }

    [Fact]
    public async Task RequestAccess_RecordsRequestForHiddenCase()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое от исполнителя дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            IsHiddenFromExecutor = true,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.RequestAccess(clientCase.Id, new CasesV2Controller.RequestCaseAccessRequest
        {
            Reason = "Нужно проверить процессуальный срок.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseAccessRequestDto>(ok.Value);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal(owner.Id, dto.RequestedByUserId);
        Assert.Equal("Владелец", dto.RequestedByUserName);
        Assert.True(dto.IsHiddenFromExecutor);
        Assert.Equal("Нужно проверить процессуальный срок.", dto.Reason);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(owner.Id, clientCase.AccessRequestedByUserId);
        Assert.Equal("Владелец", clientCase.AccessRequestedByUserName);
        Assert.Equal("Нужно проверить процессуальный срок.", clientCase.AccessRequestReason);
        Assert.Contains("Запрошен доступ к делу", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Нужно проверить процессуальный срок", clientCase.EventHistory, StringComparison.Ordinal);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(manager.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CaseAccessRequested, notification.Type);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("Запрошен доступ к делу", notification.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccessRequests_ReturnsVisiblePendingRequests()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Исполнитель");
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var other = await CreateUserAsync(db, "other@example.com", "Другой");
        var client = new Client
        {
            Name = "Клиент с запросом",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var visibleCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело",
            CaseNumber = "CASE-HIDDEN",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            IsHiddenFromExecutor = true,
            AccessRequestedAt = new DateTime(2026, 5, 19, 9, 0, 0, DateTimeKind.Utc),
            AccessRequestedByUserId = owner.Id,
            AccessRequestedByUserName = "Исполнитель",
            AccessRequestReason = "Нужно проверить срок.",
        };
        var otherClient = new Client
        {
            Name = "Чужой клиент",
            IsActive = true,
            ResponsibleUserId = other.Id,
        };
        var otherCase = new ClientCase
        {
            Client = otherClient,
            Title = "Чужой запрос",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = other.Id,
            IsHiddenFromExecutor = true,
            AccessRequestedAt = new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc),
            AccessRequestedByUserId = other.Id,
            AccessRequestedByUserName = "Другой",
        };
        db.Clients.AddRange(client, otherClient);
        db.ClientCases.AddRange(visibleCase, otherCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, manager);

        var result = await controller.GetAccessRequests(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseAccessRequestListItemDto>>(ok.Value);
        Assert.Equal(2, items.Count);
        var item = Assert.Single(items, candidate => candidate.ClientCaseId == visibleCase.Id);
        Assert.Equal("CASE-HIDDEN", item.CaseNumber);
        Assert.Equal("Скрытое дело", item.Title);
        Assert.Equal("Клиент с запросом", item.OwnerName);
        Assert.Equal(owner.Id, item.ResponsibleUserId);
        Assert.Equal("Исполнитель", item.RequestedByUserName);
        Assert.Equal("Нужно проверить срок.", item.Reason);
    }

    [Fact]
    public async Task ResolveAccessRequest_GrantUnhidesCaseAndClearsPendingRequest()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Исполнитель");
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            IsHiddenFromExecutor = true,
            AccessRequestedAt = new DateTime(2026, 5, 19, 9, 0, 0, DateTimeKind.Utc),
            AccessRequestedByUserId = owner.Id,
            AccessRequestedByUserName = "Исполнитель",
            AccessRequestReason = "Нужен доступ.",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, manager);

        var result = await controller.ResolveAccessRequest(clientCase.Id, new CasesV2Controller.ResolveCaseAccessRequest
        {
            Grant = true,
            Comment = "Можно продолжать работу.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseAccessRequestResolutionDto>(ok.Value);
        Assert.True(dto.Granted);
        Assert.False(dto.IsHiddenFromExecutor);

        await db.Entry(clientCase).ReloadAsync();
        Assert.False(clientCase.IsHiddenFromExecutor);
        Assert.Null(clientCase.AccessRequestedAt);
        Assert.Null(clientCase.AccessRequestedByUserId);
        Assert.Equal(string.Empty, clientCase.AccessRequestedByUserName);
        Assert.Equal(string.Empty, clientCase.AccessRequestReason);
        Assert.Contains("Запрос доступа разрешен", clientCase.EventHistory, StringComparison.Ordinal);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(owner.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CaseAccessResolved, notification.Type);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("разрешен", notification.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAccessRequest_DenyKeepsCaseHiddenAndClearsPendingRequest()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Исполнитель");
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            IsHiddenFromExecutor = true,
            AccessRequestedAt = new DateTime(2026, 5, 19, 9, 0, 0, DateTimeKind.Utc),
            AccessRequestedByUserId = owner.Id,
            AccessRequestedByUserName = "Исполнитель",
            AccessRequestReason = "Нужен доступ.",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, manager);

        var result = await controller.ResolveAccessRequest(clientCase.Id, new CasesV2Controller.ResolveCaseAccessRequest
        {
            Grant = false,
            Comment = "Пока рано.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseAccessRequestResolutionDto>(ok.Value);
        Assert.False(dto.Granted);
        Assert.True(dto.IsHiddenFromExecutor);

        await db.Entry(clientCase).ReloadAsync();
        Assert.True(clientCase.IsHiddenFromExecutor);
        Assert.Null(clientCase.AccessRequestedAt);
        Assert.Null(clientCase.AccessRequestedByUserId);
        Assert.Equal(string.Empty, clientCase.AccessRequestedByUserName);
        Assert.Equal(string.Empty, clientCase.AccessRequestReason);
        Assert.Contains("Запрос доступа отклонен", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Пока рано", clientCase.EventHistory, StringComparison.Ordinal);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(owner.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CaseAccessResolved, notification.Type);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("отклонен", notification.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_AppliesRegistryScope()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var client = new Client
        {
            Name = "Клиент",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(
            new ClientCase { Client = client, Title = "Мое активное дело", ResponsibleUserId = viewer.Id, Status = ClientCaseStatus.Open },
            new ClientCase { Client = client, Title = "Чужое активное дело", ResponsibleUserId = otherResponsible.Id, Status = ClientCaseStatus.OnHold },
            new ClientCase { Client = client, Title = "Закрытое дело", ResponsibleUserId = otherResponsible.Id, Status = ClientCaseStatus.Closed },
            new ClientCase { Client = client, Title = "Проигранное дело", ResponsibleUserId = viewer.Id, Status = ClientCaseStatus.Lost });
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        async Task<IReadOnlyList<ClientCase>> LoadScope(string scope)
        {
            var result = await controller.Get(
                clientId: null,
                legalEntityId: null,
                status: null,
                search: null,
                responsibleUserId: null,
                caseType: null,
                priority: null,
                from: null,
                to: null,
                minFee: null,
                maxFee: null,
                sortBy: "title",
                sortDir: "asc",
                page: 1,
                pageSize: 50,
                scope: scope,
                ct: CancellationToken.None);

            return GetAnonymousItems(Assert.IsType<OkObjectResult>(result).Value);
        }

        Assert.Equal(new[] { "Мое активное дело", "Чужое активное дело" }, (await LoadScope("active")).Select(item => item.Title).ToArray());
        Assert.Equal(new[] { "Закрытое дело", "Проигранное дело" }, (await LoadScope("archive")).Select(item => item.Title).ToArray());
        Assert.Equal(new[] { "Мое активное дело", "Проигранное дело" }, (await LoadScope("mine")).Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task ExportRegistryCsv_AppliesVisibilityAndFilters()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var nowUtc = DateTime.UtcNow;
        var selectedZone = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new
            {
                Zone = zone,
                LocalNow = CalendarTimeZoneHelper.ToUserLocal(nowUtc, zone),
            })
            .FirstOrDefault(item => item.LocalNow.Date != nowUtc.Date);
        Assert.NotNull(selectedZone);
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var visibleClient = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.ClientCases.AddRange(
            new ClientCase
            {
                Client = visibleClient,
                Title = "Видимое дело",
                CaseNumber = "CASE-001",
                ResponsibleUserId = owner.Id,
                Status = ClientCaseStatus.Open,
                FeeAmount = 25_000m,
            },
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Скрытое дело",
                CaseNumber = "CASE-002",
                ResponsibleUserId = otherResponsible.Id,
                Status = ClientCaseStatus.Open,
                FeeAmount = 50_000m,
            });
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone!.Zone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var expectedFilePrefix = $"cases-registry-{selectedZone.LocalNow:yyyyMMdd}-";
        var pdfService = new FakeInvoiceDocumentPdfService("pdf");
        var controller = CreateController(db, owner, pdfService: pdfService);

        var result = await controller.ExportRegistryCsv(
            clientId: null,
            legalEntityId: null,
            status: ClientCaseStatus.Open,
            search: "дело",
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: "title",
            sortDir: "asc",
            scope: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.StartsWith(expectedFilePrefix, file.FileDownloadName);
        var csv = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("№ дела", csv);
        Assert.Contains("CASE-001", csv);
        Assert.Contains("Видимое дело", csv);
        Assert.Contains("В работе", csv);
        Assert.DoesNotContain("CASE-002", csv);
        Assert.DoesNotContain("Скрытое дело", csv);

        var excelResult = await controller.ExportRegistryExcel(
            clientId: null,
            legalEntityId: null,
            status: ClientCaseStatus.Open,
            search: "дело",
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: "title",
            sortDir: "asc",
            scope: null,
            ct: CancellationToken.None);

        var excelFile = Assert.IsType<FileContentResult>(excelResult);
        Assert.Equal("application/vnd.ms-excel; charset=utf-8", excelFile.ContentType);
        Assert.StartsWith(expectedFilePrefix, excelFile.FileDownloadName);
        Assert.EndsWith(".xls", excelFile.FileDownloadName);
        var excel = Encoding.UTF8.GetString(excelFile.FileContents);
        Assert.Contains("Excel.Sheet", excel);
        Assert.Contains("CASE-001", excel);
        Assert.Contains("Видимое дело", excel);
        Assert.DoesNotContain("CASE-002", excel);

        var pdfResult = await controller.ExportRegistryPdf(
            clientId: null,
            legalEntityId: null,
            status: ClientCaseStatus.Open,
            search: "дело",
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: "title",
            sortDir: "asc",
            scope: null,
            ct: CancellationToken.None);

        var pdfFile = Assert.IsType<FileContentResult>(pdfResult);
        Assert.Equal("application/pdf", pdfFile.ContentType);
        Assert.StartsWith(expectedFilePrefix, pdfFile.FileDownloadName);
        Assert.EndsWith(".pdf", pdfFile.FileDownloadName);
        Assert.Equal(Encoding.UTF8.GetBytes("pdf"), pdfFile.FileContents);
        Assert.Contains("Реестр дел", pdfService.LastHtml);
        Assert.Contains("CASE-001", pdfService.LastHtml);
        Assert.DoesNotContain("CASE-002", pdfService.LastHtml);
    }

    [Fact]
    public async Task ExportRegistryCsv_ReturnsForbidWhenExportDenied()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var permission = await db.RolePermissions.FirstAsync(item => item.RoleId == owner.RoleId && item.Section == RolePermissionCatalog.Cases);
        permission.CanExport = false;
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.ExportRegistryCsv(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            scope: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);

        var excelResult = await controller.ExportRegistryExcel(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            scope: null,
            ct: CancellationToken.None);
        var pdfResult = await controller.ExportRegistryPdf(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            scope: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(excelResult);
        Assert.IsType<ForbidResult>(pdfResult);
    }

    [Fact]
    public async Task Get_ClampsRegistryPageSize()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = viewer.Id };
        db.Clients.Add(client);
        for (var i = 0; i < 250; i++)
        {
            db.ClientCases.Add(new ClientCase
            {
                Client = client,
                Title = $"Дело {i:D3}",
                Status = ClientCaseStatus.Open,
            });
        }
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: "title",
            sortDir: "asc",
            page: 1,
            pageSize: 500,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, GetAnonymousInt(ok.Value, "pageSize"));
        Assert.Equal(200, GetAnonymousItems(ok.Value).Count);
    }

    [Fact]
    public async Task Get_AddsFinanceSummaryForVisibleCases()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - финансы",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.AddRange(
            new Payment
            {
                Client = client,
                ClientCase = clientCase,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 50000m,
                PaidAmount = 35000m,
                Date = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
                PlannedDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            },
            new Payment
            {
                ExpenseClient = client,
                ClientCase = clientCase,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Pending,
                Amount = 7000m,
                PaidAmount = 2000m,
                Date = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
                PlannedDate = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            },
            new Payment
            {
                Client = client,
                ClientCase = clientCase,
                Type = PaymentType.Income,
                Status = PaymentStatus.Cancelled,
                Amount = 999999m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc),
                PlannedDate = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc),
            });
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Земцов",
                Title = "Просроченная задача",
                Status = "inProgress",
                DueAt = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Земцов",
                Title = "Будущая задача",
                Status = "waiting",
                DueAt = new DateTime(2100, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Земцов",
                Title = "Готовая задача",
                Status = "done",
                DueAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var item = Assert.Single(GetAnonymousItems(Assert.IsType<OkObjectResult>(result).Value));

        Assert.Equal(50000m, item.IncomeTotal);
        Assert.Equal(35000m, item.IncomePaid);
        Assert.Equal(15000m, item.IncomeOutstanding);
        Assert.Equal(1, item.IncomeCount);
        Assert.Equal(7000m, item.ExpenseTotal);
        Assert.Equal(2000m, item.ExpensePaid);
        Assert.Equal(5000m, item.ExpenseOutstanding);
        Assert.Equal(1, item.ExpenseCount);
        Assert.Equal(2, item.OpenTaskCount);
        Assert.Equal(1, item.CompletedTaskCount);
        Assert.Equal(1, item.OverdueTaskCount);
        Assert.Equal(33, item.TaskProgressPercent);
        Assert.NotNull(item.LastTaskAt);
        Assert.True(item.LastTaskAt >= new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task GetSummary_ReturnsCompactCaseSnapshot()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Owner");
        var client = new Client
        {
            Name = "Client One",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Client One - bankruptcy",
            CaseNumber = "CASE-001",
            Status = ClientCaseStatus.Open,
            Priority = "high",
            Stage = "Preparation",
            FeeAmount = 50000m,
            StartDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            NextHearingAt = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
            LastViewedAt = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc),
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 30000m,
            PaidAmount = 20000m,
            Date = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
        });
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Client One",
                Title = "Overdue task",
                Status = "inProgress",
                DueAt = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Client One",
                Title = "Future task",
                Status = "waiting",
                DueAt = new DateTime(2100, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Client One",
                Title = "Completed task",
                Status = "done",
                DueAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetSummary(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseSummaryDto>(ok.Value);

        Assert.Equal(clientCase.Id, dto.Id);
        Assert.Equal("Client One - bankruptcy", dto.Title);
        Assert.Equal("CASE-001", dto.CaseNumber);
        Assert.Equal(ClientCaseStatus.Open.ToString(), dto.Status);
        Assert.Equal("high", dto.Priority);
        Assert.Equal("Preparation", dto.Stage);
        Assert.Equal("Client One", dto.OwnerName);
        Assert.Equal(owner.Id, dto.ResponsibleUserId);
        Assert.Equal("Owner", dto.ResponsibleName);
        Assert.Equal(2, dto.OpenTaskCount);
        Assert.Equal(1, dto.CompletedTaskCount);
        Assert.Equal(1, dto.OverdueTaskCount);
        Assert.Equal(33, dto.TaskProgressPercent);
        Assert.Equal(50000m, dto.FeeAmount);
        Assert.Equal(20000m, dto.PaidAmount);
        Assert.Equal(30000m, dto.DebtAmount);
        Assert.Equal(new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc), dto.NextHearingAt);
        Assert.NotNull(dto.LastTaskAt);
        Assert.True(dto.LastTaskAt >= new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc), dto.LastViewedAt);
        Assert.Equal(new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc), dto.LastViewedByResponsibleAt);
    }

    [Fact]
    public async Task GetFinance_ReturnsDeduplicatedInvoicesContractsAndPayments()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Owner", DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Земцов - банкротство",
            CaseNumber = "CASE-008",
            Status = ClientCaseStatus.Open,
            FeeAmount = 25000m,
        };
        var contract = new Contract
        {
            Number = "1-2026",
            Title = "Банкротство",
            Date = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            Amount = 25000m,
            Status = ContractStatus.Concluded,
        };
        var source = new PaymentSource
        {
            Name = "Касса",
            Description = "Наличные",
            ColorHex = "#10B981",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Contracts.Add(contract);
        db.PaymentSources.Add(source);
        await db.SaveChangesAsync();

        var invoiceRepresentative = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Contract = contract,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 25000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            Description = "Счет №8",
        };
        var paymentOne = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Contract = contract,
            PaymentSource = source,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 15000m,
            PaidAmount = 15000m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            Description = "Оплата по счету №8",
        };
        var paymentTwo = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Contract = contract,
            PaymentSource = source,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 10000m,
            PaidAmount = 10000m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            Description = "Оплата по счету №8",
        };
        var cancelledPayment = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Cancelled,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 100m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Payments.AddRange(invoiceRepresentative, paymentOne, paymentTwo, cancelledPayment);
        await db.SaveChangesAsync();

        db.ContractInvoices.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = invoiceRepresentative.Id,
        });
        db.ContractPayments.Add(new ContractPayment
        {
            ContractId = contract.Id,
            PaymentId = paymentOne.Id,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetFinance(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseFinanceDetailDto>(ok.Value);

        Assert.Equal(clientCase.Id, dto.CaseId);
        Assert.Equal("Земцов Виктор Александрович", dto.OwnerName);
        Assert.Equal(25000m, dto.Totals.IncomeTotal);
        Assert.Equal(25000m, dto.Totals.IncomePaid);
        Assert.Equal(0m, dto.Totals.OutstandingBalance);
        Assert.Equal(2, dto.Totals.IncomeCount);
        Assert.Equal(2, dto.Calendar.PaymentCount);
        Assert.Equal(25000m, dto.Calendar.PlanTotal);
        Assert.Equal(25000m, dto.Calendar.PaidTotal);
        Assert.Equal(0m, dto.Calendar.OutstandingTotal);
        Assert.Equal(2, dto.Calendar.PaidCount);
        Assert.Equal(0, dto.Calendar.OverdueCount);
        Assert.Null(dto.Calendar.NextPaymentDate);
        var invoice = Assert.Single(dto.Invoices);
        Assert.Equal("8", invoice.Number);
        Assert.Equal(25000m, invoice.Amount);
        Assert.Equal(PaymentStatus.Completed.ToString(), invoice.Status);
        var linkedContract = Assert.Single(dto.Contracts);
        Assert.Equal(contract.Id, linkedContract.Id);
        Assert.Equal("1-2026", linkedContract.Number);
        Assert.Equal(2, dto.Payments.Count);
        Assert.All(dto.Payments, payment => Assert.Equal("Касса", payment.PaymentSourceName));
    }

    [Fact]
    public async Task GetFinance_UsesUserTimeZoneForPaymentCalendarStatus()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "finance-timezone@example.com", "Finance Owner", DataVisibilityScope.All);
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        var selectedZone = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new
            {
                Zone = zone,
                LocalToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, zone).Date,
            })
            .FirstOrDefault(item => item.LocalToday != utcToday);
        Assert.NotNull(selectedZone);

        var shouldBeOverdue = selectedZone!.LocalToday > utcToday;
        var plannedDate = shouldBeOverdue ? utcToday : selectedZone.LocalToday;
        var client = new Client
        {
            Name = "Finance timezone client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Finance timezone case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.Zone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Payments.Add(new Payment
        {
            Client = client,
            ClientCase = clientCase,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 1000m,
            PaidAmount = 0m,
            Date = plannedDate,
            PlannedDate = plannedDate,
            Description = "Timezone planned payment",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetFinance(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseFinanceDetailDto>(ok.Value);
        Assert.Equal(1, dto.Calendar.PaymentCount);
        Assert.Equal(1000m, dto.Calendar.OutstandingTotal);
        Assert.Equal(shouldBeOverdue ? 1 : 0, dto.Calendar.OverdueCount);
        Assert.Equal(shouldBeOverdue ? 0 : 1, dto.Calendar.PlannedCount);
        Assert.Equal(shouldBeOverdue ? null : plannedDate, dto.Calendar.NextPaymentDate);
    }

    [Fact]
    public async Task GetById_AndDelete_HideCasesFromOtherResponsibles()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(hiddenClient);
        db.ClientCases.Add(hiddenCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        Assert.IsType<NotFoundResult>(await controller.GetById(hiddenCase.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Delete(hiddenCase.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsHiddenOwner()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Create(new ClientCase
        {
            ClientId = hiddenClient.Id,
            Title = "Попытка создать чужое дело",
            Status = ClientCaseStatus.Open,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.ClientCases.ToListAsync());
    }

    [Fact]
    public async Task Create_StoresRegistryDeadlineAndCourtFields()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var startDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var hearingAt = new DateTime(2026, 5, 20, 12, 30, 0, DateTimeKind.Utc);

        var result = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - банкротство",
            Description = "Судебное дело",
            Status = ClientCaseStatus.Open,
            CaseNumber = " 2026-001 ",
            CaseType = " Банкротство ",
            Stage = " Подготовка заявления ",
            ResponsibleUserId = owner.Id,
            StartDate = startDate,
            DueDate = dueDate,
            FeeAmount = 150000.50m,
            Priority = " high ",
            CourtName = " Арбитражный суд Самарской области ",
            CourtCaseNumber = " А55-12345/2026 ",
            CourtUrl = " https://kad.arbitr.ru/card ",
            NextHearingAt = hearingAt,
            Claimant = " Земцов Виктор Александрович ",
            Defendant = " Банк Точка ",
            ThirdParties = " ФНС\nСРО ",
            Representatives = " Кожеваткин А.Р'. ",
            JudgeName = " Иванова И.И. ",
            Courtroom = " зал 12 ",
            CourtSecretaryContacts = " secretary@example.test ",
            ClaimFiledAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            AppealDeadlineAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            MotionDeadlineAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ObjectionDeadlineAt = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            DocumentsSummary = " Паспорт, договор ",
            EventHistory = " Заявление принято ",
            Strategy = " Подать ходатайство ",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        var createdCase = Assert.IsType<ClientCase>(created.Value);

        Assert.Equal("2026-001", createdCase.CaseNumber);
        Assert.Equal("Банкротство", createdCase.CaseType);
        Assert.Equal("Подготовка заявления", createdCase.Stage);
        Assert.Equal(owner.Id, createdCase.ResponsibleUserId);
        Assert.Equal(startDate, createdCase.StartDate);
        Assert.Equal(dueDate, createdCase.DueDate);
        Assert.Equal(150000.50m, createdCase.FeeAmount);
        Assert.Equal("high", createdCase.Priority);
        Assert.Equal("Арбитражный суд Самарской области", createdCase.CourtName);
        Assert.Equal("А55-12345/2026", createdCase.CourtCaseNumber);
        Assert.Equal("https://kad.arbitr.ru/card", createdCase.CourtUrl);
        Assert.Equal(hearingAt, createdCase.NextHearingAt);
        Assert.Equal("Земцов Виктор Александрович", createdCase.Claimant);
        Assert.Equal("Банк Точка", createdCase.Defendant);
        Assert.Equal("ФНС\nСРО", createdCase.ThirdParties);
        Assert.Equal("Кожеваткин А.Р'.", createdCase.Representatives);
        Assert.Equal("Иванова И.И.", createdCase.JudgeName);
        Assert.Equal("зал 12", createdCase.Courtroom);
        Assert.Equal("secretary@example.test", createdCase.CourtSecretaryContacts);
        Assert.Equal(new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc), createdCase.ClaimFiledAt);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), createdCase.AppealDeadlineAt);
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), createdCase.MotionDeadlineAt);
        Assert.Equal(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc), createdCase.ObjectionDeadlineAt);
        Assert.Equal("Паспорт, договор", createdCase.DocumentsSummary);
        Assert.Equal("Заявление принято", createdCase.EventHistory);
        Assert.Equal("Подать ходатайство", createdCase.Strategy);
    }

    [Fact]
    public async Task Get_FiltersByRegistryFieldsAndDirectResponsible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.Add(hiddenClient);
        db.ClientCases.AddRange(
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Видимое дело по прямому ответственному",
                Status = ClientCaseStatus.Open,
                CaseNumber = "ZEM-001",
                CaseType = "банкротство",
                Stage = "Подготовка",
                ResponsibleUserId = owner.Id,
                FeeAmount = 120000m,
                Priority = "high",
                CourtName = "арбитражный суд",
                CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Не подходит по типу",
                Status = ClientCaseStatus.Open,
                CaseNumber = "FAM-001",
                CaseType = "Семейное право",
                ResponsibleUserId = owner.Id,
                FeeAmount = 90000m,
                Priority = "normal",
                CreatedAt = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            },
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Скрытое дело",
                Status = ClientCaseStatus.Open,
                CaseNumber = "ZEM-002",
                CaseType = "банкротство",
                ResponsibleUserId = otherResponsible.Id,
                FeeAmount = 120000m,
                Priority = "high",
                CreatedAt = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: ClientCaseStatus.Open,
            search: "арбитраж",
            responsibleUserId: owner.Id,
            caseType: "банк",
            priority: "HIGH",
            from: null,
            to: null,
            minFee: 100000m,
            maxFee: 130000m,
            sortBy: "feeAmount",
            sortDir: "desc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.Single(GetAnonymousItems(ok.Value));

        Assert.Equal("ZEM-001", item.CaseNumber);
        Assert.Equal(owner.Id, item.ResponsibleUserId);
    }

    [Fact]
    public async Task CreateAndUpdate_SyncsProcessDeadlinesToTaskCalendarWithoutDuplicates()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var dueDate = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var courtDecision = new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc);
        var calculatedAppealDeadline = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc);
        var calculatedCassationDeadline = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
        var createResult = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - процессуальные сроки",
            Status = ClientCaseStatus.Open,
            DueDate = dueDate,
            CourtDecisionAt = courtDecision,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);
        var created = Assert.IsType<ClientCase>(Assert.IsType<CreatedResult>(createResult).Value);

        Assert.Equal(calculatedAppealDeadline, created.AppealDeadlineAt);
        Assert.Equal(calculatedCassationDeadline, created.CassationDeadlineAt);

        var tasks = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Where(item => item.ClientCaseId == created.Id)
            .OrderBy(item => item.Title)
            .ToListAsync();

        Assert.Equal(3, tasks.Count);
        var dueTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:due", StringComparison.Ordinal));
        var appealTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:appeal", StringComparison.Ordinal));
        var cassationTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:cassation", StringComparison.Ordinal));
        Assert.Equal("control", dueTask.Type);
        Assert.Equal(dueDate.Date.AddHours(9), dueTask.DueAt);
        Assert.Contains("before7d", dueTask.RemindersJson, StringComparison.Ordinal);
        Assert.Contains("before3d", dueTask.RemindersJson, StringComparison.Ordinal);
        Assert.Equal(owner.Id, dueTask.AssigneeUserId);
        Assert.Contains(dueTask.Participants, item => item.Role == "assignee" && item.UserId == owner.Id);
        Assert.Equal(calculatedAppealDeadline.Date.AddHours(9), appealTask.DueAt);
        Assert.Equal(calculatedCassationDeadline.Date.AddHours(9), cassationTask.DueAt);

        var newDueDate = dueDate.AddDays(1);
        var updateResult = await controller.Update(created.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - процессуальные сроки",
            Status = ClientCaseStatus.Open,
            DueDate = newDueDate,
            CourtDecisionAt = null,
            AppealDeadlineAt = null,
            CassationDeadlineAt = null,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResult);
        var afterUpdate = await db.TaskJournalItems
            .Where(item => item.ClientCaseId == created.Id)
            .ToListAsync();

        Assert.Equal(3, afterUpdate.Count);
        var updatedDueTask = Assert.Single(afterUpdate, item => item.TagsJson.Contains("case-deadline:due", StringComparison.Ordinal));
        var cancelledAppealTask = Assert.Single(afterUpdate, item => item.TagsJson.Contains("case-deadline:appeal", StringComparison.Ordinal));
        var cancelledCassationTask = Assert.Single(afterUpdate, item => item.TagsJson.Contains("case-deadline:cassation", StringComparison.Ordinal));
        Assert.Equal(newDueDate.Date.AddHours(9), updatedDueTask.DueAt);
        Assert.NotEqual("cancelled", updatedDueTask.Status);
        Assert.Equal("cancelled", cancelledAppealTask.Status);
        Assert.Equal("cancelled", cancelledCassationTask.Status);
    }

    [Fact]
    public async Task Create_CalculatesProceduralDeadlinesWithRussianHolidays()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - срок на праздник",
            Status = ClientCaseStatus.Open,
            CourtDecisionAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        var created = Assert.IsType<ClientCase>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Equal(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), created.AppealDeadlineAt);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), created.CassationDeadlineAt);
    }

    [Fact]
    public async Task CaseCalendarEvents_ReturnsCaseDeadlinesCourtEventsAndTasks()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Календарный клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Календарное дело",
            Status = ClientCaseStatus.Open,
            Priority = "high",
            ResponsibleUserId = owner.Id,
            CourtName = "Арбитражный суд",
            CourtCaseNumber = "А55-100/2026",
            NextHearingAt = new DateTime(2026, 6, 10, 11, 0, 0, DateTimeKind.Unspecified),
            AppealDeadlineAt = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
            MotionDeadlineAt = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientCaseId = clientCase.Id,
            ObjectName = clientCase.Title,
            Title = "Подготовить позицию",
            DueAt = new DateTime(2026, 6, 12, 9, 30, 0, DateTimeKind.Utc),
            PlannedDurationMinutes = 45,
            Type = "task",
            Priority = "urgent",
            Status = "inProgress",
            AssigneeUserId = owner.Id,
            AssigneeName = owner.FullName,
            AuthorUserId = owner.Id,
            AuthorName = owner.FullName,
        });
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetCaseCalendarEvents(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: true,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(ok.Value);
        Assert.Contains(events, item => item.Kind == "court" && item.SourceType == "hearing" && item.ClientCaseId == clientCase.Id);
        Assert.Contains(events, item => item.Kind == "procedural-deadline" && item.DeadlineType == "appeal" && item.AllDay);
        Assert.Contains(events, item => item.Kind == "task" && item.SourceId.HasValue && item.Title == "Подготовить позицию" && item.EndAt == item.StartAt.AddMinutes(45));
        Assert.DoesNotContain(events, item => item.DeadlineType == "motion");

        var summaryResult = await controller.GetCaseCalendarSummary(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: true,
            ct: CancellationToken.None);

        var summaryOk = Assert.IsType<OkObjectResult>(summaryResult.Result);
        var summary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(summaryOk.Value);
        Assert.Equal(3, summary.TotalEventCount);
        Assert.Equal(1, summary.CourtEventCount);
        Assert.Equal(1, summary.ProceduralDeadlineCount);
        Assert.Equal(1, summary.TaskCount);
        Assert.NotNull(summary.NearestEvent);

        var exportResult = await controller.ExportCaseCalendarIcs(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: true,
            reminderMinutes: "120,30",
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(exportResult);
        Assert.Equal("text/calendar; charset=utf-8", file.ContentType);
        Assert.EndsWith(".ics", file.FileDownloadName, StringComparison.Ordinal);
        var ics = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("BEGIN:VCALENDAR", ics, StringComparison.Ordinal);
        Assert.Contains("X-WR-CALNAME:Календарь дел", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Судебное заседание", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20260610T110000Z", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("DTSTART:20260610T070000Z", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Срок апелляции", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Подготовить позицию", ics, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VALARM", ics, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT2H", ics, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT30M", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("TRIGGER:-PT1H", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("TRIGGER:-PT15M", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:Напоминание: Подготовить позицию", ics, StringComparison.Ordinal);
        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item => item.ObjectType == CalendarExportAudit.ObjectType);
        Assert.Equal(CalendarExportAudit.ExportedAction, auditEvent.Action);
        Assert.Equal(owner.Id, auditEvent.ActorUserId);
        Assert.Contains("ICS-календарь дел", auditEvent.Description);
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        Assert.Equal("cases", metadata.RootElement.GetProperty("calendarKind").GetString());
        Assert.Equal(3, metadata.RootElement.GetProperty("exportedEventCount").GetInt32());
        Assert.Equal(120, metadata.RootElement.GetProperty("reminderMinutes")[0].GetInt32());
        Assert.Equal(30, metadata.RootElement.GetProperty("reminderMinutes")[1].GetInt32());
        Assert.True(metadata.RootElement.GetProperty("filters").GetProperty("includeTasks").GetBoolean());
    }

    [Fact]
    public async Task CaseCalendarEvents_UsesUserTimeZoneForTimedEventBoundaries()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-calendar-boundary@example.com", "Calendar Owner");
        var client = new Client
        {
            Name = "Boundary client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var eventAtUtc = new DateTime(2026, 5, 19, 20, 30, 0, DateTimeKind.Utc);
        var storedEventAt = DateTime.SpecifyKind(eventAtUtc, DateTimeKind.Unspecified);
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Boundary case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            NextHearingAt = storedEventAt,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            EventType = "hearing",
            Title = "Boundary court event",
            ScheduledAt = storedEventAt,
            AllDay = false,
            MovementStatus = "scheduled",
            CreatedAt = eventAtUtc,
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
            UpdatedAt = eventAtUtc,
            UpdatedByUserId = owner.Id,
            UpdatedByUserName = owner.FullName,
        });
        db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            DeadlineType = "motion",
            Title = "Boundary timed deadline",
            DueAt = storedEventAt,
            AllDay = false,
            Status = "active",
            WarningDaysBefore = 7,
            CreatedAt = eventAtUtc,
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
            UpdatedAt = eventAtUtc,
            UpdatedByUserId = owner.Id,
            UpdatedByUserName = owner.FullName,
        });
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientCaseId = clientCase.Id,
            ObjectName = clientCase.Title,
            Title = "Boundary task",
            DueAt = storedEventAt,
            PlannedDurationMinutes = 60,
            Type = "task",
            Priority = "normal",
            Status = "new",
            AssigneeUserId = owner.Id,
            AssigneeName = owner.FullName,
            AuthorUserId = owner.Id,
            AuthorName = owner.FullName,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var previousDayResult = await controller.GetCaseCalendarEvents(
            new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Unspecified),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: true,
            ct: CancellationToken.None);
        var previousDayOk = Assert.IsType<OkObjectResult>(previousDayResult.Result);
        var previousDayEvents = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(previousDayOk.Value);
        Assert.DoesNotContain(previousDayEvents, item => item.ClientCaseId == clientCase.Id);

        var localDayResult = await controller.GetCaseCalendarEvents(
            new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Unspecified),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: true,
            ct: CancellationToken.None);

        var localDayOk = Assert.IsType<OkObjectResult>(localDayResult.Result);
        var localDayEvents = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(localDayOk.Value);
        Assert.Contains(localDayEvents, item => item.Kind == "court" && item.SourceType == "hearing" && item.StartAt == eventAtUtc);
        Assert.Contains(localDayEvents, item => item.Kind == "court" && item.Title == "Boundary court event" && item.StartAt == eventAtUtc);
        Assert.Contains(localDayEvents, item => item.Kind == "procedural-deadline" && item.Title == "Boundary timed deadline" && item.StartAt == eventAtUtc);
        Assert.Contains(localDayEvents, item => item.Kind == "task" && item.Title == "Boundary task" && item.StartAt == eventAtUtc);
        Assert.All(localDayEvents.Where(item => !item.AllDay), item =>
        {
            Assert.Equal(DateTimeKind.Utc, item.StartAt.Kind);
            Assert.EndsWith("Z\"", JsonSerializer.Serialize(item.StartAt), StringComparison.Ordinal);
        });

        var exportResult = await controller.ExportCaseCalendarIcs(
            new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Unspecified),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: true,
            reminderMinutes: "30",
            ct: CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(exportResult);
        var ics = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("DTSTART:20260519T203000Z", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("DTSTART:20260520T003000Z", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaseCalendarEvents_PreserveFourPmCourtHearingForUserTimeZone()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-calendar-samara-16@example.com", "Calendar Owner");
        var client = new Client
        {
            Name = "Samara hearing client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hearingAtUtc = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);
        var storedHearingAt = DateTime.SpecifyKind(hearingAtUtc, DateTimeKind.Unspecified);
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Чудайкин - С/З",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            NextHearingAt = storedHearingAt,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetCaseCalendarEvents(
            new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Unspecified),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: false,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(ok.Value);
        var hearing = Assert.Single(events, item => item.Kind == "court" && item.SourceType == "hearing");
        Assert.Equal(hearingAtUtc, hearing.StartAt);
        Assert.Equal(DateTimeKind.Utc, hearing.StartAt.Kind);
        Assert.Equal("\"2026-06-05T12:00:00Z\"", JsonSerializer.Serialize(hearing.StartAt));

        var exportResult = await controller.ExportCaseCalendarIcs(
            new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Unspecified),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: false,
            reminderMinutes: "30",
            ct: CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(exportResult);
        var ics = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("DTSTART:20260605T120000Z", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("DTSTART:20260605T160000Z", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaseCalendarSummary_UsesUserTimeZoneForAllDayDeadlineStatus()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-calendar-status@example.com", "Calendar Owner");
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        var selectedZone = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new
            {
                Zone = zone,
                LocalToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, zone).Date,
            })
            .FirstOrDefault(item => item.LocalToday != utcToday);
        Assert.NotNull(selectedZone);

        var shouldBeOverdue = selectedZone!.LocalToday > utcToday;
        var deadlineDate = shouldBeOverdue ? utcToday : selectedZone.LocalToday;
        var client = new Client
        {
            Name = "Status boundary client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Status boundary case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            DueDate = deadlineDate,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.Zone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var eventsResult = await controller.GetCaseCalendarEvents(
            deadlineDate,
            deadlineDate,
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: false,
            ct: CancellationToken.None);

        var eventsOk = Assert.IsType<OkObjectResult>(eventsResult.Result);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(eventsOk.Value);
        var deadlineEvent = Assert.Single(events, item => item.Kind == "case-deadline" && item.ClientCaseId == clientCase.Id);
        Assert.Equal(shouldBeOverdue, deadlineEvent.IsOverdue);
        Assert.Equal(!shouldBeOverdue, deadlineEvent.IsWarning);

        var summaryResult = await controller.GetCaseCalendarSummary(
            deadlineDate,
            deadlineDate,
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: false,
            ct: CancellationToken.None);

        var summaryOk = Assert.IsType<OkObjectResult>(summaryResult.Result);
        var summary = Assert.IsType<CasesV2Controller.CaseCalendarSummaryDto>(summaryOk.Value);
        Assert.Equal(shouldBeOverdue ? 1 : 0, summary.OverdueCount);
        Assert.Equal(shouldBeOverdue ? 0 : 1, summary.WarningCount);
    }

    [Fact]
    public async Task ExportCaseCalendarIcs_ReturnsForbidWhenExportDenied()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-calendar-export-denied@example.com", "Владелец");
        var permission = await db.RolePermissions.FirstAsync(item => item.RoleId == owner.RoleId && item.Section == RolePermissionCatalog.Cases);
        permission.CanExport = false;
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.ExportCaseCalendarIcs(
            from: null,
            to: null,
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: true,
            reminderMinutes: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task CaseDeadlineAndCourtEventLists_UseUserTimeZoneForStatusFlags()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-status-flags@example.com", "Calendar Owner");
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        var selectedZone = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new
            {
                Zone = zone,
                LocalToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, zone).Date,
            })
            .FirstOrDefault(item => item.LocalToday != utcToday);
        Assert.NotNull(selectedZone);

        var shouldBeOverdue = selectedZone!.LocalToday > utcToday;
        var deadlineDate = shouldBeOverdue ? utcToday : selectedZone.LocalToday;
        var client = new Client
        {
            Name = "Status flags client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Status flags case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.Zone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            DeadlineType = "motion",
            Title = "Status deadline",
            DueAt = deadlineDate,
            AllDay = true,
            Status = "active",
            WarningDaysBefore = 0,
            CreatedAt = nowUtc,
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
            UpdatedAt = nowUtc,
            UpdatedByUserId = owner.Id,
            UpdatedByUserName = owner.FullName,
        });
        db.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            EventType = "hearing",
            Title = "Status all-day court event",
            ScheduledAt = deadlineDate,
            AllDay = true,
            MovementStatus = "scheduled",
            CreatedAt = nowUtc,
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
            UpdatedAt = nowUtc,
            UpdatedByUserId = owner.Id,
            UpdatedByUserName = owner.FullName,
        });
        db.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            EventType = "hearing",
            Title = "Status timed court event",
            ScheduledAt = nowUtc.AddMinutes(-1),
            AllDay = false,
            MovementStatus = "scheduled",
            CreatedAt = nowUtc,
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
            UpdatedAt = nowUtc,
            UpdatedByUserId = owner.Id,
            UpdatedByUserName = owner.FullName,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var deadlinesResult = await controller.GetProceduralDeadlines(clientCase.Id, CancellationToken.None);
        var deadlinesOk = Assert.IsType<OkObjectResult>(deadlinesResult.Result);
        var deadlines = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseProceduralDeadlineDto>>(deadlinesOk.Value);
        var deadline = Assert.Single(deadlines);
        Assert.Equal(shouldBeOverdue, deadline.IsOverdue);
        Assert.Equal(!shouldBeOverdue, deadline.IsWarning);

        var courtEventsResult = await controller.GetCourtEvents(clientCase.Id, CancellationToken.None);
        var courtEventsOk = Assert.IsType<OkObjectResult>(courtEventsResult.Result);
        var courtEvents = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtEventDto>>(courtEventsOk.Value);
        Assert.Contains(courtEvents, item => item.Title == "Status all-day court event" && item.IsOverdue == shouldBeOverdue);
        Assert.Contains(courtEvents, item => item.Title == "Status timed court event" && item.IsOverdue);
    }

    [Fact]
    public async Task ProceduralDeadlines_CreateShowInCalendarAndClose()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент со сроками",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с отдельным сроком",
            Status = ClientCaseStatus.Open,
            Priority = "high",
            ResponsibleUserId = owner.Id,
            AppealDeadlineAt = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createResult = await controller.CreateProceduralDeadline(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest
            {
                DeadlineType = "appeal",
                Title = "Апелляционная жалоба",
                DueAt = new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc),
                WarningDaysBefore = 5,
            },
            CancellationToken.None);

        var created = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);
        Assert.Equal("appeal", created.DeadlineType);
        Assert.Equal("active", created.Status);
        Assert.NotNull(created.TaskJournalItemId);

        var deadlineNotifications = await db.UserNotifications
            .Where(item => item.Type == UserNotificationTypes.CaseProceduralDeadline)
            .ToListAsync();
        var createdNotification = Assert.Single(deadlineNotifications, item =>
            item.MetadataJson.Contains($"\"proceduralDeadlineId\":{created.Id}", StringComparison.Ordinal)
            && item.MetadataJson.Contains("\"action\":\"created\"", StringComparison.Ordinal));
        Assert.Equal(owner.Id, createdNotification.RecipientUserId);
        Assert.Equal("case", createdNotification.ObjectType);
        Assert.Equal(clientCase.Id, createdNotification.ObjectId);
        Assert.Contains("Процессуальный срок", createdNotification.Title, StringComparison.OrdinalIgnoreCase);

        var calendarResult = await controller.GetCaseCalendarEvents(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            clientCaseId: clientCase.Id,
            responsibleUserId: null,
            includeTasks: false,
            ct: CancellationToken.None);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(
            Assert.IsType<OkObjectResult>(calendarResult.Result).Value);
        Assert.Contains(events, item => item.Kind == "procedural-deadline" && item.SourceId == created.Id && item.Title == "Апелляционная жалоба");
        Assert.DoesNotContain(events, item => item.Kind == "procedural-deadline" && item.SourceId is null && item.DeadlineType == "appeal");

        var closeResult = await controller.CloseProceduralDeadline(
            clientCase.Id,
            created.Id,
            new CasesV2Controller.CloseClientCaseProceduralDeadlineRequest { Reason = "Подано в суд" },
            CancellationToken.None);
        var closed = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<OkObjectResult>(closeResult.Result).Value);
        Assert.Equal("closed", closed.Status);
        Assert.Equal("Подано в суд", closed.CloseReason);

        var tasks = await db.TaskJournalItems
            .Where(item => item.ClientCaseId == clientCase.Id && item.TagsJson.Contains($"case-deadline:entity-{created.Id}"))
            .ToListAsync();
        Assert.Contains(tasks, item => item.Status == "cancelled");
        Assert.Contains(await db.UserNotifications
            .Where(item => item.Type == UserNotificationTypes.CaseProceduralDeadline)
            .ToListAsync(), item => item.MetadataJson.Contains("\"action\":\"closed\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CourtEvents_CreateShowInCalendarAndCanLinkDeadline()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент с заседанием",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с судебным событием",
            Status = ClientCaseStatus.Open,
            Priority = "high",
            ResponsibleUserId = owner.Id,
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-100/2026",
            Courtroom = "12",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var document = new ClientCaseDocument
        {
            ClientCaseId = clientCase.Id,
            DocumentKey = "hearing-doc",
            Version = 1,
            DocumentType = "Судебный документ",
            Status = "draft",
            OriginalFileName = "hearing.pdf",
            StoredFileName = "hearing.pdf",
            StoragePath = "cases/hearing.pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            UploadedByUserId = owner.Id,
            UploadedByName = owner.FullName,
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var deadlineResult = await controller.CreateProceduralDeadline(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest
            {
                DeadlineType = "motion",
                Title = "Подать ходатайство к заседанию",
                DueAt = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
            },
            CancellationToken.None);
        var deadline = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<CreatedResult>(deadlineResult.Result).Value);

        var createResult = await controller.CreateCourtEvent(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseCourtEventRequest
            {
                EventType = "hearing",
                Title = "Предварительное заседание",
                ScheduledAt = new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Utc),
                CourtInstance = "first",
                MovementStatus = "scheduled",
                ClientCaseDocumentId = document.Id,
                ProceduralDeadlineId = deadline.Id,
            },
            CancellationToken.None);

        var created = Assert.IsType<CasesV2Controller.ClientCaseCourtEventDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);
        Assert.Equal("hearing", created.EventType);
        Assert.Equal("first", created.CourtInstance);
        Assert.Equal("scheduled", created.MovementStatus);
        Assert.Equal(deadline.Id, created.ProceduralDeadlineId);
        Assert.Equal(document.Id, created.ClientCaseDocumentId);
        Assert.Equal("Арбитражный суд Самарской области", created.CourtName);
        Assert.Equal("А55-100/2026", created.CourtCaseNumber);

        var linkedDeadline = await db.ClientCaseProceduralDeadlines.SingleAsync(item => item.Id == deadline.Id);
        Assert.Equal("court-event", linkedDeadline.SourceType);
        Assert.Equal(created.Id, linkedDeadline.SourceId);
        Assert.Equal(document.Id, linkedDeadline.ClientCaseDocumentId);

        var listResult = await controller.GetCourtEvents(clientCase.Id, CancellationToken.None);
        var list = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtEventDto>>(
            Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Single(list);

        var calendarResult = await controller.GetCaseCalendarEvents(
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
            clientCaseId: clientCase.Id,
            responsibleUserId: null,
            includeTasks: false,
            ct: CancellationToken.None);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(
            Assert.IsType<OkObjectResult>(calendarResult.Result).Value);
        Assert.Contains(events, item =>
            item.Kind == "court"
            && item.SourceId == created.Id
            && item.SourceType == "hearing"
            && item.Title == "Предварительное заседание");

        var auditEvent = await db.ObjectAuditEvents
            .SingleOrDefaultAsync(item => item.ObjectType == "client-case-court-event" && item.ObjectId == created.Id);
        Assert.NotNull(auditEvent);

        var historyResult = await controller.GetCourtEventHistory(clientCase.Id, created.Id, CancellationToken.None);
        var history = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtEventHistoryDto>>(
            Assert.IsType<OkObjectResult>(historyResult.Result).Value);
        Assert.Contains(history, item => item.Action == "created" && item.Summary.Contains("Предварительное заседание"));

        var notification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CaseCourtEvent);
        Assert.Equal(owner.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("Судебное событие", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Предварительное заседание", notification.BodyPreview, StringComparison.Ordinal);
        Assert.Contains($"\"courtEventId\":{created.Id}", notification.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CourtDirectory_CreateUpdateListAndUseInCourtEvent()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-directory-owner@example.com", "Владелец");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Dictionaries,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All,
        });
        var client = new Client
        {
            Name = "Клиент со справочником судов",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело со справочным судом",
            Status = ClientCaseStatus.Open,
            Priority = "normal",
            ResponsibleUserId = owner.Id,
            CourtCaseNumber = "А40-200/2026",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var controller = CreateController(db, owner);
        var createCourtResult = await controller.CreateCourtDirectoryItem(
            new CasesV2Controller.UpsertClientCaseCourtRequest
            {
                Name = "Арбитражный суд города Москвы",
                Instance = "first",
                Code = "msk-arbitrage",
                Url = "https://msk.arbitr.ru",
                Address = "Москва",
                Contacts = "info@example.test",
                SortOrder = 10,
            },
            CancellationToken.None);

        var court = Assert.IsType<CasesV2Controller.ClientCaseCourtDto>(
            Assert.IsType<CreatedResult>(createCourtResult.Result).Value);
        Assert.Equal("Арбитражный суд города Москвы", court.Name);
        Assert.True(court.IsActive);

        var updateCourtResult = await controller.UpdateCourtDirectoryItem(
            court.Id,
            new CasesV2Controller.UpsertClientCaseCourtRequest
            {
                Name = "Арбитражный суд города Москвы",
                Instance = "appeal",
                Code = "msk-arbitrage",
                Url = "https://msk.arbitr.ru",
                Address = "Москва, обновлено",
                Contacts = "updated@example.test",
                SortOrder = 5,
                IsActive = true,
            },
            CancellationToken.None);
        var updatedCourt = Assert.IsType<CasesV2Controller.ClientCaseCourtDto>(
            Assert.IsType<OkObjectResult>(updateCourtResult.Result).Value);
        Assert.Equal("appeal", updatedCourt.Instance);
        Assert.Equal("Москва, обновлено", updatedCourt.Address);

        var listResult = await controller.GetCourtDirectory(includeInactive: false, CancellationToken.None);
        var courts = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtDto>>(
            Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Single(courts);

        var createEventResult = await controller.CreateCourtEvent(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseCourtEventRequest
            {
                EventType = "hearing",
                Title = "Заседание из справочника",
                ScheduledAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
                CourtId = court.Id,
                MovementStatus = "scheduled",
            },
            CancellationToken.None);
        var createdEvent = Assert.IsType<CasesV2Controller.ClientCaseCourtEventDto>(
            Assert.IsType<CreatedResult>(createEventResult.Result).Value);
        Assert.Equal(court.Id, createdEvent.CourtId);
        Assert.Equal("appeal", createdEvent.CourtInstance);
        Assert.Equal("Арбитражный суд города Москвы", createdEvent.CourtName);
        Assert.Equal("А40-200/2026", createdEvent.CourtCaseNumber);
    }

    [Fact]
    public async Task ProceduralDeadlines_CreateWithCourtEventSourceBackfillsCourtEventLink()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "deadline-court-link@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент с двусторонней связью",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело со сроком и событием",
            Status = ClientCaseStatus.Open,
            Priority = "normal",
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var courtEvent = new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            EventType = "hearing",
            Title = "Заседание для срока",
            ScheduledAt = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
            CourtInstance = "first",
            MovementStatus = "scheduled",
            SourceType = "manual",
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
        };
        db.ClientCaseCourtEvents.Add(courtEvent);
        await db.SaveChangesAsync();

        var controller = CreateController(db, owner);
        var createResult = await controller.CreateProceduralDeadline(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest
            {
                DeadlineType = "motion",
                Title = "Подать позицию к заседанию",
                DueAt = new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
                SourceType = "court-event",
                SourceId = courtEvent.Id,
            },
            CancellationToken.None);

        var created = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);
        Assert.Equal("court-event", created.SourceType);
        Assert.Equal(courtEvent.Id, created.SourceId);

        var linkedCourtEvent = await db.ClientCaseCourtEvents.SingleAsync(item => item.Id == courtEvent.Id);
        Assert.Equal(created.Id, linkedCourtEvent.ProceduralDeadlineId);
    }

    [Fact]
    public async Task CourtParties_CreateListAndArchive()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-party-owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент со сторонами",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело со сторонами процесса",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createResult = await controller.CreateCourtParty(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseCourtPartyRequest
            {
                DisplayName = "ООО Ответчик",
                Side = "respondent",
                ProcessRole = "Ответчик",
                RepresentativeName = "Петров Петр",
                Notes = "Основная сторона процесса",
            },
            CancellationToken.None);

        var created = Assert.IsType<CasesV2Controller.ClientCaseCourtPartyDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);
        Assert.Equal("respondent", created.Side);
        Assert.Equal("Ответчик", created.SideLabel);
        Assert.Equal("ООО Ответчик", created.DisplayName);

        var listResult = await controller.GetCourtParties(clientCase.Id, includeArchived: false, CancellationToken.None);
        var list = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtPartyDto>>(
            Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Single(list);

        var archiveResult = await controller.ArchiveCourtParty(clientCase.Id, created.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(archiveResult);

        var activeListResult = await controller.GetCourtParties(clientCase.Id, includeArchived: false, CancellationToken.None);
        var activeList = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtPartyDto>>(
            Assert.IsType<OkObjectResult>(activeListResult.Result).Value);
        Assert.Empty(activeList);

        var archivedListResult = await controller.GetCourtParties(clientCase.Id, includeArchived: true, CancellationToken.None);
        var archivedList = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtPartyDto>>(
            Assert.IsType<OkObjectResult>(archivedListResult.Result).Value);
        Assert.Single(archivedList);
        Assert.True(archivedList[0].IsArchived);

        var auditEvent = await db.ObjectAuditEvents
            .SingleOrDefaultAsync(item => item.ObjectType == "client-case-court-party" && item.ObjectId == created.Id && item.Action == "Created");
        Assert.NotNull(auditEvent);
    }

    [Fact]
    public async Task ProceduralDeadlineCalculationPreview_ReturnsCalculatedDate()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, new User { Id = 1, Email = "owner@example.com", FullName = "Владелец" });

        var result = await controller.CalculateProceduralDeadlinePreview(new ProceduralDeadlineCalculationRequest
        {
            StartAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            Rule = "workingDays",
            DaysToAdd = 2,
            RollForwardToWorkingDay = true,
        }, CancellationToken.None);

        var dto = Assert.IsType<CasesV2Controller.ProceduralDeadlineCalculationDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("workingDays", dto.Rule);
        Assert.Equal(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), dto.DeadlineAt);
        Assert.Equal(new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc), dto.StartAt);
        Assert.Equal(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), dto.RawDeadlineAt);
        Assert.Equal(3, dto.SkippedNonWorkingDays);
        Assert.Equal(0, dto.RollForwardDays);
        Assert.Contains("пропущено нерабочих дней: 3", dto.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProceduralDeadlineCalendarSettings_SaveAndAffectCalculation()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "calendar-settings@example.com", "Владелец");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Dictionaries,
            CanView = true,
            CanEdit = true,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var updateResult = await controller.UpdateProceduralDeadlineCalendarSettings(
            new CasesV2Controller.UpsertProceduralDeadlineCalendarSettingsRequest
            {
                WorkingDates =
                [
                    new CasesV2Controller.ProceduralDeadlineCalendarDateRequest
                    {
                        Date = "2026-05-02",
                        Note = "Рабочая суббота",
                    },
                ],
                NonWorkingDates =
                [
                    new CasesV2Controller.ProceduralDeadlineCalendarDateRequest
                    {
                        Date = "2026-05-04",
                        Note = "Перенос выходного",
                    },
                ],
            },
            CancellationToken.None);

        var saved = Assert.IsType<CasesV2Controller.ProceduralDeadlineCalendarSettingsDto>(
            Assert.IsType<OkObjectResult>(updateResult.Result).Value);
        Assert.Contains(saved.WorkingDates, item => item.Date == "2026-05-02" && item.Note == "Рабочая суббота");
        Assert.Contains(saved.NonWorkingDates, item => item.Date == "2026-05-04" && item.Note == "Перенос выходного");

        var result = await controller.CalculateProceduralDeadlinePreview(new ProceduralDeadlineCalculationRequest
        {
            StartAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            Rule = "workingDays",
            DaysToAdd = 2,
            RollForwardToWorkingDay = true,
        }, CancellationToken.None);

        var dto = Assert.IsType<CasesV2Controller.ProceduralDeadlineCalculationDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), dto.DeadlineAt);
        Assert.Equal(3, dto.SkippedNonWorkingDays);
        Assert.NotNull(dto.CalendarOverrides);
        Assert.Contains(dto.CalendarOverrides.WorkingDates, item => item.Date == "2026-05-02");
    }

    [Fact]
    public async Task Update_ToClosed_ClosesLinkedActiveTasksAndDoesNotRecreateDeadlineTasks()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var dueDate = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var createResult = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - закрываемое дело",
            Status = ClientCaseStatus.Open,
            DueDate = dueDate,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);
        var created = Assert.IsType<ClientCase>(Assert.IsType<CreatedResult>(createResult).Value);

        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientId = client.Id,
                ClientCaseId = created.Id,
                ObjectName = "Земцов Виктор Александрович - закрываемое дело",
                Title = "Ручная задача по делу",
                AssigneeUserId = owner.Id,
                AssigneeName = owner.FullName,
                AuthorUserId = owner.Id,
                AuthorName = owner.FullName,
                DueAt = new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc),
                Status = "inProgress",
            },
            new TaskJournalItem
            {
                ClientId = client.Id,
                ClientCaseId = created.Id,
                ObjectName = "Земцов Виктор Александрович - закрываемое дело",
                Title = "Уже отмененная задача",
                AssigneeUserId = owner.Id,
                AssigneeName = owner.FullName,
                AuthorUserId = owner.Id,
                AuthorName = owner.FullName,
                DueAt = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc),
                Status = "cancelled",
            });
        await db.SaveChangesAsync();

        var result = await controller.Update(created.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - закрываемое дело",
            Status = ClientCaseStatus.Closed,
            DueDate = dueDate,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var tasks = await db.TaskJournalItems
            .Include(item => item.Comments)
            .Where(item => item.ClientCaseId == created.Id)
            .OrderBy(item => item.Title)
            .ToListAsync();

        var deadlineTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:due", StringComparison.Ordinal));
        var manualTask = Assert.Single(tasks, item => item.Title == "Ручная задача по делу");
        var cancelledTask = Assert.Single(tasks, item => item.Title == "Уже отмененная задача");

        Assert.Equal("done", deadlineTask.Status);
        Assert.Equal("done", manualTask.Status);
        Assert.Equal("cancelled", cancelledTask.Status);
        Assert.Contains(manualTask.Comments, item => item.Text.Contains("Связанное дело закрыто", StringComparison.Ordinal));
        Assert.DoesNotContain(tasks, item => item.Status is not "done" and not "cancelled");
    }

    [Fact]
    public async Task Update_ToLost_ClosesLinkedActiveTasks()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - спор",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            ObjectName = "Земцов Виктор Александрович - спор",
            Title = "Активная задача по проигранному делу",
            AssigneeUserId = owner.Id,
            AssigneeName = owner.FullName,
            AuthorUserId = owner.Id,
            AuthorName = owner.FullName,
            DueAt = new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc),
            Status = "new",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - спор",
            Status = ClientCaseStatus.Lost,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("done", await db.TaskJournalItems
            .Where(item => item.ClientCaseId == clientCase.Id)
            .Select(item => item.Status)
            .SingleAsync());
    }

    [Fact]
    public async Task BulkUpdate_ChangesOnlyVisibleCases()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var nextResponsible = await CreateUserAsync(db, "next@example.com", "Новый ответственный");
        var visibleClient = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var visibleByClient = new ClientCase
        {
            Client = visibleClient,
            Title = "Видимое по клиенту",
            Status = ClientCaseStatus.Open,
            Stage = "Подготовка",
            Priority = "normal",
        };
        var visibleByCaseResponsible = new ClientCase
        {
            Client = hiddenClient,
            Title = "Видимое по ответственному дела",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            Stage = "Подготовка",
            Priority = "high",
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.ClientCases.AddRange(visibleByClient, visibleByCaseResponsible, hiddenCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.BulkUpdate(new CasesV2Controller.BulkUpdateCasesRequest
        {
            Ids = [visibleByClient.Id, visibleByCaseResponsible.Id, hiddenCase.Id],
            Status = ClientCaseStatus.OnHold,
            ResponsibleUserId = nextResponsible.Id,
            Stage = "Судебное производство",
            Priority = "urgent",
            IsHiddenFromExecutor = true,
            InactivityRemindersEnabled = false,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(3, GetAnonymousInt(ok.Value, "requested"));

        await db.Entry(visibleByClient).ReloadAsync();
        await db.Entry(visibleByCaseResponsible).ReloadAsync();
        await db.Entry(hiddenCase).ReloadAsync();

        Assert.Equal(ClientCaseStatus.OnHold, visibleByClient.Status);
        Assert.Equal(nextResponsible.Id, visibleByClient.ResponsibleUserId);
        Assert.Equal("Судебное производство", visibleByClient.Stage);
        Assert.Equal("urgent", visibleByClient.Priority);
        Assert.True(visibleByClient.IsHiddenFromExecutor);
        Assert.False(visibleByClient.InactivityRemindersEnabled);
        Assert.Equal(owner.Id, visibleByClient.HiddenFromExecutorByUserId);
        Assert.Equal("Владелец", visibleByClient.HiddenFromExecutorByUserName);
        Assert.Equal(ClientCaseStatus.OnHold, visibleByCaseResponsible.Status);
        Assert.Equal(nextResponsible.Id, visibleByCaseResponsible.ResponsibleUserId);
        Assert.Equal("Судебное производство", visibleByCaseResponsible.Stage);
        Assert.Equal("urgent", visibleByCaseResponsible.Priority);
        Assert.True(visibleByCaseResponsible.IsHiddenFromExecutor);
        Assert.False(visibleByCaseResponsible.InactivityRemindersEnabled);
        Assert.Equal(ClientCaseStatus.Open, hiddenCase.Status);
        Assert.Equal(otherResponsible.Id, hiddenCase.ResponsibleUserId);
        Assert.False(hiddenCase.IsHiddenFromExecutor);
        Assert.True(hiddenCase.InactivityRemindersEnabled);
        Assert.Contains("Массовое изменение", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("статус «В работе» → «Приостановлено»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("ответственный «Ответственный владельца» → «Новый ответственный»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("стадия «Подготовка» → «Судебное производство»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("приоритет «Обычный» → «Критический»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("доступ: скрыто от исполнителя", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("автоконтроль: выключен", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("ответственный «Владелец» → «Новый ответственный»", visibleByCaseResponsible.EventHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("Массовое изменение", hiddenCase.EventHistory, StringComparison.Ordinal);

        var changedAuditEvents = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == "client-case" && item.Action == "Updated")
            .ToListAsync();
        var visibleChangedAuditEvents = changedAuditEvents
            .Where(item => item.ObjectId == visibleByClient.Id || item.ObjectId == visibleByCaseResponsible.Id)
            .ToList();

        Assert.Equal(2, visibleChangedAuditEvents.Count);
        Assert.DoesNotContain(changedAuditEvents, item => item.ObjectId == hiddenCase.Id);
        foreach (var auditEvent in visibleChangedAuditEvents)
        {
            Assert.NotNull(auditEvent.MetadataJson);
            using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
            var reason = metadata.RootElement.GetProperty("reason").GetString();
            Assert.StartsWith("Массовое изменение дел (2):", reason);
            Assert.Contains("статус: Приостановлено", reason, StringComparison.Ordinal);
            Assert.Contains("ответственный: Новый ответственный", reason, StringComparison.Ordinal);
            Assert.Contains("доступ: скрыто от исполнителя", reason, StringComparison.Ordinal);
            Assert.Contains("автоконтроль: выключен", reason, StringComparison.Ordinal);
            Assert.Contains("Status", auditEvent.MetadataJson, StringComparison.Ordinal);
            Assert.Contains("ResponsibleUserId", auditEvent.MetadataJson, StringComparison.Ordinal);
            Assert.Contains("IsHiddenFromExecutor", auditEvent.MetadataJson, StringComparison.Ordinal);
            Assert.Contains("InactivityRemindersEnabled", auditEvent.MetadataJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Documents_UploadsVersionsListsAndDeletesVisibleCaseFiles()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var firstUpload = await controller.UploadDocument(
            clientCase.Id,
            CreateFormFile("first", "claim.txt"),
            " Иск ",
            " Подготовлен ",
            documentKey: null,
            ct: CancellationToken.None);
        var firstCreated = Assert.IsType<CreatedResult>(firstUpload.Result);
        var first = Assert.IsType<ClientCaseDocumentDto>(firstCreated.Value);

        Assert.Equal("Иск", first.DocumentType);
        Assert.Equal("Подготовлен", first.Status);
        Assert.Equal("claim.txt", first.OriginalFileName);
        Assert.Equal(1, first.Version);
        Assert.True(first.IsLatestVersion);

        var secondUpload = await controller.UploadDocument(
            clientCase.Id,
            CreateFormFile("second", "claim-v2.txt"),
            "Иск",
            "Отправлен",
            first.DocumentKey,
            CancellationToken.None);
        var secondCreated = Assert.IsType<CreatedResult>(secondUpload.Result);
        var second = Assert.IsType<ClientCaseDocumentDto>(secondCreated.Value);

        Assert.Equal(first.DocumentKey, second.DocumentKey);
        Assert.Equal(2, second.Version);
        Assert.True(second.IsLatestVersion);

        var listResult = await controller.GetDocuments(clientCase.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(listResult.Result);
        var documents = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(ok.Value);

        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, item => item.Id == first.Id && !item.IsLatestVersion);
        Assert.Contains(documents, item => item.Id == second.Id && item.IsLatestVersion);

        var deleteResult = await controller.DeleteDocument(clientCase.Id, first.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);
        Assert.DoesNotContain(await db.ClientCaseDocuments.ToListAsync(), item => item.Id == first.Id);
        Assert.Contains(await db.ClientCaseDocuments.ToListAsync(), item => item.Id == second.Id);
        var documentAuditEvents = await db.ObjectAuditEvents
            .Where(item => item.ObjectType == "client-case-document")
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(3, documentAuditEvents.Count);
        Assert.Contains(documentAuditEvents, item => item.ObjectId == first.Id && item.Action == "Created");
        Assert.Contains(documentAuditEvents, item => item.ObjectId == second.Id && item.Action == "VersionCreated");
        Assert.Contains(documentAuditEvents, item => item.ObjectId == first.Id && item.Action == "Deleted");
        var documentNotifications = await db.UserNotifications
            .Where(item => item.Type == UserNotificationTypes.CaseDocument)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(3, documentNotifications.Count);
        Assert.All(documentNotifications, item =>
        {
            Assert.Equal(owner.Id, item.RecipientUserId);
            Assert.Equal("case", item.ObjectType);
            Assert.Equal(clientCase.Id, item.ObjectId);
        });
        Assert.Contains(documentNotifications, item =>
            item.MetadataJson.Contains($"\"clientCaseDocumentId\":{first.Id}", StringComparison.Ordinal)
            && item.MetadataJson.Contains("\"action\":\"uploaded\"", StringComparison.Ordinal));
        Assert.Contains(documentNotifications, item =>
            item.MetadataJson.Contains($"\"clientCaseDocumentId\":{second.Id}", StringComparison.Ordinal)
            && item.MetadataJson.Contains("\"action\":\"version-created\"", StringComparison.Ordinal));
        Assert.Contains(documentNotifications, item =>
            item.MetadataJson.Contains($"\"clientCaseDocumentId\":{first.Id}", StringComparison.Ordinal)
            && item.MetadataJson.Contains("\"action\":\"deleted\"", StringComparison.Ordinal));
        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Документ «claim.txt» загружен", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Документ «claim-v2.txt»: загружена новая версия", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Документ «claim.txt» удален", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_ForbidMutationsWithoutCaseEditPermission()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "documents-viewer@example.com", "Наблюдатель документов");
        viewer.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanEdit = false;
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = viewer.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.txt",
            StoredFileName = "claim-v1.txt",
            StoragePath = "15/claim-v1.txt",
            ContentType = "text/plain",
            SizeBytes = 5,
            UploadedByUserId = viewer.Id,
            UploadedByName = viewer.FullName,
            UploadedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();

        var controller = CreateController(db, viewer);

        var listResult = await controller.GetDocuments(clientCase.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(listResult.Result);

        var uploadResult = await controller.UploadDocument(
            clientCase.Id,
            CreateFormFile("blocked", "blocked.txt"),
            "Иск",
            "Подготовлен",
            documentKey: null,
            ct: CancellationToken.None);
        Assert.IsType<ForbidResult>(uploadResult.Result);

        var versionResult = await controller.UploadDocumentVersionById(
            document.Id,
            CreateFormFile("blocked", "blocked-v2.txt"),
            documentType: null,
            status: null,
            ct: CancellationToken.None);
        Assert.IsType<ForbidResult>(versionResult.Result);

        var generateResult = await controller.GenerateDocumentTemplate(
            clientCase.Id,
            new CasesV2Controller.GenerateCaseDocumentTemplateRequest { TemplateKey = "claim" },
            CancellationToken.None);
        Assert.IsType<ForbidResult>(generateResult.Result);

        var updateResult = await controller.UpdateDocument(
            clientCase.Id,
            document.Id,
            new CasesV2Controller.UpdateCaseDocumentRequest { Status = "Отправлен" },
            CancellationToken.None);
        Assert.IsType<ForbidResult>(updateResult.Result);

        var updateByIdResult = await controller.UpdateDocumentById(
            document.Id,
            new CasesV2Controller.UpdateCaseDocumentRequest { Status = "Отправлен" },
            CancellationToken.None);
        Assert.IsType<ForbidResult>(updateByIdResult.Result);
    }

    [Fact]
    public async Task Documents_ForbidDeleteWithoutCaseDeletePermission()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "documents-owner@example.com", "Владелец документов");
        owner.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanDelete = false;
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.txt",
            StoredFileName = "claim-v1.txt",
            StoragePath = "15/claim-v1.txt",
            ContentType = "text/plain",
            SizeBytes = 5,
            UploadedByUserId = owner.Id,
            UploadedByName = owner.FullName,
            UploadedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();

        var controller = CreateController(db, owner);

        var uploadResult = await controller.UploadDocument(
            clientCase.Id,
            CreateFormFile("allowed", "allowed.txt"),
            "Иск",
            "Подготовлен",
            documentKey: null,
            ct: CancellationToken.None);
        Assert.IsType<CreatedResult>(uploadResult.Result);

        var deleteResult = await controller.DeleteDocument(clientCase.Id, document.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResult);
        Assert.Contains(await db.ClientCaseDocuments.ToListAsync(), item => item.Id == document.Id);
    }

    [Fact]
    public async Task DocumentTemplates_ReturnsSupportedCaseDocumentTemplates()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentTemplates(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var templates = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateDefinitionDto>>(ok.Value);

        Assert.Contains(templates, item => item.Key == "case-summary" && item.DocumentType == "Карточка дела");
        Assert.Contains(templates, item => item.Key == "claim" && item.DocumentType == "Иск");
        Assert.Contains(templates, item => item.Key == "court-letter" && item.DocumentType == "Переписка");
        Assert.Contains(templates, item => item.Key == "power-of-attorney" && item.DocumentType == "Доверенность");
        Assert.Contains(templates, item => item.Key == "application" && item.DocumentType == "Заявление");
        Assert.Contains(templates, item => item.Key == "procedural-document" && item.DocumentType == "Процессуальный документ");
    }

    [Fact]
    public async Task DocumentTemplateTokens_ReturnsSupportedPlaceholders()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentTemplateTokens(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var tokens = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateTokenDto>>(ok.Value);

        Assert.Contains(tokens, item =>
            item.Key == "case.title" &&
            item.Category == "Дело" &&
            item.Placeholder == "{{case.title}}");
        Assert.Contains(tokens, item => item.Key == "client.name" && item.Category == "Клиент");
        Assert.Contains(tokens, item => item.Key == "document.type" && item.Category == "Документ");
        Assert.DoesNotContain(tokens, item => string.IsNullOrWhiteSpace(item.Description));
    }

    [Fact]
    public async Task DocumentTemplates_CreatePatchAndRenderCustomTemplate()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Dictionaries,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            ViewScope = DataVisibilityScope.All,
        });
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            CaseType = "Банкротство",
            CourtName = "Арбитражный суд Самарской области",
            ResponsibleUserId = owner.Id,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createResult = await controller.CreateDocumentTemplate(new CasesV2Controller.UpsertCaseDocumentTemplateRequest
        {
            Key = "custom-claim",
            Label = "Рабочий шаблон",
            DocumentType = "Иск",
            Heading = "Заявление",
            CaseTypes = new List<string> { "Банкротство", " Банкротство ", "Семейное право" },
            Content = "<p>{{case.title}} — {{client.name}} — {{court.name}}</p>",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<CasesV2Controller.CaseDocumentTemplateDefinitionDto>(created.Value);
        Assert.Equal("custom-claim", createdDto.Key);
        Assert.Equal(1, createdDto.Version);
        Assert.Equal(new[] { "Банкротство", "Семейное право" }, createdDto.CaseTypes);

        var updateResult = await controller.UpdateDocumentTemplate(
            "custom-claim",
            new CasesV2Controller.UpsertCaseDocumentTemplateRequest
            {
                Label = "Обновленный шаблон",
                Content = "<p>{{case.title}} / {{responsible.name}}</p>",
            },
            CancellationToken.None);

        var updatedOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedDto = Assert.IsType<CasesV2Controller.CaseDocumentTemplateDefinitionDto>(updatedOk.Value);
        Assert.Equal("Обновленный шаблон", updatedDto.Label);
        Assert.Equal(2, updatedDto.Version);
        Assert.Equal(new[] { "Банкротство", "Семейное право" }, updatedDto.CaseTypes);

        var listResult = await controller.GetDocumentTemplates(CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var templates = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateDefinitionDto>>(listOk.Value);
        Assert.Contains(templates, item => item.Key == "custom-claim" && item.Label == "Обновленный шаблон");

        var renderResult = await controller.RenderDocumentTemplate(
            "custom-claim",
            new CasesV2Controller.RenderCaseDocumentTemplateRequest { ClientCaseId = clientCase.Id },
            CancellationToken.None);

        var renderOk = Assert.IsType<OkObjectResult>(renderResult.Result);
        var rendered = Assert.IsType<CasesV2Controller.RenderedCaseDocumentTemplateDto>(renderOk.Value);
        Assert.Equal("custom-claim", rendered.TemplateKey);
        Assert.Contains("Земцов - банкротство", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("Владелец", rendered.Html, StringComparison.Ordinal);

        var restrictedCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - трудовой спор",
            CaseType = "Трудовой спор",
            ResponsibleUserId = owner.Id,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(restrictedCase);
        await db.SaveChangesAsync();

        var restrictedRenderResult = await controller.RenderDocumentTemplate(
            "custom-claim",
            new CasesV2Controller.RenderCaseDocumentTemplateRequest { ClientCaseId = restrictedCase.Id },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(restrictedRenderResult.Result);
    }

    [Fact]
    public async Task DocumentTemplates_ForbidEditWithoutDictionaryPermission()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Dictionaries,
            CanView = true,
            CanEdit = false,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.CreateDocumentTemplate(new CasesV2Controller.UpsertCaseDocumentTemplateRequest
        {
            Key = "blocked-template",
            Label = "Закрытый шаблон",
            DocumentType = "Документ",
            Heading = "Закрытый шаблон",
            Content = "<p>{{case.title}}</p>",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task DocumentTypes_ReturnsTemplateAndStoredTypes()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.ClientCaseDocuments.Add(new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "reconciliation",
            Version = 1,
            DocumentType = "Акт сверки",
            Status = "Подготовлен",
            OriginalFileName = "reconciliation.xlsx",
            StoredFileName = "reconciliation.xlsx",
            StoragePath = "1/reconciliation.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentTypes(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var types = Assert.IsAssignableFrom<IReadOnlyList<string>>(ok.Value);
        Assert.Contains("Акт сверки", types);
        Assert.Contains("Иск", types);
        Assert.Contains("Жалоба", types);
        Assert.Contains("Доказательство", types);
        Assert.Contains("Переписка", types);
    }

    [Fact]
    public async Task CaseDocumentStorage_SavesAndProtectsRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var storage = new LocalCaseDocumentStorage(new TestWebHostEnvironment
        {
            ContentRootPath = contentRoot,
        });
        var relativePath = storage.BuildRelativePath(15, "claim.txt");

        await storage.SaveAsync(relativePath, Encoding.UTF8.GetBytes("claim"), CancellationToken.None);

        Assert.True(storage.Exists(relativePath));
        Assert.EndsWith(Path.Combine("App_Data", "case-documents", "15", "claim.txt"), storage.ResolvePath(relativePath));
        Assert.Throws<InvalidOperationException>(() => storage.ResolvePath(Path.Combine("..", "outside.txt")));

        storage.DeleteIfExists(relativePath);
        Assert.False(storage.Exists(relativePath));
    }

    [Fact]
    public async Task GetDocumentById_ReturnsDocumentWhenCaseIsVisible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var first = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "template-claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim-v1.html",
            StoredFileName = "claim-v1.html",
            StoragePath = "1/claim-v1.html",
            ContentType = "text/html",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var second = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "template-claim",
            Version = 2,
            DocumentType = "Иск",
            Status = "Подписан",
            OriginalFileName = "claim-v2.html",
            StoredFileName = "claim-v2.html",
            StoragePath = "1/claim-v2.html",
            ContentType = "text/html",
            SizeBytes = 2048,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.AddRange(first, second);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var oldVersionResult = await controller.GetDocumentById(first.Id, CancellationToken.None);
        var latestResult = await controller.GetDocumentById(second.Id, CancellationToken.None);

        var oldOk = Assert.IsType<OkObjectResult>(oldVersionResult.Result);
        var oldDto = Assert.IsType<ClientCaseDocumentDto>(oldOk.Value);
        Assert.False(oldDto.IsLatestVersion);
        Assert.Equal("claim", oldDto.TemplateKey);

        var latestOk = Assert.IsType<OkObjectResult>(latestResult.Result);
        var latestDto = Assert.IsType<ClientCaseDocumentDto>(latestOk.Value);
        Assert.True(latestDto.IsLatestVersion);
        Assert.Equal("Подписан", latestDto.Status);
    }

    [Fact]
    public async Task GetDocumentById_ReturnsSourceTaskIdForTaskDocuments()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "task-42-file",
            Version = 1,
            DocumentType = "Документ задачи",
            Status = "Загружен",
            OriginalFileName = "task-file.txt",
            StoredFileName = "task-file.txt",
            StoragePath = "1/task-file.txt",
            ContentType = "text/plain",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentById(document.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(ok.Value);
        Assert.Equal(42, dto.SourceTaskId);
    }

    [Fact]
    public async Task GetDocumentById_ReturnsExtendedDocumentSources()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Owner");
        var client = new Client
        {
            Name = "Client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "court-event-77",
            Version = 1,
            DocumentType = "Court",
            Status = "Ready",
            OriginalFileName = "court.html",
            StoredFileName = "court.html",
            StoragePath = "1/court.html",
            ContentType = "text/html",
            SizeBytes = 100,
            UploadedByName = "Owner",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentById(document.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(ok.Value);
        Assert.Equal(77, dto.SourceEventId);
    }

    [Fact]
    public async Task GetDocumentRegistry_ReturnsLatestVisibleDocumentsWithFilters()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var other = await CreateUserAsync(db, "other@example.com", "Другой");
        var visibleClient = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = other.Id,
        };
        var visibleCase = new ClientCase
        {
            Client = visibleClient,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
        };
        db.ClientCaseDocuments.AddRange(
            new ClientCaseDocument
            {
                ClientCase = visibleCase,
                DocumentKey = "claim",
                Version = 1,
                DocumentType = "Иск",
                Status = "Подготовлен",
                OriginalFileName = "claim-v1.pdf",
                StoredFileName = "claim-v1.pdf",
                StoragePath = "1/claim-v1.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1024,
                UploadedByName = "Владелец",
                UploadedAt = DateTime.UtcNow.AddMinutes(-10),
            },
            new ClientCaseDocument
            {
                ClientCase = visibleCase,
                DocumentKey = "claim",
                Version = 2,
                DocumentType = "Иск",
                Status = "Подписан",
                OriginalFileName = "claim-v2.pdf",
                StoredFileName = "claim-v2.pdf",
                StoragePath = "1/claim-v2.pdf",
                ContentType = "application/pdf",
                SizeBytes = 2048,
                UploadedByName = "Владелец",
                UploadedAt = DateTime.UtcNow,
            },
            new ClientCaseDocument
            {
                ClientCase = hiddenCase,
                DocumentKey = "claim-hidden",
                Version = 1,
                DocumentType = "Иск",
                Status = "Подготовлен",
                OriginalFileName = "claim-hidden.pdf",
                StoredFileName = "claim-hidden.pdf",
                StoragePath = "2/claim-hidden.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1024,
                UploadedByName = "Другой",
                UploadedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentRegistry(
            clientCaseId: null,
            search: "claim",
            documentType: "Иск",
            status: null,
            sourceTaskId: null,
            latestOnly: true,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var property = ok.Value!.GetType().GetProperty("items");
        Assert.NotNull(property);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(property.GetValue(ok.Value));
        var item = Assert.Single(items);
        Assert.Equal("claim-v2.pdf", item.OriginalFileName);
        Assert.True(item.IsLatestVersion);
    }

    [Fact]
    public async Task DownloadDocumentById_ReturnsFileWhenCaseIsVisible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var storagePath = Path.Combine("15", "claim.txt");
        var fullPath = Path.Combine(contentRoot, "App_Data", "case-documents", storagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "claim", Encoding.UTF8);
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.txt",
            StoredFileName = "claim.txt",
            StoragePath = storagePath,
            ContentType = "text/plain",
            SizeBytes = 5,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner, contentRoot);

        var result = await controller.DownloadDocumentById(document.Id, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(fullPath, file.FileName);
        Assert.Equal("text/plain", file.ContentType);
        Assert.Equal("claim.txt", file.FileDownloadName);

        var scopedResult = await controller.DownloadDocument(clientCase.Id, document.Id, CancellationToken.None);
        var scopedFile = Assert.IsType<PhysicalFileResult>(scopedResult);
        Assert.Equal(fullPath, scopedFile.FileName);
        Assert.Equal("claim.txt", scopedFile.FileDownloadName);

        var auditEvents = await db.ObjectAuditEvents
            .Where(item => item.ObjectType == "client-case-document")
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, auditEvents.Count);
        Assert.All(auditEvents, auditEvent =>
        {
            Assert.Equal(document.Id, auditEvent.ObjectId);
            Assert.Equal("Downloaded", auditEvent.Action);
            Assert.Contains("claim.txt", auditEvent.Description, StringComparison.Ordinal);
            Assert.Contains("\"clientCaseId\"", auditEvent.MetadataJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Documents_GeneratesTemplateFromCaseDataAndStoresVersionedFile()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            CaseNumber = "2026-001",
            CaseType = "Банкротство",
            Stage = "Подготовка заявления",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            Claimant = "Земцов Виктор Александрович",
            Defendant = "Банк",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var result = await controller.GenerateDocumentTemplate(
            clientCase.Id,
            new CasesV2Controller.GenerateCaseDocumentTemplateRequest
            {
                TemplateKey = "claim",
                Status = "Подготовлен",
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);

        Assert.Equal("template-claim", dto.DocumentKey);
        Assert.Equal("claim", dto.TemplateKey);
        Assert.Equal("Исковое заявление", dto.TemplateLabel);
        Assert.Equal("Иск", dto.DocumentType);
        Assert.Equal("Подготовлен", dto.Status);
        Assert.Equal(1, dto.Version);
        Assert.EndsWith(".html", dto.OriginalFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("text/html; charset=utf-8", dto.ContentType);
        Assert.True(dto.SizeBytes > 0);

        var stored = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.Id);
        var fullPath = Path.Combine(contentRoot, "App_Data", "case-documents", stored.StoragePath);
        Assert.True(File.Exists(fullPath));
        var html = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        Assert.Contains("Исковое заявление", html, StringComparison.Ordinal);
        Assert.Contains("Земцов Виктор Александрович", html, StringComparison.Ordinal);
        Assert.Contains("Арбитражный суд Самарской области", html, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Сформирован шаблон", clientCase.EventHistory, StringComparison.Ordinal);
        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item => item.ObjectType == "client-case-document");
        Assert.Equal(dto.Id, auditEvent.ObjectId);
        Assert.Equal("Generated", auditEvent.Action);
        Assert.Contains("template-claim", auditEvent.MetadataJson, StringComparison.Ordinal);
        var notification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CaseDocument);
        Assert.Equal(owner.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains($"\"clientCaseDocumentId\":{dto.Id}", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"generated\"", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains(dto.OriginalFileName, notification.BodyPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_GeneratesPowerOfAttorneyTemplate()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Доверитель",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для доверенности",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            Stage = "Суд первой инстанции",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var result = await controller.GenerateDocumentTemplate(
            clientCase.Id,
            new CasesV2Controller.GenerateCaseDocumentTemplateRequest
            {
                TemplateKey = "power-of-attorney",
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);
        Assert.Equal("template-power-of-attorney", dto.DocumentKey);
        Assert.Equal("power-of-attorney", dto.TemplateKey);
        Assert.Equal("Доверенность", dto.DocumentType);

        var stored = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.Id);
        var html = await File.ReadAllTextAsync(Path.Combine(contentRoot, "App_Data", "case-documents", stored.StoragePath), Encoding.UTF8);
        Assert.Contains("Доверенность", html, StringComparison.Ordinal);
        Assert.Contains("Доверитель", html, StringComparison.Ordinal);
        Assert.Contains("специальных полномочий", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_GeneratesCustomTemplateFromLibrary()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Dictionaries,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            ViewScope = DataVisibilityScope.All,
        });
        var client = new Client
        {
            Name = "Custom Client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Custom Case",
            CaseNumber = "CUSTOM-1",
            CaseType = "Briefing",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var createResult = await controller.CreateDocumentTemplate(new CasesV2Controller.UpsertCaseDocumentTemplateRequest
        {
            Key = "custom-brief",
            Label = "Custom Brief",
            DocumentType = "Brief",
            Heading = "Custom Heading",
            CaseTypes = new List<string> { "Briefing" },
            Content = "<html><body><h1>{{case.title}}</h1><p>{{client.name}}</p><p>{{document.type}}</p></body></html>",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createResult.Result);

        var result = await controller.GenerateDocumentTemplate(
            clientCase.Id,
            new CasesV2Controller.GenerateCaseDocumentTemplateRequest
            {
                TemplateKey = "custom-brief",
                Status = "Draft",
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);

        Assert.Equal("template-custom-brief", dto.DocumentKey);
        Assert.Equal("custom-brief", dto.TemplateKey);
        Assert.Equal("Custom Brief", dto.TemplateLabel);
        Assert.Equal("Brief", dto.DocumentType);

        var stored = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.Id);
        var fullPath = Path.Combine(contentRoot, "App_Data", "case-documents", stored.StoragePath);
        var html = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        Assert.Contains("<h1>Custom Case</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Custom Client</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Brief</p>", html, StringComparison.Ordinal);

        var restrictedCase = new ClientCase
        {
            Client = client,
            Title = "Restricted Case",
            CaseNumber = "CUSTOM-2",
            CaseType = "Labor",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.ClientCases.Add(restrictedCase);
        await db.SaveChangesAsync();

        var restrictedResult = await controller.GenerateDocumentTemplate(
            restrictedCase.Id,
            new CasesV2Controller.GenerateCaseDocumentTemplateRequest
            {
                TemplateKey = "custom-brief",
                Status = "Draft",
            },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(restrictedResult.Result);
    }

    [Fact]
    public async Task UpdateDocument_ChangesStatusAndWritesCaseHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            EventHistory = "Дело создано",
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.pdf",
            StoredFileName = "claim.pdf",
            StoragePath = "1/claim.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.UpdateDocument(clientCase.Id, document.Id, new CasesV2Controller.UpdateCaseDocumentRequest
        {
            Status = " Отправлен ",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(ok.Value);
        Assert.Equal("Отправлен", dto.Status);

        await db.Entry(clientCase).ReloadAsync();
        await db.Entry(document).ReloadAsync();
        Assert.Equal("Отправлен", document.Status);
        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item => item.ObjectType == "client-case-document");
        Assert.Equal(document.Id, auditEvent.ObjectId);
        Assert.Equal("Updated", auditEvent.Action);
        Assert.Contains("Отправлен", auditEvent.Description, StringComparison.Ordinal);
        var notification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CaseDocument);
        Assert.Equal(owner.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains($"\"clientCaseDocumentId\":{document.Id}", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"updated\"", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("claim.pdf", notification.BodyPreview, StringComparison.Ordinal);
        Assert.Contains("Документ «claim.pdf»: статус «Подготовлен» → «Отправлен»", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Изменил: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDocumentById_ChangesStatusAndWritesCaseHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            EventHistory = "Дело создано",
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.pdf",
            StoredFileName = "claim.pdf",
            StoragePath = "1/claim.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.UpdateDocumentById(document.Id, new CasesV2Controller.UpdateCaseDocumentRequest
        {
            DocumentType = " Ходатайство ",
            Status = " На подписи ",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(ok.Value);
        Assert.Equal("Ходатайство", dto.DocumentType);
        Assert.Equal("На подписи", dto.Status);

        await db.Entry(clientCase).ReloadAsync();
        await db.Entry(document).ReloadAsync();
        Assert.Equal("Ходатайство", document.DocumentType);
        Assert.Equal("На подписи", document.Status);
        Assert.Contains("тип «Иск» → «Ходатайство»", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("статус «Подготовлен» → «На подписи»", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadDocumentVersionById_CreatesNextVersionForExistingDocument()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim-v1.pdf",
            StoredFileName = "claim-v1.pdf",
            StoragePath = "1/claim-v1.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var result = await controller.UploadDocumentVersionById(
            document.Id,
            CreateFormFile("second version", "claim-v2.pdf", "application/pdf"),
            documentType: null,
            status: " Подписан ",
            ct: CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);
        Assert.Equal(document.DocumentKey, dto.DocumentKey);
        Assert.Equal(2, dto.Version);
        Assert.Equal("Иск", dto.DocumentType);
        Assert.Equal("Подписан", dto.Status);
        Assert.True(dto.IsLatestVersion);

        var stored = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.Id);
        Assert.Equal(document.DocumentKey, stored.DocumentKey);
        Assert.True(File.Exists(Path.Combine(contentRoot, "App_Data", "case-documents", stored.StoragePath)));

        var oldVersionResult = await controller.GetDocumentById(document.Id, CancellationToken.None);
        var oldOk = Assert.IsType<OkObjectResult>(oldVersionResult.Result);
        var oldDto = Assert.IsType<ClientCaseDocumentDto>(oldOk.Value);
        Assert.False(oldDto.IsLatestVersion);

        await db.Entry(clientCase).ReloadAsync();
        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item => item.ObjectType == "client-case-document");
        Assert.Equal(dto.Id, auditEvent.ObjectId);
        Assert.Equal("VersionCreated", auditEvent.Action);
        Assert.Contains(document.DocumentKey, auditEvent.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("Документ «claim-v2.pdf»: загружена новая версия", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkActivity_UpdatesLastViewAndConfirmationHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - активность",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.MarkActivity(clientCase.Id, new CasesV2Controller.MarkClientCaseActivityRequest
        {
            Confirmed = true,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseActivityDto>(ok.Value);
        Assert.Equal(owner.Id, dto.LastViewedByUserId);
        Assert.Equal("Владелец", dto.LastViewedByUserName);
        Assert.NotNull(dto.LastViewedByResponsibleAt);
        Assert.Equal(owner.Id, dto.LastViewedByResponsibleUserId);
        Assert.Equal("Владелец", dto.LastViewedByResponsibleUserName);
        Assert.Equal(owner.Id, dto.ActivityConfirmedByUserId);
        Assert.Contains("Активность по делу подтверждена", dto.EventHistory, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.NotNull(clientCase.LastViewedAt);
        Assert.NotNull(clientCase.ActivityConfirmedAt);
        Assert.Contains("Подтвердил: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeline_ReturnsParsedCaseHistoryEntries()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - история",
            EventHistory = """
                18.05.2026 10:30: Создано дело.
                18.05.2026 11:15: Комментарий по делу: Проверить срок.
                """,
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetTimeline(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var timeline = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseTimelineEntryDto>>(ok.Value);

        Assert.Equal(2, timeline.Count);
        Assert.Equal("Комментарий по делу: Проверить срок.", timeline[0].Text);
        Assert.Equal(new DateTime(2026, 5, 18, 11, 15, 0, DateTimeKind.Utc), timeline[0].OccurredAt);
        Assert.Equal("Создано дело.", timeline[1].Text);
    }

    [Fact]
    public async Task AddHistoryEntry_AppendsUserCommentToCaseHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - история",
            Status = ClientCaseStatus.Open,
            EventHistory = "Дело создано",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.AddHistoryEntry(clientCase.Id, new CasesV2Controller.AddClientCaseHistoryEntryRequest
        {
            Text = " Нужно проверить судебные сроки ",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseActivityDto>(ok.Value);
        Assert.Contains("Комментарий по делу: Нужно проверить судебные сроки. Автор: Владелец.", dto.EventHistory, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Дело создано", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Комментарий по делу: Нужно проверить судебные сроки. Автор: Владелец.", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddHistoryEntry_UsesUserTimeZoneForHistoryTimestamp()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "timezone-history@example.com", "Владелец");
        var nowUtc = DateTime.UtcNow;
        var selectedZone = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new
            {
                Zone = zone,
                LocalNow = CalendarTimeZoneHelper.ToUserLocal(nowUtc, zone),
            })
            .FirstOrDefault(item => item.LocalNow.Date != nowUtc.Date);
        Assert.NotNull(selectedZone);
        var client = new Client
        {
            Name = "Клиент истории",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с историей по timezone",
            Status = ClientCaseStatus.Open,
            EventHistory = "Дело создано",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone!.Zone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.AddHistoryEntry(clientCase.Id, new CasesV2Controller.AddClientCaseHistoryEntryRequest
        {
            Text = "Проверить местное время",
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        await db.Entry(clientCase).ReloadAsync();
        var commentLine = clientCase.EventHistory
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.Contains("Проверить местное время", StringComparison.Ordinal));
        var expectedLocalDate = selectedZone.LocalNow.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        var unexpectedUtcDate = nowUtc.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        Assert.StartsWith(expectedLocalDate, commentLine, StringComparison.Ordinal);
        Assert.NotEqual(unexpectedUtcDate, expectedLocalDate);
    }

    [Fact]
    public async Task GetTimeline_ParsesHistoryTimestampAsUserLocalTime()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "timezone-timeline@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент таймлайна",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с таймлайном",
            Status = ClientCaseStatus.Open,
            EventHistory = "05.06.2026 16:00: Комментарий по делу: Суд назначен на 16:00. Автор: Владелец.",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetTimeline(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseTimelineEntryDto>>(ok.Value);
        var entry = Assert.Single(entries);
        Assert.Equal(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc), entry.OccurredAt);
        Assert.Contains("Суд назначен на 16:00", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkActivity_DoesNotResetLastViewForNonResponsibleViewer()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var viewedAt = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc);
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - активность",
            Status = ClientCaseStatus.Open,
            LastViewedAt = viewedAt,
            LastViewedByUserId = owner.Id,
            LastViewedByUserName = owner.FullName,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.MarkActivity(clientCase.Id, new CasesV2Controller.MarkClientCaseActivityRequest
        {
            Confirmed = false,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseActivityDto>(ok.Value);
        Assert.Equal(viewedAt, dto.LastViewedAt);
        Assert.Equal(owner.Id, dto.LastViewedByUserId);
        Assert.Equal(viewedAt, dto.LastViewedByResponsibleAt);
        Assert.Equal(owner.Id, dto.LastViewedByResponsibleUserId);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(viewedAt, clientCase.LastViewedAt);
        Assert.Equal(owner.Id, clientCase.LastViewedByUserId);
    }

    [Fact]
    public async Task RunCourtSync_RecordsManualCourtCheckInHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - суд",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            CourtUrl = "https://kad.arbitr.ru/card/123",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);

        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("manual-check-recorded", dto.Status);
        Assert.True(dto.HasCourtBinding);
        Assert.Contains("автоматическое обновление сейчас поддерживает ссылки на сайты sudrf.ru", dto.Message, StringComparison.Ordinal);
        Assert.Equal("Владелец", dto.CheckedByUserName);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Ручная проверка судебного источника зафиксирована", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Инициатор: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCourtSync_ManualOnlyRecordsCommentWithoutExternalUpdate()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - ручная проверка",
            CourtName = "Текущий суд",
            CourtCaseNumber = "2-100/2026",
            CourtUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=100",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Новый суд из источника",
            CaseNumber = "2-100/2026",
            MovementEvents =
            [
                new()
                {
                    Title = "Судебное заседание",
                    ScheduledAt = new DateTime(2026, 6, 3, 10, 0, 0),
                    SourceSummary = "Судебное заседание; 03.06.2026 10:00",
                },
            ],
        };
        var controller = CreateController(db, owner, sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData));

        var result = await controller.RunCourtSync(
            clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncRunRequest
            {
                ManualOnly = true,
                Comment = "Проверено по телефону суда, изменений нет.",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);

        Assert.Equal("manual-check-recorded", dto.Status);
        Assert.False(dto.SyncedFromExternalSource);
        Assert.Contains("без автоматического обновления", dto.Message, StringComparison.Ordinal);
        Assert.Contains("Комментарий: Проверено по телефону суда, изменений нет.", dto.Message, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("Текущий суд", clientCase.CourtName);
        Assert.Contains("Проверено по телефону суда, изменений нет.", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Equal(0, await db.ClientCaseCourtEvents.CountAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task GetCourtInstances_ReturnsFourTabsAndLegacyFirstInstance()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Кирпита Наталья Андреевна",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Кирпита - взыскание",
            Status = ClientCaseStatus.Open,
            CourtName = "Центральный районный суд",
            CourtCaseNumber = "2-100/2026",
            CourtUrl = "https://example.test/first",
            JudgeName = "Иванов И.И.",
            Courtroom = "зал 5",
            CourtSecretaryContacts = "секретарь Петрова",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetCourtInstances(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtInstanceDto>>(ok.Value);
        Assert.Equal(4, items.Count);
        Assert.Equal(["first", "appeal", "cassation", "supreme"], items.Select(item => item.Instance).ToArray());
        var first = Assert.Single(items, item => item.Instance == "first");
        Assert.Equal("Центральный районный суд", first.CourtName);
        Assert.Equal("2-100/2026", first.CourtCaseNumber);
        Assert.Equal("https://example.test/first", first.CourtUrl);
        Assert.Equal("Верховный суд", items.Single(item => item.Instance == "supreme").InstanceLabel);
    }

    [Fact]
    public async Task CourtInstanceComplaintFlow_CreatesUpdatesAndArchivesComplaint()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Кирпита Наталья Андреевна",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Кирпита - апелляция",
            Status = ClientCaseStatus.Open,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "appeal-complaint",
            OriginalFileName = "appeal.pdf",
            StoredFileName = "appeal.pdf",
            StoragePath = "appeal.pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            DocumentType = "Жалоба",
            Status = "prepared",
            UploadedByName = owner.FullName,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var instanceResult = await controller.UpsertCourtInstance(clientCase.Id, "appeal", new CasesV2Controller.UpsertClientCaseCourtInstanceRequest
        {
            CourtName = "Самарский областной суд",
            CourtCaseNumber = "33-123/2026",
            CourtUrl = "https://example.test/appeal",
            JudgeName = "Сидоров С.С.",
        }, CancellationToken.None);
        var instanceOk = Assert.IsType<OkObjectResult>(instanceResult.Result);
        var instanceDto = Assert.IsType<CasesV2Controller.ClientCaseCourtInstanceDto>(instanceOk.Value);
        Assert.Equal("appeal", instanceDto.Instance);
        Assert.Equal("Самарский областной суд", instanceDto.CourtName);

        var complaintResult = await controller.CreateCourtComplaint(clientCase.Id, "appeal", new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "privateComplaint",
            Title = "Частная жалоба на определение",
            TargetInstance = "cassation",
            FiledAt = new DateTime(2026, 5, 24),
            Status = "filed",
            ClientCaseDocumentId = document.Id,
            Notes = "Подана через суд первой инстанции",
        }, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(complaintResult.Result);
        var complaintDto = Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(created.Value);
        Assert.Equal("privateComplaint", complaintDto.ComplaintType);
        Assert.Equal("Частная жалоба", complaintDto.ComplaintTypeLabel);
        Assert.Equal("cassation", complaintDto.TargetInstance);
        Assert.Equal(document.Id, complaintDto.ClientCaseDocumentId);

        var updateResult = await controller.UpdateCourtComplaint(clientCase.Id, "appeal", complaintDto.Id, new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "privateComplaint",
            Title = "Частная жалоба на определение суда",
            TargetInstance = "cassation",
            Status = "accepted",
        }, CancellationToken.None);
        var updateOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updated = Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(updateOk.Value);
        Assert.Equal("accepted", updated.Status);
        Assert.Equal("Принята", updated.StatusLabel);

        var archiveResult = await controller.ArchiveCourtComplaint(clientCase.Id, "appeal", complaintDto.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(archiveResult);

        var archived = await db.ClientCaseCourtComplaints.SingleAsync(item => item.Id == complaintDto.Id);
        Assert.True(archived.IsArchived);
    }

    [Fact]
    public async Task RunCourtSync_UpdatesCaseAndCreatesCourtEventFromSudrf()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Шафорост Антон Евгеньевич",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Шафорост - апелляция",
            CourtUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Хабаровский краевой суд",
            CaseNumber = "33а-3390/2026",
            JudgeName = "Казарикова Оксана Витальевна",
            SecretaryName = "Иванова И.И.",
            Instance = "appeal",
            NextHearingAt = new DateTime(2026, 5, 27, 15, 0, 0),
            NextHearingPlace = "зал 12",
            Participants =
            [
                new() { Role = "АДМИНИСТРАТИВНЫЙ ИСТЕЦ", Name = "Шафорост Антон Евгеньевич" },
                new() { Role = "АДМИНИСТРАТИВНЫЙ ОТВЕТЧИК", Name = "Главное управление ФССП по Хабаровскому краю и ЕАО" },
                new() { Role = "ПРЕДСТАВИТЕЛЬ", Name = "Кожеваткин Андрей Владимирович" },
            ],
            MovementEvents =
            [
                new()
                {
                    Title = "Судебное заседание",
                    ScheduledAt = new DateTime(2026, 5, 27, 15, 0, 0),
                    Place = "зал 12",
                    SourceSummary = "Судебное заседание; 27.05.2026 15:00; место: зал 12",
                    SecretaryName = "Иванова И.И.",
                },
            ],
            CourtActs =
            [
                new()
                {
                    Title = "Апелляционное определение",
                    ActDate = new DateTime(2026, 6, 1),
                    PostedAt = new DateTime(2026, 6, 2),
                    Result = "Решение оставлено без изменения",
                    Summary = "Апелляционное определение; 01.06.2026; Решение оставлено без изменения",
                    SourceUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
                },
            ],
        };
        var controller = CreateController(db, owner, sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData));

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);
        Assert.Equal("external-sync-completed", dto.Status);
        Assert.True(dto.SyncedFromExternalSource);
        Assert.Equal(1, dto.CreatedCourtEvents);
        Assert.Equal(3, dto.CreatedCourtParties);
        Assert.Equal(1, dto.CreatedCourtActDocuments);
        Assert.Equal(1, dto.CreatedProceduralDeadlines);
        Assert.Contains("обновлены поля", dto.Message, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("Хабаровский краевой суд", clientCase.CourtName);
        Assert.Equal("33а-3390/2026", clientCase.CourtCaseNumber);
        Assert.Equal("Казарикова Оксана Витальевна", clientCase.JudgeName);
        Assert.Equal("Шафорост Антон Евгеньевич", clientCase.Claimant);
        Assert.Equal("Главное управление ФССП по Хабаровскому краю и ЕАО", clientCase.Defendant);
        Assert.Equal("Кожеваткин Андрей Владимирович", clientCase.Representatives);
        Assert.Equal(new DateTime(2026, 5, 27, 15, 0, 0), clientCase.NextHearingAt);
        Assert.Contains("Судебный источник обновлен", clientCase.EventHistory, StringComparison.Ordinal);

        var courtEvent = await db.ClientCaseCourtEvents.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("sudrf", courtEvent.SourceType);
        Assert.Equal("appeal", courtEvent.CourtInstance);
        Assert.Equal(new DateTime(2026, 5, 27, 15, 0, 0), courtEvent.ScheduledAt);
        Assert.Equal("зал 12", courtEvent.Courtroom);
        Assert.Equal("Иванова И.И.", courtEvent.SecretaryName);

        var history = await db.ClientCaseCourtEventHistory.SingleAsync(item => item.ClientCaseCourtEventId == courtEvent.Id);
        Assert.Contains("ГАС «Правосудие»", history.Summary, StringComparison.Ordinal);

        var courtParties = await db.ClientCaseCourtParties
            .Where(item => item.ClientCaseId == clientCase.Id)
            .OrderBy(item => item.DisplayName)
            .ToListAsync();
        Assert.Equal(3, courtParties.Count);
        Assert.Contains(courtParties, item =>
            item.DisplayName == "Шафорост Антон Евгеньевич"
            && item.Side == "claimant"
            && item.ProcessRole == "АДМИНИСТРАТИВНЫЙ ИСТЕЦ"
            && item.Notes.Contains("ГАС «Правосудие»", StringComparison.Ordinal));
        Assert.Contains(courtParties, item =>
            item.DisplayName == "Главное управление ФССП по Хабаровскому краю и ЕАО"
            && item.Side == "respondent");
        Assert.Contains(courtParties, item =>
            item.DisplayName == "Кожеваткин Андрей Владимирович"
            && item.Side == "other"
            && item.ProcessRole == "ПРЕДСТАВИТЕЛЬ");

        var document = await db.ClientCaseDocuments.SingleAsync(item => item.ClientCaseId == clientCase.Id && item.DocumentType == "Судебный акт");
        Assert.Equal("received", document.Status);
        Assert.StartsWith("sudrf-act-", document.DocumentKey, StringComparison.Ordinal);

        var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("cassation", deadline.DeadlineType);
        Assert.Equal(new DateTime(2026, 9, 1), deadline.DueAt);
        Assert.Equal(document.Id, deadline.ClientCaseDocumentId);
    }

    [Fact]
    public async Task RunCourtSync_ConvertsSudrfTimedHearingsFromUserTimeZone()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-sync-samara@example.com", "Владелец");
        var client = new Client
        {
            Name = "Чудайкин Алексей",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Чудайкин - С/З",
            CourtUrl = "https://samara--sam.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var sourceLocalHearingAt = new DateTime(2026, 6, 5, 16, 0, 0, DateTimeKind.Unspecified);
        var expectedUtcHearingAt = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);
        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Самарский областной суд",
            CaseNumber = "33-100/2026",
            JudgeName = "Иванов И.И.",
            Instance = "appeal",
            NextHearingAt = sourceLocalHearingAt,
            NextHearingPlace = "зал 4",
            MovementEvents =
            [
                new()
                {
                    Title = "Судебное заседание",
                    ScheduledAt = sourceLocalHearingAt,
                    Place = "зал 4",
                    SourceSummary = "Судебное заседание; 05.06.2026 16:00; место: зал 4",
                },
            ],
        };
        var controller = CreateController(db, owner, sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData));

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);
        Assert.Equal("external-sync-completed", dto.Status);
        Assert.Equal(expectedUtcHearingAt, dto.NextHearingAt);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(expectedUtcHearingAt, clientCase.NextHearingAt);
        var courtEvent = await db.ClientCaseCourtEvents.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal(expectedUtcHearingAt, courtEvent.ScheduledAt);
        Assert.False(courtEvent.AllDay);

        var calendarResult = await controller.GetCaseCalendarEvents(
            new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Unspecified),
            clientCaseId: null,
            responsibleUserId: null,
            includeTasks: false,
            ct: CancellationToken.None);
        var calendarOk = Assert.IsType<OkObjectResult>(calendarResult.Result);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(calendarOk.Value);
        Assert.Contains(events, item => item.Kind == "court" && item.SourceType == "hearing" && item.StartAt == expectedUtcHearingAt);
        Assert.Contains(events, item => item.Kind == "court" && item.SourceId == courtEvent.Id && item.StartAt == expectedUtcHearingAt);
    }

    [Fact]
    public async Task RunCourtSync_ReusesExistingManualCourtEventAtSameHearingTime()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Тестовый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с ручным заседанием",
            CourtUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=77",
            CourtCaseNumber = "33-77/2026",
            Status = ClientCaseStatus.Open,
        };
        var manualEvent = new ClientCaseCourtEvent
        {
            ClientCase = clientCase,
            EventType = "hearing",
            Title = "Предварительное судебное заседание",
            ScheduledAt = new DateTime(2026, 5, 27, 15, 0, 0),
            AllDay = false,
            CourtCaseNumber = "33-77/2026",
            Courtroom = "зал 12",
            SourceType = "manual",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseCourtEvents.Add(manualEvent);
        await db.SaveChangesAsync();

        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Хабаровский краевой суд",
            CaseNumber = "33-77/2026",
            Instance = "appeal",
            MovementEvents =
            [
                new()
                {
                    Title = "Судебное заседание",
                    ScheduledAt = new DateTime(2026, 5, 27, 15, 0, 0),
                    Place = "зал 12",
                    SourceSummary = "Судебное заседание; 27.05.2026 15:00; место: зал 12",
                    Result = "Назначено",
                },
            ],
        };
        var controller = CreateController(db, owner, sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData));

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);
        Assert.Equal("external-sync-completed", dto.Status);
        Assert.Equal(0, dto.CreatedCourtEvents);
        Assert.Equal(1, dto.UpdatedCourtEvents);

        var events = await db.ClientCaseCourtEvents
            .Where(item => item.ClientCaseId == clientCase.Id)
            .ToListAsync();
        var courtEvent = Assert.Single(events);
        Assert.Equal(manualEvent.Id, courtEvent.Id);
        Assert.Equal("sudrf", courtEvent.SourceType);
        Assert.Equal("Предварительное судебное заседание", courtEvent.Title);
        Assert.Equal("Хабаровский краевой суд", courtEvent.CourtName);
        Assert.Equal("зал 12", courtEvent.Courtroom);

        var history = await db.ClientCaseCourtEventHistory.SingleAsync(item => item.ClientCaseCourtEventId == courtEvent.Id);
        Assert.Equal("updated", history.Action);
    }

    [Fact]
    public async Task CourtSyncLogs_ReturnManualCheckHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - суд",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            EventHistory = """
                18.05.2026 10:30: Ручная проверка судебного источника зафиксирована. Инициатор: Владелец.
                18.05.2026 10:35: Комментарий по делу: обычная история.
                """,
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetCourtSyncLogs(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var logs = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncLogDto>>(ok.Value);
        var log = Assert.Single(logs);

        Assert.Equal("manual-check-recorded", log.Status);
        Assert.Equal("Владелец", log.ActorName);
        Assert.Contains("Ручная проверка судебного источника зафиксирована", log.Message, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 5, 18, 6, 30, 0, DateTimeKind.Utc), log.OccurredAt);
    }

    [Fact]
    public async Task CourtSyncJournal_ReturnsVisibleLogsAcrossCases()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var visibleClient = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var visibleCase = new ClientCase
        {
            Client = visibleClient,
            Title = "Земцов - суд",
            CaseNumber = "CRM-1",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            CourtUrl = "https://example.sudrf.ru/case",
            EventHistory = """
                18.05.2026 10:30: Ручная проверка судебного источника зафиксирована. Инициатор: Владелец.
                18.05.2026 10:40: Проверка судебного источника не выполнена: источник временно недоступен. Инициатор: Владелец.
                18.05.2026 10:45: Комментарий по делу: обычная история.
                """,
            Status = ClientCaseStatus.Open,
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытый суд",
            CourtCaseNumber = "А40-999/2026",
            EventHistory = "18.05.2026 10:50: Ручная проверка судебного источника зафиксирована. Инициатор: Другой.",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetCourtSyncJournal(take: 50, status: null, search: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var logs = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncJournalEntryDto>>(ok.Value);

        Assert.Equal(2, logs.Count);
        Assert.All(logs, item => Assert.Equal(visibleCase.Id, item.ClientCaseId));
        Assert.Contains(logs, item => item.Status == "manual-check-recorded");
        Assert.Contains(logs, item => item.Status == "external-sync-error");
        Assert.All(logs, item => Assert.Equal("Земцов - суд", item.ClientCaseTitle));
        Assert.All(logs, item => Assert.Equal("Земцов Виктор Александрович", item.OwnerName));
    }

    [Fact]
    public async Task CourtSyncJournal_FiltersByStatusAndSearch()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = manager.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - суд",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            EventHistory = """
                18.05.2026 10:30: Ручная проверка судебного источника зафиксирована. Инициатор: Руководитель.
                18.05.2026 10:40: Проверка судебного источника не выполнена: источник временно недоступен. Инициатор: Руководитель.
                """,
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, manager);

        var result = await controller.GetCourtSyncJournal(
            take: 50,
            status: "external-sync-error",
            search: "временно недоступен",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var log = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncJournalEntryDto>>(ok.Value));

        Assert.Equal("external-sync-error", log.Status);
        Assert.Equal("А55-12345/2026", log.CourtCaseNumber);
        Assert.Contains("временно недоступен", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CourtSyncSettings_ReturnsDefaultsAndPersistsOverrides()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegalIntegrations:CourtSyncScheduler:Enabled"] = "true",
                ["LegalIntegrations:CourtSyncScheduler:IntervalMinutes"] = "90",
                ["LegalIntegrations:CourtSyncScheduler:BatchSize"] = "12",
            })
            .Build();
        var controller = CreateController(db, manager, configuration: configuration);

        var initialResult = await controller.GetCourtSyncSettings(CancellationToken.None);
        var initialOk = Assert.IsType<OkObjectResult>(initialResult.Result);
        var initial = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncSettingsDto>(initialOk.Value);
        Assert.True(initial.IsEnabled);
        Assert.Equal(90, initial.IntervalMinutes);
        Assert.Equal(12, initial.BatchSize);

        var updateResult = await controller.UpdateCourtSyncSettings(new CasesV2Controller.UpdateClientCaseCourtSyncSettingsRequest
        {
            IsEnabled = false,
            IntervalMinutes = 5,
            BatchSize = 250,
        }, CancellationToken.None);

        var updateOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updated = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncSettingsDto>(updateOk.Value);
        Assert.False(updated.IsEnabled);
        Assert.Equal(ClientCaseCourtSyncSchedulerService.MinIntervalMinutes, updated.IntervalMinutes);
        Assert.Equal(ClientCaseCourtSyncSchedulerService.MaxBatchSize, updated.BatchSize);

        var saved = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(item => item.Key, item => item.Value);
        Assert.Equal("false", saved[ClientCaseCourtSyncSchedulerService.EnabledSettingKey]);
        Assert.Equal(ClientCaseCourtSyncSchedulerService.MinIntervalMinutes.ToString(CultureInfo.InvariantCulture), saved[ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey]);
        Assert.Equal(ClientCaseCourtSyncSchedulerService.MaxBatchSize.ToString(CultureInfo.InvariantCulture), saved[ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey]);
    }

    [Fact]
    public async Task CourtSyncProviders_ReturnsSupportedAndPlannedSources()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "manager@example.com", "Руководитель", DataVisibilityScope.All);
        var controller = CreateController(db, manager);

        var result = await controller.GetCourtSyncProviders(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var providers = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncProviderDto>>(ok.Value);

        var sudrf = Assert.Single(providers, item => item.Key == "sudrf");
        Assert.True(sudrf.IsAvailable);
        Assert.True(sudrf.SupportsManualRun);
        Assert.Contains("sudrf.ru", sudrf.SupportedHosts);
        Assert.Contains(providers, item => item.Key == "kadArbitr" && item.Status == "planned");
    }

    [Fact]
    public async Task RunCourtSync_ExplainsMissingCourtBinding()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - без суда",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);

        Assert.Equal("binding-required", dto.Status);
        Assert.False(dto.HasCourtBinding);
        Assert.Contains("не указан номер дела в суде", dto.Message, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Ручная проверка судебного источника не выполнена", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelatedParties_AddListsAndDeletesDirectoryLink()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Основной клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var relatedClient = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
            Phone = "89990000000",
            Email = "zemcov@example.com",
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(client, relatedClient);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createdResult = await controller.AddRelatedParty(clientCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = relatedClient.Id,
            Role = "Свидетель",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createdResult.Result);
        var createdDto = Assert.IsType<ClientCaseRelatedPartyDto>(created.Value);
        Assert.Equal("client", createdDto.SourceType);
        Assert.Contains(await db.ObjectAuditEvents.AsNoTracking().ToListAsync(), auditEvent =>
            auditEvent.ObjectType == "client-case-related-party" &&
            auditEvent.ObjectId == createdDto.Id &&
            auditEvent.Action == "Created");
        Assert.Equal(relatedClient.Id, createdDto.SourceId);
        Assert.Equal("Земцов Виктор Александрович", createdDto.DisplayName);
        Assert.Equal("Свидетель", createdDto.Role);

        var listResult = await controller.GetRelatedParties(clientCase.Id, false, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(listResult.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(ok.Value);
        var item = Assert.Single(items);
        Assert.Equal(createdDto.Id, item.Id);

        var deleteResult = await controller.DeleteRelatedParty(clientCase.Id, createdDto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);
        var archivedParty = Assert.Single(await db.ClientCaseRelatedParties.AsNoTracking().ToListAsync());
        Assert.True(archivedParty.IsArchived);
        Assert.NotNull(archivedParty.ArchivedAt);
        Assert.Equal(owner.Id, archivedParty.ArchivedByUserId);
        Assert.Equal("Archived from case card", archivedParty.ArchiveReason);

        var activeListResult = await controller.GetRelatedParties(clientCase.Id, false, CancellationToken.None);
        var activeListOk = Assert.IsType<OkObjectResult>(activeListResult.Result);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(activeListOk.Value));

        var archivedListResult = await controller.GetRelatedParties(clientCase.Id, true, CancellationToken.None);
        var archivedListOk = Assert.IsType<OkObjectResult>(archivedListResult.Result);
        var archivedItems = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(archivedListOk.Value);
        var archivedDto = Assert.Single(archivedItems);
        Assert.True(archivedDto.IsArchived);
        var archivedAuditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .OrderByDescending(auditEvent => auditEvent.Id)
            .FirstAsync(auditEvent =>
                auditEvent.ObjectType == "client-case-related-party" &&
                auditEvent.ObjectId == createdDto.Id &&
                auditEvent.Action == "Updated");
        Assert.NotNull(archivedAuditEvent.MetadataJson);
        Assert.Contains("IsArchived", archivedAuditEvent.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("ArchiveReason", archivedAuditEvent.MetadataJson, StringComparison.Ordinal);
        using var archivedAuditMetadata = JsonDocument.Parse(archivedAuditEvent.MetadataJson!);
        Assert.Contains("Связанное лицо архивировано", archivedAuditMetadata.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);

        var restoredResult = await controller.AddRelatedParty(clientCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = relatedClient.Id,
            Role = "Представитель",
            Notes = "Повторно добавлен в дело",
        }, CancellationToken.None);

        var restoredOk = Assert.IsType<OkObjectResult>(restoredResult.Result);
        var restoredDto = Assert.IsType<ClientCaseRelatedPartyDto>(restoredOk.Value);
        Assert.Equal(createdDto.Id, restoredDto.Id);
        Assert.False(restoredDto.IsArchived);
        Assert.Equal("Представитель", restoredDto.Role);
        Assert.Equal("Повторно добавлен в дело", restoredDto.Notes);
        var restoredParty = Assert.Single(await db.ClientCaseRelatedParties.AsNoTracking().ToListAsync());
        Assert.False(restoredParty.IsArchived);

        var restoredAuditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .OrderByDescending(auditEvent => auditEvent.Id)
            .FirstAsync(auditEvent =>
                auditEvent.ObjectType == "client-case-related-party" &&
                auditEvent.ObjectId == createdDto.Id &&
                auditEvent.Action == "Updated");
        Assert.NotNull(restoredAuditEvent.MetadataJson);
        Assert.Contains("IsArchived", restoredAuditEvent.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("Role", restoredAuditEvent.MetadataJson, StringComparison.Ordinal);
        using var restoredAuditMetadata = JsonDocument.Parse(restoredAuditEvent.MetadataJson!);
        Assert.Contains("восстановлено", restoredAuditMetadata.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Связь с делом добавлена: Земцов Виктор Александрович (Свидетель).", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Связь с делом удалена: Земцов Виктор Александрович.", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelatedParties_AutoAddsVisibleOrganizationForLinkedPerson()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var organization = new LegalEntity
        {
            ShortName = "Нотариальная палата",
            FullName = "Нотариальная палата Самарской области",
            Director = "Иванов Иван Иванович",
            ResponsibleUserId = owner.Id,
        };
        var client = new Client
        {
            Name = "Основной клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var relatedClient = new Client
        {
            Name = "Петров Петр Петрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
            LegalEntity = organization,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(client, relatedClient);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        await controller.AddRelatedParty(clientCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = relatedClient.Id,
            Role = "Нотариус",
        }, CancellationToken.None);

        var listResult = await controller.GetRelatedParties(clientCase.Id, false, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(listResult.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(ok.Value);
        Assert.Contains(items, item => item.SourceType == "client" && item.DisplayName == "Петров Петр Петрович");
        var organizationItem = Assert.Single(items, item => item.SourceType == "legalEntity");
        Assert.Equal(organization.Id, organizationItem.SourceId);
        Assert.Equal("Нотариальная палата", organizationItem.DisplayName);
        Assert.Equal("Организация участника", organizationItem.Role);
        Assert.Contains("Руководитель: Иванов Иван Иванович", organizationItem.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelatedParties_ForbidsAddWithoutEditPermission()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель");
        var permission = await db.RolePermissions.FirstAsync(item => item.RoleId == viewer.RoleId && item.Section == RolePermissionCatalog.Cases);
        permission.CanEdit = false;
        var client = new Client
        {
            Name = "Основной клиент",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var relatedClient = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(client, relatedClient);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.AddRelatedParty(clientCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = relatedClient.Id,
            Role = "Свидетель",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.ClientCaseRelatedParties.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WorkOrder_CreatesUpdatesAndDeletesServiceItems()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var executor = await CreateUserAsync(db, "executor@example.com", "Исполнитель");
        var client = new Client
        {
            Name = "Клиент заказ-наряда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с заказ-нарядом",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createResult = await controller.CreateWorkOrder(clientCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderRequest
        {
            Title = "Предварительный расчет банкротства",
            Status = "draft",
            Currency = "rub",
            Notes = "Первичная оценка",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(created.Value);
        Assert.Equal(clientCase.Id, createdDto.ClientCaseId);
        Assert.Equal("RUB", createdDto.Currency);
        Assert.Empty(createdDto.Items);

        var addResult = await controller.AddWorkOrderItem(clientCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderItemRequest
        {
            ServiceName = "Подготовка заявления",
            Description = "Документы первой очереди",
            ExecutorUserId = executor.Id,
            DueDate = new DateTime(2026, 06, 01),
            Quantity = 2,
            UnitPrice = 15000,
        }, CancellationToken.None);

        var addOk = Assert.IsType<OkObjectResult>(addResult.Result);
        var addedDto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(addOk.Value);
        var item = Assert.Single(addedDto.Items);
        Assert.Equal(executor.Id, item.ExecutorUserId);
        Assert.Equal("Исполнитель", item.ExecutorName);
        Assert.Equal(30000, item.TotalAmount);
        Assert.Equal(30000, addedDto.TotalAmount);

        var updateResult = await controller.UpdateWorkOrderItem(clientCase.Id, item.Id, new CasesV2Controller.UpsertClientCaseWorkOrderItemRequest
        {
            ServiceName = "Подготовка заявления и приложений",
            Quantity = 1,
            UnitPrice = 22000,
        }, CancellationToken.None);

        var updateOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedDto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(updateOk.Value);
        Assert.Equal(22000, updatedDto.TotalAmount);
        Assert.Equal("Подготовка заявления и приложений", Assert.Single(updatedDto.Items).ServiceName);

        var deleteResult = await controller.DeleteWorkOrderItem(clientCase.Id, item.Id, CancellationToken.None);

        var deleteOk = Assert.IsType<OkObjectResult>(deleteResult.Result);
        var emptyDto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(deleteOk.Value);
        Assert.Empty(emptyDto.Items);
        Assert.Equal(0, emptyDto.TotalAmount);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Предварительный", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Подготовка заявления", clientCase.EventHistory, StringComparison.Ordinal);

        var auditEvents = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(auditEvent =>
                auditEvent.ObjectType == "client-case-work-order" ||
                auditEvent.ObjectType == "client-case-work-order-item")
            .ToListAsync();

        static bool AuditReasonContains(ObjectAuditEvent auditEvent, string expected)
        {
            if (string.IsNullOrWhiteSpace(auditEvent.MetadataJson))
            {
                return false;
            }

            using var metadata = JsonDocument.Parse(auditEvent.MetadataJson);
            return metadata.RootElement.TryGetProperty("reason", out var reason)
                && reason.GetString()?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
        }

        Assert.Contains(auditEvents, auditEvent =>
            auditEvent.ObjectType == "client-case-work-order" &&
            auditEvent.ObjectId == createdDto.Id &&
            auditEvent.Action == "Created");
        Assert.Contains(auditEvents, auditEvent =>
            auditEvent.ObjectType == "client-case-work-order" &&
            auditEvent.ObjectId == createdDto.Id &&
            auditEvent.Action == "Updated" &&
            AuditReasonContains(auditEvent, "добавлена услуга"));
        Assert.Contains(auditEvents, auditEvent =>
            auditEvent.ObjectType == "client-case-work-order-item" &&
            auditEvent.ObjectId == item.Id &&
            auditEvent.Action == "Created");
        Assert.Contains(auditEvents, auditEvent =>
            auditEvent.ObjectType == "client-case-work-order-item" &&
            auditEvent.ObjectId == item.Id &&
            auditEvent.Action == "Updated" &&
            auditEvent.MetadataJson?.Contains("ServiceName", StringComparison.Ordinal) == true);
        Assert.Contains(auditEvents, auditEvent =>
            auditEvent.ObjectType == "client-case-work-order-item" &&
            auditEvent.ObjectId == item.Id &&
            auditEvent.Action == "Deleted");
    }

    [Fact]
    public async Task WorkOrder_ForbidsUserWithoutEditPermission()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель");
        viewer.Role!.Permissions.Single().CanEdit = false;
        await db.SaveChangesAsync();
        var client = new Client
        {
            Name = "Клиент без прав редактирования",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело без прав редактирования",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = viewer.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.CreateWorkOrder(clientCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderRequest
        {
            Title = "Недоступный расчет",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.ClientCaseWorkOrders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task WorkOrder_CreatesInvoiceFromServices()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var executor = await CreateUserAsync(db, "executor@example.com", "Исполнитель");
        var client = new Client
        {
            Name = "Клиент счета",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для счета",
            CaseNumber = "CASE-77",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Расчет услуг",
            Status = "approved",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка заявления",
                    ExecutorUserId = executor.Id,
                    ExecutorName = executor.FullName,
                    Quantity = 2,
                    UnitPrice = 10000,
                    TotalAmount = 20000,
                    SortOrder = 1,
                },
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Сопровождение заседания",
                    Quantity = 1,
                    UnitPrice = 15000,
                    TotalAmount = 15000,
                    SortOrder = 2,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.CreateInvoiceFromWorkOrder(clientCase.Id, new CasesV2Controller.CreateClientCaseWorkOrderInvoiceRequest
        {
            Number = "WO-TEST-77",
            Date = new DateTime(2026, 06, 02),
            DueDate = new DateTime(2026, 06, 12),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderInvoiceDto>(created.Value);
        Assert.Equal("WO-TEST-77", dto.Number);
        Assert.Equal(35000, dto.Amount);
        Assert.Equal("invoice_created", dto.WorkOrder.Status);
        Assert.NotNull(dto.DocumentId);

        var payment = await db.Payments
            .Include(item => item.Invoice)
            .Include(item => item.InvoiceLineItems)
            .Include(item => item.InvoiceClientCases)
            .Include(item => item.Responsibles)
            .SingleAsync(item => item.Id == dto.PaymentId);

        Assert.Equal(DocumentKind.Invoice, payment.DocumentKind);
        Assert.Equal(PaymentType.Income, payment.Type);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, payment.InvoiceWorkflowStatus);
        Assert.Equal(client.Id, payment.ClientId);
        Assert.Equal(clientCase.Id, payment.ClientCaseId);
        Assert.Equal("CASE-77", payment.ClientCaseReference);
        Assert.Equal(2, payment.InvoiceLineItems.Count);
        Assert.Contains(payment.InvoiceLineItems, item => item.Name == "Подготовка заявления" && item.Quantity == 2 && item.Price == 10000);
        Assert.Contains(payment.InvoiceClientCases, item => item.ClientCaseId == clientCase.Id);
        Assert.Contains(payment.Responsibles, item => item.UserId == executor.Id);

        var invoiceDocument = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.DocumentId);
        Assert.Equal($"invoice-{payment.Id}", invoiceDocument.DocumentKey);
        Assert.Equal("Счет", invoiceDocument.DocumentType);
        Assert.Equal("text/html; charset=utf-8", invoiceDocument.ContentType);

        await db.Entry(workOrder).ReloadAsync();
        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("invoice_created", workOrder.Status);
        Assert.Contains("создан счет", clientCase.EventHistory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkOrder_CreateInvoiceDefaultsDateToUserTimeZone()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "work-order-timezone@example.com", "Work Order Owner");
        var nowUtc = DateTime.UtcNow;
        var selectedZone = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new
            {
                Zone = zone,
                LocalToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, zone).Date,
            })
            .FirstOrDefault(item => item.LocalToday != nowUtc.Date);
        Assert.NotNull(selectedZone);

        var client = new Client
        {
            Name = "Invoice timezone client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Invoice timezone case",
            CaseNumber = "TZ-INV",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Timezone services",
            Status = "approved",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Timezone service",
                    Quantity = 1,
                    UnitPrice = 5000,
                    TotalAmount = 5000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone!.Zone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.CreateInvoiceFromWorkOrder(
            clientCase.Id,
            new CasesV2Controller.CreateClientCaseWorkOrderInvoiceRequest(),
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderInvoiceDto>(created.Value);
        Assert.Equal(selectedZone.LocalToday, dto.Date);
        Assert.Equal(selectedZone.LocalToday, dto.DueDate);

        var payment = await db.Payments.Include(item => item.Invoice).SingleAsync(item => item.Id == dto.PaymentId);
        Assert.Equal(selectedZone.LocalToday, payment.Date);
        Assert.Equal(selectedZone.LocalToday, payment.PlannedDate);
        Assert.Equal(selectedZone.LocalToday, payment.AccountDate);
        Assert.NotNull(payment.Invoice);
        Assert.Equal(selectedZone.LocalToday, payment.Invoice!.Date);
    }

    [Fact]
    public async Task WorkOrder_PrintReturnsHtmlForVisibleCase()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var nowUtc = DateTime.UtcNow;
        var selectedZone = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new
            {
                Zone = zone,
                LocalNow = CalendarTimeZoneHelper.ToUserLocal(nowUtc, zone),
            })
            .FirstOrDefault(item => item.LocalNow.Date != nowUtc.Date);
        Assert.NotNull(selectedZone);
        var executor = await CreateUserAsync(db, "executor@example.com", "Исполнитель");
        var client = new Client
        {
            Name = "Клиент печатной формы",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для печати",
            CaseNumber = "PRINT-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Предварительный расчет",
            Status = "approved",
            Currency = "RUB",
            Notes = "Печатная версия для согласования",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка заявления",
                    Description = "Пакет документов",
                    ExecutorUserId = executor.Id,
                    ExecutorName = executor.FullName,
                    DueDate = new DateTime(2026, 06, 01),
                    Quantity = 2,
                    UnitPrice = 12000,
                    TotalAmount = 24000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone!.Zone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.PrintWorkOrder(clientCase.Id, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("<!doctype html>", content.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Заказ-наряд", content.Content, StringComparison.Ordinal);
        Assert.Contains("PRINT-1", content.Content, StringComparison.Ordinal);
        Assert.Contains("Подготовка заявления", content.Content, StringComparison.Ordinal);
        Assert.Contains("24", content.Content, StringComparison.Ordinal);
        Assert.Contains(selectedZone.LocalNow.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")), content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkOrder_GeneratesLinkedCaseDocument()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент документа заказ-наряда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для документа заказ-наряда",
            CaseNumber = "WO-DOC-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Заказ-наряд для документов",
            Status = "approved",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка позиции",
                    Quantity = 1,
                    UnitPrice = 18000,
                    TotalAmount = 18000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GenerateWorkOrderDocument(clientCase.Id, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);
        Assert.Equal($"work-order-{workOrder.Id}", dto.DocumentKey);
        Assert.Equal(workOrder.Id, dto.SourceWorkOrderId);
        Assert.Equal("Заказ-наряд", dto.DocumentType);
        Assert.Equal("Согласован", dto.Status);
        Assert.Equal("text/html; charset=utf-8", dto.ContentType);

        var document = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.Id);
        Assert.Equal($"work-order-{workOrder.Id}", document.DocumentKey);
        Assert.Contains("work-order", document.StoredFileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(document.SizeBytes > 0);
    }

    [Fact]
    public async Task WorkOrder_ApprovalWorkflowSubmitsApprovesAndBlocksDraftInvoice()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент согласования заказ-наряда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для согласования заказ-наряда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Расчет услуг",
            Status = "draft",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка заявления",
                    Quantity = 1,
                    UnitPrice = 10000,
                    TotalAmount = 10000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var draftInvoiceResult = await controller.CreateInvoiceFromWorkOrder(clientCase.Id, new CasesV2Controller.CreateClientCaseWorkOrderInvoiceRequest(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(draftInvoiceResult.Result);

        var submitResult = await controller.SubmitWorkOrderApproval(clientCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "Проверьте расчет",
        }, CancellationToken.None);

        var submitOk = Assert.IsType<OkObjectResult>(submitResult.Result);
        var submitted = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(submitOk.Value);
        Assert.Equal("pending_approval", submitted.Status);
        Assert.Equal(owner.Id, submitted.ApprovalRequestedByUserId);
        Assert.Equal("Проверьте расчет", submitted.ApprovalComment);

        var approveResult = await controller.ApproveWorkOrder(clientCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "Согласовано",
        }, CancellationToken.None);

        var approveOk = Assert.IsType<OkObjectResult>(approveResult.Result);
        var approved = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(approveOk.Value);
        Assert.Equal("approved", approved.Status);
        Assert.Equal(owner.Id, approved.ApprovalDecisionByUserId);
        Assert.Equal("Согласовано", approved.ApprovalComment);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("отправлен на согласование", clientCase.EventHistory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("согласован", clientCase.EventHistory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkOrder_BlocksManualApprovalAndEditingLockedStatuses()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент блокировки заказ-наряда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var draftCase = new ClientCase
        {
            Client = client,
            Title = "Дело для ручного статуса заказ-наряда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var approvedCase = new ClientCase
        {
            Client = client,
            Title = "Дело для блокировки заказ-наряда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var draftWorkOrder = new ClientCaseWorkOrder
        {
            ClientCase = draftCase,
            Title = "Черновой расчет",
            Status = "draft",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
        };
        var approvedWorkOrder = new ClientCaseWorkOrder
        {
            ClientCase = approvedCase,
            Title = "Согласованный расчет",
            Status = "approved",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка заявления",
                    Quantity = 1,
                    UnitPrice = 10000,
                    TotalAmount = 10000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(draftCase, approvedCase);
        db.ClientCaseWorkOrders.AddRange(draftWorkOrder, approvedWorkOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var manualApproval = await controller.UpdateWorkOrder(draftCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderRequest
        {
            Status = "approved",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(manualApproval.Result);

        var updateHeader = await controller.UpdateWorkOrder(approvedCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderRequest
        {
            Title = "Исправленный расчет",
        }, CancellationToken.None);
        var addItem = await controller.AddWorkOrderItem(approvedCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderItemRequest
        {
            ServiceName = "Новая услуга",
            UnitPrice = 5000,
        }, CancellationToken.None);
        var updateItem = await controller.UpdateWorkOrderItem(approvedCase.Id, approvedWorkOrder.Items.Single().Id, new CasesV2Controller.UpsertClientCaseWorkOrderItemRequest
        {
            ServiceName = "Измененная услуга",
            UnitPrice = 12000,
        }, CancellationToken.None);
        var deleteItem = await controller.DeleteWorkOrderItem(approvedCase.Id, approvedWorkOrder.Items.Single().Id, CancellationToken.None);
        var deleteHeader = await controller.DeleteWorkOrder(approvedCase.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(updateHeader.Result);
        Assert.IsType<BadRequestObjectResult>(addItem.Result);
        Assert.IsType<BadRequestObjectResult>(updateItem.Result);
        Assert.IsType<BadRequestObjectResult>(deleteItem.Result);
        Assert.IsType<BadRequestObjectResult>(deleteHeader);

        var reloaded = await db.ClientCaseWorkOrders
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == approvedWorkOrder.Id);
        Assert.Equal("approved", reloaded.Status);
        Assert.Equal("Согласованный расчет", reloaded.Title);
        var item = Assert.Single(reloaded.Items);
        Assert.Equal("Подготовка заявления", item.ServiceName);
        Assert.Equal(10000, item.TotalAmount);
    }

    [Fact]
    public async Task WorkOrder_RevisionReturnsPendingApprovalToRework()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент доработки заказ-наряда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для доработки заказ-наряда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Расчет услуг",
            Status = "pending_approval",
            Currency = "RUB",
            ApprovalRequestedAt = DateTime.UtcNow,
            ApprovalRequestedByUserId = owner.Id,
            ApprovalRequestedByUserName = owner.FullName,
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка заявления",
                    Quantity = 1,
                    UnitPrice = 10000,
                    TotalAmount = 10000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.RequestWorkOrderRevision(clientCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "Уточнить сроки",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(ok.Value);
        Assert.Equal("revision_required", dto.Status);
        Assert.Equal("Уточнить сроки", dto.ApprovalComment);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("возвращен на доработку", clientCase.EventHistory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkOrder_ApprovedCanReturnToRevisionButInvoiceCreatedCannot()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент возврата заказ-наряда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var approvedCase = new ClientCase
        {
            Client = client,
            Title = "Дело с согласованным заказ-нарядом",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var invoicedCase = new ClientCase
        {
            Client = client,
            Title = "Дело с выставленным счетом",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(approvedCase, invoicedCase);
        db.ClientCaseWorkOrders.AddRange(
            new ClientCaseWorkOrder
            {
                ClientCase = approvedCase,
                Title = "Согласованный расчет",
                Status = "approved",
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            },
            new ClientCaseWorkOrder
            {
                ClientCase = invoicedCase,
                Title = "Расчет со счетом",
                Status = "invoice_created",
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var approvedResult = await controller.RequestWorkOrderRevision(approvedCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "Клиент попросил пересчитать",
        }, CancellationToken.None);

        var approvedOk = Assert.IsType<OkObjectResult>(approvedResult.Result);
        var approvedDto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderDto>(approvedOk.Value);
        Assert.Equal("revision_required", approvedDto.Status);
        Assert.Equal("Клиент попросил пересчитать", approvedDto.ApprovalComment);

        var invoicedResult = await controller.RequestWorkOrderRevision(invoicedCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "Поздний возврат",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(invoicedResult.Result);

        var invoicedWorkOrder = await db.ClientCaseWorkOrders.SingleAsync(item => item.ClientCaseId == invoicedCase.Id);
        Assert.Equal("invoice_created", invoicedWorkOrder.Status);
    }

    [Fact]
    public async Task Conversation_AddsInternalMessageAndReturnsOnlyConversationEntries()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            EventHistory = "18.05.2026 09:00: Статус дела изменен."
                + Environment.NewLine
                + "18.05.2026 09:10: Комментарий по делу: Проверить документы. Автор: Администратор.",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var addResult = await controller.AddConversationEntry(clientCase.Id, new CasesV2Controller.AddClientCaseConversationEntryRequest
        {
            Text = "Коллеги, нужно уточнить срок подачи.\nОтветственный - юрист.",
        }, CancellationToken.None);

        var addOk = Assert.IsType<OkObjectResult>(addResult.Result);
        var addedItems = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseConversationEntryDto>>(addOk.Value);
        Assert.Equal(2, addedItems.Count);
        Assert.Contains(addedItems, item => item.Kind == "comment" && item.Text == "Проверить документы");
        var newMessage = Assert.Single(addedItems, item => item.Kind == "message");
        Assert.Equal("Коллеги, нужно уточнить срок подачи. Ответственный - юрист", newMessage.Text);
        Assert.Equal("Владелец", newMessage.AuthorName);

        var getResult = await controller.GetConversation(clientCase.Id, CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        var getItems = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseConversationEntryDto>>(getOk.Value);
        Assert.Equal(2, getItems.Count);
        Assert.DoesNotContain(getItems, item => item.Text.Contains("Статус дела", StringComparison.Ordinal));
        var existingComment = Assert.Single(getItems, item => item.Kind == "comment");
        Assert.Equal(new DateTime(2026, 5, 18, 5, 10, 0, DateTimeKind.Utc), existingComment.OccurredAt);
    }

    private static IReadOnlyList<ClientCase> GetAnonymousItems(object? value)
    {
        Assert.NotNull(value);
        var property = value.GetType().GetProperty("items");
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<IReadOnlyList<ClientCase>>(property.GetValue(value));
    }

    private static int GetAnonymousInt(object? value, string propertyName)
    {
        Assert.NotNull(value);
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<int>(property.GetValue(value));
    }

    private static CasesV2Controller CreateController(
        PaymentContext db,
        User user,
        string? contentRootPath = null,
        ISudrfCourtSyncService? sudrfCourtSyncService = null,
        IInvoiceDocumentPdfService? pdfService = null,
        ICourtSyncProviderRegistry? courtSyncProviderRegistry = null,
        IConfiguration? configuration = null,
        IOptionsMonitor<AppFeatureFlagsOptions>? featureFlags = null)
        => new(db, new TestWebHostEnvironment
        {
            ContentRootPath = contentRootPath ?? Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N")),
        }, sudrfCourtSyncService: sudrfCourtSyncService, pdfService: pdfService, courtSyncProviderRegistry: courtSyncProviderRegistry, configuration: configuration, featureFlags: featureFlags)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new Claim(ClaimTypes.Name, user.FullName),
                        new Claim(ClaimTypes.Email, user.Email),
                    ], "TestAuth")),
                },
            },
        };

    private sealed class FakeInvoiceDocumentPdfService(string content) : IInvoiceDocumentPdfService
    {
        public string LastHtml { get; private set; } = string.Empty;

        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            LastHtml = html;
            return Task.FromResult(Encoding.UTF8.GetBytes(content));
        }
    }

    private static IFormFile CreateFormFile(string content, string fileName, string contentType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
    }

    private sealed class FixedSudrfCourtSyncService(SudrfCourtCaseData data) : ISudrfCourtSyncService
    {
        public bool CanHandle(string? sourceUrl)
            => sourceUrl?.Contains("sudrf.ru", StringComparison.OrdinalIgnoreCase) == true;

        public Task<SudrfCourtCaseData> FetchCaseAsync(string sourceUrl, CancellationToken ct, string? courtCaseNumber = null)
            => Task.FromResult(data);
    }

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string email,
        string fullName,
        string viewScope = DataVisibilityScope.Own)
    {
        var role = new Role
        {
            Name = $"case-v2-{Guid.NewGuid():N}",
            Description = "Пользователь дел",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.Cases,
                    CanView = true,
                    CanCreate = true,
                    CanEdit = true,
                    CanDelete = true,
                    CanExport = true,
                    ViewScope = viewScope,
                },
            ],
        };
        var user = new User
        {
            Email = email,
            FullName = fullName,
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
