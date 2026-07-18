using System.Data.Common;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task ManagementReportExports_ReturnCsvExcelPdfAndPrintFiles()
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
            "cash-flow",
            "cash",
            "bank",
            "plan-fact",
            "advances",
            "payment-priorities",
            "paid-accounts",
            "paid-invoices",
            "invoice-statuses",
            "act-statuses",
            "contract-statuses",
            "categories",
            "counterparties",
            "projects",
            "directions",
            "sales-points",
            "vat",
            "payment-orders",
            "payment-purposes",
            "match-statuses",
            "journal-statuses",
            "currencies",
            "cash-orders",
            "bank-statement-imports",
            "spreadsheet-imports",
            "case-success",
            "current-cases",
            "tasks",
            "lawyer-workload",
            "overdue-deadlines",
            "case-activity",
            "cash-flow-forecast",
        };

        foreach (var reportKind in reportKinds)
        {
            var csvAction = await controller.ExportManagementReportCsv(
                reportKind,
                today.AddDays(-30),
                today.AddDays(30),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
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
            var csv = Assert.IsType<FileContentResult>(csvAction);
            Assert.Equal("text/csv; charset=utf-8", csv.ContentType);
            Assert.EndsWith(".csv", csv.FileDownloadName, StringComparison.Ordinal);
            Assert.Contains(";", System.Text.Encoding.UTF8.GetString(csv.FileContents), StringComparison.Ordinal);

            var printAction = await controller.PrintManagementReport(
                reportKind,
                today.AddDays(-30),
                today.AddDays(30),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
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
            var print = Assert.IsType<ContentResult>(printAction);
            Assert.Equal("text/html; charset=utf-8", print.ContentType);
            Assert.Contains("<!doctype html>", print.Content, StringComparison.OrdinalIgnoreCase);

            var excelAction = await controller.ExportManagementReportExcel(
                reportKind,
                today.AddDays(-30),
                today.AddDays(30),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
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
            "cash-flow-forecast",
            today.AddDays(-30),
            today.AddDays(30),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
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
        Assert.Contains("Прогноз денежных потоков", pdfService.LastHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagementBankExport_UsesCurrentMoneyFilters()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(
                MoneyOperationSource.Bank,
                MoneyOperationDirection.Income,
                MoneyOperationAccountingType.Fact,
                3_000m,
                new DateTime(2026, 4, 25),
                "Target bank",
                counterpartyName: "Acme LLC"),
            Operation(
                MoneyOperationSource.Bank,
                MoneyOperationDirection.Income,
                MoneyOperationAccountingType.Fact,
                7_000m,
                new DateTime(2026, 5, 4),
                "Target bank",
                counterpartyName: "Acme LLC"),
            Operation(
                MoneyOperationSource.Bank,
                MoneyOperationDirection.Income,
                MoneyOperationAccountingType.Fact,
                99_000m,
                new DateTime(2026, 5, 4),
                "Other bank",
                counterpartyName: "Acme LLC"),
            Operation(
                MoneyOperationSource.Bank,
                MoneyOperationDirection.Expense,
                MoneyOperationAccountingType.Fact,
                12_000m,
                new DateTime(2026, 5, 5),
                "Target bank",
                counterpartyName: "Beta LLC"));
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var excelAction = await controller.ExportManagementReportExcel(
            "bank",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            "Target bank",
            "Acme LLC",
            null,
            null,
            null,
            null,
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            50,
            CancellationToken.None);

        var excel = Assert.IsType<FileContentResult>(excelAction);
        var text = System.Text.Encoding.UTF8.GetString(excel.FileContents);
        Assert.Contains("Target bank", text, StringComparison.Ordinal);
        Assert.Contains("10000.00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Other bank", text, StringComparison.Ordinal);
        Assert.DoesNotContain("99000.00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("12000.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagementCashExport_UsesSelectedCashAccountFilter()
    {
        await using var db = await CreateContextAsync();
        var incomingA = CashOrder("PKO-CASH-A", CashOrderType.Incoming, CashOrderStatus.Posted, 5_000m, new DateTime(2026, 5, 1));
        incomingA.Account = "Cashbox A";
        var outgoingA = CashOrder("RKO-CASH-A", CashOrderType.Outgoing, CashOrderStatus.Posted, 2_000m, new DateTime(2026, 5, 2));
        outgoingA.Account = "Cashbox A";
        var incomingB = CashOrder("PKO-CASH-B", CashOrderType.Incoming, CashOrderStatus.Posted, 99_000m, new DateTime(2026, 5, 3));
        incomingB.Account = "Cashbox B";
        db.CashOrders.AddRange(incomingA, outgoingA, incomingB);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var excelAction = await controller.ExportManagementReportExcel(
            "cash",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            "Cashbox A",
            null,
            null,
            null,
            null,
            null,
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            50,
            CancellationToken.None);

        var excel = Assert.IsType<FileContentResult>(excelAction);
        var text = System.Text.Encoding.UTF8.GetString(excel.FileContents);
        Assert.Contains("Касса: Cashbox A", text, StringComparison.Ordinal);
        Assert.Contains("5000.00", text, StringComparison.Ordinal);
        Assert.Contains("2000.00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("99000.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagementReportExcel_UsesProfileTimeZoneForGeneratedAt()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "management-exporter-timezone", Description = "Management exporter timezone" };
        var user = new User
        {
            Email = "management-exporter-timezone@example.test",
            FullName = "Management Exporter Timezone",
            IsActive = true,
            Role = role,
        };
        var client = new Client
        {
            Name = "Management Timezone Client",
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
            });
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });

        var today = DateTime.UtcNow.Date;
        db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "MNG-TZ",
            Title = "Management timezone case",
            CaseType = "Суд",
            Status = ClientCaseStatus.Closed,
            ResponsibleUserId = user.Id,
            CreatedAt = today.AddDays(-3),
            StartDate = today.AddDays(-3),
            DueDate = today,
            FeeAmount = 12_000m,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));
        var expectedTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var before = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

        var excelAction = await controller.ExportManagementReportExcel(
            "case-success",
            today.AddDays(-30),
            today.AddDays(30),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
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

        var after = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var excel = Assert.IsType<FileContentResult>(excelAction);
        var excelText = System.Text.Encoding.UTF8.GetString(excel.FileContents);
        Assert.Contains("Сформировано", excelText, StringComparison.Ordinal);
        Assert.True(
            excelText.Contains(before, StringComparison.Ordinal) ||
            excelText.Contains(after, StringComparison.Ordinal),
            $"Excel export must contain generated time in Europe/Samara. Expected {before} or {after}. Content: {excelText}");
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
        var expectedTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var expectedDate = CalendarTimeZoneHelper
            .ToUserLocal(instantUtc, expectedTimeZone)
            .ToString("dd.MM.yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var beforeCsvGeneratedAt = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

        var csv = Assert.IsType<FileContentResult>(
            await controller.ExportLegalWorkCsv(from, to, null, null, null, CancellationToken.None));
        var afterCsvGeneratedAt = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var csvText = System.Text.Encoding.UTF8.GetString(csv.FileContents);
        Assert.Matches(@"^legal-work-report-\d{8}-\d{4}\.csv$", csv.FileDownloadName);
        Assert.Contains(expectedDate, csvText, StringComparison.Ordinal);
        Assert.Contains("Сформировано", csvText, StringComparison.Ordinal);
        Assert.True(
            csvText.Contains(beforeCsvGeneratedAt, StringComparison.Ordinal) ||
            csvText.Contains(afterCsvGeneratedAt, StringComparison.Ordinal),
            $"CSV export must contain generated time in Europe/Samara. Expected {beforeCsvGeneratedAt} or {afterCsvGeneratedAt}. Content: {csvText}");

        var beforeExcelGeneratedAt = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var excel = Assert.IsType<FileContentResult>(
            await controller.ExportLegalWorkExcel(from, to, null, null, null, CancellationToken.None));
        var afterExcelGeneratedAt = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var excelText = System.Text.Encoding.UTF8.GetString(excel.FileContents);
        Assert.Contains(expectedDate, excelText, StringComparison.Ordinal);
        Assert.Contains("Сформировано", excelText, StringComparison.Ordinal);
        Assert.True(
            excelText.Contains(beforeExcelGeneratedAt, StringComparison.Ordinal) ||
            excelText.Contains(afterExcelGeneratedAt, StringComparison.Ordinal),
            $"Excel export must contain generated time in Europe/Samara. Expected {beforeExcelGeneratedAt} or {afterExcelGeneratedAt}. Content: {excelText}");

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
        Assert.IsType<ForbidResult>(await controller.ExportManagementReportExcel(
            "case-finance",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.ExportManagementReportCsv(
            "case-finance",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.PrintManagementReport(
            "case-finance",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.ExportManagementReportPdf(
            "case-finance",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("cash-flow")]
    [InlineData("cash-flow-forecast")]
    [InlineData("categories")]
    [InlineData("paid-invoices")]
    public async Task PaymentBackedManagementExports_RequirePaymentsExportPermission(string reportKind)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: false);
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.ExportManagementReportExcel(
            reportKind,
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task CashFlowManagementExports_ApplyPaymentsVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: true,
            paymentsViewScope: DataVisibilityScope.Own);
        var hiddenUser = User("cash-flow-export-hidden-user");
        db.Users.Add(hiddenUser);
        await db.SaveChangesAsync();

        var visibleClient = new Client { Name = "Visible export client", ResponsibleUserId = user.Id };
        var hiddenClient = new Client { Name = "Hidden export client", ResponsibleUserId = hiddenUser.Id };
        var visibleInvoice = new Invoice { Number = "CF-VISIBLE", Date = new DateTime(2026, 5, 10) };
        var hiddenInvoice = new Invoice { Number = "CF-HIDDEN", Date = new DateTime(2026, 5, 11) };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        await db.SaveChangesAsync();

        var visiblePayment = InvoicePayment(
            "Main bank",
            PaymentType.Income,
            PaymentStatus.Completed,
            InvoiceWorkflowStatus.Paid,
            1_000m,
            1_000m,
            new DateTime(2026, 5, 10));
        visiblePayment.InvoiceId = visibleInvoice.Id;
        visiblePayment.ClientId = visibleClient.Id;
        var hiddenPayment = InvoicePayment(
            "Main bank",
            PaymentType.Income,
            PaymentStatus.Completed,
            InvoiceWorkflowStatus.Paid,
            9_000m,
            9_000m,
            new DateTime(2026, 5, 11));
        hiddenPayment.InvoiceId = hiddenInvoice.Id;
        hiddenPayment.ClientId = hiddenClient.Id;
        db.Payments.AddRange(visiblePayment, hiddenPayment);
        db.MoneyOperations.AddRange(
            Operation(
                MoneyOperationSource.Bank,
                MoneyOperationDirection.Income,
                MoneyOperationAccountingType.Fact,
                1_000m,
                new DateTime(2026, 5, 10),
                "Main bank",
                counterpartyName: "Visible export client",
                linkedInvoiceId: visibleInvoice.Id),
            Operation(
                MoneyOperationSource.Bank,
                MoneyOperationDirection.Income,
                MoneyOperationAccountingType.Fact,
                9_000m,
                new DateTime(2026, 5, 11),
                "Main bank",
                counterpartyName: "Hidden export client",
                linkedInvoiceId: hiddenInvoice.Id));
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var csvAction = await controller.ExportManagementReportCsv(
            "cash-flow",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);
        var printAction = await controller.PrintManagementReport(
            "cash-flow",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var csv = Assert.IsType<FileContentResult>(csvAction);
        var csvText = Encoding.UTF8.GetString(csv.FileContents);
        Assert.Contains("1000.00", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("9000.00", csvText, StringComparison.Ordinal);

        var print = Assert.IsType<ContentResult>(printAction);
        Assert.Contains("1000.00", print.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("9000.00", print.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TasksManagementExport_RequiresTasksExportPermission()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"reports-tasks-{Guid.NewGuid():N}",
            Description = "Reports tasks export test role",
        };
        var user = new User
        {
            Email = $"reports-tasks-{Guid.NewGuid():N}@example.test",
            FullName = "Reports tasks export user",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        db.RolePermissions.AddRange(
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                CanExport = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                CanExport = false,
                ViewScope = DataVisibilityScope.All,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.ExportManagementReportExcel(
            "tasks",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new DateTime(2026, 5, 26),
            "all",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            50,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task TasksManagementExports_ApplyTasksVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"reports-tasks-scope-{Guid.NewGuid():N}",
            Description = "Reports tasks visibility export test role",
        };
        var user = new User
        {
            Email = $"reports-tasks-scope-{Guid.NewGuid():N}@example.test",
            FullName = "Visible task export user",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = User("tasks-export-hidden-user");
        db.Roles.Add(role);
        db.Users.AddRange(user, hiddenUser);
        db.RolePermissions.AddRange(
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                CanExport = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                CanExport = true,
                ViewScope = DataVisibilityScope.Own,
            });
        await db.SaveChangesAsync();

        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                Title = "VISIBLE-SCOPE-TASK",
                ObjectName = "Visible export object",
                Status = "new",
                Type = "task",
                Priority = "normal",
                AssigneeUserId = user.Id,
                AssigneeName = user.FullName,
                AuthorUserId = hiddenUser.Id,
                AuthorName = hiddenUser.FullName,
                DueAt = new DateTime(2026, 5, 26, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
                Result = "Visible result",
            },
            new TaskJournalItem
            {
                Title = "HIDDEN-SCOPE-TASK",
                ObjectName = "Hidden export object",
                Status = "new",
                Type = "task",
                Priority = "normal",
                AssigneeUserId = hiddenUser.Id,
                AssigneeName = hiddenUser.FullName,
                AuthorUserId = hiddenUser.Id,
                AuthorName = hiddenUser.FullName,
                DueAt = new DateTime(2026, 5, 26, 11, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 20, 11, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 5, 20, 11, 0, 0, DateTimeKind.Utc),
                Result = "Hidden result",
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var csvAction = await controller.ExportManagementReportCsv(
            "tasks",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new DateTime(2026, 5, 26),
            "all",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            50,
            CancellationToken.None);
        var printAction = await controller.PrintManagementReport(
            "tasks",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new DateTime(2026, 5, 26),
            "all",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            50,
            CancellationToken.None);

        var csv = Assert.IsType<FileContentResult>(csvAction);
        var csvText = Encoding.UTF8.GetString(csv.FileContents);
        Assert.Contains("VISIBLE-SCOPE-TASK", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN-SCOPE-TASK", csvText, StringComparison.Ordinal);

        var print = Assert.IsType<ContentResult>(printAction);
        Assert.Contains("VISIBLE-SCOPE-TASK", print.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN-SCOPE-TASK", print.Content, StringComparison.Ordinal);
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
    public async Task CaseActivity_HandlesLargeDatasetWithLimitedRows()
    {
        await using var db = await CreateContextAsync();
        var now = DateTime.UtcNow;
        var client = new Client
        {
            Name = "Large Activity Client",
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var cases = Enumerable.Range(1, 650)
            .Select(index => new ClientCase
            {
                ClientId = client.Id,
                CaseNumber = $"LARGE-{index:000}",
                Title = $"Large activity case {index:000}",
                Status = index % 13 == 0 ? ClientCaseStatus.Closed : ClientCaseStatus.Open,
                CreatedAt = now.AddDays(-60),
                LastViewedAt = now.AddDays(-60),
                ActivityConfirmedAt = now.AddDays(-60),
                InactivityRemindersEnabled = true,
            })
            .ToList();
        db.ClientCases.AddRange(cases);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var action = await controller.CaseActivity(14, 30, "inactive", 25, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CaseActivityReportDto>(ok.Value);
        Assert.Equal(650, report.TotalVisibleCases);
        Assert.Equal(600, report.ActiveCaseCount);
        Assert.Equal(50, report.FinishedCaseCount);
        Assert.Equal(600, report.InactiveCaseCount);
        Assert.Equal(600, report.EscalatedCaseCount);
        Assert.Equal(25, report.Items.Count);
        Assert.All(report.Items, item => Assert.True(item.IsInactive));

        var printAction = await controller.PrintManagementReport(
            "case-activity",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            now.Date,
            null,
            null,
            null,
            null,
            null,
            null,
            "inactive",
            14,
            30,
            25,
            CancellationToken.None);
        var print = Assert.IsType<ContentResult>(printAction);
        Assert.Equal("text/html; charset=utf-8", print.ContentType);
        Assert.Contains("Large activity case", print.Content);
        Assert.Contains("LARGE-", print.Content);
    }

    [Fact]
    public async Task CaseActivityManagementExports_ApplyCasesVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"case-activity-export-role-{Guid.NewGuid():N}",
            Description = "Case activity export role",
        };
        var visibleUser = new User
        {
            Email = $"visible-case-activity-export-{Guid.NewGuid():N}@example.test",
            FullName = "Visible Case Activity Export User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = $"hidden-case-activity-export-{Guid.NewGuid():N}@example.test",
            FullName = "Hidden Case Activity Export User",
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
                CanExport = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var visibleClient = new Client { Name = "Visible activity export client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden activity export client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        db.ClientCases.AddRange(
            new ClientCase
            {
                ClientId = visibleClient.Id,
                CaseNumber = "ACTIVITY-EXPORT-VISIBLE",
                Title = "Visible activity export case",
                Status = ClientCaseStatus.Open,
                CreatedAt = DateTime.UtcNow.AddDays(-40),
            },
            new ClientCase
            {
                ClientId = hiddenClient.Id,
                CaseNumber = "ACTIVITY-EXPORT-HIDDEN",
                Title = "Hidden activity export case",
                Status = ClientCaseStatus.Open,
                CreatedAt = DateTime.UtcNow.AddDays(-40),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var csvAction = await controller.ExportManagementReportCsv(
            "case-activity",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "all",
            14,
            30,
            10,
            CancellationToken.None);
        var printAction = await controller.PrintManagementReport(
            "case-activity",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "all",
            14,
            30,
            10,
            CancellationToken.None);

        var csv = Assert.IsType<FileContentResult>(csvAction);
        var csvText = Encoding.UTF8.GetString(csv.FileContents);
        Assert.Contains("ACTIVITY-EXPORT-VISIBLE", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIVITY-EXPORT-HIDDEN", csvText, StringComparison.Ordinal);

        var print = Assert.IsType<ContentResult>(printAction);
        Assert.Contains("ACTIVITY-EXPORT-VISIBLE", print.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIVITY-EXPORT-HIDDEN", print.Content, StringComparison.Ordinal);
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
    public async Task TasksReport_ProjectsOnlyRequiredColumnsAndKeepsParticipantAssigneeFallback()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        var role = new Role { Name = "task-report-projection-role", Description = "Task reporter" };
        var user = new User
        {
            Email = "task-report-projection@example.test",
            FullName = "Task Report Projection User",
            IsActive = true,
            Role = role,
        };
        var client = new Client { Name = "Projection client" };
        var legalEntity = new LegalEntity { ShortName = "Projection entity" };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Projection case",
            Status = ClientCaseStatus.Open,
            CreatedAt = new DateTime(2026, 5, 1),
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        db.Clients.Add(client);
        db.LegalEntities.Add(legalEntity);
        db.ClientCases.Add(clientCase);
        db.RolePermissions.AddRange(
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Reports,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });
        var task = new TaskJournalItem
        {
            Client = client,
            LegalEntity = legalEntity,
            ClientCase = clientCase,
            ObjectName = "Projection object",
            Title = "Projection task",
            Description = new string('x', 4_000),
            AssigneeName = string.Empty,
            AuthorName = "Projection author",
            DueAt = new DateTime(2026, 5, 20, 9, 0, 0),
            Status = "new",
            Priority = "high",
            Type = "task",
            RemindersJson = "[{\"offsetMinutes\":60}]",
            ChannelsJson = "[\"inApp\"]",
            TagsJson = "[\"projection\"]",
            CreatedAt = new DateTime(2026, 5, 10, 9, 0, 0),
            Participants =
            [
                new TaskJournalParticipant
                {
                    Role = "ASSIGNEE",
                    DisplayName = "  Participant assignee  ",
                    Email = "participant@example.test",
                    Resolution = new string('y', 4_000),
                },
            ],
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));
        var action = await controller.Tasks(
            new DateTime(2026, 5, 21),
            "all",
            null,
            50,
            CancellationToken.None);

        var report = Assert.IsType<TaskReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        var item = Assert.Single(report.Items);
        Assert.Equal("Projection client", item.ClientName);
        Assert.Equal("Projection entity", item.LegalEntityName);
        Assert.Equal("Projection case", item.ClientCaseTitle);
        Assert.Equal("Participant assignee", item.AssigneeName);

        var taskQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal));
        Assert.Contains("TaskJournalParticipants", taskQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Description\"", taskQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("\"RemindersJson\"", taskQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ChannelsJson\"", taskQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TagsJson\"", taskQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Resolution\"", taskQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Email\"", taskQuery, StringComparison.Ordinal);
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
    public async Task LawyerWorkloadManagementExports_ApplyCasesAndTasksVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"lawyer-workload-export-role-{Guid.NewGuid():N}",
            Description = "Lawyer workload export role",
        };
        var visibleUser = new User
        {
            Email = $"visible-workload-export-{Guid.NewGuid():N}@example.test",
            FullName = "Visible Workload Export User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = $"hidden-workload-export-{Guid.NewGuid():N}@example.test",
            FullName = "Hidden Workload Export User",
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
                CanExport = true,
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

        var visibleClient = new Client { Name = "Visible workload export client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden workload export client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var asOf = new DateTime(2026, 5, 26);
        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "WORKLOAD-EXPORT-VISIBLE",
            Title = "Visible workload export case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = asOf.AddDays(-20),
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "WORKLOAD-EXPORT-HIDDEN",
            Title = "Hidden workload export case",
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
                Title = "Visible workload export task",
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
                ClientCaseId = hiddenCase.Id,
                ObjectName = hiddenCase.Title,
                Title = "Hidden workload export task",
                AssigneeUserId = hiddenUser.Id,
                AssigneeName = hiddenUser.FullName,
                DueAt = asOf.AddDays(-1).AddHours(9),
                Status = "new",
                Priority = "high",
                Type = "task",
                PlannedDurationMinutes = 600,
                CreatedAt = asOf.AddDays(-3),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var csvAction = await controller.ExportManagementReportCsv(
            "lawyer-workload",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            asOf,
            null,
            null,
            null,
            null,
            7,
            null,
            null,
            null,
            null,
            50,
            CancellationToken.None);
        var printAction = await controller.PrintManagementReport(
            "lawyer-workload",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            asOf,
            null,
            null,
            null,
            null,
            7,
            null,
            null,
            null,
            null,
            50,
            CancellationToken.None);

        var csv = Assert.IsType<FileContentResult>(csvAction);
        var csvText = Encoding.UTF8.GetString(csv.FileContents);
        Assert.Contains("Visible Workload Export User", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden Workload Export User", csvText, StringComparison.Ordinal);

        var print = Assert.IsType<ContentResult>(printAction);
        Assert.Contains("Visible Workload Export User", print.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden Workload Export User", print.Content, StringComparison.Ordinal);
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
    public async Task OverdueDeadlinesManagementExports_ApplyCasesVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"deadline-export-role-{Guid.NewGuid():N}",
            Description = "Deadline export role",
        };
        var visibleUser = new User
        {
            Email = $"visible-deadline-export-{Guid.NewGuid():N}@example.test",
            FullName = "Visible Deadline Export User",
            IsActive = true,
            Role = role,
        };
        var hiddenUser = new User
        {
            Email = $"hidden-deadline-export-{Guid.NewGuid():N}@example.test",
            FullName = "Hidden Deadline Export User",
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
                CanExport = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var visibleClient = new Client { Name = "Visible deadline export client", ResponsibleUserId = visibleUser.Id };
        var hiddenClient = new Client { Name = "Hidden deadline export client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var asOf = new DateTime(2026, 5, 26);
        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            CaseNumber = "DEADLINE-EXPORT-VISIBLE",
            Title = "Visible deadline export case",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = visibleUser.Id,
            CreatedAt = asOf.AddDays(-30),
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            CaseNumber = "DEADLINE-EXPORT-HIDDEN",
            Title = "Hidden deadline export case",
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
                Title = "VISIBLE-DEADLINE-EXPORT",
                DueAt = asOf.AddDays(-8),
                Status = "active",
                CreatedAt = asOf.AddDays(-20),
            },
            new ClientCaseProceduralDeadline
            {
                ClientCaseId = hiddenCase.Id,
                DeadlineType = "appeal",
                Title = "HIDDEN-DEADLINE-EXPORT",
                DueAt = asOf.AddDays(-12),
                Status = "active",
                CreatedAt = asOf.AddDays(-20),
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(visibleUser.Id));

        var csvAction = await controller.ExportManagementReportCsv(
            "overdue-deadlines",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            asOf,
            null,
            null,
            null,
            null,
            null,
            "appeal",
            null,
            null,
            null,
            20,
            CancellationToken.None);
        var printAction = await controller.PrintManagementReport(
            "overdue-deadlines",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            asOf,
            null,
            null,
            null,
            null,
            null,
            "appeal",
            null,
            null,
            null,
            20,
            CancellationToken.None);

        var csv = Assert.IsType<FileContentResult>(csvAction);
        var csvText = Encoding.UTF8.GetString(csv.FileContents);
        Assert.Contains("VISIBLE-DEADLINE-EXPORT", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN-DEADLINE-EXPORT", csvText, StringComparison.Ordinal);

        var print = Assert.IsType<ContentResult>(printAction);
        Assert.Contains("VISIBLE-DEADLINE-EXPORT", print.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN-DEADLINE-EXPORT", print.Content, StringComparison.Ordinal);
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
        var visibleExpense = new Payment
        {
            ExpenseClientId = visibleClient.Id,
            ClientCaseId = visibleCase.Id,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Completed,
            Amount = 3_000m,
            PaidAmount = 3_000m,
            Date = today,
            PlannedDate = today,
            CreatedAt = today,
        };
        visibleExpense.Responsibles.Add(new PaymentResponsible { UserId = visibleUser.Id });
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
        db.Payments.AddRange(visiblePayment, visibleExpense, hiddenPayment);
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
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(18_000m, report.OutstandingTotal);
        Assert.Equal(9_000m, report.MarginTotal);
        Assert.Equal(40m, report.CollectionRate);
        Assert.Equal(75m, report.MarginRate);
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
        Assert.Equal(3_000m, item.ExpenseTotal);
        Assert.Equal(18_000m, item.OutstandingTotal);
        Assert.Equal(9_000m, item.MarginTotal);
        Assert.Equal(75m, item.MarginRate);

        var status = Assert.Single(report.ByStatus);
        Assert.Equal(30_000m, status.FeeTotal);
        Assert.Equal(12_000m, status.PaidTotal);
        Assert.Equal(3_000m, status.ExpenseTotal);
        Assert.Equal(18_000m, status.OutstandingTotal);
        Assert.Equal(9_000m, status.MarginTotal);
        Assert.Equal(75m, status.MarginRate);

        var responsible = Assert.Single(report.ByResponsible);
        Assert.Equal(visibleUser.Id, responsible.ResponsibleUserId);
        Assert.Equal(3_000m, responsible.ExpenseTotal);
        Assert.Equal(18_000m, responsible.OutstandingTotal);
        Assert.Equal(9_000m, responsible.MarginTotal);
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
    public async Task ClientCaseReports_UseProfileTimeZoneForPeriodsAndKeepCaseDatesDateOnly()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithTimeZoneAsync(db, "America/New_York");
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = user.RoleId,
                Section = RolePermissionCatalog.Accounts,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });

        var client = new Client { Name = "Timezone Client", ResponsibleUserId = user.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var createdAtUtc = new DateTime(2026, 6, 6, 2, 30, 0, DateTimeKind.Utc);
        var dueDateWithUtcKind = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            CaseNumber = "TZ-CLIENT",
            Title = "Timezone client case",
            CaseType = "Timezone",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = user.Id,
            CreatedAt = createdAtUtc,
            StartDate = null,
            DueDate = dueDateWithUtcKind,
            FeeAmount = 10_000m,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));
        var localReportDay = new DateTime(2026, 6, 5);
        var dueDate = new DateTime(2026, 6, 10);

        var legalWork = Assert.IsType<LegalWorkReportDto>(
            Assert.IsType<OkObjectResult>((await controller.LegalWork(localReportDay, localReportDay, null, null, null, CancellationToken.None)).Result).Value);
        var legalWorkCase = Assert.Single(legalWork.Cases);
        Assert.Equal(clientCase.Id, legalWorkCase.CaseId);
        Assert.Equal(DateTimeKind.Unspecified, legalWork.From.Kind);
        Assert.Equal(DateTimeKind.Unspecified, legalWork.To.Kind);
        Assert.Equal(dueDate, legalWorkCase.DueDate);
        Assert.Equal(DateTimeKind.Unspecified, legalWorkCase.DueDate!.Value.Kind);

        var caseFinance = Assert.IsType<CaseFinanceReportDto>(
            Assert.IsType<OkObjectResult>((await controller.CaseFinance(localReportDay, localReportDay, null, null, null, 10, CancellationToken.None)).Result).Value);
        var financeItem = Assert.Single(caseFinance.Items);
        Assert.Equal(clientCase.Id, financeItem.CaseId);
        Assert.Equal(dueDate, financeItem.DueDate);
        Assert.Equal(DateTimeKind.Unspecified, financeItem.DueDate!.Value.Kind);

        var caseSuccess = Assert.IsType<CaseSuccessReportDto>(
            Assert.IsType<OkObjectResult>((await controller.CaseSuccess(localReportDay, localReportDay, null, null, null, 10, CancellationToken.None)).Result).Value);
        var successItem = Assert.Single(caseSuccess.Items);
        var expectedProfileToday = CalendarTimeZoneHelper.ToUserLocal(
            DateTime.UtcNow,
            CalendarTimeZoneHelper.ResolveTimeZone("America/New_York")).Date;
        Assert.Equal(clientCase.Id, successItem.CaseId);
        Assert.Equal(dueDate, successItem.DueDate);
        Assert.Equal(DateTimeKind.Unspecified, successItem.DueDate!.Value.Kind);
        Assert.Equal(Math.Max(0, (expectedProfileToday - localReportDay).Days), successItem.DaysInWork);

        var currentCases = Assert.IsType<CurrentCasesReportDto>(
            Assert.IsType<OkObjectResult>((await controller.CurrentCases(new DateTime(2026, 6, 7), null, null, null, 10, CancellationToken.None)).Result).Value);
        var currentCase = Assert.Single(currentCases.Items);
        Assert.Equal(clientCase.Id, currentCase.CaseId);
        Assert.Equal(dueDate, currentCase.DueDate);
        Assert.Equal(DateTimeKind.Unspecified, currentCase.DueDate!.Value.Kind);
        Assert.Equal(2, currentCase.DaysInWork);
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
            null,
            null,
            null,
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
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task ReportViewEndpoints_ReturnForbidWhenReportsAreHiddenForRole()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "reports-hidden",
            Description = "Reports hidden",
        };
        var user = new User
        {
            Email = "reports-hidden@example.test",
            FullName = "Reports Hidden User",
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

        var legalWork = await controller.LegalWork(null, null, null, null, null, CancellationToken.None);
        var caseFinance = await controller.CaseFinance(null, null, null, null, null, null, CancellationToken.None);
        var currentCases = await controller.CurrentCases(null, null, null, null, null, CancellationToken.None);
        var taskReport = await controller.Tasks(null, null, null, null, CancellationToken.None);
        var cashFlowForecast = await controller.CashFlowForecast(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(legalWork.Result);
        Assert.IsType<ForbidResult>(caseFinance.Result);
        Assert.IsType<ForbidResult>(currentCases.Result);
        Assert.IsType<ForbidResult>(taskReport.Result);
        Assert.IsType<ForbidResult>(cashFlowForecast.Result);
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
            null,
            null,
            null,
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
    public async Task CashFlowForecast_UsesOpenPaymentRemaindersAndOpeningBalance()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 4, 25), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 4, 28), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 99_999m, new DateTime(2026, 5, 1), "Другой счет"));
        db.Payments.AddRange(
            new Payment
            {
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 12_000m,
                PaidAmount = 2_000m,
                Account = "Основной банк",
                Date = new DateTime(2026, 5, 5),
                PlannedDate = new DateTime(2026, 5, 5),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Type = PaymentType.Expense,
                Status = PaymentStatus.Processing,
                Amount = 5_000m,
                PaidAmount = 1_000m,
                Account = "Основной банк",
                Date = new DateTime(2026, 5, 6),
                PlannedDate = new DateTime(2026, 5, 6),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 20_000m,
                PaidAmount = 20_000m,
                Account = "Основной банк",
                Date = new DateTime(2026, 5, 7),
                PlannedDate = new DateTime(2026, 5, 7),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Type = PaymentType.Expense,
                Status = PaymentStatus.Cancelled,
                Amount = 8_000m,
                PaidAmount = 0m,
                Account = "Основной банк",
                Date = new DateTime(2026, 5, 8),
                PlannedDate = new DateTime(2026, 5, 8),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlowForecast(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            "Основной банк",
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowForecastReportDto>(ok.Value);
        Assert.Equal(new DateTime(2026, 5, 1), report.From);
        Assert.Equal(new DateTime(2026, 5, 31), report.To);
        Assert.Equal("Основной банк", report.AccountName);
        Assert.Equal(8_000m, report.OpeningBalance);
        Assert.Equal(10_000m, report.ExpectedIncome);
        Assert.Equal(4_000m, report.ExpectedExpense);
        Assert.Equal(6_000m, report.NetProjectedFlow);
        Assert.Equal(14_000m, report.ProjectedClosingBalance);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(1, report.ExpenseCount);

        Assert.Collection(
            report.Days,
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 5), day.Date);
                Assert.Equal(10_000m, day.ExpectedIncome);
                Assert.Equal(0m, day.ExpectedExpense);
                Assert.Equal(18_000m, day.ProjectedClosingBalance);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 6), day.Date);
                Assert.Equal(0m, day.ExpectedIncome);
                Assert.Equal(4_000m, day.ExpectedExpense);
                Assert.Equal(14_000m, day.ProjectedClosingBalance);
            });
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
            null,
            null,
            null,
            null,
            null,
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
    public async Task CashFlow_FiltersByClientIdUsingCounterpartyName()
    {
        await using var db = await CreateContextAsync();
        var targetClient = new Client
        {
            Name = "Acme",
            Company = "Acme LLC",
            CreatedAt = DateTime.UtcNow,
        };
        var otherClient = new Client
        {
            Name = "Beta",
            Company = "Beta LLC",
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.AddRange(targetClient, otherClient);
        await db.SaveChangesAsync();

        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 4, 25), "Main bank", counterpartyName: "Acme LLC"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 4), "Main bank", counterpartyName: "Acme LLC"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 5), "Main bank", counterpartyName: "Beta LLC"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 6), "Cash", counterpartyName: "Acme LLC"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            targetClient.Id,
            null,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(3_000m, report.OpeningBalance);
        Assert.Equal(12_000m, report.Income);
        Assert.Equal(0m, report.Expense);
        Assert.Equal(15_000m, report.ClosingBalance);
        Assert.Equal(2, report.IncomeCount);
        Assert.Equal(0, report.ExpenseCount);
    }

    [Fact]
    public async Task CashFlow_FiltersByCaseIdUsingLinkedInvoicePayments()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Case client",
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var targetCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Target case",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        var linkedCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Linked target case",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        var otherCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Other case",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.AddRange(targetCase, linkedCase, otherCase);
        await db.SaveChangesAsync();

        var targetInvoice = new Invoice { Number = "CASE-1", Date = new DateTime(2026, 5, 1) };
        var linkedInvoice = new Invoice { Number = "CASE-2", Date = new DateTime(2026, 5, 2) };
        var otherInvoice = new Invoice { Number = "CASE-3", Date = new DateTime(2026, 5, 3) };
        db.Invoices.AddRange(targetInvoice, linkedInvoice, otherInvoice);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                InvoiceId = targetInvoice.Id,
                ClientId = client.Id,
                ClientCaseId = targetCase.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 10_000m,
                PaidAmount = 10_000m,
                Date = new DateTime(2026, 5, 1),
                PlannedDate = new DateTime(2026, 5, 1),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                InvoiceId = linkedInvoice.Id,
                ClientId = client.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 2_000m,
                PaidAmount = 2_000m,
                Date = new DateTime(2026, 5, 2),
                PlannedDate = new DateTime(2026, 5, 2),
                CreatedAt = DateTime.UtcNow,
                InvoiceClientCases = new List<InvoiceClientCase>
                {
                    new() { ClientCaseId = targetCase.Id },
                },
            },
            new Payment
            {
                InvoiceId = otherInvoice.Id,
                ClientId = client.Id,
                ClientCaseId = otherCase.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 5_000m,
                PaidAmount = 5_000m,
                Date = new DateTime(2026, 5, 3),
                PlannedDate = new DateTime(2026, 5, 3),
                CreatedAt = DateTime.UtcNow,
            });

        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 4, 25), "Main bank", linkedInvoiceId: targetInvoice.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 4), "Main bank", linkedInvoiceId: targetInvoice.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 5), "Main bank", linkedInvoiceId: linkedInvoice.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 6), "Main bank", linkedInvoiceId: otherInvoice.Id),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 9_000m, new DateTime(2026, 5, 7), "Cash"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            targetCase.Id,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(3_000m, report.OpeningBalance);
        Assert.Equal(9_000m, report.Income);
        Assert.Equal(0m, report.Expense);
        Assert.Equal(12_000m, report.ClosingBalance);
        Assert.Equal(2, report.IncomeCount);
        Assert.Equal(0, report.ExpenseCount);
    }

    [Fact]
    public async Task CashFlowForecast_FiltersByCaseIdUsingPaymentCaseLinks()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Forecast client",
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var targetCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Forecast target case",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        var otherCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Forecast other case",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.AddRange(targetCase, otherCase);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                ClientId = client.Id,
                ClientCaseId = targetCase.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 12_000m,
                PaidAmount = 2_000m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 5),
                PlannedDate = new DateTime(2026, 5, 5),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                ClientId = client.Id,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Processing,
                Amount = 5_000m,
                PaidAmount = 1_000m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 6),
                PlannedDate = new DateTime(2026, 5, 6),
                CreatedAt = DateTime.UtcNow,
                InvoiceClientCases = new List<InvoiceClientCase>
                {
                    new() { ClientCaseId = targetCase.Id },
                },
            },
            new Payment
            {
                ClientId = client.Id,
                ClientCaseId = otherCase.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 20_000m,
                PaidAmount = 0m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 7),
                PlannedDate = new DateTime(2026, 5, 7),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlowForecast(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            "Main bank",
            null,
            null,
            targetCase.Id,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowForecastReportDto>(ok.Value);
        Assert.Equal(10_000m, report.ExpectedIncome);
        Assert.Equal(4_000m, report.ExpectedExpense);
        Assert.Equal(6_000m, report.NetProjectedFlow);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(1, report.ExpenseCount);
    }

    [Fact]
    public async Task CashFlow_FiltersByContractIdUsingLinkedInvoicePayments()
    {
        await using var db = await CreateContextAsync();
        var targetContract = new Contract
        {
            Number = "C-1",
            Date = new DateTime(2026, 5, 1),
            BillingMode = ContractBillingMode.Fixed,
            CreatedAt = DateTime.UtcNow,
        };
        var otherContract = new Contract
        {
            Number = "C-2",
            Date = new DateTime(2026, 5, 1),
            BillingMode = ContractBillingMode.Fixed,
            CreatedAt = DateTime.UtcNow,
        };
        db.Contracts.AddRange(targetContract, otherContract);
        await db.SaveChangesAsync();

        var targetInvoice = new Invoice { Number = "INV-C1", Date = new DateTime(2026, 5, 2) };
        var otherInvoice = new Invoice { Number = "INV-C2", Date = new DateTime(2026, 5, 3) };
        db.Invoices.AddRange(targetInvoice, otherInvoice);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                InvoiceId = targetInvoice.Id,
                ContractId = targetContract.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 10_000m,
                PaidAmount = 10_000m,
                Date = new DateTime(2026, 5, 2),
                PlannedDate = new DateTime(2026, 5, 2),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                InvoiceId = otherInvoice.Id,
                ContractId = otherContract.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 20_000m,
                PaidAmount = 20_000m,
                Date = new DateTime(2026, 5, 3),
                PlannedDate = new DateTime(2026, 5, 3),
                CreatedAt = DateTime.UtcNow,
            });

        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 4, 25), "Main bank", linkedInvoiceId: targetInvoice.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 5), "Main bank", linkedInvoiceId: targetInvoice.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 20_000m, new DateTime(2026, 5, 6), "Main bank", linkedInvoiceId: otherInvoice.Id));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            targetContract.Id,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(2_000m, report.OpeningBalance);
        Assert.Equal(8_000m, report.Income);
        Assert.Equal(0m, report.Expense);
        Assert.Equal(10_000m, report.ClosingBalance);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(0, report.ExpenseCount);
    }

    [Fact]
    public async Task CashFlowForecast_FiltersByContractId()
    {
        await using var db = await CreateContextAsync();
        var targetContract = new Contract
        {
            Number = "C-1",
            Date = new DateTime(2026, 5, 1),
            BillingMode = ContractBillingMode.Fixed,
            CreatedAt = DateTime.UtcNow,
        };
        var otherContract = new Contract
        {
            Number = "C-2",
            Date = new DateTime(2026, 5, 1),
            BillingMode = ContractBillingMode.Fixed,
            CreatedAt = DateTime.UtcNow,
        };
        db.Contracts.AddRange(targetContract, otherContract);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                ContractId = targetContract.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 12_000m,
                PaidAmount = 2_000m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 5),
                PlannedDate = new DateTime(2026, 5, 5),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                ContractId = targetContract.Id,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Processing,
                Amount = 5_000m,
                PaidAmount = 1_000m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 6),
                PlannedDate = new DateTime(2026, 5, 6),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                ContractId = otherContract.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 20_000m,
                PaidAmount = 0m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 7),
                PlannedDate = new DateTime(2026, 5, 7),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlowForecast(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            "Main bank",
            null,
            null,
            null,
            targetContract.Id,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowForecastReportDto>(ok.Value);
        Assert.Equal(10_000m, report.ExpectedIncome);
        Assert.Equal(4_000m, report.ExpectedExpense);
        Assert.Equal(6_000m, report.NetProjectedFlow);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(1, report.ExpenseCount);
    }

    [Fact]
    public async Task CashFlow_FiltersByIncomeTypeId()
    {
        await using var db = await CreateContextAsync();
        var services = new IncomeType
        {
            Name = "Services",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var other = new IncomeType
        {
            Name = "Other",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.AddRange(services, other);
        await db.SaveChangesAsync();

        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 4, 25), "Main bank", services.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 4), "Main bank", services.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 5), "Main bank", other.Id),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 6), "Cash", services.Id));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
services.Id,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(3_000m, report.OpeningBalance);
        Assert.Equal(7_000m, report.Income);
        Assert.Equal(2_000m, report.Expense);
        Assert.Equal(8_000m, report.ClosingBalance);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(1, report.ExpenseCount);
    }

    [Fact]
    public async Task CashFlowForecast_FiltersByIncomeTypeId()
    {
        await using var db = await CreateContextAsync();
        var services = new IncomeType
        {
            Name = "Services",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var expenses = new IncomeType
        {
            Name = "Expenses",
            PaymentType = PaymentType.Expense,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var other = new IncomeType
        {
            Name = "Other",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.AddRange(services, expenses, other);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                IncomeTypeId = services.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 12_000m,
                PaidAmount = 2_000m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 5),
                PlannedDate = new DateTime(2026, 5, 5),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                IncomeTypeId = services.Id,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Processing,
                Amount = 5_000m,
                PaidAmount = 1_000m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 6),
                PlannedDate = new DateTime(2026, 5, 6),
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                IncomeTypeId = other.Id,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 20_000m,
                PaidAmount = 0m,
                Account = "Main bank",
                Date = new DateTime(2026, 5, 7),
                PlannedDate = new DateTime(2026, 5, 7),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlowForecast(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            "Main bank",
            null,
            null,
            null,
            null,
services.Id,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowForecastReportDto>(ok.Value);
        Assert.Equal(10_000m, report.ExpectedIncome);
        Assert.Equal(4_000m, report.ExpectedExpense);
        Assert.Equal(6_000m, report.NetProjectedFlow);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(1, report.ExpenseCount);
    }

    [Fact]
    public async Task Categories_GroupsFactOperationsByIncomeTypeAndCategoryName()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
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
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", services.Id, counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк", services.Id, counterpartyName: "ИП Петров"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", categoryName: "Розница"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3, 15, 0, 0), "Касса", categoryName: " розница "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 4), "Основной банк", taxes.Id, counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 5), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", services.Id));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.Categories(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<MoneyCategoryReportDto>(ok.Value);
        Assert.Equal(20_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(15_000m, report.NetTotal);

        Assert.Collection(
            report.Income,
            item =>
            {
                Assert.Equal("Услуги", item.CategoryName);
                Assert.Equal(15_000m, item.Amount);
                Assert.Equal(2, item.Count);
                Assert.Equal(75m, item.Share);
            },
            item =>
            {
                Assert.Equal("Розница", item.CategoryName);
                Assert.Equal(5_000m, item.Amount);
                Assert.Equal(2, item.Count);
                Assert.Equal(25m, item.Share);
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

        var categoryQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", categoryQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", categoryQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", categoryQuery, StringComparison.OrdinalIgnoreCase);
        interceptor.Commands.Clear();

        var accountAction = await controller.Categories(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            "Касса",
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var accountOk = Assert.IsType<OkObjectResult>(accountAction.Result);
        var accountReport = Assert.IsType<MoneyCategoryReportDto>(accountOk.Value);
        Assert.Equal(5_000m, accountReport.IncomeTotal);
        Assert.Equal(0m, accountReport.ExpenseTotal);
        var incomeItem = Assert.Single(accountReport.Income);
        Assert.Equal("Розница", incomeItem.CategoryName);
        Assert.Equal(5_000m, incomeItem.Amount);
        Assert.Equal(2, incomeItem.Count);

        var counterpartyAction = await controller.Categories(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            "Ромаш",
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var counterpartyOk = Assert.IsType<OkObjectResult>(counterpartyAction.Result);
        var counterpartyReport = Assert.IsType<MoneyCategoryReportDto>(counterpartyOk.Value);
        Assert.Equal(10_000m, counterpartyReport.IncomeTotal);
        Assert.Equal(4_000m, counterpartyReport.ExpenseTotal);
        Assert.Equal(6_000m, counterpartyReport.NetTotal);
    }

    [Fact]
    public async Task Counterparties_GroupsFactOperationsByCounterparty()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк", counterpartyName: "  ооо ромашка  "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Основной банк", counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 4), "Касса", counterpartyName: "ИП Петров"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 5), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк", counterpartyName: "ООО Ромашка"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.Counterparties(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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

        var counterpartyQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", counterpartyQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", counterpartyQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", counterpartyQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Projects_GroupsFactOperationsByProjectName()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 12_000m, new DateTime(2026, 5, 1), "Основной банк", projectName: "Проект Альфа"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 2), "Касса", projectName: " проект альфа "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 3), "Основной банк", projectName: "Проект Бета"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", projectName: "Проект Альфа"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.Projects(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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

        var projectQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", projectQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", projectQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", projectQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Directions_GroupsFactOperationsByDirectionName()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 9_000m, new DateTime(2026, 5, 1), "Основной банк", directionName: "Консалтинг"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Касса", directionName: " консалтинг "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 3), "Основной банк", directionName: "Поддержка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 4), "Основной банк", directionName: "Консалтинг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", directionName: "Консалтинг"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.Directions(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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

        var directionQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", directionQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", directionQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", directionQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SalesPoints_GroupsFactOperationsBySalesPointName()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 1), "Основной банк", salesPointName: "Офис"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 2), "Касса", salesPointName: " офис "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_500m, new DateTime(2026, 5, 3), "Основной банк", salesPointName: "Онлайн"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", salesPointName: "Офис"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.SalesPoints(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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

        var salesPointQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", salesPointQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", salesPointQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", salesPointQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Vat_GroupsFactOperationsByVatRate()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 12_000m, new DateTime(2026, 5, 1), "Основной банк", vatRate: 20m, vatAmount: 2_000m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_200m, new DateTime(2026, 5, 1), "Основной банк", vatRate: 20.004m, vatAmount: 200m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_500m, new DateTime(2026, 5, 2), "Основной банк", vatRate: 10m, vatAmount: 500m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 3), "Основной банк", vatRate: 20m, vatAmount: 1_000m),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", vatRate: 20m, vatAmount: 16_666.50m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", vatRate: 20m, vatAmount: 8_333.33m));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.Vat(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<VatReportDto>(ok.Value);
        Assert.Equal(18_700m, report.IncomeAmountTotal);
        Assert.Equal(2_700m, report.IncomeVatTotal);
        Assert.Equal(9_000m, report.ExpenseAmountTotal);
        Assert.Equal(1_000m, report.ExpenseVatTotal);
        Assert.Equal(1_700m, report.VatBalance);

        Assert.Collection(
            report.Rates,
            item =>
            {
                Assert.Equal(20m, item.VatRate);
                Assert.Equal("20%", item.VatRateLabel);
                Assert.Equal(11_000m, item.IncomeBase);
                Assert.Equal(2_200m, item.IncomeVat);
                Assert.Equal(13_200m, item.IncomeGross);
                Assert.Equal(5_000m, item.ExpenseBase);
                Assert.Equal(1_000m, item.ExpenseVat);
                Assert.Equal(6_000m, item.ExpenseGross);
                Assert.Equal(1_200m, item.NetVat);
                Assert.Equal(2, item.IncomeCount);
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

        var vatQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", vatQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", vatQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanFact_ComparesPlanAndFactOperations()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 10_000m, new DateTime(2026, 5, 1), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Plan, 4_000m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 3), "Основной банк"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.PlanFact(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            MoneyOperationSource.Bank,
            null,
            null,
            null,
            null,
            null,
            null,
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

        var planFactQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", planFactQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", planFactQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Advances_GroupsAdvanceFactOperationsByCounterpartyAndRecognition()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        var advanceClient = new Client { Name = "ООО Ромашка" };
        db.Clients.Add(advanceClient);
        await db.SaveChangesAsync();

        var sourceAdvancePayment = InvoicePayment(
            "Аванс без счета",
            PaymentType.Income,
            PaymentStatus.Completed,
            "signed",
            5_000m,
            5_000m,
            new DateTime(2026, 5, 7));
        sourceAdvancePayment.ClientId = advanceClient.Id;
        sourceAdvancePayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        db.Payments.Add(sourceAdvancePayment);
        await db.SaveChangesAsync();

        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true, recognitionDate: new DateTime(2026, 5, 10)),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 2), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 3), "Основной банк", counterpartyName: "ИП Петров", isAdvancePayment: true, recognitionDate: new DateTime(2026, 6, 1)),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 4), "Касса", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 6), "Основной банк", counterpartyName: "ООО Ромашка"));
        db.ClientBalanceTransactions.AddRange(
            new ClientBalanceTransaction
            {
                ClientId = advanceClient.Id,
                PaymentId = sourceAdvancePayment.Id,
                Amount = 5_000m,
                Type = ClientBalanceTransactionType.UnallocatedAdvance,
                Direction = ClientBalanceDirection.Credit,
                Description = "Принят как аванс",
                ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceKey(sourceAdvancePayment.Id),
                CreatedAt = new DateTime(2026, 5, 7),
            },
            new ClientBalanceTransaction
            {
                ClientId = advanceClient.Id,
                PaymentId = sourceAdvancePayment.Id,
                Amount = 1_500m,
                Type = ClientBalanceTransactionType.CreditApplied,
                Direction = ClientBalanceDirection.Debit,
                Description = "Зачет части аванса",
                ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceApplicationKey(sourceAdvancePayment.Id, sourceAdvancePayment.Id + 100, new DateTime(2026, 5, 8)),
                CreatedAt = new DateTime(2026, 5, 8),
            },
            new ClientBalanceTransaction
            {
                ClientId = advanceClient.Id,
                PaymentId = sourceAdvancePayment.Id,
                Amount = 500m,
                Type = ClientBalanceTransactionType.Refund,
                Direction = ClientBalanceDirection.Debit,
                Description = "Возврат части аванса",
                ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceRefundKey(sourceAdvancePayment.Id),
                CreatedAt = new DateTime(2026, 5, 9),
            });
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.Advances(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<AdvanceReportDto>(ok.Value);
        Assert.Equal(19_000m, report.IncomeTotal);
        Assert.Equal(4_000m, report.ExpenseTotal);
        Assert.Equal(10_000m, report.RecognizedTotal);
        Assert.Equal(13_000m, report.UnrecognizedTotal);
        Assert.Equal(3_000m, report.UnallocatedBalanceTotal);
        Assert.Equal(1, report.RecognizedCount);
        Assert.Equal(3, report.UnrecognizedCount);
        Assert.Equal(1, report.UnallocatedBalanceCount);

        Assert.Collection(
            report.Counterparties,
            item =>
            {
                Assert.Equal("ООО Ромашка", item.CounterpartyName);
                Assert.Equal(16_000m, item.IncomeTotal);
                Assert.Equal(0m, item.ExpenseTotal);
                Assert.Equal(10_000m, item.RecognizedTotal);
                Assert.Equal(6_000m, item.UnrecognizedTotal);
                Assert.Equal(3_000m, item.UnallocatedBalanceTotal);
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.UnallocatedBalanceCount);
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

        var advanceQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", advanceQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", advanceQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", advanceQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentPriorities_GroupsFactOperationsByPriority()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 2), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Основной банк", paymentPriority: 2),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paymentPriority: 1));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.PaymentPriorities(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
            },
            item =>
            {
                Assert.Equal(2, item.Priority);
                Assert.Equal("Средний приоритет", item.PriorityLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(13.3m, item.Share);
                Assert.Equal(0, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
            },
            item =>
            {
                Assert.Null(item.Priority);
                Assert.Equal("Без приоритета", item.PriorityLabel);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(6.7m, item.Share);
                Assert.Equal(1, item.IncomeCount);
                Assert.Equal(0, item.ExpenseCount);
            });

        var priorityQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", priorityQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", priorityQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", priorityQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaidAccounts_GroupsFactOperationsByPaidAccountName()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", paidAccountName: "Расчетный счет"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", paidAccountName: "Расчетный счет"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paidAccountName: " расчетный счет "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", paidAccountName: "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paidAccountName: "Расчетный счет"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paidAccountName: "Расчетный счет"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.PaidAccounts(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
            },
            item =>
            {
                Assert.Equal("Касса", item.PaidAccountName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(23.1m, item.Share);
                Assert.Equal(0, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
            },
            item =>
            {
                Assert.Equal("Без счета оплаты", item.PaidAccountName);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(7.7m, item.Share);
                Assert.Equal(1, item.IncomeCount);
                Assert.Equal(0, item.ExpenseCount);
            });

        var paidAccountQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", paidAccountQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", paidAccountQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", paidAccountQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN", paidAccountQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaidInvoices_GroupsFactOperationsByPaidInvoiceNumber()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 1_800m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 2_700m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paidInvoiceNumber: " inv-10 ", paidAmount: 1_900m),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", paidInvoiceNumber: "INV-11"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк", paidAmount: 800m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 99_999m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 50_000m));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.PaidInvoices(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
            },
            item =>
            {
                Assert.Equal("INV-11", item.PaidInvoiceNumber);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(3_000m, item.PaidAmountTotal);
                Assert.Equal(27.3m, item.Share);
                Assert.Equal(1, item.IncomeCount);
                Assert.Equal(0, item.ExpenseCount);
            },
            item =>
            {
                Assert.Equal("Без номера счета", item.PaidInvoiceNumber);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(800m, item.PaidAmountTotal);
                Assert.Equal(9.1m, item.Share);
                Assert.Equal(1, item.IncomeCount);
                Assert.Equal(0, item.ExpenseCount);
            });

        var paidInvoiceQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", paidInvoiceQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", paidInvoiceQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", paidInvoiceQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN", paidInvoiceQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvoiceStatuses_GroupsInvoicesByWorkflowStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.Payments.AddRange(
            InvoicePayment("INV-1A", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.Draft, 400m, 0m, new DateTime(2026, 5, 1, 9, 0, 0)),
            InvoicePayment("INV-1B", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.Draft, 600m, 0m, new DateTime(2026, 5, 1, 15, 0, 0)),
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
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.InvoiceStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<InvoiceStatusReportDto>(ok.Value);
        Assert.Equal(9, report.InvoiceCount);
        Assert.Equal(7, report.IncomeCount);
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
                Assert.Equal(2, item.InvoiceCount);
                Assert.Equal(2, item.IncomeCount);
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

        var invoiceStatusQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"Payments\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", invoiceStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", invoiceStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", invoiceStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", invoiceStatusQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvoiceStatuses_AppliesClientCaseContractAndIncomeTypeFilters()
    {
        await using var db = await CreateContextAsync();
        var targetClient = new Client { Name = "Invoice status target" };
        var otherClient = new Client { Name = "Invoice status other" };
        var targetCase = new ClientCase { Client = targetClient, CaseNumber = "INV-FILTER", Title = "Invoice filter case" };
        var otherCase = new ClientCase { Client = otherClient, CaseNumber = "INV-OTHER", Title = "Invoice other case" };
        var targetContract = Contract("INV-CONTRACT", ContractStatus.Concluded, 5_000m, new DateTime(2026, 5, 1), false, null);
        var targetIncomeType = new IncomeType { Name = "Invoice status services", PaymentType = PaymentType.Income };
        db.AddRange(targetClient, otherClient, targetCase, otherCase, targetContract, targetIncomeType);
        await db.SaveChangesAsync();

        var matching = InvoicePayment("INV-FILTER-1", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 1_000m, 1_000m, new DateTime(2026, 5, 10));
        matching.ClientId = targetClient.Id;
        matching.ClientCaseId = targetCase.Id;
        matching.ContractId = targetContract.Id;
        matching.IncomeTypeId = targetIncomeType.Id;

        var otherClientInvoice = InvoicePayment("INV-FILTER-2", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 2_000m, 2_000m, new DateTime(2026, 5, 11));
        otherClientInvoice.ClientId = otherClient.Id;
        otherClientInvoice.ClientCaseId = targetCase.Id;
        otherClientInvoice.ContractId = targetContract.Id;
        otherClientInvoice.IncomeTypeId = targetIncomeType.Id;

        var otherCaseInvoice = InvoicePayment("INV-FILTER-3", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 3_000m, 3_000m, new DateTime(2026, 5, 12));
        otherCaseInvoice.ClientId = targetClient.Id;
        otherCaseInvoice.ClientCaseId = otherCase.Id;
        otherCaseInvoice.ContractId = targetContract.Id;
        otherCaseInvoice.IncomeTypeId = targetIncomeType.Id;

        var otherContractInvoice = InvoicePayment("INV-FILTER-4", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 4_000m, 4_000m, new DateTime(2026, 5, 13));
        otherContractInvoice.ClientId = targetClient.Id;
        otherContractInvoice.ClientCaseId = targetCase.Id;
        otherContractInvoice.IncomeTypeId = targetIncomeType.Id;

        db.Payments.AddRange(matching, otherClientInvoice, otherCaseInvoice, otherContractInvoice);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.InvoiceStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            clientId: targetClient.Id,
            caseId: targetCase.Id,
            contractId: targetContract.Id,
            incomeTypeId: targetIncomeType.Id);

        var report = Assert.IsType<InvoiceStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, report.InvoiceCount);
        Assert.Equal(1_000m, report.TotalAmount);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(InvoiceWorkflowStatus.Paid, status.Status);
        Assert.Equal(1_000m, status.TotalAmount);
    }

    [Fact]
    public async Task InvoiceStatuses_AppliesMoneyOperationFilters()
    {
        await using var db = await CreateContextAsync();
        var targetInvoiceDocument = new Invoice { Number = "INV-MONEY-1", Date = new DateTime(2026, 5, 10) };
        var otherInvoiceDocument = new Invoice { Number = "INV-MONEY-2", Date = new DateTime(2026, 5, 11) };
        db.Invoices.AddRange(targetInvoiceDocument, otherInvoiceDocument);
        await db.SaveChangesAsync();

        var matching = InvoicePayment("INV-MONEY-1", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 1_000m, 1_000m, new DateTime(2026, 5, 10));
        matching.InvoiceId = targetInvoiceDocument.Id;
        var other = InvoicePayment("INV-MONEY-2", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 2_000m, 2_000m, new DateTime(2026, 5, 11));
        other.InvoiceId = otherInvoiceDocument.Id;
        db.Payments.AddRange(matching, other);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 10), "Main account", counterpartyName: "Target payer", linkedInvoiceId: targetInvoiceDocument.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 11), "Other account", counterpartyName: "Target payer", linkedInvoiceId: otherInvoiceDocument.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 9_000m, new DateTime(2026, 5, 12), "Main account", counterpartyName: "Target payer", linkedInvoiceId: otherInvoiceDocument.Id));
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.InvoiceStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            source: MoneyOperationSource.Bank,
            accountName: "Main account",
            counterpartyName: "Target");

        var report = Assert.IsType<InvoiceStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, report.InvoiceCount);
        Assert.Equal(1_000m, report.TotalAmount);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(InvoiceWorkflowStatus.Paid, status.Status);
        Assert.Equal(1_000m, status.TotalAmount);
    }

    [Fact]
    public async Task ActStatuses_GroupsActsByWorkflowStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.Acts.AddRange(
            Act("ACT-1A", ActStatus.Created, ActWorkflowStatus.Draft, 400m, new DateTime(2026, 5, 1, 9, 0, 0)),
            Act("ACT-1B", ActStatus.Created, ActWorkflowStatus.Draft, 600m, new DateTime(2026, 5, 1, 15, 0, 0)),
            Act("ACT-2", ActStatus.Transferred, ActWorkflowStatus.OnSigning, 2_000m, new DateTime(2026, 5, 2)),
            Act("ACT-3", ActStatus.Signed, ActWorkflowStatus.Signed, 3_000m, new DateTime(2026, 5, 3)),
            Act("ACT-4", ActStatus.Terminated, ActWorkflowStatus.Cancelled, 4_000m, new DateTime(2026, 5, 4)),
            Act("ACT-5", ActStatus.Created, ActWorkflowStatus.NotSigned, 5_000m, new DateTime(2026, 5, 5)),
            Act("ACT-OLD", ActStatus.Signed, ActWorkflowStatus.Signed, 99_999m, new DateTime(2026, 4, 30)));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.ActStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ActStatusReportDto>(ok.Value);
        Assert.Equal(6, report.ActCount);
        Assert.Equal(3, report.CreatedCount);
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
                Assert.Equal(2, item.ActCount);
                Assert.Equal(2, item.CreatedCount);
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

        var actStatusQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"Acts\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", actStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", actStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", actStatusQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActStatuses_AppliesClientAndContractFilters()
    {
        await using var db = await CreateContextAsync();
        var targetClient = new Client { Name = "Act status target" };
        var otherClient = new Client { Name = "Act status other" };
        var targetContract = Contract("ACT-CONTRACT", ContractStatus.Concluded, 9_000m, new DateTime(2026, 5, 1), false, null);
        db.AddRange(targetClient, otherClient, targetContract);
        await db.SaveChangesAsync();

        var matching = Act("ACT-FILTER-1", ActStatus.Signed, ActWorkflowStatus.Signed, 1_200m, new DateTime(2026, 5, 10));
        matching.ClientId = targetClient.Id;
        var otherClientAct = Act("ACT-FILTER-2", ActStatus.Signed, ActWorkflowStatus.Signed, 2_200m, new DateTime(2026, 5, 11));
        otherClientAct.ClientId = otherClient.Id;
        var unlinkedAct = Act("ACT-FILTER-3", ActStatus.Signed, ActWorkflowStatus.Signed, 3_200m, new DateTime(2026, 5, 12));
        unlinkedAct.ClientId = targetClient.Id;
        db.Acts.AddRange(matching, otherClientAct, unlinkedAct);
        await db.SaveChangesAsync();

        db.ContractActs.Add(new ContractAct { ContractId = targetContract.Id, ActId = matching.Id });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.ActStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            clientId: targetClient.Id,
            contractId: targetContract.Id);

        var report = Assert.IsType<ActStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, report.ActCount);
        Assert.Equal(1_200m, report.TotalAmount);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(ActWorkflowStatus.Signed, status.WorkflowStatus);
        Assert.Equal(1_200m, status.TotalAmount);
    }

    [Fact]
    public async Task ActStatuses_AppliesMoneyOperationFilters()
    {
        await using var db = await CreateContextAsync();
        var targetIncomeType = new IncomeType { Name = "Act status target income", PaymentType = PaymentType.Income };
        var otherIncomeType = new IncomeType { Name = "Act status other income", PaymentType = PaymentType.Income };
        var targetInvoiceDocument = new Invoice { Number = "ACT-MONEY-1", Date = new DateTime(2026, 5, 10) };
        var otherInvoiceDocument = new Invoice { Number = "ACT-MONEY-2", Date = new DateTime(2026, 5, 11) };
        db.IncomeTypes.AddRange(targetIncomeType, otherIncomeType);
        db.Invoices.AddRange(targetInvoiceDocument, otherInvoiceDocument);
        await db.SaveChangesAsync();

        var matchingInvoice = InvoicePayment("ACT-MONEY-1", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 1_200m, 1_200m, new DateTime(2026, 5, 10));
        matchingInvoice.InvoiceId = targetInvoiceDocument.Id;
        matchingInvoice.IncomeTypeId = targetIncomeType.Id;
        var otherInvoice = InvoicePayment("ACT-MONEY-2", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 2_200m, 2_200m, new DateTime(2026, 5, 11));
        otherInvoice.InvoiceId = otherInvoiceDocument.Id;
        otherInvoice.IncomeTypeId = otherIncomeType.Id;
        db.Payments.AddRange(matchingInvoice, otherInvoice);
        await db.SaveChangesAsync();

        var matching = Act("ACT-MONEY-1", ActStatus.Signed, ActWorkflowStatus.Signed, 1_200m, new DateTime(2026, 5, 10));
        matching.InvoiceId = matchingInvoice.Id;
        var other = Act("ACT-MONEY-2", ActStatus.Signed, ActWorkflowStatus.Signed, 2_200m, new DateTime(2026, 5, 11));
        other.InvoiceId = otherInvoice.Id;
        db.Acts.AddRange(matching, other);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_200m, new DateTime(2026, 5, 10), "Main account", counterpartyName: "Target payer", linkedInvoiceId: targetInvoiceDocument.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_200m, new DateTime(2026, 5, 11), "Main account", counterpartyName: "Target payer", linkedInvoiceId: otherInvoiceDocument.Id));
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.ActStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            source: MoneyOperationSource.Bank,
            accountName: "Main account",
            counterpartyName: "Target",
            incomeTypeId: targetIncomeType.Id);

        var report = Assert.IsType<ActStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, report.ActCount);
        Assert.Equal(1_200m, report.TotalAmount);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(ActWorkflowStatus.Signed, status.WorkflowStatus);
        Assert.Equal(1_200m, status.TotalAmount);
    }

    [Fact]
    public async Task ActStatuses_TreatActDatesAsLocalDates()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithTimeZoneAsync(db, "America/New_York");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Acts,
            CanView = true,
            ViewScope = DataVisibilityScope.All,
        });

        db.Acts.AddRange(
            Act(
                "ACT-TZ",
                ActStatus.Signed,
                ActWorkflowStatus.Signed,
                8_000m,
                new DateTime(2026, 5, 31, 18, 30, 0, DateTimeKind.Utc)),
            Act(
                "ACT-JUNE",
                ActStatus.Created,
                ActWorkflowStatus.Draft,
                6_000m,
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.ActStatuses(
            new DateTime(2026, 5, 31),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var report = Assert.IsType<ActStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(new DateTime(2026, 5, 31), report.From);
        Assert.Equal(DateTimeKind.Unspecified, report.From.Kind);
        Assert.Equal(new DateTime(2026, 5, 31), report.To);
        Assert.Equal(DateTimeKind.Unspecified, report.To.Kind);
        Assert.Equal(1, report.ActCount);
        Assert.Equal(8_000m, report.TotalAmount);
        Assert.Equal(8_000m, report.SignedAmount);

        var status = Assert.Single(report.Statuses);
        Assert.Equal(ActWorkflowStatus.Signed, status.WorkflowStatus);
        Assert.Equal(1, status.SignedCount);
    }

    [Fact]
    public async Task ContractStatuses_GroupsContractsByStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.Contracts.AddRange(
            Contract("C-1A", ContractStatus.Draft, 400m, new DateTime(2026, 5, 1, 9, 0, 0), false, new DateTime(2026, 5, 20)),
            Contract("C-1B", ContractStatus.Draft, 600m, new DateTime(2026, 5, 1, 15, 0, 0), false, new DateTime(2026, 5, 20)),
            Contract("C-2", ContractStatus.OnSigning, 2_000m, new DateTime(2026, 5, 2), false, new DateTime(2026, 6, 20)),
            Contract("C-3", ContractStatus.Concluded, 3_000m, new DateTime(2026, 5, 3), true, null),
            Contract("C-4", ContractStatus.Terminated, 4_000m, new DateTime(2026, 5, 4), false, new DateTime(2026, 5, 25)),
            Contract("C-5", ContractStatus.Cancelled, 5_000m, new DateTime(2026, 5, 5), false, null),
            Contract("C-6", ContractStatus.NotSigned, 6_000m, new DateTime(2026, 5, 6), false, new DateTime(2026, 5, 28)),
            Contract("C-OLD", ContractStatus.Concluded, 99_999m, new DateTime(2026, 4, 30), false, null));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.ContractStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ContractStatusReportDto>(ok.Value);
        Assert.Equal(7, report.ContractCount);
        Assert.Equal(2, report.DraftCount);
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
                Assert.Equal(2, item.ExpiringCount);
                Assert.Equal(2, item.ContractCount);
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

        var contractStatusQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"Contracts\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", contractStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", contractStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", contractStatusQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContractStatuses_AppliesClientCaseAndContractFilters()
    {
        await using var db = await CreateContextAsync();
        var targetClient = new Client { Name = "Contract status target" };
        var otherClient = new Client { Name = "Contract status other" };
        var targetCase = new ClientCase { Client = targetClient, CaseNumber = "CON-FILTER", Title = "Contract filter case" };
        var matching = Contract("CON-FILTER-1", ContractStatus.Concluded, 1_500m, new DateTime(2026, 5, 10), false, null);
        var otherClientContract = Contract("CON-FILTER-2", ContractStatus.Concluded, 2_500m, new DateTime(2026, 5, 11), false, null);
        db.AddRange(targetClient, otherClient, targetCase, matching, otherClientContract);
        await db.SaveChangesAsync();

        db.ClientContracts.AddRange(
            new ClientContract { ClientId = targetClient.Id, ContractId = matching.Id },
            new ClientContract { ClientId = otherClient.Id, ContractId = otherClientContract.Id });
        var linkedInvoice = InvoicePayment("CON-FILTER-INV", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 1_500m, 1_500m, new DateTime(2026, 5, 12));
        linkedInvoice.ClientId = targetClient.Id;
        linkedInvoice.ClientCaseId = targetCase.Id;
        db.Payments.Add(linkedInvoice);
        await db.SaveChangesAsync();

        db.ContractInvoices.Add(new ContractInvoice { ContractId = matching.Id, InvoiceId = linkedInvoice.Id });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.ContractStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            clientId: targetClient.Id,
            caseId: targetCase.Id,
            contractId: matching.Id);

        var report = Assert.IsType<ContractStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, report.ContractCount);
        Assert.Equal(1_500m, report.TotalAmount);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(ContractStatus.Concluded, status.Status);
        Assert.Equal(1_500m, status.TotalAmount);
    }

    [Fact]
    public async Task ContractStatuses_AppliesMoneyOperationFilters()
    {
        await using var db = await CreateContextAsync();
        var targetIncomeType = new IncomeType { Name = "Contract status target income", PaymentType = PaymentType.Income };
        var otherIncomeType = new IncomeType { Name = "Contract status other income", PaymentType = PaymentType.Income };
        var targetInvoiceDocument = new Invoice { Number = "CON-MONEY-1", Date = new DateTime(2026, 5, 10) };
        var otherInvoiceDocument = new Invoice { Number = "CON-MONEY-2", Date = new DateTime(2026, 5, 11) };
        db.IncomeTypes.AddRange(targetIncomeType, otherIncomeType);
        db.Invoices.AddRange(targetInvoiceDocument, otherInvoiceDocument);
        await db.SaveChangesAsync();

        var matching = Contract("CON-MONEY-1", ContractStatus.Concluded, 1_500m, new DateTime(2026, 5, 10), false, null);
        var other = Contract("CON-MONEY-2", ContractStatus.Concluded, 2_500m, new DateTime(2026, 5, 11), false, null);
        db.Contracts.AddRange(matching, other);
        await db.SaveChangesAsync();

        var matchingInvoice = InvoicePayment("CON-MONEY-1", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 1_500m, 1_500m, new DateTime(2026, 5, 10));
        matchingInvoice.InvoiceId = targetInvoiceDocument.Id;
        matchingInvoice.IncomeTypeId = targetIncomeType.Id;
        var otherInvoice = InvoicePayment("CON-MONEY-2", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 2_500m, 2_500m, new DateTime(2026, 5, 11));
        otherInvoice.InvoiceId = otherInvoiceDocument.Id;
        otherInvoice.IncomeTypeId = otherIncomeType.Id;
        db.Payments.AddRange(matchingInvoice, otherInvoice);
        await db.SaveChangesAsync();

        db.ContractInvoices.AddRange(
            new ContractInvoice { ContractId = matching.Id, InvoiceId = matchingInvoice.Id },
            new ContractInvoice { ContractId = other.Id, InvoiceId = otherInvoice.Id });
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 10), "Main account", counterpartyName: "Target payer", linkedInvoiceId: targetInvoiceDocument.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 2_500m, new DateTime(2026, 5, 11), "Main account", counterpartyName: "Target payer", linkedInvoiceId: otherInvoiceDocument.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_500m, new DateTime(2026, 5, 11), "Main account", counterpartyName: "Target payer", linkedInvoiceId: otherInvoiceDocument.Id));
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.ContractStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            source: MoneyOperationSource.Bank,
            accountName: "Main account",
            counterpartyName: "Target",
            incomeTypeId: targetIncomeType.Id);

        var report = Assert.IsType<ContractStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, report.ContractCount);
        Assert.Equal(1_500m, report.TotalAmount);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(ContractStatus.Concluded, status.Status);
        Assert.Equal(1_500m, status.TotalAmount);
    }

    [Fact]
    public async Task ContractStatuses_TreatContractDatesAsLocalDates()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithTimeZoneAsync(db, "America/New_York");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Contracts,
            CanView = true,
            ViewScope = DataVisibilityScope.All,
        });

        db.Contracts.AddRange(
            Contract(
                "C-TZ",
                ContractStatus.Concluded,
                7_000m,
                new DateTime(2026, 5, 31, 18, 30, 0, DateTimeKind.Utc),
                false,
                new DateTime(2026, 5, 31, 23, 30, 0, DateTimeKind.Utc)),
            Contract(
                "C-JUNE",
                ContractStatus.Draft,
                5_000m,
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                false,
                null));
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.ContractStatuses(
            new DateTime(2026, 5, 31),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var report = Assert.IsType<ContractStatusReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(new DateTime(2026, 5, 31), report.From);
        Assert.Equal(DateTimeKind.Unspecified, report.From.Kind);
        Assert.Equal(new DateTime(2026, 5, 31), report.To);
        Assert.Equal(DateTimeKind.Unspecified, report.To.Kind);
        Assert.Equal(1, report.ContractCount);
        Assert.Equal(7_000m, report.TotalAmount);

        var status = Assert.Single(report.Statuses);
        Assert.Equal(ContractStatus.Concluded, status.Status);
        Assert.Equal(1, status.ExpiringCount);
    }

    [Fact]
    public async Task PaymentOrders_GroupsFactOperationsByPaymentOrderNumber()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_500m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", paymentOrderNumber: "PO-1"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_500m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", paymentOrderNumber: "PO-1"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paymentOrderNumber: " po-1 "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 3), "Касса", paymentOrderNumber: "PO-2"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paymentOrderNumber: "PO-1"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paymentOrderNumber: "PO-1"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.PaymentOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
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

        var paymentOrderQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", paymentOrderQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", paymentOrderQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", paymentOrderQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN", paymentOrderQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentPurposes_GroupsFactOperationsByPurpose()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", purpose: "Оплата услуг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", purpose: "Оплата услуг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", purpose: " оплата услуг "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 3), "Касса", purpose: "Возврат"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", purpose: "Оплата услуг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", purpose: "Оплата услуг"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.PaymentPurposes(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
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

        var paymentPurposeQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", paymentPurposeQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", paymentPurposeQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", paymentPurposeQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN", paymentPurposeQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchStatuses_GroupsFactOperationsByMatchStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_500m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_500m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк", matchStatus: MoneyOperationMatchStatus.ClarificationRequired),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 5), "Касса", matchStatus: MoneyOperationMatchStatus.Ignored),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 7), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.MatchStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<MatchStatusReportDto>(ok.Value);
        Assert.Equal(7_000m, report.IncomeTotal);
        Assert.Equal(5_500m, report.ExpenseTotal);
        Assert.Equal(1_500m, report.NetTotal);
        Assert.Equal(6, report.OperationCount);
        Assert.Equal(3, report.MatchedCount);
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
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
                Assert.Equal(3, item.OperationCount);
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

        var matchStatusQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", matchStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", matchStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", matchStatusQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JournalStatuses_GroupsFactOperationsByComputedJournalStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_500m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_500m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк", matchStatus: MoneyOperationMatchStatus.ClarificationRequired),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 5), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 7), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.JournalStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<JournalStatusReportDto>(ok.Value);
        Assert.Equal(9_500m, report.IncomeTotal);
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(6_500m, report.NetTotal);
        Assert.Equal(6, report.OperationCount);
        Assert.Equal(4, report.PostedCount);
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
                Assert.Equal(3, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
                Assert.Equal(4, item.OperationCount);
                Assert.Equal(68m, item.Share);
            });

        var journalStatusQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", journalStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", journalStatusQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", journalStatusQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Currencies_GroupsFactOperationsByCurrency()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 1, 9, 0, 0), "Основной банк", currency: "RUB"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 1, 15, 0, 0), "Основной банк", currency: "RUB"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", currency: " rub "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк", currency: "usd"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса", currency: null),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", currency: "USD"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", currency: "RUB"));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.Currencies(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
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
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(2, item.ExpenseCount);
                Assert.Equal(76.9m, item.Share);
            },
            item =>
            {
                Assert.Equal("USD", item.Currency);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(23.1m, item.Share);
            });

        var currencyQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"MoneyOperations\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", currencyQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", currencyQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", currencyQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CashOrders_GroupsOrdersByStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.CashOrders.AddRange(
            CashOrder("PKO-1", CashOrderType.Incoming, CashOrderStatus.Posted, 3_000m, new DateTime(2026, 5, 1, 9, 0, 0)),
            CashOrder("PKO-1-2", CashOrderType.Incoming, CashOrderStatus.Posted, 2_000m, new DateTime(2026, 5, 1, 15, 0, 0)),
            CashOrder("RKO-1", CashOrderType.Outgoing, CashOrderStatus.Posted, 2_000m, new DateTime(2026, 5, 2)),
            CashOrder("PKO-2", CashOrderType.Incoming, CashOrderStatus.Draft, 1_000m, new DateTime(2026, 5, 3)),
            CashOrder("RKO-2", CashOrderType.Outgoing, CashOrderStatus.Cancelled, 500m, new DateTime(2026, 5, 4)),
            CashOrder("PKO-OLD", CashOrderType.Incoming, CashOrderStatus.Posted, 99_999m, new DateTime(2026, 4, 30)));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
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
        Assert.Equal(5, report.OrderCount);
        Assert.Equal(1, report.DraftCount);
        Assert.Equal(3, report.PostedCount);
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
                Assert.Equal(2, item.IncomingCount);
                Assert.Equal(1, item.OutgoingCount);
                Assert.Equal(3, item.OrderCount);
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

        var cashOrderQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"CashOrders\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", cashOrderQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", cashOrderQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", cashOrderQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CashOrders_AppliesMoneyOperationFilters()
    {
        await using var db = await CreateContextAsync();
        var targetClient = new Client { Name = "Cash order target client", Company = "ООО Целевой клиент" };
        var otherClient = new Client { Name = "Cash order other client" };
        var targetCase = new ClientCase { Client = targetClient, CaseNumber = "CASH-FILTER", Title = "Cash order case" };
        var targetContract = Contract("CASH-CONTRACT", ContractStatus.Concluded, 5_000m, new DateTime(2026, 5, 1), false, null);
        var services = new IncomeType { Name = "Cash order services", PaymentType = PaymentType.Income };
        db.AddRange(targetClient, otherClient, targetCase, targetContract, services);
        await db.SaveChangesAsync();

        var matchingPayment = InvoicePayment("Касса", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 1_500m, 1_500m, new DateTime(2026, 5, 10), DocumentKind.Payment);
        matchingPayment.ClientId = targetClient.Id;
        matchingPayment.ClientCaseId = targetCase.Id;
        matchingPayment.ContractId = targetContract.Id;
        matchingPayment.IncomeTypeId = services.Id;
        matchingPayment.CounterpartyName = "ООО Целевой клиент";

        var otherClientPayment = InvoicePayment("Касса", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 2_500m, 2_500m, new DateTime(2026, 5, 11), DocumentKind.Payment);
        otherClientPayment.ClientId = otherClient.Id;
        otherClientPayment.ClientCaseId = targetCase.Id;
        otherClientPayment.ContractId = targetContract.Id;
        otherClientPayment.IncomeTypeId = services.Id;
        otherClientPayment.CounterpartyName = "ООО Целевой клиент";

        var otherAccountPayment = InvoicePayment("Другая касса", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 3_500m, 3_500m, new DateTime(2026, 5, 12), DocumentKind.Payment);
        otherAccountPayment.ClientId = targetClient.Id;
        otherAccountPayment.ClientCaseId = targetCase.Id;
        otherAccountPayment.ContractId = targetContract.Id;
        otherAccountPayment.IncomeTypeId = services.Id;
        otherAccountPayment.CounterpartyName = "ООО Целевой клиент";

        db.Payments.AddRange(matchingPayment, otherClientPayment, otherAccountPayment);
        await db.SaveChangesAsync();

        var matchingOrder = CashOrder("PKO-FILTER-1", CashOrderType.Incoming, CashOrderStatus.Posted, 1_500m, new DateTime(2026, 5, 10));
        matchingOrder.ClientId = targetClient.Id;
        matchingOrder.PaymentId = matchingPayment.Id;
        matchingOrder.Account = "Касса";
        matchingOrder.PayerName = "ООО Целевой клиент";

        var otherClientOrder = CashOrder("PKO-FILTER-2", CashOrderType.Incoming, CashOrderStatus.Posted, 2_500m, new DateTime(2026, 5, 11));
        otherClientOrder.ClientId = otherClient.Id;
        otherClientOrder.PaymentId = otherClientPayment.Id;
        otherClientOrder.Account = "Касса";
        otherClientOrder.PayerName = "ООО Целевой клиент";

        var otherAccountOrder = CashOrder("PKO-FILTER-3", CashOrderType.Incoming, CashOrderStatus.Posted, 3_500m, new DateTime(2026, 5, 12));
        otherAccountOrder.ClientId = targetClient.Id;
        otherAccountOrder.PaymentId = otherAccountPayment.Id;
        otherAccountOrder.Account = "Другая касса";
        otherAccountOrder.PayerName = "ООО Целевой клиент";

        db.CashOrders.AddRange(matchingOrder, otherClientOrder, otherAccountOrder);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.CashOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            source: MoneyOperationSource.Cash,
            accountName: "Касса",
            counterpartyName: "Целевой",
            clientId: targetClient.Id,
            caseId: targetCase.Id,
            contractId: targetContract.Id,
            incomeTypeId: services.Id);

        var report = Assert.IsType<CashOrderReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, report.OrderCount);
        Assert.Equal(1_500m, report.PostedIncomingTotal);
        Assert.Equal(0m, report.PostedOutgoingTotal);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(CashOrderStatus.Posted, status.Status);
        Assert.Equal(1_500m, status.IncomingTotal);
        Assert.Equal(1, status.IncomingCount);
    }

    [Fact]
    public async Task CashOrders_AppliesPaymentsVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: true,
            paymentsViewScope: DataVisibilityScope.Own);
        var hiddenUser = User("cash-orders-hidden-user");
        db.Users.Add(hiddenUser);
        await db.SaveChangesAsync();

        var visibleClient = new Client { Name = "Visible cash client", ResponsibleUserId = user.Id };
        var hiddenClient = new Client { Name = "Hidden cash client", ResponsibleUserId = hiddenUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visibleOrder = CashOrder("PKO-VISIBLE", CashOrderType.Incoming, CashOrderStatus.Posted, 1_500m, new DateTime(2026, 5, 10));
        visibleOrder.ClientId = visibleClient.Id;
        var hiddenOrder = CashOrder("PKO-HIDDEN", CashOrderType.Incoming, CashOrderStatus.Posted, 9_000m, new DateTime(2026, 5, 11));
        hiddenOrder.ClientId = hiddenClient.Id;
        var ownManualOrder = CashOrder("RKO-OWN", CashOrderType.Outgoing, CashOrderStatus.Posted, 400m, new DateTime(2026, 5, 12));
        ownManualOrder.CreatedByUserId = user.Id;
        var otherManualOrder = CashOrder("RKO-HIDDEN", CashOrderType.Outgoing, CashOrderStatus.Posted, 700m, new DateTime(2026, 5, 13));
        otherManualOrder.CreatedByUserId = hiddenUser.Id;
        db.CashOrders.AddRange(visibleOrder, hiddenOrder, ownManualOrder, otherManualOrder);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.CashOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var report = Assert.IsType<CashOrderReportDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(2, report.OrderCount);
        Assert.Equal(1_500m, report.PostedIncomingTotal);
        Assert.Equal(400m, report.PostedOutgoingTotal);
        Assert.Equal(1_100m, report.PostedNetTotal);
    }

    [Fact]
    public async Task CashOrders_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: false,
            paymentsCanExport: false);
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.CashOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task CashOrdersExport_WithoutPaymentsExportPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: false);
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.ExportManagementReportExcel(
            "cash-orders",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task BankStatementImports_GroupsBatchesByStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.BankStatementImportBatches.AddRange(
            BankStatementImportBatch("may-ok.txt", BankStatementImportBatchStatus.Applied, 10, 8, 2, 0, 0, new DateTime(2026, 5, 1, 10, 0, 0)),
            BankStatementImportBatch("may-ok-2.txt", BankStatementImportBatchStatus.Applied, 2, 1, 1, 0, 0, new DateTime(2026, 5, 1, 15, 0, 0)),
            BankStatementImportBatch("may-errors.txt", BankStatementImportBatchStatus.AppliedWithErrors, 12, 7, 2, 3, 0, new DateTime(2026, 5, 2, 11, 0, 0)),
            BankStatementImportBatch("may-rolled.txt", BankStatementImportBatchStatus.RolledBack, 5, 5, 0, 0, 5, new DateTime(2026, 5, 3, 12, 0, 0)),
            BankStatementImportBatch("may-blocked.txt", BankStatementImportBatchStatus.RollbackBlocked, 4, 3, 0, 1, 0, new DateTime(2026, 5, 4, 13, 0, 0)),
            BankStatementImportBatch("old.txt", BankStatementImportBatchStatus.Applied, 99, 99, 0, 0, 0, new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.BankStatementImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<BankStatementImportReportDto>(ok.Value);
        Assert.Equal(5, report.BatchCount);
        Assert.Equal(33, report.RowsReadTotal);
        Assert.Equal(24, report.CreatedRowsTotal);
        Assert.Equal(5, report.SkippedRowsTotal);
        Assert.Equal(4, report.ErrorRowsTotal);
        Assert.Equal(5, report.RollbackRowsTotal);
        Assert.Equal(2, report.AppliedCount);
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
                Assert.Equal(20m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.RollbackBlocked, item.Status);
                Assert.Equal("Откат заблокирован", item.StatusLabel);
                Assert.Equal(4, item.RowsRead);
                Assert.Equal(1, item.ErrorRows);
                Assert.Equal(20m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.Applied, item.Status);
                Assert.Equal("Применен", item.StatusLabel);
                Assert.Equal(2, item.BatchCount);
                Assert.Equal(12, item.RowsRead);
                Assert.Equal(9, item.CreatedRows);
                Assert.Equal(3, item.SkippedRows);
                Assert.Equal(0, item.ErrorRows);
                Assert.Equal(40m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.RolledBack, item.Status);
                Assert.Equal("Откатан", item.StatusLabel);
                Assert.Equal(5, item.RollbackRows);
                Assert.Equal(20m, item.Share);
            });

        var batchQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"BankStatementImportBatches\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", batchQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", batchQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", batchQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BankStatementImports_AppliesRowFinancialFilters()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        var targetClient = new Client { Name = "Target Client" };
        var otherClient = new Client { Name = "Other Client" };
        var targetCase = new ClientCase { CaseNumber = "CASE-BANK-1", Client = targetClient };
        var otherCase = new ClientCase { CaseNumber = "CASE-BANK-2", Client = otherClient };
        var targetContract = Contract("BANK-CONTRACT-1", ContractStatus.Concluded, 1_500m, new DateTime(2026, 5, 1), false, new DateTime(2026, 12, 31));
        var otherContract = Contract("BANK-CONTRACT-2", ContractStatus.Concluded, 9_000m, new DateTime(2026, 5, 1), false, new DateTime(2026, 12, 31));
        var targetIncomeType = new IncomeType { Name = "Target bank income", PaymentType = PaymentType.Income };
        var otherIncomeType = new IncomeType { Name = "Other bank income", PaymentType = PaymentType.Income };
        var targetInvoice = new Invoice { Number = "INV-BANK-1", Date = new DateTime(2026, 5, 1) };
        var otherInvoice = new Invoice { Number = "INV-BANK-2", Date = new DateTime(2026, 5, 1) };
        db.AddRange(targetClient, otherClient, targetCase, otherCase, targetContract, otherContract, targetIncomeType, otherIncomeType, targetInvoice, otherInvoice);
        await db.SaveChangesAsync();

        var targetPayment = InvoicePayment("Main account", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 1_500m, 1_500m, new DateTime(2026, 5, 10));
        targetPayment.InvoiceId = targetInvoice.Id;
        targetPayment.ClientId = targetClient.Id;
        targetPayment.ClientCaseId = targetCase.Id;
        targetPayment.ContractId = targetContract.Id;
        targetPayment.IncomeTypeId = targetIncomeType.Id;
        targetPayment.CounterpartyName = "Target Client";
        var otherPayment = InvoicePayment("Other account", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 9_000m, 9_000m, new DateTime(2026, 5, 10));
        otherPayment.InvoiceId = otherInvoice.Id;
        otherPayment.ClientId = otherClient.Id;
        otherPayment.ClientCaseId = otherCase.Id;
        otherPayment.ContractId = otherContract.Id;
        otherPayment.IncomeTypeId = otherIncomeType.Id;
        otherPayment.CounterpartyName = "Other Client";
        db.Payments.AddRange(targetPayment, otherPayment);
        var targetOperation = Operation(
            MoneyOperationSource.Bank,
            MoneyOperationDirection.Income,
            MoneyOperationAccountingType.Fact,
            1_500m,
            new DateTime(2026, 5, 10),
            "Main account",
            targetIncomeType.Id,
            "Income",
            "Target Client",
            linkedInvoiceId: targetInvoice.Id);
        var otherOperation = Operation(
            MoneyOperationSource.Bank,
            MoneyOperationDirection.Income,
            MoneyOperationAccountingType.Fact,
            9_000m,
            new DateTime(2026, 5, 10),
            "Other account",
            otherIncomeType.Id,
            "Income",
            "Other Client",
            linkedInvoiceId: otherInvoice.Id);
        db.MoneyOperations.AddRange(targetOperation, otherOperation);
        await db.SaveChangesAsync();

        var batch = BankStatementImportBatch("filtered-bank.txt", BankStatementImportBatchStatus.AppliedWithErrors, 3, 2, 0, 1, 0, new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc));
        batch.Rows.Add(new BankStatementImportRow
        {
            RowNumber = 1,
            ImportKey = "target-row",
            Status = BankStatementImportRowStatus.Created,
            MoneyOperationId = targetOperation.Id,
            Date = new DateTime(2026, 5, 10),
            Direction = MoneyOperationDirection.Income,
            Amount = 1_500m,
            AccountName = "Main account",
            CounterpartyName = "Target Client",
            CreatedAt = new DateTime(2026, 5, 10, 10, 5, 0, DateTimeKind.Utc),
        });
        batch.Rows.Add(new BankStatementImportRow
        {
            RowNumber = 2,
            ImportKey = "target-row-2",
            Status = BankStatementImportRowStatus.Created,
            MoneyOperationId = targetOperation.Id,
            Date = new DateTime(2026, 5, 10),
            Direction = MoneyOperationDirection.Income,
            Amount = 1_500m,
            AccountName = "Main account",
            CounterpartyName = "Target Client",
            CreatedAt = new DateTime(2026, 5, 10, 10, 6, 0, DateTimeKind.Utc),
        });
        batch.Rows.Add(new BankStatementImportRow
        {
            RowNumber = 3,
            ImportKey = "other-row",
            Status = BankStatementImportRowStatus.Error,
            MoneyOperationId = otherOperation.Id,
            Date = new DateTime(2026, 5, 10),
            Direction = MoneyOperationDirection.Income,
            Amount = 9_000m,
            AccountName = "Other account",
            CounterpartyName = "Other Client",
            CreatedAt = new DateTime(2026, 5, 10, 10, 7, 0, DateTimeKind.Utc),
        });
        db.BankStatementImportBatches.Add(batch);
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.BankStatementImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None,
            accountName: "Main account",
            counterpartyName: "Target",
            clientId: targetClient.Id,
            caseId: targetCase.Id,
            contractId: targetContract.Id,
            incomeTypeId: targetIncomeType.Id);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<BankStatementImportReportDto>(ok.Value);
        Assert.Equal(1, report.BatchCount);
        Assert.Equal(2, report.RowsReadTotal);
        Assert.Equal(2, report.CreatedRowsTotal);
        Assert.Equal(0, report.ErrorRowsTotal);
        Assert.Equal(1, report.AppliedWithErrorsCount);
        var status = Assert.Single(report.Statuses);
        Assert.Equal(BankStatementImportBatchStatus.AppliedWithErrors, status.Status);
        Assert.Equal(1, status.BatchCount);
        Assert.Equal(2, status.RowsRead);
        Assert.Equal(2, status.CreatedRows);
        Assert.Equal(0, status.ErrorRows);
        Assert.Equal(100m, status.Share);

        var rowQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"BankStatementImportRows\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", rowQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", rowQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BankStatementImports_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: false,
            paymentsCanExport: true);
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.BankStatementImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task BankStatementImportsExport_WithoutPaymentsExportPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: false);
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.ExportManagementReportExcel(
            "bank-statement-imports",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task BankStatementImports_FiltersCreatedAtByProfileLocalDay()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithTimeZoneAsync(db, "Europe/Samara");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            ViewScope = DataVisibilityScope.All,
        });
        db.BankStatementImportBatches.AddRange(
            BankStatementImportBatch("local-may-first.txt", BankStatementImportBatchStatus.Applied, 10, 8, 2, 0, 0, new DateTime(2026, 4, 30, 21, 30, 0, DateTimeKind.Utc)),
            BankStatementImportBatch("local-may-second.txt", BankStatementImportBatchStatus.AppliedWithErrors, 20, 10, 5, 5, 0, new DateTime(2026, 5, 1, 20, 30, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.BankStatementImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 1),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<BankStatementImportReportDto>(ok.Value);
        Assert.Equal(1, report.BatchCount);
        Assert.Equal(10, report.RowsReadTotal);
        Assert.Equal(8, report.CreatedRowsTotal);
        Assert.Equal(0, report.ErrorRowsTotal);
        Assert.Collection(report.Statuses, item =>
        {
            Assert.Equal(BankStatementImportBatchStatus.Applied, item.Status);
            Assert.Equal(10, item.RowsRead);
        });
    }

    [Fact]
    public async Task SpreadsheetImports_GroupsBatchesByStatus()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
        db.SpreadsheetImportBatches.AddRange(
            SpreadsheetImportBatch("May OK", SpreadsheetImportBatchStatus.Applied, 2, 20, 10, 4, 5, 1, 0, new DateTime(2026, 5, 1, 10, 0, 0)),
            SpreadsheetImportBatch("May OK 2", SpreadsheetImportBatchStatus.Applied, 2, 4, 2, 1, 1, 0, 0, new DateTime(2026, 5, 1, 15, 0, 0)),
            SpreadsheetImportBatch("May Errors", SpreadsheetImportBatchStatus.AppliedWithErrors, 1, 12, 5, 2, 2, 3, 0, new DateTime(2026, 5, 2, 11, 0, 0)),
            SpreadsheetImportBatch("May Rolled", SpreadsheetImportBatchStatus.RolledBack, 1, 8, 4, 1, 2, 1, 4, new DateTime(2026, 5, 3, 12, 0, 0)),
            SpreadsheetImportBatch("May Conflicts", SpreadsheetImportBatchStatus.RolledBackWithConflicts, 3, 6, 2, 1, 1, 2, 2, new DateTime(2026, 5, 4, 13, 0, 0)),
            SpreadsheetImportBatch("Old", SpreadsheetImportBatchStatus.Applied, 5, 99, 99, 0, 0, 0, 0, new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db);

        var action = await controller.SpreadsheetImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<SpreadsheetImportReportDto>(ok.Value);
        Assert.Equal(5, report.BatchCount);
        Assert.Equal(9, report.FileCount);
        Assert.Equal(50, report.TotalRows);
        Assert.Equal(23, report.CreatedRows);
        Assert.Equal(9, report.UpdatedRows);
        Assert.Equal(11, report.SkippedRows);
        Assert.Equal(7, report.ErrorRows);
        Assert.Equal(6, report.RollbackRows);
        Assert.Equal(2, report.AppliedCount);
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
                Assert.Equal(20m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, item.Status);
                Assert.Equal("Откатан с конфликтами", item.StatusLabel);
                Assert.Equal(3, item.FileCount);
                Assert.Equal(6, item.TotalRows);
                Assert.Equal(2, item.RollbackRows);
                Assert.Equal(20m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.Applied, item.Status);
                Assert.Equal("Применен", item.StatusLabel);
                Assert.Equal(2, item.BatchCount);
                Assert.Equal(4, item.FileCount);
                Assert.Equal(24, item.TotalRows);
                Assert.Equal(12, item.CreatedRows);
                Assert.Equal(5, item.UpdatedRows);
                Assert.Equal(6, item.SkippedRows);
                Assert.Equal(40m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, item.Status);
                Assert.Equal("Откатан", item.StatusLabel);
                Assert.Equal(1, item.FileCount);
                Assert.Equal(4, item.RollbackRows);
                Assert.Equal(20m, item.Share);
            });

        var batchQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"SpreadsheetImportBatches\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", batchQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", batchQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", batchQuery, StringComparison.OrdinalIgnoreCase);

        var fileQuery = Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"SpreadsheetImportBatchFiles\"", StringComparison.Ordinal));
        Assert.Contains("GROUP BY", fileQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", fileQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpreadsheetImports_WithoutAdminAccess_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: true);
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.SpreadsheetImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task SpreadsheetImportsExport_WithoutAdminAccess_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: true);
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.ExportManagementReportExcel(
            "spreadsheet-imports",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task SpreadsheetImports_ForbidAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: true);
        var controller = CreateController(db, CreateUserPrincipal(user.Id, includeAdminRoleClaim: true));

        var listAction = await controller.SpreadsheetImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);
        var exportAction = await controller.ExportManagementReportExcel(
            "spreadsheet-imports",
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(listAction.Result);
        Assert.IsType<ForbidResult>(exportAction);
    }

    [Fact]
    public async Task SpreadsheetImports_ForbidAdminClaimWhenUserIsMissingInDatabase()
    {
        await using var db = await CreateContextAsync();
        await CreateReportsUserWithPaymentsPermissionAsync(
            db,
            reportsCanView: true,
            reportsCanExport: true,
            paymentsCanView: true,
            paymentsCanExport: true);
        var controller = CreateController(db, CreateUserPrincipal(987654, includeAdminRoleClaim: true));

        var action = await controller.SpreadsheetImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task SpreadsheetImports_FiltersCreatedAtByProfileLocalDay()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateAdminUserWithTimeZoneAsync(db, "Europe/Samara");
        db.SpreadsheetImportBatches.AddRange(
            SpreadsheetImportBatch("Local May First", SpreadsheetImportBatchStatus.Applied, 1, 10, 8, 1, 1, 0, 0, new DateTime(2026, 4, 30, 21, 30, 0, DateTimeKind.Utc)),
            SpreadsheetImportBatch("Local May Second", SpreadsheetImportBatchStatus.AppliedWithErrors, 1, 20, 10, 2, 3, 5, 0, new DateTime(2026, 5, 1, 20, 30, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var action = await controller.SpreadsheetImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 1),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<SpreadsheetImportReportDto>(ok.Value);
        Assert.Equal(1, report.BatchCount);
        Assert.Equal(1, report.FileCount);
        Assert.Equal(10, report.TotalRows);
        Assert.Equal(8, report.CreatedRows);
        Assert.Equal(0, report.ErrorRows);
        Assert.Collection(report.Statuses, item =>
        {
            Assert.Equal(SpreadsheetImportBatchStatus.Applied, item.Status);
            Assert.Equal(10, item.TotalRows);
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
        string? currency = "RUB",
        int? linkedInvoiceId = null)
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
            LinkedInvoiceId = linkedInvoiceId,
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

    private static User User(string prefix)
        => new()
        {
            Email = $"{prefix}-{Guid.NewGuid():N}@example.test",
            FullName = $"{prefix} user",
            IsActive = true,
            IsApproved = true,
            Role = new Role
            {
                Name = $"{prefix}-role-{Guid.NewGuid():N}",
                Description = $"{prefix} role",
            },
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

    private static ClaimsPrincipal CreateUserPrincipal(int userId, bool includeAdminRoleClaim = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (includeAdminRoleClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static async Task<User> CreateReportsUserWithTimeZoneAsync(PaymentContext db, string timeZoneId)
    {
        var role = new Role
        {
            Name = $"reports-timezone-{Guid.NewGuid():N}",
            Description = "Reports timezone test role",
        };
        var user = new User
        {
            Email = $"reports-timezone-{Guid.NewGuid():N}@example.test",
            FullName = "Reports Timezone User",
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
            ViewScope = DataVisibilityScope.All,
        });
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> CreateAdminUserWithTimeZoneAsync(PaymentContext db, string timeZoneId)
    {
        var role = new Role
        {
            Name = "admin",
            Description = "Admin test role",
        };
        var user = new User
        {
            Email = $"admin-timezone-{Guid.NewGuid():N}@example.test",
            FullName = "Admin Timezone User",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        db.UserSettings.Add(new UserSetting
        {
            User = user,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> CreateReportsUserWithPaymentsPermissionAsync(
        PaymentContext db,
        bool reportsCanView,
        bool reportsCanExport,
        bool paymentsCanView,
        bool paymentsCanExport,
        string paymentsViewScope = DataVisibilityScope.All)
    {
        var role = new Role
        {
            Name = $"reports-payments-{Guid.NewGuid():N}",
            Description = "Reports payments permission test role",
        };
        var user = new User
        {
            Email = $"reports-payments-{Guid.NewGuid():N}@example.test",
            FullName = "Reports payments permission user",
            IsActive = true,
            Role = role,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        db.RolePermissions.AddRange(
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Reports,
                CanView = reportsCanView,
                CanExport = reportsCanExport,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                Role = role,
                Section = RolePermissionCatalog.Payments,
                CanView = paymentsCanView,
                CanExport = paymentsCanExport,
                ViewScope = paymentsViewScope,
            });
        await db.SaveChangesAsync();
        return user;
    }

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

    private static Task<PaymentContext> CreateContextAsync()
        => SqliteTestContextFactory.CreateAsync();
}
