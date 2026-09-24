using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task ResolveCaseNotificationRecipientIdsAsync_UsesFixedQueryCountForSeveralRecipients()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var department = new DepartmentDictionaryItem { Name = "Судебный отдел", IsActive = true };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        var caseResponsible = await CreateUserAsync(
            db,
            "case-notification-responsible@example.com",
            "Ответственный по делу",
            DataVisibilityScope.Department);
        var clientResponsible = await CreateUserAsync(
            db,
            "case-notification-client@example.com",
            "Ответственный по клиенту",
            DataVisibilityScope.Department);
        foreach (var user in new[] { caseResponsible, clientResponsible })
        {
            user.DepartmentId = department.Id;
            user.Department = department.Name;
        }

        var client = new Client
        {
            Name = "Клиент пакетной проверки",
            IsActive = true,
            ResponsibleUserId = clientResponsible.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело пакетной проверки получателей",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = caseResponsible.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, caseResponsible);
        interceptor.Commands.Clear();

        var method = typeof(CasesV2Controller).GetMethod(
            "ResolveCaseNotificationRecipientIdsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var invocation = method.Invoke(controller, new object[] { clientCase.Id, CancellationToken.None });
        var recipientIds = await Assert.IsAssignableFrom<Task<IReadOnlyCollection<int>>>(invocation);

        Assert.Equal(
            new[] { caseResponsible.Id, clientResponsible.Id }.OrderBy(id => id),
            recipientIds.OrderBy(id => id));
        Assert.Equal(4, interceptor.Commands.Count);
    }

    [Fact]
    public async Task CaseReadEndpoints_UseBoundedQueriesAndCompactHistoryProjection()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var owner = await CreateUserAsync(
            db,
            "case-read-query-budget@example.com",
            "Владелец карточки",
            DataVisibilityScope.All);
        await GrantSectionPermissionAsync(
            db,
            owner,
            RolePermissionCatalog.Payments,
            viewScope: DataVisibilityScope.All);
        var clientCase = new ClientCase
        {
            Client = new Client
            {
                Name = "Клиент компактной карточки",
                IsActive = true,
                ResponsibleUserId = owner.Id,
            },
            ResponsibleUserId = owner.Id,
            Title = "Дело с ограниченным числом запросов",
            Description = "Полное описание нужно только полной карточке",
            EventHistory = "05.08.2026 10:15: Внутреннее сообщение: Проверить документы. Автор: Владелец карточки.",
            Status = ClientCaseStatus.Open,
            FeeAmount = 12500m,
            CreatedAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            ActivityConfirmedAt = new DateTime(2026, 8, 5, 10, 15, 0, DateTimeKind.Utc),
            CourtParties =
            [
                new ClientCaseCourtParty
                {
                    DisplayName = "ООО Ответчик",
                    Side = "respondent",
                    ProcessRole = "Ответчик",
                },
            ],
        };
        db.ClientCases.Add(clientCase);
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        interceptor.Commands.Clear();
        var details = await CreateController(db, owner).GetById(clientCase.Id, CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(details);
        var detailsItem = Assert.IsType<ClientCase>(detailsOk.Value);
        Assert.Equal(clientCase.ActivityConfirmedAt, detailsItem.LastMeaningfulActivityAt);
        Assert.Equal(clientCase.ActivityConfirmedAt.Value.AddDays(14), detailsItem.NextInactivityControlAt);
        Assert.Empty(detailsItem.InactivityControlExclusionReason);
        Assert.Equal(8, interceptor.Commands.Count);

        interceptor.Commands.Clear();
        var summary = await CreateController(db, owner).GetSummary(clientCase.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(summary.Result);
        Assert.Equal(5, interceptor.Commands.Count);
        Assert.DoesNotContain(
            interceptor.Commands,
            command => command.Contains("Description", StringComparison.OrdinalIgnoreCase));

        interceptor.Commands.Clear();
        var parties = await CreateController(db, owner).GetCourtParties(
            clientCase.Id,
            includeArchived: false,
            CancellationToken.None);
        var partyItems = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtPartyDto>>(
            Assert.IsType<OkObjectResult>(parties.Result).Value);
        Assert.Single(partyItems);
        Assert.Equal(2, interceptor.Commands.Count);

        interceptor.Commands.Clear();
        var timeline = await CreateController(db, owner).GetTimeline(clientCase.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(timeline.Result);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Contains("UserSettings", interceptor.Commands[^1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Description", interceptor.Commands[^1], StringComparison.OrdinalIgnoreCase);

        interceptor.Commands.Clear();
        var conversation = await CreateController(db, owner).GetConversation(clientCase.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(conversation.Result);
        Assert.Equal(2, interceptor.Commands.Count);
    }

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
    public async Task Get_SelectsCompactRelatedProfilesAndKeepsEditableCaseFields()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var manager = await CreateUserAsync(db, "registry-projection-manager@example.com", "Руководитель", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "registry-projection-owner@example.com", "Исполнитель");
        responsible.PasswordHash = "sensitive-password-hash";
        var client = new Client
        {
            Name = "Клиент реестра",
            Address = "Не загружать адрес в реестр",
            Company = "Не загружать компанию в реестр",
            Email = "private-client@example.com",
            IsActive = true,
            ResponsibleUserId = responsible.Id,
        };
        db.ClientCases.Add(new ClientCase
        {
            Client = client,
            Title = "Дело компактного реестра",
            Description = "Описание для быстрого редактирования",
            CaseNumber = "CASE-REGISTRY-PROJECTION",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            Claimant = "Заявитель сохранён",
            EventHistory = "История сохранена",
            Strategy = "Стратегия сохранена",
            CourtUrl = "https://court.example.test/case",
            IsUnderControl = true,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, manager);
        interceptor.Commands.Clear();

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
            pageSize: 25,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.Single(GetAnonymousItems(ok.Value));
        Assert.Equal("Описание для быстрого редактирования", item.Description);
        Assert.Equal("Заявитель сохранён", item.Claimant);
        Assert.Equal("История сохранена", item.EventHistory);
        Assert.Equal("Стратегия сохранена", item.Strategy);
        Assert.Equal("https://court.example.test/case", item.CourtUrl);
        Assert.True(item.IsUnderControl);
        Assert.Equal("Клиент реестра", item.Client?.Name);
        Assert.Equal(string.Empty, item.Client?.Address);
        Assert.Equal("Исполнитель", item.ResponsibleUser?.FullName);
        Assert.Equal(string.Empty, item.ResponsibleUser?.PasswordHash);

        var listCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal)
            && command.Contains("ORDER BY", StringComparison.Ordinal)
            && command.Contains("LIMIT", StringComparison.Ordinal));
        Assert.DoesNotContain("\"PasswordHash\"", listCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Company\"", listCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Inn\"", listCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kpp\"", listCommand, StringComparison.Ordinal);
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
    public async Task Get_UsesConfiguredInactivityPeriodAndAllMeaningfulSignalsWhenFilterThresholdIsOmitted()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var owner = await CreateUserAsync(db, "configured-activity-owner@example.com", "Владелец");
        var now = DateTime.UtcNow;
        var client = new Client
        {
            Name = "Клиент настроенного контроля",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var inactiveCase = new ClientCase
        {
            Client = client,
            Title = "Нет движения больше настроенного срока",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CreatedAt = now.AddDays(-5),
            InactivityRemindersEnabled = true,
        };
        var commentCase = new ClientCase
        {
            Client = client,
            Title = "Недавний комментарий",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CreatedAt = now.AddDays(-10),
            InactivityRemindersEnabled = true,
        };
        var documentCase = new ClientCase
        {
            Client = client,
            Title = "Недавний документ",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CreatedAt = now.AddDays(-10),
            InactivityRemindersEnabled = true,
        };
        var courtEventCase = new ClientCase
        {
            Client = client,
            Title = "Недавнее судебное событие",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CreatedAt = now.AddDays(-10),
            InactivityRemindersEnabled = true,
        };
        var commentTask = new TaskJournalItem
        {
            ClientCase = commentCase,
            ObjectName = "Дело",
            Title = "Старая задача",
            DueAt = now.AddDays(-8),
            CreatedAt = now.AddDays(-9),
            Status = "done",
        };
        commentTask.Comments.Add(new TaskJournalComment
        {
            AuthorName = "Владелец",
            Text = "Новое движение по делу",
            CreatedAt = now.AddDays(-1),
        });
        documentCase.Documents.Add(new ClientCaseDocument
        {
            DocumentKey = "configured-filter-document",
            DocumentType = "Документ",
            Status = "draft",
            OriginalFileName = "document.pdf",
            StoredFileName = "document.pdf",
            StoragePath = "cases/document.pdf",
            UploadedByName = "Владелец",
            UploadedAt = now.AddDays(-1),
        });
        courtEventCase.CourtEvents.Add(new ClientCaseCourtEvent
        {
            Title = "Получено определение",
            ScheduledAt = now.AddDays(5),
            CreatedAt = now.AddDays(-1),
            CreatedByUserName = "Владелец",
        });
        db.Clients.Add(client);
        db.ClientCases.AddRange(inactiveCase, commentCase, documentCase, courtEventCase);
        db.TaskJournalItems.Add(commentTask);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.inactiveDays",
            Value = "3",
        });
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();

        var result = await CreateController(db, owner).Get(
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
            activity: "inactive",
            inactiveDays: null,
            ct: CancellationToken.None);

        var item = Assert.Single(GetAnonymousItems(Assert.IsType<OkObjectResult>(result).Value));
        Assert.Equal(inactiveCase.Id, item.Id);
        Assert.Contains(interceptor.Commands, command =>
            command.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase)
            && command.Contains("TaskJournalComments", StringComparison.Ordinal)
            && command.Contains("ClientCaseDocuments", StringComparison.Ordinal)
            && command.Contains("ClientCaseCourtEvents", StringComparison.Ordinal)
            && command.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase));

        var activeResult = await CreateController(db, owner).Get(
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
            activity: "active",
            inactiveDays: null,
            ct: CancellationToken.None);

        var activeItems = GetAnonymousItems(Assert.IsType<OkObjectResult>(activeResult).Value);
        Assert.Equal(3, activeItems.Count);
        Assert.DoesNotContain(activeItems, activeItem => activeItem.Id == inactiveCase.Id);
    }

    [Fact]
    public async Task UpdateControl_MarksCaseAndScopeReturnsOnlyControlledCases()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-control-owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент с контрольным делом",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var controlledCase = new ClientCase
        {
            Client = client,
            Title = "Дело на контроле",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var regularCase = new ClientCase
        {
            Client = client,
            Title = "Обычное дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(controlledCase, regularCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.UpdateControl(controlledCase.Id, new CasesV2Controller.UpdateClientCaseControlRequest
        {
            IsUnderControl = true,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<ClientCase>(ok.Value);
        Assert.True(dto.IsUnderControl);
        Assert.Equal(owner.Id, dto.ControlMarkedByUserId);
        Assert.Equal("Владелец", dto.ControlMarkedByUserName);

        var scopedResult = await controller.Get(
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
            scope: "control",
            ct: CancellationToken.None);

        var scopedOk = Assert.IsType<OkObjectResult>(scopedResult);
        var scopedItem = Assert.Single(GetAnonymousItems(scopedOk.Value));
        Assert.Equal(controlledCase.Id, scopedItem.Id);
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
    public async Task GetById_AndRelatedParties_HideSensitiveContacts_ForAccountantWithoutCaseEditPermission()
    {
        await using var db = await CreateContextAsync();
        var accountant = await CreateUserAsync(db, "accountant-sensitive@example.com", "Бухгалтер");
        accountant.Role!.Name = "Бухгалтер";
        accountant.Role.Description = "Бухгалтер";
        var casesPermission = accountant.Role.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases);
        casesPermission.CanEdit = false;
        casesPermission.CanDelete = false;
        casesPermission.CanExport = false;
        await GrantSectionPermissionAsync(db, accountant, RolePermissionCatalog.Payments, canDelete: false, canExport: false, viewScope: DataVisibilityScope.Own);
        await GrantSectionPermissionAsync(db, accountant, RolePermissionCatalog.Accounts, canDelete: false, canExport: false, viewScope: DataVisibilityScope.Own);

        var legalEntity = new LegalEntity
        {
            ShortName = "ООО Закрытые данные",
            FullName = "Общество с ограниченной ответственностью Закрытые данные",
            Inn = "6312345678",
            Kpp = "631201001",
            Ogrn = "1236300000000",
            Address = "Самара, Ленина, 1",
            Phone = "+78461234567",
            Email = "secret-company@example.test",
            Director = "Директор Закрытый",
            ResponsibleUserId = accountant.Id,
        };
        var client = new Client
        {
            Name = "Клиент бухгалтера с персональными данными",
            IsActive = true,
            ResponsibleUserId = accountant.Id,
            Phone = "+79991234567",
            Email = "accountant-client@example.test",
            Address = "Самара, Полевая, 10",
            Notes = "Закрытые заметки по клиенту",
            LegalEntity = legalEntity,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с финансовым доступом без раскрытия контактов",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = accountant.Id,
        };
        var legalEntityCase = new ClientCase
        {
            LegalEntity = legalEntity,
            Title = "Дело юрлица с финансовым доступом без раскрытия реквизитов",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = accountant.Id,
        };
        db.ClientCases.AddRange(clientCase, legalEntityCase);
        db.ClientCaseRelatedParties.Add(new ClientCaseRelatedParty
        {
            ClientCase = clientCase,
            SourceType = "legalEntity",
            LegalEntity = legalEntity,
            DisplayName = legalEntity.ShortName,
            Role = "Организация",
            Phone = "+78467654321",
            Email = "related-company@example.test",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, accountant);

        var caseResult = await controller.GetById(clientCase.Id, CancellationToken.None);

        var caseOk = Assert.IsType<OkObjectResult>(caseResult);
        var dto = Assert.IsType<ClientCase>(caseOk.Value);
        Assert.False(dto.CanRevealSensitiveData);
        Assert.Equal(string.Empty, dto.Client!.Phone);
        Assert.Equal(string.Empty, dto.Client.Email);
        Assert.Equal(string.Empty, dto.Client.Address);
        Assert.Equal(string.Empty, dto.Client.Notes);

        var legalEntityCaseResult = await controller.GetById(legalEntityCase.Id, CancellationToken.None);

        var legalEntityCaseOk = Assert.IsType<OkObjectResult>(legalEntityCaseResult);
        var legalEntityDto = Assert.IsType<ClientCase>(legalEntityCaseOk.Value);
        Assert.False(legalEntityDto.CanRevealSensitiveData);
        Assert.Null(legalEntityDto.LegalEntity!.Inn);
        Assert.Null(legalEntityDto.LegalEntity.Kpp);
        Assert.Null(legalEntityDto.LegalEntity.Ogrn);
        Assert.Null(legalEntityDto.LegalEntity.Address);
        Assert.Null(legalEntityDto.LegalEntity.Phone);
        Assert.Null(legalEntityDto.LegalEntity.Email);
        Assert.Null(legalEntityDto.LegalEntity.Director);

        var partiesResult = await controller.GetRelatedParties(clientCase.Id, includeArchived: false, CancellationToken.None);

        var partiesOk = Assert.IsType<OkObjectResult>(partiesResult.Result);
        var party = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(partiesOk.Value));
        Assert.Equal(string.Empty, party.Phone);
        Assert.Equal(string.Empty, party.Email);
    }

    [Fact]
    public async Task GetById_HidesSensitiveContacts_WhenAdminClaimIsNotAdminInDatabase()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "stale-admin-case@example.com", "Наблюдатель");
        viewer.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanEdit = false;

        var client = new Client
        {
            Name = "Клиент со старым админ-токеном",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
            Phone = "+79990001122",
            Email = "stale-admin-client@example.test",
            Address = "Новосибирск, Красный проспект, 1",
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело со старым админ-токеном",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = viewer.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer, includeAdminRoleClaim: true);

        var result = await controller.GetById(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<ClientCase>(ok.Value);
        Assert.False(dto.CanRevealSensitiveData);
        Assert.Equal(string.Empty, dto.Client!.Phone);
        Assert.Equal(string.Empty, dto.Client.Email);
        Assert.Equal(string.Empty, dto.Client.Address);
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
        var controller = CreateController(db, manager, notificationService: CreateNotificationService(db));

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
    public async Task GetAccessRequests_SelectsOnlyListFields()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var manager = await CreateUserAsync(db, "access-projection-manager@example.com", "Руководитель", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "access-projection-owner@example.com", "Исполнитель");
        var client = new Client
        {
            Name = "Клиент компактного запроса",
            IsActive = true,
            ResponsibleUserId = responsible.Id,
        };
        db.ClientCases.Add(new ClientCase
        {
            Client = client,
            Title = "Дело компактного запроса",
            CaseNumber = "CASE-PROJECTION",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            Description = "Не должно загружаться для списка запросов доступа",
            EventHistory = "Не должно загружаться для списка запросов доступа",
            FeeAmount = 123456m,
            IsHiddenFromExecutor = true,
            AccessRequestedAt = new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc),
            AccessRequestedByUserId = responsible.Id,
            AccessRequestedByUserName = "Исполнитель",
            AccessRequestReason = "Нужно проверить дело.",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, manager, notificationService: CreateNotificationService(db));
        interceptor.Commands.Clear();

        var result = await controller.GetAccessRequests(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseAccessRequestListItemDto>>(ok.Value));
        Assert.Equal("CASE-PROJECTION", item.CaseNumber);
        Assert.Equal("Клиент компактного запроса", item.OwnerName);
        Assert.Equal("Исполнитель", item.ResponsibleName);

        var listCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal)
            && command.Contains("AccessRequestedAt", StringComparison.Ordinal)
            && command.Contains("ORDER BY", StringComparison.Ordinal));
        Assert.DoesNotContain("Description", listCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("EventHistory", listCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("FeeAmount", listCommand, StringComparison.Ordinal);
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
        var controller = CreateController(db, manager, notificationService: CreateNotificationService(db));

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
        var controller = CreateController(db, manager, notificationService: CreateNotificationService(db));

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
        var timeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var localNow = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone);
        var hearingAtUtc = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);
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
                NextHearingAt = hearingAtUtc,
                LastViewedAt = hearingAtUtc,
                CreatedAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            },
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Скрытое дело",
                CaseNumber = "CASE-002",
                ResponsibleUserId = otherResponsible.Id,
                Status = ClientCaseStatus.Open,
                FeeAmount = 50_000m,
                CreatedAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            });
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZone.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var expectedFilePrefix = $"cases-registry-{localNow:yyyyMMdd}-";
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
        Assert.Contains("05.06.2026 16:00", csv);
        Assert.DoesNotContain("05.06.2026 12:00", csv);
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
        Assert.Contains("05.06.2026 16:00", excel);
        Assert.DoesNotContain("05.06.2026 12:00", excel);
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
        Assert.Contains("05.06.2026 16:00", pdfService.LastHtml);
        Assert.DoesNotContain("05.06.2026 12:00", pdfService.LastHtml);
        Assert.DoesNotContain("CASE-002", pdfService.LastHtml);

        var printResult = await controller.PrintRegistry(
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

        var print = Assert.IsType<ContentResult>(printResult);
        Assert.Equal("text/html; charset=utf-8", print.ContentType);
        Assert.Contains("Реестр дел", print.Content);
        Assert.Contains("CASE-001", print.Content);
        Assert.Contains("05.06.2026 16:00", print.Content);
        Assert.DoesNotContain("05.06.2026 12:00", print.Content);
        Assert.DoesNotContain("CASE-002", print.Content);
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
        var printResult = await controller.PrintRegistry(
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
        Assert.IsType<ForbidResult>(printResult);
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
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Payments, viewScope: DataVisibilityScope.All);
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
        var linkedPayment = new Payment
        {
            Client = client,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 3000m,
            PaidAmount = 1000m,
            Date = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            Description = "Не загружать описание для сводки реестра",
            Notes = "Не загружать заметки для сводки реестра",
            CounterpartyName = "Не загружать контрагента для сводки реестра",
        };
        db.Payments.Add(linkedPayment);
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
        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedPayment.Id,
            ClientCaseId = clientCase.Id,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);
        interceptor.Commands.Clear();

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

        Assert.Equal(53000m, item.IncomeTotal);
        Assert.Equal(36000m, item.IncomePaid);
        Assert.Equal(17000m, item.IncomeOutstanding);
        Assert.Equal(2, item.IncomeCount);
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
        Assert.NotNull(item.LastMeaningfulActivityAt);
        Assert.Null(item.NextInactivityControlAt);
        Assert.Equal("Есть активная задача", item.InactivityControlExclusionReason);

        var paymentCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"Payments\"", StringComparison.Ordinal)
            && command.Contains("SystemNotes", StringComparison.Ordinal));
        Assert.DoesNotContain("\"Description\"", paymentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Notes\"", paymentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CounterpartyName\"", paymentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PaymentOrderNumber\"", paymentCommand, StringComparison.Ordinal);
        Assert.Contains(interceptor.Commands, command =>
            command.Contains("FROM \"InvoiceClientCases\"", StringComparison.Ordinal)
            && command.Contains("PaymentId", StringComparison.Ordinal)
            && command.Contains("ClientCaseId", StringComparison.Ordinal));
        var activityCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("TaskJournalComments", StringComparison.Ordinal)
            && command.Contains("ClientCaseDocuments", StringComparison.Ordinal)
            && command.Contains("ClientCaseCourtEvents", StringComparison.Ordinal));
        Assert.Contains("UNION ALL", activityCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, interceptor.Commands.Count);
    }

    [Fact]
    public async Task Get_ReturnsInactivityControlDatesAndStoredExclusionReason()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var lastActivityAt = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        db.ClientCases.AddRange(
            new ClientCase
            {
                Client = client,
                Title = "Дело с контролем",
                Status = ClientCaseStatus.Open,
                CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                ActivityConfirmedAt = lastActivityAt,
                InactivityRemindersEnabled = true,
            },
            new ClientCase
            {
                Client = client,
                Title = "Исключённое дело",
                Status = ClientCaseStatus.Open,
                CreatedAt = new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc),
                InactivityRemindersEnabled = false,
                InactivityRemindersDisabledReason = "Ожидаем решение клиента",
            });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.inactiveDays",
            Value = "7",
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db, owner).Get(
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
            ct: CancellationToken.None);

        var items = GetAnonymousItems(Assert.IsType<OkObjectResult>(result).Value);
        var controlled = Assert.Single(items, item => item.Title == "Дело с контролем");
        Assert.Equal(lastActivityAt, controlled.LastMeaningfulActivityAt);
        Assert.Equal(lastActivityAt.AddDays(7), controlled.NextInactivityControlAt);
        Assert.Empty(controlled.InactivityControlExclusionReason);

        var excluded = Assert.Single(items, item => item.Title == "Исключённое дело");
        Assert.Equal("Ожидаем решение клиента", excluded.InactivityControlExclusionReason);
        Assert.Null(excluded.NextInactivityControlAt);
    }

    [Fact]
    public async Task GetSummary_ReturnsCompactCaseSnapshot()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Owner");
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Payments, viewScope: DataVisibilityScope.All);
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
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var owner = await CreateUserAsync(db, "owner@example.com", "Owner", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Payments, viewScope: DataVisibilityScope.All);
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
        var directlyLinkedContract = new Contract
        {
            Number = "2-2026",
            Title = "Прямо связанный договор",
            Date = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc),
            Amount = 10000m,
            Status = ContractStatus.Draft,
        };
        var source = new PaymentSource
        {
            Name = "Касса",
            Description = "Наличные",
            ColorHex = "#10B981",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Contracts.AddRange(contract, directlyLinkedContract);
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
        db.ClientCaseContracts.Add(new ClientCaseContract
        {
            ClientCaseId = clientCase.Id,
            ContractId = directlyLinkedContract.Id,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);
        interceptor.Commands.Clear();

        var result = await controller.GetFinance(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseFinanceDetailDto>(ok.Value);

        Assert.Equal(clientCase.Id, dto.CaseId);
        Assert.Equal(6, interceptor.Commands.Count);
        var linkedContractsCommand = Assert.Single(interceptor.Commands, command =>
            command.Contains("ClientCaseContracts", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("ContractInvoices", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("ContractPayments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("UNION ALL", linkedContractsCommand, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(2, dto.Contracts.Count);
        Assert.Contains(dto.Contracts, linkedContract => linkedContract.Id == contract.Id && linkedContract.Number == "1-2026");
        Assert.Contains(dto.Contracts, linkedContract => linkedContract.Id == directlyLinkedContract.Id && linkedContract.Number == "2-2026");
        Assert.Equal(2, dto.Payments.Count);
        Assert.All(dto.Payments, payment => Assert.Equal("Касса", payment.PaymentSourceName));
    }

    [Fact]
    public async Task GetFinance_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-finance-denied@example.com", "Owner", DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = false,
            CanCreate = false,
            CanEdit = false,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.All,
        });
        var client = new Client
        {
            Name = "Finance denied client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Finance denied case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetFinance(clientCase.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);

        var historyResult = await controller.GetDocumentTemplateHistory(CancellationToken.None);
        Assert.IsType<ForbidResult>(historyResult.Result);

        var versionResult = await controller.GetDocumentTemplateVersion(1, CancellationToken.None);
        Assert.IsType<ForbidResult>(versionResult.Result);

        var restoreResult = await controller.RestoreDocumentTemplateVersion(1, CancellationToken.None);
        Assert.IsType<ForbidResult>(restoreResult.Result);
    }

    [Fact]
    public async Task CourtRecoveries_LinkOnlyPaidAmountWithoutCreatingAnotherPayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "court-recovery@example.com", "Юрист", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        var client = new Client { Name = "Клиент по взысканию", IsActive = true, ResponsibleUserId = user.Id };
        var clientCase = new ClientCase
        {
            Client = client, ResponsibleUserId = user.Id, Title = "Исполнение решения",
            Status = ClientCaseStatus.Open, CourtAwardedAmount = 100_000m,
        };
        var payment = new Payment
        {
            Client = client, ClientCase = clientCase, DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income, Status = PaymentStatus.Processing,
            Amount = 50_000m, PaidAmount = 30_000m,
            Date = new DateTime(2026, 9, 24), PlannedDate = new DateTime(2026, 9, 24),
            Description = "Частичное взыскание",
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user);

        Assert.IsType<BadRequestObjectResult>(await controller.SetCourtRecoveryPayment(
            clientCase.Id, payment.Id, new(30_000.01m), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.SetCourtRecoveryPayment(
            clientCase.Id, payment.Id, new(20_000m), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.SetCourtRecoveryPayment(
            clientCase.Id, payment.Id, new(25_000m), CancellationToken.None));

        var result = await controller.GetCourtRecoveries(clientCase.Id, null, CancellationToken.None);
        var overview = Assert.IsType<CasesV2Controller.CourtRecoveryOverviewDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(100_000m, overview.AwardedAmount);
        Assert.Equal(25_000m, overview.ReceivedAmount);
        Assert.Equal(75_000m, overview.RemainingAmount);
        Assert.Single(overview.LinkedPayments);
        Assert.Equal(25_000m, overview.LinkedPayments[0].EffectiveAmount);
        Assert.Single(await db.Payments.ToListAsync());
        Assert.Equal(30_000m, (await db.Payments.SingleAsync()).PaidAmount);

        Assert.IsType<NoContentResult>(await controller.RemoveCourtRecoveryPayment(
            clientCase.Id, payment.Id, CancellationToken.None));
        Assert.Empty(await db.ClientCaseCourtRecoveryPayments.ToListAsync());
    }

    [Fact]
    public async Task CourtRecoveries_RejectUnrelatedOrNonPaymentAndPreventReuseAcrossCases()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "court-recovery-boundary@example.com", "Юрист", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = user.Id };
        var firstCase = new ClientCase { Client = client, ResponsibleUserId = user.Id, Title = "Дело 1", Status = ClientCaseStatus.Open, CourtAwardedAmount = 50_000m };
        var secondCase = new ClientCase { Client = client, ResponsibleUserId = user.Id, Title = "Дело 2", Status = ClientCaseStatus.Open, CourtAwardedAmount = 50_000m };
        db.ClientCases.AddRange(firstCase, secondCase);
        await db.SaveChangesAsync();
        var payment = new Payment { Client = client, ClientCase = firstCase, DocumentKind = DocumentKind.Payment, Type = PaymentType.Income, Status = PaymentStatus.Completed, Amount = 10_000m, PaidAmount = 10_000m, Date = DateTime.UtcNow, PlannedDate = DateTime.UtcNow };
        var invoice = new Payment { Client = client, ClientCase = firstCase, DocumentKind = DocumentKind.Invoice, Type = PaymentType.Income, Status = PaymentStatus.Completed, Amount = 10_000m, PaidAmount = 10_000m, Date = DateTime.UtcNow, PlannedDate = DateTime.UtcNow };
        db.Payments.AddRange(payment, invoice);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user);

        Assert.IsType<NotFoundResult>(await controller.SetCourtRecoveryPayment(secondCase.Id, payment.Id, new(1_000m), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.SetCourtRecoveryPayment(firstCase.Id, invoice.Id, new(1_000m), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.SetCourtRecoveryPayment(firstCase.Id, payment.Id, new(0m), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.SetCourtRecoveryPayment(firstCase.Id, payment.Id, new(1_000m), CancellationToken.None));
        db.InvoiceClientCases.Add(new InvoiceClientCase { PaymentId = payment.Id, ClientCaseId = secondCase.Id });
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await controller.SetCourtRecoveryPayment(secondCase.Id, payment.Id, new(1_000m), CancellationToken.None));

        payment.PaidAmount = 500m;
        await db.SaveChangesAsync();
        var overview = Assert.IsType<CasesV2Controller.CourtRecoveryOverviewDto>(
            Assert.IsType<OkObjectResult>((await controller.GetCourtRecoveries(firstCase.Id, null, CancellationToken.None)).Result).Value);
        Assert.Equal(0m, overview.ReceivedAmount);
        Assert.NotNull(overview.LinkedPayments[0].Warning);

        db.Payments.Remove(payment);
        await db.SaveChangesAsync();
        Assert.Empty(await db.ClientCaseCourtRecoveryPayments.ToListAsync());
    }

    [Fact]
    public async Task CourtRecoveries_RequireCaseEditAndPaymentsView()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "court-recovery-permissions@example.com", "Сотрудник", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments, canView: false);
        var clientCase = new ClientCase { Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = user.Id }, Title = "Дело", Status = ClientCaseStatus.Open, ResponsibleUserId = user.Id, CourtAwardedAmount = 10_000m };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);
        Assert.IsType<ForbidResult>((await controller.GetCourtRecoveries(clientCase.Id, null, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>(await controller.SetCourtRecoveryPayment(clientCase.Id, 1, new(1m), CancellationToken.None));
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Cases, canEdit: false);
        controller = CreateController(db, user);
        Assert.IsType<ForbidResult>(await controller.SetCourtRecoveryPayment(clientCase.Id, 1, new(1m), CancellationToken.None));
    }

    [Fact]
    public async Task GetById_WithoutPaymentsViewPermission_HidesFinanceFieldsAndPayments()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-detail-finance-denied@example.com", "Owner", DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = false,
            ViewScope = DataVisibilityScope.All,
        });
        var client = new Client
        {
            Name = "Finance hidden client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Finance hidden case",
            Status = ClientCaseStatus.Open,
            FeeAmount = 50000m,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            Client = client,
            ClientCase = clientCase,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 20000m,
            PaidAmount = 20000m,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetById(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<ClientCase>(ok.Value);
        Assert.Equal(0m, dto.FeeAmount);
        Assert.Equal(0m, dto.IncomeTotal);
        Assert.Equal(0m, dto.IncomePaid);
        Assert.Equal(0m, dto.IncomeOutstanding);
        Assert.Empty(dto.Payments);
    }

    [Fact]
    public async Task CourtRole_CreateUpdateAndLegacyUpdate_PreserveExplicitChoice()
    {
        await using var db = await CreateContextAsync();
        var editor = await CreateUserAsync(db, "court-role-editor@example.com", "Редактор", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = editor.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, editor);

        var invalid = await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Дело", OurCourtRole = "other",
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalid);

        var invalidInn = await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Дело", ClaimantInn = "12345",
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalidInn);
        var invalidOgrn = await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Дело", DefendantOgrn = "123456789012X",
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalidOgrn);

        var created = Assert.IsType<CreatedResult>(await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Дело", CourtName = "Районный суд",
            OurCourtRole = " claimant ", Claimant = "ООО Истец",
            ClaimantInn = " 1234567890 ", ClaimantOgrn = "1234567890123",
            Defendant = "Ответчик", DefendantInn = "123456789012",
            DefendantOgrn = "123456789012345",
        }, CancellationToken.None));
        var courtCase = Assert.IsType<ClientCase>(created.Value);
        Assert.Equal("claimant", courtCase.OurCourtRole);
        Assert.Equal("1234567890", courtCase.ClaimantInn);
        Assert.Equal("1234567890123", courtCase.ClaimantOgrn);
        Assert.Equal("123456789012", courtCase.DefendantInn);
        Assert.Equal("123456789012345", courtCase.DefendantOgrn);

        Assert.IsType<OkObjectResult>(await controller.Update(courtCase.Id, new ClientCase
        {
            ClientId = client.Id, Title = "Дело после изменения", Status = ClientCaseStatus.Open,
            OurCourtRole = "respondent", ClaimantInn = "0987654321",
        }, CancellationToken.None));
        Assert.Equal("respondent", (await db.ClientCases.FindAsync(courtCase.Id))?.OurCourtRole);
        Assert.Equal("0987654321", (await db.ClientCases.FindAsync(courtCase.Id))?.ClaimantInn);

        Assert.IsType<OkObjectResult>(await controller.Update(courtCase.Id, new ClientCase
        {
            ClientId = client.Id, Title = "Старый клиент", Status = ClientCaseStatus.Open,
        }, CancellationToken.None));
        Assert.Equal("respondent", (await db.ClientCases.FindAsync(courtCase.Id))?.OurCourtRole);
        Assert.Equal("0987654321", (await db.ClientCases.FindAsync(courtCase.Id))?.ClaimantInn);
        Assert.Equal("123456789012345", (await db.ClientCases.FindAsync(courtCase.Id))?.DefendantOgrn);
    }

    [Fact]
    public async Task CourtAmounts_ValidatePersistClearAndSurviveOlderUpdates()
    {
        await using var db = await CreateContextAsync();
        var editor = await CreateUserAsync(db, "court-amounts@example.com", "Редактор", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = editor.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, editor);

        Assert.IsType<BadRequestObjectResult>(await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Некорректная сумма", CourtClaimAmount = -1m,
        }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Лишние копейки", CourtStateFeeAmount = 12.345m,
        }, CancellationToken.None));

        var created = Assert.IsType<ClientCase>(Assert.IsType<CreatedResult>(await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Судебные суммы", CourtClaimAmount = 100000.50m,
            CourtAwardedAmount = 75000m, CourtStateFeeAmount = 3500.25m,
        }, CancellationToken.None)).Value);
        Assert.Equal(100000.50m, created.CourtClaimAmount);
        Assert.Equal(75000m, created.CourtAwardedAmount);
        Assert.Equal(3500.25m, created.CourtStateFeeAmount);

        Assert.IsType<OkObjectResult>(await controller.Update(created.Id, new ClientCase
        {
            ClientId = client.Id, Title = created.Title, CourtAwardedAmount = 80000.10m,
            ClearCourtStateFeeAmount = true,
        }, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.Update(created.Id, new ClientCase
        {
            ClientId = client.Id, Title = "Старый клиент",
        }, CancellationToken.None));
        await db.Entry(created).ReloadAsync();
        Assert.Equal(100000.50m, created.CourtClaimAmount);
        Assert.Equal(80000.10m, created.CourtAwardedAmount);
        Assert.Null(created.CourtStateFeeAmount);
    }

    [Fact]
    public async Task Create_WithNearestHearing_NotifiesAssignedResponsible()
    {
        await using var db = await CreateContextAsync();
        var editor = await CreateUserAsync(db, "hearing-create-editor@example.com", "Редактор", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "hearing-create-responsible@example.com", "Ответственный", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = editor.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var hearingAt = DateTime.UtcNow.AddDays(7);
        var controller = CreateController(db, editor, notificationService: CreateNotificationService(db));

        var result = await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Судебное дело", Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id, NextHearingAt = hearingAt,
        }, CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CourtHearing, notification.Type);
        Assert.Contains("Назначено судебное заседание", notification.Title);
        Assert.Contains("Новая дата", notification.BodyPreview);
        Assert.DoesNotContain("Прежняя дата", notification.BodyPreview);
    }

    [Fact]
    public async Task Update_WhenNearestHearingChanges_NotifiesResponsibleOnce()
    {
        await using var db = await CreateContextAsync();
        var editor = await CreateUserAsync(db, "hearing-change-editor@example.com", "Редактор", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "hearing-change-responsible@example.com", "Ответственный", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = editor.Id };
        var previousAt = DateTime.UtcNow.AddDays(7);
        var currentAt = previousAt.AddDays(2);
        var clientCase = new ClientCase
        {
            Client = client, Title = "Судебное дело", Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id, NextHearingAt = previousAt,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, editor, notificationService: CreateNotificationService(db));
        var update = new ClientCase
        {
            ClientId = client.Id, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id, NextHearingAt = currentAt,
        };

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, update, CancellationToken.None));
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CourtHearing, notification.Type);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("Прежняя дата", notification.BodyPreview);
        Assert.Contains("Новая дата", notification.BodyPreview);

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, update, CancellationToken.None));
        Assert.Single(await db.UserNotifications.ToListAsync());

        update.NextHearingAt = null;
        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, update, CancellationToken.None));
        Assert.Single(await db.UserNotifications.ToListAsync());

        update.NextHearingAt = currentAt.AddDays(1);
        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, update, CancellationToken.None));
        var appointment = Assert.Single(await db.UserNotifications.Where(item => item.Title.StartsWith("Назначено судебное заседание")).ToListAsync());
        Assert.Equal(responsible.Id, appointment.RecipientUserId);
    }

    [Fact]
    public async Task Create_WithCourtDecisionDate_NotifiesAssignedResponsible()
    {
        await using var db = await CreateContextAsync();
        var editor = await CreateUserAsync(db, "decision-create-editor@example.com", "Редактор", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "decision-create-responsible@example.com", "Ответственный", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = editor.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var decisionAt = new DateTime(2026, 10, 5);
        var controller = CreateController(db, editor, notificationService: CreateNotificationService(db));

        var result = await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Судебное дело", Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id, CourtDecisionAt = decisionAt,
        }, CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CourtDecisionPublished, notification.Type);
        Assert.Contains("Вынесено судебное решение", notification.Title);
        Assert.Contains("05.10.2026", notification.BodyPreview);
        Assert.DoesNotContain("Прежняя дата", notification.BodyPreview);
    }

    [Fact]
    public async Task Create_WithHearingAndDecision_NotifiesForBothEvents()
    {
        await using var db = await CreateContextAsync();
        var editor = await CreateUserAsync(db, "case-events-editor@example.com", "Редактор", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "case-events-responsible@example.com", "Ответственный", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = editor.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, editor, notificationService: CreateNotificationService(db));

        Assert.IsType<CreatedResult>(await controller.Create(new ClientCase
        {
            ClientId = client.Id, Title = "Судебное дело", Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            NextHearingAt = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc),
            CourtDecisionAt = new DateTime(2026, 10, 5),
        }, CancellationToken.None));

        var notifications = await db.UserNotifications.ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, item => Assert.Equal(responsible.Id, item.RecipientUserId));
        Assert.Contains(notifications, item => item.Type == UserNotificationTypes.CourtHearing);
        Assert.Contains(notifications, item => item.Type == UserNotificationTypes.CourtDecisionPublished);
    }

    [Fact]
    public async Task Update_WhenCourtDecisionDateChanges_NotifiesOnlyForActualChanges()
    {
        await using var db = await CreateContextAsync();
        var editor = await CreateUserAsync(db, "decision-update-editor@example.com", "Редактор", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "decision-update-responsible@example.com", "Ответственный", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = editor.Id };
        var clientCase = new ClientCase
        {
            Client = client, Title = "Судебное дело", Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id, CourtDecisionAt = new DateTime(2026, 10, 5),
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, editor, notificationService: CreateNotificationService(db));
        var update = new ClientCase
        {
            ClientId = client.Id, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id, CourtDecisionAt = new DateTime(2026, 10, 7),
        };

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, update, CancellationToken.None));
        var changed = await db.UserNotifications.SingleAsync();
        Assert.Equal(responsible.Id, changed.RecipientUserId);
        Assert.Contains("Прежняя дата: 05.10.2026", changed.BodyPreview);
        Assert.Contains("Новая дата: 07.10.2026", changed.BodyPreview);

        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, update, CancellationToken.None));
        Assert.Single(await db.UserNotifications.ToListAsync());

        update.CourtDecisionAt = null;
        Assert.IsType<OkObjectResult>(await controller.Update(clientCase.Id, update, CancellationToken.None));
        var removed = Assert.Single(await db.UserNotifications.Where(item => item.Title.StartsWith("Удалена дата судебного решения")).ToListAsync());
        Assert.Equal(responsible.Id, removed.RecipientUserId);
    }

    [Fact]
    public async Task Update_WithoutPaymentsViewPermission_PreservesFeeAmount()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-update-finance-denied@example.com", "Owner", DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = false,
            ViewScope = DataVisibilityScope.All,
        });
        var client = new Client
        {
            Name = "Fee preserved client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Fee preserved case",
            Status = ClientCaseStatus.Open,
            Priority = "normal",
            FeeAmount = 50000m,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = "Updated title",
            Status = ClientCaseStatus.Open,
            Priority = "normal",
            FeeAmount = 1m,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == clientCase.Id);
        Assert.Equal(50000m, saved.FeeAmount);
    }

    [Fact]
    public async Task GetFinance_UsesUserTimeZoneForPaymentCalendarStatus()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "finance-timezone@example.com", "Finance Owner", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Payments, viewScope: DataVisibilityScope.All);
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
    public async Task GetFinance_UsesProfileTimeZoneForExpensePaymentStatuses()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "finance-expense-timezone@example.com", "Finance Expense Owner", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Payments, viewScope: DataVisibilityScope.All);
        var (timeZoneId, profileToday) = ResolveTimeZoneWithDifferentLocalDate();

        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var supplier = new Client
        {
            Name = "Finance Expense Supplier",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var client = new Client
        {
            Name = "Finance Expense Client",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Finance expense timezone case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(supplier, client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var plannedDate = profileToday.AddDays(-1);
        db.Payments.Add(new Payment
        {
            Client = supplier,
            ExpenseClient = client,
            ClientCase = clientCase,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Account = "EXP-TZ",
            Amount = 8_000m,
            PaidAmount = 0m,
            Date = plannedDate,
            PlannedDate = plannedDate,
            AccountDate = plannedDate,
            Description = "Timezone expense invoice",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetFinance(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseFinanceDetailDto>(ok.Value);
        var payment = Assert.Single(dto.Payments);
        var invoice = Assert.Single(dto.Invoices);

        Assert.Equal(PaymentStatus.Overdue.ToString(), payment.Status);
        Assert.Equal(PaymentStatus.Overdue.ToString(), invoice.Status);
        Assert.Equal(1, dto.Calendar.OverdueCount);
        Assert.Equal(8_000m, dto.Calendar.OverdueTotal);
        Assert.Equal(8_000m, dto.Totals.ExpenseOutstanding);
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
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Payments, viewScope: DataVisibilityScope.All);
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
    public async Task CourtRegistry_ReturnsOnlyCourtCasesAndServerSummary()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-owner@example.com", "Судебный юрист", DataVisibilityScope.All);
        var now = DateTime.UtcNow;
        var client = new Client { Name = "Клиент суда", IsActive = true, ResponsibleUserId = owner.Id };
        var soonCase = new ClientCase
        {
            Client = client,
            Title = "Судебное дело с заседанием",
            CaseType = "Гражданское дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CourtName = "Тестовый районный суд",
            CourtCaseNumber = "2-100/2026",
            OurCourtRole = "claimant",
            NextHearingAt = now.AddDays(5),
            IsUnderControl = true,
        };
        var instanceOnlyCase = new ClientCase
        {
            Client = client,
            Title = "Дело только с инстанцией",
            CaseType = "Арбитражное дело",
            OurCourtRole = "respondent",
            Status = ClientCaseStatus.OnHold,
            ResponsibleUserId = owner.Id,
        };
        var ordinaryCase = new ClientCase
        {
            Client = client,
            Title = "Обычное внесудебное дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.ClientCases.AddRange(soonCase, instanceOnlyCase, ordinaryCase);
        db.ClientCaseCourtInstances.Add(new ClientCaseCourtInstance
        {
            ClientCase = instanceOnlyCase,
            Instance = "appeal",
            CourtName = "Апелляционный суд",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var soonResult = await controller.Get(
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
            sortBy: "nextHearingAt",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            scope: "all",
            courtOnly: true,
            courtState: "soon",
            ct: CancellationToken.None);

        var soonItem = Assert.Single(GetAnonymousItems(Assert.IsType<OkObjectResult>(soonResult).Value));
        Assert.Equal(soonCase.Id, soonItem.Id);

        var allCourtResult = await controller.Get(
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
            scope: "all",
            courtOnly: true,
            ct: CancellationToken.None);
        var allCourtItems = GetAnonymousItems(Assert.IsType<OkObjectResult>(allCourtResult).Value);
        Assert.Equal(2, allCourtItems.Count);
        Assert.DoesNotContain(allCourtItems, item => item.Id == ordinaryCase.Id);

        var appealResult = await controller.Get(
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
            scope: "all",
            courtOnly: true,
            courtInstance: "appeal",
            ct: CancellationToken.None);
        var appealItem = Assert.Single(GetAnonymousItems(Assert.IsType<OkObjectResult>(appealResult).Value));
        Assert.Equal(instanceOnlyCase.Id, appealItem.Id);
        Assert.Contains("appeal", appealItem.CourtInstanceKeys);

        var claimantResult = await controller.Get(
            clientId: null, legalEntityId: null, status: null, search: null,
            responsibleUserId: null, caseType: null, priority: null, from: null, to: null,
            minFee: null, maxFee: null, sortBy: null, sortDir: null,
            scope: "all", courtOnly: true, ct: CancellationToken.None, ourCourtRole: "claimant");
        var claimantItem = Assert.Single(GetAnonymousItems(Assert.IsType<OkObjectResult>(claimantResult).Value));
        Assert.Equal(soonCase.Id, claimantItem.Id);
        Assert.Equal("claimant", claimantItem.OurCourtRole);

        var roleSummary = await controller.GetCourtRegistrySummary(
            status: null, search: null, responsibleUserId: null, scope: "all",
            ct: CancellationToken.None, ourCourtRole: "respondent");
        var roleSummaryValue = Assert.IsType<OkObjectResult>(roleSummary).Value!;
        Assert.Equal(1, roleSummaryValue.GetType().GetProperty("total")?.GetValue(roleSummaryValue));

        var summaryResult = await controller.GetCourtRegistrySummary(
            status: null,
            search: null,
            responsibleUserId: null,
            scope: "all",
            courtState: null,
            ct: CancellationToken.None);
        var summary = Assert.IsType<OkObjectResult>(summaryResult).Value!;
        Assert.Equal(2, summary.GetType().GetProperty("total")?.GetValue(summary));
        Assert.Equal(2, summary.GetType().GetProperty("active")?.GetValue(summary));
        Assert.Equal(1, summary.GetType().GetProperty("soon")?.GetValue(summary));
        Assert.Equal(1, summary.GetType().GetProperty("withoutHearing")?.GetValue(summary));
        Assert.Equal(1, summary.GetType().GetProperty("underControl")?.GetValue(summary));
        var categories = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            summary.GetType().GetProperty("categories")?.GetValue(summary));
        Assert.Equal(2, categories.Cast<object>().Count());
    }

    [Fact]
    public async Task CourtReportOverview_AggregatesOnlyVisibleCourtCasesAndFiltersCreationPeriod()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "court-report-manager@example.com", "Руководитель", DataVisibilityScope.All);
        var lawyer = await CreateUserAsync(db, "court-report-lawyer@example.com", "Юрист");
        var outsider = await CreateUserAsync(db, "court-report-outsider@example.com", "Другой юрист");
        db.ClientCases.AddRange(
            new ClientCase { Client = new Client { Name = "Клиент 1", IsActive = true, ResponsibleUserId = lawyer.Id }, Title = "Активное судебное дело", ResponsibleUserId = lawyer.Id, CourtName = "Суд А", OurCourtRole = "claimant", Status = ClientCaseStatus.Open, CreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), ClaimFiledAt = new DateTime(2026, 8, 1), CourtDecisionAt = new DateTime(2026, 8, 11), CourtClaimAmount = 100000.50m, CourtAwardedAmount = 75000m, CourtStateFeeAmount = 3000m },
            new ClientCase { Client = new Client { Name = "Клиент 2", IsActive = true, ResponsibleUserId = lawyer.Id }, Title = "Завершённое судебное дело", ResponsibleUserId = lawyer.Id, CourtName = "Суд А", OurCourtRole = "respondent", Status = ClientCaseStatus.Closed, CreatedAt = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), ClaimFiledAt = new DateTime(2026, 8, 2), CourtDecisionAt = new DateTime(2026, 8, 6), CourtClaimAmount = 200000m, CourtStateFeeAmount = 0m },
            new ClientCase { Client = new Client { Name = "Клиент с назначенным юристом", IsActive = true, ResponsibleUserId = lawyer.Id }, Title = "Судебное дело без отдельного исполнителя", CourtName = "Суд А", Status = ClientCaseStatus.Open, CreatedAt = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), ClaimFiledAt = new DateTime(2026, 8, 10), CourtDecisionAt = new DateTime(2026, 8, 1) },
            new ClientCase { Client = new Client { Name = "Клиент 3", IsActive = true, ResponsibleUserId = outsider.Id }, Title = "Чужое судебное дело", ResponsibleUserId = outsider.Id, CourtName = "Суд Б", Status = ClientCaseStatus.Open, CreatedAt = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), ClaimFiledAt = new DateTime(2026, 8, 3), CourtDecisionAt = new DateTime(2026, 8, 5), CourtClaimAmount = 500000m, CourtAwardedAmount = 250000m, CourtStateFeeAmount = 5000m },
            new ClientCase { Client = new Client { Name = "Клиент без ответственного", IsActive = true }, Title = "Судебное дело без ответственного", CourtName = "Суд В", Status = ClientCaseStatus.Open, CreatedAt = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), ClaimFiledAt = new DateTime(2026, 8, 1), CourtDecisionAt = new DateTime(2026, 8, 1), CourtClaimAmount = 0m },
            new ClientCase { Client = new Client { Name = "Клиент 4", IsActive = true, ResponsibleUserId = lawyer.Id }, Title = "Несудебное дело", ResponsibleUserId = lawyer.Id, Status = ClientCaseStatus.Open, CreatedAt = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), CourtClaimAmount = 900000m });
        await db.SaveChangesAsync();

        var visible = (await CreateController(db, lawyer).GetCourtReportOverview(ct: CancellationToken.None)).Value!;
        Assert.Equal(3, visible.Total);
        Assert.Equal(2, visible.Active);
        Assert.Equal(1, visible.Completed);
        Assert.Equal(300000.50m, visible.Amounts.ClaimTotal);
        Assert.Equal(2, visible.Amounts.ClaimCases);
        Assert.Equal(75000m, visible.Amounts.AwardedTotal);
        Assert.Equal(1, visible.Amounts.AwardedCases);
        Assert.Equal(3000m, visible.Amounts.StateFeeTotal);
        Assert.Equal(2, visible.Amounts.StateFeeCases);
        Assert.Equal("Юрист", Assert.Single(visible.ByResponsible).Name);
        Assert.Equal(visible.Amounts, Assert.Single(visible.ByResponsible).Amounts);
        Assert.Equal(2, Assert.Single(visible.ByResponsible).DecisionDurationSampleCount);
        Assert.Equal(7, Assert.Single(visible.ByResponsible).AverageDaysFromClaimToDecision);
        Assert.Equal(3, Assert.Single(visible.ByCourt).Total);
        Assert.Equal(7, Assert.Single(visible.ByCourt).AverageDaysFromClaimToDecision);
        Assert.Equal(visible.Amounts, Assert.Single(visible.ByCourt).Amounts);

        var claimantOnly = (await CreateController(db, lawyer).GetCourtReportOverview(
            ct: CancellationToken.None, ourCourtRole: "claimant")).Value!;
        Assert.Equal(1, claimantOnly.Total);
        Assert.Equal(100000.50m, claimantOnly.Amounts.ClaimTotal);
        Assert.Equal(1, claimantOnly.Amounts.ClaimCases);
        Assert.Equal(1, Assert.Single(claimantOnly.ByCourt).Total);

        var amountReport = (await CreateController(db, lawyer).GetCourtReportOverview(
            ct: CancellationToken.None, minClaimAmount: 200000m, maxClaimAmount: 200000m)).Value!;
        Assert.Equal(1, amountReport.Total);
        Assert.Equal(200000m, amountReport.Amounts.ClaimTotal);
        Assert.Equal(1, Assert.Single(amountReport.ByResponsible).Total);
        var amountSummary = Assert.IsType<OkObjectResult>(await CreateController(db, lawyer).GetCourtRegistrySummary(
            status: null, search: null, responsibleUserId: null, scope: "all", ct: CancellationToken.None,
            minClaimAmount: 200000m, maxClaimAmount: 200000m));
        Assert.Equal(1, amountSummary.Value!.GetType().GetProperty("total")?.GetValue(amountSummary.Value));
        var amountPage = Assert.IsType<OkObjectResult>(await CreateController(db, lawyer).Get(
            clientId: null, legalEntityId: null, status: null, search: null, responsibleUserId: null,
            caseType: null, priority: null, from: null, to: null, minFee: null, maxFee: null,
            sortBy: null, sortDir: null, scope: "all", courtOnly: true, ct: CancellationToken.None,
            minClaimAmount: 200000m, maxClaimAmount: 200000m));
        Assert.Equal("Завершённое судебное дело", Assert.Single(GetAnonymousItems(amountPage.Value)).Title);

        Assert.IsType<BadRequestObjectResult>((await CreateController(db, lawyer).GetCourtReportOverview(
            ct: CancellationToken.None, minClaimAmount: 200001m, maxClaimAmount: 200000m)).Result);
        Assert.IsType<BadRequestObjectResult>((await CreateController(db, lawyer).GetCourtReportOverview(
            ct: CancellationToken.None, minClaimAmount: -1m)).Result);
        Assert.IsType<BadRequestObjectResult>((await CreateController(db, lawyer).GetCourtReportOverview(
            ct: CancellationToken.None, maxClaimAmount: 1.001m)).Result);
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, lawyer).GetCourtRegistrySummary(
            status: null, search: null, responsibleUserId: null, ct: CancellationToken.None,
            minClaimAmount: 200001m, maxClaimAmount: 200000m));
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, lawyer).Get(
            clientId: null, legalEntityId: null, status: null, search: null, responsibleUserId: null,
            caseType: null, priority: null, from: null, to: null, minFee: null, maxFee: null,
            sortBy: null, sortDir: null, ct: CancellationToken.None, minClaimAmount: -1m));

        var all = (await CreateController(db, manager).GetCourtReportOverview(ct: CancellationToken.None)).Value!;
        Assert.Equal(5, all.Total);
        Assert.Equal(800000.50m, all.Amounts.ClaimTotal);
        Assert.Equal(4, all.Amounts.ClaimCases);
        Assert.Equal(250000m, Assert.Single(all.ByCourt, row => row.Name == "Суд Б").Amounts.AwardedTotal);
        Assert.Equal(1, Assert.Single(all.ByCourt, row => row.Name == "Суд В").Amounts.ClaimCases);
        Assert.Equal(3, all.ByCourt.Count);
        Assert.Equal(2, Assert.Single(all.ByCourt, row => row.Name == "Суд Б").AverageDaysFromClaimToDecision);
        Assert.Equal(0, Assert.Single(all.ByResponsible, row => row.Name == "Не назначен").AverageDaysFromClaimToDecision);

        var zeroAmount = (await CreateController(db, manager).GetCourtReportOverview(
            ct: CancellationToken.None, minClaimAmount: 0m, maxClaimAmount: 0m)).Value!;
        Assert.Equal(1, zeroAmount.Total);
        Assert.Equal(1, zeroAmount.Amounts.ClaimCases);

        var period = (await CreateController(db, manager).GetCourtReportOverview(
            from: new DateTime(2026, 9, 1, 17, 0, 0, DateTimeKind.Utc),
            toExclusive: new DateTime(2026, 9, 2, 17, 0, 0, DateTimeKind.Utc), ct: CancellationToken.None)).Value!;
        Assert.Equal(4, period.Total);
        Assert.IsType<BadRequestObjectResult>((await CreateController(db, manager).GetCourtReportOverview(
            from: new DateTime(2026, 9, 3), toExclusive: new DateTime(2026, 9, 2), ct: CancellationToken.None)).Result);

        lawyer.Role!.Permissions.Single().CanView = false;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await CreateController(db, lawyer).GetCourtReportOverview(ct: CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CourtWorkload_AggregatesCourtWorkAndChecksAssigneeVisibility()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "workload-manager@example.com", "Руководитель", DataVisibilityScope.All);
        var lawyer = await CreateUserAsync(db, "workload-lawyer@example.com", "Юрист");
        var noAccess = await CreateUserAsync(db, "workload-no-access@example.com", "Без доступа");
        noAccess.Role!.Permissions.Single().CanView = false;
        var now = DateTime.UtcNow;
        var client = new Client { Name = "Контрагент", IsActive = true, ResponsibleUserId = lawyer.Id };
        var caseWithEvent = new ClientCase
        {
            Client = client, Title = "Дело с событием", Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, CourtName = "Суд", NextHearingAt = now.AddDays(3),
            CourtEvents =
            [
                new ClientCaseCourtEvent
                {
                    EventType = CourtSyncNormalization.EventTypes.Hearing,
                    Title = "Судебное заседание", ScheduledAt = now.AddDays(2),
                },
                new ClientCaseCourtEvent
                {
                    EventType = CourtSyncNormalization.EventTypes.Hearing,
                    Title = "Второе заседание", ScheduledAt = now.AddDays(5),
                },
            ],
        };
        var caseWithoutEvent = new ClientCase
        {
            Client = client, Title = "Дело с датой", Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, CourtName = "Суд", NextHearingAt = now.AddDays(3),
        };
        var hiddenCase = new ClientCase
        {
            Client = client, Title = "Скрытое дело", Status = ClientCaseStatus.Open,
            ResponsibleUserId = manager.Id, CourtName = "Суд", IsHiddenFromExecutor = true,
        };
        var ordinaryCase = new ClientCase
        {
            Client = client, Title = "Несудебное дело", Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
        };
        db.ClientCases.AddRange(caseWithEvent, caseWithoutEvent, hiddenCase, ordinaryCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientCaseId = caseWithEvent.Id, AssigneeUserId = lawyer.Id,
                Title = "Просроченная судебная задача", Status = "new", DueAt = now.AddDays(-1),
            },
            new TaskJournalItem
            {
                ClientCaseId = ordinaryCase.Id, AssigneeUserId = lawyer.Id,
                Title = "Не судебная задача", Status = "new", DueAt = now.AddDays(-1),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, manager);
        var result = await controller.GetCourtWorkload(hiddenCase.Id);
        var rows = Assert.IsType<CasesV2Controller.CourtWorkloadDto[]>(
            Assert.IsType<OkObjectResult>(result).Value);
        var lawyerRow = Assert.Single(rows, row => row.UserId == lawyer.Id);
        Assert.Equal(2, lawyerRow.ActiveCases);
        Assert.Equal(3, lawyerRow.HearingsNextSevenDays);
        Assert.Equal(1, lawyerRow.OverdueCourtTasks);
        Assert.False(lawyerRow.CanAssign);
        Assert.DoesNotContain(rows, row => row.UserId == noAccess.Id);
        Assert.True(Assert.Single(rows, row => row.UserId == manager.Id).CanAssign);
    }

    [Fact]
    public async Task CourtSyncHealth_ShowsOnlyContinuousErrorsOlderThanTwentyFourHours()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "sync-health-manager@example.com", "Руководитель", DataVisibilityScope.All);
        var limited = await CreateUserAsync(db, "sync-health-limited@example.com", "Юрист");
        var client = new Client { Name = "Контрагент", IsActive = true, ResponsibleUserId = manager.Id };
        var now = DateTime.UtcNow;
        ClientCase MakeCase(string title, DateTime failureStartedAt, bool monitoringEnabled = true) => new()
        {
            Client = client, Title = title, Status = ClientCaseStatus.Open, CourtName = "Суд",
            CourtCaseNumber = "2-100/2026", CourtUrl = "https://example.sudrf.ru/case/42",
            CourtSyncFailureStartedAt = failureStartedAt, CourtSyncLastAttemptAt = now.AddMinutes(-10),
            CourtSyncLastError = "Сайт суда недоступен", CourtSyncMonitoringEnabled = monitoringEnabled,
        };
        var oldFailure = MakeCase("Старая ошибка", now.AddHours(-25));
        var recentFailure = MakeCase("Новая ошибка", now.AddHours(-23));
        var paused = MakeCase("Мониторинг остановлен", now.AddHours(-30), false);
        db.ClientCases.AddRange(oldFailure, recentFailure, paused);
        await db.SaveChangesAsync();

        var limitedController = CreateController(db, limited);
        Assert.IsType<ForbidResult>((await limitedController.GetCourtSyncHealth("failed", 1)).Result);
        var controller = CreateController(db, manager);
        var failedResult = await controller.GetCourtSyncHealth("failed", 1);
        var failed = Assert.IsType<CasesV2Controller.CourtSyncHealthPageDto>(
            Assert.IsType<OkObjectResult>(failedResult.Result).Value);
        Assert.Equal(1, failed.Total);
        Assert.Equal(oldFailure.Id, Assert.Single(failed.Items).CaseId);
        Assert.Equal("Сайт суда недоступен", failed.Items[0].LastError);

        var pausedResult = await controller.GetCourtSyncHealth("paused", 1);
        var pausedPage = Assert.IsType<CasesV2Controller.CourtSyncHealthPageDto>(
            Assert.IsType<OkObjectResult>(pausedResult.Result).Value);
        Assert.Equal(paused.Id, Assert.Single(pausedPage.Items).CaseId);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCourtSyncHealth("unknown", 1)).Result);
        var farPageResult = await controller.GetCourtSyncHealth("failed", int.MaxValue);
        var farPage = Assert.IsType<CasesV2Controller.CourtSyncHealthPageDto>(
            Assert.IsType<OkObjectResult>(farPageResult.Result).Value);
        Assert.Equal(100_000, farPage.Page);
        Assert.Empty(farPage.Items);
    }

    [Fact]
    public async Task CourtSyncHealthActions_EnforceManagerAccessAndPreserveMonitoringChoice()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "sync-actions-manager@example.com", "Руководитель", DataVisibilityScope.All);
        var limited = await CreateUserAsync(db, "sync-actions-limited@example.com", "Юрист");
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Контрагент", IsActive = true, ResponsibleUserId = manager.Id },
            Title = "Судебное дело", Status = ClientCaseStatus.Open, CourtName = "Суд",
            CourtCaseNumber = "2-100/2026", CourtUrl = "https://example.sudrf.ru/case/42",
            CourtSyncFailureStartedAt = DateTime.UtcNow.AddDays(-2), CourtSyncLastError = "Дело не найдено",
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var limitedController = CreateController(db, limited);
        Assert.IsType<ForbidResult>(await limitedController.SetCourtSyncMonitoring(
            clientCase.Id, new CasesV2Controller.SetCourtSyncMonitoringRequest { Enabled = false }, CancellationToken.None));

        var controller = CreateController(db, manager, sudrfCourtSyncService: new ThrowingSudrfCourtSyncService("Дело не найдено"));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateCourtSyncCaseNumber(
            clientCase.Id, new CasesV2Controller.UpdateCourtSyncCaseNumberRequest { CourtCaseNumber = "" }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateCourtSyncCaseNumber(
            clientCase.Id, new CasesV2Controller.UpdateCourtSyncCaseNumberRequest
            { CourtCaseNumber = "2-101/2026", CourtUrl = "https://untrusted.example/case/42" }, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.UpdateCourtSyncCaseNumber(
            clientCase.Id, new CasesV2Controller.UpdateCourtSyncCaseNumberRequest
            { CourtCaseNumber = "2-101/2026", CourtUrl = "https://example.sudrf.ru/case/43" }, CancellationToken.None));
        Assert.Equal("2-101/2026", clientCase.CourtCaseNumber);

        Assert.IsType<OkObjectResult>(await controller.SetCourtSyncMonitoring(
            clientCase.Id, new CasesV2Controller.SetCourtSyncMonitoringRequest { Enabled = false }, CancellationToken.None));
        Assert.False(clientCase.CourtSyncMonitoringEnabled);
        Assert.IsType<OkObjectResult>(await controller.SetCourtSyncMonitoring(
            clientCase.Id, new CasesV2Controller.SetCourtSyncMonitoringRequest { Enabled = true }, CancellationToken.None));
        Assert.True(clientCase.CourtSyncMonitoringEnabled);
        Assert.Null(clientCase.CourtSyncFailureStartedAt);
        Assert.Empty(clientCase.CourtSyncLastError);
    }

    [Fact]
    public async Task AssignCourtCase_RequiresManagerAndTargetAccess()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "assign-manager@example.com", "Руководитель", DataVisibilityScope.All);
        var lawyer = await CreateUserAsync(db, "assign-lawyer@example.com", "Юрист");
        var noAccess = await CreateUserAsync(db, "assign-no-access@example.com", "Без доступа");
        noAccess.Role!.Permissions.Single().CanView = false;
        var client = new Client { Name = "Контрагент", IsActive = true, ResponsibleUserId = manager.Id };
        var courtCase = new ClientCase
        {
            Client = client, Title = "Судебное дело", Status = ClientCaseStatus.Open,
            CourtName = "Суд", ResponsibleUserId = manager.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = client, Title = "Скрытое судебное дело", Status = ClientCaseStatus.Open,
            CourtName = "Суд", ResponsibleUserId = manager.Id, IsHiddenFromExecutor = true,
        };
        db.ClientCases.AddRange(courtCase, hiddenCase);
        await db.SaveChangesAsync();

        var limitedController = CreateController(db, lawyer);
        Assert.IsType<ForbidResult>(await limitedController.GetCourtWorkload(null));
        Assert.IsType<ForbidResult>(await limitedController.AssignCourtCase(
            courtCase.Id, new CasesV2Controller.AssignCourtCaseRequest { UserId = lawyer.Id }, CancellationToken.None));
        var managerController = CreateController(db, manager);
        Assert.IsType<BadRequestObjectResult>(await managerController.AssignCourtCase(
            courtCase.Id, new CasesV2Controller.AssignCourtCaseRequest { UserId = noAccess.Id }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await managerController.AssignCourtCase(
            hiddenCase.Id, new CasesV2Controller.AssignCourtCaseRequest { UserId = lawyer.Id }, CancellationToken.None));
        Assert.Equal(manager.Id, hiddenCase.ResponsibleUserId);
        Assert.IsType<OkObjectResult>(await managerController.AssignCourtCase(
            courtCase.Id, new CasesV2Controller.AssignCourtCaseRequest { UserId = lawyer.Id }, CancellationToken.None));
        Assert.Equal(lawyer.Id, courtCase.ResponsibleUserId);
        Assert.Contains("Ответственным по судебному делу назначен", courtCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TomorrowCourtHearings_UsesUserDateVisibilityAndFiveNearestHearings()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "tomorrow-lawyer@example.com", "Юрист");
        var otherLawyer = await CreateUserAsync(db, "tomorrow-other@example.com", "Другой юрист");
        db.UserSettings.Add(new UserSetting
        {
            UserId = lawyer.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Novosibirsk",
        });
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id };
        var firstInstant = new DateTime(2026, 9, 23, 17, 0, 0, DateTimeKind.Utc);
        var cases = Enumerable.Range(0, 7).Select(index => new ClientCase
        {
            Client = client,
            Title = $"Заседание {index}",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtName = "Районный суд",
            NextHearingAt = firstInstant.AddMinutes(index * 30),
        }).ToArray();
        var eventCase = new ClientCase
        {
            Client = client,
            Title = "Заседание по событию",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            Strategy = "Позиция проверена по материалам дела",
            PositionReviewedAt = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc),
            PositionReviewedByUserId = lawyer.Id,
            PositionReviewedByName = lawyer.FullName,
            NextHearingAt = firstInstant.AddMinutes(10),
            CourtEvents =
            [
                new ClientCaseCourtEvent
                {
                    EventType = "hearing",
                    ScheduledAt = firstInstant.AddMinutes(10),
                    CourtName = "Апелляционный суд",
                    JudgeName = "Судья события",
                },
            ],
        };
        var hiddenCase = new ClientCase
        {
            Client = new Client { Name = "Чужой клиент", IsActive = true, ResponsibleUserId = otherLawyer.Id },
            Title = "Чужое заседание",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherLawyer.Id,
            NextHearingAt = firstInstant.AddMinutes(5),
        };
        var outsideCase = new ClientCase
        {
            Client = client,
            Title = "Следующий день",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            NextHearingAt = firstInstant.AddDays(1),
        };
        db.ClientCases.AddRange(cases);
        db.ClientCases.AddRange(eventCase, hiddenCase, outsideCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientCaseId = eventCase.Id,
                Title = "Подготовиться к заседанию",
                ObjectName = eventCase.Title,
                Type = "courtHearing",
                Status = "done",
                DueAt = firstInstant.AddMinutes(10).AddDays(-3),
                TagsJson = $"[\"case-deadline\",\"case-deadline:court-event-{eventCase.CourtEvents.Single().Id}\"]",
            },
            new TaskJournalItem
            {
                ClientCaseId = cases[0].Id,
                Title = "Подготовиться к заседанию",
                ObjectName = cases[0].Title,
                Type = "courtHearing",
                Status = "done",
                DueAt = firstInstant.AddDays(-4),
                TagsJson = "[\"case-deadline\",\"case-deadline:hearing\"]",
            },
            new TaskJournalItem
            {
                ClientCaseId = cases[1].Id,
                Title = "Подготовиться к заседанию",
                ObjectName = cases[1].Title,
                Type = "courtHearing",
                Status = "done",
                DueAt = firstInstant.AddMinutes(30).AddDays(-3),
                TagsJson = "[\"case-deadline\",\"case-deadline:hearing\"]",
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, lawyer);
        var response = await controller.GetTomorrowCourtHearings(new DateOnly(2026, 9, 24), CancellationToken.None);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CourtTomorrowHearingDto>>(
            Assert.IsType<OkObjectResult>(response.Result).Value);

        Assert.Equal(5, rows.Count);
        Assert.Equal(cases[0].Id, rows[0].CaseId);
        Assert.Contains(rows, item => item.CaseId == eventCase.Id && item.CourtName == "Апелляционный суд" && item.JudgeName == "Судья события");
        Assert.True(rows.Single(item => item.CaseId == eventCase.Id).DocumentsPrepared);
        Assert.True(rows.Single(item => item.CaseId == eventCase.Id).PositionReviewed);
        Assert.False(rows.Single(item => item.CaseId == cases[0].Id).DocumentsPrepared);
        Assert.False(rows.Single(item => item.CaseId == cases[0].Id).PositionReviewed);
        Assert.True(rows.Single(item => item.CaseId == cases[1].Id).DocumentsPrepared);
        Assert.Equal(1, rows.Count(item => item.CaseId == eventCase.Id));
        Assert.DoesNotContain(rows, item => item.CaseId == hiddenCase.Id || item.CaseId == outsideCase.Id);

        await GrantSectionPermissionAsync(db, lawyer, RolePermissionCatalog.Cases, canView: false);
        var forbidden = await CreateController(db, lawyer).GetTomorrowCourtHearings(new DateOnly(2026, 9, 24), CancellationToken.None);
        Assert.IsType<ForbidResult>(forbidden.Result);
    }

    [Fact]
    public async Task TomorrowCourtHearings_IncludesAllDayAndExcludesCancelledOrClosedHearings()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "tomorrow-all-day@example.com", "Юрист");
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id };
        var localDate = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Unspecified);
        var allDayCase = new ClientCase
        {
            Client = client,
            Title = "Заседание на весь день",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtEvents = [new ClientCaseCourtEvent { EventType = "hearing", ScheduledAt = localDate, AllDay = true }],
        };
        var cancelledCase = new ClientCase
        {
            Client = client,
            Title = "Отменённое заседание",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtEvents = [new ClientCaseCourtEvent { EventType = "hearing", ScheduledAt = localDate, AllDay = true, MovementStatus = "cancelled" }],
        };
        var closedCase = new ClientCase
        {
            Client = client,
            Title = "Закрытое дело",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = lawyer.Id,
            CourtEvents = [new ClientCaseCourtEvent { EventType = "hearing", ScheduledAt = localDate, AllDay = true }],
        };
        db.ClientCases.AddRange(allDayCase, cancelledCase, closedCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientCaseId = allDayCase.Id,
            Title = "Подготовиться к заседанию",
            ObjectName = allDayCase.Title,
            Type = "courtHearing",
            Status = "done",
            DueAt = localDate.AddDays(-3).AddHours(9),
            TagsJson = $"[\"case-deadline\",\"case-deadline:court-event-{allDayCase.CourtEvents.Single().Id}\"]",
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, lawyer);
        var result = await controller.GetTomorrowCourtHearings(new DateOnly(2026, 9, 24), CancellationToken.None);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CourtTomorrowHearingDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(allDayCase.Id, Assert.Single(rows).CaseId);
        Assert.True(rows[0].AllDay);
        Assert.True(rows[0].DocumentsPrepared);

        var invalid = await CreateController(db, lawyer).GetTomorrowCourtHearings(DateOnly.MaxValue, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalid.Result);
    }

    [Fact]
    public async Task ReviewCasePosition_RequiresSavedPositionAndEditPermissionAndResetsAfterChange()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "position-lawyer@example.com", "Юрист");
        var viewer = await CreateUserAsync(db, "position-viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var outsider = await CreateUserAsync(db, "position-outsider@example.com", "Другой юрист", DataVisibilityScope.Own);
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Cases, canEdit: false);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с позицией", Status = ClientCaseStatus.Open, ResponsibleUserId = lawyer.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var lawyerController = CreateController(db, lawyer);
        Assert.IsType<ConflictObjectResult>((await lawyerController.ReviewCasePosition(clientCase.Id, CancellationToken.None)).Result);
        clientCase.Strategy = "Проверить доказательства и подготовить отзыв";
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await CreateController(db, viewer).ReviewCasePosition(clientCase.Id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await CreateController(db, outsider).ReviewCasePosition(clientCase.Id, CancellationToken.None)).Result);

        var first = Assert.IsType<CasesV2Controller.CasePositionReviewDto>(
            Assert.IsType<OkObjectResult>((await lawyerController.ReviewCasePosition(clientCase.Id, CancellationToken.None)).Result).Value);
        var repeated = Assert.IsType<CasesV2Controller.CasePositionReviewDto>(
            Assert.IsType<OkObjectResult>((await lawyerController.ReviewCasePosition(clientCase.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(first.PositionReviewedAt, repeated.PositionReviewedAt);
        Assert.Equal(lawyer.Id, first.PositionReviewedByUserId);
        Assert.Equal(lawyer.FullName, first.PositionReviewedByName);
        Assert.Equal(1, clientCase.EventHistory.Split("Позиция по делу проверена", StringSplitOptions.None).Length - 1);

        var update = await lawyerController.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, Strategy = "Обновлённая позиция",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(update);
        await db.Entry(clientCase).ReloadAsync();
        Assert.Null(clientCase.PositionReviewedAt);
        Assert.Null(clientCase.PositionReviewedByUserId);
        Assert.Equal(string.Empty, clientCase.PositionReviewedByName);

        clientCase.Status = ClientCaseStatus.Closed;
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>((await lawyerController.ReviewCasePosition(clientCase.Id, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ConfirmNextHearing_RequiresFutureSavedDateAndPermissionAndResetsAfterReschedule()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "hearing-lawyer@example.com", "Юрист");
        var viewer = await CreateUserAsync(db, "hearing-viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var outsider = await CreateUserAsync(db, "hearing-outsider@example.com", "Другой юрист", DataVisibilityScope.Own);
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Cases, canEdit: false);
        var nextHearing = DateTime.UtcNow.AddDays(7);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с заседанием", Status = ClientCaseStatus.Open, ResponsibleUserId = lawyer.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var controller = CreateController(db, lawyer);
        Assert.IsType<ConflictObjectResult>((await controller.ConfirmNextHearing(clientCase.Id, CancellationToken.None)).Result);
        clientCase.NextHearingAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>((await controller.ConfirmNextHearing(clientCase.Id, CancellationToken.None)).Result);
        clientCase.NextHearingAt = nextHearing;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await CreateController(db, viewer).ConfirmNextHearing(clientCase.Id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await CreateController(db, outsider).ConfirmNextHearing(clientCase.Id, CancellationToken.None)).Result);

        var first = Assert.IsType<CasesV2Controller.NextHearingConfirmationDto>(
            Assert.IsType<OkObjectResult>((await controller.ConfirmNextHearing(clientCase.Id, CancellationToken.None)).Result).Value);
        var repeated = Assert.IsType<CasesV2Controller.NextHearingConfirmationDto>(
            Assert.IsType<OkObjectResult>((await controller.ConfirmNextHearing(clientCase.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(nextHearing, first.NextHearingConfirmedForAt);
        Assert.Equal(first.NextHearingConfirmedAt, repeated.NextHearingConfirmedAt);
        Assert.Equal(lawyer.Id, first.NextHearingConfirmedByUserId);
        Assert.Equal(1, clientCase.EventHistory.Split("Дата следующего заседания проверена", StringSplitOptions.None).Length - 1);

        var sameDateUpdate = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, NextHearingAt = nextHearing,
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(sameDateUpdate);
        Assert.Equal(first.NextHearingConfirmedAt, clientCase.NextHearingConfirmedAt);

        var rescheduled = nextHearing.AddDays(2);
        var update = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, NextHearingAt = rescheduled,
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(update);
        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(rescheduled, clientCase.NextHearingAt);
        Assert.Null(clientCase.NextHearingConfirmedAt);
        Assert.Null(clientCase.NextHearingConfirmedForAt);

        clientCase.Status = ClientCaseStatus.Closed;
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>((await controller.ConfirmNextHearing(clientCase.Id, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ExpiringCaseAppeals_ReturnsOnlyVisibleUnresolvedDecisionsInThreeDayWindow()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "appeal-lawyer@example.test", "Юрист");
        var other = await CreateUserAsync(db, "appeal-other@example.test", "Другой юрист");
        var decisionAt = new DateTime(2026, 10, 1);
        var deadlineAt = new DateTime(2026, 10, 13);
        var visible = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Спор с решением",
            CaseNumber = "Д-10",
            CourtCaseNumber = "А55-10/2026",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt,
            CourtEvents =
            [
                new ClientCaseCourtEvent
                {
                    EventType = "decision",
                    Title = "Решение суда",
                    ScheduledAt = decisionAt,
                    ResultSummary = "Иск удовлетворён частично",
                },
            ],
        };
        var resolved = new ClientCase
        {
            Client = new Client { Name = "Разобранный", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Уже решено",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt,
            AppealDecisions =
            [
                new ClientCaseAppealDecision
                {
                    CourtDecisionDate = decisionAt,
                    AppealDeadlineDate = deadlineAt,
                    Choice = "decline",
                    DecidedByUserName = lawyer.FullName,
                },
            ],
        };
        var hidden = new ClientCase
        {
            Client = new Client { Name = "Чужой", IsActive = true, ResponsibleUserId = other.Id },
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = other.Id,
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt,
        };
        var late = new ClientCase
        {
            Client = new Client { Name = "Позднее", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Срок позже окна",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt.AddDays(1),
        };
        var alreadyTasked = new ClientCase
        {
            Client = new Client { Name = "Уже с задачей", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Жалоба уже готовится",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt,
        };
        db.ClientCases.AddRange(visible, resolved, hidden, late, alreadyTasked);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientCaseId = alreadyTasked.Id,
            ObjectName = alreadyTasked.Title,
            Title = "Подготовить апелляционную жалобу",
            DueAt = deadlineAt.AddDays(-7),
            Status = "inProgress",
            CreatedAt = decisionAt.AddDays(1),
        });
        await db.SaveChangesAsync();

        var response = await CreateController(db, lawyer).GetExpiringCaseAppeals(new DateOnly(2026, 10, 10), CancellationToken.None);
        var item = Assert.Single(Assert.IsType<List<CasesV2Controller.ExpiringCaseAppealDto>>(Assert.IsType<OkObjectResult>(response.Result).Value));
        Assert.Equal(visible.Id, item.CaseId);
        Assert.Equal("А55-10/2026", item.CaseNumber);
        Assert.Equal("Иск удовлетворён частично", item.DecisionSummary);
        Assert.Empty(Assert.IsType<List<CasesV2Controller.ExpiringCaseAppealDto>>(
            Assert.IsType<OkObjectResult>((await CreateController(db, lawyer).GetExpiringCaseAppeals(new DateOnly(2026, 10, 15), CancellationToken.None)).Result).Value));
    }

    [Fact]
    public async Task DecideCaseAppeal_CreatesOneFilingTaskAndKeepsHistoricalChoice()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "appeal-choice-lawyer@example.test", "Юрист");
        db.UserSettings.Add(new UserSetting
        {
            UserId = lawyer.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Novosibirsk",
        });
        await db.SaveChangesAsync();
        var decisionAt = DateTime.UtcNow.Date.AddDays(-1);
        var deadlineAt = DateTime.UtcNow.Date.AddDays(20);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Спор для обжалования",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var request = new CasesV2Controller.DecideCaseAppealRequest
        {
            CourtDecisionAt = clientCase.CourtDecisionAt.Value,
            AppealDeadlineAt = clientCase.AppealDeadlineAt.Value,
            Choice = "appeal",
            Comment = "Подготовить жалобу",
        };

        var response = await CreateController(db, lawyer).DecideCaseAppeal(clientCase.Id, request, CancellationToken.None);
        var result = Assert.IsType<CasesV2Controller.CaseAppealDecisionDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        var task = await db.TaskJournalItems.Include(item => item.Participants).SingleAsync(item => item.Id == result.TaskId);
        var decision = await db.ClientCaseAppealDecisions.SingleAsync();
        Assert.Equal("appeal", decision.Choice);
        Assert.Equal("Подготовить жалобу", decision.Comment);
        Assert.Equal(CalendarTimeZoneHelper.FromUserLocalToUtc(deadlineAt.AddDays(-7).AddHours(9),
            CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk")), task.DueAt);
        Assert.Equal("Подготовить апелляционную жалобу", task.Title);
        Assert.Equal(lawyer.Id, task.AssigneeUserId);
        Assert.Contains(task.Participants, item => item.Role == "assignee" && item.UserId == lawyer.Id);
        Assert.Contains($"case-appeal-filing:{decisionAt:yyyyMMdd}", task.TagsJson, StringComparison.Ordinal);
        var currentDecision = await CreateController(db, lawyer).GetCurrentCaseAppealDecision(clientCase.Id, CancellationToken.None);
        Assert.Equal(result.Id, Assert.IsType<CasesV2Controller.CaseAppealDecisionDto>(Assert.IsType<OkObjectResult>(currentDecision.Result).Value).Id);
        Assert.Empty(Assert.IsType<List<CasesV2Controller.ExpiringCaseAppealDto>>(
            Assert.IsType<OkObjectResult>((await CreateController(db, lawyer).GetExpiringCaseAppeals(DateOnly.FromDateTime(deadlineAt.AddDays(-3)), CancellationToken.None)).Result).Value));

        var duplicate = await CreateController(db, lawyer).DecideCaseAppeal(clientCase.Id, request, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicate.Result);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id && item.TagsJson.Contains("case-appeal-filing")));

        clientCase.CourtDecisionAt = decisionAt.AddDays(1);
        clientCase.AppealDeadlineAt = deadlineAt.AddDays(30);
        await db.SaveChangesAsync();
        var nextWindow = await CreateController(db, lawyer).GetExpiringCaseAppeals(
            DateOnly.FromDateTime(clientCase.AppealDeadlineAt.Value.AddDays(-3)), CancellationToken.None);
        Assert.Single(Assert.IsType<List<CasesV2Controller.ExpiringCaseAppealDto>>(
            Assert.IsType<OkObjectResult>(nextWindow.Result).Value));
        var secondResponse = await CreateController(db, lawyer).DecideCaseAppeal(clientCase.Id, new CasesV2Controller.DecideCaseAppealRequest
        {
            CourtDecisionAt = clientCase.CourtDecisionAt.Value,
            AppealDeadlineAt = clientCase.AppealDeadlineAt.Value,
            Choice = "appeal",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(secondResponse.Result);
        Assert.Equal(2, await db.ClientCaseAppealDecisions.CountAsync());
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id && item.TagsJson.Contains("case-appeal-filing")));
    }

    [Fact]
    public async Task DecideCaseAppeal_LateChoiceCreatesUrgentTaskWithoutPastWorkDeadline()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "appeal-late-choice@example.test", "Юрист");
        var decisionAt = DateTime.UtcNow.Date.AddDays(-1);
        var deadlineAt = DateTime.UtcNow.Date.AddDays(2);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Позднее решение обжаловать", Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, CourtDecisionAt = decisionAt, AppealDeadlineAt = deadlineAt,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var response = await CreateController(db, lawyer).DecideCaseAppeal(clientCase.Id, new CasesV2Controller.DecideCaseAppealRequest
        {
            CourtDecisionAt = decisionAt, AppealDeadlineAt = deadlineAt, Choice = "appeal",
        }, CancellationToken.None);
        var result = Assert.IsType<CasesV2Controller.CaseAppealDecisionDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        var task = await db.TaskJournalItems.SingleAsync(item => item.Id == result.TaskId);
        Assert.True(task.DueAt > before);
        var legalDueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(deadlineAt.AddHours(18),
            CalendarTimeZoneHelper.ResolveTimeZone(CalendarTimeZoneHelper.DefaultTimeZoneId));
        Assert.True(task.DueAt <= legalDueAt || task.DueAt <= before.AddMinutes(16));
        Assert.Equal("urgent", task.Priority);
    }

    [Fact]
    public async Task GetCurrentCaseAppealDecision_HidesOtherCasesAndOldJudicialActs()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "appeal-visible@example.test", "Юрист", DataVisibilityScope.Own);
        var outsider = await CreateUserAsync(db, "appeal-hidden@example.test", "Другой юрист", DataVisibilityScope.Own);
        var viewer = await CreateUserAsync(db, "appeal-no-view@example.test", "Без просмотра", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Cases, canView: false);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с новым актом", Status = ClientCaseStatus.Open, ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = DateTime.UtcNow.Date.AddDays(-1),
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.ClientCaseAppealDecisions.Add(new ClientCaseAppealDecision
        {
            ClientCaseId = clientCase.Id,
            CourtDecisionDate = clientCase.CourtDecisionAt.Value.AddDays(-3),
            AppealDeadlineDate = DateTime.UtcNow.Date.AddDays(3),
            Choice = "appeal", DecidedByUserName = "Юрист",
        });
        await db.SaveChangesAsync();

        Assert.Null(Assert.IsType<OkObjectResult>((await CreateController(db, lawyer).GetCurrentCaseAppealDecision(clientCase.Id, CancellationToken.None)).Result).Value);
        Assert.IsType<NotFoundResult>((await CreateController(db, outsider).GetCurrentCaseAppealDecision(clientCase.Id, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await CreateController(db, viewer).GetCurrentCaseAppealDecision(clientCase.Id, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task DecideCaseAppeal_RejectsStaleDatesDeclineAndMissingEditPermission()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "appeal-protection-lawyer@example.test", "Юрист");
        var viewer = await CreateUserAsync(db, "appeal-protection-viewer@example.test", "Наблюдатель", DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Cases, canEdit: false);
        var decisionAt = DateTime.UtcNow.Date.AddDays(-1);
        var deadlineAt = DateTime.UtcNow.Date.AddDays(20);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с проверкой доступа",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var request = new CasesV2Controller.DecideCaseAppealRequest
        {
            CourtDecisionAt = decisionAt,
            AppealDeadlineAt = deadlineAt.AddDays(-1),
            Choice = "appeal",
        };

        Assert.IsType<ForbidResult>((await CreateController(db, viewer).DecideCaseAppeal(clientCase.Id, request, CancellationToken.None)).Result);
        Assert.IsType<ConflictObjectResult>((await CreateController(db, lawyer).DecideCaseAppeal(clientCase.Id, request, CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await CreateController(db, lawyer).DecideCaseAppeal(clientCase.Id, new CasesV2Controller.DecideCaseAppealRequest
        {
            CourtDecisionAt = clientCase.CourtDecisionAt.Value,
            AppealDeadlineAt = clientCase.AppealDeadlineAt.Value,
            Choice = "decline",
        }, CancellationToken.None)).Result);
        Assert.Empty(await db.ClientCaseAppealDecisions.ToListAsync());
        Assert.Empty(await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id && item.TagsJson.Contains("case-appeal-filing")).ToListAsync());
    }

    [Fact]
    public async Task RemindCaseAppealAssignee_SendsOnceToVisibleLawyer()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db, "appeal-reminder-manager@example.test", "Руководитель", DataVisibilityScope.All);
        var lawyer = await CreateUserAsync(db, "appeal-reminder-lawyer@example.test", "Юрист");
        var today = DateTime.UtcNow.Date;
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Доверитель", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с напоминанием",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtDecisionAt = today.AddDays(-20),
            AppealDeadlineAt = today.AddDays(2),
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var request = new CasesV2Controller.DecideCaseAppealRequest
        {
            CourtDecisionAt = clientCase.CourtDecisionAt.Value,
            AppealDeadlineAt = clientCase.AppealDeadlineAt.Value,
        };
        var controller = CreateController(db, manager, notificationService: CreateNotificationService(db));

        var first = await controller.RemindCaseAppealAssignee(clientCase.Id, request, CancellationToken.None);
        var firstResult = Assert.IsType<CasesV2Controller.CaseAppealReminderDto>(Assert.IsType<OkObjectResult>(first.Result).Value);
        Assert.True(firstResult.Sent);
        Assert.Equal(lawyer.Id, firstResult.RecipientUserId);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(lawyer.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CaseProceduralDeadline, notification.Type);
        Assert.Contains("Срок обжалования истекает", notification.Title, StringComparison.Ordinal);

        var second = await CreateController(db, manager, notificationService: CreateNotificationService(db))
            .RemindCaseAppealAssignee(clientCase.Id, request, CancellationToken.None);
        Assert.False(Assert.IsType<CasesV2Controller.CaseAppealReminderDto>(Assert.IsType<OkObjectResult>(second.Result).Value).Sent);
        Assert.Single(await db.UserNotifications.ToListAsync());
        Assert.IsType<BadRequestObjectResult>((await CreateController(db, lawyer)
            .RemindCaseAppealAssignee(clientCase.Id, request, CancellationToken.None)).Result);
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
        Assert.Equal("Изучить решение, оценить обжалование", appealTask.Title);
        Assert.Contains("Решение вынесено 16.05.2026", appealTask.Description, StringComparison.Ordinal);
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
    public async Task DecisionAppealTask_UsesProcessDeadlineButPreservesCustomTitle()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "decision-appeal@example.com", "Юрист");
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = owner.Id };
        var decisionAt = new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc);
        var deadlineAt = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc);
        var normalCase = new ClientCase
        {
            Client = client,
            Title = "Решение суда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CourtDecisionAt = decisionAt,
            ProceduralDeadlines =
            [
                new ClientCaseProceduralDeadline
                {
                    DeadlineType = "appeal", Title = "Срок апелляции", DueAt = deadlineAt,
                },
            ],
        };
        var customCase = new ClientCase
        {
            Client = client,
            Title = "Особый срок",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CourtDecisionAt = decisionAt,
            ProceduralDeadlines =
            [
                new ClientCaseProceduralDeadline
                {
                    DeadlineType = "appeal", Title = "Проверить судебный акт", DueAt = deadlineAt,
                },
            ],
        };
        var staleCase = new ClientCase
        {
            Client = client,
            Title = "Старый срок апелляции",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CourtDecisionAt = decisionAt.AddMonths(2),
            ProceduralDeadlines =
            [
                new ClientCaseProceduralDeadline
                {
                    DeadlineType = "appeal", Title = "Срок апелляции", DueAt = deadlineAt,
                },
            ],
        };
        db.ClientCases.AddRange(normalCase, customCase, staleCase);
        await db.SaveChangesAsync();

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, normalCase.Id, owner.Id, owner.FullName, CancellationToken.None);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, customCase.Id, owner.Id, owner.FullName, CancellationToken.None);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, staleCase.Id, owner.Id, owner.FullName, CancellationToken.None);

        var normalTask = Assert.Single(await db.TaskJournalItems.Where(item => item.ClientCaseId == normalCase.Id).ToListAsync());
        var customTask = Assert.Single(await db.TaskJournalItems.Where(item => item.ClientCaseId == customCase.Id).ToListAsync());
        var staleTask = Assert.Single(await db.TaskJournalItems.Where(item => item.ClientCaseId == staleCase.Id).ToListAsync());
        Assert.Equal("Изучить решение, оценить обжалование", normalTask.Title);
        Assert.Equal(deadlineAt.AddHours(9), normalTask.DueAt);
        Assert.Equal("Проверить судебный акт", customTask.Title);
        Assert.Equal("Срок апелляции", staleTask.Title);

        normalTask.Status = "done";
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, normalCase.Id, owner.Id, owner.FullName, CancellationToken.None);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == normalCase.Id));

        normalCase.CourtDecisionAt = decisionAt.AddDays(1);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, normalCase.Id, owner.Id, owner.FullName, CancellationToken.None);
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == normalCase.Id));
    }

    [Fact]
    public async Task AppealDecision_ChangesCassationDeadlineTaskWithoutDuplicatesOrOverwritingCustomTitle()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "cassation-review@example.com", "Юрист");
        var deadlineAt = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Решение апелляции",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            ProceduralDeadlines =
            [
                new ClientCaseProceduralDeadline
                {
                    DeadlineType = "cassation", Title = "Срок кассации", DueAt = deadlineAt,
                },
            ],
        };
        var customCase = new ClientCase
        {
            Client = client,
            Title = "Особый срок кассации",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            ProceduralDeadlines =
            [
                new ClientCaseProceduralDeadline
                {
                    DeadlineType = "cassation", Title = "Проверить материалы кассации", DueAt = deadlineAt,
                },
            ],
            CourtEvents =
            [
                new ClientCaseCourtEvent
                {
                    EventType = "decision", CourtInstance = "appeal", MovementStatus = "completed",
                    Title = "Постановление апелляции", ScheduledAt = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
                },
            ],
        };
        var futureCase = new ClientCase
        {
            Client = client,
            Title = "Будущее решение апелляции",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CassationDeadlineAt = DateTime.UtcNow.Date.AddDays(30),
            CourtEvents =
            [
                new ClientCaseCourtEvent
                {
                    EventType = "decision", CourtInstance = "appeal", MovementStatus = "scheduled",
                    Title = "Ожидаемое постановление", ScheduledAt = DateTime.UtcNow.Date.AddDays(2),
                },
            ],
        };
        db.ClientCases.AddRange(clientCase, customCase, futureCase);
        await db.SaveChangesAsync();

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("Срок кассации", task.Title);

        var appealDecision = new ClientCaseCourtEvent
        {
            EventType = "decision", CourtInstance = "appeal", MovementStatus = "completed",
            Title = "Постановление апелляции", ScheduledAt = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
        };
        clientCase.CourtEvents.Add(appealDecision);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal("Оценить кассацию, подготовить жалобу", task.Title);
        Assert.Equal(deadlineAt.AddHours(9), task.DueAt);
        Assert.Equal(lawyer.Id, task.AssigneeUserId);
        Assert.Contains("Решение апелляции вынесено 10.09.2026", task.Description);

        task.Status = "done";
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        appealDecision.ScheduledAt = appealDecision.ScheduledAt.AddDays(1);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var tasks = await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.Equal("done", tasks[0].Status);
        Assert.Contains("Решение апелляции вынесено 11.09.2026", tasks[1].Description);

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, customCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var customTask = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == customCase.Id);
        Assert.Equal("Проверить материалы кассации", customTask.Title);

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, futureCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var futureTask = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == futureCase.Id);
        Assert.Equal("Срок кассации", futureTask.Title);
    }

    [Fact]
    public async Task Update_PersistsManuallyConfirmedWritDates()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "writ-dates-lawyer@example.com", "Юрист");
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Исполнительный лист", Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var effectiveAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var receivedAt = effectiveAt.AddDays(2);
        var controller = CreateController(db, lawyer);

        var result = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, CourtDecisionEffectiveAt = effectiveAt,
            ExecutiveWritReceivedAt = receivedAt,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(effectiveAt, clientCase.CourtDecisionEffectiveAt);
        Assert.Equal(receivedAt, clientCase.ExecutiveWritReceivedAt);
        var details = Assert.IsType<OkObjectResult>(await controller.GetById(clientCase.Id, CancellationToken.None));
        var detailedCase = Assert.IsType<ClientCase>(details.Value);
        Assert.Equal(effectiveAt, detailedCase.CourtDecisionEffectiveAt);
        Assert.Equal(receivedAt, detailedCase.ExecutiveWritReceivedAt);
    }

    [Fact]
    public async Task WritMilestones_CreateCompleteAndRescheduleLinkedTasksWithoutDuplicates()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "writ-workflow-lawyer@example.com", "Юрист");
        var effectiveAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Исполнительный лист", Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, CourtDecisionEffectiveAt = effectiveAt,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var obtain = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("Получить исполнительный лист", obtain.Title);
        Assert.Equal(effectiveAt.AddDays(7).AddHours(9), obtain.DueAt);
        Assert.Equal(lawyer.Id, obtain.AssigneeUserId);
        Assert.Contains("не процессуальный срок", obtain.Description);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        clientCase.ExecutiveWritReceivedAt = effectiveAt.AddDays(2);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal("done", obtain.Status);
        Assert.Contains("получен", obtain.Result);
        var submit = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id && item.Title == "Подать исполнительный лист в ФССП или банк");
        Assert.Equal(effectiveAt.AddDays(7).AddHours(9), submit.DueAt);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        submit.Status = "done";
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        clientCase.ExecutiveWritReceivedAt = effectiveAt.AddDays(3);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var submitTasks = await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id && item.Title == "Подать исполнительный лист в ФССП или банк").OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, submitTasks.Count);
        Assert.Equal("done", submitTasks[0].Status);
        Assert.Equal(effectiveAt.AddDays(8).AddHours(9), submitTasks[1].DueAt);

        clientCase.ExecutiveWritReceivedAt = null;
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal("cancelled", submitTasks[1].Status);
        var obtainTasks = await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id && item.Title == "Получить исполнительный лист").OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, obtainTasks.Count);
        Assert.Equal("done", obtainTasks[0].Status);
        Assert.Equal("new", obtainTasks[1].Status);
    }

    [Fact]
    public async Task WritSubmission_CompletesLinkedTaskAndRestoresItWhenMarkIsCleared()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "writ-submit-lawyer@example.com", "Юрист");
        var receivedAt = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Подача листа", Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, ExecutiveWritReceivedAt = receivedAt,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("new", task.Status);

        var controller = CreateController(db, lawyer);
        var submittedAt = receivedAt.AddDays(1);
        var saved = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, ExecutiveWritReceivedAt = receivedAt,
            ExecutiveWritSubmittedAt = submittedAt, ExecutiveWritSubmittedTo = "bank",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(saved);
        Assert.Equal("done", task.Status);
        Assert.Contains("банк", task.Result);
        Assert.Equal(submittedAt, clientCase.ExecutiveWritSubmittedAt);
        Assert.Equal("bank", clientCase.ExecutiveWritSubmittedTo);
        Assert.Contains("Исполнительный лист подан", clientCase.EventHistory);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        var correctedAt = submittedAt.AddDays(1);
        var corrected = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, ExecutiveWritReceivedAt = receivedAt,
            ExecutiveWritSubmittedAt = correctedAt, ExecutiveWritSubmittedTo = "fssp",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(corrected);
        Assert.Contains("ФССП", task.Result);
        Assert.Contains("05.10.2026", task.Result);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        var legacyUpdate = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(legacyUpdate);
        Assert.Equal(correctedAt, clientCase.ExecutiveWritSubmittedAt);
        Assert.Equal("fssp", clientCase.ExecutiveWritSubmittedTo);
        Assert.Equal(receivedAt, clientCase.ExecutiveWritReceivedAt);

        var cleared = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, ExecutiveWritReceivedAt = receivedAt,
            ClearExecutiveWritSubmission = true,
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(cleared);
        Assert.Null(clientCase.ExecutiveWritSubmittedAt);
        Assert.Null(clientCase.ExecutiveWritSubmittedTo);
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));
        Assert.Equal("new", (await db.TaskJournalItems.OrderByDescending(item => item.Id).FirstAsync(item => item.ClientCaseId == clientCase.Id)).Status);
    }

    [Fact]
    public async Task WritSubmission_RejectsMissingDestinationAndDateBeforeReceipt()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "writ-validation-lawyer@example.com", "Юрист");
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Подача листа", Status = ClientCaseStatus.Open, ResponsibleUserId = lawyer.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, lawyer);
        var receivedAt = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var model = new ClientCase
        {
            ClientId = clientCase.ClientId, Title = clientCase.Title, Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id, ExecutiveWritReceivedAt = receivedAt,
            ExecutiveWritSubmittedAt = receivedAt.AddDays(-1), ExecutiveWritSubmittedTo = "fssp",
        };
        Assert.IsType<BadRequestObjectResult>(await controller.Update(clientCase.Id, model, CancellationToken.None));
        model.ExecutiveWritSubmittedAt = receivedAt;
        model.ExecutiveWritSubmittedTo = null;
        Assert.IsType<BadRequestObjectResult>(await controller.Update(clientCase.Id, model, CancellationToken.None));
        model.ExecutiveWritSubmittedTo = "fssp";
        model.ExecutiveWritReceivedAt = null;
        Assert.IsType<BadRequestObjectResult>(await controller.Update(clientCase.Id, model, CancellationToken.None));
        Assert.Null(clientCase.ExecutiveWritSubmittedAt);
        Assert.Empty(await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).ToListAsync());
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
            ExecutiveWritReceivedAt = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc),
            ExecutiveWritSubmittedAt = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ExecutiveWritSubmittedTo = "bank",
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
        Assert.Contains(events, item => item.SourceType == "writ-submitted" && item.Title == "Исполнительный лист подан в банк" && item.ClientCaseId == clientCase.Id);
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
        Assert.Equal(5, summary.TotalEventCount);
        Assert.Equal(3, summary.CourtEventCount);
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
        Assert.Contains("SUMMARY:Исполнительный лист подан в банк", ics, StringComparison.Ordinal);
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
        Assert.Equal(5, metadata.RootElement.GetProperty("exportedEventCount").GetInt32());
        Assert.Equal(120, metadata.RootElement.GetProperty("reminderMinutes")[0].GetInt32());
        Assert.Equal(30, metadata.RootElement.GetProperty("reminderMinutes")[1].GetInt32());
        Assert.True(metadata.RootElement.GetProperty("filters").GetProperty("includeTasks").GetBoolean());
    }

    [Fact]
    public async Task CaseCalendarEvents_MarksOnlyTheMatchingCompletedHearingPreparation()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "hearing-calendar@example.com", "Юрист");
        var hearingAt = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = owner.Id },
            Title = "Дело с двумя заседаниями",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CourtEvents =
            [
                new ClientCaseCourtEvent { EventType = "hearing", ScheduledAt = hearingAt, Title = "Первое заседание" },
                new ClientCaseCourtEvent { EventType = "hearing", ScheduledAt = hearingAt.AddDays(7), Title = "Второе заседание" },
            ],
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var firstEventId = clientCase.CourtEvents.OrderBy(item => item.ScheduledAt).First().Id;
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientCaseId = clientCase.Id,
                Title = "Подготовка первого заседания",
                ObjectName = clientCase.Title,
                Type = "courtHearing",
                Status = "done",
                DueAt = hearingAt.AddDays(-3),
                TagsJson = $"[\"case-deadline:court-event-{firstEventId}\"]",
            },
            new TaskJournalItem
            {
                ClientCaseId = clientCase.Id,
                Title = "Подготовка второго заседания не завершена",
                ObjectName = clientCase.Title,
                Type = "courtHearing",
                Status = "inProgress",
                DueAt = hearingAt.AddDays(4),
                TagsJson = $"[\"case-deadline:court-event-{clientCase.CourtEvents.OrderBy(item => item.ScheduledAt).Last().Id}\"]",
            });
        await db.SaveChangesAsync();

        var result = await CreateController(db, owner).GetCaseCalendarEvents(
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 31),
            clientCaseId: null, responsibleUserId: null, includeTasks: false, ct: CancellationToken.None);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var hearings = events.Where(item => item.SourceType == "hearing").OrderBy(item => item.StartAt).ToArray();
        Assert.Equal(2, hearings.Length);
        Assert.True(hearings[0].DocumentsPrepared);
        Assert.False(hearings[1].DocumentsPrepared);
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
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });
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
                DueAt = new DateTime(2026, 6, 24, 22, 30, 0, DateTimeKind.Utc),
                AllDay = false,
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
        Assert.Contains("25.06.2026 02:30", createdNotification.BodyPreview, StringComparison.Ordinal);

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
    public async Task CreateProceduralDeadline_UsesNotificationServiceForResponsibleUser()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "deadline-service-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "deadline-service-responsible@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Клиент по процессуальному сроку",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для уведомления о сроке",
            CaseNumber = "CASE-DEADLINE-SERVICE-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner, notificationService: CreateNotificationService(db));

        var createResult = await controller.CreateProceduralDeadline(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest
            {
                DeadlineType = "appeal",
                Title = "Подать апелляцию",
                DueAt = new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc),
                AllDay = false,
                WarningDaysBefore = 3,
                Notes = "Проверить комплект документов",
            },
            CancellationToken.None);

        var created = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);
        var notification = await db.UserNotifications.SingleAsync(item =>
            item.Type == UserNotificationTypes.CaseProceduralDeadline);
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("CASE-DEADLINE-SERVICE-1", notification.Title);
        Assert.Contains("Подать апелляцию", notification.BodyPreview);
        Assert.Contains($"\"proceduralDeadlineId\":{created.Id}", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"created\"", notification.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCourtEvent_UsesNotificationServiceForResponsibleCourtHearing()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-service-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-service-responsible@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Клиент по суду",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для уведомления о заседании",
            CaseNumber = "CASE-HEARING-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            CourtName = "Арбитражный суд",
            CourtCaseNumber = "А55-200/2026",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner, notificationService: CreateNotificationService(db));

        var createResult = await controller.CreateCourtEvent(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseCourtEventRequest
            {
                EventType = "hearing",
                Title = "Судебное заседание",
                ScheduledAt = new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Utc),
                AllDay = false,
                CourtInstance = "first",
                MovementStatus = "scheduled",
            },
            CancellationToken.None);

        var created = Assert.IsType<CasesV2Controller.ClientCaseCourtEventDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CourtHearing, notification.Type);
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("CASE-HEARING-1", notification.Title);
        Assert.Contains("Судебное заседание", notification.BodyPreview);
        Assert.Contains("Дата: 10.07.2026 12:30", notification.BodyPreview);
        Assert.Contains($"\"courtEventId\":{created.Id}", notification.MetadataJson, StringComparison.Ordinal);

        var hearingTask = await db.TaskJournalItems.SingleAsync(item =>
            item.ClientCaseId == clientCase.Id && item.TagsJson.Contains($"case-deadline:court-event-{created.Id}"));
        Assert.Equal("courtHearing", hearingTask.Type);
        Assert.Equal(responsible.Id, hearingTask.AssigneeUserId);
        Assert.Equal(new DateTime(2026, 7, 7, 9, 30, 0, DateTimeKind.Utc), hearingTask.DueAt);
        Assert.Equal("Подготовиться к заседанию", hearingTask.Title);
        Assert.Contains("Заседание назначено на 10.07.2026 09:30", hearingTask.Description);

        var newHearingAt = new DateTime(2026, 7, 12, 9, 30, 0, DateTimeKind.Utc);
        var updateResult = await controller.UpdateCourtEvent(
            clientCase.Id,
            created.Id,
            new CasesV2Controller.UpsertClientCaseCourtEventRequest
            {
                EventType = "hearing",
                Title = "Судебное заседание",
                ScheduledAt = newHearingAt,
                AllDay = false,
                CourtInstance = "first",
                MovementStatus = "scheduled",
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResult.Result);
        var rescheduled = await db.UserNotifications.SingleAsync(item => item.Id != notification.Id);
        Assert.Equal(UserNotificationTypes.CourtHearing, rescheduled.Type);
        Assert.Equal(responsible.Id, rescheduled.RecipientUserId);
        Assert.StartsWith("Перенесено судебное заседание:", rescheduled.Title, StringComparison.Ordinal);
        Assert.Contains("Новая дата: 12.07.2026 12:30", rescheduled.BodyPreview);
        Assert.Contains("\"action\":\"rescheduled\"", rescheduled.MetadataJson, StringComparison.Ordinal);
        Assert.Equal(newHearingAt.AddDays(-3), hearingTask.DueAt);
        Assert.Equal("Учесть новую дату, проверить готовность", hearingTask.Title);
    }

    [Fact]
    public async Task HearingPreparationTask_DoesNotRecreateCompletedTaskUntilHearingMoves()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "hearing-preparation@example.com", "Юрист");
        var hearing = new ClientCaseCourtEvent
        {
            EventType = "hearing",
            Title = "Предварительное заседание",
            ScheduledAt = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc),
        };
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с подготовкой",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtEvents = [hearing],
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var preparation = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal(new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc), preparation.DueAt);
        Assert.Equal("Подготовиться к заседанию", preparation.Title);
        preparation.Status = "done";
        await db.SaveChangesAsync();

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        hearing.ScheduledAt = hearing.ScheduledAt.AddDays(7);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var tasks = await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.Equal("done", tasks[0].Status);
        Assert.Equal(new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc), tasks[1].DueAt);
        Assert.Equal("Учесть новую дату, проверить готовность", tasks[1].Title);

        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal("Учесть новую дату, проверить готовность", tasks[1].Title);
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        hearing.ScheduledAt = hearing.ScheduledAt.AddDays(1);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc), tasks[1].DueAt);
        Assert.Equal("Учесть новую дату, проверить готовность", tasks[1].Title);
    }

    [Fact]
    public async Task RecordHearingOutcome_SavesManualResultAndClosesPreparationTaskWithoutDuplicates()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "hearing-outcome-lawyer@example.com", "Юрист");
        var hearing = new ClientCaseCourtEvent
        {
            EventType = "hearing",
            Title = "Заседание",
            ScheduledAt = DateTime.UtcNow.AddDays(-1),
            MovementStatus = "scheduled",
            ResultSummary = "Данные суда",
        };
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с результатом заседания",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtEvents = [hearing],
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        var preparation = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        var controller = CreateController(db, lawyer);

        var result = await controller.RecordHearingOutcome(clientCase.Id, hearing.Id,
            new CasesV2Controller.RecordHearingOutcomeRequest { Outcome = "considered", Note = "Решение оглашено" },
            CancellationToken.None);
        var saved = Assert.IsType<CasesV2Controller.ClientCaseCourtEventDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("considered", saved.HearingOutcome);
        Assert.Equal("Решение оглашено", saved.HearingOutcomeNote);
        Assert.Equal(lawyer.Id, saved.HearingOutcomeRecordedByUserId);
        Assert.Equal("Данные суда", hearing.ResultSummary);
        Assert.Equal("cancelled", preparation.Status);
        Assert.Single(await db.ClientCaseCourtEventHistory.Where(item => item.ClientCaseCourtEventId == hearing.Id && item.Action == "hearing-outcome").ToListAsync());

        var repeated = await controller.RecordHearingOutcome(clientCase.Id, hearing.Id,
            new CasesV2Controller.RecordHearingOutcomeRequest { Outcome = "considered", Note = "Решение оглашено" },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(repeated.Result);
        Assert.Single(await db.ClientCaseCourtEventHistory.Where(item => item.ClientCaseCourtEventId == hearing.Id && item.Action == "hearing-outcome").ToListAsync());
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Single(await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).ToListAsync());
    }

    [Fact]
    public async Task RecordHearingOutcome_RejectsFutureAndNonHearingEvents()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "hearing-outcome-invalid@example.com", "Юрист");
        var futureHearing = new ClientCaseCourtEvent { EventType = "hearing", Title = "Будущее заседание", ScheduledAt = DateTime.UtcNow.AddDays(1) };
        var decision = new ClientCaseCourtEvent { EventType = "decision", Title = "Решение", ScheduledAt = DateTime.UtcNow.AddDays(-1) };
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Проверка результата",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtEvents = [futureHearing, decision],
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, lawyer);
        var request = new CasesV2Controller.RecordHearingOutcomeRequest { Outcome = "postponed" };

        Assert.IsType<ConflictObjectResult>((await controller.RecordHearingOutcome(clientCase.Id, futureHearing.Id, request, CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.RecordHearingOutcome(clientCase.Id, decision.Id, request, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.RecordHearingOutcome(clientCase.Id, -1, request, CancellationToken.None)).Result);
        Assert.Null(futureHearing.HearingOutcome);
        Assert.Null(decision.HearingOutcome);
    }

    [Fact]
    public async Task RecordHearingOutcome_RequiresEditPermissionAndCaseVisibility()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "hearing-outcome-viewer@example.com", "Наблюдатель");
        var limited = await CreateUserAsync(db, "hearing-outcome-limited@example.com", "Ограниченный");
        var owner = await CreateUserAsync(db, "hearing-outcome-owner@example.com", "Владелец");
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Cases, canEdit: false);
        await GrantSectionPermissionAsync(db, limited, RolePermissionCatalog.Cases, viewScope: DataVisibilityScope.Own);
        var hearing = new ClientCaseCourtEvent { EventType = "hearing", Title = "Заседание", ScheduledAt = DateTime.UtcNow.AddDays(-1) };
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = owner.Id },
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CourtEvents = [hearing],
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var request = new CasesV2Controller.RecordHearingOutcomeRequest { Outcome = "considered" };

        Assert.IsType<ForbidResult>((await CreateController(db, viewer).RecordHearingOutcome(clientCase.Id, hearing.Id, request, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await CreateController(db, limited).RecordHearingOutcome(clientCase.Id, hearing.Id, request, CancellationToken.None)).Result);
        Assert.Null(hearing.HearingOutcome);
    }

    [Fact]
    public async Task CourtRegistry_ShowsOutcomeOnlyForItsDisplayedHearing()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "court-registry-outcome@example.com", "Юрист");
        var displayedAt = DateTime.UtcNow.AddDays(-2);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с прошедшим заседанием",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
            CourtName = "Районный суд",
            NextHearingAt = displayedAt,
            CourtEvents =
            [
                new ClientCaseCourtEvent { EventType = "hearing", Title = "Другое заседание", ScheduledAt = displayedAt.AddDays(-7), HearingOutcome = "considered" },
                new ClientCaseCourtEvent { EventType = "hearing", Title = "Отображаемое заседание", ScheduledAt = displayedAt, HearingOutcome = "postponed", HearingOutcomeRecordedAt = DateTime.UtcNow },
            ],
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var response = await CreateController(db, lawyer).Get(
            clientId: null, legalEntityId: null, status: null, search: null,
            responsibleUserId: null, caseType: null, priority: null, from: null, to: null,
            minFee: null, maxFee: null, sortBy: null, sortDir: null,
            courtOnly: true, ct: CancellationToken.None);
        var item = Assert.Single(GetAnonymousItems(Assert.IsType<OkObjectResult>(response).Value));
        Assert.Equal("postponed", item.NextHearingOutcome);
    }

    [Fact]
    public async Task CourtEvents_CreateShowInCalendarAndCanLinkDeadline()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-owner@example.com", "Владелец");
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });
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
                ScheduledAt = new DateTime(2026, 7, 9, 22, 30, 0, DateTimeKind.Utc),
                AllDay = false,
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
        Assert.Contains("10.07.2026 02:30", notification.BodyPreview, StringComparison.Ordinal);
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
    public async Task UpdateProceduralDeadline_MovesClosesReopensAndRebindsCourtEvent()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "deadline-update-owner@example.com", "Владелец срока");
        var client = new Client
        {
            Name = "Клиент с изменяемым сроком",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с переносом срока",
            Status = ClientCaseStatus.Open,
            Priority = "normal",
            ResponsibleUserId = owner.Id,
        };
        var firstCourtEvent = new ClientCaseCourtEvent
        {
            ClientCase = clientCase,
            EventType = "hearing",
            Title = "Первое заседание",
            ScheduledAt = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
            CourtInstance = "first",
            MovementStatus = "scheduled",
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
        };
        var secondCourtEvent = new ClientCaseCourtEvent
        {
            ClientCase = clientCase,
            EventType = "hearing",
            Title = "Повторное заседание",
            ScheduledAt = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc),
            CourtInstance = "first",
            MovementStatus = "scheduled",
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
        };
        db.Clients.Add(client);
        db.ClientCaseCourtEvents.AddRange(firstCourtEvent, secondCourtEvent);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createResult = await controller.CreateProceduralDeadline(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest
            {
                DeadlineType = "motion",
                Title = "Подать позицию",
                DueAt = new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
                SourceType = "court-event",
                SourceId = firstCourtEvent.Id,
            },
            CancellationToken.None);
        var created = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);

        var closeResult = await controller.UpdateProceduralDeadline(
            clientCase.Id,
            created.Id,
            new CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest
            {
                DeadlineType = "motion",
                Title = "Подать уточнённую позицию",
                DueAt = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
                Status = "closed",
                WarningDaysBefore = 2,
                SourceType = "court-event",
                SourceId = secondCourtEvent.Id,
                CloseReason = "Документы поданы",
            },
            CancellationToken.None);

        var closed = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<OkObjectResult>(closeResult.Result).Value);
        Assert.Equal("closed", closed.Status);
        Assert.Equal("Документы поданы", closed.CloseReason);
        Assert.Equal(owner.Id, closed.ClosedByUserId);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(secondCourtEvent.Id, closed.SourceId);
        Assert.Null(firstCourtEvent.ProceduralDeadlineId);
        Assert.Equal(created.Id, secondCourtEvent.ProceduralDeadlineId);
        Assert.Contains("Перенос: 08.08.2026 -> 18.08.2026", clientCase.EventHistory, StringComparison.Ordinal);

        var reopenResult = await controller.UpdateProceduralDeadline(
            clientCase.Id,
            created.Id,
            new CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest
            {
                DeadlineType = "motion",
                Title = "Подать уточнённую позицию",
                DueAt = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
                Status = "active",
                SourceType = "manual",
            },
            CancellationToken.None);

        var reopened = Assert.IsType<CasesV2Controller.ClientCaseProceduralDeadlineDto>(
            Assert.IsType<OkObjectResult>(reopenResult.Result).Value);
        Assert.Equal("active", reopened.Status);
        Assert.Null(reopened.ClosedAt);
        Assert.Null(reopened.ClosedByUserId);
        Assert.Empty(reopened.ClosedByUserName);
        Assert.Empty(reopened.CloseReason);
        Assert.Null(reopened.SourceId);
        Assert.Null(secondCourtEvent.ProceduralDeadlineId);
    }

    [Fact]
    public async Task UpdateCourtEvent_RebindsDeadlineAndWritesHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-event-update-owner@example.com", "Владелец события");
        var client = new Client
        {
            Name = "Клиент с изменяемым заседанием",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело со сменой связанного срока",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            CourtName = "Районный суд",
            CourtCaseNumber = "2-100/2026",
        };
        var firstDeadline = new ClientCaseProceduralDeadline
        {
            ClientCase = clientCase,
            DeadlineType = "motion",
            Title = "Первый срок",
            DueAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
        };
        var secondDeadline = new ClientCaseProceduralDeadline
        {
            ClientCase = clientCase,
            DeadlineType = "motion",
            Title = "Второй срок",
            DueAt = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = owner.Id,
            CreatedByUserName = owner.FullName,
        };
        db.Clients.Add(client);
        db.ClientCaseProceduralDeadlines.AddRange(firstDeadline, secondDeadline);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createResult = await controller.CreateCourtEvent(
            clientCase.Id,
            new CasesV2Controller.UpsertClientCaseCourtEventRequest
            {
                EventType = "hearing",
                Title = "Предварительное заседание",
                ScheduledAt = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc),
                CourtInstance = "first",
                MovementStatus = "scheduled",
                ProceduralDeadlineId = firstDeadline.Id,
            },
            CancellationToken.None);
        var created = Assert.IsType<CasesV2Controller.ClientCaseCourtEventDto>(
            Assert.IsType<CreatedResult>(createResult.Result).Value);

        var updateResult = await controller.UpdateCourtEvent(
            clientCase.Id,
            created.Id,
            new CasesV2Controller.UpsertClientCaseCourtEventRequest
            {
                EventType = "decision",
                Title = "Оглашение решения",
                Description = "Дата уточнена судом",
                ScheduledAt = new DateTime(2026, 9, 12, 14, 30, 0, DateTimeKind.Utc),
                AllDay = false,
                CourtInstance = "first",
                MovementStatus = "completed",
                ResultSummary = "Решение оглашено",
                ProceduralDeadlineId = secondDeadline.Id,
            },
            CancellationToken.None);

        var updated = Assert.IsType<CasesV2Controller.ClientCaseCourtEventDto>(
            Assert.IsType<OkObjectResult>(updateResult.Result).Value);
        Assert.Equal("decision", updated.EventType);
        Assert.Equal("Оглашение решения", updated.Title);
        Assert.Equal("completed", updated.MovementStatus);
        Assert.Equal(secondDeadline.Id, updated.ProceduralDeadlineId);
        Assert.Equal("manual", firstDeadline.SourceType);
        Assert.Null(firstDeadline.SourceId);
        Assert.Equal("court-event", secondDeadline.SourceType);
        Assert.Equal(created.Id, secondDeadline.SourceId);

        var history = await db.ClientCaseCourtEventHistory
            .Where(item => item.ClientCaseCourtEventId == created.Id)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Collection(
            history,
            item => Assert.Equal("created", item.Action),
            item =>
            {
                Assert.Equal("updated", item.Action);
                Assert.Contains("Оглашение решения", item.Summary, StringComparison.Ordinal);
            });
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
        var owner = await CreateUserAsync(db, "deadline-preview@example.com", "Владелец");
        var controller = CreateController(db, owner);

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
    public async Task ProceduralDeadlineCalculationPreview_ReturnsForbidWithoutCaseViewPermission()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "deadline-preview-no-view@example.com", "Без просмотра дел");
        viewer.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanView = false;
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.CalculateProceduralDeadlinePreview(new ProceduralDeadlineCalculationRequest
        {
            StartAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            Rule = "workingDays",
            DaysToAdd = 2,
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
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

        var getResult = await controller.GetProceduralDeadlineCalendarSettings(CancellationToken.None);
        var reloaded = Assert.IsType<CasesV2Controller.ProceduralDeadlineCalendarSettingsDto>(
            Assert.IsType<OkObjectResult>(getResult.Result).Value);
        Assert.Contains(reloaded.WorkingDates, item => item.Date == "2026-05-02" && item.Note == "Рабочая суббота");
        Assert.Contains(reloaded.NonWorkingDates, item => item.Date == "2026-05-04" && item.Note == "Перенос выходного");
        Assert.NotNull(reloaded.UpdatedAt);

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
    public async Task Update_RequiresReasonWhenDisablingInactivityRemindersAndAuditsIt()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "inactivity-reason@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Клиент с исключением",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с исключением",
            Status = ClientCaseStatus.Open,
            InactivityRemindersEnabled = true,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var rejected = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = clientCase.Title,
            Status = ClientCaseStatus.Open,
            InactivityRemindersEnabled = false,
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(rejected);
        Assert.Contains("причину", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        await db.Entry(clientCase).ReloadAsync();
        Assert.True(clientCase.InactivityRemindersEnabled);

        var accepted = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = clientCase.Title,
            Status = ClientCaseStatus.Open,
            InactivityRemindersEnabled = false,
            InactivityRemindersDisabledReason = "Ожидаем решение клиента",
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(accepted);
        await db.Entry(clientCase).ReloadAsync();
        Assert.False(clientCase.InactivityRemindersEnabled);
        Assert.Equal("Ожидаем решение клиента", clientCase.InactivityRemindersDisabledReason);
        Assert.Contains("Ожидаем решение клиента", clientCase.EventHistory, StringComparison.Ordinal);

        var auditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item =>
                item.ObjectType == "client-case" &&
                item.ObjectId == clientCase.Id &&
                item.Action == "Updated")
            .OrderByDescending(item => item.Id)
            .FirstAsync();
        using var auditMetadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        var reasonChange = auditMetadata.RootElement
            .GetProperty("changes")
            .EnumerateArray()
            .Single(item => item.GetProperty("field").GetString() == "InactivityRemindersDisabledReason");
        Assert.Equal("Ожидаем решение клиента", reasonChange.GetProperty("newValue").GetString());
    }

    [Fact]
    public async Task BulkUpdate_RejectsDisablingInactivityRemindersWithoutReason()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "bulk-inactivity-reason@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Клиент массового исключения",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело массового исключения",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.BulkUpdate(new CasesV2Controller.BulkUpdateCasesRequest
        {
            Ids = [clientCase.Id],
            InactivityRemindersEnabled = false,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await db.Entry(clientCase).ReloadAsync();
        Assert.True(clientCase.InactivityRemindersEnabled);
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
            InactivityRemindersDisabledReason = "Работа временно ведётся вне CRM",
            IsUnderControl = true,
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
        Assert.Equal("Работа временно ведётся вне CRM", visibleByClient.InactivityRemindersDisabledReason);
        Assert.True(visibleByClient.IsUnderControl);
        Assert.Equal(owner.Id, visibleByClient.HiddenFromExecutorByUserId);
        Assert.Equal("Владелец", visibleByClient.HiddenFromExecutorByUserName);
        Assert.Equal(ClientCaseStatus.OnHold, visibleByCaseResponsible.Status);
        Assert.Equal(nextResponsible.Id, visibleByCaseResponsible.ResponsibleUserId);
        Assert.Equal("Судебное производство", visibleByCaseResponsible.Stage);
        Assert.Equal("urgent", visibleByCaseResponsible.Priority);
        Assert.True(visibleByCaseResponsible.IsHiddenFromExecutor);
        Assert.False(visibleByCaseResponsible.InactivityRemindersEnabled);
        Assert.Equal("Работа временно ведётся вне CRM", visibleByCaseResponsible.InactivityRemindersDisabledReason);
        Assert.True(visibleByCaseResponsible.IsUnderControl);
        Assert.Equal(ClientCaseStatus.Open, hiddenCase.Status);
        Assert.Equal(otherResponsible.Id, hiddenCase.ResponsibleUserId);
        Assert.False(hiddenCase.IsHiddenFromExecutor);
        Assert.True(hiddenCase.InactivityRemindersEnabled);
        Assert.False(hiddenCase.IsUnderControl);
        Assert.Contains("Массовое изменение", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("статус «В работе» → «Приостановлено»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("ответственный «Ответственный владельца» → «Новый ответственный»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("стадия «Подготовка» → «Судебное производство»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("приоритет «Обычный» → «Критический»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("доступ: скрыто от исполнителя", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("автоконтроль: выключен", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Работа временно ведётся вне CRM", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("контроль: включен", visibleByClient.EventHistory, StringComparison.Ordinal);
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
            Assert.Contains("контроль: включен", reason, StringComparison.Ordinal);
            Assert.Contains("Status", auditEvent.MetadataJson, StringComparison.Ordinal);
            Assert.Contains("ResponsibleUserId", auditEvent.MetadataJson, StringComparison.Ordinal);
            Assert.Contains("IsHiddenFromExecutor", auditEvent.MetadataJson, StringComparison.Ordinal);
            Assert.Contains("InactivityRemindersEnabled", auditEvent.MetadataJson, StringComparison.Ordinal);
            using var metadataChanges = JsonDocument.Parse(auditEvent.MetadataJson!);
            var reasonChange = metadataChanges.RootElement
                .GetProperty("changes")
                .EnumerateArray()
                .Single(item => item.GetProperty("field").GetString() == "InactivityRemindersDisabledReason");
            Assert.Equal("Работа временно ведётся вне CRM", reasonChange.GetProperty("newValue").GetString());
            Assert.Contains("IsUnderControl", auditEvent.MetadataJson, StringComparison.Ordinal);
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
        Assert.All(documents, item => Assert.Equal(DateTimeKind.Utc, item.UploadedAt.Kind));
        Assert.Contains("\"UploadedAt\":\"", JsonSerializer.Serialize(documents), StringComparison.Ordinal);
        Assert.Contains("Z\"", JsonSerializer.Serialize(documents), StringComparison.Ordinal);

        var secondStored = await db.ClientCaseDocuments
            .AsNoTracking()
            .SingleAsync(item => item.Id == second.Id);
        var storage = new LocalCaseDocumentStorage(
            new TestWebHostEnvironment { ContentRootPath = contentRoot });
        File.Delete(storage.ResolvePath(secondStored.StoragePath));

        var availableListResult = await controller.GetDocuments(clientCase.Id, CancellationToken.None);
        var availableDocuments = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(
            Assert.IsType<OkObjectResult>(availableListResult.Result).Value);
        var availableDocument = Assert.Single(availableDocuments);
        Assert.Equal(first.Id, availableDocument.Id);
        Assert.True(availableDocument.IsLatestVersion);

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
    public async Task UploadDocument_UsesNotificationServiceForResponsibleUser()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "case-document-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "case-document-responsible@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Клиент по документу",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для документа",
            CaseNumber = "CASE-DOCUMENT-SERVICE-1",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot, notificationService: CreateNotificationService(db));

        var uploadResult = await controller.UploadDocument(
            clientCase.Id,
            CreateFormFile("document", "claim.pdf"),
            "Иск",
            DocumentWorkflowStatus.NeedsCorrection,
            documentKey: null,
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(uploadResult.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);
        var notification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CaseDocument);
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("claim.pdf", notification.Title);
        Assert.Contains("Требуется действие", notification.BodyPreview);
        Assert.Contains($"\"clientCaseDocumentId\":{dto.Id}", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"uploaded\"", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"requiresAction\":true", notification.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_ForbidReadAndDownloadWithoutCaseViewPermission()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "documents-no-view@example.com", "Без просмотра дел");
        viewer.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanView = false;
        var client = new Client
        {
            Name = "Клиент закрытого дела",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело без права просмотра",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = viewer.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "closed",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "closed.txt",
            StoredFileName = "closed-v1.txt",
            StoragePath = "cases/closed-v1.txt",
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
        var documentResult = await controller.GetDocumentById(document.Id, CancellationToken.None);
        var downloadByIdResult = await controller.DownloadDocumentById(document.Id, CancellationToken.None);
        var scopedDownloadResult = await controller.DownloadDocument(clientCase.Id, document.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(listResult.Result);
        Assert.IsType<ForbidResult>(documentResult.Result);
        Assert.IsType<ForbidResult>(downloadByIdResult);
        Assert.IsType<ForbidResult>(scopedDownloadResult);
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
            NextHearingAt = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc),
        };
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
                Content = "<p>{{case.title}} / {{responsible.name}} / {{nextHearingAt}}</p>",
            },
            CancellationToken.None);

        var updatedOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedDto = Assert.IsType<CasesV2Controller.CaseDocumentTemplateDefinitionDto>(updatedOk.Value);
        Assert.Equal("Обновленный шаблон", updatedDto.Label);
        Assert.Equal(2, updatedDto.Version);
        Assert.Equal(new[] { "Банкротство", "Семейное право" }, updatedDto.CaseTypes);

        var duplicateUpdateResult = await controller.UpdateDocumentTemplate(
            "custom-claim",
            new CasesV2Controller.UpsertCaseDocumentTemplateRequest
            {
                Label = "Обновленный шаблон",
                Content = "<p>{{case.title}} / {{responsible.name}} / {{nextHearingAt}}</p>",
            },
            CancellationToken.None);
        var duplicateUpdate = Assert.IsType<OkObjectResult>(duplicateUpdateResult.Result);
        Assert.Equal(2, Assert.IsType<CasesV2Controller.CaseDocumentTemplateDefinitionDto>(duplicateUpdate.Value).Version);

        var historyResult = await controller.GetDocumentTemplateHistory(CancellationToken.None);
        var historyOk = Assert.IsType<OkObjectResult>(historyResult.Result);
        var history = Assert.IsAssignableFrom<IReadOnlyList<DocumentTemplateVersionDto>>(historyOk.Value);
        Assert.Equal(3, history.Count);
        var createdVersion = Assert.Single(history, item => item.Revision == 2);

        var versionResult = await controller.GetDocumentTemplateVersion(createdVersion.Id, CancellationToken.None);
        var versionOk = Assert.IsType<OkObjectResult>(versionResult.Result);
        var versionTemplates = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateDefinitionDto>>(versionOk.Value);
        Assert.Contains(versionTemplates, item => item.Key == "custom-claim" && item.Label == "Рабочий шаблон");

        var restoreResult = await controller.RestoreDocumentTemplateVersion(createdVersion.Id, CancellationToken.None);
        var restoreOk = Assert.IsType<OkObjectResult>(restoreResult.Result);
        var restoredTemplates = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateDefinitionDto>>(restoreOk.Value);
        Assert.Contains(restoredTemplates, item => item.Key == "custom-claim" && item.Label == "Рабочий шаблон");

        historyResult = await controller.GetDocumentTemplateHistory(CancellationToken.None);
        historyOk = Assert.IsType<OkObjectResult>(historyResult.Result);
        history = Assert.IsAssignableFrom<IReadOnlyList<DocumentTemplateVersionDto>>(historyOk.Value);
        Assert.Equal(4, history.Count);
        Assert.Equal(createdVersion.Id, history[0].RestoredFromVersionId);

        var listResult = await controller.GetDocumentTemplates(CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var templates = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateDefinitionDto>>(listOk.Value);
        Assert.Contains(templates, item => item.Key == "custom-claim" && item.Label == "Рабочий шаблон");

        var renderResult = await controller.RenderDocumentTemplate(
            "custom-claim",
            new CasesV2Controller.RenderCaseDocumentTemplateRequest { ClientCaseId = clientCase.Id },
            CancellationToken.None);

        var renderOk = Assert.IsType<OkObjectResult>(renderResult.Result);
        var rendered = Assert.IsType<CasesV2Controller.RenderedCaseDocumentTemplateDto>(renderOk.Value);
        Assert.Equal("custom-claim", rendered.TemplateKey);
        Assert.Contains("Земцов - банкротство", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("Земцов Виктор Александрович", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("Арбитражный суд Самарской области", rendered.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{case.title}}", rendered.Html, StringComparison.Ordinal);

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
        Assert.Contains("Исполнительный лист", types);
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
    public async Task CaseDocumentStorage_UsesConfiguredPersistentRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-content", Guid.NewGuid().ToString("N"));
        var persistentRoot = Path.Combine(Path.GetTempPath(), "payplanner-storage", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApplicationFileStoragePaths.RootPathConfigurationKey] = persistentRoot,
            })
            .Build();
        var storage = new LocalCaseDocumentStorage(
            new TestWebHostEnvironment { ContentRootPath = contentRoot },
            configuration);
        var relativePath = storage.BuildRelativePath(21, "evidence.txt");

        await storage.SaveAsync(relativePath, Encoding.UTF8.GetBytes("evidence"), CancellationToken.None);

        Assert.Equal(
            Path.Combine(persistentRoot, "case-documents", "21", "evidence.txt"),
            storage.ResolvePath(relativePath));
        Assert.True(storage.Exists(relativePath));
        storage.DeleteIfExists(relativePath);
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
            UploadedAt = DateTime.SpecifyKind(new DateTime(2026, 5, 18, 10, 0, 0), DateTimeKind.Unspecified),
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
            UploadedAt = DateTime.SpecifyKind(new DateTime(2026, 5, 18, 11, 0, 0), DateTimeKind.Unspecified),
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
        Assert.Equal(DateTimeKind.Utc, latestDto.UploadedAt.Kind);
        Assert.Contains("\"UploadedAt\":\"", JsonSerializer.Serialize(latestDto), StringComparison.Ordinal);
        Assert.Contains("Z\"", JsonSerializer.Serialize(latestDto), StringComparison.Ordinal);
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
                UploadedAt = DateTime.SpecifyKind(new DateTime(2026, 5, 19, 10, 0, 0), DateTimeKind.Unspecified),
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
                UploadedAt = DateTime.SpecifyKind(new DateTime(2026, 5, 19, 11, 0, 0), DateTimeKind.Unspecified),
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
                UploadedAt = DateTime.SpecifyKind(new DateTime(2026, 5, 19, 12, 0, 0), DateTimeKind.Unspecified),
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
        Assert.Equal(DateTimeKind.Utc, item.UploadedAt.Kind);
        Assert.Contains("\"UploadedAt\":\"", JsonSerializer.Serialize(item), StringComparison.Ordinal);
        Assert.Contains("Z\"", JsonSerializer.Serialize(item), StringComparison.Ordinal);
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
    public async Task DirectDocumentEndpoints_ReturnNotFoundForHiddenCase()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "hidden-document-viewer@example.com", "Наблюдатель");
        var otherResponsible = await CreateUserAsync(db, "hidden-document-owner@example.com", "Другой ответственный");
        var client = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = hiddenCase,
            DocumentKey = "hidden",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "hidden.txt",
            StoredFileName = "hidden-v1.txt",
            StoragePath = "hidden/hidden-v1.txt",
            ContentType = "text/plain",
            SizeBytes = 5,
            UploadedByUserId = otherResponsible.Id,
            UploadedByName = otherResponsible.FullName,
            UploadedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(hiddenCase);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var getResult = await controller.GetDocumentById(document.Id, CancellationToken.None);
        var downloadResult = await controller.DownloadDocumentById(document.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(getResult.Result);
        Assert.IsType<NotFoundResult>(downloadResult);
        Assert.False(await db.ObjectAuditEvents.AnyAsync(item =>
            item.ObjectType == "client-case-document"
            && item.ObjectId == document.Id
            && item.Action == "Downloaded"));
    }

    [Fact]
    public async Task DirectDocumentMutationEndpoints_ReturnNotFoundForHiddenCase()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "hidden-document-editor@example.com", "Редактор");
        var otherResponsible = await CreateUserAsync(db, "hidden-document-editor-owner@example.com", "Другой ответственный");
        var client = new Client
        {
            Name = "Скрытый клиент для изменения",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело для изменения",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = hiddenCase,
            DocumentKey = "hidden-edit",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "hidden-edit.txt",
            StoredFileName = "hidden-edit-v1.txt",
            StoragePath = "hidden/hidden-edit-v1.txt",
            ContentType = "text/plain",
            SizeBytes = 5,
            UploadedByUserId = otherResponsible.Id,
            UploadedByName = otherResponsible.FullName,
            UploadedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(hiddenCase);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var versionResult = await controller.UploadDocumentVersionById(
            document.Id,
            CreateFormFile("blocked", "hidden-edit-v2.txt"),
            documentType: null,
            status: "Отправлен",
            ct: CancellationToken.None);
        var updateResult = await controller.UpdateDocumentById(
            document.Id,
            new CasesV2Controller.UpdateCaseDocumentRequest { Status = "Отправлен" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(versionResult.Result);
        Assert.IsType<NotFoundResult>(updateResult.Result);
        Assert.Equal(1, await db.ClientCaseDocuments.CountAsync(item => item.DocumentKey == document.DocumentKey));
        var unchanged = await db.ClientCaseDocuments.SingleAsync(item => item.Id == document.Id);
        Assert.Equal("Подготовлен", unchanged.Status);
        Assert.False(await db.ObjectAuditEvents.AnyAsync(item =>
            item.ObjectType == "client-case-document"
            && item.ObjectId == document.Id
            && (item.Action == "Updated" || item.Action == "Uploaded")));
    }

    [Fact]
    public async Task DeleteDocument_ReturnsNotFoundForHiddenCase()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "hidden-document-delete-viewer@example.com", "Удаляющий");
        var otherResponsible = await CreateUserAsync(db, "hidden-document-delete-owner@example.com", "Другой ответственный");
        var client = new Client
        {
            Name = "Скрытый клиент для удаления",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело для удаления",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = hiddenCase,
            DocumentKey = "hidden-delete",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "hidden-delete.txt",
            StoredFileName = "hidden-delete-v1.txt",
            StoragePath = "hidden/hidden-delete-v1.txt",
            ContentType = "text/plain",
            SizeBytes = 5,
            UploadedByUserId = otherResponsible.Id,
            UploadedByName = otherResponsible.FullName,
            UploadedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(hiddenCase);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var deleteResult = await controller.DeleteDocument(hiddenCase.Id, document.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(deleteResult);
        Assert.True(await db.ClientCaseDocuments.AnyAsync(item => item.Id == document.Id));
        Assert.False(await db.ObjectAuditEvents.AnyAsync(item =>
            item.ObjectType == "client-case-document"
            && item.ObjectId == document.Id
            && item.Action == "Deleted"));
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
            NextHearingAt = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc),
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
        Assert.Contains("05.06.2026 16:00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("05.06.2026 12:00", html, StringComparison.Ordinal);

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
            NextHearingAt = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc),
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

        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var createResult = await controller.CreateDocumentTemplate(new CasesV2Controller.UpsertCaseDocumentTemplateRequest
        {
            Key = "custom-brief",
            Label = "Custom Brief",
            DocumentType = "Brief",
            Heading = "Custom Heading",
            CaseTypes = new List<string> { "Briefing" },
            Content = "<html><body><h1>{{case.title}}</h1><p>{{client.name}}</p><p>{{document.type}}</p><p>{{nextHearingAt}}</p></body></html>",
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
        Assert.Contains("<p>05.06.2026 16:00</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>05.06.2026 12:00</p>", html, StringComparison.Ordinal);

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
        var realtimePublisher = new RecordingRealtimeNotificationPublisher();
        var controller = CreateController(db, owner, realtimePublisher: realtimePublisher);

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
        Assert.Equal(dto.ActivityConfirmedAt, dto.LastMeaningfulActivityAt);
        Assert.Equal(dto.LastMeaningfulActivityAt!.Value.AddDays(14), dto.NextInactivityControlAt);
        Assert.Empty(dto.InactivityControlExclusionReason);
        Assert.Contains("Активность по делу подтверждена", dto.EventHistory, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.NotNull(clientCase.LastViewedAt);
        Assert.NotNull(clientCase.ActivityConfirmedAt);
        Assert.Contains("Подтвердил: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Equal(1, realtimePublisher.TaskJournalChangedCount);
    }

    [Fact]
    public async Task CaseActivityAndHistory_RequireCaseViewAndEditPermissions()
    {
        await using var db = await CreateContextAsync();
        var noViewUser = await CreateUserAsync(db, "case-activity-no-view@example.com", "Без просмотра");
        noViewUser.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanView = false;
        var noEditUser = await CreateUserAsync(db, "case-history-no-edit@example.com", "Без редактирования");
        noEditUser.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanEdit = false;
        var client = new Client
        {
            Name = "Клиент активности и истории",
            IsActive = true,
            ResponsibleUserId = noEditUser.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело активности и истории",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = noEditUser.Id,
            EventHistory = "Дело создано",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var activityController = CreateController(db, noViewUser);
        var activityResult = await activityController.MarkActivity(
            clientCase.Id,
            new CasesV2Controller.MarkClientCaseActivityRequest { Confirmed = true },
            CancellationToken.None);

        var historyController = CreateController(db, noEditUser);
        var historyResult = await historyController.AddHistoryEntry(
            clientCase.Id,
            new CasesV2Controller.AddClientCaseHistoryEntryRequest { Text = "Скрытый комментарий" },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(activityResult.Result);
        Assert.IsType<ForbidResult>(historyResult.Result);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Null(clientCase.ActivityConfirmedAt);
        Assert.Equal("Дело создано", clientCase.EventHistory);
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
        db.UserSettings.Add(new UserSetting
        {
            UserId = owner.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
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
        var realtimePublisher = new RecordingRealtimeNotificationPublisher();
        var controller = CreateController(db, viewer, realtimePublisher: realtimePublisher);

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
        Assert.Equal(0, realtimePublisher.TaskJournalChangedCount);
    }

    [Fact]
    public async Task MarkActivity_DoesNotResetLastViewWhenResponsibleIsNotAssigned()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer-without-responsible@example.com", "Наблюдатель", DataVisibilityScope.All);
        var viewedAt = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc);
        var client = new Client
        {
            Name = "Клиент без ответственного",
            IsActive = true,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело без ответственного",
            Status = ClientCaseStatus.Open,
            LastViewedAt = viewedAt,
            LastViewedByUserName = "Прежний ответственный",
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
        Assert.Equal(viewedAt, dto.LastViewedByResponsibleAt);
        Assert.Equal("Прежний ответственный", dto.LastViewedByResponsibleUserName);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(viewedAt, clientCase.LastViewedAt);
        Assert.Equal("Прежний ответственный", clientCase.LastViewedByUserName);
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
        Assert.Contains("выбранный источник пока не поддерживает автоматическое чтение карточки", dto.Message, StringComparison.Ordinal);
        Assert.Equal("Владелец", dto.CheckedByUserName);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Ручная проверка судебного источника зафиксирована", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Инициатор: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CourtSyncEndpoints_RequireCasePermissionsAndVisibility()
    {
        await using var db = await CreateContextAsync();
        var noViewUser = await CreateUserAsync(db, "court-sync-no-view@example.com", "Без просмотра");
        noViewUser.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanView = false;
        var noEditUser = await CreateUserAsync(db, "court-sync-no-edit@example.com", "Без редактирования");
        noEditUser.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanEdit = false;
        var limitedEditor = await CreateUserAsync(db, "court-sync-limited@example.com", "Ограниченный");
        var otherResponsible = await CreateUserAsync(db, "court-sync-other@example.com", "Другой");
        var client = new Client
        {
            Name = "Клиент судебной синхронизации",
            IsActive = true,
            ResponsibleUserId = noEditUser.Id,
        };
        var accessibleCase = new ClientCase
        {
            Client = client,
            Title = "Доступное судебное дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = noEditUser.Id,
            CourtCaseNumber = "А55-111/2026",
            EventHistory = "23.06.2026 10:00: Ручная проверка судебного источника зафиксирована. Инициатор: Тест.",
        };
        var hiddenCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое судебное дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
            CourtCaseNumber = "А55-222/2026",
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(accessibleCase, hiddenCase);
        await db.SaveChangesAsync();

        var noViewController = CreateController(db, noViewUser);
        Assert.IsType<ForbidResult>((await noViewController.GetCourtSyncSettings(CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await noViewController.GetCourtSyncProviders(CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await noViewController.GetCourtSyncJournal(take: 50, status: null, search: null, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await noViewController.GetCourtSyncLogs(accessibleCase.Id, CancellationToken.None)).Result);

        var noEditController = CreateController(db, noEditUser);
        Assert.IsType<ForbidResult>((await noEditController.UpdateCourtSyncSettings(new CasesV2Controller.UpdateClientCaseCourtSyncSettingsRequest
        {
            IsEnabled = true,
            IntervalMinutes = 60,
            BatchSize = 10,
        }, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await noEditController.RunCourtSync(accessibleCase.Id, null, CancellationToken.None)).Result);

        var limitedController = CreateController(db, limitedEditor);
        Assert.IsType<NotFoundResult>((await limitedController.RunCourtSync(hiddenCase.Id, null, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await limitedController.GetCourtSyncLogs(hiddenCase.Id, CancellationToken.None)).Result);
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
    public async Task FiledAppealComplaint_CreatesSideSpecificTasksAndTracksHearingChanges()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserAsync(db, "appeal-lawyer@example.com", "Юрист");
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = lawyer.Id },
            Title = "Дело с апелляцией",
            Stage = "Суд",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = lawyer.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, lawyer);
        var filedAt = DateTime.UtcNow.Date;

        var invalidSide = await controller.CreateCourtComplaint(clientCase.Id, "first", new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "appealComplaint",
            TargetInstance = "appeal",
            FiledBy = "other",
            FiledAt = filedAt,
            Status = "filed",
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalidSide.Result);

        var missingFiledAt = await controller.CreateCourtComplaint(clientCase.Id, "first", new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "appealComplaint",
            TargetInstance = "appeal",
            FiledBy = "ours",
            Status = "filed",
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingFiledAt.Result);

        var unknownSide = await controller.CreateCourtComplaint(clientCase.Id, "first", new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "appealComplaint",
            Title = "Сторона не указана",
            TargetInstance = "appeal",
            FiledAt = filedAt,
            Status = "filed",
        }, CancellationToken.None);
        Assert.Equal("unknown", Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(Assert.IsType<CreatedResult>(unknownSide.Result).Value).FiledBy);
        Assert.DoesNotContain(await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).ToListAsync(),
            item => item.Title is "Отслеживать назначение заседания" or "Подготовить возражения на апелляцию");

        var ourResult = await controller.CreateCourtComplaint(clientCase.Id, "first", new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "appealComplaint",
            Title = "Наша апелляция",
            TargetInstance = "appeal",
            FiledAt = filedAt,
            FiledBy = "ours",
            Status = "filed",
        }, CancellationToken.None);
        var ourComplaint = Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(Assert.IsType<CreatedResult>(ourResult.Result).Value);
        Assert.Equal("ours", ourComplaint.FiledBy);
        var legacyUpdate = await controller.UpdateCourtComplaint(clientCase.Id, "first", ourComplaint.Id, new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "appealComplaint",
            Title = "Наша апелляция уточнена",
            TargetInstance = "appeal",
            FiledAt = filedAt,
            Status = "filed",
        }, CancellationToken.None);
        Assert.Equal("ours", Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(Assert.IsType<OkObjectResult>(legacyUpdate.Result).Value).FiledBy);
        var invalidLegacyUpdate = await controller.UpdateCourtComplaint(clientCase.Id, "first", ourComplaint.Id, new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "appealComplaint",
            TargetInstance = "appeal",
            Status = "filed",
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalidLegacyUpdate.Result);
        var followup = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id && item.Title == "Отслеживать назначение заседания");
        Assert.Equal(filedAt.AddDays(10).AddHours(9), followup.DueAt);
        Assert.Equal(lawyer.Id, followup.AssigneeUserId);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id && item.Title == "Отслеживать назначение заседания"));

        var opponentResult = await controller.CreateCourtComplaint(clientCase.Id, "first", new CasesV2Controller.UpsertClientCaseCourtComplaintRequest
        {
            ComplaintType = "appealComplaint",
            Title = "Апелляция против нас",
            TargetInstance = "appeal",
            FiledAt = filedAt,
            FiledBy = "opponent",
            Status = "accepted",
        }, CancellationToken.None);
        var opponentComplaint = Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(Assert.IsType<CreatedResult>(opponentResult.Result).Value);
        Assert.DoesNotContain(await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).ToListAsync(),
            item => item.Title == "Подготовить возражения на апелляцию");

        var hearing = new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            CourtInstance = "appeal",
            EventType = "hearing",
            Title = "Заседание апелляции",
            ScheduledAt = filedAt.AddDays(20).AddHours(10),
            MovementStatus = "scheduled",
        };
        db.ClientCaseCourtEvents.Add(hearing);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal("done", followup.Status);
        var objections = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id && item.Title == "Подготовить возражения на апелляцию");
        Assert.Equal(hearing.ScheduledAt, objections.DueAt);

        hearing.ScheduledAt = hearing.ScheduledAt.AddDays(3);
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, lawyer.Id, lawyer.FullName, CancellationToken.None);
        Assert.Equal(hearing.ScheduledAt, objections.DueAt);

        await controller.ArchiveCourtComplaint(clientCase.Id, "first", opponentComplaint.Id, CancellationToken.None);
        Assert.Equal("cancelled", objections.Status);
        Assert.Equal("done", followup.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunCourtSync_NotifiesAboutFutureHearingRescheduleOnlyOnce(bool includeMovement)
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-reschedule-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-reschedule-lawyer@example.com", "Юрист");
        var oldHearingAt = DateTime.UtcNow.AddDays(10);
        var newHearingAt = oldHearingAt.AddDays(5);
        var client = new Client { Name = "Контрагент", IsActive = true, ResponsibleUserId = owner.Id };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с переносом заседания",
            Stage = "Суд",
            CourtUrl = "https://test.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
            CourtCaseNumber = "2-2026/99",
            NextHearingAt = oldHearingAt,
            NextHearingConfirmedForAt = oldHearingAt,
            NextHearingConfirmedAt = DateTime.UtcNow,
            NextHearingConfirmedByUserId = responsible.Id,
            NextHearingConfirmedByName = "Юрист",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            EventType = CourtSyncNormalization.EventTypes.Hearing,
            Title = "Судебное заседание",
            ScheduledAt = oldHearingAt,
            SourceType = "sudrf",
            SourceUrl = clientCase.CourtUrl,
            ExternalKey = "previous-hearing",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Тестовый суд",
            CaseNumber = clientCase.CourtCaseNumber,
            NextHearingAt = newHearingAt,
            MovementEvents = includeMovement
                ? [new SudrfCourtMovementEvent
                {
                    Title = "Судебное заседание",
                    ScheduledAt = newHearingAt,
                    SourceSummary = "Заседание назначено на новую дату",
                }]
                : [],
        };
        var controller = CreateController(
            db, owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData),
            notificationService: CreateNotificationService(db));

        Assert.IsType<OkObjectResult>((await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None)).Result);
        Assert.Null(clientCase.NextHearingConfirmedForAt);
        Assert.Null(clientCase.NextHearingConfirmedAt);
        var notification = await db.UserNotifications.SingleAsync(item =>
            item.RecipientUserId == responsible.Id && item.Type == UserNotificationTypes.CourtHearing);
        Assert.Contains("Перенесено", notification.Title, StringComparison.Ordinal);
        Assert.Contains("Новая дата", notification.BodyPreview, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"rescheduled\"", notification.MetadataJson, StringComparison.Ordinal);
        Assert.Single(await db.ClientCaseCourtEvents.Where(item =>
            item.ClientCaseId == clientCase.Id && item.ScheduledAt == newHearingAt).ToListAsync());

        Assert.IsType<OkObjectResult>((await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None)).Result);
        Assert.Equal(1, await db.UserNotifications.CountAsync(item =>
            item.RecipientUserId == responsible.Id && item.Type == UserNotificationTypes.CourtHearing));
        if (!includeMovement)
        {
            var detailedController = CreateController(db, owner,
                sudrfCourtSyncService: new FixedSudrfCourtSyncService(new SudrfCourtCaseData
                {
                    SourceUrl = clientCase.CourtUrl,
                    CourtName = courtData.CourtName,
                    CaseNumber = courtData.CaseNumber,
                    NextHearingAt = newHearingAt,
                    MovementEvents =
                    [
                        new() { Title = "Судебное заседание", ScheduledAt = newHearingAt, SourceSummary = "Движение дела опубликовано позднее" },
                    ],
                }),
                notificationService: CreateNotificationService(db));
            Assert.IsType<OkObjectResult>((await detailedController.RunCourtSync(clientCase.Id, null, CancellationToken.None)).Result);
            Assert.Single(await db.ClientCaseCourtEvents.Where(item =>
                item.ClientCaseId == clientCase.Id && item.ScheduledAt == newHearingAt).ToListAsync());
            Assert.Equal(1, await db.UserNotifications.CountAsync(item =>
                item.RecipientUserId == responsible.Id && item.Type == UserNotificationTypes.CourtHearing));
            var changedController = CreateController(db, owner,
                sudrfCourtSyncService: new FixedSudrfCourtSyncService(new SudrfCourtCaseData
                {
                    SourceUrl = clientCase.CourtUrl,
                    CourtName = courtData.CourtName,
                    CaseNumber = courtData.CaseNumber,
                    NextHearingAt = newHearingAt,
                    MovementEvents =
                    [
                        new() { Title = "Судебное заседание", ScheduledAt = newHearingAt, Place = "зал 5", SourceSummary = "Зал заседания уточнён" },
                    ],
                }),
                notificationService: CreateNotificationService(db));
            Assert.IsType<OkObjectResult>((await changedController.RunCourtSync(clientCase.Id, null, CancellationToken.None)).Result);
            Assert.Equal(2, await db.UserNotifications.CountAsync(item =>
                item.RecipientUserId == responsible.Id && item.Type == UserNotificationTypes.CourtHearing));
        }
    }

    [Fact]
    public async Task RunCourtSync_TreatsHearingAfterPastSessionAsNewNotRescheduled()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-next-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-next-lawyer@example.com", "Юрист");
        var client = new Client { Name = "Контрагент", IsActive = true, ResponsibleUserId = owner.Id };
        var nextHearingAt = DateTime.UtcNow.AddDays(7);
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с очередным заседанием",
            Stage = "Суд",
            CourtUrl = "https://test.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=43",
            NextHearingAt = DateTime.UtcNow.AddDays(-2),
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(new SudrfCourtCaseData
            {
                SourceUrl = clientCase.CourtUrl,
                CourtName = "Тестовый суд",
                NextHearingAt = nextHearingAt,
                MovementEvents =
                [
                    new() { Title = "Судебное заседание", ScheduledAt = nextHearingAt, SourceSummary = "Назначено заседание" },
                ],
            }),
            notificationService: CreateNotificationService(db));

        Assert.IsType<OkObjectResult>((await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None)).Result);
        var notification = await db.UserNotifications.SingleAsync(item =>
            item.RecipientUserId == responsible.Id && item.Type == UserNotificationTypes.CourtHearing);
        Assert.DoesNotContain("Перенесено", notification.Title, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"created\"", notification.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCourtSync_DoesNotCallSecondScheduledHearingAReschedule()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-two-dates-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-two-dates-lawyer@example.com", "Юрист");
        var oldHearingAt = DateTime.UtcNow.AddDays(10);
        var newHearingAt = oldHearingAt.AddDays(5);
        var client = new Client { Name = "Контрагент", IsActive = true, ResponsibleUserId = owner.Id };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с двумя действующими заседаниями",
            Stage = "Суд",
            CourtUrl = "https://test.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=44",
            NextHearingAt = oldHearingAt,
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(new SudrfCourtCaseData
            {
                SourceUrl = clientCase.CourtUrl,
                CourtName = "Тестовый суд",
                NextHearingAt = newHearingAt,
                MovementEvents =
                [
                    new() { Title = "Судебное заседание", ScheduledAt = oldHearingAt },
                    new() { Title = "Судебное заседание", ScheduledAt = newHearingAt },
                ],
            }),
            notificationService: CreateNotificationService(db));

        Assert.IsType<OkObjectResult>((await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None)).Result);
        var notifications = await db.UserNotifications.Where(item =>
            item.RecipientUserId == responsible.Id && item.Type == UserNotificationTypes.CourtHearing).ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, notification =>
            Assert.DoesNotContain("Перенесено", notification.Title, StringComparison.Ordinal));
        Assert.All(notifications, notification =>
            Assert.Contains("\"action\":\"created\"", notification.MetadataJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCourtSync_UpdatesCaseAndCreatesCourtEventFromSudrf()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-sync-responsible@example.com", "Ответственный");
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
            Stage = "Апелляция",
            CourtUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Хабаровский краевой суд",
            CaseNumber = "33а-3390/2026",
            JudicialUid = "27RS0001-01-2026-000123-45",
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
        var metrics = new CourtSyncMetrics();
        var controller = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData),
            notificationService: CreateNotificationService(db),
            courtSyncMetrics: metrics);

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);
        Assert.Equal("external-sync-completed", dto.Status);
        Assert.True(dto.SyncedFromExternalSource);
        Assert.NotNull(clientCase.CourtSyncLastAttemptAt);
        Assert.Equal(clientCase.CourtSyncLastAttemptAt, clientCase.CourtSyncLastSuccessAt);
        Assert.Null(clientCase.CourtSyncFailureStartedAt);
        Assert.Empty(clientCase.CourtSyncLastError);
        Assert.Equal(1, dto.CreatedCourtEvents);
        Assert.Equal(3, dto.CreatedCourtParties);
        Assert.Equal(1, dto.CreatedCourtActDocuments);
        var metricsSnapshot = metrics.GetSnapshot();
        Assert.Equal(1, metricsSnapshot.DocumentCount);
        Assert.True(metricsSnapshot.DocumentBytes > 0);
        Assert.Equal(1, dto.CreatedProceduralDeadlines);
        Assert.Contains(dto.NewItems, item => item == "суд");
        Assert.Contains(dto.NewItems, item => item.Contains("судебное событие", StringComparison.Ordinal));
        Assert.Contains(dto.NewItems, item => item.Contains("участник процесса", StringComparison.Ordinal));
        Assert.Empty(dto.RemovedItems);
        Assert.Empty(dto.ConflictItems);
        Assert.Contains("обновлены поля", dto.Message, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("Хабаровский краевой суд", clientCase.CourtName);
        Assert.Equal("33а-3390/2026", clientCase.CourtCaseNumber);
        Assert.Equal("27RS0001-01-2026-000123-45", clientCase.JudicialUid);
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
        Assert.StartsWith("sudrf-event-", courtEvent.ExternalKey, StringComparison.Ordinal);
        Assert.Equal(clientCase.CourtUrl, courtEvent.SourceUrl);
        Assert.NotNull(courtEvent.SourceCheckedAt);
        Assert.Equal("official-source", courtEvent.SourceConfidence);

        var history = await db.ClientCaseCourtEventHistory.SingleAsync(item => item.ClientCaseCourtEventId == courtEvent.Id);
        Assert.Contains("ГАС «Правосудие»", history.Summary, StringComparison.Ordinal);

        var hearingTask = await db.TaskJournalItems
            .Include(item => item.Participants)
            .SingleAsync(item => item.ClientCaseId == clientCase.Id
                && item.TagsJson.Contains($"case-deadline:court-event-{courtEvent.Id}"));
        Assert.Equal("courtHearing", hearingTask.Type);
        Assert.Equal(responsible.Id, hearingTask.AssigneeUserId);
        Assert.Equal(courtEvent.ScheduledAt.AddDays(-3), hearingTask.DueAt);
        Assert.Contains("судебное-заседание", JsonSerializer.Deserialize<string[]>(hearingTask.TagsJson) ?? []);
        Assert.Contains("зал 12", hearingTask.Description, StringComparison.Ordinal);
        Assert.Contains(hearingTask.Participants, item => item.Role == "assignee" && item.UserId == responsible.Id);

        var courtParties = await db.ClientCaseCourtParties
            .Where(item => item.ClientCaseId == clientCase.Id)
            .OrderBy(item => item.DisplayName)
            .ToListAsync();
        Assert.Equal(3, courtParties.Count);
        Assert.All(courtParties, item =>
        {
            Assert.Equal("sudrf", item.SourceType);
            Assert.Equal(clientCase.CourtUrl, item.SourceUrl);
            Assert.NotNull(item.SourceCheckedAt);
            Assert.Equal("official-source", item.SourceConfidence);
        });
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
        Assert.Equal("sudrf", document.SourceType);
        Assert.Equal(clientCase.CourtUrl, document.SourceUrl);
        Assert.NotNull(document.SourceCheckedAt);
        Assert.Equal("official-source", document.SourceConfidence);

        var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("cassation", deadline.DeadlineType);
        Assert.Equal(new DateTime(2026, 9, 1), deadline.DueAt);
        Assert.Equal(document.Id, deadline.ClientCaseDocumentId);
        Assert.Equal(clientCase.CourtUrl, deadline.SourceUrl);
        Assert.NotNull(deadline.SourceCheckedAt);
        Assert.Equal("official-source", deadline.SourceConfidence);
        var deadlineControlTask = await db.TaskJournalItems.SingleAsync(item =>
            item.ClientCaseId == clientCase.Id
            && item.TagsJson.Contains($"case-deadline:entity-{deadline.Id}"));
        Assert.Equal("new", deadlineControlTask.Status);

        var notification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CourtHearing);
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("Судебное заседание", notification.BodyPreview, StringComparison.Ordinal);
        Assert.Contains($"\"courtEventId\":{courtEvent.Id}", notification.MetadataJson, StringComparison.Ordinal);

        var deadlineNotification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CaseProceduralDeadline);
        Assert.Equal(responsible.Id, deadlineNotification.RecipientUserId);
        Assert.Equal("case", deadlineNotification.ObjectType);
        Assert.Equal(clientCase.Id, deadlineNotification.ObjectId);
        Assert.Contains("Срок кассационного обжалования", deadlineNotification.BodyPreview, StringComparison.Ordinal);
        Assert.Contains($"\"proceduralDeadlineId\":{deadline.Id}", deadlineNotification.MetadataJson, StringComparison.Ordinal);

        db.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = clientCase.Id,
            EventType = "hearing",
            Title = "Заседание другой инстанции",
            ScheduledAt = new DateTime(2026, 8, 10, 10, 0, 0),
            SourceType = "sudrf",
            SourceUrl = "https://another.sudrf.ru/case/100",
            ExternalKey = "sudrf-event-another-source",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var changedResultController = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(new SudrfCourtCaseData
            {
                SourceUrl = clientCase.CourtUrl,
                CourtName = courtData.CourtName,
                CaseNumber = courtData.CaseNumber,
                JudgeName = courtData.JudgeName,
                Instance = courtData.Instance,
                CourtActs =
                [
                    new()
                    {
                        Title = "Апелляционное определение",
                        ActDate = new DateTime(2026, 6, 1),
                        PostedAt = new DateTime(2026, 6, 3),
                        Result = "Результат дополнен после публикации",
                        Summary = "Апелляционное определение; обновлённый результат",
                        SourceUrl = clientCase.CourtUrl,
                    },
                ],
            }),
            notificationService: CreateNotificationService(db));

        var repeatedResult = await changedResultController.RunCourtSync(clientCase.Id, null, CancellationToken.None);
        var repeatedOk = Assert.IsType<OkObjectResult>(repeatedResult.Result);
        var repeatedDto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(repeatedOk.Value);
        Assert.Equal(0, repeatedDto.CreatedCourtActDocuments);
        Assert.Contains(repeatedDto.RemovedItems, item => item.Contains("судебное событие", StringComparison.Ordinal));
        Assert.Contains(repeatedDto.RemovedItems, item => item.Contains("участник процесса", StringComparison.Ordinal));
        Assert.DoesNotContain(repeatedDto.RemovedItems, item => item.Contains("другой инстанции", StringComparison.Ordinal));
        Assert.Equal(2, await db.ClientCaseCourtEvents.CountAsync(item => item.ClientCaseId == clientCase.Id));
        Assert.Equal("archived", (await db.ClientCaseCourtEvents.SingleAsync(item => item.Id == courtEvent.Id)).MovementStatus);
        Assert.Equal("cancelled", (await db.TaskJournalItems.SingleAsync(item => item.Id == hearingTask.Id)).Status);
        Assert.Equal(3, await db.ClientCaseCourtParties.CountAsync(item => item.ClientCaseId == clientCase.Id && item.IsArchived));
        Assert.Equal(1, await db.ClientCaseDocuments.CountAsync(item =>
            item.ClientCaseId == clientCase.Id
            && item.DocumentType == "Судебный акт"));

        var emptySourceController = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(new SudrfCourtCaseData
            {
                SourceUrl = clientCase.CourtUrl,
                CourtName = courtData.CourtName,
                CaseNumber = courtData.CaseNumber,
                JudgeName = courtData.JudgeName,
                Instance = courtData.Instance,
            }),
            notificationService: CreateNotificationService(db));
        var emptySourceResult = await emptySourceController.RunCourtSync(clientCase.Id, null, CancellationToken.None);
        var emptySourceOk = Assert.IsType<OkObjectResult>(emptySourceResult.Result);
        var emptySourceDto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(emptySourceOk.Value);
        Assert.Contains(emptySourceDto.RemovedItems, item => item.Contains("судебный акт", StringComparison.Ordinal));
        Assert.Contains(emptySourceDto.RemovedItems, item => item.Contains("процессуальный срок", StringComparison.Ordinal));
        Assert.Equal("archived", (await db.ClientCaseDocuments.SingleAsync(item => item.Id == document.Id)).Status);
        Assert.Equal("archived", (await db.ClientCaseProceduralDeadlines.SingleAsync(item => item.Id == deadline.Id)).Status);
        Assert.Equal("cancelled", (await db.TaskJournalItems.SingleAsync(item => item.Id == deadlineControlTask.Id)).Status);
        var archivedCalendarResult = await emptySourceController.GetCaseCalendarEvents(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 9, 30),
            clientCaseId: clientCase.Id,
            responsibleUserId: null,
            includeTasks: true,
            ct: CancellationToken.None);
        var archivedCalendarOk = Assert.IsType<OkObjectResult>(archivedCalendarResult.Result);
        var archivedCalendarEvents = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseCalendarEventDto>>(archivedCalendarOk.Value);
        Assert.DoesNotContain(archivedCalendarEvents, item =>
            (item.Kind == "court" && item.Title == "Судебное заседание")
            || (item.Kind == "procedural-deadline" && item.DeadlineType == "cassation")
            || (item.Kind == "task" && item.SourceId == deadlineControlTask.Id));

        var restoreController = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData),
            notificationService: CreateNotificationService(db));
        var restoreResult = await restoreController.RunCourtSync(clientCase.Id, null, CancellationToken.None);
        var restoreOk = Assert.IsType<OkObjectResult>(restoreResult.Result);
        var restoreDto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(restoreOk.Value);
        Assert.Contains(restoreDto.ChangedItems, item => item.Contains("восстановлен из архива", StringComparison.Ordinal));
        Assert.NotEqual("archived", (await db.ClientCaseCourtEvents.SingleAsync(item => item.Id == courtEvent.Id)).MovementStatus);
        Assert.Equal(3, await db.ClientCaseCourtParties.CountAsync(item => item.ClientCaseId == clientCase.Id && !item.IsArchived));
        Assert.Equal("received", (await db.ClientCaseDocuments.SingleAsync(item => item.Id == document.Id)).Status);
        Assert.Equal("active", (await db.ClientCaseProceduralDeadlines.SingleAsync(item => item.Id == deadline.Id)).Status);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item =>
            item.ClientCaseId == clientCase.Id
            && item.Status != "cancelled"
            && item.TagsJson.Contains($"case-deadline:court-event-{courtEvent.Id}")));
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item =>
            item.ClientCaseId == clientCase.Id
            && item.Status != "cancelled"
            && item.TagsJson.Contains($"case-deadline:entity-{deadline.Id}")));
    }

    [Fact]
    public async Task RunCourtSync_ImportsFullMovementAndRecognizesDecisionOutcome()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-movement-owner@example.com", "Владелец");
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = owner.Id };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Семейный спор",
            Stage = "Судебное производство",
            CourtUrl = "https://test--sam.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=1540005",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Тестовый городской суд",
            CaseNumber = "2-1500/2026",
            Instance = "first",
            DecisionAt = new DateTime(2026, 8, 21),
            CaseOutcome = "Иск удовлетворен частично",
            MovementEvents =
            [
                new()
                {
                    Title = "Регистрация иска (заявления, жалобы) в суде",
                    ScheduledAt = new DateTime(2026, 4, 13, 19, 12, 0),
                    SourceSummary = "Регистрация иска; 13.04.2026 19:12",
                },
                new()
                {
                    Title = "Судебное заседание",
                    ScheduledAt = new DateTime(2026, 6, 4, 17, 0, 0),
                    Result = "Заседание отложено",
                    Reason = "Истребование доказательств",
                    SourceSummary = "Судебное заседание; 04.06.2026 17:00; результат: Заседание отложено",
                },
                new()
                {
                    Title = "Судебное заседание",
                    ScheduledAt = new DateTime(2026, 8, 21, 10, 0, 0),
                    Result = "Иск удовлетворен частично",
                    SourceSummary = "Судебное заседание; 21.08.2026 10:00; результат: Иск удовлетворен частично",
                },
                new()
                {
                    Title = "Направлена апелляционная жалоба на решение суда",
                    ScheduledAt = new DateTime(2026, 9, 15),
                    SourceSummary = "Направлена апелляционная жалоба на решение суда; 15.09.2026",
                },
            ],
        };
        var controller = CreateController(db, owner, sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData));

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);
        Assert.Equal(4, dto.CreatedCourtEvents);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(new DateTime(2026, 8, 21), clientCase.CourtDecisionAt);

        var events = await db.ClientCaseCourtEvents
            .Where(item => item.ClientCaseId == clientCase.Id)
            .OrderBy(item => item.ScheduledAt)
            .ToListAsync();
        Assert.Equal(4, events.Count);
        Assert.Equal(CourtSyncNormalization.EventTypes.Claim, events[0].EventType);
        Assert.Equal(CourtSyncNormalization.EventTypes.Postponement, events[1].EventType);
        Assert.Equal(CourtSyncNormalization.MovementStatuses.Postponed, events[1].MovementStatus);
        Assert.Equal(CourtSyncNormalization.EventTypes.Decision, events[2].EventType);
        Assert.Equal("Иск удовлетворен частично", events[2].ResultSummary);
        Assert.Equal(CourtSyncNormalization.EventTypes.Movement, events[3].EventType);
        Assert.Contains("апелляционная жалоба", events[3].Title, StringComparison.OrdinalIgnoreCase);
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
    public async Task RunCourtSync_NotifiesResponsibleWhenExternalSourceFails()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-sync-error-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-sync-error-responsible@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Клиент с ошибкой суда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с ошибкой источника",
            CaseNumber = "CASE-SYNC-ERR",
            CourtUrl = "https://example.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
            CourtName = "Районный суд",
            CourtCaseNumber = "2-100/2026",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new ThrowingSudrfCourtSyncService("Источник временно недоступен"),
            notificationService: CreateNotificationService(db));

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);
        Assert.Equal("external-sync-error", dto.Status);
        Assert.NotNull(clientCase.CourtSyncFailureStartedAt);
        Assert.Equal(clientCase.CourtSyncLastAttemptAt, clientCase.CourtSyncFailureStartedAt);
        Assert.Equal("Источник временно недоступен", clientCase.CourtSyncLastError);

        owner.Role!.Name = "admin";
        clientCase.CourtSyncFailureStartedAt = DateTime.UtcNow.AddHours(-25);
        await db.SaveChangesAsync();
        var failureStartedAt = clientCase.CourtSyncFailureStartedAt;
        await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.Equal(failureStartedAt, clientCase.CourtSyncFailureStartedAt);
        var adminWarning = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.SystemAdminWarning);
        Assert.Equal(owner.Id, adminWarning.RecipientUserId);
        Assert.Equal(clientCase.Id, adminWarning.ObjectId);

        var recoveredController = CreateController(db, owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(new SudrfCourtCaseData
            {
                SourceUrl = clientCase.CourtUrl,
                CourtName = clientCase.CourtName,
                CaseNumber = clientCase.CourtCaseNumber,
            }));
        await recoveredController.RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.Null(clientCase.CourtSyncFailureStartedAt);
        Assert.Equal(clientCase.CourtSyncLastAttemptAt, clientCase.CourtSyncLastSuccessAt);
        Assert.Empty(clientCase.CourtSyncLastError);

        var notification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CourtSyncFailed);
        Assert.Equal(UserNotificationTypes.CourtSyncFailed, notification.Type);
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("Источник временно недоступен", notification.BodyPreview, StringComparison.Ordinal);
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
        Assert.Equal(0, dto.UpdatedCourtEvents);
        Assert.Contains(dto.ConflictItems, item => item.Contains("ручное событие", StringComparison.Ordinal));

        var events = await db.ClientCaseCourtEvents
            .Where(item => item.ClientCaseId == clientCase.Id)
            .ToListAsync();
        var courtEvent = Assert.Single(events);
        Assert.Equal(manualEvent.Id, courtEvent.Id);
        Assert.Equal("manual", courtEvent.SourceType);
        Assert.Equal("Предварительное судебное заседание", courtEvent.Title);
        Assert.Empty(courtEvent.CourtName);
        Assert.Equal("зал 12", courtEvent.Courtroom);

        Assert.Empty(await db.ClientCaseCourtEventHistory.Where(item => item.ClientCaseCourtEventId == courtEvent.Id).ToListAsync());
    }

    [Fact]
    public async Task RunCourtSync_ReturnsNoChangesWhenSourceMatchesCase()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-sync-no-changes@example.com", "Владелец");
        var hearingAt = new DateTime(2026, 5, 27, 15, 0, 0);
        var client = new Client
        {
            Name = "Клиент без изменений суда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело без изменений суда",
            CourtUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=88",
            CourtName = "Хабаровский краевой суд",
            CourtCaseNumber = "33-88/2026",
            JudgeName = "Казарикова Оксана Витальевна",
            Courtroom = "зал 12",
            NextHearingAt = hearingAt,
            Status = ClientCaseStatus.Open,
        };
        var courtEvent = new ClientCaseCourtEvent
        {
            ClientCase = clientCase,
            EventType = "hearing",
            Title = "Судебное заседание",
            ScheduledAt = hearingAt,
            AllDay = false,
            CourtName = "Хабаровский краевой суд",
            CourtCaseNumber = "33-88/2026",
            Courtroom = "зал 12",
            JudgeName = "Казарикова Оксана Витальевна",
            Description = "Судебное заседание; 27.05.2026 15:00; место: зал 12",
            MovementStatus = "scheduled",
            SourceType = "sudrf",
            CourtInstance = "appeal",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseCourtEvents.Add(courtEvent);
        await db.SaveChangesAsync();

        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Хабаровский краевой суд",
            CaseNumber = "33-88/2026",
            JudgeName = "Казарикова Оксана Витальевна",
            Instance = "appeal",
            NextHearingAt = hearingAt,
            NextHearingPlace = "зал 12",
            MovementEvents =
            [
                new()
                {
                    Title = "Судебное заседание",
                    ScheduledAt = hearingAt,
                    Place = "зал 12",
                    SourceSummary = "Судебное заседание; 27.05.2026 15:00; место: зал 12",
                },
            ],
        };
        var controller = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData),
            notificationService: CreateNotificationService(db));

        var result = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);
        Assert.Equal("external-sync-completed", dto.Status);
        Assert.Empty(dto.UpdatedFields);
        Assert.Equal(0, dto.CreatedCourtEvents);
        Assert.Equal(0, dto.UpdatedCourtEvents);
        Assert.Equal(0, dto.CreatedCourtParties);
        Assert.Equal(0, dto.CreatedCourtActDocuments);
        Assert.Equal(0, dto.CreatedProceduralDeadlines);
        Assert.Contains("новых изменений не найдено", dto.Message, StringComparison.Ordinal);

        Assert.Equal(1, await db.ClientCaseCourtEvents.CountAsync(item => item.ClientCaseId == clientCase.Id));
        Assert.StartsWith("sudrf-event-", courtEvent.ExternalKey, StringComparison.Ordinal);
        Assert.Empty(await db.ClientCaseCourtEventHistory.Where(item => item.ClientCaseCourtEventId == courtEvent.Id).ToListAsync());
        Assert.Empty(await db.UserNotifications.ToListAsync());

        var hearingTask = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("courtHearing", hearingTask.Type);
        Assert.Equal(owner.Id, hearingTask.AssigneeUserId);
        Assert.Equal(hearingAt.AddDays(-3), hearingTask.DueAt);
        Assert.Contains($"case-deadline:court-event-{courtEvent.Id}", hearingTask.TagsJson, StringComparison.Ordinal);
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
        Assert.All(logs, item => Assert.Equal("sudrf", item.ProviderKey));
        Assert.All(logs, item => Assert.Equal("ГАС Правосудие / sudrf.ru", item.ProviderLabel));
        Assert.Single(logs, item => item.HasError);
        Assert.All(logs, item => Assert.NotNull(item.AgeDays));

        var filteredResult = await controller.GetCourtSyncJournal(
            take: 50,
            status: null,
            search: "ВРЕМЕННО НЕДОСТУПЕН",
            ct: CancellationToken.None,
            provider: "sudrf",
            hasError: true,
            minAgeDays: 30);
        var filteredOk = Assert.IsType<OkObjectResult>(filteredResult.Result);
        var filteredLogs = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncJournalEntryDto>>(filteredOk.Value);
        var filteredLog = Assert.Single(filteredLogs);
        Assert.Equal("external-sync-error", filteredLog.Status);
        Assert.True(filteredLog.HasError);

        var recentResult = await controller.GetCourtSyncJournal(
            take: 50,
            status: null,
            search: null,
            ct: CancellationToken.None,
            provider: "sudrf",
            maxAgeDays: 30);
        var recentOk = Assert.IsType<OkObjectResult>(recentResult.Result);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncJournalEntryDto>>(recentOk.Value));
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
        Assert.Equal(1, sudrf.SchemaVersion);
        Assert.Contains("case-search", sudrf.Capabilities);
        Assert.Contains("case-reading", sudrf.Capabilities);
        Assert.Contains("background-sync", sudrf.Capabilities);
        Assert.DoesNotContain("document-download", sudrf.Capabilities);
        Assert.DoesNotContain("change-detection", sudrf.Capabilities);
        Assert.Contains("sudrf.ru", sudrf.SupportedHosts);
        Assert.Contains(providers, item =>
            item.Key == "kadArbitr"
            && item.Status == "planned"
            && item.SchemaVersion == 1
            && item.Capabilities.Count == 0);
    }

    [Fact]
    public async Task SearchCourtSyncCases_ReturnsProviderResults()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-search-owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - поиск суда",
            Status = ClientCaseStatus.Open,
            CourtUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
            CourtCaseNumber = "33а-3390/2026",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = clientCase.CourtUrl,
            CourtName = "Хабаровский краевой суд",
            CaseNumber = "33а-3390/2026",
            JudicialUid = "27RS0005-01-2025-002659-56",
            Instance = "appeal",
            JudgeName = "Казарикова Оксана Витальевна",
        };
        var controller = CreateController(db, owner, sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData));

        var result = await controller.SearchCourtSyncCases(
            clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncSearchRequest
            {
                SourceUrl = clientCase.CourtUrl,
                CourtCaseNumber = clientCase.CourtCaseNumber,
                Instance = "appeal",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var results = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncSearchResultDto>>(ok.Value);
        var item = Assert.Single(results);
        Assert.Equal("sudrf", item.ProviderKey);
        Assert.Equal("Хабаровский краевой суд", item.CourtName);
        Assert.Equal("33а-3390/2026", item.CourtCaseNumber);
        Assert.Equal("27RS0005-01-2025-002659-56", item.JudicialUid);
        Assert.Equal("appeal", item.Instance);
        Assert.True(item.CanFetch);
        Assert.False(item.RequiresReview);
        Assert.Collection(
            item.Differences.OrderBy(difference => difference.Field),
            difference =>
            {
                Assert.Equal("courtName", difference.Field);
                Assert.Equal("fill", difference.Kind);
                Assert.Empty(difference.CurrentValue);
                Assert.Equal("Хабаровский краевой суд", difference.SourceValue);
            },
            difference =>
            {
                Assert.Equal("judgeName", difference.Field);
                Assert.Equal("fill", difference.Kind);
                Assert.Empty(difference.CurrentValue);
                Assert.Equal("Казарикова Оксана Витальевна", difference.SourceValue);
            });
    }

    [Fact]
    public async Task BindCourtSyncCase_RejectsReplacingExistingValuesWithoutConfirmation()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-bind-review-owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент с существующей судебной карточкой",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с проверкой замены",
            Status = ClientCaseStatus.Open,
            CourtName = "Старый суд",
            CourtCaseNumber = "СТАРЫЙ-1",
            CourtUrl = "https://example.sudrf.ru/old-card",
            JudgeName = "Старый судья",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = "https://example.sudrf.ru/new-card",
            CourtName = "Новый суд",
            CaseNumber = "НОВЫЙ-2",
            JudgeName = "Новый судья",
        };
        var controller = CreateController(db, owner, sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData));

        var result = await controller.BindCourtSyncCase(
            clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncBindRequest
            {
                SourceUrl = courtData.SourceUrl,
                CourtName = courtData.CourtName,
                CourtCaseNumber = courtData.CaseNumber,
                JudgeName = courtData.JudgeName,
            },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.NotNull(conflict.Value);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("Старый суд", clientCase.CourtName);
        Assert.Equal("СТАРЫЙ-1", clientCase.CourtCaseNumber);
        Assert.Equal("https://example.sudrf.ru/old-card", clientCase.CourtUrl);
        Assert.Empty(await db.ClientCaseCourtInstances.ToListAsync());
    }

    [Fact]
    public async Task BindCourtSyncCase_UpdatesCaseAndCourtInstance()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-bind-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-bind-responsible@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - привязка суда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        var firstInstance = new ClientCaseCourtInstance
        {
            ClientCase = clientCase,
            Instance = "first",
            JudicialUid = "УСТАРЕВШИЙ-UID",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseCourtInstances.Add(firstInstance);
        await db.SaveChangesAsync();

        var courtData = new SudrfCourtCaseData
        {
            SourceUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
            CourtName = "Хабаровский краевой суд",
            CaseNumber = "33а-3390/2026",
            JudicialUid = "27RS0005-01-2025-002659-56",
            Instance = "appeal",
            JudgeName = "Казарикова Оксана Витальевна",
            MovementEvents =
            [
                new SudrfCourtMovementEvent
                {
                    Title = "Судебное заседание",
                    ScheduledAt = new DateTime(2026, 5, 27, 15, 0, 0),
                    Place = "зал 12",
                    SourceSummary = "Судебное заседание назначено на 27.05.2026 15:00.",
                },
            ],
        };
        var controller = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new FixedSudrfCourtSyncService(courtData),
            notificationService: CreateNotificationService(db));

        var result = await controller.BindCourtSyncCase(
            clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncBindRequest
            {
                Instance = "appeal",
                ProviderKey = "sudrf",
                SourceType = "sudrf",
                SourceUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
                CourtName = "Хабаровский краевой суд",
                CourtCaseNumber = "33а-3390/2026",
                JudicialUid = "27RS0005-01-2025-002659-56",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(ok.Value);
        Assert.Equal("appeal", dto.Instance);
        Assert.Equal("Хабаровский краевой суд", dto.CourtName);
        Assert.Equal("33а-3390/2026", dto.CourtCaseNumber);
        Assert.Equal("27RS0005-01-2025-002659-56", dto.JudicialUid);
        Assert.True(dto.SyncedFromExternalSource);
        Assert.Equal(1, dto.CreatedCourtEvents);
        Assert.Contains("Судебный источник обновлен", dto.Message);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("Хабаровский краевой суд", clientCase.CourtName);
        Assert.Equal("33а-3390/2026", clientCase.CourtCaseNumber);
        Assert.NotNull(clientCase.CourtSyncLastAttemptAt);
        Assert.Equal(clientCase.CourtSyncLastAttemptAt, clientCase.CourtSyncLastSuccessAt);
        Assert.Null(clientCase.CourtSyncFailureStartedAt);
        Assert.Empty(clientCase.CourtSyncLastError);
        Assert.Equal("27RS0005-01-2025-002659-56", clientCase.JudicialUid);
        Assert.Equal("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42", clientCase.CourtUrl);
        Assert.Contains("Привязана судебная карточка", clientCase.EventHistory);
        Assert.Contains("27RS0005-01-2025-002659-56", clientCase.EventHistory);

        var courtInstance = await db.ClientCaseCourtInstances.SingleAsync(item => item.ClientCaseId == clientCase.Id && item.Instance == "appeal");
        Assert.Equal("appeal", courtInstance.Instance);
        Assert.Equal("Хабаровский краевой суд", courtInstance.CourtName);
        Assert.Equal("33а-3390/2026", courtInstance.CourtCaseNumber);
        Assert.Equal("27RS0005-01-2025-002659-56", courtInstance.JudicialUid);
        Assert.Equal(clientCase.CourtUrl, courtInstance.CourtUrl);
        Assert.All(
            await db.ClientCaseCourtInstances.Where(item => item.ClientCaseId == clientCase.Id).ToListAsync(),
            item => Assert.Equal("27RS0005-01-2025-002659-56", item.JudicialUid));

        var courtEvent = await db.ClientCaseCourtEvents.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("appeal", courtEvent.CourtInstance);
        Assert.Equal("Судебное заседание", courtEvent.Title);
        Assert.Equal("зал 12", courtEvent.Courtroom);
        Assert.Equal("sudrf", courtEvent.SourceType);

        var notification = await db.UserNotifications.SingleAsync(item => item.Type == UserNotificationTypes.CourtHearing);
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Contains("Судебное заседание", notification.BodyPreview, StringComparison.Ordinal);
        Assert.Contains($"\"courtEventId\":{courtEvent.Id}", notification.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindCourtSyncCase_NotifiesResponsibleWhenInitialSyncFails()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "court-bind-error-owner@example.com", "Владелец");
        var responsible = await CreateUserAsync(db, "court-bind-error-responsible@example.com", "Ответственный");
        var client = new Client
        {
            Name = "Клиент с ошибкой привязки суда",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с ошибкой первичной синхронизации",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var controller = CreateController(
            db,
            owner,
            sudrfCourtSyncService: new ThrowingSudrfCourtSyncService("Судебный источник не ответил"),
            notificationService: CreateNotificationService(db));

        var result = await controller.BindCourtSyncCase(
            clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncBindRequest
            {
                Instance = "appeal",
                ProviderKey = "sudrf",
                SourceType = "sudrf",
                SourceUrl = "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42",
                CourtName = "Хабаровский краевой суд",
                CourtCaseNumber = "33а-3390/2026",
                JudicialUid = "27RS0005-01-2025-002659-56",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(ok.Value);
        Assert.False(dto.SyncedFromExternalSource);
        Assert.Equal("Судебный источник не ответил", dto.SyncError);
        Assert.Contains("синхронизация не выполнена", dto.Message, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("Хабаровский краевой суд", clientCase.CourtName);
        Assert.Equal("33а-3390/2026", clientCase.CourtCaseNumber);
        Assert.NotNull(clientCase.CourtSyncFailureStartedAt);
        Assert.Equal("Судебный источник не ответил", clientCase.CourtSyncLastError);
        Assert.Contains("Автоматическая синхронизация после привязки не выполнена", clientCase.EventHistory, StringComparison.Ordinal);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(UserNotificationTypes.CourtSyncFailed, notification.Type);
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("Судебный источник не ответил", notification.BodyPreview, StringComparison.Ordinal);
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
    public async Task RelatedParties_ForbidListWithoutCaseViewPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "related-parties-no-view@example.com", "Без просмотра дел");
        user.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanView = false;
        var client = new Client
        {
            Name = "Клиент закрытых связей",
            IsActive = true,
            ResponsibleUserId = user.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело со связанными лицами",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = user.Id,
        };
        var relatedClient = new Client
        {
            Name = "Связанное лицо",
            IsActive = true,
            ResponsibleUserId = user.Id,
        };
        db.Clients.AddRange(client, relatedClient);
        db.ClientCases.Add(clientCase);
        db.ClientCaseRelatedParties.Add(new ClientCaseRelatedParty
        {
            ClientCase = clientCase,
            SourceType = "client",
            Client = relatedClient,
            DisplayName = relatedClient.Name,
            Role = "Свидетель",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user);

        var result = await controller.GetRelatedParties(clientCase.Id, includeArchived: true, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task RelatedParties_ReturnNotFoundForHiddenCaseActions()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "related-parties-hidden-viewer@example.com", "Наблюдатель связанных лиц");
        var otherResponsible = await CreateUserAsync(db, "related-parties-hidden-owner@example.com", "Другой ответственный");
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент связанных лиц",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытое дело со связанными лицами",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        var visibleRelatedClient = new Client
        {
            Name = "Видимое связанное лицо",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var hiddenRelatedParty = new ClientCaseRelatedParty
        {
            ClientCase = hiddenCase,
            SourceType = "client",
            Client = visibleRelatedClient,
            DisplayName = visibleRelatedClient.Name,
            Role = "Свидетель",
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.AddRange(hiddenClient, visibleRelatedClient);
        db.ClientCases.Add(hiddenCase);
        db.ClientCaseRelatedParties.Add(hiddenRelatedParty);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var listResult = await controller.GetRelatedParties(hiddenCase.Id, includeArchived: true, CancellationToken.None);
        var addResult = await controller.AddRelatedParty(hiddenCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = visibleRelatedClient.Id,
            Role = "Представитель",
        }, CancellationToken.None);
        var deleteResult = await controller.DeleteRelatedParty(hiddenCase.Id, hiddenRelatedParty.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(listResult.Result);
        Assert.IsType<NotFoundResult>(addResult.Result);
        Assert.IsType<NotFoundResult>(deleteResult);
        var reloadedParty = await db.ClientCaseRelatedParties.AsNoTracking().SingleAsync(item => item.Id == hiddenRelatedParty.Id);
        Assert.False(reloadedParty.IsArchived);
        Assert.Equal("Свидетель", reloadedParty.Role);
        Assert.Single(await db.ClientCaseRelatedParties.AsNoTracking().Where(item => item.ClientCaseId == hiddenCase.Id).ToListAsync());
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
    public async Task GetExternalWorkOrderApproval_ReusesSingleAccessSnapshotAndReturnsStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var owner = await CreateUserAsync(db, "external-approval-status@example.com", "Владелец согласования");
        var clientCase = new ClientCase
        {
            Client = new Client
            {
                Name = "Клиент внешнего согласования",
                IsActive = true,
                ResponsibleUserId = owner.Id,
            },
            Title = "Дело внешнего согласования",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var service = new RecordingExternalWorkOrderApprovalService
        {
            LatestStatus = new ExternalWorkOrderApprovalStatusDto
            {
                PublicId = Guid.NewGuid(),
                Version = 2,
                Status = "opened",
                RecipientName = "Представитель клиента",
                RecipientEmail = "representative@example.com",
            },
        };
        var controller = CreateController(db, owner, externalWorkOrderApprovalService: service);
        interceptor.Commands.Clear();

        var result = await controller.GetExternalWorkOrderApproval(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<ExternalWorkOrderApprovalStatusDto>(ok.Value);
        Assert.Equal("opened", status.Status);
        Assert.Equal(clientCase.Id, service.LastCaseId);
        Assert.Equal(1, service.GetCalls);
        Assert.Equal(2, interceptor.Commands.Count);
        Assert.Contains(interceptor.Commands, command => command.Contains("RolePermissions", StringComparison.Ordinal));
        Assert.Contains(interceptor.Commands, command => command.Contains("ClientCases", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateExternalWorkOrderApproval_PassesActorAndRequestToService()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "external-approval-create@example.com", "Анна Постановщик");
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = owner.Id },
            Title = "Дело для отправки согласования",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var service = new RecordingExternalWorkOrderApprovalService
        {
            CreatedStatus = new ExternalWorkOrderApprovalStatusDto
            {
                PublicId = Guid.NewGuid(),
                Version = 1,
                Status = "pending",
                RecipientName = "Клиент",
                RecipientEmail = "client@example.com",
            },
        };
        var controller = CreateController(db, owner, externalWorkOrderApprovalService: service);
        var request = new CreateExternalWorkOrderApprovalRequest
        {
            RecipientName = "Клиент",
            RecipientEmail = "client@example.com",
            RecipientType = "client",
        };

        var result = await controller.CreateExternalWorkOrderApproval(clientCase.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(service.CreatedStatus, ok.Value);
        Assert.Same(request, service.LastCreateRequest);
        Assert.Equal(owner.Id, service.LastActorUserId);
        Assert.Equal(owner.FullName, service.LastActorName);
        Assert.Equal(1, service.CreateCalls);
    }

    [Fact]
    public async Task RevokeExternalWorkOrderApproval_ReturnsNotFoundWhenActiveRequestIsMissing()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "external-approval-revoke@example.com", "Сотрудник отзыва");
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = owner.Id },
            Title = "Дело без активного согласования",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var service = new RecordingExternalWorkOrderApprovalService { RevokeResult = false };
        var controller = CreateController(db, owner, externalWorkOrderApprovalService: service);

        var result = await controller.RevokeExternalWorkOrderApproval(clientCase.Id, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var message = notFound.Value?.GetType().GetProperty("message")?.GetValue(notFound.Value) as string;
        Assert.Contains("не найден", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(owner.Id, service.LastActorUserId);
        Assert.Equal(1, service.RevokeCalls);
    }

    [Fact]
    public async Task ExternalWorkOrderApproval_DoesNotCallServiceWithoutEditPermissionOrCaseVisibility()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "external-approval-viewer@example.com", "Наблюдатель");
        var other = await CreateUserAsync(db, "external-approval-other@example.com", "Другой ответственный");
        viewer.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanEdit = false;
        var visibleCase = new ClientCase
        {
            Client = new Client { Name = "Видимый клиент", IsActive = true, ResponsibleUserId = viewer.Id },
            Title = "Видимое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = viewer.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = new Client { Name = "Скрытый клиент", IsActive = true, ResponsibleUserId = other.Id },
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = other.Id,
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();
        var service = new RecordingExternalWorkOrderApprovalService();

        var editController = CreateController(db, viewer, externalWorkOrderApprovalService: service);
        var createResult = await editController.CreateExternalWorkOrderApproval(
            visibleCase.Id,
            new CreateExternalWorkOrderApprovalRequest(),
            CancellationToken.None);
        var readController = CreateController(db, viewer, externalWorkOrderApprovalService: service);
        var getResult = await readController.GetExternalWorkOrderApproval(hiddenCase.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(createResult.Result);
        Assert.IsType<NotFoundResult>(getResult.Result);
        Assert.Equal(0, service.CreateCalls);
        Assert.Equal(0, service.GetCalls);
    }

    [Fact]
    public async Task WorkOrder_ForbidsReadAndPrintWithoutCaseViewPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "work-order-no-view@example.com", "Без просмотра дел");
        user.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanView = false;
        var client = new Client
        {
            Name = "Клиент закрытого заказ-наряда",
            IsActive = true,
            ResponsibleUserId = user.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с закрытым заказ-нарядом",
            CaseNumber = "WO-NO-VIEW",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = user.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Закрытый заказ-наряд",
            Status = "approved",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка документов",
                    Quantity = 1,
                    UnitPrice = 15000,
                    TotalAmount = 15000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user);

        var getResult = await controller.GetWorkOrder(clientCase.Id, CancellationToken.None);
        var printResult = await controller.PrintWorkOrder(clientCase.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(getResult.Result);
        Assert.IsType<ForbidResult>(printResult);
    }

    [Fact]
    public async Task WorkOrder_ReturnsNotFoundForHiddenCaseActions()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "work-order-hidden-viewer@example.com", "Наблюдатель заказ-наряда");
        var otherResponsible = await CreateUserAsync(db, "work-order-hidden-owner@example.com", "Другой ответственный");
        var client = new Client
        {
            Name = "Скрытый клиент заказ-наряда",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело с заказ-нарядом",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Скрытый заказ-наряд",
            Status = "draft",
            Currency = "RUB",
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Скрытая услуга",
                    Quantity = 1,
                    UnitPrice = 1000,
                    TotalAmount = 1000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var itemId = workOrder.Items.Single().Id;
        var controller = CreateController(db, viewer);

        var getResult = await controller.GetWorkOrder(clientCase.Id, CancellationToken.None);
        var printResult = await controller.PrintWorkOrder(clientCase.Id, CancellationToken.None);
        var updateResult = await controller.UpdateWorkOrder(clientCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderRequest
        {
            Title = "Попытка изменения",
            Status = "draft",
        }, CancellationToken.None);
        var addItemResult = await controller.AddWorkOrderItem(clientCase.Id, new CasesV2Controller.UpsertClientCaseWorkOrderItemRequest
        {
            ServiceName = "Новая скрытая услуга",
            Quantity = 1,
            UnitPrice = 2000,
        }, CancellationToken.None);
        var updateItemResult = await controller.UpdateWorkOrderItem(clientCase.Id, itemId, new CasesV2Controller.UpsertClientCaseWorkOrderItemRequest
        {
            ServiceName = "Измененная скрытая услуга",
            Quantity = 2,
            UnitPrice = 2000,
        }, CancellationToken.None);
        var deleteItemResult = await controller.DeleteWorkOrderItem(clientCase.Id, itemId, CancellationToken.None);
        var submitResult = await controller.SubmitWorkOrderApproval(clientCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "На согласование",
        }, CancellationToken.None);
        var deleteResult = await controller.DeleteWorkOrder(clientCase.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(getResult.Result);
        Assert.IsType<NotFoundResult>(printResult);
        Assert.IsType<NotFoundResult>(updateResult.Result);
        Assert.IsType<NotFoundResult>(addItemResult.Result);
        Assert.IsType<NotFoundResult>(updateItemResult.Result);
        Assert.IsType<NotFoundResult>(deleteItemResult.Result);
        Assert.IsType<NotFoundResult>(submitResult.Result);
        Assert.IsType<NotFoundResult>(deleteResult);
        var reloaded = await db.ClientCaseWorkOrders
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == workOrder.Id);
        Assert.Equal("Скрытый заказ-наряд", reloaded.Title);
        Assert.Equal("draft", reloaded.Status);
        Assert.Single(reloaded.Items);
    }

    [Fact]
    public async Task WorkOrderDerivedActions_ReturnNotFoundForHiddenCase()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "work-order-hidden-derived@example.com", "Документный пользователь");
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Accounts);
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Acts);
        await GrantSectionPermissionAsync(db, viewer, RolePermissionCatalog.Contracts);
        var otherResponsible = await CreateUserAsync(db, "work-order-hidden-derived-owner@example.com", "Другой ответственный");
        var client = new Client
        {
            Name = "Скрытый клиент документов заказ-наряда",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Скрытое дело с документами заказ-наряда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Скрытый заказ-наряд для документов",
            Status = "pending_approval",
            Currency = "RUB",
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Скрытая услуга для документов",
                    Quantity = 1,
                    UnitPrice = 1000,
                    TotalAmount = 1000,
                    SortOrder = 1,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var generateResult = await controller.GenerateWorkOrderDocument(clientCase.Id, CancellationToken.None);
        var invoiceResult = await controller.CreateInvoiceFromWorkOrder(clientCase.Id, new CasesV2Controller.CreateClientCaseWorkOrderInvoiceRequest(), CancellationToken.None);
        var actResult = await controller.CreateActFromWorkOrder(clientCase.Id, new CasesV2Controller.CreateClientCaseWorkOrderActRequest(), CancellationToken.None);
        var contractResult = await controller.CreateContractFromWorkOrder(clientCase.Id, new CasesV2Controller.CreateClientCaseWorkOrderContractRequest(), CancellationToken.None);
        var approveResult = await controller.ApproveWorkOrder(clientCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "Согласовано",
        }, CancellationToken.None);
        var revisionResult = await controller.RequestWorkOrderRevision(clientCase.Id, new CasesV2Controller.ClientCaseWorkOrderApprovalRequest
        {
            Comment = "На доработку",
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(generateResult.Result);
        Assert.IsType<NotFoundResult>(invoiceResult.Result);
        Assert.IsType<NotFoundResult>(actResult.Result);
        Assert.IsType<NotFoundResult>(contractResult.Result);
        Assert.IsType<NotFoundResult>(approveResult.Result);
        Assert.IsType<NotFoundResult>(revisionResult.Result);
        Assert.False(await db.ClientCaseDocuments.AnyAsync(item => item.ClientCaseId == clientCase.Id));
        Assert.False(await db.Payments.AnyAsync(item => item.ClientCaseId == clientCase.Id));
        Assert.False(await db.Acts.AnyAsync(item => item.ClientId == client.Id));
        Assert.False(await db.Contracts.AnyAsync());
        var reloaded = await db.ClientCaseWorkOrders.SingleAsync(item => item.Id == workOrder.Id);
        Assert.Equal("pending_approval", reloaded.Status);
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
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Accounts);
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
        db.ChangeTracker.Clear();
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
        Assert.Equal("Клиент счета", db.ClientCases.Local.Single(item => item.Id == clientCase.Id).Client?.Name);

        var reloadedWorkOrder = await db.ClientCaseWorkOrders.AsNoTracking().SingleAsync(item => item.Id == workOrder.Id);
        var reloadedCase = await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == clientCase.Id);
        Assert.Equal("invoice_created", reloadedWorkOrder.Status);
        Assert.Contains("создан счет", reloadedCase.EventHistory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkOrder_CreateActCreatesDraftActForUnactedItemsOnly()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "work-order-act-owner@example.com", "Владелец акта");
        var executor = await CreateUserAsync(db, "work-order-act-executor@example.com", "Исполнитель акта");
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Acts);
        var client = new Client
        {
            Name = "Клиент акта",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для акта",
            CaseNumber = "ACT-77",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Расчет для акта",
            Status = "invoice_created",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Уже актированная услуга",
                    ExecutorUserId = executor.Id,
                    ExecutorName = executor.FullName,
                    Quantity = 1,
                    UnitPrice = 10000,
                    TotalAmount = 10000,
                    SortOrder = 1,
                },
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Новая услуга для акта",
                    ExecutorUserId = executor.Id,
                    ExecutorName = executor.FullName,
                    Quantity = 2,
                    UnitPrice = 7000,
                    TotalAmount = 14000,
                    SortOrder = 2,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();

        var alreadyActedItem = workOrder.Items.Single(item => item.ServiceName == "Уже актированная услуга");
        db.Acts.Add(new Act
        {
            Number = "ACT-OLD",
            Title = "Старый акт",
            Date = new DateTime(2026, 06, 01),
            Amount = 10000,
            ClientId = client.Id,
            ResponsibleId = executor.Id,
            Status = ActStatus.Created,
            WorkflowStatus = ActWorkflowStatus.Draft,
            CreationMode = ActCreationMode.Manual,
            LineItems =
            [
                new ActLineItem
                {
                    SourceKey = $"work-order:{workOrder.Id}:item:{alreadyActedItem.Id}",
                    SourceSortOrder = alreadyActedItem.SortOrder,
                    Name = alreadyActedItem.ServiceName,
                    Quantity = alreadyActedItem.Quantity,
                    Unit = "шт",
                    Price = alreadyActedItem.UnitPrice,
                    SortOrder = 1,
                },
            ],
        });
        await db.SaveChangesAsync();
        var targetItem = workOrder.Items.Single(item => item.ServiceName == "Новая услуга для акта");
        var controller = CreateController(db, owner);

        var result = await controller.CreateActFromWorkOrder(clientCase.Id, new CasesV2Controller.CreateClientCaseWorkOrderActRequest
        {
            Number = "WO-ACT-77",
            Date = new DateTime(2026, 06, 03),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderActDto>(created.Value);
        Assert.Equal("WO-ACT-77", dto.Number);
        Assert.Equal(new DateTime(2026, 06, 03), dto.Date);
        Assert.Equal(14000, dto.Amount);
        Assert.Equal(1, dto.LineItemCount);
        Assert.Equal("invoice_created", dto.WorkOrder.Status);
        Assert.NotNull(dto.DocumentId);

        var act = await db.Acts
            .Include(item => item.LineItems)
            .SingleAsync(item => item.Id == dto.ActId);
        Assert.Equal(ActCreationMode.Manual, act.CreationMode);
        Assert.Equal(ActStatus.Created, act.Status);
        Assert.Equal(ActWorkflowStatus.Draft, act.WorkflowStatus);
        Assert.Equal(client.Id, act.ClientId);
        Assert.Equal(executor.Id, act.ResponsibleId);
        Assert.Single(act.LineItems);
        Assert.Contains(act.LineItems, item =>
            item.Name == "Новая услуга для акта"
            && item.Quantity == 2
            && item.Price == 7000
            && item.SourceKey == $"work-order:{workOrder.Id}:item:{targetItem.Id}");

        var actDocument = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.DocumentId);
        Assert.Equal($"act-{act.Id}", actDocument.DocumentKey);
        Assert.Equal("Акт", actDocument.DocumentType);
        Assert.Equal("text/html; charset=utf-8", actDocument.ContentType);

        await db.Entry(workOrder).ReloadAsync();
        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("invoice_created", workOrder.Status);
        Assert.Contains("создан акт", clientCase.EventHistory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkOrder_CreateContractCreatesDraftContractWithOwnerLinksAndServices()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "work-order-contract-owner@example.com", "Владелец договора");
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Contracts);
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Payments, viewScope: DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Клиент договора",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для договора",
            CaseNumber = "CONTRACT-77",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Расчет договора",
            Status = "approved",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Подготовка договора",
                    Quantity = 1,
                    UnitPrice = 12000,
                    TotalAmount = 12000,
                    SortOrder = 1,
                },
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Юридическое сопровождение",
                    Quantity = 2,
                    UnitPrice = 9000,
                    TotalAmount = 18000,
                    SortOrder = 2,
                },
            ],
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.ClientCaseWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.CreateContractFromWorkOrder(clientCase.Id, new CasesV2Controller.CreateClientCaseWorkOrderContractRequest
        {
            Number = "WO-CONTRACT-77",
            Date = new DateTime(2026, 06, 04),
            StartDate = new DateTime(2026, 06, 05),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderContractDto>(created.Value);
        Assert.Equal("WO-CONTRACT-77", dto.Number);
        Assert.Equal(new DateTime(2026, 06, 04), dto.Date);
        Assert.Equal(new DateTime(2026, 06, 05), dto.StartDate);
        Assert.Equal(30000, dto.Amount);
        Assert.Equal(client.Id, dto.ClientId);
        Assert.Null(dto.LegalEntityId);
        Assert.Equal("approved", dto.WorkOrder.Status);
        Assert.NotNull(dto.DocumentId);

        var contract = await db.Contracts
            .Include(item => item.ClientContracts)
            .Include(item => item.LegalEntityContracts)
            .Include(item => item.ClientCaseContracts)
            .SingleAsync(item => item.Id == dto.ContractId);
        Assert.Equal(ContractStatus.Draft, contract.Status);
        Assert.Equal(ContractBillingMode.Fixed, contract.BillingMode);
        Assert.True(contract.IsOpenEnded);
        Assert.Equal(30000, contract.Amount);
        Assert.Contains("Подготовка договора", contract.Description);
        Assert.Contains("Юридическое сопровождение", contract.Description);
        Assert.Contains(contract.ClientContracts, item => item.ClientId == client.Id);
        Assert.Empty(contract.LegalEntityContracts);
        Assert.Contains(contract.ClientCaseContracts, item => item.ClientCaseId == clientCase.Id);

        var contractDocument = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.DocumentId);
        Assert.Equal($"contract-{contract.Id}", contractDocument.DocumentKey);
        Assert.Equal("Договор", contractDocument.DocumentType);
        Assert.Equal("text/html; charset=utf-8", contractDocument.ContentType);

        await db.Entry(workOrder).ReloadAsync();
        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal("approved", workOrder.Status);
        Assert.Contains("создан договор", clientCase.EventHistory, StringComparison.OrdinalIgnoreCase);

        db.ChangeTracker.Clear();
        var financeResult = await CreateController(db, owner).GetFinance(clientCase.Id, CancellationToken.None);
        var finance = Assert.IsType<CasesV2Controller.ClientCaseFinanceDetailDto>(
            Assert.IsType<OkObjectResult>(financeResult.Result).Value);
        var financeContract = Assert.Single(finance.Contracts);
        Assert.Equal(contract.Id, financeContract.Id);
    }

    [Fact]
    public async Task WorkOrder_DocumentCreationRequiresTargetSectionCreatePermission()
    {
        await using var db = await CreateContextAsync();
        var noAccountsUser = await CreateUserAsync(db, "work-order-no-accounts@example.com", "Без счетов");
        var noActsUser = await CreateUserAsync(db, "work-order-no-acts@example.com", "Без актов");
        var noContractsUser = await CreateUserAsync(db, "work-order-no-contracts@example.com", "Без договоров");
        await GrantSectionPermissionAsync(db, noAccountsUser, RolePermissionCatalog.Accounts, canCreate: false);
        await GrantSectionPermissionAsync(db, noActsUser, RolePermissionCatalog.Acts, canCreate: false);
        await GrantSectionPermissionAsync(db, noContractsUser, RolePermissionCatalog.Contracts, canCreate: false);
        var client = new Client
        {
            Name = "Клиент проверки прав заказ-наряда",
            IsActive = true,
            ResponsibleUserId = noAccountsUser.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело проверки прав заказ-наряда",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = noAccountsUser.Id,
        };
        var workOrder = new ClientCaseWorkOrder
        {
            ClientCase = clientCase,
            Title = "Расчет для проверки прав",
            Status = "approved",
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
            Items =
            [
                new ClientCaseWorkOrderItem
                {
                    ServiceName = "Юридическая услуга",
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

        var noAccountsController = CreateController(db, noAccountsUser);
        var noActsController = CreateController(db, noActsUser);
        var noContractsController = CreateController(db, noContractsUser);

        Assert.IsType<ForbidResult>((await noAccountsController.CreateInvoiceFromWorkOrder(
            clientCase.Id,
            new CasesV2Controller.CreateClientCaseWorkOrderInvoiceRequest(),
            CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await noActsController.CreateActFromWorkOrder(
            clientCase.Id,
            new CasesV2Controller.CreateClientCaseWorkOrderActRequest(),
            CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await noContractsController.CreateContractFromWorkOrder(
            clientCase.Id,
            new CasesV2Controller.CreateClientCaseWorkOrderContractRequest(),
            CancellationToken.None)).Result);

        Assert.Equal(0, await db.Payments.CountAsync());
        Assert.Equal(0, await db.Acts.CountAsync());
        Assert.Equal(0, await db.Contracts.CountAsync());
        Assert.Equal(0, await db.ClientCaseDocuments.CountAsync());
    }

    [Fact]
    public async Task WorkOrder_CreateInvoiceDefaultsDateToUserTimeZone()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "work-order-timezone@example.com", "Work Order Owner");
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Accounts);
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
        Assert.Contains("<th>№</th>", content.Content, StringComparison.Ordinal);
        Assert.Contains("24", content.Content, StringComparison.Ordinal);
        Assert.Contains(selectedZone.LocalNow.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")), content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkOrder_GeneratesLinkedCaseDocument()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Accounts);
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
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Accounts);
        await GrantSectionPermissionAsync(db, owner, RolePermissionCatalog.Acts);
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

    [Fact]
    public async Task Conversation_AddsMessageAttachmentsAsCaseDocuments()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "conversation-owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Клиент переписки",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с вложением",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var controller = CreateController(db, owner);
        var file = CreateFormFile("attachment body", "scan.pdf", "application/pdf");

        var addResult = await controller.AddConversationEntryWithAttachments(
            clientCase.Id,
            new CasesV2Controller.AddClientCaseConversationEntryFormRequest
            {
                Text = "Прикрепил скан заявления.",
                Files = [file],
            },
            CancellationToken.None);

        var addOk = Assert.IsType<OkObjectResult>(addResult.Result);
        var addedItems = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseConversationEntryDto>>(addOk.Value);
        var message = Assert.Single(addedItems, item => item.Kind == "message");
        Assert.Equal("Прикрепил скан заявления", message.Text);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("scan.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(file.Length, attachment.SizeBytes);

        var document = await db.ClientCaseDocuments.SingleAsync();
        Assert.Equal(document.Id, attachment.DocumentId);
        Assert.Equal("Переписка", document.DocumentType);
        Assert.Equal("Прикреплен к сообщению", document.Status);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains($"Вложения: {document.Id}.", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("Документ «scan.pdf» загружен", clientCase.EventHistory, StringComparison.Ordinal);

        var getResult = await controller.GetConversation(clientCase.Id, CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        var getItems = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseConversationEntryDto>>(getOk.Value);
        var loadedAttachment = Assert.Single(Assert.Single(getItems).Attachments);
        Assert.Equal(document.Id, loadedAttachment.DocumentId);
        Assert.Equal("scan.pdf", loadedAttachment.FileName);
    }

    [Fact]
    public async Task Conversation_NotifiesVisibleCaseParticipantsAboutNewMessage()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "conversation-actor@example.com", "Автор");
        var recipient = await CreateUserAsync(db, "conversation-recipient@example.com", "Ответственный клиент");
        var client = new Client
        {
            Name = "Клиент уведомления",
            IsActive = true,
            ResponsibleUserId = recipient.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с уведомлением",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = actor.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var controller = CreateController(db, actor);

        var result = await controller.AddConversationEntry(clientCase.Id, new CasesV2Controller.AddClientCaseConversationEntryRequest
        {
            Text = "Проверьте новую позицию по делу.",
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.CaseConversation, notification.Type);
        Assert.Equal("case", notification.ObjectType);
        Assert.Equal(clientCase.Id, notification.ObjectId);
        Assert.Contains("Новое сообщение", notification.Title, StringComparison.Ordinal);
        Assert.Contains("Проверьте новую позицию", notification.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain(db.UserNotifications, item => item.RecipientUserId == actor.Id);
    }

    [Fact]
    public async Task Conversation_RequiresCasePermissionsAndVisibility()
    {
        await using var db = await CreateContextAsync();
        var noViewUser = await CreateUserAsync(db, "conversation-no-view@example.com", "Без просмотра");
        noViewUser.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanView = false;
        var noEditUser = await CreateUserAsync(db, "conversation-no-edit@example.com", "Без редактирования");
        noEditUser.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Cases).CanEdit = false;
        var limitedUser = await CreateUserAsync(db, "conversation-limited@example.com", "Ограниченный");
        var otherResponsible = await CreateUserAsync(db, "conversation-other@example.com", "Другой");

        var client = new Client
        {
            Name = "Клиент переписки",
            IsActive = true,
            ResponsibleUserId = noEditUser.Id,
        };
        var accessibleCase = new ClientCase
        {
            Client = client,
            Title = "Доступная переписка",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = noEditUser.Id,
            EventHistory = "18.05.2026 09:10: Комментарий по делу: Проверить документы. Автор: Администратор.",
        };
        var hiddenCase = new ClientCase
        {
            Client = client,
            Title = "Чужая переписка",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(accessibleCase, hiddenCase);
        await db.SaveChangesAsync();

        var noViewController = CreateController(db, noViewUser);
        Assert.IsType<ForbidResult>((await noViewController.GetConversation(accessibleCase.Id, CancellationToken.None)).Result);

        var noEditController = CreateController(db, noEditUser);
        Assert.IsType<ForbidResult>((await noEditController.AddConversationEntry(
            accessibleCase.Id,
            new CasesV2Controller.AddClientCaseConversationEntryRequest { Text = "Новое сообщение" },
            CancellationToken.None)).Result);

        var limitedController = CreateController(db, limitedUser);
        Assert.IsType<NotFoundResult>((await limitedController.GetConversation(hiddenCase.Id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await limitedController.AddConversationEntry(
            hiddenCase.Id,
            new CasesV2Controller.AddClientCaseConversationEntryRequest { Text = "Новое сообщение" },
            CancellationToken.None)).Result);
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

    private static (string TimeZoneId, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZoneId, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC was found.");
    }

    private static CasesV2Controller CreateController(
        PaymentContext db,
        User user,
        string? contentRootPath = null,
        ISudrfCourtSyncService? sudrfCourtSyncService = null,
        IInvoiceDocumentPdfService? pdfService = null,
        ICourtSyncProviderRegistry? courtSyncProviderRegistry = null,
        IConfiguration? configuration = null,
        IOptionsMonitor<AppFeatureFlagsOptions>? featureFlags = null,
        INotificationService? notificationService = null,
        bool includeAdminRoleClaim = false,
        CourtSyncMetrics? courtSyncMetrics = null,
        IExternalWorkOrderApprovalService? externalWorkOrderApprovalService = null,
        IRealtimeNotificationPublisher? realtimePublisher = null)
        => new(db, new TestWebHostEnvironment
        {
            ContentRootPath = contentRootPath ?? Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N")),
        }, sudrfCourtSyncService: sudrfCourtSyncService, pdfService: pdfService, courtSyncProviderRegistry: courtSyncProviderRegistry, configuration: configuration, featureFlags: featureFlags, notificationService: notificationService, courtSyncMetrics: courtSyncMetrics, externalWorkOrderApprovalService: externalWorkOrderApprovalService, realtimePublisher: realtimePublisher)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        BuildUserClaims(user, includeAdminRoleClaim),
                        "TestAuth")),
                },
            },
        };

    private static Claim[] BuildUserClaims(User user, bool includeAdminRoleClaim)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
        };
        if (includeAdminRoleClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return claims.ToArray();
    }

    private static NotificationService CreateNotificationService(PaymentContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new NotificationService(
            db,
            configuration,
            new NoopEmailService(),
            new NoopTelegramNotificationService(),
            NullLogger<NotificationService>.Instance);
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;

        public Task SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            CancellationToken cancellationToken = default,
            EmailSendSettings? settings = null)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoopTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(long chatId, string text, string botToken, CancellationToken cancellationToken = default, TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());
    }

    private sealed class RecordingRealtimeNotificationPublisher : IRealtimeNotificationPublisher
    {
        public int TaskJournalChangedCount { get; private set; }

        public Task PublishUserNotificationAsync(
            UserNotification notification,
            bool toastEnabled = true,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            TaskJournalChangedCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExternalWorkOrderApprovalService : IExternalWorkOrderApprovalService
    {
        public ExternalWorkOrderApprovalStatusDto? LatestStatus { get; init; }
        public ExternalWorkOrderApprovalStatusDto CreatedStatus { get; init; } = new();
        public bool RevokeResult { get; init; }
        public int GetCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public int LastCaseId { get; private set; }
        public int? LastActorUserId { get; private set; }
        public string LastActorName { get; private set; } = string.Empty;
        public CreateExternalWorkOrderApprovalRequest? LastCreateRequest { get; private set; }

        public Task<ExternalWorkOrderApprovalStatusDto?> GetLatestStatusAsync(int caseId, CancellationToken ct)
        {
            GetCalls++;
            LastCaseId = caseId;
            return Task.FromResult(LatestStatus);
        }

        public Task<ExternalWorkOrderApprovalStatusDto> CreateAsync(
            int caseId,
            CreateExternalWorkOrderApprovalRequest request,
            int? actorUserId,
            string actorName,
            CancellationToken ct)
        {
            CreateCalls++;
            LastCaseId = caseId;
            LastCreateRequest = request;
            LastActorUserId = actorUserId;
            LastActorName = actorName;
            return Task.FromResult(CreatedStatus);
        }

        public Task<bool> RevokeAsync(int caseId, int? actorUserId, string actorName, CancellationToken ct)
        {
            RevokeCalls++;
            LastCaseId = caseId;
            LastActorUserId = actorUserId;
            LastActorName = actorName;
            return Task.FromResult(RevokeResult);
        }

        public Task<PublicWorkOrderApprovalDto> OpenAsync(Guid publicId, string token, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PublicWorkOrderApprovalDecisionDto> DecideAsync(
            Guid publicId,
            PublicWorkOrderApprovalDecisionRequest request,
            string? remoteIp,
            string? userAgent,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PublicWorkOrderAttachmentGrantDto> CreateAttachmentGrantAsync(
            Guid publicId,
            Guid attachmentPublicId,
            string token,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PublicWorkOrderAttachmentDownload> DownloadAttachmentAsync(
            Guid publicId,
            Guid attachmentPublicId,
            string grantToken,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

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

    private sealed class ThrowingSudrfCourtSyncService(string message) : ISudrfCourtSyncService
    {
        public bool CanHandle(string? sourceUrl)
            => sourceUrl?.Contains("sudrf.ru", StringComparison.OrdinalIgnoreCase) == true;

        public Task<SudrfCourtCaseData> FetchCaseAsync(string sourceUrl, CancellationToken ct, string? courtCaseNumber = null)
            => throw new SudrfCourtSyncException(message);
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

    private static async Task GrantSectionPermissionAsync(
        PaymentContext db,
        User user,
        string section,
        bool canView = true,
        bool canCreate = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canExport = true,
        string viewScope = DataVisibilityScope.All)
    {
        var roleId = user.Role?.Id > 0 ? user.Role.Id : user.RoleId;
        var permission = await db.RolePermissions
            .SingleOrDefaultAsync(item => item.RoleId == roleId && item.Section == section);
        if (permission is null)
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = roleId,
                Section = section,
                CanView = canView,
                CanCreate = canCreate,
                CanEdit = canEdit,
                CanDelete = canDelete,
                CanExport = canExport,
                ViewScope = viewScope,
            });
        }
        else
        {
            permission.CanView = canView;
            permission.CanCreate = canCreate;
            permission.CanEdit = canEdit;
            permission.CanDelete = canDelete;
            permission.CanExport = canExport;
            permission.ViewScope = viewScope;
        }

        await db.SaveChangesAsync();
    }

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var options = optionsBuilder.Options;
        var db = new PaymentContext(options);
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
}
