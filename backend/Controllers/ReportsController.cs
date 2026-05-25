using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using System.Globalization;
using System.Net;
using System.Text;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private const string CaseInactivityAutomationDaysSettingKey = "tasks.automation.caseControl.inactiveDays";
    private const string CaseInactivityAutomationEscalationDaysSettingKey = "tasks.automation.caseControl.escalationDays";

    private readonly PaymentContext _db;
    private readonly ILogger<ReportsController> _logger;
    private readonly IInvoiceDocumentPdfService? _pdfService;

    public ReportsController(PaymentContext db, ILogger<ReportsController> logger, IInvoiceDocumentPdfService? pdfService = null)
    {
        _db = db;
        _logger = logger;
        _pdfService = pdfService;
    }

    private async Task<ActionResult<TReport>> ExecuteVisibleReportAsync<TReport>(
        string section,
        Func<DataVisibilityAccess, CancellationToken, Task<TReport>> buildReport,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var visibility = await _db.ResolveDataVisibilityAsync(User, section, ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => buildReport(visibility, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    private async Task<bool> CanViewReportsAsync(CancellationToken ct)
    {
        if (User.IsInRole("admin"))
        {
            return true;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return false;
        }

        if (await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return true;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Reports, ct);
        return permission?.CanView == true;
    }

    private async Task<bool> CanExportReportsAsync(CancellationToken ct)
    {
        if (User.IsInRole("admin"))
        {
            return true;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return false;
        }

        if (await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return true;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Reports, ct);
        return permission?.CanExport == true;
    }

    [HttpGet("legal-work")]
    public async Task<ActionResult<LegalWorkReportDto>> LegalWork(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        CancellationToken ct)
    {
        var (start, end) = ResolveLegalWorkPeriod(from, to);

        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        var tasksVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        var accountsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (casesVisibility is null || tasksVisibility is null || accountsVisibility is null)
        {
            return Unauthorized();
        }

        var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildLegalWorkReportAsync(start, end, filters, casesVisibility, tasksVisibility, accountsVisibility, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("legal-work.csv")]
    public async Task<IActionResult> ExportLegalWorkCsv(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        CancellationToken ct)
    {
        var (report, error) = await BuildLegalWorkExportReportAsync(from, to, status, responsibleUserId, caseType, ct);
        if (error is not null)
        {
            return error;
        }

        return File(BuildLegalWorkCsv(report!), "text/csv; charset=utf-8", "legal-work-report.csv");
    }

    [HttpGet("legal-work.xls")]
    public async Task<IActionResult> ExportLegalWorkExcel(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        CancellationToken ct)
    {
        var (report, error) = await BuildLegalWorkExportReportAsync(from, to, status, responsibleUserId, caseType, ct);
        if (error is not null)
        {
            return error;
        }

        var fileName = $"legal-work-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.xls";
        return File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildLegalWorkExcelXml(report!))).ToArray(),
            "application/vnd.ms-excel; charset=utf-8",
            fileName);
    }

    [HttpGet("legal-work.pdf")]
    public async Task<IActionResult> ExportLegalWorkPdf(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        CancellationToken ct)
    {
        if (_pdfService is null)
        {
            return Problem("Сервис формирования PDF недоступен.");
        }

        var (report, error) = await BuildLegalWorkExportReportAsync(from, to, status, responsibleUserId, caseType, ct);
        if (error is not null)
        {
            return error;
        }

        var pdf = await _pdfService.RenderAsync(BuildLegalWorkPrintHtml(report!, DateTime.UtcNow), ct);
        return File(pdf, "application/pdf", $"legal-work-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf");
    }

    [HttpGet("case-finance")]
    public async Task<ActionResult<CaseFinanceReportDto>> CaseFinance(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        var accountsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (casesVisibility is null || accountsVisibility is null)
        {
            return Unauthorized();
        }

        var (start, end) = ResolveLegalWorkPeriod(from, to);
        var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCaseFinanceReportAsync(start, end, filters, effectiveLimit, casesVisibility, accountsVisibility, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("case-success")]
    public async Task<ActionResult<CaseSuccessReportDto>> CaseSuccess(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        if (casesVisibility is null)
        {
            return Unauthorized();
        }

        var (start, end) = ResolveLegalWorkPeriod(from, to);
        var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCaseSuccessReportAsync(start, end, filters, effectiveLimit, casesVisibility, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    private async Task<(LegalWorkReportDto? Report, IActionResult? Error)> BuildLegalWorkExportReportAsync(
        DateTime? from,
        DateTime? to,
        string? status,
        int? responsibleUserId,
        string? caseType,
        CancellationToken ct)
    {
        if (!await CanExportReportsAsync(ct))
        {
            return (null, Forbid());
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        var tasksVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        var accountsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Accounts, ct);
        if (casesVisibility is null || tasksVisibility is null || accountsVisibility is null)
        {
            return (null, Unauthorized());
        }

        var (start, end) = ResolveLegalWorkPeriod(from, to);
        var filters = new LegalWorkReportFilters(NormalizeOptional(status), responsibleUserId, NormalizeOptional(caseType));
        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildLegalWorkReportAsync(start, end, filters, casesVisibility, tasksVisibility, accountsVisibility, cancellationToken),
            _logger,
            ct);

        return (report, null);
    }

    private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        return end < start ? (end, start) : (start, end);
    }

    [HttpGet("case-activity")]
    public async Task<ActionResult<CaseActivityReportDto>> CaseActivity(
        [FromQuery] int? inactiveDays,
        [FromQuery] int? escalationDays,
        [FromQuery] string? activity,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        if (casesVisibility is null)
        {
            return Unauthorized();
        }

        var settings = await GetCaseActivityReportSettingsAsync(ct);
        var effectiveInactiveDays = Math.Clamp(inactiveDays ?? settings.InactivityDays, 1, 365);
        var effectiveEscalationDays = Math.Clamp(escalationDays ?? settings.EscalationDays, effectiveInactiveDays, 365);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 200);
        var normalizedActivity = NormalizeCaseActivityFilter(activity);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCaseActivityReportAsync(
                effectiveInactiveDays,
                effectiveEscalationDays,
                normalizedActivity,
                effectiveLimit,
                casesVisibility,
                cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("current-cases")]
    public async Task<ActionResult<CurrentCasesReportDto>> CurrentCases(
        [FromQuery] DateTime? asOf,
        [FromQuery] string? status,
        [FromQuery] int? responsibleUserId,
        [FromQuery] string? caseType,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        var tasksVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        if (casesVisibility is null || tasksVisibility is null)
        {
            return Unauthorized();
        }

        var effectiveAsOf = asOf?.Date ?? DateTime.UtcNow.Date;
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);
        var filters = new CurrentCasesReportFilters(
            NormalizeOptional(status),
            responsibleUserId,
            NormalizeOptional(caseType));

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCurrentCasesReportAsync(
                effectiveAsOf,
                filters,
                effectiveLimit,
                casesVisibility,
                tasksVisibility,
                cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("lawyer-workload")]
    public async Task<ActionResult<LawyerWorkloadReportDto>> LawyerWorkload(
        [FromQuery] DateTime? asOf,
        [FromQuery] int? horizonDays,
        [FromQuery] int? lawyerUserId,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        var tasksVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        if (casesVisibility is null || tasksVisibility is null)
        {
            return Unauthorized();
        }

        var effectiveAsOf = asOf?.Date ?? DateTime.UtcNow.Date;
        var effectiveHorizonDays = Math.Clamp(horizonDays ?? 7, 1, 60);
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildLawyerWorkloadReportAsync(
                effectiveAsOf,
                effectiveHorizonDays,
                lawyerUserId,
                effectiveLimit,
                casesVisibility,
                tasksVisibility,
                cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("overdue-deadlines")]
    public async Task<ActionResult<OverdueProceduralDeadlinesReportDto>> OverdueDeadlines(
        [FromQuery] DateTime? asOf,
        [FromQuery] string? deadlineType,
        [FromQuery] int? responsibleUserId,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        if (casesVisibility is null)
        {
            return Unauthorized();
        }

        var effectiveAsOf = (asOf?.Date ?? DateTime.UtcNow.Date);
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 500);
        var normalizedDeadlineType = NormalizeOptional(deadlineType);

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildOverdueProceduralDeadlinesReportAsync(
                effectiveAsOf,
                normalizedDeadlineType,
                responsibleUserId,
                effectiveLimit,
                casesVisibility,
                cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("cash-flow")]
    public async Task<ActionResult<CashFlowReportDto>> CashFlow(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] string? accountName,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var normalizedAccountName = string.IsNullOrWhiteSpace(accountName)
            ? null
            : accountName.Trim();

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCashFlowReportAsync(
                start,
                end,
                source,
                normalizedAccountName,
                visibility,
                cancellationToken),
            ct);
    }

    [HttpGet("categories")]
    public async Task<ActionResult<MoneyCategoryReportDto>> Categories(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCategoryReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("counterparties")]
    public async Task<ActionResult<CounterpartyReportDto>> Counterparties(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCounterpartyReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("projects")]
    public async Task<ActionResult<ProjectReportDto>> Projects(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildProjectReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("directions")]
    public async Task<ActionResult<DirectionReportDto>> Directions(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildDirectionReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("sales-points")]
    public async Task<ActionResult<SalesPointReportDto>> SalesPoints(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildSalesPointReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("vat")]
    public async Task<ActionResult<VatReportDto>> Vat(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildVatReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("plan-fact")]
    public async Task<ActionResult<PlanFactReportDto>> PlanFact(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPlanFactReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("advances")]
    public async Task<ActionResult<AdvanceReportDto>> Advances(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildAdvanceReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("payment-priorities")]
    public async Task<ActionResult<PaymentPriorityReportDto>> PaymentPriorities(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaymentPriorityReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("paid-accounts")]
    public async Task<ActionResult<PaidAccountReportDto>> PaidAccounts(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaidAccountReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("paid-invoices")]
    public async Task<ActionResult<PaidInvoiceReportDto>> PaidInvoices(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaidInvoiceReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("invoice-statuses")]
    public async Task<ActionResult<InvoiceStatusReportDto>> InvoiceStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Accounts,
            (visibility, cancellationToken) => BuildInvoiceStatusReportAsync(start, end, visibility, cancellationToken),
            ct);
    }

    [HttpGet("act-statuses")]
    public async Task<ActionResult<ActStatusReportDto>> ActStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Acts,
            (visibility, cancellationToken) => BuildActStatusReportAsync(start, end, visibility, cancellationToken),
            ct);
    }

    [HttpGet("contract-statuses")]
    public async Task<ActionResult<ContractStatusReportDto>> ContractStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Contracts,
            (visibility, cancellationToken) => BuildContractStatusReportAsync(start, end, visibility, cancellationToken),
            ct);
    }

    [HttpGet("payment-orders")]
    public async Task<ActionResult<PaymentOrderReportDto>> PaymentOrders(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaymentOrderReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("payment-purposes")]
    public async Task<ActionResult<PaymentPurposeReportDto>> PaymentPurposes(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildPaymentPurposeReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("match-statuses")]
    public async Task<ActionResult<MatchStatusReportDto>> MatchStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildMatchStatusReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("journal-statuses")]
    public async Task<ActionResult<JournalStatusReportDto>> JournalStatuses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildJournalStatusReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("currencies")]
    public async Task<ActionResult<CurrencyReportDto>> Currencies(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return await ExecuteVisibleReportAsync(
            RolePermissionCatalog.Payments,
            (visibility, cancellationToken) => BuildCurrencyReportAsync(start, end, source, visibility, cancellationToken),
            ct);
    }

    [HttpGet("cash-orders")]
    public async Task<ActionResult<CashOrderReportDto>> CashOrders(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildCashOrderReportAsync(start, end, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("bank-statement-imports")]
    public async Task<ActionResult<BankStatementImportReportDto>> BankStatementImports(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildBankStatementImportReportAsync(start, end, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    [HttpGet("spreadsheet-imports")]
    public async Task<ActionResult<SpreadsheetImportReportDto>> SpreadsheetImports(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        if (!await CanViewReportsAsync(ct))
        {
            return Forbid();
        }

        var report = await SqliteRetryHelper.ExecuteAsync(
            cancellationToken => BuildSpreadsheetImportReportAsync(start, end, cancellationToken),
            _logger,
            ct);

        return Ok(report);
    }

    private async Task<LegalWorkReportDto> BuildLegalWorkReportAsync(
        DateTime start,
        DateTime end,
        LegalWorkReportFilters filters,
        DataVisibilityAccess casesVisibility,
        DataVisibilityAccess tasksVisibility,
        DataVisibilityAccess accountsVisibility,
        CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var endInclusive = end.Date.AddDays(1).AddTicks(-1);

        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.CreatedAt <= endInclusive
                && (item.Status != ClientCaseStatus.Closed && item.Status != ClientCaseStatus.Lost
                    || (item.StartDate.HasValue && item.StartDate.Value >= start.Date)
                    || (item.DueDate.HasValue && item.DueDate.Value >= start.Date)));

        if (TryParseClientCaseStatus(filters.Status, out var statusFilter))
        {
            caseQuery = caseQuery.Where(item => item.Status == statusFilter);
        }

        if (filters.ResponsibleUserId.HasValue)
        {
            caseQuery = caseQuery.Where(item => item.ResponsibleUserId == filters.ResponsibleUserId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filters.CaseType))
        {
            caseQuery = caseQuery.Where(item => item.CaseType != null && item.CaseType.Contains(filters.CaseType));
        }

        var cases = await caseQuery
            .ToListAsync(ct);

        var caseIds = cases.Select(item => item.Id).Distinct().ToArray();

        var tasks = caseIds.Length == 0
            ? new List<LegalWorkTaskRow>()
            : await _db.TaskJournalItems
                .AsNoTracking()
                .ApplyTaskVisibility(tasksVisibility)
                .Where(item => item.ClientCaseId.HasValue && caseIds.Contains(item.ClientCaseId.Value))
                .Select(item => new LegalWorkTaskRow(
                    item.Id,
                    item.ClientCaseId,
                    item.AssigneeUserId,
                    item.AssigneeName,
                    item.Status,
                    item.Priority,
                    item.DueAt,
                    item.PlannedDurationMinutes))
                .ToListAsync(ct);

        var payments = await BuildLegalWorkPaymentRowsAsync(caseIds, accountsVisibility, ct);
        var paidByCase = payments
            .GroupBy(item => item.CaseId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => ResolvePaidAmount(item.Amount, item.PaidAmount, item.Status)));

        var activeCases = cases.Where(item => !IsFinishedCaseStatus(item.Status)).ToList();
        var openTasks = tasks.Where(item => IsOpenTaskStatus(item.Status)).ToList();
        var overdueTasks = openTasks.Where(item => item.DueAt.Date < today).ToList();
        var onApprovalTasks = openTasks
            .Where(item => string.Equals(item.Status, "onApproval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var deadlines = activeCases
            .SelectMany(item => BuildLegalWorkDeadlines(item, today))
            .ToList();
        var overdueDeadlines = deadlines
            .Where(item => item.DueAt.Date < today)
            .OrderBy(item => item.DueAt)
            .ThenBy(item => item.CaseTitle)
            .ToList();
        var upcomingDeadlines = deadlines
            .Where(item => item.DueAt.Date >= today && item.DueAt.Date <= today.AddDays(7))
            .OrderBy(item => item.DueAt)
            .ThenBy(item => item.CaseTitle)
            .ToList();

        var overdueDeadlineCountsByCase = overdueDeadlines
            .GroupBy(item => item.CaseId)
            .ToDictionary(group => group.Key, group => group.Count());
        var upcomingDeadlineCountsByCase = upcomingDeadlines
            .GroupBy(item => item.CaseId)
            .ToDictionary(group => group.Key, group => group.Count());

        var openTaskCountsByCase = openTasks
            .Where(item => item.ClientCaseId.HasValue)
            .GroupBy(item => item.ClientCaseId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var overdueTaskCountsByCase = overdueTasks
            .Where(item => item.ClientCaseId.HasValue)
            .GroupBy(item => item.ClientCaseId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var onApprovalTaskCountsByCase = onApprovalTasks
            .Where(item => item.ClientCaseId.HasValue)
            .GroupBy(item => item.ClientCaseId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var feeTotal = cases.Sum(item => item.FeeAmount);
        var paidTotal = paidByCase.Values.Sum();
        var outstandingTotal = cases.Sum(item => Math.Max(0m, item.FeeAmount - paidByCase.GetValueOrDefault(item.Id)));
        var caseRows = BuildLegalWorkCaseItems(
            cases,
            paidByCase,
            openTaskCountsByCase,
            overdueTaskCountsByCase,
            onApprovalTaskCountsByCase,
            overdueDeadlineCountsByCase,
            upcomingDeadlineCountsByCase);

        return new LegalWorkReportDto
        {
            From = start,
            To = end,
            StatusFilter = filters.Status ?? string.Empty,
            ResponsibleUserIdFilter = filters.ResponsibleUserId,
            CaseTypeFilter = filters.CaseType ?? string.Empty,
            TotalCases = cases.Count,
            ActiveCases = activeCases.Count(item => item.Status == ClientCaseStatus.Open),
            ClosedCases = cases.Count(item => item.Status == ClientCaseStatus.Closed),
            LostCases = cases.Count(item => item.Status == ClientCaseStatus.Lost),
            OnHoldCases = cases.Count(item => item.Status == ClientCaseStatus.OnHold),
            OverdueDeadlineCount = overdueDeadlines.Count,
            UpcomingDeadlineCount = upcomingDeadlines.Count,
            OpenTaskCount = openTasks.Count,
            OverdueTaskCount = overdueTasks.Count,
            OnApprovalTaskCount = onApprovalTasks.Count,
            FeeTotal = feeTotal,
            PaidTotal = paidTotal,
            OutstandingTotal = outstandingTotal,
            CasesByStatus = cases
                .GroupBy(item => item.Status)
                .OrderBy(group => group.Key)
                .Select(group => new LegalWorkCaseStatusItemDto
                {
                    Status = group.Key.ToString(),
                    StatusLabel = FormatCaseStatus(group.Key),
                    Count = group.Count(),
                    FeeTotal = group.Sum(item => item.FeeAmount),
                    OverdueDeadlines = group.Sum(item => overdueDeadlineCountsByCase.GetValueOrDefault(item.Id)),
                })
                .ToList(),
            CasesByType = cases
                .GroupBy(item => NormalizeReportText(item.CaseType, "Не указан"))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => new LegalWorkCaseTypeItemDto
                {
                    CaseType = group.Key,
                    Count = group.Count(),
                    FeeTotal = group.Sum(item => item.FeeAmount),
                    ActiveTasks = group.Sum(item => openTaskCountsByCase.GetValueOrDefault(item.Id)),
                })
                .ToList(),
            Responsibles = BuildLegalWorkResponsibleItems(cases, openTasks, overdueTasks, onApprovalTasks, overdueDeadlines, paidByCase),
            FinanceByStatus = caseRows
                .GroupBy(item => new { item.Status, item.StatusLabel })
                .OrderBy(group => group.Key.Status)
                .Select(group => new LegalWorkFinanceItemDto
                {
                    Status = group.Key.Status,
                    StatusLabel = group.Key.StatusLabel,
                    CaseCount = group.Count(),
                    FeeTotal = group.Sum(item => item.FeeAmount),
                    PaidTotal = group.Sum(item => item.PaidTotal),
                    OutstandingTotal = group.Sum(item => item.OutstandingTotal),
                })
                .ToList(),
            DeadlinesByType = deadlines
                .GroupBy(item => new { item.DeadlineType, item.DeadlineLabel })
                .OrderBy(group => group.Key.DeadlineType)
                .Select(group => new LegalWorkDeadlineSummaryItemDto
                {
                    DeadlineType = group.Key.DeadlineType,
                    DeadlineLabel = group.Key.DeadlineLabel,
                    TotalCount = group.Count(),
                    OverdueCount = group.Count(item => item.DueAt.Date < today),
                    UpcomingCount = group.Count(item => item.DueAt.Date >= today && item.DueAt.Date <= today.AddDays(7)),
                })
                .ToList(),
            Cases = caseRows
                .OrderByDescending(item => item.OverdueDeadlines)
                .ThenByDescending(item => item.OverdueTasks)
                .ThenBy(item => item.DueDate ?? DateTime.MaxValue)
                .ThenBy(item => item.CaseTitle)
                .Take(100)
                .ToList(),
            OverdueDeadlines = overdueDeadlines.Take(15).ToList(),
            UpcomingDeadlines = upcomingDeadlines.Take(15).ToList(),
        };
    }

    private async Task<CaseFinanceReportDto> BuildCaseFinanceReportAsync(
        DateTime start,
        DateTime end,
        LegalWorkReportFilters filters,
        int limit,
        DataVisibilityAccess casesVisibility,
        DataVisibilityAccess accountsVisibility,
        CancellationToken ct)
    {
        var endInclusive = end.Date.AddDays(1).AddTicks(-1);
        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.CreatedAt <= endInclusive
                && (item.Status != ClientCaseStatus.Closed && item.Status != ClientCaseStatus.Lost
                    || (item.StartDate.HasValue && item.StartDate.Value >= start.Date)
                    || (item.DueDate.HasValue && item.DueDate.Value >= start.Date)));

        if (TryParseClientCaseStatus(filters.Status, out var statusFilter))
        {
            caseQuery = caseQuery.Where(item => item.Status == statusFilter);
        }

        if (filters.ResponsibleUserId.HasValue)
        {
            caseQuery = caseQuery.Where(item => item.ResponsibleUserId == filters.ResponsibleUserId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filters.CaseType))
        {
            caseQuery = caseQuery.Where(item => item.CaseType != null && item.CaseType.Contains(filters.CaseType));
        }

        var cases = await caseQuery.ToListAsync(ct);
        var caseIds = cases.Select(item => item.Id).Distinct().ToArray();
        var payments = await BuildLegalWorkPaymentRowsAsync(caseIds, accountsVisibility, ct);
        var paidByCase = payments
            .GroupBy(item => item.CaseId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => ResolvePaidAmount(item.Amount, item.PaidAmount, item.Status)));

        var rows = cases
            .Select(item => BuildCaseFinanceItem(item, paidByCase.GetValueOrDefault(item.Id)))
            .OrderByDescending(item => item.OutstandingTotal)
            .ThenBy(item => item.DueDate ?? DateTime.MaxValue)
            .ThenBy(item => item.CaseTitle)
            .ToList();

        var feeTotal = rows.Sum(item => item.FeeAmount);
        var paidTotal = rows.Sum(item => item.PaidTotal);
        var outstandingTotal = rows.Sum(item => item.OutstandingTotal);

        return new CaseFinanceReportDto
        {
            GeneratedAt = DateTime.UtcNow,
            From = start,
            To = end,
            StatusFilter = filters.Status ?? string.Empty,
            ResponsibleUserIdFilter = filters.ResponsibleUserId,
            CaseTypeFilter = filters.CaseType ?? string.Empty,
            Limit = limit,
            TotalCases = rows.Count,
            ActiveCases = rows.Count(item => item.Status == nameof(ClientCaseStatus.Open) || item.Status == nameof(ClientCaseStatus.OnHold)),
            ClosedCases = rows.Count(item => item.Status == nameof(ClientCaseStatus.Closed) || item.Status == nameof(ClientCaseStatus.Lost)),
            FeeTotal = feeTotal,
            PaidTotal = paidTotal,
            OutstandingTotal = outstandingTotal,
            CollectionRate = CalculatePercent(paidTotal, feeTotal),
            FullyPaidCaseCount = rows.Count(item => item.FinanceState == "paid"),
            PartiallyPaidCaseCount = rows.Count(item => item.FinanceState == "partial"),
            UnpaidCaseCount = rows.Count(item => item.FinanceState == "unpaid"),
            OverpaidCaseCount = rows.Count(item => item.FinanceState == "overpaid"),
            ByStatus = rows
                .GroupBy(item => new { item.Status, item.StatusLabel })
                .OrderBy(group => group.Key.Status)
                .Select(group =>
                {
                    var groupFee = group.Sum(item => item.FeeAmount);
                    var groupPaid = group.Sum(item => item.PaidTotal);
                    return new CaseFinanceStatusItemDto
                    {
                        Status = group.Key.Status,
                        StatusLabel = group.Key.StatusLabel,
                        CaseCount = group.Count(),
                        FeeTotal = groupFee,
                        PaidTotal = groupPaid,
                        OutstandingTotal = group.Sum(item => item.OutstandingTotal),
                        CollectionRate = CalculatePercent(groupPaid, groupFee),
                    };
                })
                .ToList(),
            ByResponsible = rows
                .GroupBy(item => new { item.ResponsibleUserId, item.ResponsibleName })
                .OrderByDescending(group => group.Sum(item => item.OutstandingTotal))
                .ThenBy(group => group.Key.ResponsibleName)
                .Select(group =>
                {
                    var groupFee = group.Sum(item => item.FeeAmount);
                    var groupPaid = group.Sum(item => item.PaidTotal);
                    return new CaseFinanceResponsibleItemDto
                    {
                        ResponsibleUserId = group.Key.ResponsibleUserId,
                        ResponsibleName = group.Key.ResponsibleName,
                        CaseCount = group.Count(),
                        FeeTotal = groupFee,
                        PaidTotal = groupPaid,
                        OutstandingTotal = group.Sum(item => item.OutstandingTotal),
                        CollectionRate = CalculatePercent(groupPaid, groupFee),
                    };
                })
                .Take(25)
                .ToList(),
            Items = rows.Take(limit).ToList(),
        };
    }

    private async Task<CaseSuccessReportDto> BuildCaseSuccessReportAsync(
        DateTime start,
        DateTime end,
        LegalWorkReportFilters filters,
        int limit,
        DataVisibilityAccess casesVisibility,
        CancellationToken ct)
    {
        var asOf = DateTime.UtcNow.Date;
        var endInclusive = end.Date.AddDays(1).AddTicks(-1);
        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.CreatedAt <= endInclusive
                && (item.Status != ClientCaseStatus.Closed && item.Status != ClientCaseStatus.Lost
                    || (item.StartDate.HasValue && item.StartDate.Value >= start.Date)
                    || (item.DueDate.HasValue && item.DueDate.Value >= start.Date)));

        if (TryParseClientCaseStatus(filters.Status, out var statusFilter))
        {
            caseQuery = caseQuery.Where(item => item.Status == statusFilter);
        }

        if (filters.ResponsibleUserId.HasValue)
        {
            caseQuery = caseQuery.Where(item => item.ResponsibleUserId == filters.ResponsibleUserId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filters.CaseType))
        {
            caseQuery = caseQuery.Where(item => item.CaseType != null && item.CaseType.Contains(filters.CaseType));
        }

        var rows = (await caseQuery.ToListAsync(ct))
            .Select(item => BuildCaseSuccessItem(item, asOf))
            .OrderByDescending(item => item.IsFinished)
            .ThenBy(item => item.Status)
            .ThenBy(item => item.CaseTitle)
            .ToList();

        var successfulCases = rows.Count(item => item.OutcomeCategory == "successful");
        var lostCases = rows.Count(item => item.OutcomeCategory == "lost");
        var completedCases = successfulCases + lostCases;
        var inProgressCases = rows.Count(item => item.OutcomeCategory == "inProgress");
        var onHoldCases = rows.Count(item => item.OutcomeCategory == "onHold");

        return new CaseSuccessReportDto
        {
            GeneratedAt = DateTime.UtcNow,
            From = start,
            To = end,
            StatusFilter = filters.Status ?? string.Empty,
            ResponsibleUserIdFilter = filters.ResponsibleUserId,
            CaseTypeFilter = filters.CaseType ?? string.Empty,
            Limit = limit,
            TotalCases = rows.Count,
            CompletedCases = completedCases,
            SuccessfulCases = successfulCases,
            LostCases = lostCases,
            InProgressCases = inProgressCases,
            OnHoldCases = onHoldCases,
            SuccessRate = CalculatePercent(successfulCases, completedCases),
            LossRate = CalculatePercent(lostCases, completedCases),
            CompletionRate = CalculatePercent(completedCases, rows.Count),
            FormulaNote = "Первая версия считает успешность по текущим статусам: Closed = закрыто без отрицательного результата, Lost = отрицательный исход. Открытые и приостановленные дела не входят в процент успешности.",
            ByStatus = rows
                .GroupBy(item => new { item.Status, item.StatusLabel })
                .OrderBy(group => group.Key.Status)
                .Select(group => new CaseSuccessStatusItemDto
                {
                    Status = group.Key.Status,
                    StatusLabel = group.Key.StatusLabel,
                    CaseCount = group.Count(),
                    Share = CalculatePercent(group.Count(), rows.Count),
                })
                .ToList(),
            ByResponsible = rows
                .GroupBy(item => new { item.ResponsibleUserId, item.ResponsibleName })
                .OrderByDescending(group => group.Count(item => item.OutcomeCategory == "lost"))
                .ThenByDescending(group => group.Count())
                .ThenBy(group => group.Key.ResponsibleName)
                .Select(group =>
                {
                    var groupSuccessful = group.Count(item => item.OutcomeCategory == "successful");
                    var groupLost = group.Count(item => item.OutcomeCategory == "lost");
                    var groupCompleted = groupSuccessful + groupLost;
                    return new CaseSuccessResponsibleItemDto
                    {
                        ResponsibleUserId = group.Key.ResponsibleUserId,
                        ResponsibleName = group.Key.ResponsibleName,
                        TotalCases = group.Count(),
                        CompletedCases = groupCompleted,
                        SuccessfulCases = groupSuccessful,
                        LostCases = groupLost,
                        InProgressCases = group.Count(item => !item.IsFinished),
                        SuccessRate = CalculatePercent(groupSuccessful, groupCompleted),
                    };
                })
                .Take(25)
                .ToList(),
            ByType = rows
                .GroupBy(item => item.CaseType)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group =>
                {
                    var groupSuccessful = group.Count(item => item.OutcomeCategory == "successful");
                    var groupLost = group.Count(item => item.OutcomeCategory == "lost");
                    var groupCompleted = groupSuccessful + groupLost;
                    return new CaseSuccessTypeItemDto
                    {
                        CaseType = group.Key,
                        TotalCases = group.Count(),
                        CompletedCases = groupCompleted,
                        SuccessfulCases = groupSuccessful,
                        LostCases = groupLost,
                        SuccessRate = CalculatePercent(groupSuccessful, groupCompleted),
                    };
                })
                .Take(25)
                .ToList(),
            Items = rows.Take(limit).ToList(),
        };
    }

    private static CaseSuccessItemDto BuildCaseSuccessItem(ClientCase clientCase, DateTime asOf)
    {
        var (outcomeCategory, outcomeLabel) = ResolveCaseSuccessOutcome(clientCase.Status);
        var start = clientCase.StartDate ?? clientCase.CreatedAt;
        var finishAnchor = clientCase.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost
            ? clientCase.DueDate ?? asOf
            : asOf;
        var daysInWork = Math.Max(0, (finishAnchor.Date - start.Date).Days);

        return new CaseSuccessItemDto
        {
            CaseId = clientCase.Id,
            CaseNumber = clientCase.CaseNumber,
            CaseTitle = clientCase.Title,
            ClientName = ResolveCaseOwnerName(clientCase),
            CaseType = NormalizeReportText(clientCase.CaseType, "Не указан"),
            Status = clientCase.Status.ToString(),
            StatusLabel = FormatCaseStatus(clientCase.Status),
            OutcomeCategory = outcomeCategory,
            OutcomeLabel = outcomeLabel,
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = ResolveCaseResponsibleName(clientCase),
            CreatedAt = clientCase.CreatedAt,
            StartDate = clientCase.StartDate,
            DueDate = clientCase.DueDate,
            DaysInWork = daysInWork,
            IsFinished = clientCase.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost,
        };
    }

    private static (string Category, string Label) ResolveCaseSuccessOutcome(ClientCaseStatus status)
        => status switch
        {
            ClientCaseStatus.Closed => ("successful", "Закрыто без отрицательного результата"),
            ClientCaseStatus.Lost => ("lost", "Отрицательный исход"),
            ClientCaseStatus.OnHold => ("onHold", "Приостановлено"),
            _ => ("inProgress", "В работе"),
        };

    private static CaseFinanceItemDto BuildCaseFinanceItem(ClientCase clientCase, decimal paid)
    {
        var fee = Math.Max(0m, clientCase.FeeAmount);
        var outstanding = Math.Max(0m, fee - paid);
        var paymentRate = CalculatePercent(paid, fee);
        var (financeState, financeStateLabel) = ResolveCaseFinanceState(fee, paid, outstanding);

        return new CaseFinanceItemDto
        {
            CaseId = clientCase.Id,
            CaseNumber = clientCase.CaseNumber,
            CaseTitle = clientCase.Title,
            ClientName = ResolveCaseOwnerName(clientCase),
            CaseType = NormalizeReportText(clientCase.CaseType, "Не указан"),
            Status = clientCase.Status.ToString(),
            StatusLabel = FormatCaseStatus(clientCase.Status),
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = ResolveCaseResponsibleName(clientCase),
            CreatedAt = clientCase.CreatedAt,
            StartDate = clientCase.StartDate,
            DueDate = clientCase.DueDate,
            FeeAmount = fee,
            PaidTotal = paid,
            OutstandingTotal = outstanding,
            PaymentRate = paymentRate,
            FinanceState = financeState,
            FinanceStateLabel = financeStateLabel,
        };
    }

    private static (string State, string Label) ResolveCaseFinanceState(decimal fee, decimal paid, decimal outstanding)
    {
        if (fee <= 0m)
        {
            return ("noFee", "Без гонорара");
        }

        if (paid > fee)
        {
            return ("overpaid", "Переплата");
        }

        if (outstanding <= 0m)
        {
            return ("paid", "Оплачено");
        }

        if (paid > 0m)
        {
            return ("partial", "Частично оплачено");
        }

        return ("unpaid", "Не оплачено");
    }

    private static IReadOnlyList<LegalWorkCaseItemDto> BuildLegalWorkCaseItems(
        IReadOnlyCollection<ClientCase> cases,
        IReadOnlyDictionary<int, decimal> paidByCase,
        IReadOnlyDictionary<int, int> openTaskCountsByCase,
        IReadOnlyDictionary<int, int> overdueTaskCountsByCase,
        IReadOnlyDictionary<int, int> onApprovalTaskCountsByCase,
        IReadOnlyDictionary<int, int> overdueDeadlineCountsByCase,
        IReadOnlyDictionary<int, int> upcomingDeadlineCountsByCase)
        => cases
            .Select(clientCase =>
            {
                var paid = paidByCase.GetValueOrDefault(clientCase.Id);
                return new LegalWorkCaseItemDto
                {
                    CaseId = clientCase.Id,
                    CaseNumber = clientCase.CaseNumber,
                    CaseTitle = clientCase.Title,
                    CaseType = NormalizeReportText(clientCase.CaseType, "Не указан"),
                    ClientName = ResolveCaseOwnerName(clientCase),
                    Status = clientCase.Status.ToString(),
                    StatusLabel = FormatCaseStatus(clientCase.Status),
                    ResponsibleUserId = clientCase.ResponsibleUserId,
                    ResponsibleName = ResolveCaseResponsibleName(clientCase),
                    CreatedAt = clientCase.CreatedAt,
                    StartDate = clientCase.StartDate,
                    DueDate = clientCase.DueDate,
                    FeeAmount = clientCase.FeeAmount,
                    PaidTotal = paid,
                    OutstandingTotal = Math.Max(0m, clientCase.FeeAmount - paid),
                    OpenTasks = openTaskCountsByCase.GetValueOrDefault(clientCase.Id),
                    OverdueTasks = overdueTaskCountsByCase.GetValueOrDefault(clientCase.Id),
                    OnApprovalTasks = onApprovalTaskCountsByCase.GetValueOrDefault(clientCase.Id),
                    OverdueDeadlines = overdueDeadlineCountsByCase.GetValueOrDefault(clientCase.Id),
                    UpcomingDeadlines = upcomingDeadlineCountsByCase.GetValueOrDefault(clientCase.Id),
                };
            })
            .ToList();

    private static byte[] BuildLegalWorkCsv(LegalWorkReportDto report)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, [
            "Раздел",
            "Дело",
            "Клиент",
            "Ответственный",
            "Статус",
            "Тип дела",
            "Период",
            "Гонорар",
            "Оплачено",
            "Остаток",
            "Открытые задачи",
            "Просроченные задачи",
            "Просроченные сроки",
            "Ближайшие сроки"
        ]);

        AppendCsvRow(builder, [
            "Сводка",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            $"{report.From:yyyy-MM-dd} - {report.To:yyyy-MM-dd}",
            report.FeeTotal.ToString("0.##"),
            report.PaidTotal.ToString("0.##"),
            report.OutstandingTotal.ToString("0.##"),
            report.OpenTaskCount.ToString(),
            report.OverdueTaskCount.ToString(),
            report.OverdueDeadlineCount.ToString(),
            report.UpcomingDeadlineCount.ToString()
        ]);

        foreach (var item in report.Cases)
        {
            AppendCsvRow(builder, [
                "Дела",
                BuildCaseCsvTitle(item),
                item.ClientName,
                item.ResponsibleName,
                item.StatusLabel,
                item.CaseType,
                $"{item.CreatedAt:yyyy-MM-dd}",
                item.FeeAmount.ToString("0.##"),
                item.PaidTotal.ToString("0.##"),
                item.OutstandingTotal.ToString("0.##"),
                item.OpenTasks.ToString(),
                item.OverdueTasks.ToString(),
                item.OverdueDeadlines.ToString(),
                item.UpcomingDeadlines.ToString()
            ]);
        }

        foreach (var item in report.FinanceByStatus)
        {
            AppendCsvRow(builder, [
                "Финансы по статусам",
                item.StatusLabel,
                string.Empty,
                string.Empty,
                item.Status,
                string.Empty,
                $"{item.CaseCount} дел",
                item.FeeTotal.ToString("0.##"),
                item.PaidTotal.ToString("0.##"),
                item.OutstandingTotal.ToString("0.##"),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty
            ]);
        }

        foreach (var item in report.DeadlinesByType)
        {
            AppendCsvRow(builder, [
                "Сроки",
                item.DeadlineLabel,
                string.Empty,
                string.Empty,
                item.DeadlineType,
                string.Empty,
                $"{item.TotalCount} всего",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                item.OverdueCount.ToString(),
                item.UpcomingCount.ToString()
            ]);
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    private static string BuildLegalWorkExcelXml(LegalWorkReportDto report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        builder.AppendLine("""<?mso-application progid="Excel.Sheet"?>""");
        builder.AppendLine("""
<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"
          xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
  <Worksheet ss:Name="Legal work">
    <Table>
""");

        AppendLegalWorkExcelRow(builder, ["Отчет по юридической работе"], header: true);
        AppendLegalWorkExcelRow(builder, ["Период", $"{report.From:dd.MM.yyyy} - {report.To:dd.MM.yyyy}"]);
        AppendLegalWorkExcelRow(builder, ["Всего дел", report.TotalCases.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Дел в работе", report.ActiveCases.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Закрыто", report.ClosedCases.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Проиграно", report.LostCases.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Открытые задачи", report.OpenTaskCount.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Просроченные задачи", report.OverdueTaskCount.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Просроченные сроки", report.OverdueDeadlineCount.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Ближайшие сроки", report.UpcomingDeadlineCount.ToString(CultureInfo.InvariantCulture)]);
        AppendLegalWorkExcelRow(builder, ["Гонорар", MoneyCell(report.FeeTotal)]);
        AppendLegalWorkExcelRow(builder, ["Оплачено", MoneyCell(report.PaidTotal)]);
        AppendLegalWorkExcelRow(builder, ["Остаток", MoneyCell(report.OutstandingTotal)]);
        AppendLegalWorkExcelRow(builder, []);

        AppendLegalWorkExcelRow(builder, [
            "Дело",
            "Клиент",
            "Ответственный",
            "Статус",
            "Тип дела",
            "Создано",
            "Гонорар",
            "Оплачено",
            "Остаток",
            "Открытые задачи",
            "Просроченные задачи",
            "На согласовании",
            "Просроченные сроки",
            "Ближайшие сроки",
        ], header: true);

        foreach (var item in report.Cases)
        {
            AppendLegalWorkExcelRow(builder, [
                BuildCaseCsvTitle(item),
                item.ClientName,
                item.ResponsibleName,
                item.StatusLabel,
                item.CaseType,
                item.CreatedAt.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")),
                MoneyCell(item.FeeAmount),
                MoneyCell(item.PaidTotal),
                MoneyCell(item.OutstandingTotal),
                item.OpenTasks.ToString(CultureInfo.InvariantCulture),
                item.OverdueTasks.ToString(CultureInfo.InvariantCulture),
                item.OnApprovalTasks.ToString(CultureInfo.InvariantCulture),
                item.OverdueDeadlines.ToString(CultureInfo.InvariantCulture),
                item.UpcomingDeadlines.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        AppendLegalWorkExcelRow(builder, []);
        AppendLegalWorkExcelRow(builder, ["Ответственный", "Дел в работе", "Закрыто", "Проиграно", "Открытые задачи", "Просроченные задачи", "На согласовании", "Просроченные сроки", "Гонорар", "Оплачено", "Остаток"], header: true);
        foreach (var item in report.Responsibles)
        {
            AppendLegalWorkExcelRow(builder, [
                item.ResponsibleName,
                item.ActiveCases.ToString(CultureInfo.InvariantCulture),
                item.ClosedCases.ToString(CultureInfo.InvariantCulture),
                item.LostCases.ToString(CultureInfo.InvariantCulture),
                item.OpenTasks.ToString(CultureInfo.InvariantCulture),
                item.OverdueTasks.ToString(CultureInfo.InvariantCulture),
                item.OnApprovalTasks.ToString(CultureInfo.InvariantCulture),
                item.OverdueDeadlines.ToString(CultureInfo.InvariantCulture),
                MoneyCell(item.FeeTotal),
                MoneyCell(item.PaidTotal),
                MoneyCell(item.OutstandingTotal),
            ]);
        }

        AppendLegalWorkExcelRow(builder, []);
        AppendLegalWorkExcelRow(builder, ["Финансы по статусам", "Дел", "Гонорар", "Оплачено", "Остаток"], header: true);
        foreach (var item in report.FinanceByStatus)
        {
            AppendLegalWorkExcelRow(builder, [
                item.StatusLabel,
                item.CaseCount.ToString(CultureInfo.InvariantCulture),
                MoneyCell(item.FeeTotal),
                MoneyCell(item.PaidTotal),
                MoneyCell(item.OutstandingTotal),
            ]);
        }

        AppendLegalWorkExcelRow(builder, []);
        AppendLegalWorkExcelRow(builder, ["Срок", "Всего", "Просрочено", "Ближайшие"], header: true);
        foreach (var item in report.DeadlinesByType)
        {
            AppendLegalWorkExcelRow(builder, [
                item.DeadlineLabel,
                item.TotalCount.ToString(CultureInfo.InvariantCulture),
                item.OverdueCount.ToString(CultureInfo.InvariantCulture),
                item.UpcomingCount.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        AppendLegalWorkExcelRow(builder, []);
        AppendLegalWorkExcelRow(builder, ["Контрольные сроки", "Дело", "Клиент", "Ответственный", "Дата", "Отклонение"], header: true);
        foreach (var item in report.OverdueDeadlines.Concat(report.UpcomingDeadlines))
        {
            AppendLegalWorkExcelRow(builder, [
                item.DeadlineLabel,
                string.IsNullOrWhiteSpace(item.CaseNumber) ? item.CaseTitle : $"{item.CaseNumber} {item.CaseTitle}",
                item.ClientName,
                item.ResponsibleName,
                item.DueAt.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU")),
                FormatLegalWorkDeadlineOffset(item.DaysOffset),
            ]);
        }

        builder.AppendLine("""
    </Table>
  </Worksheet>
</Workbook>
""");
        return builder.ToString();
    }

    private static string BuildLegalWorkPrintHtml(LegalWorkReportDto report, DateTime generatedAtUtc)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <style>
    @page { size: A4 landscape; margin: 14mm; }
    body { font-family: Arial, sans-serif; color: #111827; font-size: 12px; }
    h1 { margin: 0 0 6px; font-size: 22px; }
    h2 { margin: 22px 0 8px; font-size: 15px; }
    .muted { color: #6b7280; }
    .summary { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; margin: 16px 0; }
    .metric { border: 1px solid #e5e7eb; border-radius: 8px; padding: 8px; }
    .metric strong { display: block; font-size: 17px; margin-top: 4px; }
    table { width: 100%; border-collapse: collapse; margin-top: 8px; }
    th, td { border: 1px solid #e5e7eb; padding: 5px 6px; vertical-align: top; }
    th { background: #f3f4f6; text-align: left; }
    .num { text-align: right; white-space: nowrap; }
  </style>
</head>
<body>
""");
        builder.Append("<h1>Отчет по юридической работе</h1>");
        builder.Append("<div class=\"muted\">Период: ")
            .Append(Html($"{report.From:dd.MM.yyyy} - {report.To:dd.MM.yyyy}"))
            .Append(" · Сформировано: ")
            .Append(Html(generatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))))
            .AppendLine("</div>");

        builder.AppendLine("<div class=\"summary\">");
        AppendLegalWorkMetric(builder, "Всего дел", report.TotalCases.ToString(CultureInfo.InvariantCulture));
        AppendLegalWorkMetric(builder, "Дел в работе", report.ActiveCases.ToString(CultureInfo.InvariantCulture));
        AppendLegalWorkMetric(builder, "Просроченные сроки", report.OverdueDeadlineCount.ToString(CultureInfo.InvariantCulture));
        AppendLegalWorkMetric(builder, "Остаток по гонорарам", MoneyCell(report.OutstandingTotal));
        AppendLegalWorkMetric(builder, "Открытые задачи", report.OpenTaskCount.ToString(CultureInfo.InvariantCulture));
        AppendLegalWorkMetric(builder, "На согласовании", report.OnApprovalTaskCount.ToString(CultureInfo.InvariantCulture));
        AppendLegalWorkMetric(builder, "Оплачено", MoneyCell(report.PaidTotal));
        AppendLegalWorkMetric(builder, "Гонорар", MoneyCell(report.FeeTotal));
        builder.AppendLine("</div>");

        builder.AppendLine("<h2>Дела</h2><table><thead><tr><th>Дело</th><th>Клиент</th><th>Ответственный</th><th>Статус</th><th>Тип</th><th class=\"num\">Гонорар</th><th class=\"num\">Оплачено</th><th class=\"num\">Остаток</th><th class=\"num\">Задачи</th><th class=\"num\">Сроки</th></tr></thead><tbody>");
        foreach (var item in report.Cases)
        {
            builder.Append("<tr><td>").Append(Html(BuildCaseCsvTitle(item)))
                .Append("</td><td>").Append(Html(item.ClientName))
                .Append("</td><td>").Append(Html(item.ResponsibleName))
                .Append("</td><td>").Append(Html(item.StatusLabel))
                .Append("</td><td>").Append(Html(item.CaseType))
                .Append("</td><td class=\"num\">").Append(Html(MoneyCell(item.FeeAmount)))
                .Append("</td><td class=\"num\">").Append(Html(MoneyCell(item.PaidTotal)))
                .Append("</td><td class=\"num\">").Append(Html(MoneyCell(item.OutstandingTotal)))
                .Append("</td><td class=\"num\">").Append(Html($"{item.OpenTasks}/{item.OverdueTasks}"))
                .Append("</td><td class=\"num\">").Append(Html($"{item.OverdueDeadlines}/{item.UpcomingDeadlines}"))
                .AppendLine("</td></tr>");
        }
        builder.AppendLine("</tbody></table>");

        builder.AppendLine("<h2>Ответственные</h2><table><thead><tr><th>Ответственный</th><th class=\"num\">В работе</th><th class=\"num\">Закрыто</th><th class=\"num\">Открытые задачи</th><th class=\"num\">Просрочено задач</th><th class=\"num\">Остаток</th></tr></thead><tbody>");
        foreach (var item in report.Responsibles)
        {
            builder.Append("<tr><td>").Append(Html(item.ResponsibleName))
                .Append("</td><td class=\"num\">").Append(item.ActiveCases)
                .Append("</td><td class=\"num\">").Append(item.ClosedCases)
                .Append("</td><td class=\"num\">").Append(item.OpenTasks)
                .Append("</td><td class=\"num\">").Append(item.OverdueTasks)
                .Append("</td><td class=\"num\">").Append(Html(MoneyCell(item.OutstandingTotal)))
                .AppendLine("</td></tr>");
        }
        builder.AppendLine("</tbody></table>");

        builder.AppendLine("<h2>Контрольные сроки</h2><table><thead><tr><th>Срок</th><th>Дело</th><th>Клиент</th><th>Ответственный</th><th>Дата</th><th>Отклонение</th></tr></thead><tbody>");
        foreach (var item in report.OverdueDeadlines.Concat(report.UpcomingDeadlines))
        {
            builder.Append("<tr><td>").Append(Html(item.DeadlineLabel))
                .Append("</td><td>").Append(Html(string.IsNullOrWhiteSpace(item.CaseNumber) ? item.CaseTitle : $"{item.CaseNumber} {item.CaseTitle}"))
                .Append("</td><td>").Append(Html(item.ClientName))
                .Append("</td><td>").Append(Html(item.ResponsibleName))
                .Append("</td><td>").Append(Html($"{item.DueAt:dd.MM.yyyy}"))
                .Append("</td><td>").Append(Html(FormatLegalWorkDeadlineOffset(item.DaysOffset)))
                .AppendLine("</td></tr>");
        }
        builder.AppendLine("</tbody></table></body></html>");
        return builder.ToString();
    }

    private static string BuildCaseCsvTitle(LegalWorkCaseItemDto item)
        => string.IsNullOrWhiteSpace(item.CaseNumber)
            ? item.CaseTitle
            : $"{item.CaseNumber} {item.CaseTitle}";

    private static string MoneyCell(decimal value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatLegalWorkDeadlineOffset(int days)
        => days < 0
            ? $"{Math.Abs(days)} дн. просрочено"
            : days == 0
                ? "сегодня"
                : $"через {days} дн.";

    private static void AppendLegalWorkMetric(StringBuilder builder, string label, string value)
    {
        builder.Append("<div class=\"metric\"><span>")
            .Append(Html(label))
            .Append("</span><strong>")
            .Append(Html(value))
            .AppendLine("</strong></div>");
    }

    private static void AppendLegalWorkExcelRow(StringBuilder builder, IReadOnlyList<string> values, bool header = false)
    {
        builder.AppendLine("      <Row>");
        foreach (var value in values)
        {
            var type = !header && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                ? "Number"
                : "String";
            builder.Append("        <Cell><Data ss:Type=\"").Append(type).Append("\">");
            builder.Append(WebUtility.HtmlEncode(value));
            builder.AppendLine("</Data></Cell>");
        }

        builder.AppendLine("      </Row>");
    }

    private static string Html(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static void AppendCsvRow(StringBuilder builder, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(';');
            }

            builder.Append(EscapeCsvCell(values[index]));
        }

        builder.AppendLine();
    }

    private static string EscapeCsvCell(string? value)
    {
        var normalized = value ?? string.Empty;
        return normalized.Contains(';') || normalized.Contains('"') || normalized.Contains('\n') || normalized.Contains('\r')
            ? $"\"{normalized.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : normalized;
    }

    private async Task<IReadOnlyList<LegalWorkPaymentRow>> BuildLegalWorkPaymentRowsAsync(
        IReadOnlyCollection<int> caseIds,
        DataVisibilityAccess accountsVisibility,
        CancellationToken ct)
    {
        if (caseIds.Count == 0)
        {
            return [];
        }

        var paymentQuery = _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(accountsVisibility)
            .ExcludeInstallmentParentInvoices(_db)
            .Where(item => item.Type == PaymentType.Income && item.Status != PaymentStatus.Cancelled);

        var directRows = await paymentQuery
            .Where(item => item.ClientCaseId.HasValue && caseIds.Contains(item.ClientCaseId.Value))
            .Select(item => new LegalWorkPaymentRow(
                item.Id,
                item.ClientCaseId!.Value,
                item.Amount,
                item.PaidAmount,
                item.Status))
            .ToListAsync(ct);

        var linkedRows = await _db.InvoiceClientCases
            .AsNoTracking()
            .Where(link => caseIds.Contains(link.ClientCaseId))
            .Join(
                paymentQuery,
                link => link.PaymentId,
                payment => payment.Id,
                (link, payment) => new LegalWorkPaymentRow(
                    payment.Id,
                    link.ClientCaseId,
                    payment.Amount,
                    payment.PaidAmount,
                    payment.Status))
            .ToListAsync(ct);

        return directRows
            .Concat(linkedRows)
            .GroupBy(item => new { item.PaymentId, item.CaseId })
            .Select(group => group.First())
            .ToList();
    }

    private async Task<CaseActivityReportSettings> GetCaseActivityReportSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == CaseInactivityAutomationDaysSettingKey ||
                item.Key == CaseInactivityAutomationEscalationDaysSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var inactivityDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationDaysSettingKey), 14), 1, 365);
        var escalationDays = Math.Clamp(ParseIntSetting(settings.GetValueOrDefault(CaseInactivityAutomationEscalationDaysSettingKey), 30), inactivityDays, 365);
        return new CaseActivityReportSettings(inactivityDays, escalationDays);
    }

    private async Task<CaseActivityReportDto> BuildCaseActivityReportAsync(
        int inactivityDays,
        int escalationDays,
        string activity,
        int limit,
        DataVisibilityAccess casesVisibility,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-inactivityDays);

        var visibleCases = await _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .ToListAsync(ct);

        var activeCases = visibleCases
            .Where(item => !IsFinishedCaseStatus(item.Status))
            .ToList();
        var caseIds = activeCases.Select(item => item.Id).Distinct().ToArray();

        var taskRows = caseIds.Length == 0
            ? new List<CaseActivityTaskRow>()
            : await _db.TaskJournalItems
                .AsNoTracking()
                .Where(item => item.ClientCaseId.HasValue && caseIds.Contains(item.ClientCaseId.Value))
                .Select(item => new CaseActivityTaskRow(
                    item.ClientCaseId!.Value,
                    item.Status,
                    item.UpdatedAt ?? item.CreatedAt))
                .ToListAsync(ct);

        var tasksByCase = taskRows
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var rows = activeCases
            .Select(clientCase =>
            {
                var tasks = tasksByCase.GetValueOrDefault(clientCase.Id) ?? [];
                var activeTaskCount = tasks.Count(item => IsOpenTaskStatus(item.Status));
                var completedTaskCount = tasks.Count - activeTaskCount;
                var lastTaskAt = tasks.Count > 0
                    ? tasks.Max(item => item.LastActivityAt)
                    : (DateTime?)null;
                var lastActivityAt = ResolveCaseLastActivityAt(clientCase, lastTaskAt);
                var daysWithoutActivity = Math.Max(0, (now.Date - lastActivityAt.Date).Days);
                var isInactive =
                    activeTaskCount == 0
                    && lastActivityAt <= cutoff
                    && (clientCase.LastViewedAt ?? clientCase.CreatedAt) <= cutoff
                    && (clientCase.ActivityConfirmedAt ?? clientCase.CreatedAt) <= cutoff
                    && clientCase.InactivityRemindersEnabled;
                var isEscalated = isInactive && daysWithoutActivity >= escalationDays;
                var owner = ResolveCaseOwner(clientCase);

                return new CaseActivityReportItemDto
                {
                    CaseId = clientCase.Id,
                    CaseNumber = clientCase.CaseNumber,
                    CaseTitle = clientCase.Title,
                    CaseType = NormalizeReportText(clientCase.CaseType, "Не указан"),
                    Status = clientCase.Status.ToString(),
                    StatusLabel = FormatCaseStatus(clientCase.Status),
                    OwnerId = owner.OwnerId,
                    OwnerType = owner.OwnerType,
                    OwnerName = owner.OwnerName,
                    ResponsibleUserId = clientCase.ResponsibleUserId,
                    ResponsibleName = ResolveCaseResponsibleName(clientCase),
                    CreatedAt = clientCase.CreatedAt,
                    LastActivityAt = lastActivityAt,
                    LastTaskAt = lastTaskAt,
                    LastViewedAt = clientCase.LastViewedAt,
                    ActivityConfirmedAt = clientCase.ActivityConfirmedAt,
                    DaysWithoutActivity = daysWithoutActivity,
                    ActiveTaskCount = activeTaskCount,
                    CompletedTaskCount = completedTaskCount,
                    InactivityRemindersEnabled = clientCase.InactivityRemindersEnabled,
                    IsInactive = isInactive,
                    IsEscalated = isEscalated,
                    Reason = BuildCaseActivityReason(clientCase, activeTaskCount, lastActivityAt, cutoff, isInactive),
                };
            })
            .ToList();

        var filteredRows = FilterCaseActivityRows(rows, activity)
            .OrderByDescending(item => item.IsInactive)
            .ThenByDescending(item => item.IsEscalated)
            .ThenByDescending(item => item.DaysWithoutActivity)
            .ThenBy(item => item.OwnerName)
            .ThenBy(item => item.CaseTitle)
            .Take(limit)
            .ToList();

        return new CaseActivityReportDto
        {
            GeneratedAt = now,
            InactivityDays = inactivityDays,
            EscalationDays = escalationDays,
            Activity = activity,
            TotalVisibleCases = visibleCases.Count,
            ActiveCaseCount = activeCases.Count,
            FinishedCaseCount = visibleCases.Count(item => IsFinishedCaseStatus(item.Status)),
            InactiveCaseCount = rows.Count(item => item.IsInactive),
            EscalatedCaseCount = rows.Count(item => item.IsEscalated),
            DisabledCaseCount = rows.Count(item => !item.InactivityRemindersEnabled),
            WithActiveTasksCount = rows.Count(item => item.ActiveTaskCount > 0),
            WithoutResponsibleCount = rows.Count(item => !item.ResponsibleUserId.HasValue),
            Items = filteredRows,
        };
    }

    private async Task<OverdueProceduralDeadlinesReportDto> BuildOverdueProceduralDeadlinesReportAsync(
        DateTime asOf,
        string? deadlineType,
        int? responsibleUserId,
        int limit,
        DataVisibilityAccess casesVisibility,
        CancellationToken ct)
    {
        var visibleCaseIdsQuery = _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(casesVisibility)
            .Select(item => item.Id);

        var query = _db.ClientCaseProceduralDeadlines
            .AsNoTracking()
            .Include(item => item.ClientCase)
                .ThenInclude(item => item.Client)
            .Include(item => item.ClientCase)
                .ThenInclude(item => item.LegalEntity)
            .Include(item => item.ClientCase)
                .ThenInclude(item => item.ResponsibleUser)
            .Include(item => item.TaskJournalItem)
            .Include(item => item.ClientCaseDocument)
            .Where(item =>
                visibleCaseIdsQuery.Contains(item.ClientCaseId)
                && item.DueAt < asOf.Date
                && (item.Status == "active" || item.Status == "moved"));

        if (!string.IsNullOrWhiteSpace(deadlineType))
        {
            query = query.Where(item => item.DeadlineType == deadlineType);
        }

        if (responsibleUserId.HasValue)
        {
            query = query.Where(item => item.ClientCase.ResponsibleUserId == responsibleUserId.Value);
        }

        var rows = await query
            .ToListAsync(ct);

        var items = rows
            .Select(item => MapOverdueProceduralDeadlineItem(item, asOf))
            .OrderByDescending(item => item.DaysOverdue)
            .ThenBy(item => item.DueAt)
            .ThenBy(item => item.CaseTitle)
            .ThenBy(item => item.Title)
            .ToList();

        return new OverdueProceduralDeadlinesReportDto
        {
            GeneratedAt = DateTime.UtcNow,
            AsOf = asOf,
            DeadlineTypeFilter = deadlineType ?? string.Empty,
            ResponsibleUserIdFilter = responsibleUserId,
            Limit = limit,
            TotalOverdueCount = items.Count,
            CriticalOverdueCount = items.Count(item => item.DaysOverdue >= 7),
            WithoutResponsibleCount = items.Count(item => !item.ResponsibleUserId.HasValue),
            ByType = items
                .GroupBy(item => new { item.DeadlineType, item.DeadlineTypeLabel })
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.DeadlineTypeLabel)
                .Select(group => new OverdueProceduralDeadlineTypeItemDto
                {
                    DeadlineType = group.Key.DeadlineType,
                    DeadlineTypeLabel = group.Key.DeadlineTypeLabel,
                    Count = group.Count(),
                    CriticalCount = group.Count(item => item.DaysOverdue >= 7),
                })
                .ToList(),
            ByResponsible = items
                .GroupBy(item => new { item.ResponsibleUserId, item.ResponsibleName })
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.ResponsibleName)
                .Select(group => new OverdueProceduralDeadlineResponsibleItemDto
                {
                    ResponsibleUserId = group.Key.ResponsibleUserId,
                    ResponsibleName = group.Key.ResponsibleName,
                    Count = group.Count(),
                    CriticalCount = group.Count(item => item.DaysOverdue >= 7),
                })
                .ToList(),
            Items = items.Take(limit).ToList(),
        };
    }

    private static OverdueProceduralDeadlineItemDto MapOverdueProceduralDeadlineItem(
        ClientCaseProceduralDeadline item,
        DateTime asOf)
        => new()
        {
            DeadlineId = item.Id,
            CaseId = item.ClientCaseId,
            CaseNumber = item.ClientCase.CaseNumber,
            CaseTitle = item.ClientCase.Title,
            ClientName = ResolveCaseOwnerName(item.ClientCase),
            ResponsibleUserId = item.ClientCase.ResponsibleUserId,
            ResponsibleName = ResolveCaseResponsibleName(item.ClientCase),
            DeadlineType = item.DeadlineType,
            DeadlineTypeLabel = ResolveProceduralDeadlineTypeLabel(item.DeadlineType),
            Title = NormalizeReportText(item.Title, ResolveProceduralDeadlineTypeLabel(item.DeadlineType)),
            Description = item.Description,
            DueAt = item.DueAt,
            DaysOverdue = Math.Max(0, (asOf.Date - item.DueAt.Date).Days),
            Status = item.Status,
            StatusLabel = ResolveProceduralDeadlineStatusLabel(item.Status),
            TaskJournalItemId = item.TaskJournalItemId,
            TaskTitle = item.TaskJournalItem?.Title ?? string.Empty,
            ClientCaseDocumentId = item.ClientCaseDocumentId,
            DocumentName = item.ClientCaseDocument?.OriginalFileName ?? string.Empty,
            SourceType = item.SourceType,
            SourceId = item.SourceId,
        };

    private async Task<CurrentCasesReportDto> BuildCurrentCasesReportAsync(
        DateTime asOf,
        CurrentCasesReportFilters filters,
        int limit,
        DataVisibilityAccess casesVisibility,
        DataVisibilityAccess tasksVisibility,
        CancellationToken ct)
    {
        var caseQuery = _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.Status != ClientCaseStatus.Closed && item.Status != ClientCaseStatus.Lost);

        if (TryParseClientCaseStatus(filters.Status, out var statusFilter))
        {
            caseQuery = caseQuery.Where(item => item.Status == statusFilter);
        }

        if (filters.ResponsibleUserId.HasValue)
        {
            caseQuery = caseQuery.Where(item => item.ResponsibleUserId == filters.ResponsibleUserId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filters.CaseType))
        {
            caseQuery = caseQuery.Where(item => item.CaseType != null && item.CaseType.Contains(filters.CaseType));
        }

        var cases = await caseQuery.ToListAsync(ct);
        var caseIds = cases.Select(item => item.Id).Distinct().ToArray();

        var taskRows = caseIds.Length == 0
            ? new List<LegalWorkTaskRow>()
            : await _db.TaskJournalItems
                .AsNoTracking()
                .ApplyTaskVisibility(tasksVisibility)
                .Where(item => item.ClientCaseId.HasValue && caseIds.Contains(item.ClientCaseId.Value))
                .Select(item => new LegalWorkTaskRow(
                    item.Id,
                    item.ClientCaseId,
                    item.AssigneeUserId,
                    item.AssigneeName,
                    item.Status,
                    item.Priority,
                    item.DueAt,
                    item.PlannedDurationMinutes))
                .ToListAsync(ct);

        var deadlineRows = caseIds.Length == 0
            ? new List<CurrentCaseDeadlineRow>()
            : await _db.ClientCaseProceduralDeadlines
                .AsNoTracking()
                .Where(item =>
                    caseIds.Contains(item.ClientCaseId)
                    && (item.Status == "active" || item.Status == "moved"))
                .Select(item => new CurrentCaseDeadlineRow(
                    item.ClientCaseId,
                    item.DeadlineType,
                    item.Title,
                    item.DueAt))
                .ToListAsync(ct);

        var tasksByCase = taskRows
            .Where(item => item.ClientCaseId.HasValue)
            .GroupBy(item => item.ClientCaseId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var deadlinesByCase = deadlineRows
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var rows = cases
            .Select(clientCase => MapCurrentCaseReportItem(
                clientCase,
                tasksByCase.GetValueOrDefault(clientCase.Id) ?? [],
                deadlinesByCase.GetValueOrDefault(clientCase.Id) ?? [],
                asOf))
            .ToList();

        var orderedRows = rows
            .OrderByDescending(item => item.IsOverdue)
            .ThenByDescending(item => item.OverdueDeadlineCount)
            .ThenByDescending(item => item.OverdueTaskCount)
            .ThenBy(item => item.DueDate ?? DateTime.MaxValue)
            .ThenBy(item => item.CaseTitle)
            .ToList();

        return new CurrentCasesReportDto
        {
            GeneratedAt = DateTime.UtcNow,
            AsOf = asOf,
            StatusFilter = filters.Status ?? string.Empty,
            ResponsibleUserIdFilter = filters.ResponsibleUserId,
            CaseTypeFilter = filters.CaseType ?? string.Empty,
            Limit = limit,
            TotalCurrentCases = rows.Count,
            OpenCases = rows.Count(item => item.Status == nameof(ClientCaseStatus.Open)),
            OnHoldCases = rows.Count(item => item.Status == nameof(ClientCaseStatus.OnHold)),
            OverdueCases = rows.Count(item => item.IsOverdue),
            WithoutResponsibleCount = rows.Count(item => !item.ResponsibleUserId.HasValue),
            WithOpenTasksCount = rows.Count(item => item.OpenTaskCount > 0),
            WithOverdueTasksCount = rows.Count(item => item.OverdueTaskCount > 0),
            WithOverdueDeadlinesCount = rows.Count(item => item.OverdueDeadlineCount > 0),
            ByStatus = rows
                .GroupBy(item => new { item.Status, item.StatusLabel })
                .OrderBy(group => group.Key.Status)
                .Select(group => new CurrentCasesStatusItemDto
                {
                    Status = group.Key.Status,
                    StatusLabel = group.Key.StatusLabel,
                    Count = group.Count(),
                    OverdueCases = group.Count(item => item.IsOverdue),
                    OpenTasks = group.Sum(item => item.OpenTaskCount),
                })
                .ToList(),
            ByResponsible = rows
                .GroupBy(item => new { item.ResponsibleUserId, item.ResponsibleName })
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.ResponsibleName)
                .Select(group => new CurrentCasesResponsibleItemDto
                {
                    ResponsibleUserId = group.Key.ResponsibleUserId,
                    ResponsibleName = group.Key.ResponsibleName,
                    CaseCount = group.Count(),
                    OverdueCases = group.Count(item => item.IsOverdue),
                    OpenTasks = group.Sum(item => item.OpenTaskCount),
                    OverdueTasks = group.Sum(item => item.OverdueTaskCount),
                    OverdueDeadlines = group.Sum(item => item.OverdueDeadlineCount),
                })
                .ToList(),
            Items = orderedRows.Take(limit).ToList(),
        };
    }

    private static CurrentCaseReportItemDto MapCurrentCaseReportItem(
        ClientCase clientCase,
        IReadOnlyCollection<LegalWorkTaskRow> tasks,
        IReadOnlyCollection<CurrentCaseDeadlineRow> deadlines,
        DateTime asOf)
    {
        var openTasks = tasks.Where(item => IsOpenTaskStatus(item.Status)).ToList();
        var overdueTasks = openTasks.Where(item => item.DueAt.Date < asOf.Date).ToList();
        var onApprovalTasks = openTasks
            .Where(item => string.Equals(item.Status, "onApproval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var overdueDeadlines = deadlines.Where(item => item.DueAt.Date < asOf.Date).ToList();
        var nextDeadline = deadlines
            .Where(item => item.DueAt.Date >= asOf.Date)
            .OrderBy(item => item.DueAt)
            .FirstOrDefault();
        var lastActivityAt = ResolveCaseLastActivityAt(clientCase, null);

        return new CurrentCaseReportItemDto
        {
            CaseId = clientCase.Id,
            CaseNumber = clientCase.CaseNumber,
            CaseTitle = clientCase.Title,
            CaseType = NormalizeReportText(clientCase.CaseType, "Не указан"),
            ClientName = ResolveCaseOwnerName(clientCase),
            Status = clientCase.Status.ToString(),
            StatusLabel = FormatCaseStatus(clientCase.Status),
            ResponsibleUserId = clientCase.ResponsibleUserId,
            ResponsibleName = ResolveCaseResponsibleName(clientCase),
            CreatedAt = clientCase.CreatedAt,
            StartDate = clientCase.StartDate,
            DueDate = clientCase.DueDate,
            NextHearingAt = clientCase.NextHearingAt,
            DaysInWork = Math.Max(0, (asOf.Date - (clientCase.StartDate ?? clientCase.CreatedAt).Date).Days),
            DaysUntilDue = clientCase.DueDate.HasValue
                ? (clientCase.DueDate.Value.Date - asOf.Date).Days
                : null,
            IsOverdue = clientCase.DueDate.HasValue && clientCase.DueDate.Value.Date < asOf.Date,
            OpenTaskCount = openTasks.Count,
            OverdueTaskCount = overdueTasks.Count,
            OnApprovalTaskCount = onApprovalTasks.Count,
            ActiveDeadlineCount = deadlines.Count,
            OverdueDeadlineCount = overdueDeadlines.Count,
            NextDeadlineAt = nextDeadline?.DueAt,
            NextDeadlineLabel = nextDeadline is null
                ? string.Empty
                : NormalizeReportText(nextDeadline.Title, ResolveProceduralDeadlineTypeLabel(nextDeadline.DeadlineType)),
            LastActivityAt = lastActivityAt,
        };
    }

    private async Task<LawyerWorkloadReportDto> BuildLawyerWorkloadReportAsync(
        DateTime asOf,
        int horizonDays,
        int? lawyerUserId,
        int limit,
        DataVisibilityAccess casesVisibility,
        DataVisibilityAccess tasksVisibility,
        CancellationToken ct)
    {
        var horizonEnd = asOf.Date.AddDays(horizonDays);
        var activeCases = await _db.ClientCases
            .AsNoTracking()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(casesVisibility)
            .Where(item => item.Status != ClientCaseStatus.Closed && item.Status != ClientCaseStatus.Lost)
            .ToListAsync(ct);

        var caseIds = activeCases.Select(item => item.Id).Distinct().ToArray();
        var tasks = caseIds.Length == 0
            ? new List<LegalWorkTaskRow>()
            : await _db.TaskJournalItems
                .AsNoTracking()
                .ApplyTaskVisibility(tasksVisibility)
                .Where(item => item.ClientCaseId.HasValue && caseIds.Contains(item.ClientCaseId.Value))
                .Select(item => new LegalWorkTaskRow(
                    item.Id,
                    item.ClientCaseId,
                    item.AssigneeUserId,
                    item.AssigneeName,
                    item.Status,
                    item.Priority,
                    item.DueAt,
                    item.PlannedDurationMinutes))
                .ToListAsync(ct);

        var deadlines = caseIds.Length == 0
            ? new List<CurrentCaseDeadlineRow>()
            : await _db.ClientCaseProceduralDeadlines
                .AsNoTracking()
                .Where(item =>
                    caseIds.Contains(item.ClientCaseId)
                    && (item.Status == "active" || item.Status == "moved"))
                .Select(item => new CurrentCaseDeadlineRow(
                    item.ClientCaseId,
                    item.DeadlineType,
                    item.Title,
                    item.DueAt))
                .ToListAsync(ct);

        static int workloadKey(int? userId) => userId ?? int.MinValue;
        static int? workloadUserId(int key) => key == int.MinValue ? null : key;

        var caseResponsibleNames = activeCases
            .GroupBy(item => workloadKey(item.ResponsibleUserId))
            .ToDictionary(
                group => group.Key,
                group => ResolveCaseResponsibleName(group.First()));
        var taskAssigneeNames = tasks
            .GroupBy(item => workloadKey(item.AssigneeUserId))
            .ToDictionary(
                group => group.Key,
                group => NormalizeReportText(group.First().AssigneeName, "Не назначен"));
        var casesByResponsible = activeCases
            .GroupBy(item => workloadKey(item.ResponsibleUserId))
            .ToDictionary(group => group.Key, group => group.ToList());
        var tasksByAssignee = tasks
            .Where(item => IsOpenTaskStatus(item.Status))
            .GroupBy(item => workloadKey(item.AssigneeUserId))
            .ToDictionary(group => group.Key, group => group.ToList());
        var deadlinesByResponsible = deadlines
            .Join(
                activeCases,
                deadline => deadline.ClientCaseId,
                clientCase => clientCase.Id,
                (deadline, clientCase) => new { deadline, clientCase.ResponsibleUserId })
            .GroupBy(item => workloadKey(item.ResponsibleUserId))
            .ToDictionary(group => group.Key, group => group.Select(item => item.deadline).ToList());

        var lawyerIds = casesByResponsible.Keys
            .Concat(tasksByAssignee.Keys)
            .Concat(deadlinesByResponsible.Keys)
            .Distinct()
            .ToList();

        var rows = lawyerIds
            .Select(key => BuildLawyerWorkloadItem(
                workloadUserId(key),
                ResolveLawyerWorkloadName(key, caseResponsibleNames, taskAssigneeNames),
                casesByResponsible.GetValueOrDefault(key) ?? [],
                tasksByAssignee.GetValueOrDefault(key) ?? [],
                deadlinesByResponsible.GetValueOrDefault(key) ?? [],
                asOf,
                horizonEnd))
            .Where(item => !lawyerUserId.HasValue || item.LawyerUserId == lawyerUserId.Value)
            .OrderByDescending(item => item.IsOverloaded)
            .ThenByDescending(item => item.WorkloadScore)
            .ThenByDescending(item => item.OpenTaskCount + item.ActiveCaseCount)
            .ThenBy(item => item.LawyerName)
            .ToList();

        return new LawyerWorkloadReportDto
        {
            GeneratedAt = DateTime.UtcNow,
            AsOf = asOf,
            HorizonDays = horizonDays,
            LawyerUserIdFilter = lawyerUserId,
            Limit = limit,
            LawyerCount = rows.Count,
            TotalActiveCases = rows.Sum(item => item.ActiveCaseCount),
            TotalOpenTasks = rows.Sum(item => item.OpenTaskCount),
            TotalOverdueTasks = rows.Sum(item => item.OverdueTaskCount),
            TotalOverdueDeadlines = rows.Sum(item => item.OverdueDeadlineCount),
            OverloadedLawyerCount = rows.Count(item => item.IsOverloaded),
            TotalPlannedHours = rows.Sum(item => item.PlannedHours),
            Items = rows.Take(limit).ToList(),
        };
    }

    private static LawyerWorkloadItemDto BuildLawyerWorkloadItem(
        int? lawyerUserId,
        string lawyerName,
        IReadOnlyCollection<ClientCase> cases,
        IReadOnlyCollection<LegalWorkTaskRow> tasks,
        IReadOnlyCollection<CurrentCaseDeadlineRow> deadlines,
        DateTime asOf,
        DateTime horizonEnd)
    {
        var overdueTasks = tasks.Where(item => item.DueAt.Date < asOf.Date).ToList();
        var onApprovalTasks = tasks
            .Where(item => string.Equals(item.Status, "onApproval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var dueTodayTasks = tasks.Where(item => item.DueAt.Date == asOf.Date).ToList();
        var dueSoonTasks = tasks
            .Where(item => item.DueAt.Date > asOf.Date && item.DueAt.Date <= horizonEnd.Date)
            .ToList();
        var overdueDeadlines = deadlines.Where(item => item.DueAt.Date < asOf.Date).ToList();
        var plannedMinutes = tasks.Sum(item => Math.Max(0, item.PlannedDurationMinutes));
        var plannedHours = Math.Round(plannedMinutes / 60m, 1, MidpointRounding.AwayFromZero);
        var workloadScore =
            cases.Count * 2
            + tasks.Count * 3
            + overdueTasks.Count * 4
            + overdueDeadlines.Count * 5
            + onApprovalTasks.Count * 2
            + (int)Math.Ceiling(plannedHours);
        var isOverloaded =
            workloadScore >= 40
            || tasks.Count >= 10
            || plannedHours >= 32m
            || overdueDeadlines.Count > 0
            || overdueTasks.Count >= 3;

        return new LawyerWorkloadItemDto
        {
            LawyerUserId = lawyerUserId,
            LawyerName = lawyerName,
            ActiveCaseCount = cases.Count,
            OpenTaskCount = tasks.Count,
            OverdueTaskCount = overdueTasks.Count,
            OnApprovalTaskCount = onApprovalTasks.Count,
            DueTodayTaskCount = dueTodayTasks.Count,
            DueSoonTaskCount = dueSoonTasks.Count,
            OverdueDeadlineCount = overdueDeadlines.Count,
            ActiveDeadlineCount = deadlines.Count,
            PlannedMinutes = plannedMinutes,
            PlannedHours = plannedHours,
            WorkloadScore = workloadScore,
            IsOverloaded = isOverloaded,
            RiskLabel = ResolveLawyerWorkloadRiskLabel(isOverloaded, overdueTasks.Count, overdueDeadlines.Count, plannedHours, tasks.Count),
            NearestTaskDueAt = tasks.Count == 0 ? null : tasks.Min(item => item.DueAt),
            NearestDeadlineAt = deadlines.Count == 0 ? null : deadlines.Min(item => item.DueAt),
        };
    }

    private static string ResolveLawyerWorkloadName(
        int lawyerUserKey,
        IReadOnlyDictionary<int, string> caseResponsibleNames,
        IReadOnlyDictionary<int, string> taskAssigneeNames)
    {
        if (caseResponsibleNames.TryGetValue(lawyerUserKey, out var caseName))
        {
            return NormalizeReportText(caseName, "Не назначен");
        }

        if (taskAssigneeNames.TryGetValue(lawyerUserKey, out var taskName))
        {
            return NormalizeReportText(taskName, "Не назначен");
        }

        return "Не назначен";
    }

    private static string ResolveLawyerWorkloadRiskLabel(
        bool isOverloaded,
        int overdueTaskCount,
        int overdueDeadlineCount,
        decimal plannedHours,
        int openTaskCount)
    {
        if (!isOverloaded)
        {
            return "Норма";
        }

        if (overdueDeadlineCount > 0)
        {
            return "Есть просроченные сроки";
        }

        if (overdueTaskCount > 0)
        {
            return "Есть просроченные задачи";
        }

        if (plannedHours >= 32m)
        {
            return "Высокая плановая нагрузка";
        }

        return openTaskCount >= 10 ? "Много открытых задач" : "Повышенная нагрузка";
    }

    private static IReadOnlyList<LegalWorkResponsibleItemDto> BuildLegalWorkResponsibleItems(
        IReadOnlyCollection<ClientCase> cases,
        IReadOnlyCollection<LegalWorkTaskRow> openTasks,
        IReadOnlyCollection<LegalWorkTaskRow> overdueTasks,
        IReadOnlyCollection<LegalWorkTaskRow> onApprovalTasks,
        IReadOnlyCollection<LegalWorkDeadlineItemDto> overdueDeadlines,
        IReadOnlyDictionary<int, decimal> paidByCase)
    {
        var taskGroups = openTasks
            .Where(item => item.AssigneeUserId.HasValue)
            .GroupBy(item => item.AssigneeUserId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var overdueTaskGroups = overdueTasks
            .Where(item => item.AssigneeUserId.HasValue)
            .GroupBy(item => item.AssigneeUserId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var approvalTaskGroups = onApprovalTasks
            .Where(item => item.AssigneeUserId.HasValue)
            .GroupBy(item => item.AssigneeUserId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var overdueDeadlineGroups = overdueDeadlines
            .Where(item => item.ResponsibleUserId.HasValue)
            .GroupBy(item => item.ResponsibleUserId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        return cases
            .GroupBy(item => new ResponsibleReportKey(
                item.ResponsibleUserId,
                ResolveCaseResponsibleName(item)))
            .Select(group =>
            {
                var responsibleId = group.Key.ResponsibleUserId;
                var feeTotal = group.Sum(item => item.FeeAmount);
                var paidTotal = group.Sum(item => paidByCase.GetValueOrDefault(item.Id));
                var openTaskCount = responsibleId.HasValue && taskGroups.TryGetValue(responsibleId.Value, out var responsibleTasks)
                    ? responsibleTasks.Count
                    : 0;

                return new LegalWorkResponsibleItemDto
                {
                    ResponsibleUserId = responsibleId,
                    ResponsibleName = group.Key.ResponsibleName,
                    ActiveCases = group.Count(item => !IsFinishedCaseStatus(item.Status)),
                    ClosedCases = group.Count(item => item.Status == ClientCaseStatus.Closed),
                    LostCases = group.Count(item => item.Status == ClientCaseStatus.Lost),
                    OpenTasks = openTaskCount,
                    OverdueTasks = responsibleId.HasValue ? overdueTaskGroups.GetValueOrDefault(responsibleId.Value) : 0,
                    OnApprovalTasks = responsibleId.HasValue ? approvalTaskGroups.GetValueOrDefault(responsibleId.Value) : 0,
                    OverdueDeadlines = responsibleId.HasValue ? overdueDeadlineGroups.GetValueOrDefault(responsibleId.Value) : 0,
                    FeeTotal = feeTotal,
                    PaidTotal = paidTotal,
                    OutstandingTotal = Math.Max(0m, feeTotal - paidTotal),
                };
            })
            .OrderByDescending(item => item.ActiveCases + item.OpenTasks + item.OverdueDeadlines)
            .ThenBy(item => item.ResponsibleName)
            .ToList();
    }

    private static IEnumerable<LegalWorkDeadlineItemDto> BuildLegalWorkDeadlines(ClientCase item, DateTime today)
    {
        foreach (var row in BuildDeadlineRows(item))
        {
            yield return new LegalWorkDeadlineItemDto
            {
                CaseId = item.Id,
                CaseNumber = item.CaseNumber,
                CaseTitle = item.Title,
                ClientName = ResolveCaseOwnerName(item),
                ResponsibleUserId = item.ResponsibleUserId,
                ResponsibleName = ResolveCaseResponsibleName(item),
                DeadlineType = row.Type,
                DeadlineLabel = row.Label,
                DueAt = row.DueAt,
                DaysOffset = (row.DueAt.Date - today).Days,
            };
        }
    }

    private static IEnumerable<DeadlineSourceRow> BuildDeadlineRows(ClientCase item)
    {
        if (item.DueDate.HasValue)
        {
            yield return new DeadlineSourceRow("due", "Крайний срок по делу", item.DueDate.Value);
        }

        if (item.NextHearingAt.HasValue)
        {
            yield return new DeadlineSourceRow("hearing", "Судебное заседание", item.NextHearingAt.Value);
        }

        if (item.ClaimFiledAt.HasValue)
        {
            yield return new DeadlineSourceRow("claim", "Подача иска", item.ClaimFiledAt.Value);
        }

        if (item.AppealDeadlineAt.HasValue)
        {
            yield return new DeadlineSourceRow("appeal", "Срок обжалования", item.AppealDeadlineAt.Value);
        }

        if (item.MotionDeadlineAt.HasValue)
        {
            yield return new DeadlineSourceRow("motion", "Срок ходатайства", item.MotionDeadlineAt.Value);
        }

        if (item.ObjectionDeadlineAt.HasValue)
        {
            yield return new DeadlineSourceRow("objection", "Срок возражений", item.ObjectionDeadlineAt.Value);
        }
    }

    private static decimal ResolvePaidAmount(decimal amount, decimal paidAmount, PaymentStatus status)
    {
        if (paidAmount > 0m)
        {
            return Math.Min(amount, paidAmount);
        }

        return status == PaymentStatus.Completed ? amount : 0m;
    }

    private static bool IsOpenTaskStatus(string? status)
        => !string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<CaseActivityReportItemDto> FilterCaseActivityRows(
        IReadOnlyCollection<CaseActivityReportItemDto> rows,
        string activity)
        => activity switch
        {
            "inactive" => rows.Where(item => item.IsInactive),
            "escalated" => rows.Where(item => item.IsEscalated),
            "disabled" => rows.Where(item => !item.InactivityRemindersEnabled),
            "active" => rows.Where(item => !item.IsInactive && item.InactivityRemindersEnabled),
            _ => rows,
        };

    private static string NormalizeCaseActivityFilter(string? activity)
    {
        var normalized = activity?.Trim().ToLowerInvariant();
        return normalized is "inactive" or "active" or "escalated" or "disabled"
            ? normalized
            : "all";
    }

    private static int ParseIntSetting(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static DateTime ResolveCaseLastActivityAt(ClientCase clientCase, DateTime? lastTaskAt)
    {
        var lastActivity = clientCase.CreatedAt;
        if (lastTaskAt.HasValue && lastTaskAt.Value > lastActivity)
        {
            lastActivity = lastTaskAt.Value;
        }

        if (clientCase.LastViewedAt.HasValue && clientCase.LastViewedAt.Value > lastActivity)
        {
            lastActivity = clientCase.LastViewedAt.Value;
        }

        if (clientCase.ActivityConfirmedAt.HasValue && clientCase.ActivityConfirmedAt.Value > lastActivity)
        {
            lastActivity = clientCase.ActivityConfirmedAt.Value;
        }

        return lastActivity;
    }

    private static string BuildCaseActivityReason(
        ClientCase clientCase,
        int activeTaskCount,
        DateTime lastActivityAt,
        DateTime cutoff,
        bool isInactive)
    {
        if (!clientCase.InactivityRemindersEnabled)
        {
            return "Автоконтроль по делу выключен";
        }

        if (activeTaskCount > 0)
        {
            return $"Есть активные задачи: {activeTaskCount}";
        }

        if ((clientCase.LastViewedAt ?? clientCase.CreatedAt) > cutoff)
        {
            return "Карточку дела недавно открывали";
        }

        if ((clientCase.ActivityConfirmedAt ?? clientCase.CreatedAt) > cutoff)
        {
            return "Активность подтверждена вручную";
        }

        if (lastActivityAt > cutoff)
        {
            return "Недавно была активность по задаче или делу";
        }

        return isInactive
            ? "Нет активных задач и свежей активности по делу"
            : "Не попадает под правило неактивности";
    }

    private static CaseActivityOwner ResolveCaseOwner(ClientCase item)
    {
        if (item.Client is not null)
        {
            return new CaseActivityOwner(
                item.ClientId,
                "client",
                NormalizeReportText(
                    !string.IsNullOrWhiteSpace(item.Client.Name) ? item.Client.Name : item.Client.Company,
                    "Клиент без имени"));
        }

        if (item.LegalEntity is not null)
        {
            return new CaseActivityOwner(
                item.LegalEntityId,
                "legalEntity",
                NormalizeReportText(
                    !string.IsNullOrWhiteSpace(item.LegalEntity.ShortName) ? item.LegalEntity.ShortName : item.LegalEntity.FullName,
                    "Юридическое лицо без названия"));
        }

        return new CaseActivityOwner(null, "none", "Без владельца");
    }

    private static string ResolveCaseResponsibleName(ClientCase item)
    {
        var name = item.ResponsibleUser?.FullName;
        return NormalizeReportText(name, "Не назначен");
    }

    private static string ResolveCaseOwnerName(ClientCase item)
    {
        if (item.Client is not null)
        {
            return NormalizeReportText(
                !string.IsNullOrWhiteSpace(item.Client.Name) ? item.Client.Name : item.Client.Company,
                "Клиент без имени");
        }

        if (item.LegalEntity is not null)
        {
            return NormalizeReportText(
                !string.IsNullOrWhiteSpace(item.LegalEntity.ShortName) ? item.LegalEntity.ShortName : item.LegalEntity.FullName,
                "Юридическое лицо без названия");
        }

        return "Без владельца";
    }

    private static string NormalizeReportText(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool TryParseClientCaseStatus(string? value, out ClientCaseStatus status)
    {
        if (Enum.TryParse(value, ignoreCase: true, out status))
        {
            return true;
        }

        status = default;
        return false;
    }

    private static string FormatCaseStatus(ClientCaseStatus status)
        => status switch
        {
            ClientCaseStatus.Open => "В работе",
            ClientCaseStatus.OnHold => "Приостановлено",
            ClientCaseStatus.Closed => "Закрыто",
            ClientCaseStatus.Lost => "Проиграно",
            _ => status.ToString(),
        };

    private static string ResolveProceduralDeadlineTypeLabel(string value)
        => value.ToLowerInvariant() switch
        {
            "appeal" => "Апелляция",
            "cassation" => "Кассация",
            "motion" => "Ходатайство",
            "objection" => "Возражения",
            "claim" => "Подача заявления",
            "hearing" => "Судебное заседание",
            "document" => "Документ",
            _ => value,
        };

    private static string ResolveProceduralDeadlineStatusLabel(string value)
        => value.ToLowerInvariant() switch
        {
            "active" => "Активен",
            "moved" => "Перенесен",
            "closed" => "Закрыт",
            "cancelled" => "Отменен",
            _ => value,
        };

    private static bool IsFinishedCaseStatus(ClientCaseStatus status)
        => status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;

    private async Task<CashFlowReportDto> BuildCashFlowReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        string? accountName,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var openingRows = await BuildFactQuery(null, from.AddDays(-1), source, accountName, paymentsVisibility)
            .GroupBy(item => new { item.Source, AccountName = item.AccountName ?? string.Empty })
            .Select(group => new CashFlowAccountQueryRow
            {
                Source = group.Key.Source,
                AccountName = group.Key.AccountName,
                Income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount),
                Expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount),
                IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
            })
            .ToListAsync(ct);

        var periodRows = await BuildFactQuery(from, to, source, accountName, paymentsVisibility)
            .GroupBy(item => new { item.Source, AccountName = item.AccountName ?? string.Empty })
            .Select(group => new CashFlowAccountQueryRow
            {
                Source = group.Key.Source,
                AccountName = group.Key.AccountName,
                Income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount),
                Expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount),
                IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
            })
            .ToListAsync(ct);

        var dailyRows = await BuildFactQuery(from, to, source, accountName, paymentsVisibility)
            .GroupBy(item => item.Date.Date)
            .Select(group => new CashFlowDayQueryRow
            {
                Date = group.Key,
                Income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount),
                Expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount),
                IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
            })
            .OrderBy(item => item.Date)
            .ToListAsync(ct);

        var openingByAccount = openingRows.ToDictionary(
            row => (row.Source, row.AccountName),
            row => row.Income - row.Expense);

        var accountKeys = openingRows
            .Select(row => (row.Source, row.AccountName))
            .Concat(periodRows.Select(row => (row.Source, row.AccountName)))
            .Distinct()
            .OrderBy(row => row.Source)
            .ThenBy(row => row.AccountName)
            .ToList();

        var accountBreakdown = accountKeys
            .Select(key =>
            {
                var period = periodRows.FirstOrDefault(row => row.Source == key.Source && row.AccountName == key.AccountName);
                var openingBalance = openingByAccount.GetValueOrDefault(key);
                var income = period?.Income ?? 0m;
                var expense = period?.Expense ?? 0m;

                return new CashFlowAccountBreakdownDto
                {
                    Source = key.Source,
                    AccountName = string.IsNullOrWhiteSpace(key.AccountName) ? "Без счета" : key.AccountName,
                    OpeningBalance = openingBalance,
                    Income = income,
                    Expense = expense,
                    NetCashFlow = income - expense,
                    ClosingBalance = openingBalance + income - expense,
                    IncomeCount = period?.IncomeCount ?? 0,
                    ExpenseCount = period?.ExpenseCount ?? 0,
                };
            })
            .ToList();

        var sourceBreakdown = accountBreakdown
            .GroupBy(row => row.Source)
            .Select(group =>
            {
                var income = group.Sum(row => row.Income);
                var expense = group.Sum(row => row.Expense);
                var openingBalance = group.Sum(row => row.OpeningBalance);

                return new CashFlowSourceBreakdownDto
                {
                    Source = group.Key,
                    OpeningBalance = openingBalance,
                    Income = income,
                    Expense = expense,
                    NetCashFlow = income - expense,
                    ClosingBalance = openingBalance + income - expense,
                    IncomeCount = group.Sum(row => row.IncomeCount),
                    ExpenseCount = group.Sum(row => row.ExpenseCount),
                };
            })
            .OrderBy(row => row.Source)
            .ToList();

        var openingBalanceTotal = openingRows.Sum(row => row.Income - row.Expense);
        var incomeTotal = periodRows.Sum(row => row.Income);
        var expenseTotal = periodRows.Sum(row => row.Expense);
        var runningBalance = openingBalanceTotal;

        var days = dailyRows
            .Select(row =>
            {
                var dayOpening = runningBalance;
                runningBalance += row.Income - row.Expense;

                return new CashFlowDayDto
                {
                    Date = row.Date,
                    OpeningBalance = dayOpening,
                    Income = row.Income,
                    Expense = row.Expense,
                    NetCashFlow = row.Income - row.Expense,
                    ClosingBalance = runningBalance,
                    IncomeCount = row.IncomeCount,
                    ExpenseCount = row.ExpenseCount,
                };
            })
            .ToList();

        return new CashFlowReportDto
        {
            From = from,
            To = to,
            Source = source,
            AccountName = accountName,
            OpeningBalance = openingBalanceTotal,
            Income = incomeTotal,
            Expense = expenseTotal,
            NetCashFlow = incomeTotal - expenseTotal,
            ClosingBalance = openingBalanceTotal + incomeTotal - expenseTotal,
            IncomeCount = periodRows.Sum(row => row.IncomeCount),
            ExpenseCount = periodRows.Sum(row => row.ExpenseCount),
            BySource = sourceBreakdown,
            ByAccount = accountBreakdown,
            Days = days,
        };
    }

    private IQueryable<MoneyOperation> BuildFactQuery(
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        string? accountName,
        DataVisibilityAccess paymentsVisibility)
    {
        var query = _db.MoneyOperations
            .AsNoTracking()
            .Where(item =>
                item.AccountingType == MoneyOperationAccountingType.Fact &&
                (item.Direction == MoneyOperationDirection.Income || item.Direction == MoneyOperationDirection.Expense));

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(item => item.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(item => item.Date <= toDate);
        }

        if (source.HasValue)
        {
            query = query.Where(item => item.Source == source.Value);
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            query = query.Where(item => item.AccountName == accountName);
        }

        return ApplyMoneyOperationVisibility(query, paymentsVisibility);
    }

    private IQueryable<MoneyOperation> ApplyMoneyOperationVisibility(
        IQueryable<MoneyOperation> query,
        DataVisibilityAccess paymentsVisibility)
    {
        if (paymentsVisibility.SeesAll)
        {
            return query;
        }

        var visibleInvoiceIds = _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(paymentsVisibility)
            .Where(payment => payment.InvoiceId.HasValue)
            .Select(payment => payment.InvoiceId!.Value);

        return query.Where(operation =>
            operation.LinkedInvoiceId.HasValue
            && visibleInvoiceIds.Contains(operation.LinkedInvoiceId.Value));
    }

    private async Task<MoneyCategoryReportDto> BuildCategoryReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var query = BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Include(item => item.IncomeType);

        var rows = await query
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                IncomeTypeName = item.IncomeType != null ? item.IncomeType.Name : null,
                item.CategoryName,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);

        return new MoneyCategoryReportDto
        {
            From = from,
            To = to,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Income = BuildCategoryItems(
                rows.Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Select(item => (item.Amount, CategoryName: ResolveCategoryName(item.IncomeTypeName, item.CategoryName))),
                incomeTotal),
            Expense = BuildCategoryItems(
                rows.Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Select(item => (item.Amount, CategoryName: ResolveCategoryName(item.IncomeTypeName, item.CategoryName))),
                expenseTotal),
        };
    }

    private static List<MoneyCategoryReportItemDto> BuildCategoryItems(
        IEnumerable<(decimal Amount, string CategoryName)> rows,
        decimal total)
        => rows
            .GroupBy(item => item.CategoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var amount = group.Sum(item => item.Amount);

                return new MoneyCategoryReportItemDto
                {
                    CategoryName = group.First().CategoryName,
                    Amount = amount,
                    Count = group.Count(),
                    Share = total > 0m ? Math.Round(amount / total * 100m, 1, MidpointRounding.AwayFromZero) : 0m,
                };
            })
            .OrderByDescending(item => item.Amount)
            .ThenBy(item => item.CategoryName)
            .ToList();

    private static string ResolveCategoryName(string? incomeTypeName, string? categoryName)
    {
        if (!string.IsNullOrWhiteSpace(incomeTypeName))
        {
            return incomeTypeName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            return categoryName.Trim();
        }

        return "Без статьи";
    }

    private async Task<CounterpartyReportDto> BuildCounterpartyReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.CounterpartyName,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var counterparties = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                CounterpartyName = ResolveCounterpartyName(item.CounterpartyName),
            })
            .GroupBy(item => item.CounterpartyName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new CounterpartyReportItemDto
                {
                    CounterpartyName = group.First().CounterpartyName,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.CounterpartyName)
            .ToList();

        return new CounterpartyReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Counterparties = counterparties,
        };
    }

    private static string ResolveCounterpartyName(string? counterpartyName)
        => string.IsNullOrWhiteSpace(counterpartyName)
            ? "Без контрагента"
            : counterpartyName.Trim();

    private async Task<ProjectReportDto> BuildProjectReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.ProjectName,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var projects = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                ProjectName = ResolveProjectName(item.ProjectName),
            })
            .GroupBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new ProjectReportItemDto
                {
                    ProjectName = group.First().ProjectName,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.ProjectName)
            .ToList();

        return new ProjectReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Projects = projects,
        };
    }

    private static string ResolveProjectName(string? projectName)
        => string.IsNullOrWhiteSpace(projectName)
            ? "Без проекта"
            : projectName.Trim();

    private async Task<DirectionReportDto> BuildDirectionReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.DirectionName,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var directions = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                DirectionName = ResolveDirectionName(item.DirectionName),
            })
            .GroupBy(item => item.DirectionName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new DirectionReportItemDto
                {
                    DirectionName = group.First().DirectionName,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.DirectionName)
            .ToList();

        return new DirectionReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Directions = directions,
        };
    }

    private static string ResolveDirectionName(string? directionName)
        => string.IsNullOrWhiteSpace(directionName)
            ? "Без направления"
            : directionName.Trim();

    private async Task<SalesPointReportDto> BuildSalesPointReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.SalesPointName,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var salesPoints = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                SalesPointName = ResolveSalesPointName(item.SalesPointName),
            })
            .GroupBy(item => item.SalesPointName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new SalesPointReportItemDto
                {
                    SalesPointName = group.First().SalesPointName,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.SalesPointName)
            .ToList();

        return new SalesPointReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            SalesPoints = salesPoints,
        };
    }

    private static string ResolveSalesPointName(string? salesPointName)
        => string.IsNullOrWhiteSpace(salesPointName)
            ? "Без точки продаж"
            : salesPointName.Trim();

    private async Task<VatReportDto> BuildVatReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.VatRate,
                item.VatAmount,
            })
            .ToListAsync(ct);

        var incomeRows = rows.Where(item => item.Direction == MoneyOperationDirection.Income).ToList();
        var expenseRows = rows.Where(item => item.Direction == MoneyOperationDirection.Expense).ToList();

        var incomeVatTotal = incomeRows.Sum(item => item.VatAmount ?? 0m);
        var expenseVatTotal = expenseRows.Sum(item => item.VatAmount ?? 0m);

        var rates = rows
            .GroupBy(item => NormalizeVatRate(item.VatRate))
            .Select(group =>
            {
                var incomeGross = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var incomeVat = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.VatAmount ?? 0m);
                var expenseGross = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var expenseVat = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.VatAmount ?? 0m);

                return new VatReportItemDto
                {
                    VatRate = group.Key,
                    VatRateLabel = FormatVatRateLabel(group.Key),
                    IncomeBase = incomeGross - incomeVat,
                    IncomeVat = incomeVat,
                    IncomeGross = incomeGross,
                    ExpenseBase = expenseGross - expenseVat,
                    ExpenseVat = expenseVat,
                    ExpenseGross = expenseGross,
                    NetVat = incomeVat - expenseVat,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                };
            })
            .OrderBy(item => item.VatRate.HasValue ? 0 : 1)
            .ThenByDescending(item => item.VatRate)
            .ToList();

        return new VatReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeAmountTotal = incomeRows.Sum(item => item.Amount),
            IncomeVatTotal = incomeVatTotal,
            ExpenseAmountTotal = expenseRows.Sum(item => item.Amount),
            ExpenseVatTotal = expenseVatTotal,
            VatBalance = incomeVatTotal - expenseVatTotal,
            Rates = rates,
        };
    }

    private static decimal? NormalizeVatRate(decimal? vatRate)
        => vatRate.HasValue ? Math.Round(vatRate.Value, 2, MidpointRounding.AwayFromZero) : null;

    private static string FormatVatRateLabel(decimal? vatRate)
        => vatRate.HasValue ? $"{vatRate.Value:0.##}%" : "Без НДС";

    private async Task<PlanFactReportDto> BuildPlanFactReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildPlanFactQuery(from, to, source, paymentsVisibility)
            .Select(item => new PlanFactQueryRow
            {
                Date = item.Date,
                Direction = item.Direction,
                AccountingType = item.AccountingType,
                Amount = item.Amount,
            })
            .ToListAsync(ct);

        var planIncome = SumPlanFact(rows, MoneyOperationAccountingType.Plan, MoneyOperationDirection.Income);
        var factIncome = SumPlanFact(rows, MoneyOperationAccountingType.Fact, MoneyOperationDirection.Income);
        var planExpense = SumPlanFact(rows, MoneyOperationAccountingType.Plan, MoneyOperationDirection.Expense);
        var factExpense = SumPlanFact(rows, MoneyOperationAccountingType.Fact, MoneyOperationDirection.Expense);

        var days = rows
            .GroupBy(item => item.Date.Date)
            .Select(group =>
            {
                var dayPlanIncome = SumPlanFact(group, MoneyOperationAccountingType.Plan, MoneyOperationDirection.Income);
                var dayFactIncome = SumPlanFact(group, MoneyOperationAccountingType.Fact, MoneyOperationDirection.Income);
                var dayPlanExpense = SumPlanFact(group, MoneyOperationAccountingType.Plan, MoneyOperationDirection.Expense);
                var dayFactExpense = SumPlanFact(group, MoneyOperationAccountingType.Fact, MoneyOperationDirection.Expense);

                return new PlanFactDayDto
                {
                    Date = group.Key,
                    PlanIncome = dayPlanIncome,
                    FactIncome = dayFactIncome,
                    IncomeVariance = dayFactIncome - dayPlanIncome,
                    PlanExpense = dayPlanExpense,
                    FactExpense = dayFactExpense,
                    ExpenseVariance = dayFactExpense - dayPlanExpense,
                    PlanNetTotal = dayPlanIncome - dayPlanExpense,
                    FactNetTotal = dayFactIncome - dayFactExpense,
                    NetVariance = (dayFactIncome - dayFactExpense) - (dayPlanIncome - dayPlanExpense),
                };
            })
            .OrderBy(item => item.Date)
            .ToList();

        return new PlanFactReportDto
        {
            From = from,
            To = to,
            Source = source,
            PlanIncome = planIncome,
            FactIncome = factIncome,
            IncomeVariance = factIncome - planIncome,
            IncomeCompletionPercent = CalculatePercent(factIncome, planIncome),
            PlanExpense = planExpense,
            FactExpense = factExpense,
            ExpenseVariance = factExpense - planExpense,
            ExpenseExecutionPercent = CalculatePercent(factExpense, planExpense),
            PlanNetTotal = planIncome - planExpense,
            FactNetTotal = factIncome - factExpense,
            NetVariance = (factIncome - factExpense) - (planIncome - planExpense),
            Days = days,
        };
    }

    private IQueryable<MoneyOperation> BuildPlanFactQuery(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility)
    {
        var query = _db.MoneyOperations
            .AsNoTracking()
            .Where(item =>
                (item.AccountingType == MoneyOperationAccountingType.Plan ||
                    item.AccountingType == MoneyOperationAccountingType.Fact) &&
                (item.Direction == MoneyOperationDirection.Income || item.Direction == MoneyOperationDirection.Expense) &&
                item.Date >= from &&
                item.Date <= to);

        if (source.HasValue)
        {
            query = query.Where(item => item.Source == source.Value);
        }

        return ApplyMoneyOperationVisibility(query, paymentsVisibility);
    }

    private static decimal SumPlanFact(
        IEnumerable<PlanFactQueryRow> rows,
        MoneyOperationAccountingType accountingType,
        MoneyOperationDirection direction)
        => rows
            .Where(item => item.AccountingType == accountingType && item.Direction == direction)
            .Sum(item => item.Amount);

    private static decimal CalculatePercent(decimal value, decimal total)
        => total > 0m ? Math.Round(value / total * 100m, 1, MidpointRounding.AwayFromZero) : 0m;

    private async Task<AdvanceReportDto> BuildAdvanceReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Where(item => item.IsAdvancePayment)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.CounterpartyName,
                item.RecognitionDate,
            })
            .ToListAsync(ct);

        var enriched = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                CounterpartyName = ResolveCounterpartyName(item.CounterpartyName),
                IsRecognized = item.RecognitionDate.HasValue && item.RecognitionDate.Value.Date <= to,
            })
            .ToList();

        var counterparties = enriched
            .GroupBy(item => item.CounterpartyName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AdvanceReportItemDto
            {
                CounterpartyName = group.First().CounterpartyName,
                IncomeTotal = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount),
                ExpenseTotal = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount),
                RecognizedTotal = group
                    .Where(item => item.IsRecognized)
                    .Sum(item => item.Amount),
                UnrecognizedTotal = group
                    .Where(item => !item.IsRecognized)
                    .Sum(item => item.Amount),
                IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                RecognizedCount = group.Count(item => item.IsRecognized),
                UnrecognizedCount = group.Count(item => !item.IsRecognized),
            })
            .OrderByDescending(item => item.IncomeTotal + item.ExpenseTotal)
            .ThenBy(item => item.CounterpartyName)
            .ToList();

        return new AdvanceReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = enriched
                .Where(item => item.Direction == MoneyOperationDirection.Income)
                .Sum(item => item.Amount),
            ExpenseTotal = enriched
                .Where(item => item.Direction == MoneyOperationDirection.Expense)
                .Sum(item => item.Amount),
            RecognizedTotal = enriched
                .Where(item => item.IsRecognized)
                .Sum(item => item.Amount),
            UnrecognizedTotal = enriched
                .Where(item => !item.IsRecognized)
                .Sum(item => item.Amount),
            RecognizedCount = enriched.Count(item => item.IsRecognized),
            UnrecognizedCount = enriched.Count(item => !item.IsRecognized),
            Counterparties = counterparties,
        };
    }

    private async Task<PaymentPriorityReportDto> BuildPaymentPriorityReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.PaymentPriority,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var priorities = rows
            .GroupBy(item => item.PaymentPriority)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new PaymentPriorityReportItemDto
                {
                    Priority = group.Key,
                    PriorityLabel = FormatPriorityLabel(group.Key),
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderBy(item => item.Priority ?? int.MaxValue)
            .ThenByDescending(item => item.TotalAmount)
            .ToList();

        return new PaymentPriorityReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Priorities = priorities,
        };
    }

    private static string FormatPriorityLabel(int? priority)
        => priority switch
        {
            1 => "Высокий приоритет",
            2 => "Средний приоритет",
            3 => "Низкий приоритет",
            null => "Без приоритета",
            _ => $"Приоритет {priority.Value}",
        };

    private async Task<PaidAccountReportDto> BuildPaidAccountReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.PaidAccountName,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var accounts = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                PaidAccountName = ResolvePaidAccountName(item.PaidAccountName),
            })
            .GroupBy(item => item.PaidAccountName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new PaidAccountReportItemDto
                {
                    PaidAccountName = group.First().PaidAccountName,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.PaidAccountName)
            .ToList();

        return new PaidAccountReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Accounts = accounts,
        };
    }

    private static string ResolvePaidAccountName(string? paidAccountName)
        => string.IsNullOrWhiteSpace(paidAccountName)
            ? "Без счета оплаты"
            : paidAccountName.Trim();

    private async Task<PaidInvoiceReportDto> BuildPaidInvoiceReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.PaidInvoiceNumber,
                item.PaidAmount,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var invoices = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                PaidAmount = item.PaidAmount ?? item.Amount,
                PaidInvoiceNumber = ResolvePaidInvoiceNumber(item.PaidInvoiceNumber),
            })
            .GroupBy(item => item.PaidInvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var operationTotal = income + expense;

                return new PaidInvoiceReportItemDto
                {
                    PaidInvoiceNumber = group.First().PaidInvoiceNumber,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    OperationTotal = operationTotal,
                    PaidAmountTotal = group.Sum(item => item.PaidAmount),
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(operationTotal / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.PaidAmountTotal)
            .ThenBy(item => item.PaidInvoiceNumber)
            .ToList();

        return new PaidInvoiceReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            PaidAmountTotal = invoices.Sum(item => item.PaidAmountTotal),
            Invoices = invoices,
        };
    }

    private static string ResolvePaidInvoiceNumber(string? paidInvoiceNumber)
        => string.IsNullOrWhiteSpace(paidInvoiceNumber)
            ? "Без номера счета"
            : paidInvoiceNumber.Trim();

    private async Task<InvoiceStatusReportDto> BuildInvoiceStatusReportAsync(
        DateTime from,
        DateTime to,
        DataVisibilityAccess accountsVisibility,
        CancellationToken ct)
    {
        var rows = await _db.Payments
            .AsNoTracking()
            .ApplyPaymentVisibility(accountsVisibility)
            .Where(item => item.DocumentKind == DocumentKind.Invoice && item.Date >= from && item.Date <= to)
            .Select(item => new
            {
                item.Type,
                item.Status,
                WorkflowStatus = item.InvoiceWorkflowStatus,
                item.Amount,
                item.PaidAmount,
            })
            .ToListAsync(ct);

        var totalAmount = rows.Sum(item => item.Amount);
        var statuses = rows
            .Select(item => new
            {
                item.Type,
                item.Status,
                WorkflowStatus = NormalizeInvoiceReportStatus(item.WorkflowStatus, item.Status, item.PaidAmount, item.Amount),
                item.Amount,
                PaidAmount = Math.Min(item.PaidAmount, item.Amount),
                OutstandingAmount = item.Amount > item.PaidAmount
                    ? Math.Round(item.Amount - item.PaidAmount, 2)
                    : 0m,
            })
            .GroupBy(item => item.WorkflowStatus, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var statusTotal = group.Sum(item => item.Amount);

                return new InvoiceStatusReportItemDto
                {
                    Status = group.First().WorkflowStatus,
                    StatusLabel = FormatInvoiceWorkflowStatusLabel(group.First().WorkflowStatus),
                    InvoiceCount = group.Count(),
                    IncomeCount = group.Count(item => item.Type == PaymentType.Income),
                    ExpenseCount = group.Count(item => item.Type == PaymentType.Expense),
                    PaidCount = group.Count(item => item.Status == PaymentStatus.Completed),
                    OverdueCount = group.Count(item => item.Status == PaymentStatus.Overdue),
                    TotalAmount = statusTotal,
                    PaidAmount = group.Sum(item => item.PaidAmount),
                    OutstandingAmount = group.Sum(item => item.OutstandingAmount),
                    Share = totalAmount > 0m
                        ? Math.Round(statusTotal / totalAmount * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderBy(item => InvoiceWorkflowStatusSortOrder(item.Status))
            .ThenByDescending(item => item.TotalAmount)
            .ToList();

        return new InvoiceStatusReportDto
        {
            From = from,
            To = to,
            InvoiceCount = rows.Count,
            IncomeCount = rows.Count(item => item.Type == PaymentType.Income),
            ExpenseCount = rows.Count(item => item.Type == PaymentType.Expense),
            PaidCount = rows.Count(item => item.Status == PaymentStatus.Completed),
            OverdueCount = rows.Count(item => item.Status == PaymentStatus.Overdue),
            TotalAmount = totalAmount,
            PaidAmount = rows.Sum(item => Math.Min(item.PaidAmount, item.Amount)),
            OutstandingAmount = rows.Sum(item => item.Amount > item.PaidAmount
                ? Math.Round(item.Amount - item.PaidAmount, 2)
                : 0m),
            Statuses = statuses,
        };
    }

    private static string NormalizeInvoiceReportStatus(
        string? workflowStatus,
        PaymentStatus paymentStatus,
        decimal paidAmount,
        decimal amount)
    {
        var normalized = InvoiceWorkflowStatus.Normalize(workflowStatus);
        return normalized ?? InvoiceWorkflowStatus.FromPaymentStatus(paymentStatus, paidAmount, amount);
    }

    private static int InvoiceWorkflowStatusSortOrder(string status)
        => status switch
        {
            InvoiceWorkflowStatus.RequiresClientSignature => 0,
            InvoiceWorkflowStatus.OnApproval => 1,
            InvoiceWorkflowStatus.ApprovalRejected => 2,
            InvoiceWorkflowStatus.NeedsCorrection => 3,
            InvoiceWorkflowStatus.OnSigning => 4,
            InvoiceWorkflowStatus.NotSigned => 5,
            InvoiceWorkflowStatus.Approved => 6,
            InvoiceWorkflowStatus.SignedByClient => 7,
            InvoiceWorkflowStatus.Draft => 8,
            InvoiceWorkflowStatus.Overdue => 9,
            InvoiceWorkflowStatus.PartiallyPaid => 10,
            InvoiceWorkflowStatus.Paid => 11,
            InvoiceWorkflowStatus.Cancelled => 12,
            _ => 99,
        };

    private static string FormatInvoiceWorkflowStatusLabel(string status)
        => status switch
        {
            InvoiceWorkflowStatus.Draft => "Черновик",
            InvoiceWorkflowStatus.NotSigned => "Не подписан",
            InvoiceWorkflowStatus.RequiresClientSignature => "Требуется подпись клиента",
            InvoiceWorkflowStatus.OnApproval => "На согласовании",
            InvoiceWorkflowStatus.Approved => "Согласован",
            InvoiceWorkflowStatus.ApprovalRejected => "Отказ в согласовании",
            InvoiceWorkflowStatus.NeedsCorrection => "Требует исправления",
            InvoiceWorkflowStatus.OnSigning => "На подписании",
            InvoiceWorkflowStatus.SignedByClient => "Подписан клиентом",
            InvoiceWorkflowStatus.Paid => "Оплачен",
            InvoiceWorkflowStatus.PartiallyPaid => "Частично оплачен",
            InvoiceWorkflowStatus.Overdue => "Просрочен",
            InvoiceWorkflowStatus.Cancelled => "Аннулирован",
            _ => status,
        };

    private async Task<ActStatusReportDto> BuildActStatusReportAsync(
        DateTime from,
        DateTime to,
        DataVisibilityAccess actsVisibility,
        CancellationToken ct)
    {
        var rows = await _db.Acts
            .AsNoTracking()
            .ApplyActVisibility(actsVisibility)
            .Where(item => item.Date >= from && item.Date <= to)
            .Select(item => new
            {
                item.Status,
                WorkflowStatus = item.WorkflowStatus,
                item.Amount,
            })
            .ToListAsync(ct);

        var totalAmount = rows.Sum(item => item.Amount);
        var enriched = rows
            .Select(item => new
            {
                item.Status,
                WorkflowStatus = NormalizeActReportWorkflowStatus(item.WorkflowStatus, item.Status),
                item.Amount,
            })
            .ToList();

        var statuses = enriched
            .GroupBy(item => item.WorkflowStatus, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var statusTotal = group.Sum(item => item.Amount);
                var signedAmount = group
                    .Where(item => item.Status == ActStatus.Signed)
                    .Sum(item => item.Amount);

                return new ActStatusReportItemDto
                {
                    WorkflowStatus = group.First().WorkflowStatus,
                    WorkflowStatusLabel = FormatActWorkflowStatusLabel(group.First().WorkflowStatus),
                    ActCount = group.Count(),
                    CreatedCount = group.Count(item => item.Status == ActStatus.Created),
                    TransferredCount = group.Count(item => item.Status == ActStatus.Transferred),
                    SignedCount = group.Count(item => item.Status == ActStatus.Signed),
                    TerminatedCount = group.Count(item => item.Status == ActStatus.Terminated),
                    TotalAmount = statusTotal,
                    SignedAmount = signedAmount,
                    PendingAmount = statusTotal - signedAmount,
                    Share = totalAmount > 0m
                        ? Math.Round(statusTotal / totalAmount * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderBy(item => ActWorkflowStatusSortOrder(item.WorkflowStatus))
            .ThenByDescending(item => item.TotalAmount)
            .ToList();

        var totalSignedAmount = enriched
            .Where(item => item.Status == ActStatus.Signed)
            .Sum(item => item.Amount);

        return new ActStatusReportDto
        {
            From = from,
            To = to,
            ActCount = enriched.Count,
            CreatedCount = enriched.Count(item => item.Status == ActStatus.Created),
            TransferredCount = enriched.Count(item => item.Status == ActStatus.Transferred),
            SignedCount = enriched.Count(item => item.Status == ActStatus.Signed),
            TerminatedCount = enriched.Count(item => item.Status == ActStatus.Terminated),
            TotalAmount = totalAmount,
            SignedAmount = totalSignedAmount,
            PendingAmount = totalAmount - totalSignedAmount,
            Statuses = statuses,
        };
    }

    private static string NormalizeActReportWorkflowStatus(string? workflowStatus, ActStatus status)
    {
        var normalized = ActWorkflowStatus.Normalize(workflowStatus);
        return normalized ?? ActWorkflowStatus.FromActStatus(status);
    }

    private static int ActWorkflowStatusSortOrder(string status)
        => status switch
        {
            ActWorkflowStatus.RequiresClientSignature => 0,
            ActWorkflowStatus.OnApproval => 1,
            ActWorkflowStatus.ApprovalRejected => 2,
            ActWorkflowStatus.NeedsCorrection => 3,
            ActWorkflowStatus.OnSigning => 4,
            ActWorkflowStatus.NotSigned => 5,
            ActWorkflowStatus.Draft => 6,
            ActWorkflowStatus.Approved => 7,
            ActWorkflowStatus.Signed => 8,
            ActWorkflowStatus.Cancelled => 9,
            _ => 10,
        };

    private static string FormatActWorkflowStatusLabel(string status)
        => status switch
        {
            ActWorkflowStatus.Draft => "Черновик",
            ActWorkflowStatus.NotSigned => "Не подписан",
            ActWorkflowStatus.RequiresClientSignature => "Требуется подпись клиента",
            ActWorkflowStatus.OnApproval => "На согласовании",
            ActWorkflowStatus.Approved => "Согласован",
            ActWorkflowStatus.ApprovalRejected => "Отказ в согласовании",
            ActWorkflowStatus.NeedsCorrection => "Требует исправления",
            ActWorkflowStatus.OnSigning => "На подписании",
            ActWorkflowStatus.Signed => "Подписан",
            ActWorkflowStatus.Cancelled => "Аннулирован",
            _ => status,
        };

    private async Task<ContractStatusReportDto> BuildContractStatusReportAsync(
        DateTime from,
        DateTime to,
        DataVisibilityAccess contractsVisibility,
        CancellationToken ct)
    {
        var rows = await _db.Contracts
            .AsNoTracking()
            .ApplyContractVisibility(contractsVisibility)
            .Where(item => item.Date >= from && item.Date <= to)
            .Select(item => new
            {
                item.Status,
                item.Amount,
                item.IsOpenEnded,
                item.ValidUntil,
            })
            .ToListAsync(ct);

        var normalizedRows = rows
            .Select(item => new
            {
                Status = NormalizeContractReportStatus(item.Status),
                Amount = item.Amount ?? 0m,
                item.IsOpenEnded,
                item.ValidUntil,
            })
            .ToList();

        var totalAmount = normalizedRows.Sum(item => item.Amount);
        var statuses = normalizedRows
            .GroupBy(item => item.Status, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var statusTotal = group.Sum(item => item.Amount);

                return new ContractStatusReportItemDto
                {
                    Status = group.First().Status,
                    StatusLabel = FormatContractStatusLabel(group.First().Status),
                    ContractCount = group.Count(),
                    OpenEndedCount = group.Count(item => item.IsOpenEnded),
                    ExpiringCount = group.Count(item =>
                        !item.IsOpenEnded &&
                        item.ValidUntil.HasValue &&
                        item.ValidUntil.Value.Date >= from &&
                        item.ValidUntil.Value.Date <= to),
                    TotalAmount = statusTotal,
                    Share = totalAmount > 0m
                        ? Math.Round(statusTotal / totalAmount * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderBy(item => ContractStatusSortOrder(item.Status))
            .ThenByDescending(item => item.TotalAmount)
            .ToList();

        var activeStatuses = new[]
        {
            ContractStatus.Draft,
            ContractStatus.NotSigned,
            ContractStatus.RequiresClientSignature,
            ContractStatus.OnApproval,
            ContractStatus.Approved,
            ContractStatus.NeedsCorrection,
            ContractStatus.OnSigning,
            ContractStatus.Concluded
        };

        return new ContractStatusReportDto
        {
            From = from,
            To = to,
            ContractCount = normalizedRows.Count,
            DraftCount = normalizedRows.Count(item => item.Status == ContractStatus.Draft),
            NotSignedCount = normalizedRows.Count(item => item.Status == ContractStatus.NotSigned),
            OnSigningCount = normalizedRows.Count(item => item.Status == ContractStatus.OnSigning),
            ConcludedCount = normalizedRows.Count(item => item.Status == ContractStatus.Concluded),
            TerminatedCount = normalizedRows.Count(item => item.Status == ContractStatus.Terminated),
            CancelledCount = normalizedRows.Count(item => item.Status == ContractStatus.Cancelled),
            OpenEndedCount = normalizedRows.Count(item => item.IsOpenEnded),
            TotalAmount = totalAmount,
            ActiveAmount = normalizedRows
                .Where(item => activeStatuses.Contains(item.Status))
                .Sum(item => item.Amount),
            ClosedAmount = normalizedRows
                .Where(item => item.Status is ContractStatus.Terminated or ContractStatus.Cancelled)
                .Sum(item => item.Amount),
            Statuses = statuses,
        };
    }

    private static string NormalizeContractReportStatus(string? status)
        => ContractStatus.Normalize(status) ?? ContractStatus.Draft;

    private static int ContractStatusSortOrder(string status)
        => status switch
        {
            ContractStatus.RequiresClientSignature => 0,
            ContractStatus.OnApproval => 1,
            ContractStatus.ApprovalRejected => 2,
            ContractStatus.NeedsCorrection => 3,
            ContractStatus.OnSigning => 4,
            ContractStatus.NotSigned => 5,
            ContractStatus.Draft => 6,
            ContractStatus.Approved => 7,
            ContractStatus.Concluded => 8,
            ContractStatus.Terminated => 9,
            ContractStatus.Cancelled => 10,
            _ => 11,
        };

    private static string FormatContractStatusLabel(string status)
        => status switch
        {
            ContractStatus.Draft => "Черновик",
            ContractStatus.NotSigned => "Не подписан",
            ContractStatus.RequiresClientSignature => "Требуется подпись клиента",
            ContractStatus.OnApproval => "На согласовании",
            ContractStatus.Approved => "Согласован",
            ContractStatus.ApprovalRejected => "Отказ в согласовании",
            ContractStatus.NeedsCorrection => "Требует исправления",
            ContractStatus.OnSigning => "На подписании",
            ContractStatus.Concluded => "Подписан",
            ContractStatus.Terminated => "Расторгнут",
            ContractStatus.Cancelled => "Аннулирован",
            _ => status,
        };

    private async Task<PaymentOrderReportDto> BuildPaymentOrderReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.PaymentOrderNumber,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var orders = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                PaymentOrderNumber = ResolvePaymentOrderNumber(item.PaymentOrderNumber),
            })
            .GroupBy(item => item.PaymentOrderNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new PaymentOrderReportItemDto
                {
                    PaymentOrderNumber = group.First().PaymentOrderNumber,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.PaymentOrderNumber)
            .ToList();

        return new PaymentOrderReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Orders = orders,
        };
    }

    private static string ResolvePaymentOrderNumber(string? paymentOrderNumber)
        => string.IsNullOrWhiteSpace(paymentOrderNumber)
            ? "Без номера платежки"
            : paymentOrderNumber.Trim();

    private async Task<PaymentPurposeReportDto> BuildPaymentPurposeReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.Purpose,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var purposes = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                Purpose = ResolvePaymentPurpose(item.Purpose),
            })
            .GroupBy(item => item.Purpose, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new PaymentPurposeReportItemDto
                {
                    Purpose = group.First().Purpose,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.Purpose)
            .ToList();

        return new PaymentPurposeReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Purposes = purposes,
        };
    }

    private static string ResolvePaymentPurpose(string? purpose)
        => string.IsNullOrWhiteSpace(purpose)
            ? "Без назначения"
            : purpose.Trim();

    private async Task<MatchStatusReportDto> BuildMatchStatusReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.MatchStatus,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var statuses = rows
            .GroupBy(item => item.MatchStatus)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new MatchStatusReportItemDto
                {
                    Status = group.Key,
                    StatusLabel = FormatMatchStatusLabel(group.Key),
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    OperationCount = group.Count(),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderBy(item => MatchStatusSortOrder(item.Status))
            .ThenByDescending(item => item.TotalAmount)
            .ToList();

        return new MatchStatusReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            OperationCount = rows.Count,
            MatchedCount = rows.Count(item => item.MatchStatus == MoneyOperationMatchStatus.Matched),
            NeedsAttentionCount = rows.Count(item =>
                item.MatchStatus == MoneyOperationMatchStatus.None ||
                item.MatchStatus == MoneyOperationMatchStatus.ClarificationRequired),
            Statuses = statuses,
        };
    }

    private static int MatchStatusSortOrder(MoneyOperationMatchStatus status)
        => status switch
        {
            MoneyOperationMatchStatus.ClarificationRequired => 0,
            MoneyOperationMatchStatus.None => 1,
            MoneyOperationMatchStatus.Matched => 2,
            MoneyOperationMatchStatus.Ignored => 3,
            _ => 4,
        };

    private static string FormatMatchStatusLabel(MoneyOperationMatchStatus status)
        => status switch
        {
            MoneyOperationMatchStatus.None => "Не сверено",
            MoneyOperationMatchStatus.ClarificationRequired => "Требует уточнения",
            MoneyOperationMatchStatus.Matched => "Сверено",
            MoneyOperationMatchStatus.Ignored => "Исключено",
            _ => status.ToString(),
        };

    private async Task<JournalStatusReportDto> BuildJournalStatusReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Source,
                item.Direction,
                item.AccountingType,
                item.MatchStatus,
                item.Amount,
            })
            .ToListAsync(ct);

        var enriched = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                Status = ResolveJournalStatus(item.Source, item.Direction, item.AccountingType, item.MatchStatus),
            })
            .ToList();

        var incomeTotal = enriched
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = enriched
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var statuses = enriched
            .GroupBy(item => item.Status)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new JournalStatusReportItemDto
                {
                    Status = group.Key,
                    StatusLabel = FormatJournalStatusLabel(group.Key),
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    OperationCount = group.Count(),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderBy(item => JournalStatusSortOrder(item.Status))
            .ThenByDescending(item => item.TotalAmount)
            .ToList();

        return new JournalStatusReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            OperationCount = enriched.Count,
            PostedCount = enriched.Count(item => item.Status == MoneyOperationJournalStatus.Posted),
            ProcessingCount = enriched.Count(item => item.Status == MoneyOperationJournalStatus.Processing),
            ErrorCount = enriched.Count(item => item.Status == MoneyOperationJournalStatus.Error),
            Statuses = statuses,
        };
    }

    private static MoneyOperationJournalStatus ResolveJournalStatus(
        MoneyOperationSource source,
        MoneyOperationDirection direction,
        MoneyOperationAccountingType accountingType,
        MoneyOperationMatchStatus matchStatus)
    {
        if (matchStatus == MoneyOperationMatchStatus.ClarificationRequired)
        {
            return MoneyOperationJournalStatus.Error;
        }

        if (accountingType == MoneyOperationAccountingType.Plan)
        {
            return MoneyOperationJournalStatus.Processing;
        }

        if (source == MoneyOperationSource.Bank &&
            direction == MoneyOperationDirection.Income &&
            matchStatus == MoneyOperationMatchStatus.None)
        {
            return MoneyOperationJournalStatus.Processing;
        }

        return MoneyOperationJournalStatus.Posted;
    }

    private static int JournalStatusSortOrder(MoneyOperationJournalStatus status)
        => status switch
        {
            MoneyOperationJournalStatus.Error => 0,
            MoneyOperationJournalStatus.Processing => 1,
            MoneyOperationJournalStatus.Posted => 2,
            _ => 3,
        };

    private static string FormatJournalStatusLabel(MoneyOperationJournalStatus status)
        => status switch
        {
            MoneyOperationJournalStatus.Posted => "Проведено",
            MoneyOperationJournalStatus.Processing => "В обработке",
            MoneyOperationJournalStatus.Error => "Ошибка",
            _ => status.ToString(),
        };

    private async Task<CurrencyReportDto> BuildCurrencyReportAsync(
        DateTime from,
        DateTime to,
        MoneyOperationSource? source,
        DataVisibilityAccess paymentsVisibility,
        CancellationToken ct)
    {
        var rows = await BuildFactQuery(from, to, source, null, paymentsVisibility)
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                item.Currency,
            })
            .ToListAsync(ct);

        var incomeTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Income)
            .Sum(item => item.Amount);
        var expenseTotal = rows
            .Where(item => item.Direction == MoneyOperationDirection.Expense)
            .Sum(item => item.Amount);
        var turnoverTotal = incomeTotal + expenseTotal;

        var currencies = rows
            .Select(item => new
            {
                item.Direction,
                item.Amount,
                Currency = ResolveCurrency(item.Currency),
            })
            .GroupBy(item => item.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var income = group
                    .Where(item => item.Direction == MoneyOperationDirection.Income)
                    .Sum(item => item.Amount);
                var expense = group
                    .Where(item => item.Direction == MoneyOperationDirection.Expense)
                    .Sum(item => item.Amount);
                var totalAmount = income + expense;

                return new CurrencyReportItemDto
                {
                    Currency = group.First().Currency,
                    Income = income,
                    Expense = expense,
                    NetTotal = income - expense,
                    TotalAmount = totalAmount,
                    IncomeCount = group.Count(item => item.Direction == MoneyOperationDirection.Income),
                    ExpenseCount = group.Count(item => item.Direction == MoneyOperationDirection.Expense),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderByDescending(item => item.TotalAmount)
            .ThenBy(item => item.Currency)
            .ToList();

        return new CurrencyReportDto
        {
            From = from,
            To = to,
            Source = source,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            NetTotal = incomeTotal - expenseTotal,
            Currencies = currencies,
        };
    }

    private static string ResolveCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency)
            ? "RUB"
            : currency.Trim().ToUpperInvariant();

    private async Task<CashOrderReportDto> BuildCashOrderReportAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var rows = await _db.CashOrders
            .AsNoTracking()
            .Where(item => item.Date >= from && item.Date <= to)
            .Select(item => new
            {
                item.Status,
                item.Type,
                item.Amount,
            })
            .ToListAsync(ct);

        var turnoverTotal = rows.Sum(item => item.Amount);
        var statuses = rows
            .GroupBy(item => item.Status)
            .Select(group =>
            {
                var incomingTotal = group
                    .Where(item => item.Type == CashOrderType.Incoming)
                    .Sum(item => item.Amount);
                var outgoingTotal = group
                    .Where(item => item.Type == CashOrderType.Outgoing)
                    .Sum(item => item.Amount);
                var totalAmount = incomingTotal + outgoingTotal;

                return new CashOrderReportItemDto
                {
                    Status = group.Key,
                    StatusLabel = FormatCashOrderStatusLabel(group.Key),
                    IncomingTotal = incomingTotal,
                    OutgoingTotal = outgoingTotal,
                    NetTotal = incomingTotal - outgoingTotal,
                    TotalAmount = totalAmount,
                    IncomingCount = group.Count(item => item.Type == CashOrderType.Incoming),
                    OutgoingCount = group.Count(item => item.Type == CashOrderType.Outgoing),
                    OrderCount = group.Count(),
                    Share = turnoverTotal > 0m
                        ? Math.Round(totalAmount / turnoverTotal * 100m, 1, MidpointRounding.AwayFromZero)
                        : 0m,
                };
            })
            .OrderBy(item => CashOrderStatusSortOrder(item.Status))
            .ThenByDescending(item => item.TotalAmount)
            .ToList();

        var postedRows = rows.Where(item => item.Status == CashOrderStatus.Posted).ToList();
        var postedIncomingTotal = postedRows
            .Where(item => item.Type == CashOrderType.Incoming)
            .Sum(item => item.Amount);
        var postedOutgoingTotal = postedRows
            .Where(item => item.Type == CashOrderType.Outgoing)
            .Sum(item => item.Amount);

        return new CashOrderReportDto
        {
            From = from,
            To = to,
            PostedIncomingTotal = postedIncomingTotal,
            PostedOutgoingTotal = postedOutgoingTotal,
            PostedNetTotal = postedIncomingTotal - postedOutgoingTotal,
            OrderCount = rows.Count,
            DraftCount = rows.Count(item => item.Status == CashOrderStatus.Draft),
            PostedCount = rows.Count(item => item.Status == CashOrderStatus.Posted),
            CancelledCount = rows.Count(item => item.Status == CashOrderStatus.Cancelled),
            Statuses = statuses,
        };
    }

    private static int CashOrderStatusSortOrder(CashOrderStatus status)
        => status switch
        {
            CashOrderStatus.Draft => 0,
            CashOrderStatus.Posted => 1,
            CashOrderStatus.Cancelled => 2,
            _ => 3,
        };

    private static string FormatCashOrderStatusLabel(CashOrderStatus status)
        => status switch
        {
            CashOrderStatus.Draft => "Черновик",
            CashOrderStatus.Posted => "Проведен",
            CashOrderStatus.Cancelled => "Отменен",
            _ => status.ToString(),
        };

    private async Task<BankStatementImportReportDto> BuildBankStatementImportReportAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var endExclusive = to.AddDays(1);
        var rows = await _db.BankStatementImportBatches
            .AsNoTracking()
            .Where(item => item.CreatedAt >= from && item.CreatedAt < endExclusive)
            .Select(item => new
            {
                item.Status,
                item.RowsRead,
                item.CreatedRows,
                item.SkippedRows,
                item.ErrorRows,
                item.RollbackRows,
            })
            .ToListAsync(ct);

        var statuses = rows
            .GroupBy(item => item.Status)
            .Select(group => new BankStatementImportReportItemDto
            {
                Status = group.Key,
                StatusLabel = FormatBankStatementImportStatusLabel(group.Key),
                BatchCount = group.Count(),
                RowsRead = group.Sum(item => item.RowsRead),
                CreatedRows = group.Sum(item => item.CreatedRows),
                SkippedRows = group.Sum(item => item.SkippedRows),
                ErrorRows = group.Sum(item => item.ErrorRows),
                RollbackRows = group.Sum(item => item.RollbackRows),
                Share = rows.Count > 0
                    ? Math.Round((decimal)group.Count() / rows.Count * 100m, 1, MidpointRounding.AwayFromZero)
                    : 0m,
            })
            .OrderBy(item => BankStatementImportStatusSortOrder(item.Status))
            .ThenByDescending(item => item.ErrorRows)
            .ThenByDescending(item => item.RowsRead)
            .ToList();

        return new BankStatementImportReportDto
        {
            From = from,
            To = to,
            BatchCount = rows.Count,
            RowsReadTotal = rows.Sum(item => item.RowsRead),
            CreatedRowsTotal = rows.Sum(item => item.CreatedRows),
            SkippedRowsTotal = rows.Sum(item => item.SkippedRows),
            ErrorRowsTotal = rows.Sum(item => item.ErrorRows),
            RollbackRowsTotal = rows.Sum(item => item.RollbackRows),
            AppliedCount = rows.Count(item => item.Status == BankStatementImportBatchStatus.Applied),
            AppliedWithErrorsCount = rows.Count(item => item.Status == BankStatementImportBatchStatus.AppliedWithErrors),
            RolledBackCount = rows.Count(item => item.Status == BankStatementImportBatchStatus.RolledBack),
            RollbackBlockedCount = rows.Count(item => item.Status == BankStatementImportBatchStatus.RollbackBlocked),
            Statuses = statuses,
        };
    }

    private static int BankStatementImportStatusSortOrder(string status)
        => status switch
        {
            BankStatementImportBatchStatus.AppliedWithErrors => 0,
            BankStatementImportBatchStatus.RollbackBlocked => 1,
            BankStatementImportBatchStatus.Applied => 2,
            BankStatementImportBatchStatus.RolledBack => 3,
            _ => 4,
        };

    private static string FormatBankStatementImportStatusLabel(string status)
        => status switch
        {
            BankStatementImportBatchStatus.Applied => "Применен",
            BankStatementImportBatchStatus.AppliedWithErrors => "Применен с ошибками",
            BankStatementImportBatchStatus.RolledBack => "Откатан",
            BankStatementImportBatchStatus.RollbackBlocked => "Откат заблокирован",
            _ => status,
        };

    private async Task<SpreadsheetImportReportDto> BuildSpreadsheetImportReportAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var endExclusive = to.AddDays(1);
        var rows = await _db.SpreadsheetImportBatches
            .AsNoTracking()
            .Where(item => item.CreatedAt >= from && item.CreatedAt < endExclusive)
            .Select(item => new
            {
                item.Status,
                FileCount = item.Files.Count,
                item.TotalRows,
                item.CreatedRows,
                item.UpdatedRows,
                item.SkippedRows,
                item.ErrorRows,
                item.RollbackRows,
            })
            .ToListAsync(ct);

        var statuses = rows
            .GroupBy(item => item.Status)
            .Select(group => new SpreadsheetImportReportItemDto
            {
                Status = group.Key,
                StatusLabel = FormatSpreadsheetImportStatusLabel(group.Key),
                BatchCount = group.Count(),
                FileCount = group.Sum(item => item.FileCount),
                TotalRows = group.Sum(item => item.TotalRows),
                CreatedRows = group.Sum(item => item.CreatedRows),
                UpdatedRows = group.Sum(item => item.UpdatedRows),
                SkippedRows = group.Sum(item => item.SkippedRows),
                ErrorRows = group.Sum(item => item.ErrorRows),
                RollbackRows = group.Sum(item => item.RollbackRows),
                Share = rows.Count > 0
                    ? Math.Round((decimal)group.Count() / rows.Count * 100m, 1, MidpointRounding.AwayFromZero)
                    : 0m,
            })
            .OrderBy(item => SpreadsheetImportStatusSortOrder(item.Status))
            .ThenByDescending(item => item.ErrorRows)
            .ThenByDescending(item => item.TotalRows)
            .ToList();

        return new SpreadsheetImportReportDto
        {
            From = from,
            To = to,
            BatchCount = rows.Count,
            FileCount = rows.Sum(item => item.FileCount),
            TotalRows = rows.Sum(item => item.TotalRows),
            CreatedRows = rows.Sum(item => item.CreatedRows),
            UpdatedRows = rows.Sum(item => item.UpdatedRows),
            SkippedRows = rows.Sum(item => item.SkippedRows),
            ErrorRows = rows.Sum(item => item.ErrorRows),
            RollbackRows = rows.Sum(item => item.RollbackRows),
            AppliedCount = rows.Count(item => item.Status == SpreadsheetImportBatchStatus.Applied),
            AppliedWithErrorsCount = rows.Count(item => item.Status == SpreadsheetImportBatchStatus.AppliedWithErrors),
            RolledBackCount = rows.Count(item => item.Status == SpreadsheetImportBatchStatus.RolledBack),
            RolledBackWithConflictsCount = rows.Count(item => item.Status == SpreadsheetImportBatchStatus.RolledBackWithConflicts),
            Statuses = statuses,
        };
    }

    private static int SpreadsheetImportStatusSortOrder(string status)
        => status switch
        {
            SpreadsheetImportBatchStatus.AppliedWithErrors => 0,
            SpreadsheetImportBatchStatus.RolledBackWithConflicts => 1,
            SpreadsheetImportBatchStatus.Applied => 2,
            SpreadsheetImportBatchStatus.RolledBack => 3,
            _ => 4,
        };

    private static string FormatSpreadsheetImportStatusLabel(string status)
        => status switch
        {
            SpreadsheetImportBatchStatus.Applied => "Применен",
            SpreadsheetImportBatchStatus.AppliedWithErrors => "Применен с ошибками",
            SpreadsheetImportBatchStatus.RolledBack => "Откатан",
            SpreadsheetImportBatchStatus.RolledBackWithConflicts => "Откатан с конфликтами",
            _ => status,
        };

    private sealed record LegalWorkTaskRow(
        int Id,
        int? ClientCaseId,
        int? AssigneeUserId,
        string AssigneeName,
        string Status,
        string Priority,
        DateTime DueAt,
        int PlannedDurationMinutes);

    private sealed record LegalWorkPaymentRow(
        int PaymentId,
        int CaseId,
        decimal Amount,
        decimal PaidAmount,
        PaymentStatus Status);

    private sealed record LegalWorkReportFilters(
        string? Status,
        int? ResponsibleUserId,
        string? CaseType);

    private sealed record CurrentCasesReportFilters(
        string? Status,
        int? ResponsibleUserId,
        string? CaseType);

    private sealed record CaseActivityReportSettings(
        int InactivityDays,
        int EscalationDays);

    private sealed record CaseActivityTaskRow(
        int ClientCaseId,
        string Status,
        DateTime LastActivityAt);

    private sealed record CaseActivityOwner(
        int? OwnerId,
        string OwnerType,
        string OwnerName);

    private sealed record CurrentCaseDeadlineRow(
        int ClientCaseId,
        string DeadlineType,
        string Title,
        DateTime DueAt);

    private sealed record DeadlineSourceRow(
        string Type,
        string Label,
        DateTime DueAt);

    private sealed record ResponsibleReportKey(
        int? ResponsibleUserId,
        string ResponsibleName);

    private sealed class CashFlowAccountQueryRow
    {
        public MoneyOperationSource Source { get; init; }
        public string AccountName { get; init; } = string.Empty;
        public decimal Income { get; init; }
        public decimal Expense { get; init; }
        public int IncomeCount { get; init; }
        public int ExpenseCount { get; init; }
    }

    private sealed class CashFlowDayQueryRow
    {
        public DateTime Date { get; init; }
        public decimal Income { get; init; }
        public decimal Expense { get; init; }
        public int IncomeCount { get; init; }
        public int ExpenseCount { get; init; }
    }

    private sealed class PlanFactQueryRow
    {
        public DateTime Date { get; init; }
        public MoneyOperationDirection Direction { get; init; }
        public MoneyOperationAccountingType AccountingType { get; init; }
        public decimal Amount { get; init; }
    }
}
