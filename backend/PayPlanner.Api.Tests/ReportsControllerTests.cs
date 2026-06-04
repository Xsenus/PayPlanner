using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ReportsControllerTests
{
    [Fact]
    public async Task LegalWork_GroupsVisibleCasesDeadlinesTasksAndFinance()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "lawyer",
            Description = "Юрист",
        };
        var responsible = new User
        {
            Email = "lawyer@example.test",
            FullName = "Иванов Иван",
            IsActive = true,
            Role = role,
        };
        var client = new Client
        {
            Name = "Земцов Виктор",
            ResponsibleUser = responsible,
        };
        db.Roles.Add(role);
        db.Users.Add(responsible);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var today = DateTime.UtcNow.Date;
        var activeCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "З-1",
            Title = "Банкротство",
            CaseType = "Банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
            DueDate = today.AddDays(-1),
            AppealDeadlineAt = today.AddDays(3),
            FeeAmount = 25_000m,
        };
        var closedCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "З-2",
            Title = "Закрытое дело",
            CaseType = "Наследство",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = responsible.Id,
            CreatedAt = today.AddDays(-5),
            StartDate = today.AddDays(-5),
            FeeAmount = 5_000m,
        };
        var lostCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "З-3",
            Title = "Проигранное дело",
            CaseType = "Гражданское",
            Status = ClientCaseStatus.Lost,
            ResponsibleUserId = responsible.Id,
            CreatedAt = today.AddDays(-4),
            StartDate = today.AddDays(-4),
            FeeAmount = 7_000m,
        };
        db.ClientCases.AddRange(activeCase, closedCase, lostCase);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            ClientId = client.Id,
            ClientCaseId = activeCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 10_000m,
            PaidAmount = 10_000m,
            Date = today,
            PlannedDate = today,
            CreatedAt = today,
        });
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientId = client.Id,
            ClientCaseId = activeCase.Id,
            ObjectName = "Земцов Виктор",
            Title = "Подготовить документы",
            AssigneeUserId = responsible.Id,
            AssigneeName = responsible.FullName,
            DueAt = today.AddDays(-1).AddHours(9),
            Status = "onApproval",
            Priority = "high",
            Type = "task",
            CreatedAt = today.AddDays(-2),
        });
        await db.SaveChangesAsync();

        var pdfService = new FakeInvoiceDocumentPdfService([0x25, 0x50, 0x44, 0x46]);
        var controller = CreateController(db, CreateAdminPrincipal(), pdfService);

        var action = await controller.LegalWork(today.AddDays(-30), today.AddDays(30), null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<LegalWorkReportDto>(ok.Value);
        Assert.Equal(3, report.TotalCases);
        Assert.Equal(1, report.ActiveCases);
        Assert.Equal(1, report.ClosedCases);
        Assert.Equal(1, report.LostCases);
        Assert.Equal(1, report.OverdueDeadlineCount);
        Assert.Equal(1, report.UpcomingDeadlineCount);
        Assert.Equal(1, report.OpenTaskCount);
        Assert.Equal(1, report.OverdueTaskCount);
        Assert.Equal(1, report.OnApprovalTaskCount);
        Assert.Equal(37_000m, report.FeeTotal);
        Assert.Equal(10_000m, report.PaidTotal);
        Assert.Equal(27_000m, report.OutstandingTotal);
        Assert.Equal(3, report.Cases.Count);
        Assert.Contains(report.Cases, item =>
            item.CaseId == activeCase.Id
            && item.OpenTasks == 1
            && item.OverdueTasks == 1
            && item.OverdueDeadlines == 1
            && item.PaidTotal == 10_000m
            && item.OutstandingTotal == 15_000m);
        Assert.Contains(report.FinanceByStatus, item =>
            item.Status == nameof(ClientCaseStatus.Open)
            && item.CaseCount == 1
            && item.FeeTotal == 25_000m
            && item.PaidTotal == 10_000m);
        Assert.Contains(report.DeadlinesByType, item =>
            item.DeadlineType == "due"
            && item.OverdueCount == 1);

        var responsibleRow = Assert.Single(report.Responsibles);
        Assert.Equal(responsible.Id, responsibleRow.ResponsibleUserId);
        Assert.Equal("Иванов Иван", responsibleRow.ResponsibleName);
        Assert.Equal(1, responsibleRow.ActiveCases);
        Assert.Equal(1, responsibleRow.ClosedCases);
        Assert.Equal(1, responsibleRow.LostCases);
        Assert.Equal(1, responsibleRow.OpenTasks);
        Assert.Equal(1, responsibleRow.OverdueDeadlines);

        Assert.Contains(report.CasesByType, row => row.CaseType == "Банкротство" && row.ActiveTasks == 1);
        Assert.Contains(report.CasesByStatus, row => row.Status == nameof(ClientCaseStatus.Open) && row.OverdueDeadlines == 1);
        Assert.Equal("Крайний срок по делу", Assert.Single(report.OverdueDeadlines).DeadlineLabel);
        Assert.Equal("Срок обжалования", Assert.Single(report.UpcomingDeadlines).DeadlineLabel);

        var filteredAction = await controller.LegalWork(
            today.AddDays(-30),
            today.AddDays(30),
            nameof(ClientCaseStatus.Open),
            responsible.Id,
            "Банк",
            CancellationToken.None);
        var filteredReport = Assert.IsType<LegalWorkReportDto>(Assert.IsType<OkObjectResult>(filteredAction.Result).Value);
        Assert.Equal(1, filteredReport.TotalCases);
        Assert.Equal(nameof(ClientCaseStatus.Open), filteredReport.StatusFilter);
        Assert.Equal(responsible.Id, filteredReport.ResponsibleUserIdFilter);
        Assert.Equal("Банк", filteredReport.CaseTypeFilter);
        Assert.Equal(activeCase.Id, Assert.Single(filteredReport.Cases).CaseId);

        var csvAction = await controller.ExportLegalWorkCsv(
            today.AddDays(-30),
            today.AddDays(30),
            nameof(ClientCaseStatus.Open),
            responsible.Id,
            "Банк",
            CancellationToken.None);
        var csv = Assert.IsType<FileContentResult>(csvAction);
        Assert.Equal("text/csv; charset=utf-8", csv.ContentType);
        Assert.Matches(@"^legal-work-report-\d{8}-\d{4}\.csv$", csv.FileDownloadName);

        var excelAction = await controller.ExportLegalWorkExcel(
            today.AddDays(-30),
            today.AddDays(30),
            nameof(ClientCaseStatus.Open),
            responsible.Id,
            "Банк",
            CancellationToken.None);
        var excel = Assert.IsType<FileContentResult>(excelAction);
        Assert.Equal("application/vnd.ms-excel; charset=utf-8", excel.ContentType);
        Assert.EndsWith(".xls", excel.FileDownloadName, StringComparison.Ordinal);
        var excelText = System.Text.Encoding.UTF8.GetString(excel.FileContents);
        Assert.Contains("Excel.Sheet", excelText, StringComparison.Ordinal);
        Assert.Contains("<Worksheet ss:Name=\"Legal work\">", excelText, StringComparison.Ordinal);

        var pdfAction = await controller.ExportLegalWorkPdf(
            today.AddDays(-30),
            today.AddDays(30),
            nameof(ClientCaseStatus.Open),
            responsible.Id,
            "Банк",
            CancellationToken.None);
        var pdf = Assert.IsType<FileContentResult>(pdfAction);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Equal([0x25, 0x50, 0x44, 0x46], pdf.FileContents);
        Assert.Contains("Отчет по юридической работе", pdfService.LastHtml, StringComparison.Ordinal);

        var csvText = System.Text.Encoding.UTF8.GetString(csv.FileContents);
        Assert.Contains("Банкротство", csvText, StringComparison.Ordinal);
        Assert.Contains("Финансы по статусам", csvText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagementReportExports_ReturnExcelAndPdfFiles()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "manager", Description = "Manager" };
        var responsible = new User
        {
            Email = "manager@example.test",
            FullName = "Manager User",
            IsActive = true,
            Role = role,
        };
        var client = new Client
        {
            Name = "Export Client",
            ResponsibleUser = responsible,
        };
        db.Roles.Add(role);
        db.Users.Add(responsible);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var today = DateTime.UtcNow.Date;
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "EXP-1",
            Title = "Export case",
            CaseType = "Bankruptcy",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = responsible.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
            DueDate = today.AddDays(-1),
            FeeAmount = 10_000m,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var pdfService = new FakeInvoiceDocumentPdfService([0x25, 0x50, 0x44, 0x46]);
        var controller = CreateController(db, CreateAdminPrincipal(), pdfService);
        var reportKinds = new[]
        {
            "case-finance",
            "case-success",
            "current-cases",
            "lawyer-workload",
            "overdue-deadlines",
            "case-activity",
        };

        foreach (var reportKind in reportKinds)
        {
            var excelAction = await controller.ExportManagementReportExcel(
                reportKind,
                today.AddDays(-30),
                today.AddDays(30),
                today,
                null,
                null,
                null,
                null,
                7,
                null,
                "all",
                null,
                null,
                50,
                CancellationToken.None);
            var excel = Assert.IsType<FileContentResult>(excelAction);
            Assert.Equal("application/vnd.ms-excel; charset=utf-8", excel.ContentType);
            Assert.EndsWith(".xls", excel.FileDownloadName, StringComparison.Ordinal);
            Assert.Contains("Excel.Sheet", System.Text.Encoding.UTF8.GetString(excel.FileContents), StringComparison.Ordinal);
        }

        var pdfAction = await controller.ExportManagementReportPdf(
            "case-success",
            today.AddDays(-30),
            today.AddDays(30),
            today,
            null,
            null,
            null,
            null,
            7,
            null,
            "all",
            null,
            null,
            50,
            CancellationToken.None);
        var pdf = Assert.IsType<FileContentResult>(pdfAction);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Equal([0x25, 0x50, 0x44, 0x46], pdf.FileContents);
        Assert.Contains("Статистика успешности дел", pdfService.LastHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegalWorkPdf_UsesProfileTimeZoneForGeneratedAt()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "reports-exporter", Description = "Reports exporter" };
        var user = new User
        {
            Email = "reports-exporter@example.test",
            FullName = "Reports Exporter",
            IsActive = true,
            Role = role,
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                CanExport = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Accounts,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });
        await db.SaveChangesAsync();

        var pdfService = new FakeInvoiceDocumentPdfService([0x25, 0x50, 0x44, 0x46]);
        var controller = CreateController(db, CreateUserPrincipal(user.Id), pdfService);
        var expectedTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var before = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var beforeFileStamp = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, expectedTimeZone).ToString("yyyyMMdd-HHmm");

        var action = await controller.ExportLegalWorkPdf(null, null, null, null, null, CancellationToken.None);

        var after = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var file = Assert.IsType<FileContentResult>(action);
        var afterFileStamp = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, expectedTimeZone).ToString("yyyyMMdd-HHmm");
        Assert.True(
            pdfService.LastHtml.Contains(before, StringComparison.Ordinal) ||
            pdfService.LastHtml.Contains(after, StringComparison.Ordinal),
            $"PDF HTML must contain generated time in Europe/Samara. Expected {before} or {after}. HTML: {pdfService.LastHtml}");
        Assert.True(
            file.FileDownloadName == $"legal-work-report-{beforeFileStamp}.pdf" ||
            file.FileDownloadName == $"legal-work-report-{afterFileStamp}.pdf",
            $"PDF file name must use Europe/Samara local time. Expected {beforeFileStamp} or {afterFileStamp}. Actual: {file.FileDownloadName}");
    }

    [Fact]
    public async Task LegalWorkExports_FormatReportDatesInProfileTimeZone()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "reports-exporter-dates", Description = "Reports exporter dates" };
        var user = new User
        {
            Email = "reports-exporter-dates@example.test",
            FullName = "Reports Exporter Dates",
            IsActive = true,
            Role = role,
        };
        var client = new Client
        {
            Name = "Timezone Report Client",
            ResponsibleUser = user,
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                CanExport = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Accounts,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });

        var instantUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(22).AddMinutes(30), DateTimeKind.Utc);
        db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "TZ-REPORT",
            Title = "Timezone report case",
            CaseType = "Суд",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = user.Id,
            CreatedAt = instantUtc,
            StartDate = instantUtc.Date,
            NextHearingAt = instantUtc,
            FeeAmount = 10_000m,
        });
        await db.SaveChangesAsync();

        var pdfService = new FakeInvoiceDocumentPdfService([0x25, 0x50, 0x44, 0x46]);
        var controller = CreateController(db, CreateUserPrincipal(user.Id), pdfService);
        var from = instantUtc.Date.AddDays(-1);
        var to = instantUtc.Date.AddDays(2);
        var expectedDate = CalendarTimeZoneHelper
            .ToUserLocal(instantUtc, CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara"))
            .ToString("dd.MM.yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

        var csv = Assert.IsType<FileContentResult>(
            await controller.ExportLegalWorkCsv(from, to, null, null, null, CancellationToken.None));
        var csvText = System.Text.Encoding.UTF8.GetString(csv.FileContents);
        Assert.Matches(@"^legal-work-report-\d{8}-\d{4}\.csv$", csv.FileDownloadName);
        Assert.Contains(expectedDate, csvText, StringComparison.Ordinal);

        var excel = Assert.IsType<FileContentResult>(
            await controller.ExportLegalWorkExcel(from, to, null, null, null, CancellationToken.None));
        var excelText = System.Text.Encoding.UTF8.GetString(excel.FileContents);
        Assert.Contains(expectedDate, excelText, StringComparison.Ordinal);

        var pdf = Assert.IsType<FileContentResult>(
            await controller.ExportLegalWorkPdf(from, to, null, null, null, CancellationToken.None));
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Contains(expectedDate, pdfService.LastHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegalWorkExports_RequireExportPermission()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "reports-viewer", Description = "Reports viewer" };
        var user = new User
        {
            Email = "reports-viewer@example.test",
            FullName = "Reports Viewer",
            IsActive = true,
            Role = role,
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Reports,
            CanView = true,
            CanExport = false,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(
            db,
            CreateUserPrincipal(user.Id),
            new FakeInvoiceDocumentPdfService([0x25, 0x50, 0x44, 0x46]));

        Assert.IsType<ForbidResult>(await controller.ExportLegalWorkCsv(null, null, null, null, null, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.ExportLegalWorkExcel(null, null, null, null, null, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.ExportLegalWorkPdf(null, null, null, null, null, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.ExportManagementReportExcel("case-finance", null, null, null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.ExportManagementReportPdf("case-finance", null, null, null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task CaseActivity_BuildsInactivityDiagnostics()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "case-activity-admin", Description = "Admin" };
        var responsible = new User
        {
            Email = "case-owner@example.test",
            FullName = "Case Owner",
            IsActive = true,
            Role = role,
        };
        var client = new Client
        {
            Name = "Activity Client",
            ResponsibleUser = responsible,
        };

        db.Roles.Add(role);
        db.Users.Add(responsible);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var inactiveCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "A-1",
            Title = "Inactive case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            CreatedAt = now.AddDays(-45),
            LastViewedAt = now.AddDays(-45),
            ActivityConfirmedAt = now.AddDays(-45),
            InactivityRemindersEnabled = true,
        };
        var activeCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "A-2",
            Title = "Active case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            CreatedAt = now.AddDays(-40),
            InactivityRemindersEnabled = true,
        };
        var disabledCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "A-3",
            Title = "Disabled case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            CreatedAt = now.AddDays(-40),
            InactivityRemindersEnabled = false,
        };
        var closedCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "A-4",
            Title = "Closed case",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = responsible.Id,
            CreatedAt = now.AddDays(-40),
        };
        db.ClientCases.AddRange(inactiveCase, activeCase, disabledCase, closedCase);
        await db.SaveChangesAsync();

        var oldCompletedTask = new TaskJournalItem
        {
            ClientCaseId = inactiveCase.Id,
            ObjectName = "Inactive case",
            Title = "Old completed task",
            Status = "done",
            DueAt = now.AddDays(-40),
            CreatedAt = now.AddDays(-40),
            UpdatedAt = now.AddDays(-40),
        };
        db.TaskJournalItems.AddRange(
            oldCompletedTask,
            new TaskJournalItem
            {
                ClientCaseId = activeCase.Id,
                ObjectName = "Active case",
                Title = "Open task",
                Status = "new",
                DueAt = now.AddDays(1),
                CreatedAt = now.AddDays(-1),
                UpdatedAt = now.AddDays(-1),
            });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE TaskJournalItems
            SET CreatedAt = {now.AddDays(-40)}, UpdatedAt = {now.AddDays(-40)}
            WHERE Id = {oldCompletedTask.Id}
            """);

        var controller = CreateController(db, CreateAdminPrincipal());

        var action = await controller.CaseActivity(14, 30, "all", 10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CaseActivityReportDto>(ok.Value);
        Assert.Equal(4, report.TotalVisibleCases);
        Assert.Equal(3, report.ActiveCaseCount);
        Assert.Equal(1, report.FinishedCaseCount);
        Assert.Equal(1, report.InactiveCaseCount);
        Assert.Equal(1, report.EscalatedCaseCount);
        Assert.Equal(1, report.DisabledCaseCount);
        Assert.Equal(1, report.WithActiveTasksCount);

        var row = Assert.Single(report.Items, item => item.CaseId == inactiveCase.Id);
        Assert.True(row.IsInactive);
        Assert.True(row.IsEscalated);
        Assert.Equal(0, row.ActiveTaskCount);
        Assert.Equal(1, row.CompletedTaskCount);
        Assert.Equal(DateTimeKind.Utc, report.GeneratedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, row.LastActivityAt.Kind);
        Assert.Equal(DateTimeKind.Utc, row.LastTaskAt?.Kind);
        Assert.Contains("Нет активных задач", row.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaseActivity_UsesCaseVisibility()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "case-activity-lawyer", Description = "Lawyer" };
        var visibleUser = new User
        {
            Email = "visible-case-report@example.test",
            FullName = "Visible User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-case-report@example.test",
            FullName = "Hidden User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var visibleClient = new Client { Name = "Visible Client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden Client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        db.ClientCases.AddRange(
            new ClientCase
            {
                ClientId = visibleClient.Id,
                CaseNumber = "VISIBLE",
                Title = "Visible case",
                Status = ClientCaseStatus.Open,
                CreatedAt = DateTime.UtcNow.AddDays(-40),
            },
            new ClientCase
            {
                ClientId = hiddenClient.Id,
                CaseNumber = "HIDDEN",
                Title = "Hidden case",
                Status = ClientCaseStatus.Open,
                CreatedAt = DateTime.UtcNow.AddDays(-40),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.CaseActivity(14, 30, "all", 10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CaseActivityReportDto>(ok.Value);
        Assert.Equal(1, report.TotalVisibleCases);
        var row = Assert.Single(report.Items);
        Assert.Equal("VISIBLE", row.CaseNumber);
    }

    [Fact]
    public async Task CurrentCases_UsesCaseAndTaskVisibility()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "current-cases-lawyer", Description = "Lawyer" };
        var visibleUser = new User
        {
            Email = "visible-current-cases@example.test",
            FullName = "Visible Current User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-current-cases@example.test",
            FullName = "Hidden Current User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var visibleClient = new Client { Name = "Visible Current Client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden Current Client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var asOf = new DateTime(2026, 5, 26);
        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "CURRENT-VISIBLE",
            Title = "Visible current case",
            CaseType = "Банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = asOf.AddDays(-20),
            StartDate = asOf.AddDays(-15),
            DueDate = asOf.AddDays(-1),
            NextHearingAt = new DateTime(2026, 5, 26, 12, 0, 0),
        };
        var onHoldCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "CURRENT-HOLD",
            Title = "On hold current case",
            CaseType = "Наследство",
            Status = ClientCaseStatus.OnHold,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = asOf.AddDays(-10),
            StartDate = asOf.AddDays(-10),
            DueDate = asOf.AddDays(10),
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "CURRENT-HIDDEN",
            Title = "Hidden current case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = hiddenUser.Id,
            CreatedAt = asOf.AddDays(-20),
            DueDate = asOf.AddDays(-1),
        };
        var closedCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "CURRENT-CLOSED",
            Title = "Closed current case",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = asOf.AddDays(-20),
            DueDate = asOf.AddDays(-1),
        };
        db.ClientCases.AddRange(visibleCase, onHoldCase, hiddenCase, closedCase);
        await db.SaveChangesAsync();

        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientCaseId = visibleCase.Id,
                ObjectName = visibleCase.Title,
                Title = "Visible overdue task",
                AssigneeUserId = visibleUser.Id,
                AssigneeName = visibleUser.FullName,
                DueAt = asOf.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                CreatedAt = asOf.AddDays(-3),
            },
            new TaskJournalItem
            {
                ClientCaseId = hiddenCase.Id,
                ObjectName = hiddenCase.Title,
                Title = "Hidden overdue task",
                AssigneeUserId = hiddenUser.Id,
                AssigneeName = hiddenUser.FullName,
                DueAt = asOf.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                CreatedAt = asOf.AddDays(-3),
            });
        db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
        {
            ClientCaseId = visibleCase.Id,
            DeadlineType = "appeal",
            Title = "Подать апелляцию",
            DueAt = asOf.AddDays(-2),
            Status = "active",
            CreatedAt = asOf.AddDays(-10),
        });
        db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
        {
            ClientCaseId = visibleCase.Id,
            DeadlineType = "motion",
            Title = "Timed procedural deadline",
            DueAt = new DateTime(2026, 5, 27, 16, 0, 0),
            AllDay = false,
            Status = "active",
            CreatedAt = asOf.AddDays(-10),
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.CurrentCases(asOf, null, null, null, 50, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CurrentCasesReportDto>(ok.Value);
        Assert.Equal(2, report.TotalCurrentCases);
        Assert.Equal(1, report.OpenCases);
        Assert.Equal(1, report.OnHoldCases);
        Assert.Equal(1, report.OverdueCases);
        Assert.Equal(1, report.WithOverdueTasksCount);
        Assert.Equal(1, report.WithOverdueDeadlinesCount);
        Assert.DoesNotContain(report.Items, item => item.CaseNumber == "CURRENT-HIDDEN");
        Assert.DoesNotContain(report.Items, item => item.CaseNumber == "CURRENT-CLOSED");

        var visibleRow = Assert.Single(report.Items, item => item.CaseNumber == "CURRENT-VISIBLE");
        Assert.True(visibleRow.IsOverdue);
        Assert.Equal(1, visibleRow.OpenTaskCount);
        Assert.Equal(1, visibleRow.OverdueTaskCount);
        Assert.Equal(2, visibleRow.ActiveDeadlineCount);
        Assert.Equal(1, visibleRow.OverdueDeadlineCount);
        Assert.Equal(15, visibleRow.DaysInWork);
        Assert.Equal(DateTimeKind.Utc, report.GeneratedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, visibleRow.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, visibleRow.LastActivityAt.Kind);
        Assert.Equal(DateTimeKind.Utc, visibleRow.NextHearingAt?.Kind);
        Assert.Equal(DateTimeKind.Utc, visibleRow.NextDeadlineAt?.Kind);
        Assert.False(visibleRow.NextDeadlineAllDay);
        Assert.Contains("\"NextHearingAt\":\"2026-05-26T12:00:00Z\"", JsonSerializer.Serialize(visibleRow));

        var filteredAction = await controller.CurrentCases(asOf, nameof(ClientCaseStatus.OnHold), visibleUser.Id, "Наслед", 10, CancellationToken.None);
        var filteredReport = Assert.IsType<CurrentCasesReportDto>(Assert.IsType<OkObjectResult>(filteredAction.Result).Value);
        var filteredRow = Assert.Single(filteredReport.Items);
        Assert.Equal("CURRENT-HOLD", filteredRow.CaseNumber);
        Assert.Equal(nameof(ClientCaseStatus.OnHold), filteredReport.StatusFilter);
        Assert.Equal(visibleUser.Id, filteredReport.ResponsibleUserIdFilter);
        Assert.Equal("Наслед", filteredReport.CaseTypeFilter);
    }

    [Fact]
    public async Task TasksReport_ReturnsOpenClosedAndAllVisibleTasks()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "task-report-role", Description = "Task reporter" };
        var visibleUser = new User
        {
            Email = "visible-task-report@example.test",
            FullName = "Visible Task User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-task-report@example.test",
            FullName = "Hidden Task User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var asOf = new DateTime(2026, 5, 26);
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ObjectName = "Visible object",
                Title = "Open overdue task",
                AssigneeUserId = visibleUser.Id,
                AssigneeName = visibleUser.FullName,
                DueAt = asOf.AddDays(-2).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                CreatedAt = asOf.AddDays(-5),
            },
            new TaskJournalItem
            {
                ObjectName = "Visible object",
                Title = "Closed done task",
                AssigneeUserId = visibleUser.Id,
                AssigneeName = visibleUser.FullName,
                DueAt = asOf.AddDays(-1).AddHours(9),
                Status = "done",
                Priority = "normal",
                Type = "document",
                CreatedAt = asOf.AddDays(-4),
            },
            new TaskJournalItem
            {
                ObjectName = "Visible object",
                Title = "Cancelled task",
                AssigneeUserId = visibleUser.Id,
                AssigneeName = visibleUser.FullName,
                DueAt = asOf.AddDays(1).AddHours(9),
                Status = "cancelled",
                Priority = "low",
                Type = "meeting",
                CreatedAt = asOf.AddDays(-3),
            },
            new TaskJournalItem
            {
                ObjectName = "Hidden object",
                Title = "Hidden task",
                AssigneeUserId = hiddenUser.Id,
                AssigneeName = hiddenUser.FullName,
                DueAt = asOf.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "urgent",
                Type = "task",
                CreatedAt = asOf.AddDays(-2),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var openAction = await controller.Tasks(asOf, "open", null, 50, CancellationToken.None);
        var openReport = Assert.IsType<TaskReportDto>(Assert.IsType<OkObjectResult>(openAction.Result).Value);

        Assert.Equal(3, openReport.TotalTaskCount);
        Assert.Equal(1, openReport.OpenTaskCount);
        Assert.Equal(2, openReport.ClosedTaskCount);
        Assert.Equal(1, openReport.OverdueTaskCount);
        Assert.Equal("open", openReport.Scope);
        var openRow = Assert.Single(openReport.Items);
        Assert.Equal("Open overdue task", openRow.Title);
        Assert.Equal(DateTimeKind.Utc, openReport.GeneratedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, openRow.DueAt.Kind);
        Assert.Equal(DateTimeKind.Utc, openRow.CreatedAt.Kind);
        Assert.DoesNotContain(openReport.Items, item => item.Title == "Hidden task");
        Assert.Contains(openReport.ByStatus, item => item.Status == "done" && item.Count == 1);
        Assert.Contains(openReport.ByResponsible, item => item.AssigneeUserId == visibleUser.Id && item.TotalTasks == 3);

        var closedAction = await controller.Tasks(asOf, "closed", null, 50, CancellationToken.None);
        var closedReport = Assert.IsType<TaskReportDto>(Assert.IsType<OkObjectResult>(closedAction.Result).Value);
        Assert.Equal("closed", closedReport.Scope);
        Assert.Equal(2, closedReport.Items.Count);
        Assert.All(closedReport.Items, item => Assert.True(item.IsClosed));

        var allAction = await controller.Tasks(asOf, "all", null, 50, CancellationToken.None);
        var allReport = Assert.IsType<TaskReportDto>(Assert.IsType<OkObjectResult>(allAction.Result).Value);
        Assert.Equal("all", allReport.Scope);
        Assert.Equal(3, allReport.Items.Count);
    }

    [Fact]
    public async Task TasksReport_UsesProfileTimeZoneForDefaultAsOf()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "task-report-timezone-role", Description = "Task reporter" };
        var user = new User
        {
            Email = "task-report-timezone@example.test",
            FullName = "Task Report Timezone User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.TimeZoneId,
        });

        var dueDate = selectedZone.ProfileToday > selectedZone.UtcToday
            ? selectedZone.ProfileToday.AddDays(-1)
            : selectedZone.ProfileToday;
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ObjectName = "Timezone task object",
            Title = "Timezone default as-of task",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            DueAt = dueDate.AddHours(9),
            Status = "new",
            Priority = "normal",
            Type = "task",
            CreatedAt = dueDate.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.Tasks(
            asOf: null,
            scope: "open",
            assigneeUserId: null,
            limit: 50,
            ct: CancellationToken.None);

        var report = Assert.IsType<TaskReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);

        Assert.Equal(selectedZone.ProfileToday, report.AsOf);
        Assert.Equal(selectedZone.ProfileToday > selectedZone.UtcToday ? 1 : 0, report.OverdueTaskCount);
        Assert.Equal(selectedZone.ProfileToday < selectedZone.UtcToday ? 1 : 0, report.DueTodayTaskCount);
    }

    [Fact]
    public async Task LawyerWorkload_UsesVisibleCasesTasksAndDeadlines()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer-workload-role", Description = "Lawyer" };
        var visibleUser = new User
        {
            Email = "visible-workload@example.test",
            FullName = "Visible Workload User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-workload@example.test",
            FullName = "Hidden Workload User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var visibleClient = new Client { Name = "Visible Workload Client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden Workload Client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var asOf = new DateTime(2026, 5, 26);
        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "WORKLOAD-VISIBLE",
            Title = "Visible workload case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = asOf.AddDays(-20),
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "WORKLOAD-HIDDEN",
            Title = "Hidden workload case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = hiddenUser.Id,
            CreatedAt = asOf.AddDays(-20),
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientCaseId = visibleCase.Id,
                ObjectName = visibleCase.Title,
                Title = "Visible overdue task",
                AssigneeUserId = visibleUser.Id,
                AssigneeName = visibleUser.FullName,
                DueAt = asOf.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                PlannedDurationMinutes = 180,
                CreatedAt = asOf.AddDays(-3),
            },
            new TaskJournalItem
            {
                ClientCaseId = visibleCase.Id,
                ObjectName = visibleCase.Title,
                Title = "Visible due soon task",
                AssigneeUserId = visibleUser.Id,
                AssigneeName = visibleUser.FullName,
                DueAt = asOf.AddDays(2).AddHours(10),
                Status = "onApproval",
                Priority = "normal",
                Type = "task",
                PlannedDurationMinutes = 120,
                CreatedAt = asOf.AddDays(-1),
            },
            new TaskJournalItem
            {
                ClientCaseId = hiddenCase.Id,
                ObjectName = hiddenCase.Title,
                Title = "Hidden task",
                AssigneeUserId = hiddenUser.Id,
                AssigneeName = hiddenUser.FullName,
                DueAt = asOf.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                PlannedDurationMinutes = 600,
                CreatedAt = asOf.AddDays(-3),
            });
        db.ClientCaseProceduralDeadlines.AddRange(
            new ClientCaseProceduralDeadline
            {
                ClientCaseId = visibleCase.Id,
                DeadlineType = "appeal",
                Title = "Просроченная апелляция",
                DueAt = asOf.AddDays(-3),
                Status = "active",
                CreatedAt = asOf.AddDays(-10),
            },
            new ClientCaseProceduralDeadline
            {
                ClientCaseId = hiddenCase.Id,
                DeadlineType = "motion",
                Title = "Скрытый срок",
                DueAt = asOf.AddDays(-5),
                Status = "active",
                CreatedAt = asOf.AddDays(-10),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.LawyerWorkload(asOf, 7, null, 50, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<LawyerWorkloadReportDto>(ok.Value);
        Assert.Equal(asOf, report.AsOf);
        Assert.Equal(7, report.HorizonDays);
        Assert.Equal(1, report.LawyerCount);
        Assert.Equal(1, report.TotalActiveCases);
        Assert.Equal(2, report.TotalOpenTasks);
        Assert.Equal(1, report.TotalOverdueTasks);
        Assert.Equal(1, report.TotalOverdueDeadlines);
        Assert.Equal(5m, report.TotalPlannedHours);

        var row = Assert.Single(report.Items);
        Assert.Equal(visibleUser.Id, row.LawyerUserId);
        Assert.Equal("Visible Workload User", row.LawyerName);
        Assert.Equal(1, row.ActiveCaseCount);
        Assert.Equal(2, row.OpenTaskCount);
        Assert.Equal(1, row.OverdueTaskCount);
        Assert.Equal(1, row.OnApprovalTaskCount);
        Assert.Equal(1, row.DueSoonTaskCount);
        Assert.Equal(1, row.OverdueDeadlineCount);
        Assert.Equal(300, row.PlannedMinutes);
        Assert.Equal(5m, row.PlannedHours);
        Assert.Equal(DateTimeKind.Utc, report.GeneratedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, row.NearestTaskDueAt?.Kind);
        Assert.True(row.NearestDeadlineAllDay);
        Assert.Equal(DateTimeKind.Unspecified, row.NearestDeadlineAt?.Kind);
        Assert.Contains("срок", row.RiskLabel, StringComparison.OrdinalIgnoreCase);

        var filteredAction = await controller.LawyerWorkload(asOf, 7, visibleUser.Id, 10, CancellationToken.None);
        var filteredReport = Assert.IsType<LawyerWorkloadReportDto>(Assert.IsType<OkObjectResult>(filteredAction.Result).Value);
        Assert.Equal(visibleUser.Id, filteredReport.LawyerUserIdFilter);
        Assert.Equal(visibleUser.Id, Assert.Single(filteredReport.Items).LawyerUserId);
    }

    [Fact]
    public async Task OverdueDeadlines_UsesVisibleProceduralDeadlines()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "deadline-report-lawyer", Description = "Lawyer" };
        var visibleUser = new User
        {
            Email = "visible-deadline-report@example.test",
            FullName = "Visible Deadline User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-deadline-report@example.test",
            FullName = "Hidden Deadline User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var visibleClient = new Client { Name = "Visible Deadline Client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden Deadline Client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var asOf = new DateTime(2026, 5, 26);
        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "DEADLINE-VISIBLE",
            Title = "Visible deadline case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = asOf.AddDays(-30),
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "DEADLINE-HIDDEN",
            Title = "Hidden deadline case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = hiddenUser.Id,
            CreatedAt = asOf.AddDays(-30),
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        db.ClientCaseProceduralDeadlines.AddRange(
            new ClientCaseProceduralDeadline
            {
                ClientCaseId = visibleCase.Id,
                DeadlineType = "appeal",
                Title = "Подать апелляцию",
                DueAt = asOf.AddDays(-8),
                Status = "active",
                CreatedAt = asOf.AddDays(-20),
            },
            new ClientCaseProceduralDeadline
            {
                ClientCaseId = visibleCase.Id,
                DeadlineType = "appeal",
                Title = "Закрытый срок",
                DueAt = asOf.AddDays(-10),
                Status = "closed",
                CreatedAt = asOf.AddDays(-20),
            },
            new ClientCaseProceduralDeadline
            {
                ClientCaseId = hiddenCase.Id,
                DeadlineType = "appeal",
                Title = "Скрытый срок",
                DueAt = asOf.AddDays(-12),
                Status = "active",
                CreatedAt = asOf.AddDays(-20),
            },
            new ClientCaseProceduralDeadline
            {
                ClientCaseId = visibleCase.Id,
                DeadlineType = "motion",
                Title = "Будущий срок",
                DueAt = asOf.AddDays(1),
                Status = "active",
                CreatedAt = asOf.AddDays(-20),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.OverdueDeadlines(asOf, "appeal", null, 20, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<OverdueProceduralDeadlinesReportDto>(ok.Value);
        Assert.Equal(asOf, report.AsOf);
        Assert.Equal("appeal", report.DeadlineTypeFilter);
        Assert.Equal(1, report.TotalOverdueCount);
        Assert.Equal(1, report.CriticalOverdueCount);
        Assert.Equal(20, report.Limit);

        var row = Assert.Single(report.Items);
        Assert.Equal("DEADLINE-VISIBLE", row.CaseNumber);
        Assert.Equal("Подать апелляцию", row.Title);
        Assert.Equal("Апелляция", row.DeadlineTypeLabel);
        Assert.Equal("Активен", row.StatusLabel);
        Assert.Equal(8, row.DaysOverdue);
        Assert.Equal(visibleUser.Id, row.ResponsibleUserId);
        Assert.Equal(DateTimeKind.Utc, report.GeneratedAt.Kind);
        Assert.True(row.AllDay);
        Assert.Equal(DateTimeKind.Unspecified, row.DueAt.Kind);

        var type = Assert.Single(report.ByType);
        Assert.Equal("appeal", type.DeadlineType);
        Assert.Equal(1, type.CriticalCount);

        var responsible = Assert.Single(report.ByResponsible);
        Assert.Equal(visibleUser.Id, responsible.ResponsibleUserId);
        Assert.Equal(1, responsible.Count);
    }

    [Fact]
    public async Task LegalWork_UsesCaseTaskAndPaymentVisibility()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "legal-work-lawyer", Description = "Lawyer" };
        var visibleUser = new User
        {
            Email = "visible-legal-work@example.test",
            FullName = "Visible Legal Work User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-legal-work@example.test",
            FullName = "Hidden Legal Work User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Accounts,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var today = DateTime.UtcNow.Date;
        var visibleClient = new Client { Name = "Visible Legal Client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden Legal Client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "LEGAL-VISIBLE",
            Title = "Visible legal work case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
            DueDate = today.AddDays(-1),
            FeeAmount = 30_000m,
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "LEGAL-HIDDEN",
            Title = "Hidden legal work case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = hiddenUser.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
            DueDate = today.AddDays(-1),
            FeeAmount = 90_000m,
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientCaseId = visibleCase.Id,
                ObjectName = visibleCase.Title,
                Title = "Visible task",
                AssigneeUserId = visibleUser.Id,
                AssigneeName = visibleUser.FullName,
                DueAt = today.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                CreatedAt = today.AddDays(-2),
            },
            new TaskJournalItem
            {
                ClientCaseId = hiddenCase.Id,
                ObjectName = hiddenCase.Title,
                Title = "Hidden task",
                AssigneeUserId = hiddenUser.Id,
                AssigneeName = hiddenUser.FullName,
                DueAt = today.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                CreatedAt = today.AddDays(-2),
            });

        var visiblePayment = new Payment
        {
            ClientId = visibleClient.Id,
            ClientCaseId = visibleCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 12_000m,
            PaidAmount = 12_000m,
            Date = today,
            PlannedDate = today,
            CreatedAt = today,
        };
        visiblePayment.Responsibles.Add(new PaymentResponsible { UserId = visibleUser.Id });
        var hiddenPayment = new Payment
        {
            ClientId = hiddenClient.Id,
            ClientCaseId = hiddenCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 50_000m,
            PaidAmount = 50_000m,
            Date = today,
            PlannedDate = today,
            CreatedAt = today,
        };
        hiddenPayment.Responsibles.Add(new PaymentResponsible { UserId = hiddenUser.Id });
        db.Payments.AddRange(visiblePayment, hiddenPayment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.LegalWork(today.AddDays(-30), today.AddDays(30), null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<LegalWorkReportDto>(ok.Value);
        Assert.Equal(1, report.TotalCases);
        Assert.Equal(30_000m, report.FeeTotal);
        Assert.Equal(12_000m, report.PaidTotal);
        Assert.Equal(18_000m, report.OutstandingTotal);
        Assert.Equal(1, report.OpenTaskCount);
        Assert.Equal("LEGAL-VISIBLE", Assert.Single(report.OverdueDeadlines).CaseNumber);
        Assert.DoesNotContain(report.Responsibles, item => item.ResponsibleUserId == hiddenUser.Id);
    }

    [Fact]
    public async Task CaseFinance_UsesVisibleCasesAndPayments()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "case-finance-lawyer", Description = "Lawyer" };
        var visibleUser = new User
        {
            Email = "visible-case-finance@example.test",
            FullName = "Visible Case Finance User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-case-finance@example.test",
            FullName = "Hidden Case Finance User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Accounts,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var today = DateTime.UtcNow.Date;
        var visibleClient = new Client { Name = "Visible Finance Client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden Finance Client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "FIN-VISIBLE",
            Title = "Visible finance case",
            CaseType = "Банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
            DueDate = today.AddDays(20),
            FeeAmount = 30_000m,
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "FIN-HIDDEN",
            Title = "Hidden finance case",
            CaseType = "Наследство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = hiddenUser.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
            DueDate = today.AddDays(20),
            FeeAmount = 90_000m,
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        var visiblePayment = new Payment
        {
            ClientId = visibleClient.Id,
            ClientCaseId = visibleCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 12_000m,
            PaidAmount = 12_000m,
            Date = today,
            PlannedDate = today,
            CreatedAt = today,
        };
        visiblePayment.Responsibles.Add(new PaymentResponsible { UserId = visibleUser.Id });
        var hiddenPayment = new Payment
        {
            ClientId = hiddenClient.Id,
            ClientCaseId = hiddenCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 50_000m,
            PaidAmount = 50_000m,
            Date = today,
            PlannedDate = today,
            CreatedAt = today,
        };
        hiddenPayment.Responsibles.Add(new PaymentResponsible { UserId = hiddenUser.Id });
        db.Payments.AddRange(visiblePayment, hiddenPayment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.CaseFinance(
            today.AddDays(-30),
            today.AddDays(30),
            nameof(ClientCaseStatus.Open),
            visibleUser.Id,
            "Банк",
            10,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CaseFinanceReportDto>(ok.Value);
        Assert.Equal(1, report.TotalCases);
        Assert.Equal(1, report.ActiveCases);
        Assert.Equal(0, report.ClosedCases);
        Assert.Equal(30_000m, report.FeeTotal);
        Assert.Equal(12_000m, report.PaidTotal);
        Assert.Equal(18_000m, report.OutstandingTotal);
        Assert.Equal(40m, report.CollectionRate);
        Assert.Equal(1, report.PartiallyPaidCaseCount);
        Assert.Equal(0, report.UnpaidCaseCount);
        Assert.Equal(nameof(ClientCaseStatus.Open), report.StatusFilter);
        Assert.Equal(visibleUser.Id, report.ResponsibleUserIdFilter);
        Assert.Equal("Банк", report.CaseTypeFilter);

        var item = Assert.Single(report.Items);
        Assert.Equal(visibleCase.Id, item.CaseId);
        Assert.Equal("FIN-VISIBLE", item.CaseNumber);
        Assert.Equal("Частично оплачено", item.FinanceStateLabel);
        Assert.Equal(40m, item.PaymentRate);

        var status = Assert.Single(report.ByStatus);
        Assert.Equal(30_000m, status.FeeTotal);
        Assert.Equal(12_000m, status.PaidTotal);
        Assert.Equal(18_000m, status.OutstandingTotal);

        var responsible = Assert.Single(report.ByResponsible);
        Assert.Equal(visibleUser.Id, responsible.ResponsibleUserId);
        Assert.Equal(18_000m, responsible.OutstandingTotal);
        Assert.DoesNotContain(report.Items, row => row.CaseId == hiddenCase.Id);
    }

    [Fact]
    public async Task CaseSuccess_UsesVisibleCasesAndStatusFormula()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "case-success-lawyer", Description = "Lawyer" };
        var visibleUser = new User
        {
            Email = "visible-case-success@example.test",
            FullName = "Visible Case Success User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden-case-success@example.test",
            FullName = "Hidden Case Success User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var today = DateTime.UtcNow.Date;
        var visibleClient = new Client { Name = "Visible Success Client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden Success Client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var closedCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "SUCCESS-CLOSED",
            Title = "Closed success case",
            CaseType = "Банкротство",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = today.AddDays(-20),
            StartDate = today.AddDays(-20),
            DueDate = today.AddDays(-2),
        };
        var lostCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "SUCCESS-LOST",
            Title = "Lost success case",
            CaseType = "Банкротство",
            Status = ClientCaseStatus.Lost,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = today.AddDays(-18),
            StartDate = today.AddDays(-18),
            DueDate = today.AddDays(-1),
        };
        var openCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "SUCCESS-OPEN",
            Title = "Open success case",
            CaseType = "Семейное",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "SUCCESS-HIDDEN",
            Title = "Hidden success case",
            CaseType = "Банкротство",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = hiddenUser.Id,
            CreatedAt = today.AddDays(-10),
            StartDate = today.AddDays(-10),
            DueDate = today.AddDays(-1),
        };
        db.ClientCases.AddRange(closedCase, lostCase, openCase, hiddenCase);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.CaseSuccess(today.AddDays(-30), today.AddDays(30), null, null, null, 20, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CaseSuccessReportDto>(ok.Value);
        Assert.Equal(3, report.TotalCases);
        Assert.Equal(2, report.CompletedCases);
        Assert.Equal(1, report.SuccessfulCases);
        Assert.Equal(1, report.LostCases);
        Assert.Equal(1, report.InProgressCases);
        Assert.Equal(0, report.OnHoldCases);
        Assert.Equal(50m, report.SuccessRate);
        Assert.Equal(50m, report.LossRate);
        Assert.Equal(66.7m, report.CompletionRate);
        Assert.Contains("Closed", report.FormulaNote, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, item => item.CaseId == hiddenCase.Id);

        Assert.Contains(report.ByStatus, item => item.Status == nameof(ClientCaseStatus.Closed) && item.CaseCount == 1);
        Assert.Contains(report.ByType, item =>
            item.CaseType == "Банкротство"
            && item.TotalCases == 2
            && item.CompletedCases == 2
            && item.SuccessRate == 50m);

        var responsible = Assert.Single(report.ByResponsible);
        Assert.Equal(visibleUser.Id, responsible.ResponsibleUserId);
        Assert.Equal(3, responsible.TotalCases);
        Assert.Equal(2, responsible.CompletedCases);
        Assert.Equal(1, responsible.SuccessfulCases);
        Assert.Equal(1, responsible.LostCases);
        Assert.Equal(1, responsible.InProgressCases);
        Assert.Equal(50m, responsible.SuccessRate);

        var filteredAction = await controller.CaseSuccess(
            today.AddDays(-30),
            today.AddDays(30),
            nameof(ClientCaseStatus.Closed),
            visibleUser.Id,
            "Банк",
            10,
            CancellationToken.None);
        var filteredReport = Assert.IsType<CaseSuccessReportDto>(Assert.IsType<OkObjectResult>(filteredAction.Result).Value);
        Assert.Equal(1, filteredReport.TotalCases);
        Assert.Equal(1, filteredReport.SuccessfulCases);
        Assert.Equal(nameof(ClientCaseStatus.Closed), filteredReport.StatusFilter);
        Assert.Equal(visibleUser.Id, filteredReport.ResponsibleUserIdFilter);
        Assert.Equal("Банк", filteredReport.CaseTypeFilter);
        Assert.Equal(closedCase.Id, Assert.Single(filteredReport.Items).CaseId);
    }

    [Fact]
    public async Task CashFlow_UsesPaymentVisibilityForLinkedOperations()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "lawyer",
            Description = "Юрист",
        };
        var visibleUser = new User
        {
            Email = "visible@example.test",
            FullName = "Видимый пользователь",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = "hidden@example.test",
            FullName = "Скрытый пользователь",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.AddRange(visibleUser, hiddenUser);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                CanCreate = true,
                CanEdit = true,
                CanDelete = true,
                CanExport = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var visibleInvoice = new Invoice
        {
            Number = "101",
            Date = new DateTime(2026, 5, 5),
            CreatedAt = DateTime.UtcNow,
        };
        var hiddenInvoice = new Invoice
        {
            Number = "102",
            Date = new DateTime(2026, 5, 5),
            CreatedAt = DateTime.UtcNow,
        };
        var visiblePayment = new Payment
        {
            Invoice = visibleInvoice,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 12_000m,
            PaidAmount = 12_000m,
            Date = new DateTime(2026, 5, 5),
            PlannedDate = new DateTime(2026, 5, 5),
            CreatedAt = DateTime.UtcNow,
        };
        visiblePayment.Responsibles.Add(new PaymentResponsible { UserId = visibleUser.Id });

        var hiddenPayment = new Payment
        {
            Invoice = hiddenInvoice,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 50_000m,
            PaidAmount = 50_000m,
            Date = new DateTime(2026, 5, 5),
            PlannedDate = new DateTime(2026, 5, 5),
            CreatedAt = DateTime.UtcNow,
        };
        hiddenPayment.Responsibles.Add(new PaymentResponsible { UserId = hiddenUser.Id });

        db.Payments.AddRange(visiblePayment, hiddenPayment);
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной",
                Amount = 12_000m,
                Date = new DateTime(2026, 5, 5),
                LinkedInvoice = visibleInvoice,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной",
                Amount = 50_000m,
                Date = new DateTime(2026, 5, 5),
                LinkedInvoice = hiddenInvoice,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(12_000m, report.Income);
        Assert.Equal(0m, report.Expense);
        Assert.Equal(1, report.IncomeCount);
        Assert.DoesNotContain(report.ByAccount, item => item.Income == 50_000m);
    }

    [Fact]
    public async Task CashFlow_ReturnsForbidWhenReportsAreHiddenForRole()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "limited",
            Description = "Ограниченная роль",
        };
        var user = new User
        {
            Email = "limited@example.test",
            FullName = "Ограниченный пользователь",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Reports,
            CanView = false,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task CashFlow_ReturnsOpeningPeriodAndDailyBalances()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 4, 25), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 4, 28), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 1), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 2), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 3), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(new DateTime(2026, 5, 1), report.From);
        Assert.Equal(new DateTime(2026, 5, 31), report.To);
        Assert.Equal(8_000m, report.OpeningBalance);
        Assert.Equal(7_000m, report.Income);
        Assert.Equal(2_000m, report.Expense);
        Assert.Equal(5_000m, report.NetCashFlow);
        Assert.Equal(13_000m, report.ClosingBalance);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(2, report.ExpenseCount);

        Assert.Collection(
            report.Days,
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 1), day.Date);
                Assert.Equal(8_000m, day.OpeningBalance);
                Assert.Equal(7_000m, day.Income);
                Assert.Equal(0m, day.Expense);
                Assert.Equal(15_000m, day.ClosingBalance);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 2), day.Date);
                Assert.Equal(15_000m, day.OpeningBalance);
                Assert.Equal(0m, day.Income);
                Assert.Equal(2_000m, day.Expense);
                Assert.Equal(13_000m, day.ClosingBalance);
            });

        var bank = Assert.Single(report.BySource, item => item.Source == MoneyOperationSource.Bank);
        Assert.Equal(8_000m, bank.OpeningBalance);
        Assert.Equal(7_000m, bank.Income);
        Assert.Equal(1_500m, bank.Expense);
        Assert.Equal(13_500m, bank.ClosingBalance);

        var cash = Assert.Single(report.BySource, item => item.Source == MoneyOperationSource.Cash);
        Assert.Equal(0m, cash.OpeningBalance);
        Assert.Equal(500m, cash.Expense);
        Assert.Equal(-500m, cash.ClosingBalance);
    }

    [Fact]
    public async Task CashFlow_FiltersBySourceAndAccountName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 4, 30), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 5), "Резервный банк"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 4), "Основной банк"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            MoneyOperationSource.Bank,
            "  Основной банк  ",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(MoneyOperationSource.Bank, report.Source);
        Assert.Equal("Основной банк", report.AccountName);
        Assert.Equal(4_000m, report.OpeningBalance);
        Assert.Equal(6_000m, report.Income);
        Assert.Equal(0m, report.Expense);
        Assert.Equal(10_000m, report.ClosingBalance);
        Assert.Single(report.BySource);
        var account = Assert.Single(report.ByAccount);
        Assert.Equal("Основной банк", account.AccountName);
    }

    [Fact]
    public async Task Categories_GroupsFactOperationsByIncomeTypeAndCategoryName()
    {
        await using var db = await CreateContextAsync();
        var services = new IncomeType
        {
            Name = "Услуги",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var taxes = new IncomeType
        {
            Name = "Налоги",
            PaymentType = PaymentType.Expense,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.AddRange(services, taxes);
        await db.SaveChangesAsync();

        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", services.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк", services.Id),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", categoryName: "Розница"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 4), "Основной банк", taxes.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 5), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", services.Id));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Categories(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<MoneyCategoryReportDto>(ok.Value);
        Assert.Equal(18_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(13_000m, report.NetTotal);

        Assert.Collection(
            report.Income,
            item =>
            {
                Assert.Equal("Услуги", item.CategoryName);
                Assert.Equal(15_000m, item.Amount);
                Assert.Equal(2, item.Count);
                Assert.Equal(83.3m, item.Share);
            },
            item =>
            {
                Assert.Equal("Розница", item.CategoryName);
                Assert.Equal(3_000m, item.Amount);
                Assert.Equal(1, item.Count);
                Assert.Equal(16.7m, item.Share);
            });

        Assert.Collection(
            report.Expense,
            item =>
            {
                Assert.Equal("Налоги", item.CategoryName);
                Assert.Equal(4_000m, item.Amount);
                Assert.Equal(80.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без статьи", item.CategoryName);
                Assert.Equal(1_000m, item.Amount);
                Assert.Equal(20.0m, item.Share);
            });
    }

    [Fact]
    public async Task Counterparties_GroupsFactOperationsByCounterparty()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк", counterpartyName: "  ооо ромашка  "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Основной банк", counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 4), "Касса", counterpartyName: "ИП Петров"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 5), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк", counterpartyName: "ООО Ромашка"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Counterparties(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CounterpartyReportDto>(ok.Value);
        Assert.Equal(19_000m, report.IncomeTotal);
        Assert.Equal(6_000m, report.ExpenseTotal);
        Assert.Equal(13_000m, report.NetTotal);

        Assert.Collection(
            report.Counterparties,
            item =>
            {
                Assert.Equal("ООО Ромашка", item.CounterpartyName);
                Assert.Equal(15_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(13_000m, item.NetTotal);
                Assert.Equal(17_000m, item.TotalAmount);
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
                Assert.Equal(68.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без контрагента", item.CounterpartyName);
                Assert.Equal(4_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(16.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("ИП Петров", item.CounterpartyName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(4_000m, item.Expense);
                Assert.Equal(16.0m, item.Share);
            });
    }

    [Fact]
    public async Task Projects_GroupsFactOperationsByProjectName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 12_000m, new DateTime(2026, 5, 1), "Основной банк", projectName: "Проект Альфа"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 2), "Касса", projectName: " проект альфа "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 3), "Основной банк", projectName: "Проект Бета"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", projectName: "Проект Альфа"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Projects(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ProjectReportDto>(ok.Value);
        Assert.Equal(17_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(12_000m, report.NetTotal);

        Assert.Collection(
            report.Projects,
            item =>
            {
                Assert.Equal("Проект Альфа", item.ProjectName);
                Assert.Equal(12_000m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(9_000m, item.NetTotal);
                Assert.Equal(15_000m, item.TotalAmount);
                Assert.Equal(68.2m, item.Share);
            },
            item =>
            {
                Assert.Equal("Проект Бета", item.ProjectName);
                Assert.Equal(5_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(22.7m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без проекта", item.ProjectName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(9.1m, item.Share);
            });
    }

    [Fact]
    public async Task Directions_GroupsFactOperationsByDirectionName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 9_000m, new DateTime(2026, 5, 1), "Основной банк", directionName: "Консалтинг"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Касса", directionName: " консалтинг "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 3), "Основной банк", directionName: "Поддержка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 4), "Основной банк", directionName: "Консалтинг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", directionName: "Консалтинг"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Directions(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<DirectionReportDto>(ok.Value);
        Assert.Equal(13_000m, report.IncomeTotal);
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(10_000m, report.NetTotal);

        Assert.Collection(
            report.Directions,
            item =>
            {
                Assert.Equal("Консалтинг", item.DirectionName);
                Assert.Equal(9_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(7_000m, item.NetTotal);
                Assert.Equal(11_000m, item.TotalAmount);
                Assert.Equal(68.8m, item.Share);
            },
            item =>
            {
                Assert.Equal("Поддержка", item.DirectionName);
                Assert.Equal(4_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(25.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без направления", item.DirectionName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_000m, item.Expense);
                Assert.Equal(6.3m, item.Share);
            });
    }

    [Fact]
    public async Task SalesPoints_GroupsFactOperationsBySalesPointName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 1), "Основной банк", salesPointName: "Офис"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 2), "Касса", salesPointName: " офис "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_500m, new DateTime(2026, 5, 3), "Основной банк", salesPointName: "Онлайн"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", salesPointName: "Офис"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.SalesPoints(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<SalesPointReportDto>(ok.Value);
        Assert.Equal(11_500m, report.IncomeTotal);
        Assert.Equal(2_500m, report.ExpenseTotal);
        Assert.Equal(9_000m, report.NetTotal);

        Assert.Collection(
            report.SalesPoints,
            item =>
            {
                Assert.Equal("Офис", item.SalesPointName);
                Assert.Equal(8_000m, item.Income);
                Assert.Equal(1_500m, item.Expense);
                Assert.Equal(6_500m, item.NetTotal);
                Assert.Equal(9_500m, item.TotalAmount);
                Assert.Equal(67.9m, item.Share);
            },
            item =>
            {
                Assert.Equal("Онлайн", item.SalesPointName);
                Assert.Equal(3_500m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(25.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без точки продаж", item.SalesPointName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_000m, item.Expense);
                Assert.Equal(7.1m, item.Share);
            });
    }

    [Fact]
    public async Task Vat_GroupsFactOperationsByVatRate()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 12_000m, new DateTime(2026, 5, 1), "Основной банк", vatRate: 20m, vatAmount: 2_000m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_500m, new DateTime(2026, 5, 2), "Основной банк", vatRate: 10m, vatAmount: 500m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 3), "Основной банк", vatRate: 20m, vatAmount: 1_000m),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", vatRate: 20m, vatAmount: 16_666.50m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", vatRate: 20m, vatAmount: 8_333.33m));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Vat(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<VatReportDto>(ok.Value);
        Assert.Equal(17_500m, report.IncomeAmountTotal);
        Assert.Equal(2_500m, report.IncomeVatTotal);
        Assert.Equal(9_000m, report.ExpenseAmountTotal);
        Assert.Equal(1_000m, report.ExpenseVatTotal);
        Assert.Equal(1_500m, report.VatBalance);

        Assert.Collection(
            report.Rates,
            item =>
            {
                Assert.Equal(20m, item.VatRate);
                Assert.Equal("20%", item.VatRateLabel);
                Assert.Equal(10_000m, item.IncomeBase);
                Assert.Equal(2_000m, item.IncomeVat);
                Assert.Equal(12_000m, item.IncomeGross);
                Assert.Equal(5_000m, item.ExpenseBase);
                Assert.Equal(1_000m, item.ExpenseVat);
                Assert.Equal(6_000m, item.ExpenseGross);
                Assert.Equal(1_000m, item.NetVat);
            },
            item =>
            {
                Assert.Equal(10m, item.VatRate);
                Assert.Equal("10%", item.VatRateLabel);
                Assert.Equal(5_000m, item.IncomeBase);
                Assert.Equal(500m, item.IncomeVat);
                Assert.Equal(5_500m, item.IncomeGross);
                Assert.Equal(500m, item.NetVat);
            },
            item =>
            {
                Assert.Null(item.VatRate);
                Assert.Equal("Без НДС", item.VatRateLabel);
                Assert.Equal(3_000m, item.ExpenseBase);
                Assert.Equal(0m, item.ExpenseVat);
                Assert.Equal(-0m, item.NetVat);
            });
    }

    [Fact]
    public async Task PlanFact_ComparesPlanAndFactOperations()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 10_000m, new DateTime(2026, 5, 1), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 1), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Plan, 4_000m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 3), "Основной банк"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PlanFact(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            MoneyOperationSource.Bank,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PlanFactReportDto>(ok.Value);
        Assert.Equal(MoneyOperationSource.Bank, report.Source);
        Assert.Equal(10_000m, report.PlanIncome);
        Assert.Equal(8_000m, report.FactIncome);
        Assert.Equal(-2_000m, report.IncomeVariance);
        Assert.Equal(80.0m, report.IncomeCompletionPercent);
        Assert.Equal(4_000m, report.PlanExpense);
        Assert.Equal(5_000m, report.FactExpense);
        Assert.Equal(1_000m, report.ExpenseVariance);
        Assert.Equal(125.0m, report.ExpenseExecutionPercent);
        Assert.Equal(6_000m, report.PlanNetTotal);
        Assert.Equal(3_000m, report.FactNetTotal);
        Assert.Equal(-3_000m, report.NetVariance);

        Assert.Collection(
            report.Days,
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 1), day.Date);
                Assert.Equal(10_000m, day.PlanIncome);
                Assert.Equal(8_000m, day.FactIncome);
                Assert.Equal(-2_000m, day.IncomeVariance);
                Assert.Equal(10_000m, day.PlanNetTotal);
                Assert.Equal(8_000m, day.FactNetTotal);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 2), day.Date);
                Assert.Equal(4_000m, day.PlanExpense);
                Assert.Equal(5_000m, day.FactExpense);
                Assert.Equal(1_000m, day.ExpenseVariance);
                Assert.Equal(-4_000m, day.PlanNetTotal);
                Assert.Equal(-5_000m, day.FactNetTotal);
            });
    }

    [Fact]
    public async Task Advances_GroupsAdvanceFactOperationsByCounterpartyAndRecognition()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true, recognitionDate: new DateTime(2026, 5, 10)),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 2), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 3), "Основной банк", counterpartyName: "ИП Петров", isAdvancePayment: true, recognitionDate: new DateTime(2026, 6, 1)),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 4), "Касса", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 6), "Основной банк", counterpartyName: "ООО Ромашка"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Advances(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<AdvanceReportDto>(ok.Value);
        Assert.Equal(19_000m, report.IncomeTotal);
        Assert.Equal(4_000m, report.ExpenseTotal);
        Assert.Equal(10_000m, report.RecognizedTotal);
        Assert.Equal(13_000m, report.UnrecognizedTotal);
        Assert.Equal(1, report.RecognizedCount);
        Assert.Equal(3, report.UnrecognizedCount);

        Assert.Collection(
            report.Counterparties,
            item =>
            {
                Assert.Equal("ООО Ромашка", item.CounterpartyName);
                Assert.Equal(16_000m, item.IncomeTotal);
                Assert.Equal(0m, item.ExpenseTotal);
                Assert.Equal(10_000m, item.RecognizedTotal);
                Assert.Equal(6_000m, item.UnrecognizedTotal);
                Assert.Equal(2, item.IncomeCount);
            },
            item =>
            {
                Assert.Equal("ИП Петров", item.CounterpartyName);
                Assert.Equal(0m, item.IncomeTotal);
                Assert.Equal(4_000m, item.ExpenseTotal);
                Assert.Equal(0m, item.RecognizedTotal);
                Assert.Equal(4_000m, item.UnrecognizedTotal);
            },
            item =>
            {
                Assert.Equal("Без контрагента", item.CounterpartyName);
                Assert.Equal(3_000m, item.IncomeTotal);
                Assert.Equal(3_000m, item.UnrecognizedTotal);
            });
    }

    [Fact]
    public async Task PaymentPriorities_GroupsFactOperationsByPriority()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 9_000m, new DateTime(2026, 5, 1), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 2), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Основной банк", paymentPriority: 2),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paymentPriority: 1));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaymentPriorities(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaymentPriorityReportDto>(ok.Value);
        Assert.Equal(10_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(5_000m, report.NetTotal);

        Assert.Collection(
            report.Priorities,
            item =>
            {
                Assert.Equal(1, item.Priority);
                Assert.Equal("Высокий приоритет", item.PriorityLabel);
                Assert.Equal(9_000m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(6_000m, item.NetTotal);
                Assert.Equal(12_000m, item.TotalAmount);
                Assert.Equal(80.0m, item.Share);
            },
            item =>
            {
                Assert.Equal(2, item.Priority);
                Assert.Equal("Средний приоритет", item.PriorityLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(13.3m, item.Share);
            },
            item =>
            {
                Assert.Null(item.Priority);
                Assert.Equal("Без приоритета", item.PriorityLabel);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(6.7m, item.Share);
            });
    }

    [Fact]
    public async Task PaidAccounts_GroupsFactOperationsByPaidAccountName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 1), "Основной банк", paidAccountName: "Расчетный счет"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paidAccountName: " расчетный счет "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", paidAccountName: "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paidAccountName: "Расчетный счет"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paidAccountName: "Расчетный счет"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaidAccounts(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaidAccountReportDto>(ok.Value);
        Assert.Equal(8_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(3_000m, report.NetTotal);

        Assert.Collection(
            report.Accounts,
            item =>
            {
                Assert.Equal("Расчетный счет", item.PaidAccountName);
                Assert.Equal(7_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(5_000m, item.NetTotal);
                Assert.Equal(9_000m, item.TotalAmount);
                Assert.Equal(69.2m, item.Share);
            },
            item =>
            {
                Assert.Equal("Касса", item.PaidAccountName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(23.1m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без счета оплаты", item.PaidAccountName);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(7.7m, item.Share);
            });
    }

    [Fact]
    public async Task PaidInvoices_GroupsFactOperationsByPaidInvoiceNumber()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 1), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 4_500m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paidInvoiceNumber: " inv-10 ", paidAmount: 1_900m),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", paidInvoiceNumber: "INV-11"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк", paidAmount: 800m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 99_999m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 50_000m));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaidInvoices(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaidInvoiceReportDto>(ok.Value);
        Assert.Equal(9_000m, report.IncomeTotal);
        Assert.Equal(2_000m, report.ExpenseTotal);
        Assert.Equal(7_000m, report.NetTotal);
        Assert.Equal(10_200m, report.PaidAmountTotal);

        Assert.Collection(
            report.Invoices,
            item =>
            {
                Assert.Equal("INV-10", item.PaidInvoiceNumber);
                Assert.Equal(5_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(3_000m, item.NetTotal);
                Assert.Equal(7_000m, item.OperationTotal);
                Assert.Equal(6_400m, item.PaidAmountTotal);
                Assert.Equal(63.6m, item.Share);
            },
            item =>
            {
                Assert.Equal("INV-11", item.PaidInvoiceNumber);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(3_000m, item.PaidAmountTotal);
                Assert.Equal(27.3m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без номера счета", item.PaidInvoiceNumber);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(800m, item.PaidAmountTotal);
                Assert.Equal(9.1m, item.Share);
            });
    }

    [Fact]
    public async Task InvoiceStatuses_GroupsInvoicesByWorkflowStatus()
    {
        await using var db = await CreateContextAsync();
        db.Payments.AddRange(
            InvoicePayment("INV-1", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.Draft, 1_000m, 0m, new DateTime(2026, 5, 1)),
            InvoicePayment("INV-2", PaymentType.Expense, PaymentStatus.Processing, InvoiceWorkflowStatus.OnSigning, 2_000m, 500m, new DateTime(2026, 5, 2)),
            InvoicePayment("INV-3", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 3_000m, 3_000m, new DateTime(2026, 5, 3)),
            InvoicePayment("INV-4", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.PartiallyPaid, 4_000m, 1_500m, new DateTime(2026, 5, 4)),
            InvoicePayment("INV-5", PaymentType.Income, PaymentStatus.Overdue, InvoiceWorkflowStatus.Overdue, 5_000m, 1_000m, new DateTime(2026, 5, 5)),
            InvoicePayment("INV-6", PaymentType.Expense, PaymentStatus.Cancelled, InvoiceWorkflowStatus.Cancelled, 6_000m, 100m, new DateTime(2026, 5, 6)),
            InvoicePayment("INV-7", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.NotSigned, 7_000m, 0m, new DateTime(2026, 5, 7)),
            InvoicePayment("INV-8", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.RequiresClientSignature, 0m, 0m, new DateTime(2026, 5, 8)),
            InvoicePayment("INV-OLD", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 99_999m, 99_999m, new DateTime(2026, 4, 30)),
            InvoicePayment("PAY-1", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 88_888m, 88_888m, new DateTime(2026, 5, 7), DocumentKind.Payment));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.InvoiceStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<InvoiceStatusReportDto>(ok.Value);
        Assert.Equal(8, report.InvoiceCount);
        Assert.Equal(6, report.IncomeCount);
        Assert.Equal(2, report.ExpenseCount);
        Assert.Equal(1, report.PaidCount);
        Assert.Equal(1, report.OverdueCount);
        Assert.Equal(28_000m, report.TotalAmount);
        Assert.Equal(6_100m, report.PaidAmount);
        Assert.Equal(21_900m, report.OutstandingAmount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.RequiresClientSignature, item.Status);
                Assert.Equal("Требуется подпись клиента", item.StatusLabel);
                Assert.Equal(0m, item.TotalAmount);
                Assert.Equal(0m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.OnSigning, item.Status);
                Assert.Equal("На подписании", item.StatusLabel);
                Assert.Equal(2_000m, item.TotalAmount);
                Assert.Equal(500m, item.PaidAmount);
                Assert.Equal(7.1m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.NotSigned, item.Status);
                Assert.Equal("Не подписан", item.StatusLabel);
                Assert.Equal(7_000m, item.TotalAmount);
                Assert.Equal(7_000m, item.OutstandingAmount);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Draft, item.Status);
                Assert.Equal("Черновик", item.StatusLabel);
                Assert.Equal(1_000m, item.TotalAmount);
                Assert.Equal(3.6m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Overdue, item.Status);
                Assert.Equal("Просрочен", item.StatusLabel);
                Assert.Equal(5_000m, item.TotalAmount);
                Assert.Equal(4_000m, item.OutstandingAmount);
                Assert.Equal(17.9m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, item.Status);
                Assert.Equal("Частично оплачен", item.StatusLabel);
                Assert.Equal(4_000m, item.TotalAmount);
                Assert.Equal(2_500m, item.OutstandingAmount);
                Assert.Equal(14.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Paid, item.Status);
                Assert.Equal("Оплачен", item.StatusLabel);
                Assert.Equal(3_000m, item.TotalAmount);
                Assert.Equal(0m, item.OutstandingAmount);
                Assert.Equal(10.7m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Cancelled, item.Status);
                Assert.Equal("Аннулирован", item.StatusLabel);
                Assert.Equal(6_000m, item.TotalAmount);
                Assert.Equal(5_900m, item.OutstandingAmount);
                Assert.Equal(21.4m, item.Share);
            });
    }

    [Fact]
    public async Task ActStatuses_GroupsActsByWorkflowStatus()
    {
        await using var db = await CreateContextAsync();
        db.Acts.AddRange(
            Act("ACT-1", ActStatus.Created, ActWorkflowStatus.Draft, 1_000m, new DateTime(2026, 5, 1)),
            Act("ACT-2", ActStatus.Transferred, ActWorkflowStatus.OnSigning, 2_000m, new DateTime(2026, 5, 2)),
            Act("ACT-3", ActStatus.Signed, ActWorkflowStatus.Signed, 3_000m, new DateTime(2026, 5, 3)),
            Act("ACT-4", ActStatus.Terminated, ActWorkflowStatus.Cancelled, 4_000m, new DateTime(2026, 5, 4)),
            Act("ACT-5", ActStatus.Created, ActWorkflowStatus.NotSigned, 5_000m, new DateTime(2026, 5, 5)),
            Act("ACT-OLD", ActStatus.Signed, ActWorkflowStatus.Signed, 99_999m, new DateTime(2026, 4, 30)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ActStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ActStatusReportDto>(ok.Value);
        Assert.Equal(5, report.ActCount);
        Assert.Equal(2, report.CreatedCount);
        Assert.Equal(1, report.TransferredCount);
        Assert.Equal(1, report.SignedCount);
        Assert.Equal(1, report.TerminatedCount);
        Assert.Equal(15_000m, report.TotalAmount);
        Assert.Equal(3_000m, report.SignedAmount);
        Assert.Equal(12_000m, report.PendingAmount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(ActWorkflowStatus.OnSigning, item.WorkflowStatus);
                Assert.Equal("На подписании", item.WorkflowStatusLabel);
                Assert.Equal(2_000m, item.TotalAmount);
                Assert.Equal(13.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.NotSigned, item.WorkflowStatus);
                Assert.Equal("Не подписан", item.WorkflowStatusLabel);
                Assert.Equal(5_000m, item.TotalAmount);
                Assert.Equal(5_000m, item.PendingAmount);
                Assert.Equal(33.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.Draft, item.WorkflowStatus);
                Assert.Equal("Черновик", item.WorkflowStatusLabel);
                Assert.Equal(1_000m, item.TotalAmount);
                Assert.Equal(6.7m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.Signed, item.WorkflowStatus);
                Assert.Equal("Подписан", item.WorkflowStatusLabel);
                Assert.Equal(3_000m, item.SignedAmount);
                Assert.Equal(20m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.Cancelled, item.WorkflowStatus);
                Assert.Equal("Аннулирован", item.WorkflowStatusLabel);
                Assert.Equal(4_000m, item.PendingAmount);
                Assert.Equal(26.7m, item.Share);
            });
    }

    [Fact]
    public async Task ContractStatuses_GroupsContractsByStatus()
    {
        await using var db = await CreateContextAsync();
        db.Contracts.AddRange(
            Contract("C-1", ContractStatus.Draft, 1_000m, new DateTime(2026, 5, 1), false, new DateTime(2026, 5, 20)),
            Contract("C-2", ContractStatus.OnSigning, 2_000m, new DateTime(2026, 5, 2), false, new DateTime(2026, 6, 20)),
            Contract("C-3", ContractStatus.Concluded, 3_000m, new DateTime(2026, 5, 3), true, null),
            Contract("C-4", ContractStatus.Terminated, 4_000m, new DateTime(2026, 5, 4), false, new DateTime(2026, 5, 25)),
            Contract("C-5", ContractStatus.Cancelled, 5_000m, new DateTime(2026, 5, 5), false, null),
            Contract("C-6", ContractStatus.NotSigned, 6_000m, new DateTime(2026, 5, 6), false, new DateTime(2026, 5, 28)),
            Contract("C-OLD", ContractStatus.Concluded, 99_999m, new DateTime(2026, 4, 30), false, null));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ContractStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ContractStatusReportDto>(ok.Value);
        Assert.Equal(6, report.ContractCount);
        Assert.Equal(1, report.DraftCount);
        Assert.Equal(1, report.NotSignedCount);
        Assert.Equal(1, report.OnSigningCount);
        Assert.Equal(1, report.ConcludedCount);
        Assert.Equal(1, report.TerminatedCount);
        Assert.Equal(1, report.CancelledCount);
        Assert.Equal(1, report.OpenEndedCount);
        Assert.Equal(21_000m, report.TotalAmount);
        Assert.Equal(12_000m, report.ActiveAmount);
        Assert.Equal(9_000m, report.ClosedAmount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(ContractStatus.OnSigning, item.Status);
                Assert.Equal("На подписании", item.StatusLabel);
                Assert.Equal(2_000m, item.TotalAmount);
                Assert.Equal(0, item.ExpiringCount);
                Assert.Equal(9.5m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.NotSigned, item.Status);
                Assert.Equal("Не подписан", item.StatusLabel);
                Assert.Equal(6_000m, item.TotalAmount);
                Assert.Equal(1, item.ExpiringCount);
                Assert.Equal(28.6m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Draft, item.Status);
                Assert.Equal("Черновик", item.StatusLabel);
                Assert.Equal(1_000m, item.TotalAmount);
                Assert.Equal(1, item.ExpiringCount);
                Assert.Equal(4.8m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Concluded, item.Status);
                Assert.Equal("Подписан", item.StatusLabel);
                Assert.Equal(3_000m, item.TotalAmount);
                Assert.Equal(1, item.OpenEndedCount);
                Assert.Equal(14.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Terminated, item.Status);
                Assert.Equal("Расторгнут", item.StatusLabel);
                Assert.Equal(4_000m, item.TotalAmount);
                Assert.Equal(1, item.ExpiringCount);
                Assert.Equal(19.0m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Cancelled, item.Status);
                Assert.Equal("Аннулирован", item.StatusLabel);
                Assert.Equal(5_000m, item.TotalAmount);
                Assert.Equal(23.8m, item.Share);
            });
    }

    [Fact]
    public async Task PaymentOrders_GroupsFactOperationsByPaymentOrderNumber()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 1), "Основной банк", paymentOrderNumber: "PO-1"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paymentOrderNumber: " po-1 "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 3), "Касса", paymentOrderNumber: "PO-2"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paymentOrderNumber: "PO-1"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paymentOrderNumber: "PO-1"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaymentOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaymentOrderReportDto>(ok.Value);
        Assert.Equal(6_500m, report.IncomeTotal);
        Assert.Equal(3_500m, report.ExpenseTotal);
        Assert.Equal(3_000m, report.NetTotal);

        Assert.Collection(
            report.Orders,
            item =>
            {
                Assert.Equal("PO-1", item.PaymentOrderNumber);
                Assert.Equal(6_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(4_000m, item.NetTotal);
                Assert.Equal(8_000m, item.TotalAmount);
                Assert.Equal(80m, item.Share);
            },
            item =>
            {
                Assert.Equal("PO-2", item.PaymentOrderNumber);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_500m, item.Expense);
                Assert.Equal(15m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без номера платежки", item.PaymentOrderNumber);
                Assert.Equal(500m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(5m, item.Share);
            });
    }

    [Fact]
    public async Task PaymentPurposes_GroupsFactOperationsByPurpose()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 1), "Основной банк", purpose: "Оплата услуг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", purpose: " оплата услуг "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 3), "Касса", purpose: "Возврат"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", purpose: "Оплата услуг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", purpose: "Оплата услуг"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaymentPurposes(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaymentPurposeReportDto>(ok.Value);
        Assert.Equal(8_500m, report.IncomeTotal);
        Assert.Equal(3_500m, report.ExpenseTotal);
        Assert.Equal(5_000m, report.NetTotal);

        Assert.Collection(
            report.Purposes,
            item =>
            {
                Assert.Equal("Оплата услуг", item.Purpose);
                Assert.Equal(8_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(6_000m, item.NetTotal);
                Assert.Equal(10_000m, item.TotalAmount);
                Assert.Equal(83.3m, item.Share);
            },
            item =>
            {
                Assert.Equal("Возврат", item.Purpose);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_500m, item.Expense);
                Assert.Equal(12.5m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без назначения", item.Purpose);
                Assert.Equal(500m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(4.2m, item.Share);
            });
    }

    [Fact]
    public async Task MatchStatuses_GroupsFactOperationsByMatchStatus()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 1), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк", matchStatus: MoneyOperationMatchStatus.ClarificationRequired),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 5), "Касса", matchStatus: MoneyOperationMatchStatus.Ignored),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 7), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.MatchStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<MatchStatusReportDto>(ok.Value);
        Assert.Equal(7_000m, report.IncomeTotal);
        Assert.Equal(5_500m, report.ExpenseTotal);
        Assert.Equal(1_500m, report.NetTotal);
        Assert.Equal(5, report.OperationCount);
        Assert.Equal(2, report.MatchedCount);
        Assert.Equal(2, report.NeedsAttentionCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, item.Status);
                Assert.Equal("Требует уточнения", item.StatusLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(24m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.None, item.Status);
                Assert.Equal("Не сверено", item.StatusLabel);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(8m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.Matched, item.Status);
                Assert.Equal("Сверено", item.StatusLabel);
                Assert.Equal(6_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(8_000m, item.TotalAmount);
                Assert.Equal(64m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.Ignored, item.Status);
                Assert.Equal("Исключено", item.StatusLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(500m, item.Expense);
                Assert.Equal(4m, item.Share);
            });
    }

    [Fact]
    public async Task JournalStatuses_GroupsFactOperationsByComputedJournalStatus()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 1), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк", matchStatus: MoneyOperationMatchStatus.ClarificationRequired),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 5), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 7), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.JournalStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<JournalStatusReportDto>(ok.Value);
        Assert.Equal(9_500m, report.IncomeTotal);
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(6_500m, report.NetTotal);
        Assert.Equal(5, report.OperationCount);
        Assert.Equal(3, report.PostedCount);
        Assert.Equal(1, report.ProcessingCount);
        Assert.Equal(1, report.ErrorCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(MoneyOperationJournalStatus.Error, item.Status);
                Assert.Equal("Ошибка", item.StatusLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_000m, item.Expense);
                Assert.Equal(8m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationJournalStatus.Processing, item.Status);
                Assert.Equal("В обработке", item.StatusLabel);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(24m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationJournalStatus.Posted, item.Status);
                Assert.Equal("Проведено", item.StatusLabel);
                Assert.Equal(6_500m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(8_500m, item.TotalAmount);
                Assert.Equal(68m, item.Share);
            });
    }

    [Fact]
    public async Task Currencies_GroupsFactOperationsByCurrency()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 1), "Основной банк", currency: "RUB"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", currency: " rub "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк", currency: "usd"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса", currency: null),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", currency: "USD"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", currency: "RUB"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Currencies(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CurrencyReportDto>(ok.Value);
        Assert.Equal(10_000m, report.IncomeTotal);
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(7_000m, report.NetTotal);

        Assert.Collection(
            report.Currencies,
            item =>
            {
                Assert.Equal("RUB", item.Currency);
                Assert.Equal(7_000m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(4_000m, item.NetTotal);
                Assert.Equal(10_000m, item.TotalAmount);
                Assert.Equal(76.9m, item.Share);
            },
            item =>
            {
                Assert.Equal("USD", item.Currency);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(23.1m, item.Share);
            });
    }

    [Fact]
    public async Task CashOrders_GroupsOrdersByStatus()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            CashOrder("PKO-1", CashOrderType.Incoming, CashOrderStatus.Posted, 5_000m, new DateTime(2026, 5, 1)),
            CashOrder("RKO-1", CashOrderType.Outgoing, CashOrderStatus.Posted, 2_000m, new DateTime(2026, 5, 2)),
            CashOrder("PKO-2", CashOrderType.Incoming, CashOrderStatus.Draft, 1_000m, new DateTime(2026, 5, 3)),
            CashOrder("RKO-2", CashOrderType.Outgoing, CashOrderStatus.Cancelled, 500m, new DateTime(2026, 5, 4)),
            CashOrder("PKO-OLD", CashOrderType.Incoming, CashOrderStatus.Posted, 99_999m, new DateTime(2026, 4, 30)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashOrderReportDto>(ok.Value);
        Assert.Equal(5_000m, report.PostedIncomingTotal);
        Assert.Equal(2_000m, report.PostedOutgoingTotal);
        Assert.Equal(3_000m, report.PostedNetTotal);
        Assert.Equal(4, report.OrderCount);
        Assert.Equal(1, report.DraftCount);
        Assert.Equal(2, report.PostedCount);
        Assert.Equal(1, report.CancelledCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(CashOrderStatus.Draft, item.Status);
                Assert.Equal("Черновик", item.StatusLabel);
                Assert.Equal(1_000m, item.IncomingTotal);
                Assert.Equal(0m, item.OutgoingTotal);
                Assert.Equal(11.8m, item.Share);
            },
            item =>
            {
                Assert.Equal(CashOrderStatus.Posted, item.Status);
                Assert.Equal("Проведен", item.StatusLabel);
                Assert.Equal(5_000m, item.IncomingTotal);
                Assert.Equal(2_000m, item.OutgoingTotal);
                Assert.Equal(7_000m, item.TotalAmount);
                Assert.Equal(82.4m, item.Share);
            },
            item =>
            {
                Assert.Equal(CashOrderStatus.Cancelled, item.Status);
                Assert.Equal("Отменен", item.StatusLabel);
                Assert.Equal(0m, item.IncomingTotal);
                Assert.Equal(500m, item.OutgoingTotal);
                Assert.Equal(5.9m, item.Share);
            });
    }

    [Fact]
    public async Task BankStatementImports_GroupsBatchesByStatus()
    {
        await using var db = await CreateContextAsync();
        db.BankStatementImportBatches.AddRange(
            BankStatementImportBatch("may-ok.txt", BankStatementImportBatchStatus.Applied, 10, 8, 2, 0, 0, new DateTime(2026, 5, 1, 10, 0, 0)),
            BankStatementImportBatch("may-errors.txt", BankStatementImportBatchStatus.AppliedWithErrors, 12, 7, 2, 3, 0, new DateTime(2026, 5, 2, 11, 0, 0)),
            BankStatementImportBatch("may-rolled.txt", BankStatementImportBatchStatus.RolledBack, 5, 5, 0, 0, 5, new DateTime(2026, 5, 3, 12, 0, 0)),
            BankStatementImportBatch("may-blocked.txt", BankStatementImportBatchStatus.RollbackBlocked, 4, 3, 0, 1, 0, new DateTime(2026, 5, 4, 13, 0, 0)),
            BankStatementImportBatch("old.txt", BankStatementImportBatchStatus.Applied, 99, 99, 0, 0, 0, new DateTime(2026, 4, 30, 23, 59, 59)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.BankStatementImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<BankStatementImportReportDto>(ok.Value);
        Assert.Equal(4, report.BatchCount);
        Assert.Equal(31, report.RowsReadTotal);
        Assert.Equal(23, report.CreatedRowsTotal);
        Assert.Equal(4, report.SkippedRowsTotal);
        Assert.Equal(4, report.ErrorRowsTotal);
        Assert.Equal(5, report.RollbackRowsTotal);
        Assert.Equal(1, report.AppliedCount);
        Assert.Equal(1, report.AppliedWithErrorsCount);
        Assert.Equal(1, report.RolledBackCount);
        Assert.Equal(1, report.RollbackBlockedCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.AppliedWithErrors, item.Status);
                Assert.Equal("Применен с ошибками", item.StatusLabel);
                Assert.Equal(12, item.RowsRead);
                Assert.Equal(3, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.RollbackBlocked, item.Status);
                Assert.Equal("Откат заблокирован", item.StatusLabel);
                Assert.Equal(4, item.RowsRead);
                Assert.Equal(1, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.Applied, item.Status);
                Assert.Equal("Применен", item.StatusLabel);
                Assert.Equal(10, item.RowsRead);
                Assert.Equal(0, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.RolledBack, item.Status);
                Assert.Equal("Откатан", item.StatusLabel);
                Assert.Equal(5, item.RollbackRows);
                Assert.Equal(25m, item.Share);
            });
    }

    [Fact]
    public async Task SpreadsheetImports_GroupsBatchesByStatus()
    {
        await using var db = await CreateContextAsync();
        db.SpreadsheetImportBatches.AddRange(
            SpreadsheetImportBatch("May OK", SpreadsheetImportBatchStatus.Applied, 2, 20, 10, 4, 5, 1, 0, new DateTime(2026, 5, 1, 10, 0, 0)),
            SpreadsheetImportBatch("May Errors", SpreadsheetImportBatchStatus.AppliedWithErrors, 1, 12, 5, 2, 2, 3, 0, new DateTime(2026, 5, 2, 11, 0, 0)),
            SpreadsheetImportBatch("May Rolled", SpreadsheetImportBatchStatus.RolledBack, 1, 8, 4, 1, 2, 1, 4, new DateTime(2026, 5, 3, 12, 0, 0)),
            SpreadsheetImportBatch("May Conflicts", SpreadsheetImportBatchStatus.RolledBackWithConflicts, 3, 6, 2, 1, 1, 2, 2, new DateTime(2026, 5, 4, 13, 0, 0)),
            SpreadsheetImportBatch("Old", SpreadsheetImportBatchStatus.Applied, 5, 99, 99, 0, 0, 0, 0, new DateTime(2026, 4, 30, 23, 59, 59)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.SpreadsheetImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<SpreadsheetImportReportDto>(ok.Value);
        Assert.Equal(4, report.BatchCount);
        Assert.Equal(7, report.FileCount);
        Assert.Equal(46, report.TotalRows);
        Assert.Equal(21, report.CreatedRows);
        Assert.Equal(8, report.UpdatedRows);
        Assert.Equal(10, report.SkippedRows);
        Assert.Equal(7, report.ErrorRows);
        Assert.Equal(6, report.RollbackRows);
        Assert.Equal(1, report.AppliedCount);
        Assert.Equal(1, report.AppliedWithErrorsCount);
        Assert.Equal(1, report.RolledBackCount);
        Assert.Equal(1, report.RolledBackWithConflictsCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.AppliedWithErrors, item.Status);
                Assert.Equal("Применен с ошибками", item.StatusLabel);
                Assert.Equal(1, item.FileCount);
                Assert.Equal(12, item.TotalRows);
                Assert.Equal(3, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, item.Status);
                Assert.Equal("Откатан с конфликтами", item.StatusLabel);
                Assert.Equal(3, item.FileCount);
                Assert.Equal(6, item.TotalRows);
                Assert.Equal(2, item.RollbackRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.Applied, item.Status);
                Assert.Equal("Применен", item.StatusLabel);
                Assert.Equal(2, item.FileCount);
                Assert.Equal(20, item.TotalRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, item.Status);
                Assert.Equal("Откатан", item.StatusLabel);
                Assert.Equal(1, item.FileCount);
                Assert.Equal(4, item.RollbackRows);
                Assert.Equal(25m, item.Share);
            });
    }

    private static MoneyOperation Operation(
        MoneyOperationSource source,
        MoneyOperationDirection direction,
        MoneyOperationAccountingType accountingType,
        decimal amount,
        DateTime date,
        string accountName,
        int? incomeTypeId = null,
        string? categoryName = null,
        string? counterpartyName = null,
        string? projectName = null,
        string? directionName = null,
        string? salesPointName = null,
        decimal? vatRate = null,
        decimal? vatAmount = null,
        bool isAdvancePayment = false,
        DateTime? recognitionDate = null,
        int? paymentPriority = null,
        string? paidAccountName = null,
        string? paidInvoiceNumber = null,
        decimal? paidAmount = null,
        string? paymentOrderNumber = null,
        string? purpose = null,
        MoneyOperationMatchStatus matchStatus = MoneyOperationMatchStatus.None,
        string? currency = "RUB")
        => new()
        {
            Source = source,
            Direction = direction,
            AccountingType = accountingType,
            AccountName = accountName,
            CounterpartyName = counterpartyName,
            IncomeTypeId = incomeTypeId,
            CategoryName = categoryName,
            ProjectName = projectName,
            DirectionName = directionName,
            SalesPointName = salesPointName,
            VatRate = vatRate,
            VatAmount = vatAmount,
            Amount = amount,
            Date = date,
            RecognitionDate = recognitionDate,
            IsAdvancePayment = isAdvancePayment,
            PaymentPriority = paymentPriority,
            PaidAccountName = paidAccountName,
            PaidInvoiceNumber = paidInvoiceNumber,
            PaidAmount = paidAmount,
            PaymentOrderNumber = paymentOrderNumber,
            Purpose = purpose,
            MatchStatus = matchStatus,
            Currency = currency,
            CreatedAt = DateTime.UtcNow,
        };

    private static Payment InvoicePayment(
        string account,
        PaymentType type,
        PaymentStatus status,
        string workflowStatus,
        decimal amount,
        decimal paidAmount,
        DateTime date,
        DocumentKind documentKind = DocumentKind.Invoice)
        => new()
        {
            Account = account,
            AccountDate = date,
            Amount = amount,
            PaidAmount = paidAmount,
            Date = date,
            PlannedDate = date,
            Status = status,
            Type = type,
            DocumentKind = documentKind,
            InvoiceWorkflowStatus = workflowStatus,
            IsPaid = status == PaymentStatus.Completed,
            CreatedAt = DateTime.UtcNow,
        };

    private static Act Act(
        string number,
        ActStatus status,
        string workflowStatus,
        decimal amount,
        DateTime date)
        => new()
        {
            Number = number,
            Status = status,
            WorkflowStatus = workflowStatus,
            Amount = amount,
            Date = date,
            CreatedAt = DateTime.UtcNow,
        };

    private static Contract Contract(
        string number,
        string status,
        decimal amount,
        DateTime date,
        bool isOpenEnded,
        DateTime? validUntil)
        => new()
        {
            Number = number,
            Status = status,
            Amount = amount,
            Date = date,
            IsOpenEnded = isOpenEnded,
            ValidUntil = validUntil,
            BillingMode = ContractBillingMode.Fixed,
            CreatedAt = DateTime.UtcNow,
        };

    private static BankStatementImportBatch BankStatementImportBatch(
        string fileName,
        string status,
        int rowsRead,
        int createdRows,
        int skippedRows,
        int errorRows,
        int rollbackRows,
        DateTime createdAt)
        => new()
        {
            FileName = fileName,
            FileHash = $"{fileName}-hash",
            FileSize = rowsRead * 100,
            Status = status,
            RowsRead = rowsRead,
            CreatedRows = createdRows,
            SkippedRows = skippedRows,
            ErrorRows = errorRows,
            RollbackRows = rollbackRows,
            CreatedAt = createdAt,
        };

    private static SpreadsheetImportBatch SpreadsheetImportBatch(
        string title,
        string status,
        int fileCount,
        int totalRows,
        int createdRows,
        int updatedRows,
        int skippedRows,
        int errorRows,
        int rollbackRows,
        DateTime createdAt)
        => new()
        {
            Title = title,
            Status = status,
            TotalRows = totalRows,
            CreatedRows = createdRows,
            UpdatedRows = updatedRows,
            SkippedRows = skippedRows,
            ErrorRows = errorRows,
            RollbackRows = rollbackRows,
            CreatedAt = createdAt,
            Files = Enumerable.Range(1, fileCount)
                .Select(index => new SpreadsheetImportBatchFile
                {
                    FileName = $"{title}-{index}.xlsx",
                    FileHash = $"{title}-{index}-hash",
                    DetectedKind = "mixed",
                    RowCount = Math.Max(1, totalRows / Math.Max(1, fileCount)),
                    CreatedAt = createdAt,
                })
                .ToList(),
        };

    private static CashOrder CashOrder(
        string number,
        CashOrderType type,
        CashOrderStatus status,
        decimal amount,
        DateTime date)
        => new()
        {
            Number = number,
            Type = type,
            Status = status,
            Amount = amount,
            Date = date,
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
        };

    private static ReportsController CreateController(
        PaymentContext db,
        ClaimsPrincipal? user = null,
        IInvoiceDocumentPdfService? pdfService = null)
    {
        var controller = new ReportsController(db, NullLogger<ReportsController>.Instance, pdfService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? CreateAdminPrincipal() },
        };

        return controller;
    }

    private sealed class FakeInvoiceDocumentPdfService : IInvoiceDocumentPdfService
    {
        private readonly byte[] _bytes;

        public FakeInvoiceDocumentPdfService(byte[] bytes)
        {
            _bytes = bytes;
        }

        public string LastHtml { get; private set; } = string.Empty;

        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            LastHtml = html;
            return Task.FromResult(_bytes);
        }
    }

    private static ClaimsPrincipal CreateAdminPrincipal()
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "admin")],
            authenticationType: "Test"));

    private static ClaimsPrincipal CreateUserPrincipal(int userId)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            authenticationType: "Test"));

    private static (string TimeZoneId, DateTime UtcToday, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
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
                return (timeZoneId, utcToday, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC was found.");
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
