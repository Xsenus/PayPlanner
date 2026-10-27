using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BackendPermissionMatrixTests
{
    [Fact]
    public async Task ClientPassport_RespectsClientVisibilityForReadAndWrite()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "passport-owner", new RolePermission
        {
            Section = RolePermissionCatalog.Clients, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.Own,
        });
        var other = await CreateUserAsync(db, "passport-other", roleName: "other-employee");
        var own = new Client { Name = "Own", ResponsibleUserId = user.Id };
        var hidden = new Client { Name = "Hidden", ResponsibleUserId = other.Id };
        db.Clients.AddRange(own, hidden);
        await db.SaveChangesAsync();
        db.ClientPassports.AddRange(
            new ClientPassport { ClientId = own.Id, Number = "111111", IsPrimary = true },
            new ClientPassport { ClientId = hidden.Id, Number = "222222", IsPrimary = true });
        await db.SaveChangesAsync();
        var controller = new ClientPassportController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        Assert.IsType<OkObjectResult>(await controller.Get(own.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Get(hidden.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Upsert(hidden.Id, new UpsertClientPassportRequest { Number = "333333" }, CancellationToken.None));
        Assert.Equal("222222", await db.ClientPassports.Where(item => item.ClientId == hidden.Id).Select(item => item.Number).SingleAsync());
        Assert.IsType<OkObjectResult>(await controller.Upsert(own.Id, new UpsertClientPassportRequest { Number = "444444" }, CancellationToken.None));
        Assert.Equal("444444", await db.ClientPassports.Where(item => item.ClientId == own.Id).Select(item => item.Number).SingleAsync());
    }

    [Fact]
    public async Task ClientPassport_RejectsReadAndWriteWithoutClientView()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "passport-no-access", new RolePermission
        {
            Section = RolePermissionCatalog.Clients, CanView = false, ViewScope = DataVisibilityScope.Own,
        });
        var client = new Client { Name = "Restricted", ResponsibleUserId = user.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = new ClientPassportController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        Assert.IsType<ForbidResult>(await controller.Get(client.Id, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.Upsert(client.Id, new UpsertClientPassportRequest { Number = "333333" }, CancellationToken.None));
        Assert.False(await db.ClientPassports.AnyAsync(item => item.ClientId == client.Id));
    }

    [Fact]
    public void UncalculatedCaseTaskSummary_IsNotAdvertisedAsComplete()
        => Assert.Equal("none", new ClientCase().TaskSummaryVisibility);

    [Theory]
    [InlineData("none", "Недоступно")]
    [InlineData("partial", "2 (доступные)")]
    [InlineData("full", "2")]
    public void CaseTaskSummaryExport_LabelsVisibility(string visibility, string expected)
    {
        var method = typeof(CasesV2Controller).GetMethod("FormatTaskSummaryValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [new ClientCase { TaskSummaryVisibility = visibility }, 2, false]));
    }

    [Fact]
    public async Task CaseDetail_DoesNotDiscloseTaskCountsWithoutTaskViewPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "case-without-tasks", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = false, ViewScope = DataVisibilityScope.Own },
        ]);
        var clientCase = new ClientCase { Title = "Visible case", Client = new Client { Name = "Case client" } };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientCaseId = clientCase.Id,
            Title = "Hidden task",
            ObjectName = "Hidden task",
            AuthorUserId = user.Id,
            DueAt = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = Assert.IsType<OkObjectResult>(await controller.GetById(clientCase.Id, CancellationToken.None));
        var response = Assert.IsType<ClientCase>(result.Value);
        Assert.Equal(0, response.OpenTaskCount);
        Assert.Equal("none", response.TaskSummaryVisibility);
        Assert.Equal(0, response.OverdueTaskCount);
        Assert.Null(response.LastTaskAt);
        Assert.Null(response.LastMeaningfulActivityAt);
        Assert.Null(response.NextInactivityControlAt);
        Assert.Equal("Сведения автоконтроля недоступны из-за ограничений доступа", response.InactivityControlExclusionReason);
    }

    [Fact]
    public async Task CaseDetail_CountsOnlyTasksVisibleToTheEmployee()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "case-own-tasks", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own },
        ]);
        var other = await CreateUserAsync(db, "other-case-task", roleName: "other-employee");
        var clientCase = new ClientCase { Title = "Visible case", Client = new Client { Name = "Case client" } };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.AddRange(
            new TaskJournalItem { ClientCaseId = clientCase.Id, Title = "Own", ObjectName = "Own", AuthorUserId = user.Id, DueAt = DateTime.UtcNow.AddDays(1) },
            new TaskJournalItem { ClientCaseId = clientCase.Id, Title = "Other", ObjectName = "Other", AuthorUserId = other.Id, DueAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = Assert.IsType<OkObjectResult>(await controller.GetById(clientCase.Id, CancellationToken.None));
        var response = Assert.IsType<ClientCase>(result.Value);
        Assert.Equal(1, response.OpenTaskCount);
        Assert.Equal("partial", response.TaskSummaryVisibility);
        Assert.Equal(0, response.OverdueTaskCount);
        Assert.Null(response.NextInactivityControlAt);
    }

    [Fact]
    public async Task CaseDetail_PreservesFullSummaryAndControlForFullVisibility()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "case-all-tasks", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, ViewScope = DataVisibilityScope.All },
        ]);
        user.IsApproved = true;
        await db.SaveChangesAsync();
        var clientCase = new ClientCase { Title = "Visible case", Client = new Client { Name = "Case client" } };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientCaseId = clientCase.Id, Title = "Open task", ObjectName = "Open task", AuthorUserId = user.Id,
            Status = "inProgress", DueAt = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = Assert.IsType<OkObjectResult>(await controller.GetById(clientCase.Id, CancellationToken.None));
        var response = Assert.IsType<ClientCase>(result.Value);
        Assert.Equal(1, response.OpenTaskCount);
        Assert.Equal("full", response.TaskSummaryVisibility);
        Assert.Equal(1, response.OverdueTaskCount);
        Assert.Equal("Есть активная задача", response.InactivityControlExclusionReason);
        Assert.NotNull(response.LastMeaningfulActivityAt);
    }

    [Fact]
    public async Task CaseDetail_DoesNotCountRequestTaskForUnapprovedEmployee()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "unapproved-case-tasks", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, ViewScope = DataVisibilityScope.All },
        ]);
        var client = new Client { Name = "Case client" };
        var clientCase = new ClientCase { Title = "Visible case", Client = client };
        var status = new SalesRequestStatus { Key = "new", Title = "New" };
        db.ClientCases.Add(clientCase);
        db.SalesRequestStatuses.Add(status);
        await db.SaveChangesAsync();
        var request = new SalesRequest { ClientId = client.Id, StatusId = status.Id, ResponsibleUserId = user.Id, CreatedByUserId = user.Id, Summary = "Private" };
        db.SalesRequests.Add(request);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientCaseId = clientCase.Id, SalesRequestId = request.Id, Title = "Request task", ObjectName = "Request task",
            AuthorUserId = user.Id, DueAt = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = Assert.IsType<OkObjectResult>(await controller.GetById(clientCase.Id, CancellationToken.None));
        var response = Assert.IsType<ClientCase>(result.Value);
        Assert.Equal("partial", response.TaskSummaryVisibility);
        Assert.Equal(0, response.OpenTaskCount);
    }

    [Fact]
    public async Task CaseDetail_CountsOnlyRequestTasksVisibleToApprovedEmployee()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "own-request-tasks", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, ViewScope = DataVisibilityScope.Own },
        ]);
        user.IsApproved = true;
        var other = await CreateUserAsync(db, "other-request-task", roleName: "other-employee");
        var client = new Client { Name = "Case client" };
        var clientCase = new ClientCase { Title = "Visible case", Client = client };
        var status = new SalesRequestStatus { Key = "new", Title = "New" };
        db.ClientCases.Add(clientCase);
        db.SalesRequestStatuses.Add(status);
        await db.SaveChangesAsync();
        var own = new SalesRequest { ClientId = client.Id, StatusId = status.Id, ResponsibleUserId = user.Id, CreatedByUserId = user.Id, Summary = "Own" };
        var hidden = new SalesRequest { ClientId = client.Id, StatusId = status.Id, ResponsibleUserId = other.Id, CreatedByUserId = other.Id, Summary = "Hidden" };
        db.SalesRequests.AddRange(own, hidden);
        await db.SaveChangesAsync();
        db.TaskJournalItems.AddRange(
            new TaskJournalItem { ClientCaseId = clientCase.Id, SalesRequestId = own.Id, Title = "Own", ObjectName = "Own", AuthorUserId = user.Id, DueAt = DateTime.UtcNow.AddDays(1) },
            new TaskJournalItem { ClientCaseId = clientCase.Id, SalesRequestId = hidden.Id, Title = "Hidden", ObjectName = "Hidden", AuthorUserId = other.Id, DueAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = Assert.IsType<OkObjectResult>(await controller.GetById(clientCase.Id, CancellationToken.None));
        var response = Assert.IsType<ClientCase>(result.Value);
        Assert.Equal("partial", response.TaskSummaryVisibility);
        Assert.Equal(1, response.OpenTaskCount);
        Assert.Equal(0, response.OverdueTaskCount);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("inactive")]
    public async Task CaseRegistry_RejectsActivityFilterWithoutFullTaskVisibility(string activity)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "case-filter", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own },
        ]);
        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = await controller.Get(null, null, null, null, null, null, null, null, null, null, null, null, null,
            activity: activity);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task CaseRegistry_ReportsActivityFilterAccessWhenEmpty()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "empty-case-registry", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All },
        ]);
        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null, null, null, null, null, null, null, null, null));
        var property = result.Value!.GetType().GetProperty("canFilterActivity");
        Assert.NotNull(property);
        Assert.Equal(false, property.GetValue(result.Value));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("inactive")]
    public async Task CaseExport_RejectsActivityFilterWithoutFullTaskVisibility(string activity)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithPermissionsAsync(db, "case-export-filter", "employee",
        [
            new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, CanExport = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own },
        ]);
        var controller = new CasesV2Controller(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreatePrincipal(user) } },
        };
        var result = await controller.ExportRegistryCsv(null, null, null, null, null, null, null, null, null, null, null, null, null,
            activity: activity);
        Assert.IsType<ForbidResult>(result);
    }

    public static IEnumerable<object[]> CriticalSections()
    {
        yield return [RolePermissionCatalog.Payments];
        yield return [RolePermissionCatalog.Accounts];
        yield return [RolePermissionCatalog.Acts];
        yield return [RolePermissionCatalog.Contracts];
        yield return [RolePermissionCatalog.Cases];
        yield return [RolePermissionCatalog.Tasks];
        yield return [RolePermissionCatalog.Reports];
        yield return [RolePermissionCatalog.Roles];
        yield return [RolePermissionCatalog.Audit];
        yield return [RolePermissionCatalog.Integrations];
        yield return [RolePermissionCatalog.YandexDisk];
        yield return [RolePermissionCatalog.S3Storage];
    }

    public static IEnumerable<object[]> DataScopedSections()
    {
        foreach (var section in RolePermissionCatalog.DataScopedSections)
        {
            yield return [section];
        }
    }

    [Theory]
    [MemberData(nameof(CriticalSections))]
    public async Task CriticalSectionPermissions_ReturnCanView_ForUserWithSectionAccess(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-viewer", new RolePermission
        {
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.All
        });

        var permission = await db.GetUserSectionPermissionAsync(user.Id, section);

        Assert.NotNull(permission);
        Assert.True(permission.CanView);
    }

    [Theory]
    [MemberData(nameof(CriticalSections))]
    public async Task CriticalSectionPermissions_DenyView_ForUserWithoutSectionAccess(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-blocked", new RolePermission
        {
            Section = section,
            CanView = false,
            ViewScope = DataVisibilityScope.Own
        });

        var permission = await db.GetUserSectionPermissionAsync(user.Id, section);

        Assert.NotNull(permission);
        Assert.False(permission.CanView);
    }

    [Theory]
    [MemberData(nameof(CriticalSections))]
    public async Task CriticalSectionPermissions_ReturnNull_ForUserWithoutConfiguredSection(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-missing");

        var permission = await db.GetUserSectionPermissionAsync(user.Id, section);

        Assert.Null(permission);
    }

    [Theory]
    [MemberData(nameof(CriticalSections))]
    public async Task SectionActionPermissions_DenyMissingSection_ForExistingRegularUser(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-missing-actions", new RolePermission
        {
            Section = RolePermissionCatalog.Notifications,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });
        var principal = CreatePrincipal(user);

        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanView));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanCreate));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanEdit));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanDelete));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanExport));
    }

    [Fact]
    public async Task SectionActionPermissions_DoNotOpenCasesFromLegacyClientPermissionFallback()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "legacy-client-only", new RolePermission
        {
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All
        });
        var principal = CreatePrincipal(user);

        var casesPermission = await db.GetUserSectionPermissionAsync(user.Id, RolePermissionCatalog.Cases);

        Assert.Null(casesPermission);
        Assert.False(await db.HasUserSectionPermissionAsync(principal, RolePermissionCatalog.Cases, static permission => permission.CanView));
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_RestrictsRegularUserToOwnScope_ForDataScopedSections(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-own", new RolePermission
        {
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(user), section);

        Assert.NotNull(access);
        Assert.False(access.IsAdmin);
        Assert.Equal(DataVisibilityScope.Own, access.ViewScope);
        Assert.True(access.CanSeeUser(user.Id));
        Assert.False(access.CanSeeUser(user.Id + 1000));
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_FiltersDirectIdQueries_ForOwnScope(string section)
    {
        await using var db = await CreateContextAsync();
        var current = await CreateUserAsync(db, $"{section}-direct-own", new RolePermission
        {
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });
        var other = await CreateUserAsync(db, $"{section}-direct-hidden", roleName: $"{section}-direct-hidden-role");
        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(current), section);
        Assert.NotNull(access);
        var (visibleId, hiddenId) = await SeedDataScopedRecordsAsync(db, section, current.Id, other.Id);

        Assert.True(await IsDataScopedRecordVisibleAsync(db, section, access, visibleId));
        Assert.False(await IsDataScopedRecordVisibleAsync(db, section, access, hiddenId));
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_DeniesRegularUserWithoutSectionViewAccess(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-denied", new RolePermission
        {
            Section = section,
            CanView = false,
            ViewScope = DataVisibilityScope.All
        });

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(user), section);

        Assert.Null(access);
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_DeniesRegularUserWithoutConfiguredSection(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-missing-visibility", new RolePermission
        {
            Section = RolePermissionCatalog.Notifications,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(user), section);

        Assert.Null(access);
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task SectionActionPermissions_RespectExplicitActionDenials(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-actions", new RolePermission
        {
            Section = section,
            CanView = true,
            CanCreate = false,
            CanEdit = true,
            CanDelete = false,
            CanExport = true,
            ViewScope = DataVisibilityScope.All
        });
        var principal = CreatePrincipal(user);

        var canCreate = await db.HasUserSectionPermissionAsync(principal, section, permission => permission.CanCreate);
        var canEdit = await db.HasUserSectionPermissionAsync(principal, section, permission => permission.CanEdit);
        var canDelete = await db.HasUserSectionPermissionAsync(principal, section, permission => permission.CanDelete);

        Assert.False(canCreate);
        Assert.True(canEdit);
        Assert.False(canDelete);
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task SectionActionPermissions_DenyActionsWhenViewIsDisabledEvenIfActionsAreEnabled(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-view-disabled-actions-enabled", new RolePermission
        {
            Section = section,
            CanView = false,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All
        });
        var principal = CreatePrincipal(user);

        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanCreate));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanEdit));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanDelete));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanExport));
    }

    [Fact]
    public async Task RequireSectionPermissionFilter_ForbidsClosedSectionBeforeActionRuns()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "filter-payments-denied", new RolePermission
        {
            Section = RolePermissionCatalog.Payments,
            CanView = false,
            ViewScope = DataVisibilityScope.Own
        });
        var filter = new RequireSectionPermissionFilter(
            RolePermissionCatalog.Payments,
            RequireSectionPermissionOperations.View,
            db);
        var context = CreateActionExecutingContext(CreatePrincipal(user));
        var nextWasCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextWasCalled = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.False(nextWasCalled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task RequireSectionPermissionFilter_AllowsOpenSectionToRunAction()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "filter-payments-allowed", new RolePermission
        {
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });
        var filter = new RequireSectionPermissionFilter(
            RolePermissionCatalog.Payments,
            RequireSectionPermissionOperations.View,
            db);
        var context = CreateActionExecutingContext(CreatePrincipal(user));
        var nextWasCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextWasCalled = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.True(nextWasCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task RequireSectionPermissionFilter_AllowsDatabaseAdminWithoutConfiguredSection()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "filter-admin-without-section", roleName: "admin");
        var filter = new RequireSectionPermissionFilter(
            RolePermissionCatalog.Payments,
            RequireSectionPermissionOperations.View,
            db);
        var context = CreateActionExecutingContext(CreatePrincipal(admin));
        var nextWasCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextWasCalled = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.True(nextWasCalled);
        Assert.Null(context.Result);
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_AllowsAdminAcrossDataScopedSections(string section)
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, $"{section}-admin", new RolePermission
        {
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        }, roleName: "admin");

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(admin, isAdmin: true), section);

        Assert.NotNull(access);
        Assert.True(access.IsAdmin);
        Assert.Equal(DataVisibilityScope.All, access.ViewScope);
        Assert.True(access.CanSeeUser(admin.Id + 1000));
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_AllowsAdminWithoutConfiguredSection(string section)
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, $"{section}-admin-no-section", roleName: "admin");

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(admin), section);

        Assert.NotNull(access);
        Assert.True(access.IsAdmin);
        Assert.Equal(DataVisibilityScope.All, access.ViewScope);
        Assert.True(access.CanSeeUser(admin.Id + 1000));
    }

    [Fact]
    public void HealthEndpoint_RemainsAnonymousAndReturnsOk()
    {
        var allowAnonymous = typeof(HealthController)
            .GetMethod(nameof(HealthController.Get))?
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
            .Any() == true;
        var controller = new HealthController();

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(allowAnonymous);
        Assert.NotNull(ok.Value);
        var status = ok.Value.GetType().GetProperty("status")?.GetValue(ok.Value);
        Assert.Equal("ok", status);
    }

    [Fact]
    public void LawyerRoleTemplate_OpensOnlyOperationalSections()
    {
        var defaults = RolePermissionCatalog.BuildDefaultsForRole("\u042e\u0440\u0438\u0441\u0442", allowRoleManagement: false);

        Assert.True(RolePermissionCatalog.IsLawyerRoleName("\u0421\u0442\u0430\u0440\u0448\u0438\u0439 \u044e\u0440\u0438\u0441\u0442"));
        Assert.True(RolePermissionCatalog.IsLawyerRoleName("lawyer-template-temp-codex"));
        Assert.True(defaults[RolePermissionCatalog.Tasks].CanView);
        Assert.True(defaults[RolePermissionCatalog.Tasks].CanCreate);
        Assert.True(defaults[RolePermissionCatalog.Calendar].CanView);
        Assert.False(defaults[RolePermissionCatalog.Clients].CanView);
        Assert.False(defaults[RolePermissionCatalog.Cases].CanView);
        Assert.True(defaults[RolePermissionCatalog.Notifications].CanView);
        Assert.Equal(DataVisibilityScope.Own, defaults[RolePermissionCatalog.Tasks].ViewScope);
        Assert.Equal(DataVisibilityScope.Own, defaults[RolePermissionCatalog.Clients].ViewScope);
        Assert.Equal(DataVisibilityScope.Own, defaults[RolePermissionCatalog.Cases].ViewScope);

        foreach (var section in new[]
        {
            RolePermissionCatalog.Clients,
            RolePermissionCatalog.Cases,
            RolePermissionCatalog.Payments,
            RolePermissionCatalog.Accounts,
            RolePermissionCatalog.Acts,
            RolePermissionCatalog.Contracts,
            RolePermissionCatalog.Reports,
            RolePermissionCatalog.Roles,
            RolePermissionCatalog.Audit,
            RolePermissionCatalog.Integrations,
            RolePermissionCatalog.YandexDisk,
            RolePermissionCatalog.S3Storage,
        })
        {
            Assert.False(defaults[section].CanView);
            Assert.False(defaults[section].CanCreate);
            Assert.False(defaults[section].CanEdit);
            Assert.False(defaults[section].CanDelete);
            Assert.False(defaults[section].CanExport);
        }
    }

    [Fact]
    public async Task LawyerRoleTemplate_ProtectsClosedApiSections()
    {
        await using var db = await CreateContextAsync();
        var lawyer = await CreateUserWithPermissionsAsync(
            db,
            "lawyer-template-api",
            "\u0421\u0442\u0430\u0440\u0448\u0438\u0439 \u044e\u0440\u0438\u0441\u0442",
            RolePermissionCatalog.BuildDefaultsForRole(
                    "\u0421\u0442\u0430\u0440\u0448\u0438\u0439 \u044e\u0440\u0438\u0441\u0442",
                    allowRoleManagement: false)
                .Select(pair => ToRolePermission(pair.Key, pair.Value)));

        foreach (var section in new[]
        {
            RolePermissionCatalog.Tasks,
            RolePermissionCatalog.Calendar,
            RolePermissionCatalog.Notifications,
        })
        {
            await AssertSectionFilterAsync(db, lawyer, section, shouldAllow: true);
        }

        foreach (var section in new[]
        {
            RolePermissionCatalog.Clients,
            RolePermissionCatalog.Cases,
            RolePermissionCatalog.Payments,
            RolePermissionCatalog.Accounts,
            RolePermissionCatalog.Acts,
            RolePermissionCatalog.Contracts,
            RolePermissionCatalog.Reports,
            RolePermissionCatalog.Roles,
            RolePermissionCatalog.Audit,
            RolePermissionCatalog.Integrations,
            RolePermissionCatalog.YandexDisk,
            RolePermissionCatalog.S3Storage,
        })
        {
            await AssertSectionFilterAsync(db, lawyer, section, shouldAllow: false);
        }
    }

    [Fact]
    public async Task AcceptedFinancialRoles_RespectCashAccessMatrix()
    {
        await using var db = await CreateContextAsync();
        var secretary = await CreateUserWithPermissionsAsync(db, "secretary", "Секретарь",
        [
            Denied(RolePermissionCatalog.Payments),
            Denied(RolePermissionCatalog.Reports),
            DocumentLimited(RolePermissionCatalog.Accounts),
            DocumentLimited(RolePermissionCatalog.Acts),
            DocumentLimited(RolePermissionCatalog.Contracts),
        ]);
        var lawyer = await CreateUserWithPermissionsAsync(db, "lawyer", "Юрист",
        [
            Denied(RolePermissionCatalog.Payments),
            Denied(RolePermissionCatalog.Reports),
            Denied(RolePermissionCatalog.Accounts),
            Denied(RolePermissionCatalog.Acts),
            Denied(RolePermissionCatalog.Contracts),
        ]);
        var accountant = await CreateUserWithPermissionsAsync(db, "accountant", "Бухгалтер",
        [
            DocumentLimited(RolePermissionCatalog.Payments),
            Denied(RolePermissionCatalog.Reports),
            DocumentLimited(RolePermissionCatalog.Accounts),
            DocumentLimited(RolePermissionCatalog.Acts),
            DocumentLimited(RolePermissionCatalog.Contracts),
        ]);

        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Payments, false, false, false, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Reports, false, false, false, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Accounts, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Acts, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Contracts, true, true, true, false, false);

        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Payments, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Reports, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Accounts, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Acts, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Contracts, false, false, false, false, false);

        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Payments, true, true, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Reports, false, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Accounts, true, true, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Acts, true, true, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Contracts, true, true, true, false, false);
    }

    [Fact]
    public async Task AcceptedOperationalRoles_RespectClientCaseTaskAccessMatrix()
    {
        await using var db = await CreateContextAsync();
        var secretary = await CreateUserWithPermissionsAsync(db, "secretary-operations", "Секретарь",
        [
            WorkLimited(RolePermissionCatalog.Clients),
            WorkLimited(RolePermissionCatalog.Cases),
            WorkLimited(RolePermissionCatalog.Tasks),
            CalendarLimited(),
            NotificationLimited(),
            Denied(RolePermissionCatalog.Roles),
            Denied(RolePermissionCatalog.Audit),
        ]);
        var lawyer = await CreateUserWithPermissionsAsync(db, "lawyer-operations", "Юрист",
        [
            WorkLimited(RolePermissionCatalog.Clients),
            WorkLimited(RolePermissionCatalog.Cases),
            WorkLimited(RolePermissionCatalog.Tasks),
            CalendarLimited(),
            NotificationLimited(),
            Denied(RolePermissionCatalog.Roles),
            Denied(RolePermissionCatalog.Audit),
        ]);
        var accountant = await CreateUserWithPermissionsAsync(db, "accountant-operations", "Бухгалтер",
        [
            ReadOnly(RolePermissionCatalog.Clients),
            ReadOnly(RolePermissionCatalog.Cases),
            NotificationLimited(),
            Denied(RolePermissionCatalog.Roles),
            Denied(RolePermissionCatalog.Audit),
        ]);

        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Clients, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Cases, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Tasks, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Calendar, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Notifications, true, false, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Roles, false, false, false, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Audit, false, false, false, false, false);
        await AssertVisibilityScopeAsync(db, secretary, RolePermissionCatalog.Clients, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, secretary, RolePermissionCatalog.Cases, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, secretary, RolePermissionCatalog.Tasks, DataVisibilityScope.Own);

        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Clients, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Cases, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Tasks, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Calendar, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Notifications, true, false, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Roles, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Audit, false, false, false, false, false);
        await AssertVisibilityScopeAsync(db, lawyer, RolePermissionCatalog.Clients, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, lawyer, RolePermissionCatalog.Cases, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, lawyer, RolePermissionCatalog.Tasks, DataVisibilityScope.Own);

        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Clients, true, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Cases, true, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Notifications, true, false, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Roles, false, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Audit, false, false, false, false, false);
        await AssertVisibilityScopeAsync(db, accountant, RolePermissionCatalog.Clients, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, accountant, RolePermissionCatalog.Cases, DataVisibilityScope.Own);
    }

    private static RolePermission Denied(string section)
        => new()
        {
            Section = section,
            CanView = false,
            CanCreate = false,
            CanEdit = false,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission ReadOnly(string section)
        => new()
        {
            Section = section,
            CanView = true,
            CanCreate = false,
            CanEdit = false,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission WorkLimited(string section)
        => new()
        {
            Section = section,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission DocumentLimited(string section)
        => new()
        {
            Section = section,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission CalendarLimited()
        => new()
        {
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = false,
            CanExport = false,
            CanViewAnalytics = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission NotificationLimited()
        => new()
        {
            Section = RolePermissionCatalog.Notifications,
            CanView = true,
            CanCreate = false,
            CanEdit = true,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission ToRolePermission(string section, RolePermissionValues values)
        => new()
        {
            Section = section,
            CanView = values.CanView,
            CanCreate = values.CanCreate,
            CanEdit = values.CanEdit,
            CanDelete = values.CanDelete,
            CanExport = values.CanExport,
            CanViewAnalytics = values.CanViewAnalytics,
            ViewScope = values.ViewScope,
        };

    private static async Task AssertSectionAsync(
        PaymentContext db,
        User user,
        string section,
        bool canView,
        bool canCreate,
        bool canEdit,
        bool canDelete,
        bool canExport)
    {
        var principal = CreatePrincipal(user);
        Assert.Equal(canView, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanView));
        Assert.Equal(canCreate, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanCreate));
        Assert.Equal(canEdit, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanEdit));
        Assert.Equal(canDelete, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanDelete));
        Assert.Equal(canExport, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanExport));
    }

    private static async Task AssertVisibilityScopeAsync(
        PaymentContext db,
        User user,
        string section,
        string expectedScope)
    {
        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(user), section);

        Assert.NotNull(access);
        Assert.Equal(expectedScope, access.ViewScope);
        Assert.False(access.IsAdmin);
        Assert.True(access.CanSeeUser(user.Id));
        Assert.False(access.CanSeeUser(user.Id + 10_000));
    }

    private static async Task AssertSectionFilterAsync(PaymentContext db, User user, string section, bool shouldAllow)
    {
        var filter = new RequireSectionPermissionFilter(
            section,
            RequireSectionPermissionOperations.View,
            db);
        var context = CreateActionExecutingContext(CreatePrincipal(user));
        var nextWasCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextWasCalled = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.Equal(shouldAllow, nextWasCalled);
        if (shouldAllow)
        {
            Assert.Null(context.Result);
        }
        else
        {
            Assert.IsType<ForbidResult>(context.Result);
        }
    }

    private static async Task<(int VisibleId, int HiddenId)> SeedDataScopedRecordsAsync(
        PaymentContext db,
        string section,
        int visibleUserId,
        int hiddenUserId)
    {
        switch (section)
        {
            case RolePermissionCatalog.Clients:
                {
                    var visible = new Client { Name = "Visible client", ResponsibleUserId = visibleUserId };
                    var hidden = new Client { Name = "Hidden client", ResponsibleUserId = hiddenUserId };
                    db.Clients.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.LegalEntities:
                {
                    var visible = new LegalEntity { ShortName = "Visible legal", ResponsibleUserId = visibleUserId };
                    var hidden = new LegalEntity { ShortName = "Hidden legal", ResponsibleUserId = hiddenUserId };
                    db.LegalEntities.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.Cases:
                {
                    var visibleClient = new Client { Name = "Visible case client", ResponsibleUserId = visibleUserId };
                    var hiddenClient = new Client { Name = "Hidden case client", ResponsibleUserId = hiddenUserId };
                    var visible = new ClientCase { Title = "Visible case", Client = visibleClient, ResponsibleUserId = visibleUserId };
                    var hidden = new ClientCase { Title = "Hidden case", Client = hiddenClient, ResponsibleUserId = hiddenUserId };
                    db.ClientCases.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.Requests:
                {
                    var client = new Client { Name = "Request client", Phone = "+79000000009" };
                    db.Clients.Add(client);
                    await db.SaveChangesAsync();
                    var visible = new SalesRequest { ClientId = client.Id, Summary = "visible", ResponsibleUserId = visibleUserId, CreatedByUserId = visibleUserId };
                    var hidden = new SalesRequest { ClientId = client.Id, Summary = "hidden", ResponsibleUserId = hiddenUserId, CreatedByUserId = hiddenUserId };
                    db.SalesRequests.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.Calendar:
                {
                    var visible = new TaskJournalItem
                    {
                        Title = "Visible calendar task",
                        ObjectName = "Visible calendar task",
                        AssigneeUserId = visibleUserId,
                        AuthorUserId = hiddenUserId,
                        DueAt = DateTime.UtcNow.AddDays(1)
                    };
                    var hidden = new TaskJournalItem
                    {
                        Title = "Hidden calendar task",
                        ObjectName = "Hidden calendar task",
                        AssigneeUserId = hiddenUserId,
                        AuthorUserId = visibleUserId,
                        DueAt = DateTime.UtcNow.AddDays(1)
                    };
                    db.TaskJournalItems.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.Tasks:
                {
                    var visible = new TaskJournalItem
                    {
                        Title = "Visible task",
                        ObjectName = "Visible task",
                        AssigneeUserId = hiddenUserId,
                        AuthorUserId = visibleUserId,
                        DueAt = DateTime.UtcNow.AddDays(1)
                    };
                    var hidden = new TaskJournalItem
                    {
                        Title = "Hidden task",
                        ObjectName = "Hidden task",
                        AssigneeUserId = hiddenUserId,
                        AuthorUserId = hiddenUserId,
                        DueAt = DateTime.UtcNow.AddDays(1)
                    };
                    db.TaskJournalItems.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.Accounts:
            case RolePermissionCatalog.Payments:
                {
                    var visibleClient = new Client { Name = $"Visible {section} client", ResponsibleUserId = visibleUserId };
                    var hiddenClient = new Client { Name = $"Hidden {section} client", ResponsibleUserId = hiddenUserId };
                    var visible = new Payment
                    {
                        Client = visibleClient,
                        Type = PaymentType.Income,
                        Status = PaymentStatus.Pending,
                        DocumentKind = section == RolePermissionCatalog.Accounts ? DocumentKind.Invoice : DocumentKind.Payment,
                        Amount = 1_000m,
                        Date = DateTime.UtcNow.Date,
                        PlannedDate = DateTime.UtcNow.Date
                    };
                    var hidden = new Payment
                    {
                        Client = hiddenClient,
                        Type = PaymentType.Income,
                        Status = PaymentStatus.Pending,
                        DocumentKind = section == RolePermissionCatalog.Accounts ? DocumentKind.Invoice : DocumentKind.Payment,
                        Amount = 1_000m,
                        Date = DateTime.UtcNow.Date,
                        PlannedDate = DateTime.UtcNow.Date
                    };
                    db.Payments.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.Acts:
                {
                    var visibleClient = new Client { Name = "Visible act client", ResponsibleUserId = visibleUserId };
                    var hiddenClient = new Client { Name = "Hidden act client", ResponsibleUserId = hiddenUserId };
                    var visible = new Act { Number = "ACT-VISIBLE", Date = DateTime.UtcNow.Date, Amount = 1_000m, Client = visibleClient };
                    var hidden = new Act { Number = "ACT-HIDDEN", Date = DateTime.UtcNow.Date, Amount = 1_000m, Client = hiddenClient };
                    db.Acts.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            case RolePermissionCatalog.Contracts:
                {
                    var visibleClient = new Client { Name = "Visible contract client", ResponsibleUserId = visibleUserId };
                    var hiddenClient = new Client { Name = "Hidden contract client", ResponsibleUserId = hiddenUserId };
                    var visible = new Contract { Number = "CONTRACT-VISIBLE", Date = DateTime.UtcNow.Date, Status = ContractStatus.Draft };
                    var hidden = new Contract { Number = "CONTRACT-HIDDEN", Date = DateTime.UtcNow.Date, Status = ContractStatus.Draft };
                    visible.ClientContracts.Add(new ClientContract { Client = visibleClient, Contract = visible });
                    hidden.ClientContracts.Add(new ClientContract { Client = hiddenClient, Contract = hidden });
                    db.Contracts.AddRange(visible, hidden);
                    await db.SaveChangesAsync();
                    return (visible.Id, hidden.Id);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported data scoped section.");
        }
    }

    private static Task<bool> IsDataScopedRecordVisibleAsync(
        PaymentContext db,
        string section,
        DataVisibilityAccess access,
        int id)
        => section switch
        {
            RolePermissionCatalog.Calendar or RolePermissionCatalog.Tasks =>
                db.TaskJournalItems.ApplyTaskVisibility(access, db).AnyAsync(item => item.Id == id),
            RolePermissionCatalog.LegalEntities =>
                db.LegalEntities.ApplyLegalEntityVisibility(access).AnyAsync(item => item.Id == id),
            RolePermissionCatalog.Clients =>
                db.Clients.ApplyClientVisibility(access).AnyAsync(item => item.Id == id),
            RolePermissionCatalog.Cases =>
                db.ClientCases.ApplyClientCaseVisibility(access).AnyAsync(item => item.Id == id),
            RolePermissionCatalog.Requests =>
                db.SalesRequests.ApplySalesRequestVisibility(access).AnyAsync(item => item.Id == id),
            RolePermissionCatalog.Accounts or RolePermissionCatalog.Payments =>
                db.Payments.ApplyPaymentVisibility(access).AnyAsync(item => item.Id == id),
            RolePermissionCatalog.Acts =>
                db.Acts.ApplyActVisibility(access).AnyAsync(item => item.Id == id),
            RolePermissionCatalog.Contracts =>
                db.Contracts.ApplyContractVisibility(access).AnyAsync(item => item.Id == id),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported data scoped section.")
        };

    private static ClaimsPrincipal CreatePrincipal(User user, bool isAdmin = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName)
        };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ActionExecutingContext CreateActionExecutingContext(ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
    }

    private static ActionExecutedContext CreateActionExecutedContext(ActionExecutingContext context)
        => new(context, [], controller: new object());

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string name,
        RolePermission? permission = null,
        string roleName = "employee")
    {
        var role = new Role { Name = roleName, Description = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        if (permission is not null)
        {
            permission.RoleId = role.Id;
            db.RolePermissions.Add(permission);
        }

        var user = new User
        {
            Email = $"{name}@example.test",
            PasswordHash = "hash",
            FullName = name,
            RoleId = role.Id,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> CreateUserWithPermissionsAsync(
        PaymentContext db,
        string name,
        string roleName,
        IEnumerable<RolePermission> permissions)
    {
        var role = new Role { Name = roleName, Description = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            permission.RoleId = role.Id;
            db.RolePermissions.Add(permission);
        }

        var user = new User
        {
            Email = $"{name}@example.test",
            PasswordHash = "hash",
            FullName = name,
            RoleId = role.Id,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
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
}
